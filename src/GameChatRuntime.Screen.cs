using System;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class GameChatRuntime
{
    private const float ButtonSize = 72f;
    private const float ButtonGap = 16f;
    private const float ExpressionWindowRescanSeconds = 0.5f;
    // Visible share of an expression column button's scaled bounds, measured in game.
    private const float ExpressionArtFill = 0.85f;
    // Expression column buttons, top to bottom in the PC layout.
    private static readonly string[] ExpressionColumn = { "btn_OpenExpr", "btn_OpenChat", "btn_MapTool", "btn_MapChat" };
    private static FguiNode? _expressionWindow;
    private static float _nextExpressionScanAt;
    private static string _loggedAnchorPhase = string.Empty;
    private static string _loggedAnchor = string.Empty;

    /// <summary>
    /// Reads the game's current UIManager panel every frame. The chat button is
    /// placed inside the panel's own FairyGUI component next to a native button,
    /// so it inherits the panel's scaling, relations, transitions and layer order.
    /// </summary>
    private static void ResolveChatScreen(FguiNode root)
    {
        var snapshot = _snapshot;
        string phase;
        string panelClass = string.Empty;
        IntPtr ui;
        try
        {
            var manager = FairyGui.ReadStaticObject(FairyGui.Class("UI", "UIManager"), "_inst");
            var panel = manager == IntPtr.Zero ? IntPtr.Zero : FairyGui.Call(manager, "get_currentPanel");
            ui = panel == IntPtr.Zero ? IntPtr.Zero : FairyGui.Call(panel, "get_UIObject");
            panelClass = FairyGui.ClassName(panel);
            phase = ui != IntPtr.Zero && FairyGui.Get<bool>(ui, "get_onStage")
                ? ScreenPhase(panelClass)
                : "기타";
        }
        catch (Exception ex)
        {
            LogRuntimeWarning("screen", "Chat screen read failed: " + ex.GetType().Name + ": " + ex.Message);
            ui = IntPtr.Zero;
            phase = "기타";
        }

        // BattleLogic opens HandCardPanel right after BattleInfoPanel, so the
        // current panel during a match is not BattleInfoPanel. The play anchor
        // does not use the panel, so any panel counts while the game data says
        // a match is running.
        if (phase == "기타" && _gameState.Available && _gameState.Phase == "플레이")
            phase = "플레이";

        if (!string.Equals(phase, snapshot.ScreenPhase, StringComparison.Ordinal))
            _log?.LogInfo("Chat screen: " + phase + " (" + (panelClass.Length == 0 ? "no panel" : panelClass)
                + "); session phase: " + Session.Phase + ".");
        snapshot.ScreenPhase = phase;
        snapshot.ChatAvailableOnScreen = phase != "기타";
        snapshot.ChatButtonVisible = snapshot.ChatAvailableOnScreen && phase != "플레이";
        if (!snapshot.ChatAvailableOnScreen)
        {
            snapshot.ChatButtonHost = null;
            return;
        }

        string anchor;
        try
        {
            anchor = phase switch
            {
                "방" => BesideRoomStart(ui, snapshot),
                "캐릭터 선택" => BesideHeroConfirm(ui, snapshot),
                _ => BelowExpressionColumn(root, snapshot)
            };
        }
        catch (Exception ex)
        {
            snapshot.ChatButtonHost = null;
            LogRuntimeWarning("anchor", "Chat button anchor read failed: " + ex.GetType().Name + ": " + ex.Message);
            anchor = string.Empty;
        }

        if (anchor.Length == 0 && snapshot.ChatButtonVisible)
        {
            // Preserve the established fallback for changed button names. In
            // play it is allowed only while the native column is on stage.
            snapshot.ChatButtonHost = root;
            var width = root.Width;
            var height = root.Height;
            var center = phase switch
            {
                "방" => new Vector2(width * 0.902f, height * 0.855f),
                "캐릭터 선택" => new Vector2(width * 0.953f, height * 0.725f),
                _ => new Vector2(width * 0.982f, height * 0.300f)
            };
            snapshot.ChatButtonPosition = center - new Vector2(ButtonSize, ButtonSize) * 0.5f;
            snapshot.ChatButtonSize = ButtonSize;
            anchor = "screen ratio";
        }
        if (!snapshot.ChatButtonVisible)
        {
            snapshot.ChatButtonHost = null;
            anchor = "hidden expression column";
        }

        if (!string.Equals(phase, _loggedAnchorPhase, StringComparison.Ordinal)
            || !string.Equals(anchor, _loggedAnchor, StringComparison.Ordinal))
        {
            _loggedAnchorPhase = phase;
            _loggedAnchor = anchor;
            _log?.LogInfo("Chat button anchor: " + phase + " -> " + anchor + ".");
        }
    }

    private static string ScreenPhase(string panelClass) => panelClass switch
    {
        "RoomWaitPanel" => "방",
        "RoomHeroPanel" => "캐릭터 선택",
        "BattleInfoPanel" or "HandCardPanel" or "BattleSettlementPanel" => "플레이",
        _ => "기타"
    };

    /// <summary>
    /// Room: left of Start. Ready shares Start's row, and the host's
    /// kick countdown appears on Start's right side.
    /// </summary>
    private static string BesideRoomStart(IntPtr panel, ChatSnapshot snapshot)
    {
        var start = Child(panel, "btn_Start");
        if (start == IntPtr.Zero)
            return string.Empty;
        var bounds = Bounds(start);
        Place(snapshot, panel, new Vector2(bounds.xMin - ButtonGap - ButtonSize,
            bounds.center.y - ButtonSize * 0.5f), ButtonSize);
        return "btn_Start";
    }

    /// <summary>
    /// Character selection: the confirm button's upper right, where the
    /// previous screen-ratio placement sat on 16:9 screens.
    /// </summary>
    private static string BesideHeroConfirm(IntPtr panel, ChatSnapshot snapshot)
    {
        var confirm = Child(panel, "btn_SureHero");
        if (confirm == IntPtr.Zero)
            return string.Empty;
        var bounds = Bounds(confirm);
        Place(snapshot, panel, new Vector2(bounds.xMin + 154f, bounds.yMin - 18f), ButtonSize);
        return "btn_SureHero";
    }

    /// <summary>
    /// Battle: the next slot of the expression window's PC button column,
    /// spaced like the column's last two visible buttons.
    /// </summary>
    private static string BelowExpressionColumn(FguiNode root, ChatSnapshot snapshot)
    {
        var window = FindExpressionWindow(root);
        var pane = window == IntPtr.Zero ? IntPtr.Zero : FairyGui.Call(window, "get_contentPane");
        var column = pane == IntPtr.Zero ? IntPtr.Zero : Child(pane, "com_Interact_PC");
        if (column == IntPtr.Zero || !FairyGui.Get<bool>(column, "get_visible")
            || !FairyGui.Get<bool>(column, "get_onStage"))
            return string.Empty;
        snapshot.ChatButtonVisible = true;

        Rect previous = default, last = default;
        var count = 0;
        foreach (var name in ExpressionColumn)
        {
            var button = Child(column, name);
            if (button == IntPtr.Zero || !FairyGui.Get<bool>(button, "get_visible"))
                continue;
            var bounds = Bounds(button);
            if (count > 0 && bounds.yMin < last.yMin)
                return string.Empty;
            previous = last;
            last = bounds;
            count++;
        }
        var step = last.center.y - previous.center.y;
        if (count < 2 || Math.Abs(last.center.x - previous.center.x) > 4f
            || step < last.height * 0.5f || step > last.height * 3f)
            return string.Empty;

        // The native button art has transparent margins inside its scaled
        // bounds; matching the visible art keeps the chat button the same size.
        var size = Math.Min(last.width, last.height) * ExpressionArtFill;
        var center = new Vector2(last.center.x, last.center.y + step);
        Place(snapshot, column, center - new Vector2(size, size) * 0.5f, size);
        return "expression column";
    }

    /// <summary>ExpressionWindow is a FairyGUI Window shown as a GRoot child during battle.</summary>
    private static IntPtr FindExpressionWindow(FguiNode root)
    {
        // The cached window is retained by a GC handle, so its pointer stays
        // valid after the game drops it; leaving GRoot invalidates the cache.
        if (_expressionWindow != null)
        {
            if (FguiNode.ParentOf(_expressionWindow.Pointer) == root.Pointer)
                return _expressionWindow.Pointer;
            ReleaseExpressionWindow();
            _nextExpressionScanAt = 0f;
        }
        if (Time.unscaledTime < _nextExpressionScanAt)
            return IntPtr.Zero;
        _nextExpressionScanAt = Time.unscaledTime + ExpressionWindowRescanSeconds;
        var count = FairyGui.Get<int>(root.Pointer, "get_numChildren");
        for (var index = 0; index < count; index++)
        {
            var child = FairyGui.Call(root.Pointer, "GetChildAt", FairyGui.Int, index);
            if (FairyGui.ClassName(child) != "ExpressionWindow")
                continue;
            _expressionWindow = FguiNode.Retain(child);
            return child;
        }
        return IntPtr.Zero;
    }

    private static void ReleaseExpressionWindow()
    {
        _expressionWindow?.ReleaseHandle();
        _expressionWindow = null;
    }

    private static void Place(ChatSnapshot snapshot, IntPtr host, Vector2 position, float size)
    {
        // Compare only after the current frame resolves the native host.
        if (snapshot.ChatButtonHost == null || snapshot.ChatButtonHost.Pointer != host)
            snapshot.ChatButtonHost = FguiNode.Borrow(host);
        snapshot.ChatButtonPosition = position;
        snapshot.ChatButtonSize = size;
    }

    private static IntPtr Child(IntPtr component, string name) =>
        FairyGui.CallObject(component, "GetChild", FairyGui.String, FairyGui.InternString(name));

    /// <summary>
    /// Rendered rectangle in the parent's coordinates. FairyGUI scales around
    /// the pivot, so a scaled object's art does not start at xMin/yMin
    /// (the expression column buttons are 124x124 at scale 0.65, pivot 1,0.5).
    /// </summary>
    private static Rect Bounds(IntPtr gObject)
    {
        var width = FairyGui.Get<float>(gObject, "get_width");
        var height = FairyGui.Get<float>(gObject, "get_height");
        var scaleX = FairyGui.Get<float>(gObject, "get_scaleX");
        var scaleY = FairyGui.Get<float>(gObject, "get_scaleY");
        return new Rect(
            FairyGui.Get<float>(gObject, "get_xMin") + width * FairyGui.Get<float>(gObject, "get_pivotX") * (1f - scaleX),
            FairyGui.Get<float>(gObject, "get_yMin") + height * FairyGui.Get<float>(gObject, "get_pivotY") * (1f - scaleY),
            width * scaleX,
            height * scaleY);
    }
}
