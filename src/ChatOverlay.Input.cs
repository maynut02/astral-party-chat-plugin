using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    private static readonly Queue<string> OutgoingMessages = new();
    private static readonly ChatInputSubmitState InputSubmitState = new();
    private static bool _chatWindowOpen;
    private static bool _chatCloseHovered;
    private static bool _readingOverlayInput;
    private static bool _chatDragging;
    private static bool _chatWindowDragging;
    private static Vector2 _chatWindowDragPointerStart;
    private static Vector2 _chatWindowDragPositionStart;
    private static Vector2 _chatScrollDragStartLocal;
    private static float _chatDragStartNormalized;
    private static int _lastInputPollFrame = int.MinValue;
    private static PointerEventData? _chatInputPointer;
    private static int _chatInputPointerStartFrame = int.MinValue;
    [ThreadStatic] private static int _chatInputReadDepth;

    public static bool IsChatInputField(InputField? field) => field != null && field == _chatInputField;

    public static void EnterChatInputRead() => _chatInputReadDepth++;
    public static void ExitChatInputRead()
    {
        _chatInputReadDepth = Math.Max(0, _chatInputReadDepth - 1);
        if (_chatInputReadDepth == 0 && (_chatInputField == null || !_chatInputField.isFocused))
            InputSubmitState.CancelEnter();
    }

    public static bool ShouldBlockGameKeyboardInput() => !_readingOverlayInput
        && _chatInputReadDepth == 0
        && _chatWindowOpen && _chatWindow != null && _chatWindow.activeInHierarchy
        && _chatInputField != null && _chatInputField.isFocused;

    /// <summary>
    /// The stock SingleLine InputField finishes editing on Enter even when
    /// Enter only commits IME. Handle this field's Enter and retain its focus.
    /// Other fields and editing keys keep their native behavior.
    /// </summary>
    public static bool InterceptChatInputKeyPress(InputField field, Event keyEvent, out InputField.EditState result)
    {
        result = InputField.EditState.Continue;
        if (!IsChatInputField(field) || !_chatWindowOpen
            || _chatWindow == null || !_chatWindow.activeInHierarchy)
            return false;
        if (keyEvent.rawType == EventType.KeyDown && keyEvent.keyCode == KeyCode.Escape)
        {
            InputSubmitState.CancelEnter();
            CancelChatInputPointer();
            return false;
        }
        if (!field.isFocused)
            return false;
        if (keyEvent.rawType != EventType.KeyDown
            || (keyEvent.keyCode != KeyCode.Return && keyEvent.keyCode != KeyCode.KeypadEnter))
            return false;

        InputSubmitState.RequestEnter(Time.frameCount, !string.IsNullOrEmpty(Input.compositionString));
        return true;
    }

    public static void SetChatStatus(string status)
    {
        _chatStatus = string.IsNullOrWhiteSpace(status) ? "연결 대기 중" : status.Trim();
        UpdateChatInputStatus();
    }

    private static void UpdateChatInputStatus()
    {
        if (_chatRoomText == null)
            return;

        // Validate only committed text. Keep the header free of a character
        // counter and never rewrite InputField text or IME composition.
        var value = (_chatInputField?.text ?? string.Empty).Trim();
        var length = PartyProtocol.TextLength(value);
        var status = length > PartyProtocol.MaxTextLength
            ? "입력 길이 초과: 내용을 줄여주세요."
            : _chatStatus;
        if (_chatRoomText.text != status)
            _chatRoomText.text = status;
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

    public static void SetReadingOverlayInput(bool reading)
    {
        _readingOverlayInput = reading;
    }

    public static bool ShouldBlockRawMouseInput()
    {
        if (_readingOverlayInput || _root == null)
            return false;

        var pointer = (Vector2)Input.mousePosition;
        if (_chatButton != null
            && _chatButton.activeInHierarchy
            && _chatButtonRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatButtonRect,
                pointer,
                null))
            return true;

        if (_chatWindowDragging || _chatDragging || IsChatScrollbarDragging || _chatInputPointer != null)
            return true;

        return _chatWindowOpen
            && _chatWindow != null
            && _chatWindow.activeInHierarchy
            && _chatWindowRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatWindowRect,
                pointer,
                null);
    }

    public static bool HandlePointerClick()
    {
        if (_root == null)
            return false;

        var pointer = (Vector2)Input.mousePosition;
        CancelChatInputPointer();
        if (_chatButton != null
            && _chatButton.activeInHierarchy
            && _chatButtonRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatButtonRect,
                pointer,
                null))
        {
            SetChatWindowOpen(!_chatWindowOpen);
            ConsumeOverlayPointer();
            return true;
        }

        if (!_chatWindowOpen
            || _chatWindow == null
            || !_chatWindow.activeInHierarchy)
            return false;

        if (_chatCloseRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatCloseRect,
                pointer,
                null))
        {
            SetChatWindowOpen(false);
            ConsumeOverlayPointer();
            return true;
        }

        if (TryHandleChatScrollControls(pointer))
        {
            ConsumeOverlayPointer();
            return true;
        }

        if (_chatHeaderRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatHeaderRect,
                pointer,
                null))
        {
            BeginChatWindowMove();
            return true;
        }

        if (_chatViewportRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatViewportRect,
                pointer,
                null))
        {
            if (TryGetViewportLocalPointer(out var viewportPoint))
                BeginChatScrollDrag(viewportPoint);
            return true;
        }

        if (_chatInputRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatInputRect,
                pointer,
                null))
        {
            BeginChatInputPointer(pointer);
            ConsumeOverlayPointer();
            return true;
        }

        if (_chatSendRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatSendRect,
                pointer,
                null))
        {
            RequestExplicitInputSubmit();
            ConsumeOverlayPointer();
            return true;
        }

        if (_chatWindowRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatWindowRect,
                pointer,
                null))
        {
            ConsumeOverlayPointer();
            return true;
        }

        // The manual handler runs before EventSystem processes this click.
        // Cancel Enter now, before this tick can submit a field about to blur.
        BlurChatInput();
        return false;
    }

    private static void BeginChatInputPointer(Vector2 pointer)
    {
        var field = _chatInputField;
        var eventSystem = EventSystem.current;
        if (field == null || !field.isFocused)
            InputSubmitState.CancelEnter();
        if (field == null || eventSystem == null)
        {
            FocusChatInput();
            return;
        }

        CancelChatScrollControlDrag();
        _chatDragging = false;
        _chatWindowDragging = false;
        var data = new PointerEventData(eventSystem)
        {
            button = PointerEventData.InputButton.Left,
            position = pointer,
            pressPosition = pointer,
            delta = Vector2.zero,
            pointerPress = field.gameObject,
            pointerDrag = field.gameObject,
            useDragThreshold = true
        };
        _chatInputPointer = data;
        _chatInputPointerStartFrame = Time.frameCount;
        EnterChatInputRead();
        try
        {
            // Forward only to our field. Raw mouse polling stays masked for
            // the game; this field's scope permits Shift-click text selection.
            field.OnPointerDown(data);
            field.OnPointerClick(data);
        }
        catch
        {
            CancelChatInputPointer();
            throw;
        }
        finally { ExitChatInputRead(); }
    }

    private static void UpdateChatInputPointer()
    {
        var data = _chatInputPointer;
        if (data == null || Time.frameCount == _chatInputPointerStartFrame)
            return;

        var field = _chatInputField;
        if (field == null || !field.isFocused || !ReadOverlayMouseButton(0))
        {
            CancelChatInputPointer();
            return;
        }

        var pointer = (Vector2)Input.mousePosition;
        data.delta = pointer - data.position;
        data.position = pointer;
        var distance = pointer - data.pressPosition;
        var threshold = Math.Max(0, EventSystem.current?.pixelDragThreshold ?? 5);
        EnterChatInputRead();
        try
        {
            if (!data.dragging && distance.x * distance.x + distance.y * distance.y >= threshold * threshold)
            {
                field.OnBeginDrag(data);
                data.dragging = true;
            }
            if (data.dragging)
                field.OnDrag(data);
        }
        catch
        {
            CancelChatInputPointer();
            throw;
        }
        finally { ExitChatInputRead(); }
    }

    private static void CancelChatInputPointer()
    {
        var data = _chatInputPointer;
        _chatInputPointer = null;
        _chatInputPointerStartFrame = int.MinValue;
        if (data == null || _chatInputField == null)
            return;

        EnterChatInputRead();
        try
        {
            if (data.dragging)
                _chatInputField.OnEndDrag(data);
            _chatInputField.OnPointerUp(data);
        }
        catch { }
        finally { ExitChatInputRead(); }
    }

    private static void BlurChatInput()
    {
        InputSubmitState.CancelEnter();
        CancelChatInputPointer();
        try
        {
            var eventSystem = EventSystem.current;
            if (_chatInputField != null && eventSystem != null
                && eventSystem.currentSelectedGameObject == _chatInputField.gameObject)
                eventSystem.SetSelectedGameObject(null);
            _chatInputField?.DeactivateInputField();
        }
        catch { }
    }

    private static bool TryGetOverlayPointerLocal(out Vector2 localPoint)
    {
        localPoint = Vector2.zero;
        if (_root == null)
            return false;

        var rootRect = _root.GetComponent<RectTransform>();
        return rootRect != null
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect,
                Input.mousePosition,
                null,
                out localPoint);
    }

    private static bool TryGetViewportLocalPointer(out Vector2 localPoint)
    {
        localPoint = Vector2.zero;
        return _chatViewportRect != null
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _chatViewportRect,
                Input.mousePosition,
                null,
                out localPoint);
    }

    private static void BeginChatWindowMove()
    {
        if (_chatWindowRect == null
            || !TryGetOverlayPointerLocal(out var pointer))
            return;

        CancelChatScrollControlDrag();
        _chatDragging = false;
        _chatWindowDragging = true;
        _chatWindowDragPointerStart = pointer;
        _chatWindowDragPositionStart = _chatWindowRect.anchoredPosition;
    }

    private static void UpdateChatWindowManipulation()
    {
        if (_chatWindowRect == null || !_chatWindowDragging)
            return;

        if (!ReadOverlayMouseButton(0))
        {
            _chatWindowDragging = false;
            ClampChatWindowHeaderVisible();
            SaveChatWindowTransform();
            return;
        }

        if (!TryGetOverlayPointerLocal(out var pointer))
            return;

        _chatWindowRect.anchoredPosition =
            _chatWindowDragPositionStart + pointer - _chatWindowDragPointerStart;
        ClampChatWindowHeaderVisible();
    }

    private static bool IsSaneChatWindowPosition(float x, float y)
    {
        if (float.IsNaN(x)
            || float.IsNaN(y)
            || float.IsInfinity(x)
            || float.IsInfinity(y))
            return false;

        var maxX = Math.Max(1920f, Screen.width) * 3f;
        var maxY = Math.Max(1080f, Screen.height) * 3f;
        return Math.Abs(x) <= maxX && Math.Abs(y) <= maxY;
    }

    private static void ClampChatWindowHeaderVisible()
    {
        if (_chatWindowRect == null || _root == null)
            return;

        try
        {
            var rootRect = _root.GetComponent<RectTransform>();
            if (rootRect == null)
                return;

            var rootWidth = rootRect.rect.width;
            var rootHeight = rootRect.rect.height;
            if (rootWidth <= 1f || rootHeight <= 1f)
                return;

            const float minVisibleHeaderWidth = 100f;
            const float minVisibleHeaderHeight = 36f;
            const float headerLeft = -548f;
            const float headerRight = -12f;
            const float headerBottom = 256f;
            const float headerTop = 338f;

            var minX = -rootWidth + minVisibleHeaderWidth - headerRight;
            var maxX = -minVisibleHeaderWidth - headerLeft;
            var minY = (-rootHeight * 0.5f) + minVisibleHeaderHeight - headerTop;
            var maxY = (rootHeight * 0.5f) - minVisibleHeaderHeight - headerBottom;
            var position = _chatWindowRect.anchoredPosition;
            position.x = Mathf.Clamp(position.x, minX, maxX);
            position.y = Mathf.Clamp(position.y, minY, maxY);
            _chatWindowRect.anchoredPosition = position;
        }
        catch { }
    }

    private static void SaveChatWindowTransform()
    {
        if (_chatWindowRect == null)
            return;

        try
        {
            var position = _chatWindowRect.anchoredPosition;
            PlayerPrefs.SetFloat(ChatWindowXPref, position.x);
            PlayerPrefs.SetFloat(ChatWindowYPref, position.y);
            PlayerPrefs.Save();
        }
        catch { }
    }

    private static void BeginChatScrollDrag(Vector2 pointerLocal)
    {
        CancelChatScrollControlDrag();
        if (_chatScrollRect == null
            || _chatContentRect == null
            || _chatViewportRect == null)
            return;

        var scrollable = Math.Max(
            0f,
            _chatContentRect.rect.height - _chatViewportRect.rect.height);
        if (scrollable <= 0.5f)
            return;

        _chatDragging = true;
        _chatScrollDragStartLocal = pointerLocal;
        _chatDragStartNormalized = _chatScrollRect.verticalNormalizedPosition;
    }

    private static bool ReadOverlayMouseButton(int button)
    {
        var wasReading = _readingOverlayInput;
        SetReadingOverlayInput(true);
        try { return Input.GetMouseButton(button); }
        finally { SetReadingOverlayInput(wasReading); }
    }

    private static void UpdateChatScrollDrag()
    {
        if (!_chatDragging
            || _chatScrollRect == null
            || _chatContentRect == null
            || _chatViewportRect == null)
            return;

        if (!ReadOverlayMouseButton(0))
        {
            _chatDragging = false;
            return;
        }

        var scrollable = Math.Max(
            0f,
            _chatContentRect.rect.height - _chatViewportRect.rect.height);
        if (scrollable <= 0.5f || !TryGetViewportLocalPointer(out var pointerLocal))
        {
            _chatDragging = false;
            return;
        }

        var deltaLocalY = pointerLocal.y - _chatScrollDragStartLocal.y;
        _chatScrollRect.verticalNormalizedPosition = Mathf.Clamp01(
            _chatDragStartNormalized - (deltaLocalY / scrollable));
    }

    private static void RequestExplicitInputSubmit()
    {
        if (_chatInputField == null)
            return;

        InputSubmitState.RequestExplicitSend(Time.frameCount);
    }

    private static void PollChatInputSubmit()
    {
        var frame = Time.frameCount;
        if (_lastInputPollFrame == frame)
            return;
        _lastInputPollFrame = frame;

        if (_chatInputField == null || !_chatInputField.isFocused)
            InputSubmitState.CancelEnter();

        var composing = !string.IsNullOrEmpty(Input.compositionString);
        InputSubmitState.ObserveFrame(frame, composing);

        EnterChatInputRead();
        try
        {
            if (_chatInputField != null
                && _chatInputField.isFocused
                && (Input.GetKeyDown(KeyCode.Return)
                    || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                InputSubmitState.RequestEnter(frame, composing);
            }
        }
        finally { ExitChatInputRead(); }

        if (InputSubmitState.TryTakeReady(frame, composing, out _))
            QueueCurrentInput();
    }

    private static void QueueCurrentInput()
    {
        if (_chatInputField == null)
            return;

        var value = (_chatInputField.text ?? string.Empty).Trim();
        if (value.Length == 0)
            return;

        if (PartyProtocol.TextLength(value) > PartyProtocol.MaxTextLength)
        {
            UpdateChatInputStatus();
            return;
        }

        OutgoingMessages.Enqueue(value);
        _chatInputField.text = string.Empty;
        _chatInputField.ActivateInputField();
        UpdateChatInputStatus();
    }

    private static void FocusChatInput()
    {
        if (_chatInputField == null)
            return;

        if (!_chatInputField.isFocused)
            InputSubmitState.CancelEnter();

        try
        {
            var eventSystem = EventSystem.current;
            if (eventSystem != null)
                eventSystem.SetSelectedGameObject(_chatInputField.gameObject);

            _chatInputField.ActivateInputField();
        }
        catch { }
    }

    private static void ConsumeOverlayPointer()
    {
        try { Input.ResetInputAxes(); }
        catch { }
    }

    private static void SetChatWindowOpen(bool open)
    {
        if (!open)
            CancelChatInputPointer();
        _chatWindowOpen = open;
        if (_chatWindow != null && _chatWindow.activeSelf != open)
            _chatWindow.SetActive(open);

        if (open)
        {
            _chatWindowRect?.SetAsLastSibling();
            ClampChatWindowHeaderVisible();
            SaveChatWindowTransform();
        }
        else
        {
            if (_chatWindowDragging)
                SaveChatWindowTransform();

            SetChatCloseHover(false);
            CancelChatScrollControlDrag();
            _chatDragging = false;
            _chatWindowDragging = false;
            InputSubmitState.Reset();
            _lastInputPollFrame = int.MinValue;
            try { _chatInputField?.DeactivateInputField(); }
            catch { }
        }
    }

    private static void UpdateChatWindow(ChatSnapshot snapshot)
    {
        if (_chatWindow == null)
            return;

        ProcessCharacterImageDownloads();

        if (!snapshot.ChatButtonVisible)
        {
            if (_chatWindowOpen)
                SetChatWindowOpen(false);
            return;
        }

        if (_chatTitleText != null)
        {
            _chatTitleText.text = "채팅";
            _chatTitleText.enabled = true;
            _chatTitleText.color = Color.white;
            _chatTitleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _chatTitleText.verticalOverflow = VerticalWrapMode.Overflow;
        }

        UpdateChatInputStatus();

        if (!_chatWindowOpen || !_chatWindow.activeInHierarchy)
        {
            CancelChatInputPointer();
            CancelChatScrollControlDrag();
            SetChatCloseHover(false);
            return;
        }

        UpdateChatWindowManipulation();
        ClampChatWindowHeaderVisible();

        var hovered = _chatCloseRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatCloseRect,
                Input.mousePosition,
                null);
        SetChatCloseHover(hovered);

        UpdateChatInputPointer();
        PollChatInputSubmit();
        UpdateChatScrollDrag();

        if (_chatScrollRect != null
            && _chatContentRect != null
            && _chatViewportRect != null
            && RectTransformUtility.RectangleContainsScreenPoint(
                _chatViewportRect,
                Input.mousePosition,
                null))
        {
            var scroll = Input.mouseScrollDelta.y;
            if (Math.Abs(scroll) > 0.001f)
            {
                var scrollable = Math.Max(
                    0f,
                    _chatContentRect.rect.height - _chatViewportRect.rect.height);
                _chatScrollRect.verticalNormalizedPosition = scrollable <= 0.5f
                    ? 0f
                    : Mathf.Clamp01(_chatScrollRect.verticalNormalizedPosition
                        + (scroll * ChatWheelStep / scrollable));
                ConsumeOverlayPointer();
            }
        }
        UpdateChatScrollControls();
    }

    private static void SetChatCloseHover(bool hovered)
    {
        if (_chatCloseHovered == hovered)
            return;

        _chatCloseHovered = hovered;
        if (_chatCloseBackground != null)
            _chatCloseBackground.color = hovered
                ? new Color32(255, 0, 180, 255)
                : new Color32(52, 52, 52, 255);
    }

    private static void ResetInputState()
    {
        CancelChatInputPointer();
        try
        {
            var eventSystem = EventSystem.current;
            if (_chatInputField != null
                && eventSystem != null
                && eventSystem.currentSelectedGameObject == _chatInputField.gameObject)
                eventSystem.SetSelectedGameObject(null);
            _chatInputField?.DeactivateInputField();
        }
        catch { }

        OutgoingMessages.Clear();
        InputSubmitState.Reset();
        _lastInputPollFrame = int.MinValue;
        _chatInputReadDepth = 0;
        _chatWindowOpen = false;
        _chatCloseHovered = false;
        _readingOverlayInput = false;
        _chatDragging = false;
        _chatWindowDragging = false;
        _chatWindowDragPointerStart = Vector2.zero;
        _chatWindowDragPositionStart = Vector2.zero;
        _chatScrollDragStartLocal = Vector2.zero;
        _chatDragStartNormalized = 0f;
    }
}
