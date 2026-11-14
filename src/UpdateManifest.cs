using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AstralPartyChatPlugin;

/// <summary>
/// The signed description of one plugin release. Mirrors the update format of
/// astral-party-korean-plugin; every field is pinned to this repository and DLL,
/// so a manifest signed for another plugin with the same key is rejected.
/// </summary>
internal sealed class UpdateManifest
{
    public const int ManifestLimit = 64 * 1024;
    public const int SignatureLimit = 256;
    public const int PackageLimit = 64 * 1024 * 1024;
    public const string DllName = "AstralPartyChatPlugin.dll";
    public const string DllPath = "BepInEx/plugins/AstralPartyChatPlugin/" + DllName;
    public const string ReleasePrefix = "https://github.com/maynut02/astral-party-chat-plugin/releases/download/";
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEylLFkAOyq5ioR6NxNYMWG5R6d1tocK4EeRzPIf6r6Ia4U5mn9lKCi9nwkm8g6LoAfulbxhT8954C9+/Lrj4UJA==";

    private readonly string _packageHash;
    private readonly int _dllSize;
    private readonly string _dllHash;

    private UpdateManifest(JsonElement root)
    {
        RequireObject(root, "schemaVersion", "version", "package", "files", "minimumBepInEx");
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported plugin update schema");
        var versionText = root.GetProperty("version").GetString() ?? "";
        Version = SemanticVersion.Parse(versionText);
        if (versionText != Version.Text || Version.IsPrerelease || versionText.Contains('+'))
            throw new InvalidDataException("Update release must have a stable X.Y.Z version");
        MinimumBepInEx = SemanticVersion.Parse(root.GetProperty("minimumBepInEx").GetString() ?? "");

        var package = root.GetProperty("package");
        RequireObject(package, "name", "url", "size", "sha256");
        var name = "update-v" + Version.Text + ".zip";
        if (package.GetProperty("name").GetString() != name)
            throw new InvalidDataException("Unexpected plugin update package name");
        // Never follow a signed URL outside this repository's release for that exact version.
        var expectedUrl = ReleasePrefix + "v" + Version.Text + "/" + name;
        if (package.GetProperty("url").GetString() != expectedUrl)
            throw new InvalidDataException("Unexpected plugin update package URL");
        PackageUrl = new Uri(expectedUrl, UriKind.Absolute);
        PackageSize = ReadSize(package);
        _packageHash = ReadHash(package);

        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1)
            throw new InvalidDataException("Update manifest must list exactly the plugin DLL");
        var file = files[0];
        RequireObject(file, "path", "size", "sha256");
        if (file.GetProperty("path").GetString() != DllPath)
            throw new InvalidDataException("Unexpected update file path");
        _dllSize = ReadSize(file);
        _dllHash = ReadHash(file);
    }

    public SemanticVersion Version { get; }
    public SemanticVersion MinimumBepInEx { get; }
    public Uri PackageUrl { get; }
    public int PackageSize { get; }

    public static UpdateManifest Verify(byte[] bytes, byte[] signatureText, string publicKey = PublicKey)
    {
        if (bytes.Length == 0 || bytes.Length > ManifestLimit
            || signatureText.Length == 0 || signatureText.Length > SignatureLimit)
            throw new InvalidDataException("Plugin update manifest or signature exceeds size limit");
        foreach (var value in signatureText)
            if (value > 127) throw new InvalidDataException("Plugin update signature is not ASCII");
        var signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureText));
        if (signature.Length != 64) throw new CryptographicException("Invalid P1363 signature length");
        using var key = ECDsa.Create();
        var spki = Convert.FromBase64String(publicKey);
        key.ImportSubjectPublicKeyInfo(spki, out var read);
        if (read != spki.Length || key.KeySize != 256
            || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("Update signing key must be ECDSA P-256");
        // Verify the exact downloaded bytes before a JSON parser sees them.
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Plugin update manifest signature is invalid");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        return new UpdateManifest(document.RootElement);
    }

    /// <summary>Returns the DLL bytes once the ZIP and its single entry match the signed sizes and hashes.</summary>
    public byte[] Extract(byte[] package)
    {
        VerifyBytes(package, PackageSize, _packageHash, "update package");
        using var stream = new MemoryStream(package, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != DllPath)
            throw new InvalidDataException("Update ZIP must contain exactly the plugin DLL");
        var entry = zip.Entries[0];
        if (entry.Length != _dllSize)
            throw new InvalidDataException("Update file size mismatch");
        // Only a regular file is accepted, not a Unix symlink or directory entry.
        var type = (entry.ExternalAttributes >> 16) & 0xF000;
        if ((entry.ExternalAttributes & 0x10) != 0 || (type != 0 && type != 0x8000))
            throw new InvalidDataException("Update ZIP entry is not a regular file");
        var bytes = new byte[_dllSize];
        using var input = entry.Open();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = input.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidDataException("Truncated update file");
            offset += count;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("Update file exceeds declared size");
        VerifyBytes(bytes, _dllSize, _dllHash, DllName);
        return bytes;
    }

    private static void VerifyBytes(byte[] bytes, int size, string hash, string label)
    {
        if (bytes.Length != size) throw new InvalidDataException("Size mismatch: " + label);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 mismatch: " + label);
    }

    private static int ReadSize(JsonElement element)
    {
        var size = element.GetProperty("size").GetInt32();
        if (size <= 0 || size > PackageLimit) throw new InvalidDataException("Invalid update size");
        return size;
    }

    private static string ReadHash(JsonElement element)
    {
        var hash = element.GetProperty("sha256").GetString() ?? "";
        if (hash.Length != 64) throw new InvalidDataException("Invalid update SHA-256");
        foreach (var value in hash)
            if (!(value >= '0' && value <= '9') && !(value >= 'a' && value <= 'f'))
                throw new InvalidDataException("Update SHA-256 must be lowercase hexadecimal");
        return hash;
    }

    private static void RequireObject(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected update JSON object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (Array.IndexOf(names, property.Name) < 0 || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate update property: " + property.Name);
        if (seen.Count != names.Length) throw new InvalidDataException("Missing update property");
    }
}
