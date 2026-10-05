#if OVERLAY_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using AstralParty.Chat;
using UnityEngine;

internal static class ScrollControlsTests
{
    private static int _passed;
    private static int _assertions;

    internal static void Run()
    {
        Check("custom scrollbar reserves space and every row/text uses the viewport width", ReservedWidthIsConsistent);
        Check("thumb size and position represent short and long histories", ThumbRepresentsHistory);
        Check("track clicks and manual dragging work with raw mouse suppression", TrackClickAndDrag);
        Check("drag release, cancellation, close and cleanup clear captured pointers", DragLifecycle);
        Check("new messages retain reading position and latest button clears the count", UnreadAndLatest);
        Check("echo, metadata, portrait refresh and rerender never count as arrivals", PresentationUpdatesAreNotArrivals);
        Check("history prepends, replays and reconnect history never count as arrivals", HistoryIsNotAnArrival);
        Check("300-row trim retains rows, reader offset and only available unread messages", TrimRetainsReaderAndUnread);
        Check("room switches and empty histories reset unread and drag state", RoomAndEmptyHistoryReset);
        Check("wheel, viewport drag and bottom-distance threshold update notifications", BottomClearsNotification);
        Console.WriteLine($"{_passed} scroll control checks passed ({_assertions} assertions).");
    }

    private static void Check(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            throw;
        }
    }

    private static void Prepare(int count = 10)
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, count).Select(Message));
        ChatOverlay.RecreateScrollControlsForTest();
    }

    private static ChatUiMessage Message(int index) => new()
    {
        Id = $"scroll-{index:D4}", Sender = "Mina", CharacterId = "101",
        CharacterName = "캐릭터", Order = "P1", Text = $"메시지 {index}"
    };

    private static void ReadAboveBottom(float distance = 200f) =>
        ChatOverlay.SetScrollPosition(distance / Math.Max(1f, ChatOverlay.GetContentHeight() - 502f));

    private static float TopOffset() =>
        (1f - ChatOverlay.GetScrollPosition()) * (ChatOverlay.GetContentHeight() - 502f);

    private static void ReservedWidthIsConsistent()
    {
        Prepare();
        var viewportWidth = ChatOverlay.GetViewportWidthForTest();
        Near(500f, viewportWidth, "reserved viewport width");
        var controls = ChatOverlay.InspectScrollControls();
        Near(528f, controls.Track.anchoredPosition.x, "track starts after reserved gap");
        Near(540f, controls.Track.anchoredPosition.x + controls.Track.rect.width, "track right edge");
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "normal", Sender = "Mina", Text = new string('가', 200) },
            new ChatUiMessage { Id = "system", Text = "참가 정보", IsSystem = true }
        });
        AssertRowWidths(viewportWidth);
        var oldRows = ChatOverlay.InspectRows();
        var oldHeight = oldRows[0].Height;
        ReadAboveBottom();
        ChatOverlay.SetViewportWidthForTest(380f);
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "normal", Sender = "Mina", Text = new string('가', 200) },
            new ChatUiMessage { Id = "system", Text = "참가 정보", IsSystem = true }
        });
        AssertRowWidths(380f);
        Same(oldRows[0].Root, ChatOverlay.InspectRows()[0].Root, "width change reuses normal row");
        Same(oldRows[1].Root, ChatOverlay.InspectRows()[1].Root, "width change reuses system row");
        True(ChatOverlay.InspectRows()[0].Height > oldHeight, "narrower width remeasures wrapping");
    }

    private static void AssertRowWidths(float width)
    {
        foreach (var row in ChatOverlay.InspectRows())
        {
            Near(width, row.Root.GetComponent<RectTransform>().rect.width, "row width");
            foreach (var text in row.Texts)
            {
                True(text.rectTransform.anchoredPosition.x + text.rectTransform.rect.width <= width + 0.01f,
                    $"{text.gameObject.name} extends into reserved scrollbar space");
                if (text.gameObject.name == "Body")
                    Near(width - 72f, text.rectTransform.rect.width, "body width");
                if (text.gameObject.name == "SystemMessage")
                    Near(width, text.rectTransform.rect.width, "system text width");
            }
        }
    }

    private static void ThumbRepresentsHistory()
    {
        foreach (var count in new[] { 0, 1, 10, 300 })
        {
            Prepare(count);
            var controls = ChatOverlay.InspectScrollControls();
            True(controls.Track.gameObject.activeInHierarchy, "visible track");
            var expectedHeight = Math.Clamp(502f * 502f / Math.Max(502f, ChatOverlay.GetContentHeight()), 28f, 502f);
            Near(expectedHeight, controls.Thumb.rect.height, $"thumb height for {count} rows");
            True(!controls.LatestButton.gameObject.activeSelf, "initial unread button visibility");
            foreach (var normalized in new[] { 0f, 0.5f, 1f })
            {
                ChatOverlay.SetScrollPosition(normalized);
                ChatOverlay.UpdateScrollControlsForTest();
                controls = ChatOverlay.InspectScrollControls();
                Near(-(1f - normalized) * (502f - expectedHeight), controls.Thumb.anchoredPosition.y, "thumb position");
            }
            if (count <= 1)
            {
                True(ChatOverlay.ClickScrollbarForTest(100f), "disabled track consumes pointer");
                True(!ChatOverlay.IsChatScrollbarDragging, "short history starts no drag");
                True(controls.ThumbColor.r < 0.3f, "short history visibly disabled");
            }
        }
    }

    private static void TrackClickAndDrag()
    {
        Prepare(300);
        var controls = ChatOverlay.InspectScrollControls();
        Near(28f, controls.Thumb.rect.height, "minimum thumb size");
        ChatOverlay.BeginOtherChatDragsForTest();
        True(ChatOverlay.ClickScrollbarForTest(251f), "track click was handled");
        True(!ChatOverlay.HasOtherChatDragForTest(), "track cancels viewport and window drags");
        Near(0.5f, ChatOverlay.GetScrollPosition(), "track click centers thumb");
        True(ChatOverlay.IsChatScrollbarDragging, "track click captures drag");
        True(!Input.MouseButtonHeld, "ResetInputAxes clears same-frame held state");
        ChatOverlay.UpdateScrollControlsForTest();
        True(ChatOverlay.IsChatScrollbarDragging, "same-frame axes reset must not cancel capture");
        ChatOverlay.DragScrollbarForTest(-100f);
        Near(1f, ChatOverlay.GetScrollPosition(), "drag beyond top clamps");
        ChatOverlay.DragScrollbarForTest(1000f);
        Near(0f, ChatOverlay.GetScrollPosition(), "drag beyond bottom clamps");
        ChatOverlay.DragScrollbarForTest(251f, held: false);
        True(!ChatOverlay.IsChatScrollbarDragging, "release cancels capture");

        ChatOverlay.SetScrollPosition(0.5f);
        ChatOverlay.UpdateScrollControlsForTest();
        controls = ChatOverlay.InspectScrollControls();
        var top = -controls.Thumb.anchoredPosition.y;
        ChatOverlay.ClickScrollbarForTest(top + 3f);
        Near(0.5f, ChatOverlay.GetScrollPosition(), "thumb grab does not jump");
        ChatOverlay.DragScrollbarForTest(top + 50f + 3f);
        Near(0.5f - 50f / (502f - 28f), ChatOverlay.GetScrollPosition(), "thumb keeps grab offset");
        True(!Input.BlockedMouseReadObserved, "manual drag read left mouse without bypass");
        True(!ChatOverlay.IsReadingOverlayInputForTest(), "drag leaked input read scope");
        ChatOverlay.EnterChatInputRead();
        try
        {
            ChatOverlay.DragScrollbarForTest(top + 60f + 3f);
            True(ChatOverlay.IsReadingOverlayInputForTest(), "drag lost enclosing input scope");
        }
        finally { ChatOverlay.ExitChatInputRead(); }
    }

    private static void DragLifecycle()
    {
        Prepare();
        ChatOverlay.ClickScrollbarForTest(100f);
        True(ChatOverlay.IsChatScrollbarDragging, "initial drag");
        ChatOverlay.CancelChatScrollControlDrag();
        True(!ChatOverlay.IsChatScrollbarDragging, "explicit cancellation");
        ChatOverlay.ClickScrollbarForTest(100f);
        ChatOverlay.CloseChatWindowForTest();
        True(!ChatOverlay.IsChatScrollbarDragging, "close cancels drag");
        True(!ChatOverlay.ClickScrollbarForTest(100f), "closed window rejects control click");

        Prepare();
        ReadAboveBottom();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 11).Select(Message));
        ChatOverlay.ClickScrollbarForTest(100f);
        var old = ChatOverlay.InspectScrollControls();
        ChatOverlay.ResetScrollControlsForTest();
        True(!ChatOverlay.IsChatScrollbarDragging, "cleanup cancels drag");
        True(old.Track.gameObject.IsDestroyed && old.LatestButton.gameObject.IsDestroyed, "cleanup releases control objects");
        ChatOverlay.RecreateScrollControlsForTest();
        var next = ChatOverlay.InspectScrollControls();
        True(!ReferenceEquals(old.Track, next.Track), "controls rebuilt after cleanup");
        Equal(0, next.UnreadCount, "cleanup clears unread");
        True(!next.LatestButton.gameObject.activeSelf, "clean rebuild hides latest button");
    }

    private static void UnreadAndLatest()
    {
        Prepare();
        ReadAboveBottom();
        var topBefore = TopOffset();
        var oldRows = ChatOverlay.InspectRows();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 12).Select(Message));
        Near(topBefore, TopOffset(), "new messages preserve reading position");
        for (var index = 0; index < oldRows.Count; index++)
            Same(oldRows[index].Root, ChatOverlay.InspectRows()[index].Root, "arrival reuses row");
        var controls = ChatOverlay.InspectScrollControls();
        Equal(2, controls.UnreadCount, "new message count");
        Equal("새로운 메시지", controls.LatestText, "Korean latest label");
        True(controls.LatestButton.gameObject.activeInHierarchy, "latest button visible");
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 13).Select(Message));
        Equal(3, ChatOverlay.InspectScrollControls().UnreadCount, "accumulated unread");
        var createdBefore = GameObject.CreatedCount;
        ChatOverlay.BeginOtherChatDragsForTest();
        True(ChatOverlay.ClickLatestForTest(), "latest click handled");
        True(!ChatOverlay.HasOtherChatDragForTest(), "latest cancels viewport and window drags");
        Near(0f, ChatOverlay.GetScrollPosition(), "latest jumps to bottom");
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "latest resets count");
        True(!controls.LatestButton.gameObject.activeSelf, "latest hides after click");
        Equal(createdBefore, GameObject.CreatedCount, "latest click recreates no rows");
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 14).Select(Message));
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "bottom follows without unread");
    }

    private static void PresentationUpdatesAreNotArrivals()
    {
        Prepare();
        ReadAboveBottom();
        var optimistic = new ChatUiMessage { Id = "local:a", ClientMessageId = "a", Sender = "Mina", Text = "새 메시지" };
        var messages = Enumerable.Range(0, 10).Select(Message).Append(optimistic).ToArray();
        ChatOverlay.SetChatMessages(messages);
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "optimistic counts once");
        var optimisticRow = ChatOverlay.InspectRows().Last().Root;
        messages[^1] = new ChatUiMessage { Id = "server:a", ClientMessageId = "a", Sender = "Mina", Text = "새 메시지" };
        ChatOverlay.SetChatMessages(messages);
        Same(optimisticRow, ChatOverlay.InspectRows().Last().Root, "echo keeps row");
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "echo does not increment");
        messages[^1] = new ChatUiMessage
        {
            Id = "server:a", ClientMessageId = "a", Sender = "Mina", Text = "새 메시지",
            CharacterId = "101", CharacterName = "갱신 캐릭터", Order = "P2"
        };
        ChatOverlay.SetChatMessages(messages);
        ChatOverlay.SetChatMessages(messages.Select(message => message).ToArray());
        ChatOverlay.RenderMessagesForTest();
        ChatOverlay.SetCdnSprite("101", new Sprite(new Texture()));
        ChatOverlay.RefreshPortraitsForTest(new ChatSnapshot { RoomId = "123456", ScreenPhase = "방" });
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "presentation does not increment");
    }

    private static void HistoryIsNotAnArrival()
    {
        Prepare();
        ReadAboveBottom();
        ChatOverlay.SetChatMessages(Enumerable.Range(-5, 15).Select(Message));
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "history prepend");
        ChatOverlay.SetChatMessages(Enumerable.Range(-5, 15).Select(Message));
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "same history replay");
        ChatOverlay.SetChatMessages(Enumerable.Range(-5, 16).Select(Message));
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "live append after history");

        ChatOverlay.SetChatStatus("채팅 서버 재연결 중");
        ChatOverlay.ObserveChatScrollConnectionStatus();
        // The connected status and JOINED replacement may arrive in one tick.
        ChatOverlay.SetChatStatus("연결됨");
        ChatOverlay.SetChatMessages(Enumerable.Range(-5, 20).Select(Message));
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "reconnect suffix is history");
        ChatOverlay.SetChatMessages(Enumerable.Range(-5, 21).Select(Message));
        Equal(2, ChatOverlay.InspectScrollControls().UnreadCount, "live append after reconnect");

        ChatOverlay.SetChatMessages(Enumerable.Range(50, 20).Select(Message));
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "non-overlapping history replacement");
        ReadAboveBottom();
        ChatOverlay.SetChatMessages(Enumerable.Range(50, 21).Select(Message));
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "live append after replacement");
    }

    private static void TrimRetainsReaderAndUnread()
    {
        Prepare(300);
        ChatOverlay.SetScrollPosition(1f - 5000f / (ChatOverlay.GetContentHeight() - 502f));
        var oldRows = ChatOverlay.InspectRows();
        ChatOverlay.SetChatMessages(Enumerable.Range(1, 300).Select(Message));
        Near(5000f - 96f, TopOffset(), "trim preserves top offset");
        Same(oldRows[1].Root, ChatOverlay.InspectRows()[0].Root, "trim reuses next row");
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "trim counts appended message");
        ChatOverlay.SetChatMessages(Enumerable.Range(2, 300).Select(Message));
        Equal(2, ChatOverlay.InspectScrollControls().UnreadCount, "second trim accumulates unread");
        ChatOverlay.SetScrollPosition(1f);
        for (var start = 3; start <= 302; start++)
        {
            ChatOverlay.SetScrollPosition(1f);
            ChatOverlay.SetChatMessages(Enumerable.Range(start, 300).Select(Message));
        }
        Equal(300, ChatOverlay.InspectScrollControls().UnreadCount, "trim removes unread identities outside retained history");
        Equal(300, ChatOverlay.InspectRows().Count, "rows remain bounded");
    }

    private static void RoomAndEmptyHistoryReset()
    {
        Prepare();
        ChatOverlay.RefreshPortraitsForTest(new ChatSnapshot { RoomId = "123456", ScreenPhase = "방" });
        ReadAboveBottom();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 11).Select(Message));
        ChatOverlay.ClickScrollbarForTest(100f);
        ChatOverlay.RefreshPortraitsForTest(new ChatSnapshot { RoomId = "654321", ScreenPhase = "방" });
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "room change clears unread");
        True(!ChatOverlay.IsChatScrollbarDragging, "room change cancels capture");
        Near(0f, ChatOverlay.GetScrollPosition(), "room change returns to latest");
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 12).Select(Message));
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "room baseline is not new");
        ReadAboveBottom();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 13).Select(Message));
        Equal(1, ChatOverlay.InspectScrollControls().UnreadCount, "live message in new room");
        ChatOverlay.SetChatMessages(Array.Empty<ChatUiMessage>());
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "empty clears unread");
        Near(502f, ChatOverlay.InspectScrollControls().Thumb.rect.height, "empty history disabled thumb");
    }

    private static void BottomClearsNotification()
    {
        foreach (var distance in new[] { 31f, 32f, 33f, 200f })
        {
            Prepare();
            ReadAboveBottom(distance);
            var topBefore = TopOffset();
            ChatOverlay.SetChatMessages(Enumerable.Range(0, 11).Select(Message));
            Equal(distance <= 32f ? 0 : 1, ChatOverlay.InspectScrollControls().UnreadCount, "32-unit following threshold");
            if (distance > 32f)
                Near(topBefore, TopOffset(), "reader outside threshold stays put");
            ChatOverlay.WheelForTest(-10000f);
            ChatOverlay.UpdateScrollControlsForTest();
            Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "wheel to bottom clears unread");
        }
        Prepare();
        ReadAboveBottom();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 11).Select(Message));
        ChatOverlay.SetScrollPosition(32f / (ChatOverlay.GetContentHeight() - 502f));
        ChatOverlay.UpdateScrollControlsForTest();
        Equal(0, ChatOverlay.InspectScrollControls().UnreadCount, "manual scroll to bottom threshold clears unread");
    }

    private static void True(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}");
    }

    private static void Near(float expected, float actual, string description)
    {
        _assertions++;
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > 0.05f)
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}");
    }

    private static void Same(object expected, object actual, string description)
    {
        _assertions++;
        if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException(description);
    }
}
#endif
