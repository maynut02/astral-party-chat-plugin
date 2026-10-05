using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AstralParty.Chat;

internal static partial class ChatOverlay
{
    private const float ChatReadingAreaWidth = 520f;
    private const float ChatScrollbarReservedWidth = 20f;
    private const float ChatMessageViewportWidth = ChatReadingAreaWidth - ChatScrollbarReservedWidth;
    private const float ChatScrollbarWidth = 12f;
    private const float ChatScrollbarMinimumThumbHeight = 28f;
    private const float ChatLatestButtonWidth = 284f;
    private const float ChatLatestButtonHeight = 38f;
    private static RectTransform? _chatScrollbarTrackRect;
    private static RectTransform? _chatScrollbarThumbRect;
    private static Image? _chatScrollbarThumbImage;
    private static RectTransform? _chatLatestButtonRect;
    private static Text? _chatLatestButtonText;
    private static bool _chatScrollbarDragging;
    private static int _chatScrollbarDragStartFrame = int.MinValue;
    private static float _chatScrollbarGrabOffset;
    private static readonly HashSet<string> UnreadChatMessageIdentities = new(StringComparer.Ordinal);
    private static string _chatScrollRoomId = string.Empty;
    private static bool _chatScrollRoomKnown;
    private static bool _chatScrollHistoryPending;

    internal static bool IsChatScrollbarDragging => _chatScrollbarDragging;

    private static float GetChatMessageRowWidth() => _chatViewportRect == null
        ? ChatMessageViewportWidth
        : Math.Max(1f, _chatViewportRect.rect.width);

    private static float GetChatScrollViewportHeight() => _chatViewportRect == null
        ? 502f
        : Math.Max(1f, _chatViewportRect.rect.height);

    private static float GetChatScrollableHeight() => _chatContentRect == null
        ? 0f
        : Math.Max(0f, _chatContentRect.rect.height - GetChatScrollViewportHeight());

    private static bool IsChatNearBottom() => _chatScrollRect == null
        || Mathf.Clamp01(_chatScrollRect.verticalNormalizedPosition)
            * GetChatScrollableHeight() <= ChatBottomFollowDistance;

    private static void CreateChatScrollControls()
    {
        if (_chatWindow == null || _chatScrollbarTrackRect != null)
            return;

        var track = CreateFlatImage(
            _chatWindow.transform, "MessageScrollbarTrack", new Color32(48, 48, 48, 255), true);
        _chatScrollbarTrackRect = track.GetComponent<RectTransform>();
        SetTopLeftRect(
            _chatScrollbarTrackRect,
            new Vector2(20f + ChatMessageViewportWidth + 8f, 110f),
            new Vector2(ChatScrollbarWidth, GetChatScrollViewportHeight()));

        var thumb = CreateFlatImage(
            track.transform, "MessageScrollbarThumb", new Color32(137, 137, 137, 255), true);
        _chatScrollbarThumbRect = thumb.GetComponent<RectTransform>();
        _chatScrollbarThumbImage = thumb.GetComponent<Image>();

        var latest = CreateFlatImage(
            _chatWindow.transform, "LatestMessagesButton", new Color32(106, 0, 78, 250), true);
        _chatLatestButtonRect = latest.GetComponent<RectTransform>();
        _chatLatestButtonText = CreateUiText(
            latest.transform, "LatestMessagesText", string.Empty, 18, Color.white,
            TextAnchor.MiddleCenter, Vector2.zero,
            new Vector2(ChatLatestButtonWidth, ChatLatestButtonHeight));
        _chatLatestButtonText.supportRichText = false;
        RefreshChatScrollControlPresentation();
    }

    // Called before the viewport's own pointer handler. These controls use the
    // overlay's manual pointer path, so they also work after ResetInputAxes and
    // while Harmony suppresses EventSystem's reads of the left mouse button.
    internal static bool TryHandleChatScrollControls(Vector2 pointer)
    {
        if (!_chatWindowOpen || _chatWindow == null || !_chatWindow.activeInHierarchy)
            return false;

        if (_chatLatestButtonRect != null
            && _chatLatestButtonRect.gameObject.activeInHierarchy
            && RectTransformUtility.RectangleContainsScreenPoint(_chatLatestButtonRect, pointer, null))
        {
            CancelChatScrollControlDrag();
            _chatDragging = false;
            _chatWindowDragging = false;
            if (_chatScrollRect != null)
            {
                _chatScrollRect.StopMovement();
                _chatScrollRect.verticalNormalizedPosition = 0f;
            }
            UnreadChatMessageIdentities.Clear();
            RefreshChatScrollControlPresentation();
            ConsumeOverlayPointer();
            return true;
        }

        if (_chatScrollbarTrackRect == null
            || !RectTransformUtility.RectangleContainsScreenPoint(_chatScrollbarTrackRect, pointer, null))
            return false;

        RefreshChatScrollControlPresentation();
        _chatDragging = false;
        _chatWindowDragging = false;
        if (_chatScrollRect != null && _chatScrollbarThumbRect != null
            && GetChatScrollableHeight() > 0.5f && TryGetChatScrollbarPointer(pointer, out var pointerTop))
        {
            var thumbHeight = _chatScrollbarThumbRect.rect.height;
            var travel = Math.Max(0f, _chatScrollbarTrackRect.rect.height - thumbHeight);
            var thumbTop = (1f - Mathf.Clamp01(_chatScrollRect.verticalNormalizedPosition)) * travel;
            _chatScrollbarGrabOffset = pointerTop >= thumbTop && pointerTop <= thumbTop + thumbHeight
                ? pointerTop - thumbTop
                : thumbHeight * 0.5f;
            _chatScrollbarDragging = travel > 0.5f;
            _chatScrollbarDragStartFrame = Time.frameCount;
            SetChatScrollbarPointer(pointerTop);
        }
        ConsumeOverlayPointer();
        return true;
    }

    private static bool TryGetChatScrollbarPointer(Vector2 pointer, out float pointerTop)
    {
        pointerTop = 0f;
        if (_chatScrollbarTrackRect == null
            || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _chatScrollbarTrackRect, pointer, null, out var local))
            return false;

        pointerTop = _chatScrollbarTrackRect.rect.yMax - local.y;
        return !float.IsNaN(pointerTop) && !float.IsInfinity(pointerTop);
    }

    private static void SetChatScrollbarPointer(float pointerTop)
    {
        if (_chatScrollRect == null || _chatScrollbarTrackRect == null || _chatScrollbarThumbRect == null)
            return;

        var travel = _chatScrollbarTrackRect.rect.height - _chatScrollbarThumbRect.rect.height;
        if (travel <= 0.5f)
            return;

        _chatScrollRect.StopMovement();
        _chatScrollRect.verticalNormalizedPosition = 1f - Mathf.Clamp01(
            (pointerTop - _chatScrollbarGrabOffset) / travel);
        RefreshChatScrollControlPresentation();
    }

    // Input reads are confined to an explicit overlay scope. Presentation can
    // also be refreshed by message/portrait updates without reading any input.
    internal static void UpdateChatScrollControls()
    {
        if (!_chatWindowOpen || _chatWindow == null || !_chatWindow.activeInHierarchy)
        {
            CancelChatScrollControlDrag();
            return;
        }

        // HandlePointerClick consumes/reset axes after capturing the control.
        // ResetInputAxes can clear held-button state for that frame only; use
        // the click itself as evidence until the next frame's physical read.
        if (_chatScrollbarDragging && Time.frameCount != _chatScrollbarDragStartFrame)
        {
            var wasReading = _readingOverlayInput;
            SetReadingOverlayInput(true);
            try
            {
                if (!ReadOverlayMouseButton(0) || GetChatScrollableHeight() <= 0.5f)
                    CancelChatScrollControlDrag();
                else
                {
                    SetReadingOverlayInput(true);
                    if (TryGetChatScrollbarPointer(Input.mousePosition, out var pointerTop))
                        SetChatScrollbarPointer(pointerTop);
                }
            }
            finally { SetReadingOverlayInput(wasReading); }
        }
        RefreshChatScrollControlPresentation();
    }

    private static void RefreshChatScrollControlPresentation()
    {
        if (IsChatNearBottom())
            UnreadChatMessageIdentities.Clear();

        if (_chatScrollbarTrackRect != null && _chatScrollbarThumbRect != null)
        {
            var viewportHeight = GetChatScrollViewportHeight();
            var scrollable = GetChatScrollableHeight();
            var trackHeight = viewportHeight;
            _chatScrollbarTrackRect.sizeDelta = new Vector2(ChatScrollbarWidth, trackHeight);
            var thumbHeight = scrollable <= 0.5f ? trackHeight : Math.Min(trackHeight,
                Math.Max(ChatScrollbarMinimumThumbHeight, trackHeight * viewportHeight / (viewportHeight + scrollable)));
            var thumbTop = (1f - Mathf.Clamp01(_chatScrollRect?.verticalNormalizedPosition ?? 0f))
                * Math.Max(0f, trackHeight - thumbHeight);
            SetTopLeftRect(_chatScrollbarThumbRect, new Vector2(2f, thumbTop),
                new Vector2(ChatScrollbarWidth - 4f, thumbHeight));
            if (_chatScrollbarThumbImage != null)
                _chatScrollbarThumbImage.color = scrollable <= 0.5f
                    ? new Color32(65, 65, 65, 255)
                    : _chatScrollbarDragging ? new Color32(255, 0, 180, 255) : new Color32(137, 137, 137, 255);
        }

        if (_chatLatestButtonRect != null)
        {
            SetTopLeftRect(_chatLatestButtonRect,
                new Vector2(20f + Math.Max(0f, (GetChatMessageRowWidth() - ChatLatestButtonWidth) * 0.5f),
                    110f + GetChatScrollViewportHeight() - ChatLatestButtonHeight - 8f),
                new Vector2(ChatLatestButtonWidth, ChatLatestButtonHeight));
            _chatLatestButtonRect.gameObject.SetActive(UnreadChatMessageIdentities.Count > 0);
        }
        if (_chatLatestButtonText != null)
            _chatLatestButtonText.text = "새로운 메시지";
    }

    internal static void CancelChatScrollControlDrag()
    {
        _chatScrollbarDragging = false;
        _chatScrollbarDragStartFrame = int.MinValue;
        _chatScrollbarGrabOffset = 0f;
    }

    private static void ObserveChatScrollContext(ChatSnapshot snapshot)
    {
        var roomId = snapshot.RoomId ?? string.Empty;
        if (_chatScrollRoomKnown && !string.Equals(roomId, _chatScrollRoomId, StringComparison.Ordinal))
        {
            CancelChatScrollControlDrag();
            UnreadChatMessageIdentities.Clear();
            _chatScrollHistoryPending = true;
            if (_chatScrollRect != null)
                _chatScrollRect.verticalNormalizedPosition = 0f;
        }
        _chatScrollRoomKnown = true;
        _chatScrollRoomId = roomId;

        ObserveChatScrollConnectionStatus();
        RefreshChatScrollControlPresentation();
    }

    internal static void ObserveChatScrollConnectionStatus()
    {
        // JOINED replaces history after connection/entry statuses. The first
        // resulting list is a baseline, even if it happens to overlap its tail.
        if (_chatStatus.StartsWith("채팅 서버 연결", StringComparison.Ordinal)
            || _chatStatus.StartsWith("채팅 서버 재연결", StringComparison.Ordinal)
            || _chatStatus.StartsWith("채팅 연결 대기", StringComparison.Ordinal)
            || _chatStatus.StartsWith("채팅방 입장", StringComparison.Ordinal)
            || _chatStatus.StartsWith("채팅방 연결 준비", StringComparison.Ordinal)
            || _chatStatus.StartsWith("채팅방 확인", StringComparison.Ordinal))
            _chatScrollHistoryPending = true;
    }

    internal static void ResetChatScrollControls()
    {
        CancelChatScrollControlDrag();
        UnreadChatMessageIdentities.Clear();
        _chatScrollHistoryPending = false;
        _chatScrollRoomKnown = false;
        _chatScrollRoomId = string.Empty;
        _chatScrollbarTrackRect = null;
        _chatScrollbarThumbRect = null;
        _chatScrollbarThumbImage = null;
        _chatLatestButtonRect = null;
        _chatLatestButtonText = null;
    }
}
