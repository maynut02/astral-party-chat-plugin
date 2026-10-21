using System;
using System.Collections.Generic;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    private const int MaximumControlDepth = 16;
    private static readonly Queue<string> OutgoingMessages = new();
    private static readonly ChatInputSubmitState InputSubmitState = new();
    private static bool _chatWindowOpen;
    private static bool _readingOverlayInput;
    private static bool _inputFocused;
    private static bool _focusInputNextFrame;
    private static int _lastEnterKeyFrame = int.MinValue;
    private static int _lastInputBlurFrame = int.MinValue;
    private static int _lastInputPollFrame = int.MinValue;
    private static string? _validatedInputText;
    private static bool _inputLengthExceeded;
    private static ChatWindowDrag _windowDrag;
    private static ChatWindowRect _windowDragStartRect;
    private static Vector2 _windowDragStartPointer;
    [ThreadStatic] private static int _chatInputReadDepth;

    private enum ChatWindowDrag { None, Move, Resize }

    public static void EnterChatInputRead() => _chatInputReadDepth++;

    public static void ExitChatInputRead() => _chatInputReadDepth = Math.Max(0, _chatInputReadDepth - 1);

    /// <summary>
    /// True while the chat input has FairyGUI focus. FairyGUI delivers keys to
    /// the focused input itself; this guard only masks the game's direct Unity
    /// Input polling. Focus is sampled once per frame by <see cref="Refresh"/>.
    /// </summary>
    public static bool ShouldBlockGameKeyboardInput() => !_readingOverlayInput
        && _chatInputReadDepth == 0
        && _chatWindowOpen
        && _inputFocused;

    /// <summary>
    /// Records Enter from FairyGUI's OnGUI key events. During IME composition
    /// Windows can report Enter only there, not through Input.GetKeyDown.
    /// </summary>
    public static void ObserveGuiKeyDown(KeyCode key)
    {
        if (!ShouldBlockGameKeyboardInput()
            || (key != KeyCode.Return && key != KeyCode.KeypadEnter))
            return;
        RequestEnter(Time.frameCount);
    }

    public static void SetChatStatus(string status)
    {
        var next = string.IsNullOrWhiteSpace(status) ? "연결 대기 중" : status.Trim();
        if (string.Equals(next, _chatStatus, StringComparison.Ordinal))
            return;
        _chatStatus = next;
        UpdateChatInputStatus(readText: true);
    }

    private static void UpdateChatInputStatus(bool readText = false)
    {
        if (_chatStatusText == null)
            return;

        // Validate only committed text. Keep the header free of a character
        // counter and never rewrite input text or IME composition.
        if (readText || _inputFocused)
        {
            var value = _chatInput?.GetText() ?? string.Empty;
            if (!string.Equals(value, _validatedInputText, StringComparison.Ordinal))
            {
                _inputLengthExceeded = PartyProtocol.TextLength(value.Trim()) > PartyProtocol.MaxTextLength;
                _validatedInputText = value;
            }
        }
        _chatStatusText.SetText(_inputLengthExceeded
            ? "입력 길이 초과: 내용을 줄여주세요."
            : _chatStatus);
    }

    public static bool TryDequeueOutgoing(out string text)
    {
        if (OutgoingMessages.Count > 0)
        {
            text = OutgoingMessages.Dequeue();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool ReadOverlayInput(Func<bool> read)
    {
        var wasReading = _readingOverlayInput;
        _readingOverlayInput = true;
        try { return read(); }
        finally { _readingOverlayInput = wasReading; }
    }

    /// <summary>Maps FairyGUI's touch target to the overlay control that owns it.</summary>
    private static ChatControl HitControl(FguiNode root)
    {
        var target = FguiNode.TouchTarget(root);
        for (var depth = 0; target != IntPtr.Zero && depth < MaximumControlDepth; depth++)
        {
            if (Controls.TryGetValue(target, out var control))
                return control;
            if (target == root.Pointer)
                break;
            target = FguiNode.ParentOf(target);
        }
        return ChatControl.None;
    }

    private static void UpdateChatWindow(ChatSnapshot snapshot, FguiNode root)
    {
        if (_chatWindow == null)
            return;

        if (!snapshot.ChatAvailableOnScreen && _chatWindowOpen)
            SetChatWindowOpen(false);
        SampleInputFocus();

        var hovered = HitControl(root);
        ApplyHover(hovered);
        var pressed = ReadOverlayInput(() => Input.GetMouseButtonDown(0));
        if (hovered is not (ChatControl.Input or ChatControl.Send)
            && (pressed || ReadOverlayInput(() => Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))))
            InputSubmitState.CancelEnter();
        if (pressed && hovered == ChatControl.Button && snapshot.ChatButtonVisible)
        {
            if (!_chatWindowOpen && ApplyWindowLayout(root, force: false))
                RenderChatMessages(force: false);
            SetChatWindowOpen(!_chatWindowOpen);
            return;
        }

        if (!_chatWindowOpen)
            return;
        UpdateChatInputStatus();

        if (ApplyWindowLayout(root, force: false))
            RenderChatMessages(force: false);

        var held = ReadOverlayInput(() => Input.GetMouseButton(0));
        var pointer = FguiNode.PointerInRoot(root, Input.mousePosition);
        var windowPointer = pointer - new Vector2(_windowRect.X, _windowRect.Y);
        if (pressed)
            HandleWindowPress(hovered, pointer, windowPointer, root);
        UpdateWindowDrag(held, pointer, root);
        UpdateChatScrollDrags(held, windowPointer);

        if (!_settingsOpen && hovered is ChatControl.Viewport or ChatControl.ScrollTrack or ChatControl.Latest)
            ScrollChatByWheel(Input.mouseScrollDelta.y);

        PollChatInputSubmit();
    }

    private static void HandleWindowPress(ChatControl control, Vector2 pointer, Vector2 windowPointer, FguiNode root)
    {
        switch (control)
        {
            case ChatControl.Close:
                SetChatWindowOpen(false);
                break;
            case ChatControl.Settings:
                SetSettingsOpen(!_settingsOpen);
                break;
            case ChatControl.Header:
                BeginWindowDrag(ChatWindowDrag.Move, pointer);
                break;
            case ChatControl.Resize:
                BeginWindowDrag(ChatWindowDrag.Resize, pointer);
                break;
            case ChatControl.Latest:
                ScrollChatToLatest();
                break;
            case ChatControl.ScrollTrack:
                BeginChatScrollbarPress(windowPointer.y - ChatLayout.MessagesTop);
                break;
            case ChatControl.Viewport:
                BeginChatViewportDrag(windowPointer.y);
                break;
            case ChatControl.Send:
                RequestExplicitInputSubmit();
                break;
            case ChatControl.TextSmaller:
            case ChatControl.TextLarger:
            case ChatControl.OpacityDown:
            case ChatControl.OpacityUp:
            case ChatControl.ResetLayout:
                HandleSettingsControl(control, root);
                break;
        }
    }

    private static void BeginWindowDrag(ChatWindowDrag drag, Vector2 pointer)
    {
        CancelChatScrollDrags();
        _windowDrag = drag;
        _windowDragStartRect = _windowRect;
        _windowDragStartPointer = pointer;
    }

    private static void UpdateWindowDrag(bool held, Vector2 pointer, FguiNode root)
    {
        if (_windowDrag == ChatWindowDrag.None)
            return;

        var delta = pointer - _windowDragStartPointer;
        var rootWidth = root.Width;
        var rootHeight = root.Height;
        var resizing = _windowDrag == ChatWindowDrag.Resize;
        _windowGeometry = resizing
            ? ChatLayout.Resize(_windowDragStartRect, delta.x, delta.y, rootWidth, rootHeight)
            : ChatLayout.Move(_windowDragStartRect, delta.x, delta.y, rootWidth, rootHeight);
        ApplyWindowLayout(root, force: false);
        if (held)
            return;

        // Rows are re-measured once on release; a live resize only clips them.
        _windowDrag = ChatWindowDrag.None;
        SaveWindowGeometry();
        if (resizing)
            RenderChatMessages(force: false);
    }

    private static void RequestEnter(int frame)
    {
        _lastEnterKeyFrame = frame;
        InputSubmitState.RequestEnter(frame, !string.IsNullOrEmpty(Input.compositionString));
    }

    private static void RequestExplicitInputSubmit()
    {
        if (_chatInput == null)
            return;

        InputSubmitState.RequestExplicitSend(Time.frameCount);
    }

    private static void PollChatInputSubmit()
    {
        var frame = Time.frameCount;
        if (_lastInputPollFrame == frame)
            return;
        _lastInputPollFrame = frame;

        if (_focusInputNextFrame)
        {
            _focusInputNextFrame = false;
            FocusChatInput();
        }

        var composing = !string.IsNullOrEmpty(Input.compositionString);
        InputSubmitState.ObserveFrame(frame, composing);
        bool enterPressed;
        EnterChatInputRead();
        try { enterPressed = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter); }
        finally { ExitChatInputRead(); }
        if (enterPressed && _inputFocused)
            RequestEnter(frame);

        // Attribute a blur to Enter once, at the focus transition. Its pending
        // IME commit may take arbitrarily many unfocused frames to settle.
        if (_lastInputBlurFrame == frame
            && (_lastEnterKeyFrame == int.MinValue || (long)frame - _lastEnterKeyFrame > 1))
            InputSubmitState.CancelEnter();

        if (InputSubmitState.TryTakeReady(frame, composing, out _))
            QueueCurrentInput();
    }

    private static void QueueCurrentInput()
    {
        if (_chatInput == null)
            return;

        var value = _chatInput.GetText().Trim();
        if (value.Length > 0 && PartyProtocol.TextLength(value) <= PartyProtocol.MaxTextLength)
        {
            OutgoingMessages.Enqueue(value);
            _chatInput.SetText(string.Empty);
        }

        // Enter and the send button both leave the input ready for the next line.
        FocusChatInput();
        UpdateChatInputStatus(readText: true);
    }

    private static void FocusChatInput()
    {
        if (_chatInput == null || !_chatWindowOpen)
            return;
        _chatInput.Focus();
        _inputFocused = _chatInput.Focused;
    }

    private static void SetChatWindowOpen(bool open)
    {
        _chatWindowOpen = open;
        _chatWindow?.SetVisible(open);
        InputSubmitState.Reset();
        _lastInputPollFrame = int.MinValue;
        _lastEnterKeyFrame = int.MinValue;
        _lastInputBlurFrame = int.MinValue;
        if (open)
        {
            UpdateChatInputStatus(readText: true);
            // FairyGUI moves focus to the pressed button after this frame's
            // update; focus the input on the next frame instead.
            _focusInputNextFrame = true;
            return;
        }

        var resizing = _windowDrag == ChatWindowDrag.Resize;
        if (_windowDrag != ChatWindowDrag.None)
            SaveWindowGeometry();
        _windowDrag = ChatWindowDrag.None;
        _focusInputNextFrame = false;
        // A hidden input keeps FairyGUI focus and would still take keystrokes.
        try { _chatInput?.Blur(); }
        catch { }
        _inputFocused = false;
        CancelChatScrollDrags();
        SetSettingsOpen(false);
        if (resizing)
            RenderChatMessages(force: false);
    }

    /// <summary>Samples FairyGUI focus once per frame for the keyboard guards.</summary>
    private static void SampleInputFocus()
    {
        var focused = _chatWindowOpen && _chatInput != null && _chatInput.Focused;
        if (_inputFocused && !focused)
            _lastInputBlurFrame = Time.frameCount;
        _inputFocused = focused;
    }

    private static void ResetInputState()
    {
        OutgoingMessages.Clear();
        InputSubmitState.Reset();
        _lastInputPollFrame = int.MinValue;
        _lastEnterKeyFrame = int.MinValue;
        _lastInputBlurFrame = int.MinValue;
        _validatedInputText = null;
        _inputLengthExceeded = false;
        _chatInputReadDepth = 0;
        _chatWindowOpen = false;
        _readingOverlayInput = false;
        _inputFocused = false;
        _focusInputNextFrame = false;
        _windowDrag = ChatWindowDrag.None;
    }
}
