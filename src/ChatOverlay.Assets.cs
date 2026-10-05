using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    private const string PreferredChatFontName = "Afacad-Regular";
    private static readonly object CharacterImageSync = new();
    private static readonly Dictionary<string, Sprite> CharacterImageSprites =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> CharacterImageRequested =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> CharacterImageRetryAfter =
        new(StringComparer.Ordinal);
    private static readonly Queue<CharacterImagePayload> CharacterImageReady = new();
    private static readonly HttpClient CharacterImageHttp = CreateCharacterImageHttp();
    private static readonly List<UnityEngine.Object> OwnedUnityResources = new();
    private static readonly MethodInfo? FindAllFontsMethod = FindResourcesFontMethod();

    private static CancellationTokenSource? _characterImageLifetime;
    private static long _characterImageGeneration;
    private static bool _assetsShutdown = true;
    private static Font? _gameKoreanFont;
    private static Font? _appliedGameKoreanFont;
    private static Font? _font;
    private static float _nextFontSearchAt;
    private static bool _fontWasFound;

    private static HttpClient CreateCharacterImageHttp()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "AstralPartyChatPlugin/" + AstralPartyChatPlugin.PluginVersion);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin",
            "https://astral.maynutlab.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "image/png,image/*;q=0.8,*/*;q=0.5");
        return client;
    }

    private static void EnsureAssetsRunning()
    {
        lock (CharacterImageSync)
        {
            if (!_assetsShutdown
                && _characterImageLifetime != null
                && !_characterImageLifetime.IsCancellationRequested)
                return;

            _characterImageLifetime?.Dispose();
            _characterImageLifetime = new CancellationTokenSource();
            _characterImageGeneration++;
            _assetsShutdown = false;
        }
    }

    private static void ShutdownAssets()
    {
        CancellationTokenSource? lifetime;
        lock (CharacterImageSync)
        {
            _assetsShutdown = true;
            _characterImageGeneration++;
            lifetime = _characterImageLifetime;
            _characterImageLifetime = null;
            CharacterImageRequested.Clear();
            CharacterImageRetryAfter.Clear();
            CharacterImageReady.Clear();
            CharacterImageSprites.Clear();
        }

        try { lifetime?.Cancel(); }
        catch { }
        lifetime?.Dispose();

        for (var index = OwnedUnityResources.Count - 1; index >= 0; index--)
        {
            var resource = OwnedUnityResources[index];
            try
            {
                if (resource != null)
                    UnityEngine.Object.Destroy(resource);
            }
            catch { }
        }
        OwnedUnityResources.Clear();

        _gameKoreanFont = null;
        _appliedGameKoreanFont = null;
        _font = null;
        _fontWasFound = false;
        _nextFontSearchAt = 0f;
    }

    private static T TrackOwnedUnityResource<T>(T resource)
        where T : UnityEngine.Object
    {
        if (resource is null)
            throw new ArgumentNullException(nameof(resource));

        OwnedUnityResources.Add(resource);
        return resource;
    }

    private static void ReleaseOwnedUnityResource(UnityEngine.Object? resource)
    {
        if (resource == null)
            return;

        OwnedUnityResources.Remove(resource);
        try { UnityEngine.Object.Destroy(resource); }
        catch { }
    }

    private static Sprite CreateOwnedSprite(
        Texture2D texture,
        Rect rect,
        Vector2 pivot,
        float pixelsPerUnit)
    {
        return TrackOwnedUnityResource(
            Sprite.Create(texture, rect, pivot, pixelsPerUnit));
    }

    private static void RequestCharacterImage(string characterId)
    {
        var id = PartyProtocol.NormalizeCharacter(characterId);
        if (id == "spectator")
            return;

        long generation;
        CancellationToken token;
        lock (CharacterImageSync)
        {
            if (_assetsShutdown
                || _characterImageLifetime == null
                || CharacterImageSprites.ContainsKey(id)
                || CharacterImageRequested.ContainsKey(id))
                return;

            if (CharacterImageRetryAfter.TryGetValue(id, out var retryAfter)
                && DateTime.UtcNow < retryAfter)
                return;

            generation = _characterImageGeneration;
            token = _characterImageLifetime.Token;
            CharacterImageRequested[id] = generation;
        }

        _ = DownloadCharacterImageAsync(id, generation, token);
    }

    private static async Task DownloadCharacterImageAsync(
        string characterId,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var url =
                "https://file.maynutlab.com/character/UT_Item_Hero_"
                + characterId
                + ".png";
            using var response = await CharacterImageHttp.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await RemotePayload.ReadBytesAsync(
                response.Content, RemotePayload.MaxPortraitBytes, deadline.Token).ConfigureAwait(false);
            if (!RemotePayload.IsSafePortraitPng(bytes, out _, out _))
                throw new System.IO.InvalidDataException("Portrait PNG dimensions or format rejected.");

            lock (CharacterImageSync)
            {
                if (_assetsShutdown
                    || generation != _characterImageGeneration
                    || cancellationToken.IsCancellationRequested)
                    return;

                CharacterImageReady.Enqueue(
                    new CharacterImagePayload(characterId, generation, bytes));
            }
        }
        catch
        {
            MarkCharacterImageFailure(characterId, generation);
        }
    }

    private static void MarkCharacterImageFailure(string characterId, long generation)
    {
        lock (CharacterImageSync)
        {
            if (generation != _characterImageGeneration)
                return;

            if (CharacterImageRequested.TryGetValue(characterId, out var requestedGeneration)
                && requestedGeneration == generation)
                CharacterImageRequested.Remove(characterId);

            CharacterImageRetryAfter[characterId] = DateTime.UtcNow.AddSeconds(15);
        }
    }

    private static void ProcessCharacterImageDownloads()
    {
        // Spread native decoding work over frames when several portraits arrive together.
        for (var processed = 0; processed < 2; processed++)
        {
            CharacterImagePayload item;
            lock (CharacterImageSync)
            {
                if (CharacterImageReady.Count == 0)
                    return;

                item = CharacterImageReady.Dequeue();
                if (_assetsShutdown || item.Generation != _characterImageGeneration)
                    continue;
            }

            Texture2D? texture = null;
            Sprite? sprite = null;
            try
            {
                if (!RemotePayload.IsSafePortraitPng(item.Bytes, out var width, out var height))
                    throw new System.IO.InvalidDataException("Portrait PNG dimensions or format rejected.");
                texture = TrackOwnedUnityResource(
                    new Texture2D(2, 2, TextureFormat.RGBA32, false));
                texture.name = "AstralPartyChatPluginCharacter_" + item.CharacterId;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;

                var il2CppBytes = new Il2CppStructArray<byte>(item.Bytes);
                if (!ImageConversion.LoadImage(texture, il2CppBytes, true)
                    || texture.width != width || texture.height != height)
                {
                    ReleaseOwnedUnityResource(texture);
                    MarkCharacterImageFailure(item.CharacterId, item.Generation);
                    continue;
                }

                sprite = CreateOwnedSprite(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);

                var accepted = false;
                lock (CharacterImageSync)
                {
                    if (!_assetsShutdown
                        && item.Generation == _characterImageGeneration)
                    {
                        CharacterImageSprites[item.CharacterId] = sprite;
                        CharacterImageRequested.Remove(item.CharacterId);
                        CharacterImageRetryAfter.Remove(item.CharacterId);
                        accepted = true;
                    }
                }

                if (accepted)
                {
                    RefreshRenderedCharacterImage(item.CharacterId, sprite);
                }
                else
                {
                    ReleaseOwnedUnityResource(sprite);
                    ReleaseOwnedUnityResource(texture);
                }
            }
            catch
            {
                ReleaseOwnedUnityResource(sprite);
                ReleaseOwnedUnityResource(texture);
                MarkCharacterImageFailure(item.CharacterId, item.Generation);
            }
        }
    }

    private static bool TryGetCharacterSprite(string characterId, out Sprite? sprite)
    {
        lock (CharacterImageSync)
            return CharacterImageSprites.TryGetValue(characterId, out sprite);
    }

    private static MethodInfo? FindResourcesFontMethod()
    {
        try
        {
            return typeof(Resources)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, "FindObjectsOfTypeAll", StringComparison.Ordinal)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 0);
        }
        catch
        {
            return null;
        }
    }

    private static Font GetChatFont()
    {
        var preferred = FindGameKoreanFont();
        if (preferred != null)
        {
            _gameKoreanFont = preferred;
            _fontWasFound = true;
            return preferred;
        }

        return _gameKoreanFont ?? GetFont();
    }

    private static Font? FindGameKoreanFont()
    {
        if (_gameKoreanFont != null)
            return _gameKoreanFont;

        var now = Time.realtimeSinceStartup;
        if (now < _nextFontSearchAt)
            return null;

        _nextFontSearchAt = now + 5f;
        if (FindAllFontsMethod == null)
            return null;

        try
        {
            var fonts = FindAllFontsMethod
                .MakeGenericMethod(typeof(Font))
                .Invoke(null, null) as IEnumerable;
            if (fonts == null)
                return null;

            Font? match = null;
            foreach (var item in fonts)
            {
                if (item is Font font
                    && string.Equals(font.name, PreferredChatFontName, StringComparison.Ordinal))
                    match = font;
            }

            _gameKoreanFont = match;
            _fontWasFound = match != null;
            return match;
        }
        catch
        {
            return null;
        }
    }

    private static void RefreshGameKoreanFont()
    {
        // A destroyed cached Unity font is the only reason to invalidate a
        // successful lookup. Missing fonts are retried on a five-second timer.
        if (_fontWasFound && _gameKoreanFont == null)
        {
            _fontWasFound = false;
            _appliedGameKoreanFont = null;
            _nextFontSearchAt = 0f;
        }

        var preferred = FindGameKoreanFont();
        if (preferred == null || preferred == _appliedGameKoreanFont)
            return;

        _appliedGameKoreanFont = preferred;
        var changed = false;
        foreach (var text in _chatTexts)
        {
            if (text == null || text.font == preferred)
                continue;

            text.font = preferred;
            text.SetVerticesDirty();
            text.SetLayoutDirty();
            changed = true;
        }

        if (changed)
            RenderChatMessages(force: true);
    }

    private static Font GetFont()
    {
        if (_font != null)
            return _font;

        try { _font = CreateOwnedFallbackFont("Malgun Gothic"); }
        catch { }

        if (_font == null)
        {
            try { _font = CreateOwnedFallbackFont("맑은 고딕"); }
            catch { }
        }

        if (_font == null)
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        return _font;
    }

    private static Font? CreateOwnedFallbackFont(string fontName)
    {
        var font = Font.CreateDynamicFontFromOSFont(fontName, 24);
        return font == null ? null : TrackOwnedUnityResource(font);
    }

    private readonly struct CharacterImagePayload
    {
        public CharacterImagePayload(string characterId, long generation, byte[] bytes)
        {
            CharacterId = characterId;
            Generation = generation;
            Bytes = bytes;
        }

        public string CharacterId { get; }
        public long Generation { get; }
        public byte[] Bytes { get; }
    }
}
