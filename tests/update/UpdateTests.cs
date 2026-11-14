using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AstralPartyChatPlugin;

// "verify <release directory> <tag> <public key>": tests/scripts/PackageReleaseTests.ps1 hands a packaged
// release to the plugin's own verifier, so the release scripts and the plugin cannot drift apart.
if (args.Length == 4 && args[0] == "verify")
{
    try
    {
        var manifest = UpdateManifest.Verify(File.ReadAllBytes(Path.Combine(args[1], "update.json")),
            File.ReadAllBytes(Path.Combine(args[1], "update.json.sig")), args[3]);
        if ("v" + manifest.Version.Text != args[2]) throw new InvalidDataException("Manifest version differs from the tag.");
        var dll = manifest.Extract(File.ReadAllBytes(Path.Combine(args[1], Path.GetFileName(manifest.PackageUrl.AbsolutePath))));
        Console.WriteLine("VERIFIED " + Convert.ToHexString(SHA256.HashData(dll)).ToLowerInvariant());
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("REJECTED " + ex.GetType().Name + ": " + ex.Message);
        return 1;
    }
}

// The production update code runs against a throwaway signing key, an in-memory
// release and a temporary plugin folder. No game, GitHub or real key is used.
var tests = new List<(string Name, Func<Task> Run)>
{
    ("SemVer orders releases and BepInEx bleeding-edge builds", () =>
    {
        int Compare(string left, string right) => Math.Sign(SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right)));
        Check.True(Compare("0.10.0", "0.9.9") == 1 && Compare("1.0.0", "1.0.0") == 0, "Numeric core order is wrong.");
        Check.True(Compare("6.0.0-be.788+5b766a3", "6.0.0-be.788") == 0, "Build metadata changed precedence.");
        Check.True(Compare("6.0.0-be.697", "6.0.0-be.788") == -1 && Compare("6.0.0", "6.0.0-be.788") == 1, "Prerelease order is wrong.");
        Check.Rejects<FormatException>(() => SemanticVersion.Parse("1.0"));
        Check.Rejects<FormatException>(() => SemanticVersion.Parse("01.0.0"));
        return Task.CompletedTask;
    }),
    ("A signed manifest for this repository verifies and yields the exact DLL", () =>
    {
        using var release = new Release("0.3.0");
        var manifest = UpdateManifest.Verify(release.Manifest, release.Signature, release.PublicKey);
        Check.True(manifest.Version.Text == "0.3.0" && manifest.MinimumBepInEx.Text == "6.0.0-be.788", "Manifest fields changed.");
        Check.True(manifest.PackageUrl.AbsoluteUri == UpdateManifest.ReleasePrefix + "v0.3.0/update-v0.3.0.zip", "Package URL changed.");
        Check.True(manifest.Extract(release.Package).SequenceEqual(release.Dll), "Extracted DLL differs from the release.");
        return Task.CompletedTask;
    }),
    ("Altered bytes, foreign keys and malformed signatures are rejected before parsing", () =>
    {
        using var release = new Release("0.3.0");
        using var other = new Release("0.3.0");
        var altered = (byte[])release.Manifest.Clone();
        altered[^3] ^= 1;
        Check.Rejects<CryptographicException>(() => UpdateManifest.Verify(altered, release.Signature, release.PublicKey));
        Check.Rejects<CryptographicException>(() => UpdateManifest.Verify(release.Manifest, release.Signature, other.PublicKey));
        Check.Rejects<CryptographicException>(() => UpdateManifest.Verify(release.Manifest, release.Signature));
        Check.Rejects<CryptographicException>(() => UpdateManifest.Verify(release.Manifest,
            Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[65])), release.PublicKey));
        Check.Rejects<FormatException>(() => UpdateManifest.Verify(release.Manifest, Encoding.ASCII.GetBytes("not base64!"), release.PublicKey));
        Check.Rejects<InvalidDataException>(() => UpdateManifest.Verify(release.Manifest, new byte[UpdateManifest.SignatureLimit + 1], release.PublicKey));
        Check.Rejects<InvalidDataException>(() => UpdateManifest.Verify(new byte[UpdateManifest.ManifestLimit + 1], release.Signature, release.PublicKey));
        return Task.CompletedTask;
    }),
    ("Correctly signed manifests that describe anything but this plugin's release are rejected", () =>
    {
        // Each variant carries a valid signature: only the content rules can stop it.
        void Rejected(Func<Release.Fields, Release.Fields> change, string reason)
        {
            using var release = new Release("0.3.0", change);
            try { UpdateManifest.Verify(release.Manifest, release.Signature, release.PublicKey); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException) { return; }
            throw new Exception("Accepted a signed manifest with " + reason + ".");
        }
        Rejected(f => f with { Url = "https://github.com/maynut02/astral-party-korean-plugin/releases/download/v0.3.0/update-v0.3.0.zip" }, "another repository's URL");
        Rejected(f => f with { Url = "https://example.com/update-v0.3.0.zip" }, "an external URL");
        Rejected(f => f with { Url = UpdateManifest.ReleasePrefix + "v0.2.0/update-v0.3.0.zip" }, "another release's URL");
        Rejected(f => f with { PackageName = "AstralPartyChatPlugin-v0.3.0.zip" }, "the installer package name");
        Rejected(f => f with { FilePath = "BepInEx/plugins/AstralPartyKoreanPlugin/AstralPartyKoreanPlugin.dll" }, "another plugin's DLL");
        Rejected(f => f with { FilePath = "../" + UpdateManifest.DllPath }, "a traversal path");
        Rejected(f => f with { ExtraFile = true }, "a second file");
        Rejected(f => f with { Version = "0.3.0-rc.1" }, "a prerelease version");
        Rejected(f => f with { Version = "0.3.0+build" }, "build metadata");
        Rejected(f => f with { Schema = 2 }, "an unknown schema");
        Rejected(f => f with { ExtraProperty = true }, "an unknown property");
        Rejected(f => f with { DllHash = new string('A', 64) }, "an uppercase hash");
        Rejected(f => f with { PackageSize = 0 }, "an empty package");
        Rejected(f => f with { PackageSize = UpdateManifest.PackageLimit + 1 }, "an oversized package");
        Rejected(f => f with { MinimumBepInEx = "latest" }, "an unparseable BepInEx requirement");
        return Task.CompletedTask;
    }),
    ("A package that differs from the signed description is never extracted", () =>
    {
        using var release = new Release("0.3.0");
        var manifest = UpdateManifest.Verify(release.Manifest, release.Signature, release.PublicKey);
        var flipped = (byte[])release.Package.Clone();
        flipped[^1] ^= 1;
        Check.Rejects<InvalidDataException>(() => manifest.Extract(flipped));
        Check.Rejects<InvalidDataException>(() => manifest.Extract(release.Package.Concat(new byte[] { 0 }).ToArray()));

        // Signed package size and hash match the ZIP and the signed file entry describes the real DLL,
        // so only the rule about the ZIP's contents can reject each case.
        void RejectedZip(params (string Path, byte[] Bytes)[] entries)
        {
            var package = Release.Zip(entries);
            using var crafted = new Release("0.3.0", f => f with
            {
                Package = package,
                DllSize = release.Dll.Length,
                DllHash = Release.Hash(release.Dll),
            });
            var craftedManifest = UpdateManifest.Verify(crafted.Manifest, crafted.Signature, crafted.PublicKey);
            Check.Rejects<InvalidDataException>(() => craftedManifest.Extract(package));
        }
        RejectedZip((UpdateManifest.DllPath, release.Dll), ("BepInEx/plugins/Other/Other.dll", new byte[] { 1 }));
        RejectedZip(("BepInEx/plugins/AstralPartyChatPlugin/Other.dll", release.Dll));
        RejectedZip((UpdateManifest.DllPath, release.Dll.Concat(new byte[] { 0 }).ToArray()));
        RejectedZip((UpdateManifest.DllPath, release.Dll.Reverse().ToArray()));
        RejectedZip();
        return Task.CompletedTask;
    }),
    ("A newer signed release replaces the loaded DLL and asks for a restart", async () =>
    {
        using var folder = new PluginFolder("0.2.1");
        using var release = new Release("0.3.0");
        // BepInEx keeps the plugin image mapped; Windows still lets it be renamed.
        using (new FileStream(folder.DllPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            using var client = folder.Check(release);
            await client.RefreshTask;
            var state = client.Snapshot();
            Check.True(state.RestartRequired && state.Severity == UpdateSeverity.Success && state.Message!.Contains("v0.3.0"), "Installed update was not announced.");
            Check.True(client.Revision == 1, "One result must be one notice revision.");
        }
        Check.True(File.ReadAllBytes(folder.DllPath).SequenceEqual(release.Dll), "The new DLL was not installed.");
        Check.True(File.ReadAllBytes(folder.DllPath + ".old").SequenceEqual(folder.OriginalDll), "The previous DLL was not kept as a backup.");
        Check.True(folder.Files().SequenceEqual(new[] { "AstralPartyChatPlugin.dll", "AstralPartyChatPlugin.dll.old" }), "Staging files were left behind.");
        Check.True(release.Requests.SequenceEqual(new[] { "update.json", "update.json.sig", "update-v0.3.0.zip" }), "Unexpected download sequence.");

        new UpdateInstaller(folder.DllPath, _ => { }).PrepareStartup();
        Check.True(folder.Files().SequenceEqual(new[] { "AstralPartyChatPlugin.dll" }), "The next start did not remove the backup.");
    }),
    ("The update follows a renamed or relocated plugin file instead of adding a second DLL", async () =>
    {
        using var folder = new PluginFolder("0.2.1", "AstralParty.Chat.dll");
        using var release = new Release("0.3.0");
        using var client = folder.Check(release);
        await client.RefreshTask;
        Check.True(client.Snapshot().RestartRequired, "Renamed plugin was not updated.");
        Check.True(File.ReadAllBytes(folder.DllPath).SequenceEqual(release.Dll), "Renamed plugin file was not replaced.");
        Check.True(folder.Files().SequenceEqual(new[] { "AstralParty.Chat.dll", "AstralParty.Chat.dll.old" }), "A duplicate plugin DLL was created.");
    }),
    ("Current and older releases change nothing and show nothing", async () =>
    {
        foreach (var latest in new[] { "0.2.1", "0.2.0" })
        {
            using var folder = new PluginFolder("0.2.1");
            using var release = new Release(latest);
            using var client = folder.Check(release);
            await client.RefreshTask;
            Check.True(client.Snapshot().Message == null && client.Revision == 0, "An up-to-date plugin produced a notice.");
            Check.True(folder.Unchanged() && !release.Requests.Contains("update-v" + latest + ".zip"), "A non-newer release was downloaded or installed.");
        }
    }),
    ("Disabled auto-update and an old BepInEx only announce the release", async () =>
    {
        foreach (var (autoUpdate, bepInEx) in new[] { (false, "6.0.0-be.788"), (true, "6.0.0-be.697"), (true, "") })
        {
            using var folder = new PluginFolder("0.2.1");
            using var release = new Release("0.3.0");
            using var client = folder.Check(release, autoUpdate, bepInEx);
            await client.RefreshTask;
            var state = client.Snapshot();
            Check.True(state.Message != null && state.Message.Contains("v0.3.0") && state.Severity == UpdateSeverity.Warning && !state.RestartRequired,
                "The player was not told to update by hand.");
            Check.True(folder.Unchanged() && release.Requests.Count == 2, "A blocked update still downloaded or installed the package.");
        }
    }),
    ("Unreachable or unverifiable release data stays silent", async () =>
    {
        using var folder = new PluginFolder("0.2.1");
        using var release = new Release("0.3.0");
        using var foreign = new Release("0.3.0");
        // A release without update files (404), an offline start, and a manifest signed by another key.
        foreach (var download in new UpdateDownload[]
        {
            (_, _, _, _) => throw new HttpRequestException("404"),
            (_, _, _, _) => throw new TimeoutException("offline"),
            (url, limit, noCache, token) => url == PluginUpdateClient.SignatureUrl
                ? Task.FromResult(foreign.Signature) : release.Download(url, limit, noCache, token)
        })
        {
            using var client = new PluginUpdateClient("0.2.1", "6.0.0-be.788", true, download,
                folder.Installer.Install, _ => { }, release.PublicKey);
            await client.RefreshTask;
            Check.True(client.Snapshot().Message == null && client.Revision == 0, "A failed check interrupted the player.");
        }
        Check.True(folder.Unchanged() && !release.Requests.Contains("update-v0.3.0.zip"), "An unverified manifest led to a package download.");
    }),
    ("A package that fails verification leaves the installed DLL and reports a manual update", async () =>
    {
        using var folder = new PluginFolder("0.2.1");
        using var release = new Release("0.3.0");
        UpdateDownload download = async (url, limit, noCache, token) =>
        {
            var bytes = await release.Download(url, limit, noCache, token);
            if (url.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)) bytes[^1] ^= 1;
            return bytes;
        };
        using var client = new PluginUpdateClient("0.2.1", "6.0.0-be.788", true, download,
            folder.Installer.Install, _ => { }, release.PublicKey);
        await client.RefreshTask;
        var state = client.Snapshot();
        Check.True(state.Message != null && state.Severity == UpdateSeverity.Warning && !state.RestartRequired, "A failed install was not reported.");
        Check.True(folder.Unchanged(), "A corrupt package changed the plugin folder.");
    }),
    ("A DLL that cannot be replaced is left intact with no staging files", async () =>
    {
        using var folder = new PluginFolder("0.2.1");
        using var release = new Release("0.3.0");
        using (new FileStream(folder.DllPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var client = folder.Check(release);
            await client.RefreshTask;
            var state = client.Snapshot();
            Check.True(state.Message != null && state.Severity == UpdateSeverity.Warning && !state.RestartRequired, "A blocked replacement was reported as installed.");
        }
        Check.True(folder.Unchanged(), "A blocked replacement changed the plugin folder.");

        // Staging itself fails after the temporary file was written.
        Directory.CreateDirectory(folder.DllPath + ".new");
        Check.Rejects<Exception>(() => folder.Installer.Install(release.Dll));
        Check.True(folder.Unchanged(), "A failed staging write left a file beside the plugin.");
    }),
    ("Stopping the plugin cancels the check before anything is installed", async () =>
    {
        using var folder = new PluginFolder("0.2.1");
        using var release = new Release("0.3.0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateDownload download = async (url, limit, noCache, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return await release.Download(url, limit, noCache, token);
        };
        var client = new PluginUpdateClient("0.2.1", "6.0.0-be.788", true, download,
            folder.Installer.Install, _ => { }, release.PublicKey);
        await started.Task;
        client.Dispose();
        await client.RefreshTask;
        Check.True(client.Snapshot().Message == null && folder.Unchanged(), "A cancelled check installed or announced an update.");
    }),
    ("Startup discards staged files it cannot authenticate", () =>
    {
        using var folder = new PluginFolder("0.2.1");
        foreach (var suffix in new[] { ".new", ".new.tmp", ".old" })
            File.WriteAllBytes(folder.DllPath + suffix, new byte[] { 1, 2, 3 });
        new UpdateInstaller(folder.DllPath, _ => { }).PrepareStartup();
        Check.True(folder.Unchanged(), "Startup applied or kept a file from an interrupted update.");
        return Task.CompletedTask;
    }),
    ("A failed restart keeps the installed update and tells the player to restart by hand", async () =>
    {
        using var folder = new PluginFolder("0.2.1");
        using var release = new Release("0.3.0");
        using var client = folder.Check(release);
        await client.RefreshTask;
        client.RestartFailed(new IOException("helper"));
        var state = client.Snapshot();
        Check.True(state.RestartRequired && state.Severity == UpdateSeverity.Warning && client.Revision == 2, "Restart failure was not shown as a new notice.");
    }),
    ("The restart helper relaunches the same executable without Doorstop's disable flag", () =>
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using var folder = new PluginFolder("0.2.1", "Astral & Party %TEMP% !.exe");
        Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", "TRUE");
        Environment.SetEnvironmentVariable("SteamAppId", "2622000");
        try
        {
            var info = GameRestart.CreateStartInfo(folder.DllPath, 4321);
            Check.True(!info.Environment.Keys.Any(name => name.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)),
                "The relaunched game would skip BepInEx.");
            Check.True(info.Environment["SteamAppId"] == "2622000", "Steam launch variables were dropped.");
            Check.True(info.Environment["ASTRAL_RESTART_EXECUTABLE"] == folder.DllPath
                && info.Environment["ASTRAL_RESTART_DIRECTORY"] == Path.GetDirectoryName(folder.DllPath), "Restart target changed.");
            // Special characters travel through the environment, never through the command text.
            Check.True(!info.Arguments.Contains("Astral & Party") && info.Arguments.Contains("PID eq 4321")
                && info.Arguments.Contains("/v:off") && info.CreateNoWindow && !info.UseShellExecute, "Restart command is unsafe or visible.");
            Check.True(Path.GetFileName(info.FileName).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
                && Path.IsPathRooted(info.FileName), "Restart must use the system shell by absolute path.");
            Check.Rejects<IOException>(() => GameRestart.CreateStartInfo(folder.DllPath + ".missing", 4321));
            Check.Rejects<IOException>(() => GameRestart.CreateStartInfo(folder.DllPath, 0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", null);
            Environment.SetEnvironmentVariable("SteamAppId", null);
        }
        return Task.CompletedTask;
    })
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} plugin update checks passed. A temporary key and folder were used; no network or game.");
return failures == 0 ? 0 : 1;

internal static class Check
{
    public static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Rejects<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
}

/// <summary>A release as scripts/package-release.ps1 publishes it, signed with a throwaway key.</summary>
internal sealed class Release : IDisposable
{
    public sealed record Fields(int Schema, string Version, string PackageName, string Url, int PackageSize,
        string PackageHash, string FilePath, int DllSize, string DllHash, string MinimumBepInEx,
        bool ExtraFile = false, bool ExtraProperty = false, byte[]? Package = null);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Release(string version, Func<Fields, Fields>? change = null)
    {
        Dll = Encoding.UTF8.GetBytes("plugin " + version + " " + Guid.NewGuid().ToString("N"));
        var package = Zip((UpdateManifest.DllPath, Dll));
        var name = "update-v" + version + ".zip";
        var fields = new Fields(1, version, name, UpdateManifest.ReleasePrefix + "v" + version + "/" + name,
            package.Length, Hash(package), UpdateManifest.DllPath, Dll.Length, Hash(Dll), "6.0.0-be.788");
        if (change != null)
        {
            fields = change(fields);
            if (fields.Package != null)
            {
                package = fields.Package;
                fields = fields with { PackageSize = package.Length, PackageHash = Hash(package) };
            }
        }
        Package = package;
        var files = "{\"path\":\"" + fields.FilePath + "\",\"size\":" + fields.DllSize + ",\"sha256\":\"" + fields.DllHash + "\"}";
        if (fields.ExtraFile) files += "," + files;
        Manifest = Encoding.UTF8.GetBytes("{\"schemaVersion\":" + fields.Schema + ",\"version\":\"" + fields.Version
            + "\",\"package\":{\"name\":\"" + fields.PackageName + "\",\"url\":\"" + fields.Url + "\",\"size\":" + fields.PackageSize
            + ",\"sha256\":\"" + fields.PackageHash + "\"},\"files\":[" + files + "],\"minimumBepInEx\":\"" + fields.MinimumBepInEx + "\""
            + (fields.ExtraProperty ? ",\"script\":\"run.cmd\"" : "") + "}\n");
        Signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(
            _key.SignData(Manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    }

    public byte[] Dll { get; }
    public byte[] Package { get; }
    public byte[] Manifest { get; }
    public byte[] Signature { get; }
    public string PublicKey { get; }
    public List<string> Requests { get; } = new();

    public Task<byte[]> Download(Uri url, int limit, bool noCache, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = Path.GetFileName(url.AbsolutePath);
        lock (Requests) Requests.Add(name);
        var bytes = url == PluginUpdateClient.ManifestUrl ? Manifest
            : url == PluginUpdateClient.SignatureUrl ? Signature
            : name.EndsWith(".zip", StringComparison.Ordinal) ? Package
            : throw new HttpRequestException("404: " + url);
        if (bytes.Length > limit) throw new InvalidDataException("Response exceeds the requested limit.");
        return Task.FromResult((byte[])bytes.Clone());
    }

    public static byte[] Zip(params (string Path, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                using var output = archive.CreateEntry(path).Open();
                output.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() => _key.Dispose();
}

internal sealed class PluginFolder : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astral-chat-update-tests", Guid.NewGuid().ToString("N"));

    public PluginFolder(string version, string fileName = "AstralPartyChatPlugin.dll")
    {
        Directory.CreateDirectory(_root);
        DllPath = Path.Combine(_root, fileName);
        OriginalDll = Encoding.UTF8.GetBytes("installed " + version);
        File.WriteAllBytes(DllPath, OriginalDll);
        Installer = new UpdateInstaller(DllPath, _ => { });
    }

    public string DllPath { get; }
    public byte[] OriginalDll { get; }
    public UpdateInstaller Installer { get; }

    public PluginUpdateClient Check(Release release, bool autoUpdate = true, string bepInEx = "6.0.0-be.788+5b766a3") =>
        new("0.2.1", bepInEx, autoUpdate, release.Download, Installer.Install, _ => { }, release.PublicKey);

    public string[] Files() => Directory.GetFiles(_root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray()!;

    public bool Unchanged() => Files().SequenceEqual(new[] { Path.GetFileName(DllPath) })
        && File.ReadAllBytes(DllPath).SequenceEqual(OriginalDll);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
