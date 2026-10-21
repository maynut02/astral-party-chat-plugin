using AstralPartyChatPlugin;
using UnityEngine;

internal static class Check
{
    internal static int Passed { get; private set; }
    private static int _assertions;
    internal static void Run(string name, Action test)
    {
        try
        {
            test();
            Passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + error.Message);
            throw;
        }
    }
    internal static void True(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(description);
    }
    internal static void Equal<T>(T expected, T actual, string description)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}");
    }
    internal static void Near(float expected, float actual, string description)
    {
        _assertions++;
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > 0.05f)
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}");
    }
    internal static void Summary() => Console.WriteLine($"{Passed} overlay regression checks passed ({_assertions} assertions).");
}

internal static class OverlayTests
{
    private static int Main()
    {
        Check.Run("300-row shift retains rows and releases only dropped texts", ShiftRetainsRows);
        Check.Run("optimistic acknowledgement keeps the rendered row", OptimisticAcknowledgement);
        Check.Run("participant metadata change replaces only its row", ParticipantChange);
        Check.Run("row creation failure cleans old and completed new rows before retry", RowCreationFailureRecovery);
        Check.Run("initial row creation failure releases the partial UI before delayed retry", InitialRowCreationFailureRecovery);
        Check.Run("ordinary refresh restores rows after a text scale presentation failure", TextScalePresentationFailureRecovery);
        Check.Run("multiline row heights follow measured FairyGUI text", MultilineMeasurement);
        Check.Run("display truncation counts Unicode code points", DisplayTruncation);
        Check.Run("Enter and Send preserve over-limit drafts", OverLimitDraft);
        Check.Run("1000 code points send intact through Enter and Send", CodePointBoundary);
        Check.Run("draft feedback survives status updates and clears after correction", DraftFeedback);
        Check.Run("explicit IME Send validates the final committed draft", ExplicitImeValidation);
        ScrollControlsTests.Run();
        KeyboardInputTests.Run();
        PortraitStateTests.Run();
        WindowSettingsTests.Run();
        ChatOverlay.Shutdown();
        Check.Summary();
        return 0;
    }

    internal static ChatUiMessage Message(int index, string? text = null, string character = "101", string name = "Character", string order = "P1") => new()
    {
        Id = "message-" + index,
        Sender = "Sender " + index,
        CharacterId = character,
        CharacterName = name,
        Order = order,
        Text = text ?? "Message " + index
    };

    private static void ShiftRetainsRows()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 300).Select(i => Message(i)));
        var old = ChatOverlay.InspectRows();
        var staticTexts = ChatOverlay.InspectChatTexts().Except(old.SelectMany(row => row.Texts)).ToArray();
        ChatOverlay.SetChatMessages(Enumerable.Range(1, 300).Select(i => Message(i)));
        var next = ChatOverlay.InspectRows();
        Check.Equal(300, next.Length, "bounded row count");
        Check.True(old[0].Root.IsDisposed, "trimmed row disposed");
        Check.True(old[0].Texts.All(text => text.IsDisposed && text.HandleReleased), "trimmed texts released");
        for (var i = 1; i < old.Length; i++)
            Check.True(ReferenceEquals(old[i].Root, next[i - 1].Root), "retained row " + i);
        var tracked = ChatOverlay.InspectChatTexts();
        Check.Equal(staticTexts.Length + 900, tracked.Length, "tracked text references bounded");
        Check.True(staticTexts.All(tracked.Contains), "window text remains tracked");
        Check.True(old[0].Texts.All(text => !tracked.Contains(text)), "trimmed text references removed");
        Check.True(old.Skip(1).SelectMany(row => row.Texts).All(tracked.Contains), "retained text references remain");
    }

    private static void OptimisticAcknowledgement()
    {
        ChatOverlay.ResetHarness();
        ChatUiMessage Pending(string id) => new()
        {
            Id = id, ClientMessageId = "client-1", Sender = "Mina", CharacterId = "101",
            CharacterName = "Character", Order = "P1", Text = "hello"
        };
        ChatOverlay.SetChatMessages(new[] { Pending("") });
        var old = ChatOverlay.InspectRows().Single();
        ChatOverlay.SetChatMessages(new[] { Pending("server-1") });
        var next = ChatOverlay.InspectRows().Single();
        Check.True(ReferenceEquals(old.Root, next.Root), "ack reuses row");
        Check.True(!old.Root.IsDisposed, "ack does not dispose row");
        Check.Equal(old.Identity, next.Identity, "client identity stable");
    }

    private static void ParticipantChange()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(new[] { Message(0), Message(1) });
        var old = ChatOverlay.InspectRows();
        ChatOverlay.SetChatMessages(new[] { Message(0, character: "102", name: "New character", order: "P3"), Message(1) });
        var next = ChatOverlay.InspectRows();
        Check.True(old[0].Root.IsDisposed && !ReferenceEquals(old[0].Root, next[0].Root), "changed presentation rebuilt");
        Check.True(ReferenceEquals(old[1].Root, next[1].Root), "unaffected row retained");
        Check.Equal("P3 · New character", next[0].Participant!.GetText(), "new participant displayed");
    }

    private static void RowCreationFailureRecovery()
    {
        foreach (var retainFirst in new[] { false, true })
        {
            ChatOverlay.ResetHarness();
            var emptyUiHandles = FguiNode.LiveHandles;
            var staticTexts = ChatOverlay.InspectChatTexts();
            var count = retainFirst ? 3 : 2;
            var firstReplacement = retainFirst ? 1 : 0;
            ChatOverlay.SetChatMessages(Enumerable.Range(0, count).Select(index => Message(index)));
            var old = ChatOverlay.InspectRows();
            var oldNodes = old.SelectMany(row => row.Root.Children.Prepend(row.Root)).ToArray();
            var populatedHandles = FguiNode.LiveHandles;
            var content = ChatOverlay.Node("Viewport").Children.Single();
            const string faultText = "second replacement fails";
            var next = Enumerable.Range(0, count).Select(index => index < firstReplacement
                ? Message(index)
                : Message(index, index == firstReplacement ? "first replacement succeeds" : faultText)).ToArray();

            InvalidOperationException? failure = null;
            FguiNode.FaultText = faultText;
            try { ChatOverlay.SetChatMessages(next); }
            catch (InvalidOperationException error) { failure = error; }
            finally { FguiNode.FaultText = null; }

            Check.True(failure != null, "row creation failure propagates");
            Check.Equal("Injected text failure.", failure!.Message, "original creation failure is preserved");
            Check.Equal(0, ChatOverlay.InspectRows().Length, "failed reconciliation publishes no disposed rows");
            Check.Equal(0, content.Children.Count, "failed reconciliation leaves no orphan content children");
            Check.True(oldNodes.All(node => node.IsDisposed && node.HandleReleased), "all old row handles are released");
            Check.True(staticTexts.SequenceEqual(ChatOverlay.InspectChatTexts()), "only live window texts remain registered");
            Check.Equal(emptyUiHandles, FguiNode.LiveHandles, "completed and failed new rows release every handle");
            Check.Near(0f, ChatOverlay.Scrollable, "failed presentation has no stale scroll range");

            ChatOverlay.SetChatMessages(next);
            var recovered = ChatOverlay.InspectRows();
            Check.Equal(count, recovered.Length, "same snapshot retries completely");
            Check.Equal(count, content.Children.Count, "retry has exactly one content child per published row");
            Check.True(recovered.All(row => content.Children.Contains(row.Root)
                && !row.Root.IsDisposed && !row.Root.HandleReleased), "retry publishes only live owned rows");
            Check.True(old.Select(row => row.Identity).SequenceEqual(recovered.Select(row => row.Identity)),
                "retry retains logical message identities");
            for (var index = 0; index < count; index++)
                Check.Equal(next[index].Text, recovered[index].Body.GetText(), "retry displays the retained snapshot");
            var tracked = ChatOverlay.InspectChatTexts();
            Check.Equal(staticTexts.Length + recovered.Sum(row => row.Texts.Length), tracked.Length,
                "retry registers exactly the live window and row texts");
            Check.True(tracked.All(text => !text.IsDisposed && !text.HandleReleased), "retry has no stale text registrations");
            Check.Equal(populatedHandles, FguiNode.LiveHandles, "retry restores the original live handle balance");

            ChatOverlay.SetChatMessages(next.ToArray());
            Check.True(recovered.Select(row => row.Root).SequenceEqual(ChatOverlay.InspectRows().Select(row => row.Root)),
                "replay after recovery reuses every row");
            Check.Equal(populatedHandles, FguiNode.LiveHandles, "replay after recovery adds no handles");
            ChatOverlay.Shutdown();
            Check.Equal(1, FguiNode.LiveHandles, "shutdown after reconciliation recovery retains only the borrowed root");
        }
    }

    private static void InitialRowCreationFailureRecovery()
    {
        ChatOverlay.ResetHarness();
        var emptyUiHandles = FguiNode.LiveHandles;
        var root = ChatOverlay.TestRoot;
        ChatOverlay.Shutdown();
        const string faultText = "second initial row fails";
        var messages = new[] { Message(0, "first initial row succeeds"), Message(1, faultText) };
        ChatOverlay.SetChatMessages(messages);

        InvalidOperationException? failure = null;
        FguiNode.FaultText = faultText;
        try { ChatOverlay.Tick(); }
        catch (InvalidOperationException error) { failure = error; }
        finally { FguiNode.FaultText = null; }

        Check.True(failure != null, "initial row creation failure propagates");
        Check.Equal("Injected text failure.", failure!.Message, "initial creation preserves the original exception");
        Check.Equal(0, ChatOverlay.InspectRows().Length, "initial failure clears published rows");
        Check.Equal(0, ChatOverlay.InspectChatTexts().Length, "partial UI retains no text registrations");
        Check.Equal(0, root.Children.Count, "partial UI leaves no native children attached to the game root");
        Check.Equal(1, FguiNode.LiveHandles, "initial failure releases the completed first row and all UI handles");
        ChatOverlay.Tick();
        Check.Equal(1, FguiNode.LiveHandles, "creation retry waits without allocating another partial UI");
        Check.Equal(0, ChatOverlay.InspectRows().Length, "rows stay absent during the retry delay");

        Time.unscaledTime += 5.1f;
        ChatOverlay.Tick();
        var recovered = ChatOverlay.InspectRows();
        Check.Equal(messages.Length, recovered.Length, "delayed UI retry renders the cached messages");
        var content = ChatOverlay.Node("Viewport").Children.Single();
        Check.Equal(recovered.Length, content.Children.Count, "initial recovery has no orphan content children");
        for (var index = 0; index < messages.Length; index++)
        {
            Check.Equal(messages[index].Text, recovered[index].Body.GetText(), "initial recovery keeps the cached message text");
            Check.True(content.Children.Contains(recovered[index].Root) && !recovered[index].Root.IsDisposed,
                "initial recovery publishes live attached rows");
        }
        var recoveredHandles = emptyUiHandles + recovered.Sum(row => row.Root.Children.Count + 1);
        Check.Equal(recoveredHandles, FguiNode.LiveHandles, "initial recovery owns exactly the window and rendered row handles");
        Check.True(ChatOverlay.InspectChatTexts().All(text => !text.IsDisposed && !text.HandleReleased),
            "initial recovery retains only live text registrations");
        ChatOverlay.SetChatMessages(messages.ToArray());
        Check.True(recovered.Select(row => row.Root).SequenceEqual(ChatOverlay.InspectRows().Select(row => row.Root)),
            "cached identities survive initial failure and replay");
        Check.Equal(recoveredHandles, FguiNode.LiveHandles, "initial recovery replay does not grow live handles");
        ChatOverlay.Shutdown();
        Check.Equal(1, FguiNode.LiveHandles, "shutdown after initial recovery retains only the borrowed root");
        Check.True(!root.IsDisposed, "initial failure and recovery never dispose the game root");
    }

    private static void TextScalePresentationFailureRecovery()
    {
        ChatOverlay.ResetHarness();
        try
        {
            ChatOverlay.Click("Settings");
            var emptyUiHandles = FguiNode.LiveHandles;
            const string faultText = "second scaled row fails";
            var messages = new[]
            {
                Message(0, "first scaled row succeeds"), Message(1, faultText), Message(2, "third scaled row")
            };
            ChatOverlay.SetChatMessages(messages);
            var old = ChatOverlay.InspectRows();
            var oldNodes = old.SelectMany(row => row.Root.Children.Prepend(row.Root)).ToArray();
            var populatedHandles = FguiNode.LiveHandles;
            var windowRect = ChatOverlay.WindowRect;
            var content = ChatOverlay.Node("Viewport").Children.Single();

            InvalidOperationException? failure = null;
            FguiNode.FaultText = faultText;
            try { ChatOverlay.Click("TextLarger"); }
            catch (InvalidOperationException error) { failure = error; }
            finally { FguiNode.FaultText = null; }

            Check.True(failure != null, "text scale presentation failure propagates");
            Check.Equal("Injected text failure.", failure!.Message, "text scale failure preserves the original exception");
            Check.Near(1.1f, ChatOverlay.TextScale, "failed render retains the requested text scale");
            Check.Equal(0, ChatOverlay.InspectRows().Length, "failed text scale render clears published rows");
            Check.Equal(0, content.Children.Count, "failed text scale render leaves no orphan rows");
            Check.True(oldNodes.All(node => node.IsDisposed && node.HandleReleased), "failed text scale render releases every old row handle");
            Check.Equal(emptyUiHandles, FguiNode.LiveHandles, "failed text scale render releases completed and partial replacement handles");
            Check.True(ChatOverlay.WindowOpen, "presentation failure keeps the chat window open");

            // No new snapshot, font or geometry update drives this recovery.
            ChatOverlay.Tick();
            var recovered = ChatOverlay.InspectRows();
            Check.Equal(messages.Length, recovered.Length, "ordinary refresh restores every cached row");
            Check.True(old.Select(row => row.Identity).SequenceEqual(recovered.Select(row => row.Identity)),
                "ordinary refresh preserves cached message identities and order");
            Check.True(content.Children.SequenceEqual(recovered.Select(row => row.Root)),
                "ordinary refresh restores exactly the published rows in native order");
            for (var index = 0; index < messages.Length; index++)
            {
                Check.Equal(messages[index].Text, recovered[index].Body.GetText(), "ordinary refresh restores each cached body in order");
                Check.Equal(23, recovered[index].Body.FontSize, "ordinary refresh applies the retained text scale");
                Check.True(!recovered[index].Root.IsDisposed && !recovered[index].Root.HandleReleased,
                    "ordinary refresh publishes only live rows");
            }
            Check.Equal(populatedHandles, FguiNode.LiveHandles, "ordinary refresh restores the original live handle balance");
            Check.True(ChatOverlay.WindowOpen, "ordinary refresh preserves the open window");
            Check.Equal(windowRect, ChatOverlay.WindowRect, "ordinary refresh needs no geometry change");
            Check.Near(1.1f, ChatOverlay.TextScale, "ordinary refresh needs no further font change");
            Check.True(ChatOverlay.InspectChatTexts().All(text => !text.IsDisposed && !text.HandleReleased),
                "ordinary refresh leaves no stale text registrations");

            ChatOverlay.Tick();
            Check.True(recovered.Select(row => row.Root).SequenceEqual(ChatOverlay.InspectRows().Select(row => row.Root)),
                "subsequent ordinary refresh retains the recovered rows");
            Check.Equal(populatedHandles, FguiNode.LiveHandles, "subsequent ordinary refresh adds no handles");
        }
        finally
        {
            FguiNode.FaultText = null;
            ChatOverlay.Shutdown();
        }
        Check.Equal(1, FguiNode.LiveHandles, "shutdown after text scale recovery retains only the borrowed root");
    }

    private static void MultilineMeasurement()
    {
        ChatOverlay.ResetHarness();
        var lines = string.Join("\n", Enumerable.Repeat("measured line", 12));
        ChatOverlay.SetChatMessages(new[] { Message(0), Message(1, lines), new ChatUiMessage { Id = "system", IsSystem = true, Text = lines } });
        var rows = ChatOverlay.InspectRows();
        Check.True(rows[1].Height > rows[0].Height, "multiline player row grows");
        Check.Near(12 * 21 * 1.25f, rows[1].Body.Height, "deterministic measured text height");
        Check.Near(rows[1].Body.Y + rows[1].Body.Height + 12, rows[1].Height, "player includes measured text and footer");
        Check.Near(rows[2].Body.Height + 12, rows[2].Height, "system includes measured text and padding");
        Check.True(rows[2].Root.Y >= rows[1].Root.Y + rows[1].Height, "rows do not overlap");
    }

    private static void DisplayTruncation()
    {
        ChatOverlay.ResetHarness();
        var exact = string.Concat(Enumerable.Repeat("😀", 1400));
        var longText = exact + "끝";
        ChatOverlay.SetChatMessages(new[] { Message(0, exact), Message(1, longText), Message(2, "ordinary") });
        var rows = ChatOverlay.InspectRows();
        Check.Equal(exact, rows[0].Body.GetText(), "1400 code points untouched");
        Check.Equal(string.Concat(Enumerable.Repeat("😀", 1399)) + "…", rows[1].Body.GetText(), "one code point reserved for ellipsis");
        Check.Equal(1400, PartyProtocol.TextLength(rows[1].Body.GetText()), "display bound uses code points");
        Check.Equal("ordinary", rows[2].Body.GetText(), "short message unchanged");
    }

    private static void Submit(bool send)
    {
        if (send) ChatOverlay.Click("Send");
        else ChatOverlay.Tick(key: KeyCode.Return);
        ChatOverlay.Tick();
    }

    private static void OverLimitDraft()
    {
        foreach (var send in new[] { false, true })
        {
            ChatOverlay.ResetHarness();
            var draft = string.Concat(Enumerable.Repeat("😀", 1001));
            ChatOverlay.SetDraft(draft);
            Submit(send);
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "over-limit draft not queued");
            Check.Equal(draft, ChatOverlay.GetDraft(), "over-limit draft preserved");
            Check.True(ChatOverlay.Node("Input").Focused, "rejected draft stays editable");
            Check.Equal("입력 길이 초과: 내용을 줄여주세요.", ChatOverlay.Status, "length feedback shown");
        }
    }

    private static void CodePointBoundary()
    {
        foreach (var send in new[] { false, true })
        {
            ChatOverlay.ResetHarness();
            var draft = string.Concat(Enumerable.Repeat("😀", 1000));
            ChatOverlay.SetDraft("  " + draft + "  ");
            Submit(send);
            Check.True(ChatOverlay.TryDequeueOutgoing(out var sent), "boundary draft sent");
            Check.Equal(draft, sent, "all code points retained and surrounding space trimmed");
            Check.Equal(string.Empty, ChatOverlay.GetDraft(), "successful draft cleared");
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "single submit");
        }
    }

    private static void DraftFeedback()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatStatus("  연결됨  ");
        ChatOverlay.SetDraft(new string('가', 1001));
        ChatOverlay.Tick();
        Check.Equal("입력 길이 초과: 내용을 줄여주세요.", ChatOverlay.Status, "over-limit feedback");
        ChatOverlay.SetChatStatus("재연결 중");
        Check.Equal("입력 길이 초과: 내용을 줄여주세요.", ChatOverlay.Status, "connection change cannot hide draft feedback");
        ChatOverlay.SetDraft("corrected");
        ChatOverlay.Tick();
        Check.Equal("재연결 중", ChatOverlay.Status, "corrected draft restores status");
        Input.compositionString = new string('가', 2000);
        ChatOverlay.Tick();
        Check.Equal("재연결 중", ChatOverlay.Status, "uncommitted IME text not validated");
        Check.Equal("corrected", ChatOverlay.GetDraft(), "validation never rewrites draft");
    }

    private static void ExplicitImeValidation()
    {
        foreach (var overLimit in new[] { false, true })
        {
            ChatOverlay.ResetHarness();
            var draft = new string('가', overLimit ? 1000 : 999);
            ChatOverlay.SetDraft(draft);
            Input.compositionString = "나";
            ChatOverlay.Click("Send");
            ChatOverlay.Node("Input").Blur();
            ChatOverlay.Tick();
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "composition waits");
            ChatOverlay.SetDraft(draft + "나");
            Input.compositionString = string.Empty;
            ChatOverlay.Tick();
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "commit frame waits");
            ChatOverlay.Tick();
            Check.Equal(!overLimit, ChatOverlay.TryDequeueOutgoing(out var text), "final committed length validated");
            Check.Equal(overLimit ? draft + "나" : string.Empty, ChatOverlay.GetDraft(), "committed draft preserved or cleared");
            if (!overLimit) Check.Equal(draft + "나", text, "final committed message sent");
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "explicit IME sends once");
        }
    }
}
