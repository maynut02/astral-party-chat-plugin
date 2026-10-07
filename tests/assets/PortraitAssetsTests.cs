using System.Buffers.Binary;
using UnityEngine;
using UnityEngine.UI;

namespace AstralPartyChatPlugin;

internal static class AstralPartyChatPlugin { public const string PluginVersion = "test"; }

internal static partial class ChatOverlay
{
    private static readonly List<Text> _chatTexts = new();
    private static readonly Dictionary<string, Texture> PublishedTextures = new();
    private static bool HasCharacterPortrait(string? id) => PartyProtocol.NormalizeCharacter(id) is not ("spectator" or "unselected");
    private static void RefreshRenderedCharacterImage(string id, Texture texture) => PublishedTextures[id] = texture;
    private static void RenderChatMessages(bool force) { }

    private static byte[] PngHeader()
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        new byte[] { 73, 72, 68, 82 }.CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 64);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 64);
        return bytes;
    }

    private static Texture2D DecodePortrait(string id)
    {
        Require(TryReserveCharacterImageRequest(id, out var generation, out _), "Request could not be reserved.");
        CharacterImageReady.Enqueue(new CharacterImagePayload(id, generation, PngHeader()));
        ProcessCharacterImageDownloads();
        Require(TryGetCharacterTexture(id, out var texture) && texture != null, "Decoded texture is missing.");
        return texture!;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void ResetTest()
    {
        ShutdownAssets();
        PublishedTextures.Clear();
        ImageConversion.DecodeSucceeds = true;
        EnsureAssetsRunning();
    }

    public static int Main()
    {
        var passed = 0;
        var checks = new (string Name, Action Run)[]
        {
            ("portraits survive unused-asset cleanup before and during play", () =>
            {
                var texture = DecodePortrait("101");
                var gameOwned = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                foreach (var phase in new[] { "방", "캐릭터 선택", "플레이", "방" })
                {
                    UnityEngine.Object.UnloadUnusedAssets();
                    Require(TryGetCharacterTexture("101", out var cached) && ReferenceEquals(cached, texture), "Portrait lost in " + phase);
                    Require(!TryReserveCharacterImageRequest("101", out _, out _), "Scene change redownloaded a valid texture.");
                }
                Require(gameOwned.IsDestroyed, "Unused-asset simulation did not run.");
                Require(OwnedUnityResources.Count == 1, "Portrait should own only its displayed texture, not an unused Sprite.");
            }),
            ("destroyed native cache entries can be requested and displayed again", () =>
            {
                var old = DecodePortrait("101");
                UnityEngine.Object.Destroy(old);
                Require(old == null && !ReferenceEquals(old, null), "Fixture did not model Unity native-object destruction.");
                // Reserve directly: the request must detect the invalid cache
                // without requiring the lookup/render path to run beforehand.
                Require(TryReserveCharacterImageRequest("101", out var generation, out _), "Dead cache blocked redownload forever.");
                Require(!TryGetCharacterTexture("101", out var missing) && ReferenceEquals(missing, null), "Dead wrapper escaped lookup.");
                Require(!TryReserveCharacterImageRequest("101", out _, out _), "Duplicate pending download was allowed.");
                CharacterImageReady.Enqueue(new CharacterImagePayload("101", generation, PngHeader()));
                ProcessCharacterImageDownloads();
                Require(TryGetCharacterTexture("101", out var replacement) && replacement != null && !ReferenceEquals(old, replacement), "Replacement texture missing.");
                Require(ReferenceEquals(PublishedTextures["101"], replacement), "Replacement was not published to retained rows.");
                Require(OwnedUnityResources.Count == 1, "Destroyed cached wrapper was retained.");
            }),
            ("shutdown releases owned portraits without destroying game resources", () =>
            {
                var portrait = DecodePortrait("101");
                var gameOwned = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                ShutdownAssets();
                Require(portrait.IsDestroyed && !gameOwned.IsDestroyed, "Shutdown affected wrong resources.");
                Require(CharacterImageTextures.Count == 0 && OwnedUnityResources.Count == 0, "Shutdown retained cache/resources.");
                Require(!TryReserveCharacterImageRequest("101", out _, out _), "Shutdown allowed another request.");
            }),
            ("old scene-lifetime callbacks cannot overwrite a restarted overlay", () =>
            {
                Require(TryReserveCharacterImageRequest("101", out var oldGeneration, out var token), "Initial request missing.");
                ShutdownAssets();
                Require(token.IsCancellationRequested, "Shutdown did not cancel downloads.");
                EnsureAssetsRunning();
                var current = DecodePortrait("101");
                CharacterImageReady.Enqueue(new CharacterImagePayload("101", oldGeneration, PngHeader()));
                ProcessCharacterImageDownloads();
                Require(TryGetCharacterTexture("101", out var cached) && ReferenceEquals(current, cached), "Old payload replaced current portrait.");
                Require(OwnedUnityResources.Count == 1, "Old payload allocated a leaked texture.");
            }),
            ("decode failure cleans resources and preserves bounded retry", () =>
            {
                Require(TryReserveCharacterImageRequest("101", out var generation, out _), "Initial request missing.");
                ImageConversion.DecodeSucceeds = false;
                CharacterImageReady.Enqueue(new CharacterImagePayload("101", generation, PngHeader()));
                ProcessCharacterImageDownloads();
                Require(OwnedUnityResources.Count == 0 && !TryGetCharacterTexture("101", out _), "Failed decode leaked or cached a texture.");
                Require(!CharacterImageRequested.ContainsKey("101") && !TryReserveCharacterImageRequest("101", out _, out _), "Failed decode bypassed cooldown.");
                CharacterImageRetryAfter["101"] = DateTime.UtcNow.AddSeconds(-1);
                ImageConversion.DecodeSucceeds = true;
                DecodePortrait("101");
            }),
            ("generated UI sprites are protected and explicitly released", () =>
            {
                var texture = TrackOwnedUnityResource(new Texture2D(2, 2, TextureFormat.RGBA32, false));
                var sprite = CreateOwnedSprite(texture, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 100f);
                UnityEngine.Object.UnloadUnusedAssets();
                Require(texture != null && sprite != null, "Generated UI assets were unloaded.");
                ShutdownAssets();
                Require(texture!.IsDestroyed && sprite!.IsDestroyed, "Protected UI assets were leaked.");
            })
        };
        foreach (var (name, run) in checks)
        {
            ResetTest();
            try { run(); passed++; Console.WriteLine("PASS assets: " + name); }
            catch (Exception error) { Console.Error.WriteLine("FAIL assets: " + name + ": " + error); return 1; }
            finally { ShutdownAssets(); }
        }
        Console.WriteLine($"{passed}/{checks.Length} production portrait asset checks passed. Native cleanup is simulated; no game or network was used.");
        return 0;
    }
}
