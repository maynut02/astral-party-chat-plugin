using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AstralParty.Chat;

internal sealed class GamePortraitResource
{
    public GamePortraitResource(Texture texture, Rect uvRect)
    {
        Texture = texture;
        UvRect = uvRect;
    }

    public Texture Texture { get; }
    public Rect UvRect { get; }
}

internal static partial class GameChatRuntime
{
    private const float ScanIntervalSeconds = 2.0f;
    private const float SteamNameRefreshSeconds = 5.0f;
    private const float SteamTypeRetrySeconds = 10.0f;
    private static readonly object Sync = new object();

    private static ManualLogSource? _log;
    private static MethodInfo? _findObjectsOfTypeAll;
    private static readonly Dictionary<string, Type?> NativeWrapperTypeCache = new Dictionary<string, Type?>(StringComparer.Ordinal);
    private static readonly MethodInfo? TryCastMethod = typeof(Il2CppObjectBase)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .FirstOrDefault(method => method.Name == "TryCast" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
    private static readonly HashSet<int> KnownCharacterIds = new HashSet<int>(
        Enumerable.Range(101, 29).Concat(Enumerable.Range(301, 6)));
    private static readonly Dictionary<PortraitLookupKey, PortraitLookupCacheEntry> BattlePortraitCache =
        new Dictionary<PortraitLookupKey, PortraitLookupCacheEntry>();
    private static readonly Dictionary<string, float> WarningNextAt =
        new Dictionary<string, float>(StringComparer.Ordinal);
    private static float _nextScanAt;
    private static bool _initialized;
    private static bool _scanning;
    private static ChatSnapshot _snapshot = new ChatSnapshot();
    private static readonly GameSessionState Session = new GameSessionState();
    private static Camera[] _scanCameras = Array.Empty<Camera>();
    private static Type? _steamClientType;
    private static PropertyInfo? _steamNameProperty;
    private static string _cachedSteamName = string.Empty;
    private static float _nextSteamNameRefreshAt;
    private static float _nextSteamTypeLookupAt;
    private static int _portraitCacheGeneration;

    private readonly record struct PortraitLookupKey(
        string RoomId,
        string Nickname,
        string Phase);

    private sealed class PortraitLookupCacheEntry
    {
        public GamePortraitResource? Portrait { get; init; }
        public float RetryAfter { get; init; }
        public float LastUsedAt { get; set; }
        public int Failures { get; init; }
    }

    internal static bool TryGetBattlePlayerPortrait(
        string nickname,
        out GamePortraitResource? portrait)
    {
        portrait = null;
        if (string.IsNullOrWhiteSpace(nickname))
            return false;

        var phase = Session.Phase;
        if (!string.Equals(phase, "플레이", StringComparison.Ordinal))
            return false;

        var roomId = Session.RoomId;
        var key = new PortraitLookupKey(roomId, nickname, phase);
        var now = Time.unscaledTime;
        var generation = 0;
        var nextFailures = 1;

        lock (Sync)
        {
            if (BattlePortraitCache.TryGetValue(key, out var cached))
            {
                cached.LastUsedAt = now;
                if (cached.Portrait?.Texture != null)
                {
                    portrait = cached.Portrait;
                    return true;
                }

                if (now < cached.RetryAfter)
                    return false;

                nextFailures = Math.Min(cached.Failures + 1, 6);
            }

            generation = _portraitCacheGeneration;
            StorePortraitLookupLocked(
                key,
                new PortraitLookupCacheEntry
                {
                    Failures = nextFailures,
                    RetryAfter = now + PortraitRetryDelay(nextFailures),
                    LastUsedAt = now
                });
        }

        GamePortraitResource? foundPortrait = null;
        try
        {
            var components = FindAll(typeof(MonoBehaviour))
                .OfType<MonoBehaviour>()
                .Where(IsActiveComponent)
                .Take(12000)
                .ToArray();

            var texts = CollectFairyGuiTexts(
                components,
                path => path.Contains(
                    "/BattleInfoPanel/BattleInfo_Com_Player/BattleInfo_Com_PlayerContainer/BattleInfo_Button_PlayerInfo/",
                    StringComparison.Ordinal));

            Transform? matchingPanel = null;
            foreach (var entry in texts)
            {
                if (!string.Equals(entry.Text, nickname, StringComparison.Ordinal))
                    continue;

                matchingPanel = FindAncestorNamed(
                    entry.Transform,
                    "BattleInfo_Button_PlayerInfo");
                if (matchingPanel != null)
                    break;
            }

            if (matchingPanel != null)
            {
                foreach (var component in components)
                {
                    if (!string.Equals(
                            NativeTypeName(component),
                            "FairyGUI.DisplayObjectInfo",
                            StringComparison.Ordinal))
                        continue;

                    if (!string.Equals(
                            SafeName(component.gameObject),
                            "Image",
                            StringComparison.Ordinal))
                        continue;

                    if (!IsDescendantOf(component.transform, matchingPanel))
                        continue;

                    var path = HierarchyPath(component.transform);
                    if (!path.Contains(
                            "/Com_playerInfo_Head/GLoader/Image",
                            StringComparison.Ordinal))
                        continue;

                    if (TryExtractPortraitFromRenderedObject(
                            component.transform,
                            out var renderedPortrait)
                        && renderedPortrait != null)
                    {
                        foundPortrait = renderedPortrait;
                        break;
                    }
                }
            }
        }
        catch { }

        lock (Sync)
        {
            if (generation != _portraitCacheGeneration
                || !string.Equals(Session.Phase, phase, StringComparison.Ordinal)
                || !string.Equals(Session.RoomId, roomId, StringComparison.Ordinal))
                return false;

            var completedAt = Time.unscaledTime;
            if (foundPortrait == null)
            {
                StorePortraitLookupLocked(
                    key,
                    new PortraitLookupCacheEntry
                    {
                        Failures = nextFailures,
                        RetryAfter = completedAt + PortraitRetryDelay(nextFailures),
                        LastUsedAt = completedAt
                    });
                return false;
            }

            StorePortraitLookupLocked(
                key,
                new PortraitLookupCacheEntry
                {
                    Portrait = foundPortrait,
                    LastUsedAt = completedAt
                });
            portrait = foundPortrait;
            return true;
        }
    }

    private static float PortraitRetryDelay(int failures) =>
        Math.Min(30f, 2f * (float)Math.Pow(2d, Math.Min(failures - 1, 4)));

    private static void StorePortraitLookupLocked(
        PortraitLookupKey key,
        PortraitLookupCacheEntry entry)
    {
        const int maxEntries = 128;
        if (!BattlePortraitCache.ContainsKey(key)
            && BattlePortraitCache.Count >= maxEntries)
        {
            var oldest = BattlePortraitCache
                .OrderBy(pair => pair.Value.LastUsedAt)
                .FirstOrDefault();
            if (!oldest.Equals(default(KeyValuePair<PortraitLookupKey, PortraitLookupCacheEntry>)))
                BattlePortraitCache.Remove(oldest.Key);
        }

        BattlePortraitCache[key] = entry;
    }

    private static bool TryExtractPortraitFromRenderedObject(
        Transform root,
        out GamePortraitResource? portrait)
    {
        portrait = null;

        try
        {
            var stack = new Stack<Transform>();
            stack.Push(root);
            var guard = 0;

            while (stack.Count > 0 && guard++ < 32)
            {
                var current = stack.Pop();

                try
                {
                    var renderer = current.GetComponent<Renderer>();
                    var meshFilter = current.GetComponent<MeshFilter>();
                    if (renderer != null && meshFilter != null)
                    {
                        var material = renderer.sharedMaterial;
                        var texture = material != null
                            ? material.mainTexture
                            : null;
                        var mesh = meshFilter.sharedMesh;

                        if (texture != null && mesh != null)
                        {
                            var uvs = mesh.uv;
                            if (uvs != null && uvs.Length > 0)
                            {
                                var minX = 1f;
                                var minY = 1f;
                                var maxX = 0f;
                                var maxY = 0f;

                                for (var index = 0; index < uvs.Length; index++)
                                {
                                    var uv = uvs[index];
                                    minX = Mathf.Min(minX, uv.x);
                                    minY = Mathf.Min(minY, uv.y);
                                    maxX = Mathf.Max(maxX, uv.x);
                                    maxY = Mathf.Max(maxY, uv.y);
                                }

                                var width = maxX - minX;
                                var height = maxY - minY;
                                if (width > 0.0001f && height > 0.0001f)
                                {
                                    portrait = new GamePortraitResource(
                                        texture,
                                        new Rect(
                                            Mathf.Clamp01(minX),
                                            Mathf.Clamp01(minY),
                                            Mathf.Clamp01(width),
                                            Mathf.Clamp01(height)));
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch { }

                for (var index = 0; index < current.childCount; index++)
                {
                    try { stack.Push(current.GetChild(index)); }
                    catch { }
                }
            }
        }
        catch { }

        return false;
    }

    public static ChatGameState GetChatGameState()
    {
        var snapshot = _snapshot;
        return new ChatGameState
        {
            Available = snapshot.ChatAvailable,
            Phase = Session.Phase,
            RoomId = Session.RoomId,
            Nickname = GetLocalSteamName(),
            CharacterId = CleanCandidate(Session.Character),
            Order = CleanCandidate(Session.Order)
        };
    }

    private static string CleanCandidate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "후보 없음", StringComparison.Ordinal))
            return string.Empty;

        var marker = value.IndexOf("  [", StringComparison.Ordinal);
        return (marker >= 0 ? value[..marker] : value).Trim();
    }

    public static void Initialize(ManualLogSource log)
    {
        if (_initialized) return;
        _initialized = true;
        _log = log;

        _findObjectsOfTypeAll = typeof(Resources)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "FindObjectsOfTypeAll", StringComparison.Ordinal)
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 0);
    }

    public static void Tick()
    {
        if (!_initialized) return;

        try
        {
            ChatOverlay.EnsureCreated();

            bool mouseDown;
            ChatOverlay.SetReadingOverlayInput(true);
            try
            {
                mouseDown = Input.GetMouseButtonDown(0);
            }
            finally
            {
                ChatOverlay.SetReadingOverlayInput(false);
            }

            if (mouseDown)
                ChatOverlay.HandlePointerClick();

            if (Time.unscaledTime >= _nextScanAt)
                Scan();

            ChatOverlay.Refresh(_snapshot);
        }
        catch (Exception ex)
        {
            LogRuntimeWarning(
                "tick",
                "Chat runtime tick failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Scan()
    {
        if (_scanning) return;
        _scanning = true;

        try
        {
            _nextScanAt = Time.unscaledTime + ScanIntervalSeconds;
            _scanCameras = FindAll(typeof(Camera))
                .OfType<Camera>()
                .Where(IsActiveComponent)
                .ToArray();

            var components = FindAll(typeof(MonoBehaviour))
                .OfType<MonoBehaviour>()
                .Where(IsActiveComponent)
                .Take(12000)
                .ToArray();

            var next = new ChatSnapshot();
            next.ScreenPhase = DetectScreenPhase(components);
            var previousPhase = Session.Phase;
            if (Session.ObserveScreenPhase(next.ScreenPhase, Time.unscaledTime))
                ResetBattlePortraitCache();
            next.ChatAvailable = Session.ChatAvailable;
            if (next.ScreenPhase != _snapshot.ScreenPhase || Session.Phase != previousPhase)
                _log?.LogInfo("Chat screen: " + next.ScreenPhase + "; session phase: " + Session.Phase + ".");

            // Read the current RoomWaitPanel identity on every scan, including
            // character selection and battle scans. A changed room is applied
            // before this scan can discover and store a new player order.
            var hasObservedRoomId = TryInspectCurrentRoomId(components, out var observedRoomId);
            if (Session.ObserveRoomId(hasObservedRoomId ? observedRoomId : string.Empty))
                ResetBattlePortraitCache();

            var placement = ResolveChatButtonPlacement(
                next.ScreenPhase,
                components);
            next.ChatButtonVisible = placement.Visible;
            next.ChatButtonScreenPoint = placement.ScreenPoint;
            next.ChatButtonScreenSize = placement.ScreenSize;

            var steamName = GetLocalSteamName();
            next.Nickname = string.IsNullOrWhiteSpace(steamName)
                ? "후보 없음"
                : steamName;

            if (string.Equals(next.ScreenPhase, "방", StringComparison.Ordinal))
            {
                var orderCandidates = new List<ScoredValue>();
                InspectRoomPlayerSlots(components, orderCandidates);
                var detectedOrder = Best(orderCandidates);
                if (!string.Equals(detectedOrder, "후보 없음", StringComparison.Ordinal))
                    Session.SetOrder(detectedOrder);
            }

            if (string.Equals(next.ScreenPhase, "캐릭터 선택", StringComparison.Ordinal))
            {
                var orderCandidates = new List<ScoredValue>();
                InspectHeroPlayerTexts(components, orderCandidates);
                var detectedOrder = Best(orderCandidates);
                if (!string.Equals(detectedOrder, "후보 없음", StringComparison.Ordinal))
                    Session.SetOrder(detectedOrder);
            }
            else if (string.Equals(next.ScreenPhase, "플레이", StringComparison.Ordinal))
            {
                var orderCandidates = new List<ScoredValue>();
                InspectBattlePlayerTexts(components, orderCandidates);
                var detectedOrder = Best(orderCandidates);
                if (!string.Equals(detectedOrder, "후보 없음", StringComparison.Ordinal))
                    Session.SetOrder(detectedOrder);

                var detectedCharacter = DetectBattleCharacterId(components);
                if (!string.IsNullOrWhiteSpace(detectedCharacter))
                    Session.SetCharacter(detectedCharacter);
            }

            next.RoomId = Session.RoomId;
            next.Order = next.ChatAvailable && !string.IsNullOrWhiteSpace(Session.Order)
                ? Session.Order
                : "후보 없음";
            next.Character = next.ChatAvailable && !string.IsNullOrWhiteSpace(Session.Character)
                ? Session.Character
                : "후보 없음";

            lock (Sync)
                _snapshot = next;
        }
        catch (Exception ex)
        {
            LogRuntimeWarning(
                "scan",
                "Chat state scan failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _scanCameras = Array.Empty<Camera>();
            _scanning = false;
        }
    }

    private static ChatButtonPlacement ResolveChatButtonPlacement(
        string phase,
        MonoBehaviour[] components)
    {
        // These are normalized against the live game viewport and were tuned
        // against the three target layouts. The old FairyGUI Transform projection
        // was suitable for pointer inspection but not stable enough for UI placement.
        if (string.Equals(phase, "방", StringComparison.Ordinal))
        {
            return new ChatButtonPlacement(
                true,
                new Vector2(Screen.width * 0.902f, Screen.height * 0.145f),
                0f);
        }

        if (string.Equals(phase, "캐릭터 선택", StringComparison.Ordinal))
        {
            return new ChatButtonPlacement(
                true,
                new Vector2(Screen.width * 0.953f, Screen.height * 0.275f),
                0f);
        }

        if (string.Equals(phase, "플레이", StringComparison.Ordinal))
        {
            if (TryResolveBattleExpressionStackPlacement(
                    components,
                    out var stackPoint,
                    out var stackButtonSize))
            {
                return new ChatButtonPlacement(
                    true,
                    stackPoint,
                    stackButtonSize);
            }

            return new ChatButtonPlacement(
                true,
                new Vector2(
                    (Screen.width * 0.982f) - 3f,
                    (Screen.height * 0.700f) + 1f),
                0f);
        }

        return new ChatButtonPlacement(false, Vector2.zero, 0f);
    }

    private static bool TryResolveBattleExpressionStackPlacement(
        MonoBehaviour[] components,
        out Vector2 screenPoint,
        out float screenButtonSize)
    {
        screenPoint = Vector2.zero;
        screenButtonSize = 0f;

        var quickChat = FindFairyGuiObject(
            components,
            "Expression_Button_Chat",
            "/Expression_Com_Interact_PC/");
        var ping = FindFairyGuiObject(
            components,
            "Expression_Button_MapChat",
            "/Expression_Com_Interact_PC/");

        if (quickChat == null || ping == null
            || !TryGetUiCamera(out var uiCamera)
            || uiCamera == null)
            return false;

        Vector2 quickPoint;
        Vector2 pingPoint;

        var hasQuickRect = TryGetRenderedScreenRect(
            quickChat.transform,
            uiCamera,
            out var quickRect);
        var hasPingRect = TryGetRenderedScreenRect(
            ping.transform,
            uiCamera,
            out var pingRect);

        if (hasQuickRect && hasPingRect)
        {
            quickPoint = quickRect.center;
            pingPoint = pingRect.center;

            var measuredSize = Mathf.Max(
                pingRect.width,
                pingRect.height);
            if (measuredSize >= 35f && measuredSize <= 120f)
                screenButtonSize = measuredSize;
        }
        else
        {
            if (!TryEstimateScreenPointWithUiCamera(
                    quickChat.transform,
                    out quickPoint)
                || !TryEstimateScreenPointWithUiCamera(
                    ping.transform,
                    out pingPoint))
                return false;
        }

        var step = pingPoint - quickPoint;
        var spacing = step.magnitude;

        if (quickPoint == Vector2.zero
            || pingPoint == Vector2.zero
            || Math.Abs(step.x) > 45f
            || spacing < 35f
            || spacing > 180f)
            return false;

        // Reuse the native Quick Chat -> Ping center-to-center vector for the
        // fourth slot. Using renderer bounds rather than Transform.position
        // also corrects FairyGUI objects whose transform origin is not centered.
        screenPoint = pingPoint + step;

        if (screenButtonSize <= 0f)
            screenButtonSize = Mathf.Clamp(spacing * 0.78f, 44f, 78f);

        return screenPoint.x >= -100f
            && screenPoint.x <= Screen.width + 100f
            && screenPoint.y >= -100f
            && screenPoint.y <= Screen.height + 100f;
    }

    private static bool TryGetUiCamera(out Camera? uiCamera)
    {
        uiCamera = null;

        try
        {
            uiCamera = _scanCameras.FirstOrDefault(camera =>
                    string.Equals(
                        SafeName(camera.gameObject),
                        "UICamera",
                        StringComparison.Ordinal));

            return uiCamera != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetRenderedScreenRect(
        Transform root,
        Camera camera,
        out Rect screenRect)
    {
        screenRect = default;

        try
        {
            var renderers = root
                .GetComponentsInChildren<Renderer>(false)
                .Where(renderer =>
                    renderer != null
                    && renderer.enabled
                    && renderer.gameObject.activeInHierarchy)
                .ToArray();

            if (renderers.Length == 0)
                return false;

            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;
            var pointCount = 0;

            foreach (var renderer in renderers)
            {
                var bounds = renderer.bounds;
                var min = bounds.min;
                var max = bounds.max;

                for (var xi = 0; xi < 2; xi++)
                for (var yi = 0; yi < 2; yi++)
                for (var zi = 0; zi < 2; zi++)
                {
                    var world = new Vector3(
                        xi == 0 ? min.x : max.x,
                        yi == 0 ? min.y : max.y,
                        zi == 0 ? min.z : max.z);
                    var point = camera.WorldToScreenPoint(world);
                    if (point.z < -0.01f)
                        continue;

                    minX = Mathf.Min(minX, point.x);
                    minY = Mathf.Min(minY, point.y);
                    maxX = Mathf.Max(maxX, point.x);
                    maxY = Mathf.Max(maxY, point.y);
                    pointCount++;
                }
            }

            if (pointCount == 0)
                return false;

            var width = maxX - minX;
            var height = maxY - minY;
            if (width < 10f || height < 10f
                || width > 180f || height > 180f)
                return false;

            screenRect = new Rect(minX, minY, width, height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEstimateScreenPointWithUiCamera(
        Transform transform,
        out Vector2 screenPoint)
    {
        screenPoint = Vector2.zero;

        try
        {
            if (TryGetUiCamera(out var uiCamera)
                && uiCamera != null)
            {
                var point = uiCamera.WorldToScreenPoint(transform.position);
                if (point.z >= -0.01f
                    && point.x >= -200f
                    && point.x <= Screen.width + 200f
                    && point.y >= -200f
                    && point.y <= Screen.height + 200f)
                {
                    screenPoint = new Vector2(point.x, point.y);
                    return true;
                }
            }

            var fallback = EstimateScreenPoint(transform);
            if (fallback == Vector2.zero)
                return false;

            screenPoint = fallback;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MonoBehaviour? FindFairyGuiObject(
        MonoBehaviour[] components,
        string objectName,
        string requiredPathPart)
    {
        foreach (var component in components)
        {
            try
            {
                if (!string.Equals(
                        NativeTypeName(component),
                        "FairyGUI.DisplayObjectInfo",
                        StringComparison.Ordinal))
                    continue;

                if (!string.Equals(
                        SafeName(component.gameObject),
                        objectName,
                        StringComparison.Ordinal))
                    continue;

                var path = HierarchyPath(component.transform);
                if (!path.Contains(requiredPathPart, StringComparison.Ordinal))
                    continue;

                return component;
            }
            catch { }
        }

        return null;
    }

    

    private static string DetectScreenPhase(MonoBehaviour[] components)
    {
        var hasBattle = false;
        var hasHero = false;
        var hasRoom = false;
        var hasRoomSetup = false;

        foreach (var component in components)
        {
            string path;
            try { path = HierarchyPath(component.transform); }
            catch { continue; }

            if (path.Contains("BattleInfoPanel", StringComparison.Ordinal)
                || path.Contains("BattleController(Clone)", StringComparison.Ordinal))
                hasBattle = true;

            if (path.Contains("/RoomHeroPanel", StringComparison.Ordinal))
                hasHero = true;

            if (path.Contains("/RoomWaitPanel", StringComparison.Ordinal))
                hasRoom = true;

            if (path.Contains("/RoomListPanel", StringComparison.Ordinal)
                || path.Contains("/RoomCreate", StringComparison.Ordinal)
                || path.Contains("/RoomSetting", StringComparison.Ordinal))
                hasRoomSetup = true;
        }

        if (hasBattle) return "플레이";
        if (hasHero) return "캐릭터 선택";
        if (hasRoom) return "방";
        if (hasRoomSetup) return "방 설정";
        return "기타";
    }

    private static void InspectRoomPlayerSlots(
        MonoBehaviour[] components,
        List<ScoredValue> orderCandidates)
    {
        var slots = new List<RoomSlotInfo>();
        var seen = new HashSet<int>();

        foreach (var component in components)
        {
            try
            {
                if (!string.Equals(
                        NativeTypeName(component),
                        "FairyGUI.DisplayObjectInfo",
                        StringComparison.Ordinal))
                    continue;

                if (!string.Equals(
                        SafeName(component.gameObject),
                        "RoomPlayer_Com_PlayerLabel",
                        StringComparison.Ordinal))
                    continue;

                var path = HierarchyPath(component.transform);
                if (!path.Contains("/RoomWaitPanel/", StringComparison.Ordinal))
                    continue;

                var id = component.gameObject.GetInstanceID();
                if (!seen.Add(id)) continue;

                slots.Add(new RoomSlotInfo(
                    component,
                    SafeSiblingIndex(component.transform),
                    SafeWorldPosition(component.transform)));
            }
            catch { }
        }

        if (slots.Count == 0) return;

        var ordered = slots
            .OrderBy(slot => slot.SiblingIndex)
            .ThenByDescending(slot => slot.WorldPosition.y)
            .ThenBy(slot => slot.WorldPosition.x)
            .ToArray();

        var steamName = GetLocalSteamName();
        var namedSlots = ordered
            .Select((slot, index) => new
            {
                Slot = slot,
                Index = index,
                Name = ReadRoomSlotPlayerName(slot, components)
            })
            .ToArray();

        var localByName = namedSlots
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Name)
                && !string.IsNullOrWhiteSpace(steamName)
                && string.Equals(entry.Name, steamName, StringComparison.Ordinal))
            .ToArray();

        if (localByName.Length == 1)
        {
            var playerIndex = localByName[0].Index + 1;
            if (playerIndex >= 1 && playerIndex <= 4)
            {
                var order = "P" + playerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                orderCandidates.Add(new ScoredValue(400, order + "  [Room player name match]"));
            }
        }

    }

    private static string ReadRoomSlotPlayerName(
        RoomSlotInfo slot,
        MonoBehaviour[] components)
    {
        foreach (var component in components)
        {
            try
            {
                if (!string.Equals(
                        NativeTypeName(component),
                        "FairyGUI.DisplayObjectInfo",
                        StringComparison.Ordinal))
                    continue;

                if (!IsDescendantOf(component.transform, slot.Component.transform))
                    continue;

                if (!string.Equals(
                        SafeName(component.gameObject),
                        "TextField",
                        StringComparison.Ordinal))
                    continue;

                var path = HierarchyPath(component.transform);
                if (!path.Contains(
                        "/Com_PlayerName/Container/TextField",
                        StringComparison.Ordinal))
                    continue;

                if (TryReadFairyGuiText(component, out var text)
                    && IsPlausibleText(text, 1, 80))
                    return text.Trim();
            }
            catch { }
        }

        return string.Empty;
    }

    private static bool TryReadFairyGuiText(
        MonoBehaviour displayObjectInfo,
        out string text)
    {
        text = string.Empty;

        if (displayObjectInfo is not Il2CppObjectBase il2CppObject
            || il2CppObject.Pointer == IntPtr.Zero)
            return false;

        try
        {
            var infoClass = IL2CPP.il2cpp_object_get_class(il2CppObject.Pointer);
            if (infoClass == IntPtr.Zero) return false;

            var displayObjectField = FindNativeField(infoClass, "displayObject");
            if (displayObjectField == IntPtr.Zero) return false;

            var displayObject = IL2CPP.il2cpp_field_get_value_object(
                displayObjectField,
                il2CppObject.Pointer);
            if (displayObject == IntPtr.Zero) return false;

            var textClass = IL2CPP.il2cpp_object_get_class(displayObject);
            if (textClass == IntPtr.Zero) return false;

            foreach (var fieldName in new[] { "_text", "_parsedText", "text" })
            {
                var field = FindNativeField(textClass, fieldName);
                if (field == IntPtr.Zero) continue;

                var fieldType = IL2CPP.il2cpp_field_get_type(field);
                if (fieldType == IntPtr.Zero) continue;

                var typeCode = IL2CPP.il2cpp_type_get_type(fieldType);
                if (typeCode != 14) continue;

                if (TryReadNativeFieldValue(
                        field,
                        displayObject,
                        fieldType,
                        typeCode,
                        out var value)
                    && value is string stringValue
                    && !string.IsNullOrWhiteSpace(stringValue))
                {
                    text = stringValue;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static IntPtr FindNativeField(IntPtr klass, string fieldName)
    {
        var current = klass;
        var depth = 0;

        while (current != IntPtr.Zero && depth++ < 12)
        {
            var iterator = IntPtr.Zero;

            while (true)
            {
                var field = IL2CPP.il2cpp_class_get_fields(current, ref iterator);
                if (field == IntPtr.Zero) break;

                var name = Marshal.PtrToStringAnsi(
                    IL2CPP.il2cpp_field_get_name(field));
                if (string.Equals(name, fieldName, StringComparison.Ordinal))
                    return field;
            }

            current = IL2CPP.il2cpp_class_get_parent(current);
        }

        return IntPtr.Zero;
    }

    private static bool TryInspectCurrentRoomId(
        MonoBehaviour[] components,
        out string roomId)
    {
        roomId = string.Empty;
        var texts = CollectFairyGuiTexts(
            components,
            path => path.Contains("/RoomWaitPanel/", StringComparison.Ordinal));

        foreach (var entry in texts)
        {
            if (!TryExtractRoomId(entry.Text, out var candidate))
                continue;

            roomId = candidate;
            return true;
        }

        return false;
    }

    private static void InspectHeroPlayerTexts(
        MonoBehaviour[] components,
        List<ScoredValue> orderCandidates)
    {
        var texts = CollectFairyGuiTexts(
            components,
            path => path.Contains(
                "/RoomHeroPanel/RoomHero_Com_Player/",
                StringComparison.Ordinal),
            includeScreenPoint: true);

        var steamName = GetLocalSteamName();
        if (string.IsNullOrWhiteSpace(steamName))
            return;

        var nameEntries = texts
            .Where(entry =>
                entry.Path.Contains(
                    "/Com_PlayerName/Container/TextField",
                    StringComparison.Ordinal)
                && IsPlausibleText(entry.Text, 1, 80))
            .OrderBy(entry => entry.ScreenPoint.x)
            .ToArray();

        var localIndex = Array.FindIndex(
            nameEntries,
            entry => string.Equals(
                entry.Text,
                steamName,
                StringComparison.Ordinal));

        if (localIndex >= 0 && localIndex < 4)
        {
            var order = "P" + (localIndex + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);

            orderCandidates.Add(new ScoredValue(
                800,
                order + "  [RoomHeroPanel name x-order]"));

        }
    }

    private static void InspectBattlePlayerTexts(
        MonoBehaviour[] components,
        List<ScoredValue> orderCandidates)
    {
        var texts = CollectFairyGuiTexts(
            components,
            path =>
                path.Contains("/BattleInfoPanel/", StringComparison.Ordinal)
                && ContainsAny(
                    Normalize(path),
                    "player", "character", "nickname", "name"));

        var steamName = GetLocalSteamName();
        if (string.IsNullOrWhiteSpace(steamName))
            return;

        var grouped = new Dictionary<int, (Transform Panel, List<FairyTextEntry> Entries)>();

        foreach (var entry in texts)
        {
            var panel = FindAncestorNamed(
                entry.Transform,
                "BattleInfo_Button_PlayerInfo");

            if (panel == null)
                continue;

            var id = panel.gameObject.GetInstanceID();
            if (!grouped.TryGetValue(id, out var group))
            {
                group = (panel, new List<FairyTextEntry>());
                grouped[id] = group;
            }

            group.Entries.Add(entry);
        }

        foreach (var group in grouped.Values)
        {
            if (!group.Entries.Any(entry =>
                    string.Equals(
                        entry.Text,
                        steamName,
                        StringComparison.Ordinal)))
                continue;

            foreach (var entry in group.Entries)
            {
                if (!TryParseExplicitPlayerOrder(entry.Text, out var order))
                    continue;

                orderCandidates.Add(new ScoredValue(
                    1000,
                    order + "  [Battle player card/name+rank]"));

                return;
            }
        }
    }

    private static List<FairyTextEntry> CollectFairyGuiTexts(
        MonoBehaviour[] components,
        Func<string, bool> pathPredicate,
        bool includeScreenPoint = false)
    {
        var result = new List<FairyTextEntry>();

        foreach (var component in components)
        {
            try
            {
                if (!string.Equals(
                        NativeTypeName(component),
                        "FairyGUI.DisplayObjectInfo",
                        StringComparison.Ordinal))
                    continue;

                if (!string.Equals(
                        SafeName(component.gameObject),
                        "TextField",
                        StringComparison.Ordinal))
                    continue;

                var path = HierarchyPath(component.transform);
                if (!pathPredicate(path))
                    continue;

                if (!TryReadFairyGuiText(component, out var text)
                    || !IsPlausibleText(text.Trim(), 1, 100))
                    continue;

                result.Add(new FairyTextEntry(
                    text.Trim(),
                    path,
                    component.transform,
                    includeScreenPoint
                        ? EstimateScreenPoint(component.transform)
                        : Vector2.zero));
            }
            catch { }
        }

        return result
            .GroupBy(entry => entry.Path + "\u001f" + entry.Text)
            .Select(group => group.First())
            .ToList();
    }

    private static Vector2 EstimateScreenPoint(Transform transform)
    {
        try
        {
            var world = transform.position;
            foreach (var camera in _scanCameras)
            {
                var point = camera.WorldToScreenPoint(world);
                if (point.z >= -0.01f
                    && point.x >= -200f && point.x <= Screen.width + 200f
                    && point.y >= -200f && point.y <= Screen.height + 200f)
                    return new Vector2(point.x, point.y);
            }

            return new Vector2(world.x, world.y);
        }
        catch
        {
            return Vector2.zero;
        }
    }

    private static bool TryParseExplicitPlayerOrder(
        string text,
        out string order)
    {
        order = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = Normalize(text).ToUpperInvariant();

        for (var index = 1; index <= 4; index++)
        {
            var number = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ordinal = index switch
            {
                1 => "1ST",
                2 => "2ND",
                3 => "3RD",
                4 => "4TH",
                _ => string.Empty
            };

            if (normalized == "P" + number
                || normalized == number + "P"
                || normalized == "PLAYER" + number
                || normalized == "PLAYER" + number + "P"
                || normalized == ordinal)
            {
                order = "P" + number;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractRoomId(
        string text,
        out string roomId)
    {
        roomId = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        var looksLikeRoomId =
            trimmed.Contains("방 ID", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("방ID", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Room ID", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("RoomID", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeRoomId)
            return false;

        var markerEnd = trimmed.IndexOf("방 ID", StringComparison.OrdinalIgnoreCase);
        if (markerEnd >= 0)
            markerEnd += "방 ID".Length;
        else if ((markerEnd = trimmed.IndexOf("방ID", StringComparison.OrdinalIgnoreCase)) >= 0)
            markerEnd += "방ID".Length;
        else if ((markerEnd = trimmed.IndexOf("Room ID", StringComparison.OrdinalIgnoreCase)) >= 0)
            markerEnd += "Room ID".Length;
        else if ((markerEnd = trimmed.IndexOf("RoomID", StringComparison.OrdinalIgnoreCase)) >= 0)
            markerEnd += "RoomID".Length;

        if (markerEnd < 0)
            return false;

        var suffix = trimmed.Substring(markerEnd);
        var start = -1;
        for (var index = 0; index < suffix.Length; index++)
        {
            var character = suffix[index];
            if (character < '0' || character > '9')
            {
                if (start >= 0)
                    break;
                continue;
            }

            if (start < 0)
                start = index;
        }

        if (start < 0)
            return false;

        var end = start;
        while (end < suffix.Length && suffix[end] >= '0' && suffix[end] <= '9')
            end++;

        if (end - start != 6)
            return false;

        roomId = suffix.Substring(start, 6);
        return true;
    }

    private static Transform? FindAncestorNamed(
        Transform? start,
        string targetName)
    {
        var current = start;
        var guard = 0;

        while (current != null && guard++ < 32)
        {
            try
            {
                if (string.Equals(
                        SafeName(current.gameObject),
                        targetName,
                        StringComparison.Ordinal))
                    return current;

                current = current.parent;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    

    

    

    private static Vector2 SafeWorldPosition(Transform transform)
    {
        try
        {
            var position = transform.position;
            return new Vector2(position.x, position.y);
        }
        catch
        {
            return Vector2.zero;
        }
    }

    

    

    private static string DetectBattleCharacterId(MonoBehaviour[] components)
    {
        var steamName = GetLocalSteamName();
        if (string.IsNullOrWhiteSpace(steamName))
            return string.Empty;

        foreach (var component in components)
        {
            try
            {
                var nativeTypeName = NativeTypeName(component);
                if (!string.Equals(
                        nativeTypeName,
                        "Cinemachine.CinemachineVirtualCamera",
                        StringComparison.Ordinal))
                    continue;

                ResolveConcreteComponent(
                    component,
                    nativeTypeName,
                    out var instance,
                    out var type);

                var gameObjectName = SafeName(component.gameObject);
                if (!string.Equals(
                        gameObjectName,
                        steamName + "_VirtualCamera",
                        StringComparison.Ordinal))
                    continue;

                var path = HierarchyPath(component.transform);
                if (!path.Contains("BattleController(Clone)", StringComparison.Ordinal))
                    continue;

                var followPath = ReadTransformPropertyPath(instance, type, "Follow")
                    ?? ReadTransformPropertyPath(instance, type, "m_Follow");
                var localEntity = FirstHierarchySegment(followPath ?? string.Empty);

                return TryCharacterIdFromEntityName(
                        localEntity,
                        out var characterId)
                    ? characterId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            catch { }
        }

        return string.Empty;
    }

    private static string GetLocalSteamName()
    {
        try
        {
            var now = Time.unscaledTime;
            if (now >= _nextSteamNameRefreshAt)
            {
                _nextSteamNameRefreshAt = now + SteamNameRefreshSeconds;
                if (_steamNameProperty == null && now >= _nextSteamTypeLookupAt)
                {
                    _nextSteamTypeLookupAt = now + SteamTypeRetrySeconds;
                    _steamClientType ??= FindType("Steamworks.SteamClient");
                    _steamNameProperty = _steamClientType?.GetProperty(
                        "Name",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                var value = _steamNameProperty?.GetValue(null)?.ToString()?.Trim();
                if (value != null)
                    _cachedSteamName = value;
            }

            return _cachedSteamName;
        }
        catch
        {
            return _cachedSteamName;
        }
    }

    

    private static string? ReadTransformPropertyPath(object instance, Type type, string name)
    {
        try
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetMethod == null) return null;

            var value = property.GetValue(instance);
            return value is Transform transform ? HierarchyPath(transform) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FirstHierarchySegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var index = path.IndexOf('/');
        return index < 0 ? path : path.Substring(0, index);
    }

    

    

    

    

    

    

    

    

    private static bool IsDescendantOf(Transform? candidate, Transform? root)
    {
        if (candidate == null || root == null) return false;

        var current = candidate;
        var guard = 0;
        while (current != null && guard++ < 64)
        {
            if (current == root) return true;
            try { current = current.parent; }
            catch { break; }
        }

        return false;
    }

    private static void ResolveConcreteComponent(
        MonoBehaviour component,
        string nativeTypeName,
        out object instance,
        out Type type)
    {
        instance = component;
        type = component.GetType();

        if (string.IsNullOrWhiteSpace(nativeTypeName))
            nativeTypeName = type.FullName ?? type.Name;

        var wrapperType = ResolveWrapperType(nativeTypeName);
        if (wrapperType == null || wrapperType == type || TryCastMethod == null)
            return;

        try
        {
            var cast = TryCastMethod.MakeGenericMethod(wrapperType).Invoke(component, null);
            if (cast == null) return;
            instance = cast;
            type = wrapperType;
        }
        catch
        {
            // Keep the base MonoBehaviour wrapper. Native type name is still
            // useful in logs even if a generated managed wrapper cannot be cast.
        }
    }

    private static string NativeTypeName(Component component)
    {
        try
        {
            if (component is not Il2CppObjectBase il2CppObject || il2CppObject.Pointer == IntPtr.Zero)
                return component.GetType().FullName ?? component.GetType().Name;

            var klass = IL2CPP.il2cpp_object_get_class(il2CppObject.Pointer);
            if (klass == IntPtr.Zero)
                return component.GetType().FullName ?? component.GetType().Name;

            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? string.Empty;
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return component.GetType().FullName ?? component.GetType().Name;

            return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
        }
        catch
        {
            return component.GetType().FullName ?? component.GetType().Name;
        }
    }

    private static Type? ResolveWrapperType(string nativeTypeName)
    {
        if (string.IsNullOrWhiteSpace(nativeTypeName)) return null;
        if (NativeWrapperTypeCache.TryGetValue(nativeTypeName, out var cached)) return cached;

        var managedName = nativeTypeName.Replace('/', '+');
        Type? resolved = null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                resolved = assembly.GetType(managedName, throwOnError: false);
                if (resolved != null) break;
            }
            catch { }
        }

        NativeWrapperTypeCache[nativeTypeName] = resolved;
        return resolved;
    }

    

    

    

    

    

    

    private static bool TryCharacterIdFromEntityName(string entityName, out int characterId)
    {
        characterId = 0;
        if (string.IsNullOrWhiteSpace(entityName)) return false;

        var digits = new string(entityName.Where(char.IsDigit).ToArray());
        if (digits.Length < 3) return false;

        var suffix = digits.Substring(digits.Length - 3);
        if (!int.TryParse(suffix, out characterId)) return false;

        if (!KnownCharacterIds.Contains(characterId))
        {
            characterId = 0;
            return false;
        }

        return true;
    }

    

    

    

    

    

    private static bool TryReadNativeFieldValue(
        IntPtr field,
        IntPtr objectPointer,
        IntPtr fieldType,
        int typeCode,
        out object? value)
    {
        value = null;

        try
        {
            var boxed = IL2CPP.il2cpp_field_get_value_object(field, objectPointer);
            if (boxed == IntPtr.Zero) return false;

            if (typeCode == 14) // string
            {
                value = IL2CPP.Il2CppStringToManaged(boxed);
                return value != null;
            }

            var data = IL2CPP.il2cpp_object_unbox(boxed);
            if (data == IntPtr.Zero) return false;

            switch (typeCode)
            {
                case 2: // bool
                    value = Marshal.ReadByte(data) != 0;
                    return true;
                case 3: // char
                    value = (char)(ushort)Marshal.ReadInt16(data);
                    return true;
                case 4: // i1
                    value = unchecked((sbyte)Marshal.ReadByte(data));
                    return true;
                case 5: // u1
                    value = Marshal.ReadByte(data);
                    return true;
                case 6: // i2
                    value = Marshal.ReadInt16(data);
                    return true;
                case 7: // u2
                    value = unchecked((ushort)Marshal.ReadInt16(data));
                    return true;
                case 8: // i4
                    value = Marshal.ReadInt32(data);
                    return true;
                case 9: // u4
                    value = unchecked((uint)Marshal.ReadInt32(data));
                    return true;
                case 10: // i8
                    value = Marshal.ReadInt64(data);
                    return true;
                case 11: // u8
                    value = unchecked((ulong)Marshal.ReadInt64(data));
                    return true;
                case 12: // r4
                    value = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data));
                    return true;
                case 13: // r8
                    value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data));
                    return true;
                case 17: // valuetype / enum
                {
                    var enumClass = IL2CPP.il2cpp_class_from_il2cpp_type(fieldType);
                    if (enumClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_enum(enumClass))
                        return false;
                    var baseType = IL2CPP.il2cpp_class_enum_basetype(enumClass);
                    if (baseType == IntPtr.Zero) return false;
                    var baseCode = IL2CPP.il2cpp_type_get_type(baseType);
                    return TryReadUnboxedPrimitive(data, baseCode, out value);
                }
                default:
                    return false;
            }
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static bool TryReadUnboxedPrimitive(IntPtr data, int typeCode, out object? value)
    {
        value = null;
        try
        {
            switch (typeCode)
            {
                case 2: value = Marshal.ReadByte(data) != 0; return true;
                case 3: value = (char)(ushort)Marshal.ReadInt16(data); return true;
                case 4: value = unchecked((sbyte)Marshal.ReadByte(data)); return true;
                case 5: value = Marshal.ReadByte(data); return true;
                case 6: value = Marshal.ReadInt16(data); return true;
                case 7: value = unchecked((ushort)Marshal.ReadInt16(data)); return true;
                case 8: value = Marshal.ReadInt32(data); return true;
                case 9: value = unchecked((uint)Marshal.ReadInt32(data)); return true;
                case 10: value = Marshal.ReadInt64(data); return true;
                case 11: value = unchecked((ulong)Marshal.ReadInt64(data)); return true;
                default: return false;
            }
        }
        catch
        {
            value = null;
            return false;
        }
    }

    

    

    

    

    

    

    

    

    

    

    

    

    

    

    

    private static string Best(List<ScoredValue> candidates)
    {
        if (candidates.Count == 0) return "후보 없음";
        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Value.Length)
            .First()
            .Value;
    }

    private static IEnumerable<object> FindAll(Type type)
    {
        var method = _findObjectsOfTypeAll;
        if (method == null) yield break;

        IEnumerable? values;
        try
        {
            values = method.MakeGenericMethod(type).Invoke(null, null) as IEnumerable;
        }
        catch
        {
            yield break;
        }

        if (values == null) yield break;

        foreach (var value in values)
        {
            if (value != null) yield return value;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            catch { }
        }

        return null;
    }

    private static bool IsActiveComponent(Component component)
    {
        try
        {
            return component != null
                && component.gameObject != null
                && component.gameObject.activeInHierarchy;
        }
        catch
        {
            return false;
        }
    }

    private static string HierarchyPath(Transform? transform)
    {
        if (transform == null) return "<no-transform>";

        var names = new List<string>();
        var current = transform;
        var guard = 0;

        while (current != null && guard++ < 64)
        {
            names.Add(SafeName(current.gameObject));
            try { current = current.parent; }
            catch { break; }
        }

        names.Reverse();
        return string.Join("/", names);
    }

    

    private static int SafeSiblingIndex(Transform transform)
    {
        try { return transform.GetSiblingIndex(); }
        catch { return -1; }
    }

    

    private static string SafeName(GameObject gameObject)
    {
        try { return gameObject.name ?? "<unnamed>"; }
        catch { return "<unavailable>"; }
    }

    private static bool IsPlausibleText(string value, int min, int max)
    {
        if (value.Length < min || value.Length > max) return false;
        if (value.Any(char.IsControl)) return false;
        return true;
    }

    

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    

    

    private readonly record struct ScoredValue(int Score, string Value);
    private readonly record struct FairyTextEntry(
        string Text,
        string Path,
        Transform Transform,
        Vector2 ScreenPoint);
    private readonly record struct RoomSlotInfo(
        MonoBehaviour Component,
        int SiblingIndex,
        Vector2 WorldPosition);
    private readonly record struct ChatButtonPlacement(
        bool Visible,
        Vector2 ScreenPoint,
        float ScreenSize);
}

internal sealed class ChatSnapshot
{
    public string ScreenPhase { get; set; } = "기타";
    public bool ChatAvailable { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string Nickname { get; set; } = "후보 없음";
    public string Character { get; set; } = "후보 없음";
    public string Order { get; set; } = "후보 없음";
    public bool ChatButtonVisible { get; set; }
    public Vector2 ChatButtonScreenPoint { get; set; }
    public float ChatButtonScreenSize { get; set; }
}

