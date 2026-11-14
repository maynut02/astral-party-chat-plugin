using System;
using System.Collections.Generic;
using UnityEngine;

namespace AstralPartyChatPlugin;

/// <summary>
/// Top-left FairyGUI panel for plugin update results. It exists only while
/// there is something to tell the player, so an up-to-date or offline start
/// creates no UI and makes no native call here.
/// </summary>
internal static partial class UpdateNotice
{
    private const float Width = 500f;
    private const float Margin = 32f;
    private const float Padding = 24f;
    private const float TitleTop = 14f;
    private const float TitleHeight = 34f;
    private const float MessageTop = 54f;
    private const float MessageLineHeight = 26f;
    private const float CloseSize = 40f;
    private const float RestartWidth = 190f;
    private const float RestartHeight = 42f;
    // Above the chat window (900000) so a dragged window never covers the restart button.
    private const int SortingOrder = 905000;
    private const int MaximumTargetDepth = 16;
    // The notice can be due before the game finishes starting FairyGUI, so a
    // failure is retried a few times instead of losing the notice for the session.
    private const int MaximumFailures = 3;
    private const float RetrySeconds = 5f;
    private const string FontAlias = "AstralPartyChatNotice";

    private static readonly Color PanelColor = new Color32(28, 28, 28, 252);
    private static readonly Color AccentColor = new Color32(255, 0, 180, 255);
    private static readonly Color CloseColor = new Color32(52, 52, 52, 255);
    private static readonly Color SuccessColor = new Color32(120, 230, 150, 255);
    private static readonly Color WarningColor = new Color32(255, 190, 80, 255);

    private static readonly List<FguiNode> Nodes = new();
    private static FguiNode? _panel;
    private static FguiNode? _closeButton;
    private static FguiNode? _restartButton;
    private static int _shownRevision = -1;
    private static int _dismissedRevision = -1;
    private static int _fontVersion;
    private static bool _retiring;
    private static int _failures;
    private static float _retryAt;

    /// <summary>Main thread, once per frame. <paramref name="revision"/> identifies <paramref name="state"/>.</summary>
    public static void Tick(int revision, PluginUpdateState state, Action restart, Action<string> warn)
    {
        if (_failures > 0 && (_failures >= MaximumFailures || Time.unscaledTime < _retryAt))
            return;
        try
        {
            if (_retiring)
            {
                // Hidden on the previous frame. FairyGUI caches each frame's hit
                // target and game code reads it; disposing that target in the
                // same frame would hand the game a dead object.
                _retiring = false;
                DisposeNodes();
            }

            var wanted = state.Message != null && revision != _dismissedRevision;
            if (!wanted && _panel == null)
                return;
            var root = FguiNode.Root;
            if (root == null)
                return;

            if (_panel != null)
            {
                if (_panel.IsDisposed)
                    DisposeNodes(); // The game tore the tree down; only wrappers remain.
                else if (!wanted || revision != _shownRevision || !_panel.IsChildOf(root) || FontLost())
                {
                    _panel.SetVisible(false);
                    _retiring = true;
                    return;
                }
            }
            if (!wanted)
                return;
            if (_panel == null)
            {
                Create(root, state);
                _shownRevision = revision;
            }

            if (!Input.GetMouseButtonDown(0))
                return;
            var target = FguiNode.TouchTarget(root);
            for (var depth = 0; target != IntPtr.Zero && depth < MaximumTargetDepth; depth++)
            {
                if (target == _closeButton!.Pointer)
                {
                    // The same notice is not reopened; a later result is a new revision.
                    _dismissedRevision = revision;
                    _panel!.SetVisible(false);
                    _retiring = true;
                    return;
                }
                if (_restartButton != null && target == _restartButton.Pointer)
                {
                    restart();
                    return;
                }
                if (target == root.Pointer || target == _panel!.Pointer)
                    return;
                target = FguiNode.ParentOf(target);
            }
        }
        catch (Exception ex)
        {
            // The update itself is unaffected; it applies on the next game start.
            _failures++;
            _retryAt = Time.unscaledTime + RetrySeconds;
            warn((_failures < MaximumFailures ? "Update notice failed, retrying: " : "Update notice disabled: ")
                + ex.GetType().Name + ": " + ex.Message);
            try { DisposeNodes(); }
            catch { }
        }
    }

    /// <summary>Releases the panel, its font registration and font asset. Main thread only.</summary>
    public static void Shutdown()
    {
        try { DisposeNodes(); }
        catch { }
        try { FguiNode.ReleaseFont(FontAlias); }
        catch { }
        ReleaseFontAsset();
        _shownRevision = _dismissedRevision = -1;
        _fontVersion = 0;
        _retiring = false;
        _failures = 0;
        _retryAt = 0f;
    }

    /// <summary>
    /// The game's FontManager.Clear() on game start disposes our font, and every
    /// text must then bind to a fresh registration. Other writes to the font
    /// table, such as another plugin registering its font, are not a reason to
    /// rebuild: two panels reacting to each other's writes would never settle.
    /// </summary>
    private static bool FontLost()
    {
        if (FguiNode.FontRegistryVersion == _fontVersion)
            return false;
        var lost = !FguiNode.OwnsFont(FontAlias);
        // Read again: asking about a dropped alias writes the table itself.
        _fontVersion = FguiNode.FontRegistryVersion;
        return lost;
    }

    private static void Create(FguiNode root, PluginUpdateState state)
    {
        FguiNode.RegisterFont(FontAlias, GetFont());
        _fontVersion = FguiNode.FontRegistryVersion;

        var panel = _panel = Track(FguiNode.Component(Width, 1f));
        // Hide first so a partial construction failure can never leak onto the screen.
        panel.SetVisible(false);
        panel.SetOpaque(true);
        root.AddChild(panel);
        panel.SetSortingOrder(SortingOrder);
        var background = AddGraph(panel, 0f, 0f, Width, 1f, PanelColor, 18f, 3f, AccentColor);

        AddText(panel, "채팅 플러그인 업데이트", 22, Color.white, FguiAlign.Left,
            Padding, TitleTop, Width - Padding * 2f - CloseSize, TitleHeight, wrap: false);
        var message = AddText(panel, state.Message ?? string.Empty, 18,
            state.Severity == UpdateSeverity.Warning ? WarningColor : SuccessColor, FguiAlign.Left,
            Padding, MessageTop, Width - Padding * 2f, MessageLineHeight, wrap: true);

        var close = _closeButton = Track(FguiNode.Component(CloseSize, CloseSize));
        panel.AddChild(close);
        close.SetXY(Width - CloseSize - 12f, 10f);
        close.SetOpaque(true);
        AddGraph(close, 0f, 0f, CloseSize, CloseSize, CloseColor, 10f);
        foreach (var angle in new[] { 45f, -45f })
        {
            var stroke = AddGraph(close, 0f, 0f, 18f, 2.6f, Color.white, 1.3f);
            stroke.SetCenterPivot();
            stroke.SetXY(CloseSize * 0.5f, CloseSize * 0.5f);
            stroke.SetRotation(angle);
        }

        // The wrapped message sizes itself; the panel follows its real height.
        var height = MessageTop + Math.Max(MessageLineHeight, message.Height) + 20f;
        if (state.RestartRequired)
        {
            var restart = _restartButton = Track(FguiNode.Component(RestartWidth, RestartHeight));
            panel.AddChild(restart);
            restart.SetXY(Width - Padding - RestartWidth, height - 6f);
            restart.SetOpaque(true);
            AddGraph(restart, 0f, 0f, RestartWidth, RestartHeight, AccentColor, 12f);
            AddText(restart, "지금 다시 시작", 19, Color.white, FguiAlign.Center,
                0f, 0f, RestartWidth, RestartHeight, wrap: false);
            height += RestartHeight + 12f;
        }
        panel.SetSize(Width, height);
        background.SetSize(Width, height);
        panel.SetXY(Margin, Margin);
        panel.SetVisible(true);
    }

    private static FguiNode Track(FguiNode node)
    {
        Nodes.Add(node);
        return node;
    }

    private static FguiNode AddGraph(FguiNode parent, float x, float y, float width, float height,
        Color fill, float radius, float lineSize = 0f, Color lineColor = default)
    {
        var node = Track(FguiNode.Graph(width, height));
        parent.AddChild(node);
        node.SetXY(x, y);
        node.SetTouchable(false);
        node.Fill(fill, radius, lineSize, lineColor);
        return node;
    }

    private static FguiNode AddText(FguiNode parent, string value, int size, Color color, FguiAlign align,
        float x, float y, float width, float height, bool wrap)
    {
        var node = Track(FguiNode.Text(width, height));
        parent.AddChild(node);
        node.SetTouchable(false);
        node.SetTextStyle(FontAlias, size, color, align, wrap ? FguiVerticalAlign.Top : FguiVerticalAlign.Middle,
            singleLine: !wrap, wrap ? FguiAutoSize.Height : FguiAutoSize.Shrink);
        node.SetXY(x, y);
        node.SetSize(width, height);
        node.SetText(value);
        return node;
    }

    private static void DisposeNodes()
    {
        try
        {
            // Native disposal reaches the children; their wrappers still hold handles.
            _panel?.Dispose();
        }
        finally
        {
            foreach (var node in Nodes)
            {
                try { node.ReleaseHandle(); }
                catch { }
            }
            Nodes.Clear();
            _panel = _closeButton = _restartButton = null;
        }
    }
}
