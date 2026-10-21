using System;
using System.Collections.Generic;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    private const float ChatScrollbarWidth = 12f;
    private const float ChatScrollbarMinimumThumbHeight = 28f;
    private const float ChatLatestButtonWidth = 284f;
    private const float ChatLatestButtonHeight = 38f;
    private static readonly Color ScrollTrackColor = new Color32(48, 48, 48, 255);
    private static readonly Color ScrollThumbColor = new Color32(137, 137, 137, 255);
    private static readonly Color ScrollThumbIdleColor = new Color32(65, 65, 65, 255);
    private static readonly Color LatestButtonColor = new Color32(106, 0, 78, 250);
    private static FguiNode? _scrollTrack;
    private static FguiNode? _scrollTrackBackground;
    private static FguiNode? _scrollThumb;
    private static FguiNode? _latestButton;
    private static Color _scrollThumbDrawnColor;
    private static float _chatScrollTop;
    private static float _chatContentHeight;
    private static float _chatRowsHeight;
    private static float _chatViewportWidth = ChatLayout.ViewportWidth(ChatLayout.DefaultWidth);
    private static float _chatViewportHeight = ChatLayout.ViewportHeight(ChatLayout.DefaultHeight);
    private static bool _chatScrollbarDragging;
    private static float _chatScrollbarGrabOffset;
    private static bool _chatViewportDragging;
    private static float _chatViewportDragStartPointer;
    private static float _chatViewportDragStartTop;
    private static readonly HashSet<string> UnreadChatMessageIdentities = new(StringComparer.Ordinal);
    private static string _chatScrollRoomId = string.Empty;
    private static bool _chatScrollRoomKnown;
    private static bool _chatScrollHistoryPending;

    private static float GetChatMessageRowWidth() => Math.Max(1f, _chatViewportWidth);

    private static float GetChatScrollViewportHeight() => Math.Max(1f, _chatViewportHeight);

    private static float GetChatScrollableHeight() =>
        Math.Max(0f, _chatContentHeight - GetChatScrollViewportHeight());

    private static bool IsChatNearBottom() =>
        GetChatScrollableHeight() - _chatScrollTop <= ChatBottomFollowDistance;

    private static void CreateChatScrollControls(FguiNode window)
    {
        _scrollTrack = AddButton(window, ChatControl.ScrollTrack, 0f, ChatLayout.MessagesTop, ChatScrollbarWidth, 1f);
        _scrollTrackBackground = AddGraph(_scrollTrack, 0f, 0f, ChatScrollbarWidth, 1f, ScrollTrackColor);
        _scrollThumb = AddGraph(_scrollTrack, 2f, 0f, ChatScrollbarWidth - 4f, 1f, ScrollThumbIdleColor);
        _scrollThumbDrawnColor = ScrollThumbIdleColor;

        _latestButton = AddButton(window, ChatControl.Latest, 0f, 0f, ChatLatestButtonWidth, ChatLatestButtonHeight);
        AddGraph(_latestButton, 0f, 0f, ChatLatestButtonWidth, ChatLatestButtonHeight,
            LatestButtonColor, 8f);
        AddText(_latestButton, "새로운 메시지", 18, Color.white, FguiAlign.Center,
            0f, 0f, ChatLatestButtonWidth, ChatLatestButtonHeight);
        _latestButton.SetVisible(false);
    }

    private static void LayoutChatScrollControls(float windowWidth, float viewportHeight)
    {
        var wasAtBottom = IsChatNearBottom();
        _chatViewportWidth = ChatLayout.ViewportWidth(windowWidth);
        _chatViewportHeight = viewportHeight;
        _chatContentHeight = Math.Max(GetChatScrollViewportHeight(), _chatRowsHeight);
        _chatContent?.SetSize(_chatViewportWidth, _chatContentHeight);
        _scrollTrack?.SetXY(ChatLayout.SidePadding + _chatViewportWidth + 8f, ChatLayout.MessagesTop);
        _scrollTrack?.SetSize(ChatScrollbarWidth, viewportHeight);
        _scrollTrackBackground?.SetSize(ChatScrollbarWidth, viewportHeight);
        _latestButton?.SetXY(
            ChatLayout.SidePadding + Math.Max(0f, (_chatViewportWidth - ChatLatestButtonWidth) * 0.5f),
            ChatLayout.MessagesTop + viewportHeight - ChatLatestButtonHeight - 8f);
        SetChatScrollTop(wasAtBottom ? GetChatScrollableHeight() : _chatScrollTop);
    }

    private static void SetChatScrollTop(float top)
    {
        _chatScrollTop = Math.Clamp(float.IsFinite(top) ? top : 0f, 0f, GetChatScrollableHeight());
        _chatContent?.SetXY(0f, -_chatScrollTop);
        RefreshChatRowVisibility();
        RefreshChatScrollControlPresentation();
    }

    private static void ScrollChatToLatest()
    {
        CancelChatScrollDrags();
        SetChatScrollTop(GetChatScrollableHeight());
        UnreadChatMessageIdentities.Clear();
        RefreshChatScrollControlPresentation();
    }

    private static void ScrollChatByWheel(float notches)
    {
        if (Math.Abs(notches) <= 0.001f)
            return;
        // A fixed distance per notch keeps wheel travel independent of history length.
        SetChatScrollTop(_chatScrollTop - notches * ChatWheelStep);
    }

    private static void BeginChatViewportDrag(float pointerY)
    {
        CancelChatScrollDrags();
        if (GetChatScrollableHeight() <= 0.5f)
            return;
        _chatViewportDragging = true;
        _chatViewportDragStartPointer = pointerY;
        _chatViewportDragStartTop = _chatScrollTop;
    }

    /// <summary>Starts a track press; a press outside the thumb centers the thumb on the pointer.</summary>
    private static void BeginChatScrollbarPress(float pointerTop)
    {
        CancelChatScrollDrags();
        var (thumbTop, thumbHeight, travel) = GetChatScrollThumb();
        if (GetChatScrollableHeight() <= 0.5f || travel <= 0.5f)
            return;
        _chatScrollbarGrabOffset = pointerTop >= thumbTop && pointerTop <= thumbTop + thumbHeight
            ? pointerTop - thumbTop
            : thumbHeight * 0.5f;
        _chatScrollbarDragging = true;
        SetChatScrollbarPointer(pointerTop);
    }

    private static void SetChatScrollbarPointer(float pointerTop)
    {
        var (_, _, travel) = GetChatScrollThumb();
        if (travel <= 0.5f)
            return;
        SetChatScrollTop(Math.Clamp((pointerTop - _chatScrollbarGrabOffset) / travel, 0f, 1f)
            * GetChatScrollableHeight());
    }

    /// <summary>Continues an active drag with the pointer in window coordinates.</summary>
    private static void UpdateChatScrollDrags(bool held, Vector2 windowPointer)
    {
        if (!_chatScrollbarDragging && !_chatViewportDragging)
            return;
        if (!held || GetChatScrollableHeight() <= 0.5f)
        {
            CancelChatScrollDrags();
            return;
        }

        if (_chatScrollbarDragging)
            SetChatScrollbarPointer(windowPointer.y - ChatLayout.MessagesTop);
        else
            SetChatScrollTop(_chatViewportDragStartTop + _chatViewportDragStartPointer - windowPointer.y);
    }

    private static (float Top, float Height, float Travel) GetChatScrollThumb()
    {
        var trackHeight = GetChatScrollViewportHeight();
        var scrollable = GetChatScrollableHeight();
        var thumbHeight = scrollable <= 0.5f ? trackHeight : Math.Min(trackHeight,
            Math.Max(ChatScrollbarMinimumThumbHeight, trackHeight * trackHeight / (trackHeight + scrollable)));
        var travel = Math.Max(0f, trackHeight - thumbHeight);
        var top = scrollable <= 0.5f ? 0f : _chatScrollTop / scrollable * travel;
        return (top, thumbHeight, travel);
    }

    private static void RefreshChatScrollControlPresentation()
    {
        if (IsChatNearBottom())
            UnreadChatMessageIdentities.Clear();

        if (_scrollThumb != null)
        {
            var (top, height, _) = GetChatScrollThumb();
            _scrollThumb.SetXY(2f, top);
            _scrollThumb.SetSize(ChatScrollbarWidth - 4f, height);
            var color = GetChatScrollableHeight() <= 0.5f ? ScrollThumbIdleColor
                : _chatScrollbarDragging ? AccentColor : ScrollThumbColor;
            if (color != _scrollThumbDrawnColor)
            {
                // A shape keeps its draw arguments across resizes; redraw only on color change.
                _scrollThumb.Fill(color);
                _scrollThumbDrawnColor = color;
            }
        }

        _latestButton?.SetVisible(UnreadChatMessageIdentities.Count > 0 && !_settingsOpen);
    }

    private static void CancelChatScrollDrags()
    {
        var wasDragging = _chatScrollbarDragging;
        ResetChatScrollDrags();
        if (wasDragging)
            RefreshChatScrollControlPresentation();
    }

    private static void ResetChatScrollDrags()
    {
        _chatScrollbarDragging = false;
        _chatViewportDragging = false;
        _chatScrollbarGrabOffset = 0f;
        _chatViewportDragStartPointer = 0f;
        _chatViewportDragStartTop = 0f;
    }

    private static void ObserveChatScrollContext(ChatSnapshot snapshot)
    {
        var roomId = snapshot.RoomId ?? string.Empty;
        if (_chatScrollRoomKnown && !string.Equals(roomId, _chatScrollRoomId, StringComparison.Ordinal))
        {
            CancelChatScrollDrags();
            UnreadChatMessageIdentities.Clear();
            _chatScrollHistoryPending = true;
            SetChatScrollTop(GetChatScrollableHeight());
        }
        _chatScrollRoomKnown = true;
        _chatScrollRoomId = roomId;

        ObserveChatScrollConnectionStatus();
        RefreshChatScrollControlPresentation();
    }

    private static void ObserveChatScrollConnectionStatus()
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

    private static void ResetChatScrollControls()
    {
        ResetChatScrollDrags();
        UnreadChatMessageIdentities.Clear();
        _chatScrollHistoryPending = false;
        _chatScrollRoomKnown = false;
        _chatScrollRoomId = string.Empty;
        _chatScrollTop = 0f;
        _chatContentHeight = 0f;
        _chatRowsHeight = 0f;
        _chatViewportWidth = ChatLayout.ViewportWidth(ChatLayout.DefaultWidth);
        _chatViewportHeight = ChatLayout.ViewportHeight(ChatLayout.DefaultHeight);
        _scrollTrack = _scrollTrackBackground = _scrollThumb = _latestButton = null;
        _scrollThumbDrawnColor = ScrollThumbIdleColor;
    }
}
