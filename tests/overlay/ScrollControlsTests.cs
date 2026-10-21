#if OVERLAY_HARNESS
using System;
using System.Linq;
using AstralPartyChatPlugin;
using UnityEngine;

internal static class ScrollControlsTests
{
    internal static void Run()
    {
        Check.Run("scroll rows and text reserve FairyGUI scrollbar space", ReservedWidthIsConsistent);
        Check.Run("wheel travels 100 units per notch independent of history length", WheelDistanceIsFixed);
        Check.Run("wheel clamps boundaries and preserves fractional notches", WheelBoundariesAndFractions);
        Check.Run("wheel ignores absent targets, outside controls and settings", WheelRequiresReadingTarget);
        Check.Run("32-unit bottom following includes nonoverflowing history", BottomFollowingThreshold);
        Check.Run("thumb is proportional with a 28-unit minimum and full empty track", ThumbRepresentsHistory);
        Check.Run("track clicks center thumb and held drags retain grab offset", TrackClickAndDrag);
        Check.Run("scrollbar and viewport capture end on release, close and room change", DragLifecycle);
        Check.Run("viewport dragging preserves its starting reader offset", ViewportDrag);
        Check.Run("arrivals retain rows and reader position until latest is clicked", UnreadAndLatest);
        Check.Run("wheel and viewport drag clear unread within bottom threshold", ReachingBottomClearsUnread);
        Check.Run("acknowledgement, metadata and portrait refresh are not arrivals", PresentationUpdatesAreNotArrivals);
        Check.Run("prepend, replay, reconnect and replacement establish history baselines", HistoryIsNotAnArrival);
        Check.Run("300-row trim preserves retained rows, reader offset and available unread", TrimRetainsReaderAndUnread);
        Check.Run("historical metadata replacement preserves the reader anchor", HistoricalMetadataKeepsReaderAnchor);
        Check.Run("trim plus first-survivor replacement preserves reader offset and unread", TrimAndMetadataKeepsReaderAnchor);
        Check.Run("height changes above the reader preserve content while latest follows", HeightChangeKeepsReaderAnchor);
        Check.Run("trimming the anchored row selects its first surviving successor", TrimmedAnchorSelectsFollowingRow);
        Check.Run("room changes and empty histories reset scroll notifications", RoomAndEmptyHistoryReset);
        Check.Run("height-only resize recomputes short content and preserves bottom following", HeightResizeFollowsBottom);
        Check.Run("only message rows intersecting the viewport remain visible", RowVisibilityFollowsViewport);
    }

    private static ChatUiMessage Message(int index, string? text = null, string name = "캐릭터") => new()
    {
        Id = $"scroll-{index:D4}", Sender = "Mina", CharacterId = "101",
        CharacterName = name, Order = "P1", Text = text ?? $"메시지 {index}"
    };

    private static void Prepare(int count = 20)
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatStatus("연결됨");
        SetMessages(0, count);
    }

    private static void SetMessages(int start, int count)
    {
        ChatOverlay.SetChatMessages(Enumerable.Range(start, count).Select(index => Message(index)));
        ChatOverlay.Tick();
    }

    private static Vector2 Position(FguiNode node, float x = 0f, float y = 0f)
    {
        for (FguiNode? current = node; current != null && !ReferenceEquals(current, ChatOverlay.TestRoot); current = current.Parent)
        {
            x += current.X;
            y += current.Y;
        }
        return new Vector2(x, y);
    }

    private static FguiNode Thumb => ChatOverlay.Node("ScrollTrack").Children[1];

    private static void ReadAboveBottom(float distance = 200f) =>
        ChatOverlay.ScrollTop = Math.Max(0f, ChatOverlay.Scrollable - distance);

    private static void Wheel(float notches, FguiNode? target = null) =>
        ChatOverlay.Tick(target: target ?? ChatOverlay.Node("Viewport"), wheel: notches);

    private static void Press(FguiNode target, Vector2 pointer) =>
        ChatOverlay.Tick(target: target, pointer: pointer, pressed: true, held: true);

    private static void Same(object expected, object actual, string description) =>
        Check.True(ReferenceEquals(expected, actual), description);

    private static void ReservedWidthIsConsistent()
    {
        Prepare();
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "normal", Sender = "Mina", Text = new string('가', 200) },
            new ChatUiMessage { Id = "system", Text = "참가 정보", IsSystem = true }
        });
        ChatOverlay.Tick();
        AssertRowWidths();
        var rows = ChatOverlay.InspectRows();
        var oldWidth = ChatOverlay.ViewportWidth;
        var resize = ChatOverlay.Node("Resize");
        var start = Position(resize, resize.Width * 0.5f, resize.Height * 0.5f);
        Press(resize, start);
        var narrower = new Vector2(start.x - 120f, start.y);
        ChatOverlay.Tick(pointer: narrower, held: true);
        ChatOverlay.Tick(pointer: narrower);
        Check.Near(oldWidth - 120f, ChatOverlay.ViewportWidth, "resize narrows the viewport");
        AssertRowWidths();
        var resized = ChatOverlay.InspectRows();
        Same(rows[0].Root, resized[0].Root, "resize retains ordinary row");
        Same(rows[1].Root, resized[1].Root, "resize retains system row");
        Check.True(resized[0].Height > rows[0].Height, "narrower viewport remeasures wrapped text");
    }

    private static void AssertRowWidths()
    {
        var viewport = ChatOverlay.Node("Viewport");
        var track = ChatOverlay.Node("ScrollTrack");
        Check.Near(viewport.X + ChatOverlay.ViewportWidth + 8f, track.X, "reserved scrollbar gap");
        Check.Near(12f, track.Width, "track width");
        foreach (var row in ChatOverlay.InspectRows())
        {
            Check.Near(ChatOverlay.ViewportWidth, row.Width, "measured row width");
            Check.Near(ChatOverlay.ViewportWidth, row.Root.Width, "rendered row width");
            foreach (var text in row.Texts)
                Check.True(text.X + text.Width <= ChatOverlay.ViewportWidth + 0.05f, "text stays outside scrollbar gutter");
            Check.Near(row.Participant == null ? ChatOverlay.ViewportWidth : ChatOverlay.ViewportWidth - 72f,
                row.Body.Width, "body uses available reading width");
        }
    }

    private static void WheelDistanceIsFixed()
    {
        foreach (var count in new[] { 20, 100, 300 })
        {
            Prepare(count);
            ChatOverlay.ScrollTop = 400f;
            Wheel(1f);
            Check.Near(300f, ChatOverlay.ScrollTop, $"one notch up with {count} rows");
            Wheel(-1f);
            Check.Near(400f, ChatOverlay.ScrollTop, $"one notch down with {count} rows");
            // Text is a non-touchable descendant, so routing must find the viewport ancestor.
            Wheel(1f, ChatOverlay.InspectRows()[0].Body);
            Check.Near(300f, ChatOverlay.ScrollTop, "row descendant routes to viewport");
        }
    }

    private static void WheelBoundariesAndFractions()
    {
        Prepare();
        ChatOverlay.ScrollTop = 200f;
        Wheel(0.25f);
        Check.Near(175f, ChatOverlay.ScrollTop, "fractional upward notch");
        Wheel(-0.5f);
        Check.Near(225f, ChatOverlay.ScrollTop, "fractional downward notch");
        Wheel(0.001f);
        Check.Near(225f, ChatOverlay.ScrollTop, "negligible delta ignored");
        Wheel(10000f);
        Check.Near(0f, ChatOverlay.ScrollTop, "upper boundary");
        Wheel(-10000f);
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "lower boundary");
        Prepare(1);
        Wheel(1f);
        Wheel(-1f);
        Check.Near(0f, ChatOverlay.ScrollTop, "nonoverflowing wheel remains stationary");
    }

    private static void WheelRequiresReadingTarget()
    {
        Prepare();
        ChatOverlay.ScrollTop = 400f;
        ChatOverlay.Tick(wheel: 1f);
        Check.Near(400f, ChatOverlay.ScrollTop, "no touch target");
        foreach (var control in new[] { "Header", "Input", "Send", "Button" })
        {
            ChatOverlay.Tick(target: ChatOverlay.Node(control), wheel: 1f);
            Check.Near(400f, ChatOverlay.ScrollTop, $"outside viewport: {control}");
        }
        Wheel(1f, ChatOverlay.Node("ScrollTrack"));
        Check.Near(300f, ChatOverlay.ScrollTop, "track accepts wheel");
        ChatOverlay.Click("Settings");
        foreach (var control in new[] { "SettingsPanel", "Viewport", "ScrollTrack", "Latest" })
        {
            ChatOverlay.Tick(target: ChatOverlay.Node(control), wheel: 1f);
            Check.Near(300f, ChatOverlay.ScrollTop, $"settings block wheel on {control}");
        }
        ChatOverlay.Click("Settings");
        Wheel(1f);
        Check.Near(200f, ChatOverlay.ScrollTop, "wheel resumes after settings close");
    }

    private static void BottomFollowingThreshold()
    {
        foreach (var distance in new[] { 0f, 31f, 32f, 33f, 200f })
        {
            Prepare();
            ReadAboveBottom(distance);
            var top = ChatOverlay.ScrollTop;
            SetMessages(0, 21);
            Check.Equal(distance <= 32f ? 0 : 1, ChatOverlay.Unread, "inclusive 32-unit arrival threshold");
            Check.Near(distance <= 32f ? ChatOverlay.Scrollable : top, ChatOverlay.ScrollTop,
                "follow only when previously near bottom");
        }
        Prepare(1);
        Check.Near(0f, ChatOverlay.Scrollable, "short history has no overflow");
        SetMessages(0, 20);
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "nonoverflowing history follows when it first overflows");
        Check.Equal(0, ChatOverlay.Unread, "first overflow is already read");
    }

    private static void ResizeHeight(float delta)
    {
        var grip = ChatOverlay.Node("Resize");
        var start = Position(grip, 10f, 10f);
        Press(grip, start);
        var end = start + new Vector2(0f, delta);
        ChatOverlay.Tick(pointer: end, held: true);
        ChatOverlay.Tick(pointer: end);
    }

    private static void HeightResizeFollowsBottom()
    {
        Prepare(1);
        var oldHeight = ChatOverlay.ViewportHeight;
        ResizeHeight(-120f);
        Check.Near(oldHeight - 120f, ChatOverlay.ViewportHeight, "height-only resize shrinks viewport");
        Check.Near(ChatOverlay.ViewportHeight, ChatOverlay.ContentHeight, "short history content follows viewport height");
        Check.Near(0f, ChatOverlay.Scrollable, "short history has no stale scroll range");
        Check.Near(ChatOverlay.ContentHeight, ChatOverlay.Node("Viewport").Children.Single().Height, "native content size refreshed");

        Prepare();
        ResizeHeight(-120f);
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "overflowing history stays at bottom after shrinking");
        SetMessages(0, 21);
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "next arrival still follows after height-only resize");
        Check.Equal(0, ChatOverlay.Unread, "followed arrival is not unread");
        ChatOverlay.Click("Settings");
        ChatOverlay.Click("ResetLayout");
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "reset layout preserves bottom-follow");
        Check.Equal(0, ChatOverlay.Unread, "reset introduces no unread messages");
    }

    private static void RowVisibilityFollowsViewport()
    {
        Prepare(100);
        void AssertVisibility()
        {
            var bottom = ChatOverlay.ScrollTop + ChatOverlay.ViewportHeight;
            var rows = ChatOverlay.InspectRows();
            foreach (var row in rows)
                Check.Equal(row.Root.Y + row.Height >= ChatOverlay.ScrollTop && row.Root.Y <= bottom,
                    row.Root.Visible, "row visibility matches viewport intersection");
            Check.True(rows.Any(row => row.Root.Visible) && rows.Any(row => !row.Root.Visible),
                "long history has visible and culled rows");
        }
        AssertVisibility();
        ChatOverlay.ScrollTop = 0f;
        AssertVisibility();
        ChatOverlay.ScrollTop = ChatOverlay.Scrollable * 0.5f;
        AssertVisibility();
        ResizeHeight(-120f);
        AssertVisibility();
        // The first row is culled here, but must still reflow at a new width.
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 100).Select(index =>
            index == 0 ? new ChatUiMessage { Id = Message(0).Id, Sender = "Mina", Text = new string('가', 600) } : Message(index)));
        var first = ChatOverlay.InspectRows()[0];
        var grip = ChatOverlay.Node("Resize");
        var start = Position(grip, 10f, 10f);
        Press(grip, start);
        var end = start + new Vector2(-100f, 0f);
        ChatOverlay.Tick(pointer: end, held: true);
        ChatOverlay.Tick(pointer: end);
        Check.True(ChatOverlay.InspectRows()[0].Height > first.Height, "culled row is still measured during reflow");
        AssertVisibility();
    }

    private static void ThumbRepresentsHistory()
    {
        foreach (var count in new[] { 0, 1, 20, 300 })
        {
            Prepare(count);
            var track = ChatOverlay.Node("ScrollTrack");
            var expected = Math.Min(ChatOverlay.ViewportHeight,
                Math.Max(28f, ChatOverlay.ViewportHeight * ChatOverlay.ViewportHeight / ChatOverlay.ContentHeight));
            Check.Near(ChatOverlay.ViewportHeight, track.Height, "track fills viewport");
            Check.Near(expected, Thumb.Height, $"proportional thumb for {count} rows");
            foreach (var fraction in new[] { 0f, 0.5f, 1f })
            {
                ChatOverlay.ScrollTop = ChatOverlay.Scrollable * fraction;
                Check.Near((track.Height - expected) * fraction, Thumb.Y, "thumb represents top offset");
            }
            if (count <= 1)
            {
                Press(track, Position(track, 6f, track.Height * 0.5f));
                Check.True(!ChatOverlay.ScrollbarDragging, "full thumb starts no drag");
                Check.Near(track.Height, Thumb.Height, "empty and short histories use full thumb");
                Check.True(Thumb.FillColor.r < 0.3f, "idle thumb is visibly disabled");
            }
            if (count == 300)
                Check.Near(28f, Thumb.Height, "long history reaches minimum thumb height");
            Check.True(!ChatOverlay.Node("Latest").Visible, "baseline has no latest notification");
        }
    }

    private static void TrackClickAndDrag()
    {
        Prepare(300);
        var track = ChatOverlay.Node("ScrollTrack");
        Press(track, Position(track, 6f, track.Height * 0.5f));
        Check.True(ChatOverlay.ScrollbarDragging, "track press captures held pointer");
        Check.Near(ChatOverlay.Scrollable * 0.5f, ChatOverlay.ScrollTop, "outside-thumb click centers thumb");
        ChatOverlay.Tick(pointer: Position(track, 6f, -100f), held: true);
        Check.Near(0f, ChatOverlay.ScrollTop, "captured drag outside track clamps at top");
        ChatOverlay.Tick(pointer: Position(track, 6f, track.Height + 100f), held: true);
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "captured drag outside track clamps at bottom");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.ScrollbarDragging, "release ends capture");
        ChatOverlay.ScrollTop = ChatOverlay.Scrollable * 0.5f;
        var thumbTop = Thumb.Y;
        Press(Thumb, Position(track, 6f, thumbTop + 3f));
        Check.Near(ChatOverlay.Scrollable * 0.5f, ChatOverlay.ScrollTop, "grabbing thumb preserves position");
        ChatOverlay.Tick(pointer: Position(track, 6f, thumbTop + 53f), held: true);
        Check.Near(ChatOverlay.Scrollable * (0.5f + 50f / (track.Height - Thumb.Height)),
            ChatOverlay.ScrollTop, "held drag preserves three-unit grab offset");
    }

    private static void DragLifecycle()
    {
        foreach (var control in new[] { "ScrollTrack", "Viewport" })
        foreach (var end in new[] { "release", "close", "room" })
        {
            Prepare();
            var node = ChatOverlay.Node(control);
            Press(node, Position(node, 6f, node.Height * 0.5f));
            Check.True(control == "ScrollTrack" ? ChatOverlay.ScrollbarDragging : ChatOverlay.ViewportDragging,
                $"{control} captures pointer");
            if (end == "release") ChatOverlay.Tick();
            else if (end == "close") ChatOverlay.Click("Close");
            else
            {
                ChatOverlay.TestSnapshot.RoomId = "other-room";
                ChatOverlay.Tick(pointer: Position(node, 6f, node.Height * 0.5f), held: true);
            }
            Check.True(!ChatOverlay.ScrollbarDragging && !ChatOverlay.ViewportDragging,
                $"{end} cancels both scroll captures");
            var top = ChatOverlay.ScrollTop;
            ChatOverlay.Tick(pointer: new Vector2(0f, 0f), held: true);
            Check.Near(top, ChatOverlay.ScrollTop, "held pointer cannot revive cancelled capture");
        }
    }

    private static void ViewportDrag()
    {
        Prepare();
        ChatOverlay.ScrollTop = 400f;
        var viewport = ChatOverlay.Node("Viewport");
        var start = Position(viewport, 40f, 200f);
        Press(ChatOverlay.InspectRows()[0].Body, start);
        Check.True(ChatOverlay.ViewportDragging && !ChatOverlay.ScrollbarDragging, "body press captures viewport only");
        ChatOverlay.Tick(pointer: new Vector2(start.x, start.y - 125f), held: true);
        Check.Near(525f, ChatOverlay.ScrollTop, "upward pointer moves reading offset down");
        ChatOverlay.Tick(pointer: new Vector2(start.x, start.y + 50f), held: true);
        Check.Near(350f, ChatOverlay.ScrollTop, "drag uses original offset, not accumulated deltas");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.ViewportDragging, "viewport release");
    }

    private static void UnreadAndLatest()
    {
        Prepare();
        ReadAboveBottom();
        var top = ChatOverlay.ScrollTop;
        var rows = ChatOverlay.InspectRows();
        SetMessages(0, 22);
        Check.Near(top, ChatOverlay.ScrollTop, "arrivals preserve reader offset");
        for (var index = 0; index < rows.Length; index++)
            Same(rows[index].Root, ChatOverlay.InspectRows()[index].Root, "arrival retains existing row");
        Check.Equal(2, ChatOverlay.Unread, "two appended arrivals");
        var latest = ChatOverlay.Node("Latest");
        Check.True(latest.Visible, "latest notification visible");
        Check.True(latest.Children.Any(child => child.GetText() == "새로운 메시지"), "Korean latest label");
        SetMessages(0, 23);
        Check.Equal(3, ChatOverlay.Unread, "unread accumulates");
        rows = ChatOverlay.InspectRows();
        ChatOverlay.Click("Latest");
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "latest jumps to bottom");
        Check.Equal(0, ChatOverlay.Unread, "latest clears unread");
        Check.True(!latest.Visible && !ChatOverlay.ScrollbarDragging && !ChatOverlay.ViewportDragging,
            "latest hides and leaves no captured scroll");
        for (var index = 0; index < rows.Length; index++)
            Same(rows[index].Root, ChatOverlay.InspectRows()[index].Root, "latest does not rebuild rows");
        SetMessages(0, 24);
        Check.Equal(0, ChatOverlay.Unread, "bottom follows subsequent arrival");
    }

    private static void ReachingBottomClearsUnread()
    {
        Prepare();
        ReadAboveBottom();
        SetMessages(0, 21);
        Check.Equal(1, ChatOverlay.Unread, "unread before wheel");
        Wheel(-10000f);
        Check.Equal(0, ChatOverlay.Unread, "wheel to bottom clears unread");
        ReadAboveBottom();
        SetMessages(0, 22);
        var viewport = ChatOverlay.Node("Viewport");
        var start = Position(viewport, 40f, 200f);
        var movement = ChatOverlay.Scrollable - 32f - ChatOverlay.ScrollTop;
        Press(viewport, start);
        ChatOverlay.Tick(pointer: new Vector2(start.x, start.y - movement), held: true);
        Check.Near(ChatOverlay.Scrollable - 32f, ChatOverlay.ScrollTop, "drag reaches inclusive bottom threshold");
        Check.Equal(0, ChatOverlay.Unread, "viewport drag clears unread at threshold");
        Check.True(!ChatOverlay.Node("Latest").Visible, "threshold hides notification");
    }

    private static void PresentationUpdatesAreNotArrivals()
    {
        Prepare();
        ReadAboveBottom();
        var messages = Enumerable.Range(0, 20).Select(index => Message(index)).Append(new ChatUiMessage
        {
            Id = "local:a", ClientMessageId = "a", Sender = "Mina", Text = "새 메시지"
        }).ToArray();
        ChatOverlay.SetChatMessages(messages);
        ChatOverlay.Tick();
        Check.Equal(1, ChatOverlay.Unread, "optimistic arrival counts once");
        var optimistic = ChatOverlay.InspectRows().Last();
        messages[^1] = new ChatUiMessage
        {
            Id = "server:a", ClientMessageId = "a", Sender = "Mina", Text = "새 메시지"
        };
        ChatOverlay.SetChatMessages(messages);
        ChatOverlay.Tick();
        Same(optimistic.Root, ChatOverlay.InspectRows().Last().Root, "ack retains optimistic row");
        Check.Equal(1, ChatOverlay.Unread, "ack is not another arrival");
        messages[^1] = new ChatUiMessage
        {
            Id = "server:a", ClientMessageId = "a", Sender = "Mina", Text = "새 메시지",
            CharacterId = "101", CharacterName = "갱신 캐릭터", Order = "P2"
        };
        ChatOverlay.SetChatMessages(messages);
        ChatOverlay.Tick();
        var updated = ChatOverlay.InspectRows().Last();
        Check.Equal(optimistic.Identity, updated.Identity, "metadata keeps message identity");
        Check.True(optimistic.Root.IsDisposed, "obsolete presentation row disposed");
        ChatOverlay.SetChatMessages(messages.ToArray());
        ChatOverlay.DeliverPortrait("101", new Texture());
        Time.unscaledTime += 3f;
        ChatOverlay.Tick();
        Check.Equal(1, ChatOverlay.Unread, "metadata, replay and portrait reconciliation do not increment unread");
    }

    private static void HistoryIsNotAnArrival()
    {
        Prepare();
        ReadAboveBottom();
        SetMessages(-5, 25);
        Check.Equal(0, ChatOverlay.Unread, "prepend is not arrival");
        SetMessages(-5, 25);
        Check.Equal(0, ChatOverlay.Unread, "same history replay");
        SetMessages(-5, 26);
        Check.Equal(1, ChatOverlay.Unread, "live append after prepend");
        ChatOverlay.SetChatStatus("채팅 서버 재연결 중");
        ChatOverlay.Tick();
        ChatOverlay.SetChatStatus("연결됨");
        SetMessages(-5, 30);
        Check.Equal(1, ChatOverlay.Unread, "overlapping reconnect suffix is baseline history");
        SetMessages(-5, 31);
        Check.Equal(2, ChatOverlay.Unread, "live append after reconnect counts");
        SetMessages(100, 20);
        Check.Equal(0, ChatOverlay.Unread, "non-overlapping replacement resets baseline");
        ReadAboveBottom();
        SetMessages(100, 21);
        Check.Equal(1, ChatOverlay.Unread, "live append after replacement counts");
    }

    private static void TrimRetainsReaderAndUnread()
    {
        Prepare(300);
        ChatOverlay.ScrollTop = 5000f;
        var rows = ChatOverlay.InspectRows();
        var removedHeight = rows[1].Root.Y - rows[0].Root.Y;
        SetMessages(0, 301);
        Check.Equal(300, ChatOverlay.InspectRows().Length, "production bounds oversized input to 300 rows");
        Check.Near(5000f - removedHeight, ChatOverlay.ScrollTop, "leading trim preserves reader offset");
        Same(rows[1].Root, ChatOverlay.InspectRows()[0].Root, "trim reuses first retained row");
        Check.True(rows[0].Root.IsDisposed, "trim disposes removed row");
        Check.Equal(1, ChatOverlay.Unread, "trim counts appended message");
        SetMessages(2, 300);
        Check.Equal(2, ChatOverlay.Unread, "second trim accumulates unread");
        for (var start = 3; start <= 302; start++)
        {
            ChatOverlay.ScrollTop = 0f;
            SetMessages(start, 300);
        }
        Check.Equal(300, ChatOverlay.Unread, "trim drops unread identities no longer in retained history");
        Check.Equal(300, ChatOverlay.InspectRows().Length, "row bound survives repeated trims");
    }

    private static void HistoricalMetadataKeepsReaderAnchor()
    {
        Prepare(300);
        ChatOverlay.ScrollTop = 5000f;
        var old = ChatOverlay.InspectRows();
        var anchor = old.Last(row => row.Root.Y <= ChatOverlay.ScrollTop);
        var pixelOffset = ChatOverlay.ScrollTop - anchor.Root.Y;
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 300)
            .Select(index => Message(index, name: index == 0 ? "갱신 캐릭터" : "캐릭터")));
        var next = ChatOverlay.InspectRows();
        Check.True(old[0].Root.IsDisposed, "historical metadata replaces its obsolete presentation");
        Check.Near(old[0].Height, next[0].Height, "metadata replacement keeps the same row height");
        Check.Near(5000f, ChatOverlay.ScrollTop, "presentation replacement is not a leading history removal");
        var nextAnchor = next.Single(row => row.Identity == anchor.Identity);
        Same(anchor.Root, nextAnchor.Root, "metadata above the reader retains the visible row");
        Check.Near(pixelOffset, ChatOverlay.ScrollTop - nextAnchor.Root.Y, "metadata preserves the anchor pixel offset");
        Check.Equal(0, ChatOverlay.Unread, "historical metadata introduces no unread arrivals");
    }

    private static void TrimAndMetadataKeepsReaderAnchor()
    {
        Prepare(300);
        ChatOverlay.ScrollTop = 5000f;
        var old = ChatOverlay.InspectRows();
        var removedHeight = old[3].Root.Y - old[0].Root.Y;
        var anchor = old.Last(row => row.Root.Y <= ChatOverlay.ScrollTop);
        var pixelOffset = ChatOverlay.ScrollTop - anchor.Root.Y;
        ChatOverlay.SetChatMessages(Enumerable.Range(3, 300)
            .Select(index => Message(index, name: index == 3 ? "갱신 캐릭터" : "캐릭터")));
        var next = ChatOverlay.InspectRows();
        Check.True(old.Take(3).All(row => row.Root.IsDisposed), "trimmed history rows are disposed");
        Check.True(old[3].Root.IsDisposed, "first surviving message replaces only its presentation");
        Check.Equal(old[3].Identity, next[0].Identity, "first survivor keeps its logical identity");
        Check.Near(old[3].Height, next[0].Height, "survivor metadata does not alter row height");
        Same(old[4].Root, next[1].Root, "unaffected survivor remains reused");
        Check.Near(5000f - removedHeight, ChatOverlay.ScrollTop, "only actual trimmed history shifts the reader offset");
        var nextAnchor = next.Single(row => row.Identity == anchor.Identity);
        Check.Near(pixelOffset, ChatOverlay.ScrollTop - nextAnchor.Root.Y, "trim and replacement preserve the anchor offset");
        Check.Equal(3, ChatOverlay.Unread, "three appended messages remain unread");
    }

    private static void HeightChangeKeepsReaderAnchor()
    {
        foreach (var followLatest in new[] { false, true })
        {
            Prepare(300);
            if (!followLatest)
                ChatOverlay.ScrollTop = 5000f;
            var tallText = string.Join("\n", Enumerable.Repeat("measured line", 12));
            foreach (var text in new[] { tallText, "메시지 0" })
            {
                var old = ChatOverlay.InspectRows();
                var previousTop = ChatOverlay.ScrollTop;
                var anchor = old.Last(row => row.Root.Y <= previousTop);
                var pixelOffset = previousTop - anchor.Root.Y;
                ChatOverlay.SetChatMessages(Enumerable.Range(0, 300)
                    .Select(index => Message(index, text: index == 0 ? text : null)));
                var next = ChatOverlay.InspectRows();
                Check.True(Math.Abs(next[0].Height - old[0].Height) > 1f, "historical replacement changes measured height");
                Check.True(old[0].Root.IsDisposed, "height-changing presentation is replaced");
                if (followLatest)
                    Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "latest followers stay at the bottom after height changes");
                else
                {
                    var nextAnchor = next.Single(row => row.Identity == anchor.Identity);
                    Same(anchor.Root, nextAnchor.Root, "height changes above the viewport retain the visible row");
                    Check.Near(previousTop + next[0].Height - old[0].Height, ChatOverlay.ScrollTop,
                        "reader offset follows the measured height delta above it");
                    Check.Near(pixelOffset, ChatOverlay.ScrollTop - nextAnchor.Root.Y,
                        "growing and shrinking historical rows preserve the visible pixel offset");
                }
                Check.Equal(0, ChatOverlay.Unread, "height-only replacements are not arrivals");
            }
        }
    }

    private static void TrimmedAnchorSelectsFollowingRow()
    {
        Prepare(300);
        var old = ChatOverlay.InspectRows();
        ChatOverlay.ScrollTop = old[2].Root.Y + 24f;
        ChatOverlay.SetChatMessages(Enumerable.Range(3, 300)
            .Select(index => Message(index, name: index == 3 ? "갱신 캐릭터" : "캐릭터")));
        var next = ChatOverlay.InspectRows();
        Check.True(old[2].Root.IsDisposed, "the previous reader anchor was trimmed");
        Check.Equal(old[3].Identity, next[0].Identity, "the first following message survives presentation replacement");
        Check.Near(next[0].Root.Y, ChatOverlay.ScrollTop, "the first surviving successor becomes the top reading anchor");
        Check.Equal(3, ChatOverlay.Unread, "choosing a replacement anchor preserves new-arrival notifications");
    }

    private static void RoomAndEmptyHistoryReset()
    {
        Prepare();
        ReadAboveBottom();
        SetMessages(0, 21);
        Check.Equal(1, ChatOverlay.Unread, "unread in original room");
        ChatOverlay.TestSnapshot.RoomId = "654321";
        ChatOverlay.Tick();
        Check.Equal(0, ChatOverlay.Unread, "room transition clears unread");
        Check.True(!ChatOverlay.Node("Latest").Visible, "room transition hides latest");
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "room transition jumps to latest");
        ReadAboveBottom();
        SetMessages(0, 22);
        Check.Equal(0, ChatOverlay.Unread, "first overlapping room history is not a live arrival");
        SetMessages(0, 23);
        Check.Equal(1, ChatOverlay.Unread, "subsequent arrival in new room counts");
        SetMessages(0, 0);
        Check.Equal(0, ChatOverlay.Unread, "empty history clears unread");
        Check.Equal(0, ChatOverlay.InspectRows().Length, "empty history removes rows");
        Check.Near(0f, ChatOverlay.ScrollTop, "empty history resets offset");
        Check.Near(ChatOverlay.ViewportHeight, Thumb.Height, "empty history fills track");
        Check.True(!ChatOverlay.Node("Latest").Visible, "empty history hides latest");
    }
}
#endif
