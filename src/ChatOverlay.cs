using System;
using System.Collections.Generic;
using UnityEngine;

namespace AstralPartyChatPlugin;

/// <summary>
/// Chat UI drawn with the game's own FairyGUI. The button lives inside the
/// current game panel (see <see cref="ChatSnapshot.ChatButtonHost"/>); the window
/// is a top-level GRoot child, so FairyGUI's hit testing keeps clicks, wheel and
/// focus inside it away from the game UI underneath.
/// </summary>
internal static partial class ChatOverlay
{
    private const int WindowSortingOrder = 900000;
    private const float ButtonDesignSize = 72f;
    private const float HeaderButtonSize = 46f;
    private const float CreateRetrySeconds = 5f;

    private static readonly Color ButtonBorderColor = new Color32(137, 137, 137, 255);
    private static readonly Color ButtonFillColor = new Color32(34, 34, 34, 255);
    private static readonly Color ButtonIconColor = new Color32(155, 155, 155, 255);
    private static readonly Color AccentColor = new Color32(255, 0, 180, 255);
    private static readonly Color WindowColor = new Color32(28, 28, 28, 252);
    private static readonly Color WindowBorderColor = new Color32(120, 120, 120, 255);
    private static readonly Color HeaderColor = new Color32(38, 38, 38, 255);
    private static readonly Color HeaderButtonColor = new Color32(52, 52, 52, 255);
    private static readonly Color InputColor = new Color32(22, 22, 22, 255);
    private static readonly Color InputBorderColor = new Color32(92, 92, 92, 255);
    private static readonly Color MutedTextColor = new Color32(175, 175, 175, 255);
    private static readonly Color BodyTextColor = new Color32(235, 235, 235, 255);

    private static FguiNode? _chatButton;
    private static FguiNode? _buttonFill;
    private static FguiNode? _buttonIcon;
    private static FguiNode? _chatWindow;
    private static FguiNode? _windowBackground;
    private static FguiNode? _headerBackground;
    private static FguiNode? _chatStatusText;
    private static FguiNode? _closeButton;
    private static FguiNode? _closeBackground;
    private static FguiNode? _settingsButton;
    private static FguiNode? _settingsBackground;
    private static FguiNode? _chatViewport;
    private static FguiNode? _chatContent;
    private static FguiNode? _inputBackground;
    private static FguiNode? _chatInput;
    private static FguiNode? _sendButton;
    private static FguiNode? _resizeGrip;
    private static readonly List<FguiNode> OwnedNodes = new();
    private static readonly List<FguiNode> ButtonNodes = new();
    private static readonly List<FguiNode> _chatTexts = new();
    private static readonly Dictionary<IntPtr, ChatControl> Controls = new();
    private static ChatWindowRect _windowRect;
    private static ChatControl _hoveredControl;
    private static string _chatStatus = "연결 대기 중";
    private static float _nextCreateAttemptAt;

    private enum ChatControl
    {
        None,
        Button,
        Window,
        Header,
        Close,
        Settings,
        Viewport,
        ScrollTrack,
        Latest,
        Input,
        Send,
        Resize,
        SettingsPanel,
        TextSmaller,
        TextLarger,
        OpacityDown,
        OpacityUp,
        ResetLayout
    }

    private static bool IsCreated => _chatWindow != null;

    /// <summary>
    /// Tears down overlay-owned FairyGUI objects and cancels outstanding
    /// portrait requests. Game-owned objects are never disposed. Main thread only.
    /// </summary>
    public static void Shutdown()
    {
        ResetInputState();
        try { DisposeNodes(); }
        catch { }
        ShutdownAssets();
        ResetMessagesPresentation();
        _messages = new List<ChatUiMessage>();
        _chatStatus = "연결 대기 중";
        _nextCreateAttemptAt = 0f;
    }

    public static void Refresh(ChatSnapshot snapshot, FguiNode root)
    {
        // The game registers its fonts and panels before any chat screen
        // appears, so the UI is created on that first appearance.
        if (!snapshot.ChatButtonVisible && !IsCreated)
            return;
        if (!EnsureCreated(root))
            return;

        if (_messagesPresentationInvalid)
            RenderChatMessages(force: false);

        RefreshGameKoreanFont();
        ProcessCharacterImageDownloads();
        UpdateChatButton(snapshot, root);
        UpdateChatWindow(snapshot, root);
        RefreshRenderedPortraits(snapshot);
    }

    private static bool EnsureCreated(FguiNode root)
    {
        if (IsCreated && !_chatWindow!.IsDisposed)
        {
            if (!_chatWindow.IsChildOf(root))
                root.AddChild(_chatWindow);
            _chatWindow.SetSortingOrder(WindowSortingOrder);
            return true;
        }
        if (IsCreated)
        {
            // Another plugin or a game reset disposed the window; rebuild it.
            DisposeNodes();
            ResetInputState();
        }
        if (Time.unscaledTime < _nextCreateAttemptAt)
            return false;

        try
        {
            EnsureAssetsRunning();
            LoadSettings();
            RefreshChatFont();
            CreateChatButton();
            CreateChatWindow(root);
            return true;
        }
        catch
        {
            // Never keep a half-built UI; the caller logs the failure.
            _nextCreateAttemptAt = Time.unscaledTime + CreateRetrySeconds;
            try { DisposeNodes(); }
            catch { }
            throw;
        }
    }

    private static FguiNode Track(FguiNode node, List<FguiNode>? owner)
    {
        (owner ?? OwnedNodes).Add(node);
        return node;
    }

    private static FguiNode AddComponent(FguiNode parent, float x, float y, float width, float height,
        List<FguiNode>? owner = null)
    {
        var node = Track(FguiNode.Component(width, height), owner);
        parent.AddChild(node);
        node.SetXY(x, y);
        return node;
    }

    private static FguiNode AddGraph(FguiNode parent, float x, float y, float width, float height,
        Color fill, float radius = 0f, float lineSize = 0f, Color lineColor = default, List<FguiNode>? owner = null)
    {
        var node = Track(FguiNode.Graph(width, height), owner);
        parent.AddChild(node);
        node.SetXY(x, y);
        node.SetTouchable(false);
        node.Fill(fill, radius, lineSize, lineColor);
        return node;
    }

    private static FguiNode AddText(FguiNode parent, string value, int size, Color color,
        FguiAlign align, float x, float y, float width, float height,
        FguiVerticalAlign verticalAlign = FguiVerticalAlign.Middle, bool wrap = false, List<FguiNode>? owner = null)
    {
        var node = Track(FguiNode.Text(width, height), owner);
        parent.AddChild(node);
        node.SetTouchable(false);
        node.SetTextStyle(ChatFontName, size, color, align, verticalAlign, singleLine: !wrap,
            wrap ? FguiAutoSize.Height : FguiAutoSize.Shrink);
        node.SetXY(x, y);
        node.SetSize(width, height);
        node.SetText(value);
        _chatTexts.Add(node);
        return node;
    }

    private static FguiNode AddLoader(FguiNode parent, float x, float y, float size, List<FguiNode>? owner = null)
    {
        var node = Track(FguiNode.Loader(size, size), owner);
        parent.AddChild(node);
        node.SetTouchable(false);
        node.StretchContent();
        node.SetXY(x, y);
        return node;
    }

    private static FguiNode AddIcon(FguiNode parent, float x, float y, float size, Color color,
        List<FguiNode>? owner = null)
    {
        var node = AddLoader(parent, x, y, size, owner);
        node.SetTexture(GetChatIconTexture());
        node.SetColor(color);
        return node;
    }

    private static FguiNode AddButton(FguiNode parent, ChatControl control, float x, float y, float width, float height)
    {
        var button = AddComponent(parent, x, y, width, height);
        button.SetOpaque(true);
        Register(button, control);
        return button;
    }

    private static void Register(FguiNode node, ChatControl control) => Controls[node.Pointer] = control;

    private static void CreateChatButton()
    {
        _chatButton = Track(FguiNode.Component(ButtonDesignSize, ButtonDesignSize), ButtonNodes);
        _chatButton.SetOpaque(true);
        Register(_chatButton, ChatControl.Button);
        AddGraph(_chatButton, 0f, 0f, ButtonDesignSize, ButtonDesignSize, ButtonBorderColor, 10f, owner: ButtonNodes);
        _buttonFill = AddGraph(_chatButton, 4f, 4f, 64f, 64f, ButtonFillColor, 7f, owner: ButtonNodes);
        _buttonIcon = AddIcon(_chatButton, 17f, 17f, 38f, ButtonIconColor, ButtonNodes);
        _chatButton.SetVisible(false);
    }

    private static void CreateChatWindow(FguiNode root)
    {
        var window = _chatWindow = Track(FguiNode.Component(ChatLayout.DefaultWidth, ChatLayout.DefaultHeight), null);
        root.AddChild(window);
        window.SetSortingOrder(WindowSortingOrder);
        window.SetOpaque(true);
        // Hide first so a partial construction failure can never leak onto the screen.
        window.SetVisible(false);
        Register(window, ChatControl.Window);

        _windowBackground = AddGraph(window, 0f, 0f, 1f, 1f, WindowColor);
        _headerBackground = AddGraph(window, 12f, 12f, 1f, 82f, HeaderColor);
        _headerBackground.SetTouchable(true);
        Register(_headerBackground, ChatControl.Header);
        AddIcon(window, 28f, 34f, 34f, Color.white);
        AddText(window, "채팅", 24, Color.white, FguiAlign.Left, 76f, 18f, 300f, 44f);
        _chatStatusText = AddText(window, _chatStatus, 17, MutedTextColor, FguiAlign.Left, 76f, 58f, 1f, 26f);

        (_settingsButton, _settingsBackground) = CreateHeaderButton(window, ChatControl.Settings);
        for (var line = 0; line < 3; line++)
            AddGraph(_settingsButton, 13f, 14f + line * 8f, 20f, 3f, Color.white, 1.5f);
        (_closeButton, _closeBackground) = CreateHeaderButton(window, ChatControl.Close);
        foreach (var angle in new[] { 45f, -45f })
        {
            var stroke = AddGraph(_closeButton, 13f, 21.6f, 20f, 2.8f, Color.white, 1.4f);
            stroke.SetCenterPivot();
            stroke.SetXY(23f, 23f);
            stroke.SetRotation(angle);
        }

        // Rows are clipped by the viewport and scrolled by moving the content.
        _chatViewport = AddButton(window, ChatControl.Viewport, ChatLayout.SidePadding, ChatLayout.MessagesTop, 1f, 1f);
        _chatViewport.ClipChildren();
        _chatContent = AddComponent(_chatViewport, 0f, 0f, 1f, 1f);
        _chatContent.SetTouchable(false);

        CreateChatScrollControls(window);

        _inputBackground = AddGraph(window, ChatLayout.SidePadding, 0f, 1f, ChatLayout.InputHeight,
            InputColor, 12f, 3f, InputBorderColor);
        var input = _chatInput = Track(FguiNode.Input(1f, ChatLayout.InputHeight), null);
        window.AddChild(input);
        input.SetTextStyle(ChatFontName, 21, BodyTextColor, FguiAlign.Left, FguiVerticalAlign.Middle,
            singleLine: true, FguiAutoSize.None);
        input.SetPrompt("[color=#7D7D7D]메시지를 입력하세요...[/color]");
        input.SetMaxLength(PartyProtocol.MaxTextLength * 2);
        _chatTexts.Add(input);
        Register(input, ChatControl.Input);

        _sendButton = AddButton(window, ChatControl.Send, 0f, 0f, ChatLayout.SendWidth, ChatLayout.InputHeight);
        AddGraph(_sendButton, 0f, 0f, ChatLayout.SendWidth, ChatLayout.InputHeight,
            AccentColor, 12f, 3f, ButtonBorderColor);
        AddText(_sendButton, "전송", 21, Color.white, FguiAlign.Center, 0f, 0f, ChatLayout.SendWidth, ChatLayout.InputHeight);

        _resizeGrip = AddButton(window, ChatControl.Resize, 0f, 0f, 22f, 22f);
        foreach (var (dx, dy) in new[] { (14f, 14f), (8f, 14f), (14f, 8f), (2f, 14f), (8f, 8f), (14f, 2f) })
            AddGraph(_resizeGrip, dx, dy, 4f, 4f, ButtonBorderColor);

        CreateSettingsPanel(window);
        _windowRect = default;
        ApplyWindowLayout(root, force: true);
        ApplyWindowOpacity();
        RenderChatMessages(force: true);
    }

    private static (FguiNode Button, FguiNode Background) CreateHeaderButton(FguiNode window, ChatControl control)
    {
        var button = AddButton(window, control, 0f, 0f, HeaderButtonSize, HeaderButtonSize);
        var background = AddGraph(button, 0f, 0f, HeaderButtonSize, HeaderButtonSize, HeaderButtonColor, 10f);
        return (button, background);
    }

    /// <summary>Applies the persisted geometry; returns whether the message width changed.</summary>
    private static bool ApplyWindowLayout(FguiNode root, bool force)
    {
        if (_chatWindow == null)
            return false;
        var rect = ChatLayout.Place(_windowGeometry, root.Width, root.Height);
        if (!force && rect == _windowRect)
            return false;
        var resized = force || rect.Width != _windowRect.Width || rect.Height != _windowRect.Height;
        var widthChanged = force || rect.Width != _windowRect.Width;
        _windowRect = rect;
        _chatWindow.SetXY(rect.X, rect.Y);
        if (!resized)
            return false;

        var width = rect.Width;
        var height = rect.Height;
        _chatWindow.SetSize(width, height);
        _windowBackground?.SetSize(width, height);
        _headerBackground?.SetSize(width - 24f, 82f);
        _chatStatusText?.SetSize(Math.Max(1f, width - 76f - HeaderButtonSize * 2f - 40f), 26f);
        _settingsButton?.SetXY(width - 42f - 54f - HeaderButtonSize * 0.5f, 53f - HeaderButtonSize * 0.5f);
        _closeButton?.SetXY(width - 42f - HeaderButtonSize * 0.5f, 53f - HeaderButtonSize * 0.5f);

        var viewportHeight = ChatLayout.ViewportHeight(height);
        _chatViewport?.SetSize(ChatLayout.ViewportWidth(width), viewportHeight);

        var footerY = height - 82f;
        var inputWidth = width - ChatLayout.SidePadding * 2f - ChatLayout.SendWidth - 12f;
        _inputBackground?.SetXY(ChatLayout.SidePadding, footerY);
        _inputBackground?.SetSize(inputWidth, ChatLayout.InputHeight);
        _chatInput?.SetXY(ChatLayout.SidePadding + 16f, footerY);
        _chatInput?.SetSize(inputWidth - 32f, ChatLayout.InputHeight);
        _sendButton?.SetXY(width - ChatLayout.SidePadding - ChatLayout.SendWidth, footerY);
        _resizeGrip?.SetXY(width - 22f, height - 22f);

        LayoutChatScrollControls(width, viewportHeight);
        LayoutSettingsPanel(width, viewportHeight);
        return widthChanged;
    }

    private static void ApplyWindowOpacity()
    {
        _windowBackground?.Fill(WithAlpha(WindowColor, _windowOpacity), 18f, 3f,
            WithAlpha(WindowBorderColor, _windowOpacity));
        _headerBackground?.Fill(WithAlpha(HeaderColor, _windowOpacity), 12f);
    }

    private static Color WithAlpha(Color color, float opacity) =>
        new(color.r, color.g, color.b, color.a * opacity);

    private static void UpdateChatButton(ChatSnapshot snapshot, FguiNode root)
    {
        var button = _chatButton;
        if (button == null)
            return;
        if (button.IsDisposed)
        {
            // The game disposed the panel that hosted the button; rebuild it.
            ReleaseTrackedNodes(ButtonNodes);
            _chatButton = _buttonFill = _buttonIcon = null;
            if (_hoveredControl == ChatControl.Button)
                _hoveredControl = ChatControl.None;
            CreateChatButton();
            button = _chatButton!;
        }

        var host = snapshot.ChatButtonHost;
        if (!snapshot.ChatButtonVisible || host == null)
        {
            button.SetVisible(false);
            return;
        }

        if (!button.IsChildOf(host))
            host.AddChild(button);
        // Inside a game panel the button keeps the panel's own order; on GRoot
        // it must stay above the panels it is laid over.
        button.SetSortingOrder(host.Pointer == root.Pointer ? WindowSortingOrder - 1 : 0);
        var size = snapshot.ChatButtonSize > 1f ? snapshot.ChatButtonSize : ButtonDesignSize;
        button.SetScale(size / ButtonDesignSize);
        button.SetXY(snapshot.ChatButtonPosition.x, snapshot.ChatButtonPosition.y);
        button.SetVisible(true);
    }

    private static void ApplyHover(ChatControl next)
    {
        if (_hoveredControl == next)
            return;
        var previous = _hoveredControl;
        _hoveredControl = next;
        SetControlHover(previous, false);
        SetControlHover(next, true);
    }

    private static void SetControlHover(ChatControl control, bool hovered)
    {
        switch (control)
        {
            case ChatControl.Button:
                _buttonFill?.Fill(hovered ? AccentColor : ButtonFillColor, 7f);
                _buttonIcon?.SetColor(hovered ? Color.white : ButtonIconColor);
                break;
            case ChatControl.Close:
                _closeBackground?.Fill(hovered ? AccentColor : HeaderButtonColor, 10f);
                break;
            case ChatControl.Settings:
                RefreshSettingsButton(hovered);
                break;
            case ChatControl.TextSmaller:
            case ChatControl.TextLarger:
            case ChatControl.OpacityDown:
            case ChatControl.OpacityUp:
            case ChatControl.ResetLayout:
                SetSettingsControlHover(control, hovered);
                break;
        }
    }

    private static void ReleaseTrackedNodes(List<FguiNode> nodes)
    {
        foreach (var node in nodes)
        {
            Controls.Remove(node.Pointer);
            _chatTexts.Remove(node);
            try { node.ReleaseHandle(); }
            catch { }
        }
        nodes.Clear();
    }

    private static void DisposeNodes()
    {
        try
        {
            // Native disposal reaches children, but their CLR wrappers still
            // own individual handles and must all be released separately.
            try { _chatWindow?.Dispose(); }
            catch { }
            try { _chatButton?.Dispose(); }
            catch { }
        }
        finally
        {
            foreach (var row in RenderedChatRows)
                row.ReleaseHandles();
            RenderedChatRows.Clear();
            ReleaseTrackedNodes(OwnedNodes);
            ReleaseTrackedNodes(ButtonNodes);
            _chatTexts.Clear();
            Controls.Clear();
            ResetSettingsPanel();
            ResetChatScrollControls();
            _chatButton = _buttonFill = _buttonIcon = null;
            _chatWindow = _windowBackground = _headerBackground = _chatStatusText = null;
            _closeButton = _closeBackground = _settingsButton = _settingsBackground = null;
            _chatViewport = _chatContent = null;
            _inputBackground = _chatInput = _sendButton = _resizeGrip = null;
            _windowRect = default;
            _hoveredControl = ChatControl.None;
        }
    }
}
