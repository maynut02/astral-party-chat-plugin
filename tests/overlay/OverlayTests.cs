#if OVERLAY_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AstralPartyChatPlugin;
using UnityEngine;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

internal static class OverlayTests
{
    private static int _passed;
    private static int _assertions;

    private static int Main()
    {
        Run("300-row shift retains rows and releases only the dropped texts", ShiftRetainsRowsAndBoundsTextReferences);
        Run("optimistic acknowledgement keeps the rendered row", OptimisticAcknowledgementKeepsIdentity);
        Run("participant metadata change replaces only its row", ParticipantChangeReplacesOnlyAffectedRow);
        Run("row height follows measured multiline preferred height", MultilineUsesPreferredHeight);
        Run("trim preserves the reader's top offset", TrimPreservesScrollOffset);
        Run("non-battle CDN retry updates a portrait in place", NonBattleCdnRetryUpdatesInPlace);
        Run("invalidated game portrait falls back to CDN in place", InvalidatedGamePortraitFallsBack);
        Run("display truncation counts Unicode code points", DisplayTruncationUsesUnicodeCodePoints);
        Run("Enter and send button reject over-limit drafts without changing text", OverLimitDraftIsPreserved);
        Run("1000 code points send intact through Enter and the send button", CodePointBoundarySendsIntact);
        Run("draft feedback survives connection status updates and clears on correction", DraftFeedbackFollowsEditing);
        Run("unchanged spaced Unicode drafts avoid repeated validation allocations", UnchangedDraftAvoidsValidationAllocations);
        Run("explicit IME send validates only the final committed draft", ImeSendValidatesCommittedDraft);
        Run("one IME Enter waits for committed text and sends once", ImeEnterSendsCommittedText);
        Run("wheel moves the same UI distance for short and long histories", WheelDistanceDoesNotDependOnHistory);
        Run("wheel handles fractions, boundaries, empty content and pointer location", WheelRespectsBoundsAndViewport);
        Run("bottom following uses a 32 UI-unit distance for any history length", BottomFollowingUsesDistance);
        Run("non-overflowing history follows the bottom when it begins overflowing", NonOverflowingHistoryFollowsBottom);

        Console.WriteLine($"{_passed} overlay regression checks passed ({_assertions} assertions).");
        ScrollControlsTests.Run();
        KeyboardInputTests.Run();
        return 0;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void ShiftRetainsRowsAndBoundsTextReferences()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 300).Select(CreateMessage));

        var oldRows = ChatOverlay.InspectRows();
        var oldTexts = ChatOverlay.InspectChatTexts();
        var destroyedBefore = UnityObject.DestroyCallCount;
        var droppedTexts = new HashSet<Text>(oldRows[0].Texts, ReferenceComparer<Text>.Instance);
        var retainedTexts = new HashSet<Text>(
            oldRows.Skip(1).SelectMany(row => row.Texts),
            ReferenceComparer<Text>.Instance);

        ChatOverlay.SetChatMessages(Enumerable.Range(1, 300).Select(CreateMessage));

        var nextRows = ChatOverlay.InspectRows();
        var nextTexts = new HashSet<Text>(
            ChatOverlay.InspectChatTexts(),
            ReferenceComparer<Text>.Instance);
        Equal(300, nextRows.Count, "rendered row count");
        Equal(1, UnityObject.DestroyCallCount - destroyedBefore, "destroy calls");
        for (var index = 1; index < oldRows.Count; index++)
            Same(oldRows[index].Root, nextRows[index - 1].Root, $"retained row {index}");

        Equal(900, nextTexts.Count, "bounded tracked text count");
        True(droppedTexts.All(text => !nextTexts.Contains(text)), "dropped row texts were retained");
        True(retainedTexts.All(nextTexts.Contains), "a retained row text reference was removed");
        Equal(897, oldTexts.Count(text => nextTexts.Contains(text)), "retained old text references");
    }

    private static void OptimisticAcknowledgementKeepsIdentity()
    {
        ChatOverlay.ResetHarness();
        var optimistic = new ChatUiMessage
        {
            ClientMessageId = "client-ack-1",
            Sender = "Mina",
            CharacterId = "101",
            CharacterName = "Character",
            Order = "P1",
            Text = "hello"
        };
        ChatOverlay.SetChatMessages(new[] { optimistic });
        var before = ChatOverlay.InspectRows().Single();
        var createdBefore = GameObject.CreatedCount;
        var destroyedBefore = UnityObject.DestroyCallCount;

        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage
            {
                Id = "server-ack-1",
                ClientMessageId = "client-ack-1",
                Sender = "Mina",
                CharacterId = "101",
                CharacterName = "Character",
                Order = "P1",
                Text = "hello"
            }
        });

        var after = ChatOverlay.InspectRows().Single();
        Same(before.Root, after.Root, "acknowledged row GameObject");
        Equal(before.Identity, after.Identity, "identity");
        Equal(before.RenderKey, after.RenderKey, "render key");
        Equal(createdBefore, GameObject.CreatedCount, "created objects");
        Equal(destroyedBefore, UnityObject.DestroyCallCount, "destroy calls");
    }

    private static void ParticipantChangeReplacesOnlyAffectedRow()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage
            {
                ClientMessageId = "participant-change",
                ParticipantId = "participant-old",
                Sender = "Mina",
                CharacterId = "101",
                CharacterName = "First",
                Order = "P1",
                Text = "same message"
            },
            CreateMessage(2)
        });
        var before = ChatOverlay.InspectRows();
        var destroyedBefore = UnityObject.DestroyCallCount;

        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage
            {
                ClientMessageId = "participant-change",
                ParticipantId = "participant-new",
                Sender = "Mina",
                CharacterId = "102",
                CharacterName = "Second",
                Order = "P2",
                Text = "same message"
            },
            CreateMessage(2)
        });

        var after = ChatOverlay.InspectRows();
        NotSame(before[0].Root, after[0].Root, "changed participant row");
        Same(before[1].Root, after[1].Root, "unaffected row");
        Equal(before[0].Identity, after[0].Identity, "changed row identity");
        True(before[0].RenderKey != after[0].RenderKey, "changed participant render key");
        Equal(1, UnityObject.DestroyCallCount - destroyedBefore, "destroy calls");
        Equal(6, ChatOverlay.InspectChatTexts().Count, "tracked text count");
    }

    private static void MultilineUsesPreferredHeight()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "sized-row", Sender = "Mina", Text = "short" }
        });
        var shortRow = ChatOverlay.InspectRows().Single();
        var shortPreferred = shortRow.Body!.preferredHeight;
        var readsBefore = Text.PreferredHeightReadCount;

        var multiline = string.Join("\n", Enumerable.Repeat("measured line", 12));
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "sized-row", Sender = "Mina", Text = multiline }
        });

        var longRow = ChatOverlay.InspectRows().Single();
        var measured = longRow.Body!.preferredHeight;
        True(measured > shortPreferred, "multiline preferred height did not grow");
        Equal(readsBefore + 2, Text.PreferredHeightReadCount, "preferredHeight reads (one inspect, one render)");
        Near(measured, longRow.Body.rectTransform.rect.height, 0.01f, "body measured height");
        Near(40f + measured + 12f, longRow.Height, 0.01f, "row measured height");
    }

    private static void TrimPreservesScrollOffset()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetViewportHeight(502f);
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 300).Select(CreateMessage));
        var contentBefore = ChatOverlay.GetContentHeight();
        var scrollableBefore = contentBefore - 502f;
        const float topOffsetBefore = 5000f;
        ChatOverlay.SetScrollPosition(1f - topOffsetBefore / scrollableBefore);

        ChatOverlay.SetChatMessages(Enumerable.Range(1, 300).Select(CreateMessage));

        var contentAfter = ChatOverlay.GetContentHeight();
        var actualTopOffset = (1f - ChatOverlay.GetScrollPosition()) * (contentAfter - 502f);
        Near(topOffsetBefore - 96f, actualTopOffset, 0.1f, "top offset after removing a 90px row and gap");
    }

    private static void NonBattleCdnRetryUpdatesInPlace()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage
            {
                Id = "cdn-row",
                Sender = "Mina",
                CharacterId = "101",
                CharacterName = "Character",
                Text = "portrait pending"
            }
        });
        var row = ChatOverlay.InspectRows().Single();
        var portrait = row.Portrait!;
        var snapshot = new ChatSnapshot { RoomId = "123456", ScreenPhase = "방" };
        Time.unscaledTime = 0f;
        ChatOverlay.RefreshPortraitsForTest(snapshot);
        True(!portrait.gameObject.activeSelf && portrait.texture == null, "portrait should begin missing");
        var createdBeforeRefresh = GameObject.CreatedCount;
        var destroyedBeforeRefresh = UnityObject.DestroyCallCount;

        var texture = new Texture();
        ChatOverlay.SetCdnSprite("101", new Sprite(texture));
        Time.unscaledTime = 3f;
        ChatOverlay.RefreshPortraitsForTest(snapshot);

        Same(row.Root, ChatOverlay.InspectRows().Single().Root, "row after portrait retry");
        Same(texture, portrait.texture!, "refreshed portrait texture");
        True(portrait.gameObject.activeSelf, "refreshed portrait visibility");
        Equal(createdBeforeRefresh, GameObject.CreatedCount, "objects created by portrait refresh");
        Equal(destroyedBeforeRefresh, UnityObject.DestroyCallCount, "objects destroyed by portrait refresh");
    }

    private static void InvalidatedGamePortraitFallsBack()
    {
        ChatOverlay.ResetHarness();
        var gameTexture = new Texture();
        GameChatRuntime.ConfigurePortraitLookup(_ => new GamePortraitResource(gameTexture, new Rect(0f, 0f, 1f, 1f)));
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "battle-portrait", Sender = "Mina", CharacterId = "101", Text = "battle" }
        });
        var snapshot = new ChatSnapshot { RoomId = "123456", ScreenPhase = "플레이" };
        Time.unscaledTime = 0f;
        ChatOverlay.RefreshPortraitsForTest(snapshot);
        var row = ChatOverlay.InspectRows().Single();
        Same(gameTexture, row.Portrait!.texture!, "initial game portrait");
        var cdnTexture = new Texture();
        ChatOverlay.SetCdnSprite("101", new Sprite(cdnTexture));
        GameChatRuntime.ConfigurePortraitLookup(_ => null);
        row.Portrait.texture = null;
        Time.unscaledTime = 3f;
        ChatOverlay.RefreshPortraitsForTest(snapshot);
        Same(row.Root, ChatOverlay.InspectRows().Single().Root, "row after invalidated texture");
        Same(cdnTexture, row.Portrait.texture!, "CDN fallback texture");
        True(row.Portrait.gameObject.activeSelf, "fallback portrait visibility");
    }

    private static void DisplayTruncationUsesUnicodeCodePoints()
    {
        ChatOverlay.ResetHarness();
        var text = string.Concat(Enumerable.Repeat("😀", 1401));
        ChatOverlay.SetChatMessages(new[]
        {
            new ChatUiMessage { Id = "unicode-row", Sender = "Mina", Text = text }
        });

        var displayed = ChatOverlay.InspectRows().Single().Body!.text;
        Equal(1400, PartyProtocol.TextLength(displayed), "displayed rune count including ellipsis");
        Equal(1399, PartyProtocol.TextLength(displayed[..^1]), "truncated message rune count");
        True(displayed.EndsWith("…", StringComparison.Ordinal), "truncation marker");
        True(char.IsHighSurrogate(displayed[^3]) && char.IsLowSurrogate(displayed[^2]), "last emoji was split");
    }

    private static void RequestSubmit(bool useButton)
    {
        if (useButton)
            ChatOverlay.ClickSendForTest();
        else
            ChatOverlay.TickInputForTest(KeyCode.Return);
        ChatOverlay.TickInputForTest();
    }

    private static void OverLimitDraftIsPreserved()
    {
        foreach (var useButton in new[] { false, true })
        foreach (var draft in new[] { new string('가', 1500), string.Concat(Enumerable.Repeat("😀", 999)) + "ab" })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft(draft);
            var writesBefore = ChatOverlay.GetDraftWriteCount();
            RequestSubmit(useButton);

            True(!ChatOverlay.TryDequeueOutgoing(out _), "over-limit draft was queued");
            Equal(draft, ChatOverlay.GetDraft(), "preserved draft");
            Equal(writesBefore, ChatOverlay.GetDraftWriteCount(), "writes to rejected draft");
            True(!ChatOverlay.GetStatusText().Contains("/1000"), "header retained the character counter");
            True(ChatOverlay.GetStatusText().Contains("초과"), "missing Korean validation feedback");
            ChatOverlay.TickInputForTest();
            True(!ChatOverlay.TryDequeueOutgoing(out _), "rejected submit was repeated");
        }
    }

    private static void CodePointBoundarySendsIntact()
    {
        foreach (var useButton in new[] { false, true })
        foreach (var draft in new[] { new string('가', 1000), string.Concat(Enumerable.Repeat("😀", 1000)) })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft(draft);
            ChatOverlay.TickInputForTest();
            Equal("연결됨", ChatOverlay.GetStatusText(), "valid draft header");
            RequestSubmit(useButton);

            True(ChatOverlay.TryDequeueOutgoing(out var sent), "valid boundary draft did not send");
            Equal(draft, sent, "boundary payload");
            Equal(string.Empty, ChatOverlay.GetDraft(), "draft after accepted send");
            Equal("연결됨", ChatOverlay.GetStatusText(), "header after accepted send");
            ChatOverlay.TickInputForTest();
            True(!ChatOverlay.TryDequeueOutgoing(out _), "accepted submit was repeated");
        }
    }

    private static void DraftFeedbackFollowsEditing()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft(new string('a', 1001));
        RequestSubmit(useButton: true);
        ChatOverlay.SetChatStatus("재연결 중");
        Equal("입력 길이 초과: 내용을 줄여주세요.", ChatOverlay.GetStatusText(), "connection status hid validation feedback");
        ChatOverlay.TickInputForTest();
        True(ChatOverlay.GetStatusText().Contains("초과"), "frame refresh hid validation feedback");

        ChatOverlay.SetDraft("  안녕😀  ");
        ChatOverlay.TickInputForTest();
        Equal("재연결 중", ChatOverlay.GetStatusText(), "feedback after correction");
        Equal("  안녕😀  ", ChatOverlay.GetDraft(), "editing was normalized before send");
        True(!ChatOverlay.TryDequeueOutgoing(out _), "correcting the draft retriggered a rejected submit");
        RequestSubmit(useButton: false);
        True(ChatOverlay.TryDequeueOutgoing(out var sent), "corrected draft did not send");
        Equal("안녕😀", sent, "existing trimming on accepted send");
        ChatOverlay.SetChatStatus("연결됨");
        Equal("연결됨", ChatOverlay.GetStatusText(), "latest connection status after send");
    }

    private static void UnchangedDraftAvoidsValidationAllocations()
    {
        ChatOverlay.ResetHarness();
        var draft = "  " + string.Concat(Enumerable.Repeat("😀", 1000)) + "  ";
        ChatOverlay.SetDraft(draft);
        for (var i = 0; i < 10; i++) ChatOverlay.RefreshInputStatusForTest();
        var writesBefore = ChatOverlay.GetDraftWriteCount();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) ChatOverlay.RefreshInputStatusForTest();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        True(allocated < 1024, $"unchanged draft validation allocated {allocated} bytes");
        ChatOverlay.SetChatStatus("재연결 중");
        Equal("재연결 중", ChatOverlay.GetStatusText(), "cached draft hid a connection status change");
        Equal(draft, ChatOverlay.GetDraft(), "validation changed the draft");
        Equal(writesBefore, ChatOverlay.GetDraftWriteCount(), "validation rewrote the draft");
    }

    private static void ImeSendValidatesCommittedDraft()
    {
        foreach (var finalLength in new[] { 1000, 1001 })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft(new string('가', 999));
            Input.compositionString = "나";
            ChatOverlay.TickInputForTest();
            Equal("연결됨", ChatOverlay.GetStatusText(), "header during composition");
            ChatOverlay.ClickSendForTest();
            var writesBefore = ChatOverlay.GetDraftWriteCount();
            ChatOverlay.TickInputForTest();
            True(!ChatOverlay.TryDequeueOutgoing(out _), "send ran during composition");
            Equal(writesBefore, ChatOverlay.GetDraftWriteCount(), "composition draft was rewritten");
            Equal("나", Input.compositionString, "IME composition was changed");

            var committed = new string('가', finalLength);
            ChatOverlay.SetDraft(committed);
            Input.compositionString = string.Empty;
            ChatOverlay.TickInputForTest();
            True(!ChatOverlay.TryDequeueOutgoing(out _), "send ran in the IME commit frame");
            ChatOverlay.TickInputForTest();
            if (finalLength == 1000)
            {
                True(ChatOverlay.TryDequeueOutgoing(out var sent), "committed boundary draft was rejected");
                Equal(committed, sent, "committed payload");
                Equal(string.Empty, ChatOverlay.GetDraft(), "accepted IME draft was not cleared");
            }
            else
            {
                True(!ChatOverlay.TryDequeueOutgoing(out _), "committed over-limit draft was sent");
                Equal(committed, ChatOverlay.GetDraft(), "rejected committed draft");
                True(ChatOverlay.GetStatusText().Contains("초과"), "committed oversize feedback");
            }
            ChatOverlay.TickInputForTest();
            True(!ChatOverlay.TryDequeueOutgoing(out _), "IME submit token was consumed twice");
        }
    }

    private static void ImeEnterSendsCommittedText()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("안녕");
        Input.compositionString = "가";
        var writesBefore = ChatOverlay.GetDraftWriteCount();
        ChatOverlay.TickInputForTest(KeyCode.Return);
        Equal(writesBefore, ChatOverlay.GetDraftWriteCount(), "candidate Enter changed the draft");
        Equal("가", Input.compositionString, "candidate Enter changed composition");
        True(!ChatOverlay.TryDequeueOutgoing(out _), "candidate Enter sent text");

        ChatOverlay.SetDraft("안녕가");
        Input.compositionString = string.Empty;
        ChatOverlay.TickInputForTest();
        True(!ChatOverlay.TryDequeueOutgoing(out _), "commit frame sent text before it settled");
        Equal("안녕가", ChatOverlay.GetDraft(), "draft after candidate commit");
        ChatOverlay.TickInputForTest();
        True(ChatOverlay.TryDequeueOutgoing(out var sent), "one Enter did not send after composition committed");
        Equal("안녕가", sent, "payload after candidate commit");
        True(!ChatOverlay.TryDequeueOutgoing(out _), "IME Enter sent twice");
    }

    private static void WheelDistanceDoesNotDependOnHistory()
    {
        foreach (var count in new[] { 10, 300 })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetChatMessages(Enumerable.Range(0, count).Select(CreateMessage));
            var scrollable = ChatOverlay.GetContentHeight() - 502f;
            ChatOverlay.WheelForTest(1f);
            Near(100f, ChatOverlay.GetScrollPosition() * scrollable, 0.01f, $"upward wheel distance for {count} messages");
            ChatOverlay.WheelForTest(-0.25f);
            Near(75f, ChatOverlay.GetScrollPosition() * scrollable, 0.01f, $"fractional downward wheel distance for {count} messages");
        }
    }

    private static void WheelRespectsBoundsAndViewport()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetViewportHeight(800f);
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 30).Select(CreateMessage));
        var scrollable = ChatOverlay.GetContentHeight() - 800f;
        ChatOverlay.WheelForTest(2.5f);
        Near(250f, ChatOverlay.GetScrollPosition() * scrollable, 0.01f, "distance with a taller viewport");
        var before = ChatOverlay.GetScrollPosition();
        ChatOverlay.WheelForTest(1f, insideViewport: false);
        Near(before, ChatOverlay.GetScrollPosition(), 0.0001f, "wheel outside viewport");
        ChatOverlay.WheelForTest(10000f);
        Near(1f, ChatOverlay.GetScrollPosition(), 0.0001f, "top clamp");
        ChatOverlay.WheelForTest(-10000f);
        Near(0f, ChatOverlay.GetScrollPosition(), 0.0001f, "bottom clamp");

        foreach (var count in new[] { 0, 1 })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetChatMessages(Enumerable.Range(0, count).Select(CreateMessage));
            ChatOverlay.SetScrollPosition(0.8f);
            ChatOverlay.WheelForTest(1f);
            True(float.IsFinite(ChatOverlay.GetScrollPosition()), "non-overflowing wheel produced a non-finite position");
            Near(0f, ChatOverlay.GetScrollPosition(), 0.0001f, "non-overflowing wheel position");
        }
    }

    private static void BottomFollowingUsesDistance()
    {
        foreach (var count in new[] { 10, 300 })
        foreach (var distance in new[] { 31f, 32f, 33f, 200f })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetChatMessages(Enumerable.Range(0, count).Select(CreateMessage));
            var scrollableBefore = ChatOverlay.GetContentHeight() - 502f;
            ChatOverlay.SetScrollPosition(distance / scrollableBefore);
            var topOffsetBefore = (1f - ChatOverlay.GetScrollPosition()) * scrollableBefore;
            ChatOverlay.SetChatMessages(Enumerable.Range(0, count + 1).Select(CreateMessage));

            if (distance <= 32f)
                Near(0f, ChatOverlay.GetScrollPosition(), 0.0001f, $"bottom following at {distance} UI units with {count} messages");
            else
            {
                var topOffsetAfter = (1f - ChatOverlay.GetScrollPosition()) * (ChatOverlay.GetContentHeight() - 502f);
                var removedLeadingHeight = count == 300 ? 96f : 0f;
                Near(topOffsetBefore - removedLeadingHeight, topOffsetAfter, 0.02f, $"reader position at {distance} UI units with {count} messages");
                True(ChatOverlay.GetScrollPosition() > 0f, "reader outside the bottom threshold was snapped down");
            }
        }
    }

    private static void NonOverflowingHistoryFollowsBottom()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 1).Select(CreateMessage));
        ChatOverlay.SetScrollPosition(1f);
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 10).Select(CreateMessage));
        Near(0f, ChatOverlay.GetScrollPosition(), 0.0001f, "initial overflow bottom following");
    }

    private static ChatUiMessage CreateMessage(int index) => new()
    {
        Id = $"message-{index:D3}",
        Sender = $"Sender {index:D3}",
        CharacterId = "101",
        CharacterName = "Character",
        Order = "P1",
        Text = $"Message {index:D3}"
    };

    private static void True(bool condition, string description)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException($"Expected true: {description}.");
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private static void Same(object expected, object actual, string description)
    {
        _assertions++;
        if (!ReferenceEquals(expected, actual))
            throw new InvalidOperationException($"{description}: expected the same object reference.");
    }

    private static void NotSame(object expected, object actual, string description)
    {
        _assertions++;
        if (ReferenceEquals(expected, actual))
            throw new InvalidOperationException($"{description}: expected a new object reference.");
    }

    private static void Near(float expected, float actual, float tolerance, string description)
    {
        _assertions++;
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new();
        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
#endif
