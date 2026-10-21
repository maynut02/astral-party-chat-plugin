using System.Buffers.Binary;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static class AstralPartyChatPlugin { public const string PluginVersion = "test"; }

internal static partial class ChatOverlay
{
    private static readonly List<FguiNode> _chatTexts = new();
    private static readonly Dictionary<string, Texture> PublishedTextures = new();
    private static readonly List<bool> RenderRequests = new();
    private static bool HasCharacterPortrait(string? id) => PartyProtocol.NormalizeCharacter(id) is not ("spectator" or "unselected");
    private static void RefreshRenderedCharacterImage(string id, Texture texture) => PublishedTextures[id] = texture;
    private static void ClearRenderedTexture(Texture texture)
    {
        foreach (var id in PublishedTextures.Where(pair => ReferenceEquals(pair.Value, texture)).Select(pair => pair.Key).ToArray())
            PublishedTextures.Remove(id);
    }
    private static void RenderChatMessages(bool force) => RenderRequests.Add(force);

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
        _chatTexts.Clear();
        RenderRequests.Clear();
        FguiNode.FontRegistrations.Clear();
        FguiNode.TextureReleases.Clear();
        Resources.AvailableFonts = Array.Empty<Font>();
        Time.realtimeSinceStartup = 0f;
        ImageConversion.DecodeSucceeds = true;
        ImageConversion.DecodeCalls = 0;
        ImageConversion.LastDecodedTexture = null;
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
                Require(OwnedUnityResources.Count == 1, "Portrait should own only its displayed texture.");
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
                Require(FguiNode.TextureReleases.Any(texture => ReferenceEquals(texture, old)), "Dead texture's native wrapper was retained.");
                Require(!PublishedTextures.Values.Any(texture => ReferenceEquals(texture, old)), "Loader retained the released native wrapper.");
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
                Require(CharacterImageRequested.Count == 0 && CharacterImageRetryAfter.Count == 0 && CharacterImageReady.Count == 0,
                    "Shutdown retained pending portrait state.");
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
                Require(ImageConversion.LastDecodedTexture is { IsDestroyed: true }, "Failed decode did not destroy its native texture.");
                Require(!CharacterImageRequested.ContainsKey("101") && !TryReserveCharacterImageRequest("101", out _, out _), "Failed decode bypassed cooldown.");
                CharacterImageRetryAfter["101"] = DateTime.UtcNow.AddSeconds(-1);
                ImageConversion.DecodeSucceeds = true;
                DecodePortrait("101");
            }),
            ("portrait decoding is limited to two payloads per frame", () =>
            {
                foreach (var id in new[] { "101", "102", "103" })
                {
                    Require(TryReserveCharacterImageRequest(id, out var generation, out _), "Request could not be reserved.");
                    CharacterImageReady.Enqueue(new CharacterImagePayload(id, generation, PngHeader()));
                }
                ProcessCharacterImageDownloads();
                Require(ImageConversion.DecodeCalls == 2 && CharacterImageReady.Count == 1, "First frame exceeded the decode budget.");
                Require(PublishedTextures.Count == 2 && CharacterImageRequested.ContainsKey("103"), "Queued portrait was published or released early.");
                ProcessCharacterImageDownloads();
                Require(ImageConversion.DecodeCalls == 3 && CharacterImageReady.Count == 0, "Second frame did not finish the queue.");
                Require(PublishedTextures.Count == 3 && CharacterImageRequested.Count == 0, "Successful portraits retained pending requests.");
                Require(ImageConversion.LastMarkNonReadable, "Portrait decoding retained unnecessary CPU-readable pixels.");
            }),
            ("invalid portrait headers are rejected before native decoding", () =>
            {
                Require(TryReserveCharacterImageRequest("101", out var generation, out _), "Initial request missing.");
                var bytes = PngHeader();
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)RemotePayload.MaxPortraitDimension + 1);
                CharacterImageReady.Enqueue(new CharacterImagePayload("101", generation, bytes));
                ProcessCharacterImageDownloads();
                Require(ImageConversion.DecodeCalls == 0 && OwnedUnityResources.Count == 0, "Rejected portrait reached the native decoder.");
                Require(!TryGetCharacterTexture("101", out _) && PublishedTextures.Count == 0, "Rejected portrait was published.");
                Require(!CharacterImageRequested.ContainsKey("101") && !TryReserveCharacterImageRequest("101", out _, out _),
                    "Rejected portrait bypassed retry cooldown.");
            }),
            ("fallback chat font is registered once and released on shutdown", () =>
            {
                RefreshChatFont();
                Require(FguiNode.FontRegistrations.Count == 1, "Fallback font alias was not registered.");
                var registration = FguiNode.FontRegistrations[0];
                Require(registration.Alias == ChatFontName && registration.Font.name == "Malgun Gothic", "Wrong fallback font or alias.");
                Require(ReferenceEquals(registration.Font, _appliedChatFont), "Registered font was not applied.");
                UnityEngine.Object.UnloadUnusedAssets();
                Require(!registration.Font.IsDestroyed, "Fallback font did not survive scene cleanup.");
                RefreshChatFont();
                Require(FguiNode.FontRegistrations.Count == 1, "Unchanged fallback font was registered again.");
                ShutdownAssets();
                Require(registration.Font.IsDestroyed && _font == null && _appliedChatFont == null, "Shutdown retained fallback font state.");
                Require(registration.Disposed && registration.Unregistered, "Shutdown retained the registered native font.");
            }),
            ("preferred game font rebinds retained FairyGUI text and rerenders", () =>
            {
                RefreshChatFont();
                var fallback = FguiNode.FontRegistrations[0].Font;
                var body = new FguiNode();
                var input = new FguiNode();
                _chatTexts.Add(body);
                _chatTexts.Add(input);
                var preferred = new Font { name = PreferredChatFontName };
                Resources.AvailableFonts = new[] { new Font { name = "Other" }, preferred };
                RefreshGameKoreanFont();
                Require(FguiNode.FontRegistrations.Count == 1 && RenderRequests.Count == 0, "Font lookup ignored its retry interval.");
                Time.realtimeSinceStartup = 5f;
                RefreshGameKoreanFont();
                Require(FguiNode.FontRegistrations.Count == 2, "Preferred font was not registered.");
                var registration = FguiNode.FontRegistrations[1];
                Require(registration.Alias == ChatFontName && ReferenceEquals(registration.Font, preferred), "Wrong preferred font alias.");
                Require(FguiNode.FontRegistrations[0].Disposed && !FguiNode.FontRegistrations[0].Unregistered,
                    "Replacement must dispose the previous native font without unregistering the new alias.");
                Require(body.FontAssignments.SequenceEqual(new[] { ChatFontName }) && input.FontAssignments.SequenceEqual(new[] { ChatFontName }),
                    "Retained text did not resolve the replacement font.");
                Require(RenderRequests.SequenceEqual(new[] { true }), "Font replacement did not force message remeasurement.");
                RefreshGameKoreanFont();
                Require(FguiNode.FontRegistrations.Count == 2 && RenderRequests.Count == 1 && body.FontAssignments.Count == 1 && input.FontAssignments.Count == 1,
                    "Unchanged preferred font refreshed retained rows again.");
                ShutdownAssets();
                Require(fallback.IsDestroyed && !preferred.IsDestroyed, "Font shutdown destroyed a game-owned font or leaked the fallback.");
                Require(registration.Disposed && registration.Unregistered, "Shutdown retained the replacement native font.");
            }),
            ("embedded messages icon is decoded once and explicitly released", () =>
            {
                var texture = GetChatIconTexture();
                Require(texture.width == 96 && texture.height == 96, "Embedded messages icon dimensions changed.");
                Require(texture.name == "AstralPartyChatPluginMessagesIcon" && texture.wrapMode == TextureWrapMode.Clamp
                    && texture.filterMode == FilterMode.Bilinear, "Messages icon texture settings were not applied.");
                Require(ImageConversion.DecodeCalls == 1 && ImageConversion.LastMarkNonReadable, "Messages icon was not decoded as non-readable.");
                UnityEngine.Object.UnloadUnusedAssets();
                Require(!texture.IsDestroyed && ReferenceEquals(GetChatIconTexture(), texture), "Messages icon was unloaded or recreated.");
                Require(ImageConversion.DecodeCalls == 1 && OwnedUnityResources.Count == 1, "Cached messages icon was decoded or owned twice.");
                ShutdownAssets();
                Require(texture.IsDestroyed && _chatIconTexture == null, "Shutdown leaked the embedded messages icon.");
                EnsureAssetsRunning();
                var replacement = GetChatIconTexture();
                Require(!ReferenceEquals(texture, replacement) && ImageConversion.DecodeCalls == 2, "Restart reused the destroyed messages icon.");
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
