using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class GameChatRuntime
{
    private const float ScanIntervalSeconds = 2.0f;
    private const float GameDataIntervalSeconds = 0.25f;
    private static readonly object Sync = new object();

    private static ManualLogSource? _log;
    private static MethodInfo? _findObjectsOfTypeAll;
    private static readonly Dictionary<string, float> WarningNextAt =
        new Dictionary<string, float>(StringComparer.Ordinal);
    private static float _nextScanAt;
    private static bool _initialized;
    private static bool _scanning;
    private static ChatSnapshot _snapshot = new ChatSnapshot();
    private static readonly GameSessionState Session = new GameSessionState();
    private static Camera[] _scanCameras = Array.Empty<Camera>();
    private static readonly NativeGameData NativeData = new();
    private static readonly GameStateReader StateReader = new(NativeData);
    private static ChatGameState _gameState = GameStateReader.Pending();
    private static float _nextGameDataAt;

    public static ChatGameState GetChatGameState() => _gameState;

    private static void ReadGameState()
    {
        _nextGameDataAt = Time.unscaledTime + GameDataIntervalSeconds;
        ChatGameState state;
        try { state = StateReader.Read(); }
        catch (Exception ex)
        {
            state = GameStateReader.Pending();
            LogRuntimeWarning("game-data", "Game data read failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        var previous = _gameState;
        _gameState = state;
        var phase = state.Available ? state.Phase : "기타";
        Session.ObservePhase(phase);
        Session.ObserveRoomId(state.Available ? state.RoomId : string.Empty);
        Session.SetOrder(state.Order);
        Session.SetCharacter(state.CharacterId);
        _snapshot.ChatAvailable = state.Available;
        _snapshot.RoomId = state.RoomId;
        if (previous.InformationReady != state.InformationReady || previous.Available != state.Available
            || previous.Phase != state.Phase)
            _log?.LogInfo("Game data: " + (state.InformationReady ? (state.Available ? state.Phase : "outside room") : "pending") + ".");
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

            if (Time.unscaledTime >= _nextGameDataAt)
                ReadGameState();

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
            next.ChatAvailable = _gameState.Available;
            if (next.ScreenPhase != _snapshot.ScreenPhase)
                _log?.LogInfo("Chat screen: " + next.ScreenPhase + "; session phase: " + Session.Phase + ".");

            var placement = ResolveChatButtonPlacement(
                next.ScreenPhase,
                components);
            next.ChatButtonVisible = placement.Visible;
            next.ChatButtonScreenPoint = placement.ScreenPoint;
            next.ChatButtonScreenSize = placement.ScreenSize;

            next.RoomId = Session.RoomId;
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

    private static string SafeName(GameObject gameObject)
    {
        try { return gameObject.name ?? "<unnamed>"; }
        catch { return "<unavailable>"; }
    }

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
    public bool ChatButtonVisible { get; set; }
    public Vector2 ChatButtonScreenPoint { get; set; }
    public float ChatButtonScreenSize { get; set; }
}

