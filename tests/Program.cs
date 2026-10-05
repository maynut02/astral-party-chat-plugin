using System.Net;
using System.Reflection;
using System.Text.Json;
using AstralParty.Chat;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Room creation leaves nickname ownership to WebSocket JOIN", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (_, _) => Task.FromResult(FakeHttp.Response(
            f.Handler.RoomBodies.Last().TryGetProperty("nickname", out _) ? HttpStatusCode.Conflict : HttpStatusCode.OK));
        f.Start(); await f.Joined();
        Check.True(f.Handler.RoomBodies.Single().GetProperty("roomId").GetString() == "123456", "Wrong room was ensured.");
        Check.True(f.Latest.Nickname == "tester", "JOIN lost the validated nickname.");
    }),
    ("Reconnect survives stale nickname ownership without repeating HTTP room checks", async () =>
    {
        using var f = new Fixture(); var count = 0;
        // Model the logged failure: an HTTP nickname check would keep returning 409.
        f.Handler.Respond = (attempt, _) => Task.FromResult(FakeHttp.Response(attempt > 1 ? HttpStatusCode.Conflict : HttpStatusCode.OK));
        f.ConfigureSocket = socket =>
        {
            var attempt = Interlocked.Increment(ref count);
            if (attempt is not (2 or 3)) return;
            socket.AutoJoin = false;
            socket.OnSend = payload =>
            {
                if (payload.GetProperty("type").GetString() == "JOIN")
                    socket.Feed(new { type = "ERROR", code = "NICKNAME_TAKEN", message = "previous peer still present" });
                return Task.CompletedTask;
            };
        };
        f.Start(); await f.Joined(); f.Latest.Abort();
        await Check.Eventually(() => f.Sockets.Count == 4); await f.Joined();
        Check.True(f.Handler.RoomRequests == 1, "Reconnect was blocked by a redundant HTTP nickname check.");
        f.Client.SendChat("recovered");
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.Any(message => message.Text == "recovered" && !message.Id.StartsWith("local:")));
    }),
    ("Expired server room is recreated before rejoining", async () =>
    {
        using var f = new Fixture(); var count = 0;
        f.ConfigureSocket = socket =>
        {
            if (Interlocked.Increment(ref count) != 2) return;
            socket.AutoJoin = false;
            socket.OnSend = payload =>
            {
                if (payload.GetProperty("type").GetString() == "JOIN")
                    socket.Feed(new { type = "ERROR", code = "ROOM_NOT_FOUND", message = "expired" });
                return Task.CompletedTask;
            };
        };
        f.Start(); await f.Joined(); f.Latest.Abort();
        await Check.Eventually(() => f.Sockets.Count == 3); await f.Joined();
        Check.True(f.Handler.RoomRequests == 2, "Expired room was never recreated.");
    }),
    ("Room to selection across missing UI roots keeps the original socket", async () =>
    {
        using var f = new Fixture(); var state = new GameSessionState();
        void Observe(string phase, double time, string room = "")
        {
            state.ObserveScreenPhase(phase, time); state.ObserveRoomId(room);
            f.Client.UpdateGameState(new ChatGameState
            {
                Available = state.ChatAvailable, Phase = state.Phase,
                RoomId = state.RoomId, Nickname = "tester", CharacterId = "spectator", Order = state.Order
            });
        }
        Observe("방", 1, "123456"); await f.Joined(); var socket = f.Latest;
        Observe("기타", 2); Observe("기타", 5); Observe("캐릭터 선택", 8);
        await Task.Delay(80);
        Check.True(state.RoomId == "123456" && state.Phase == "캐릭터 선택", "Transition lost room identity.");
        Check.True(f.Sockets.Count == 1 && ReferenceEquals(socket, f.Latest), "A scene gap unnecessarily reconnected the socket.");
        f.Client.SendChat("selection chat");
        await Check.Eventually(() => socket.Count("CHAT") == 1);
    }),
    ("A long room-to-selection gap suspends chat then reconnects", async () =>
    {
        using var f = new Fixture(); var state = new GameSessionState();
        void Observe(string phase, double time, string room = "")
        {
            state.ObserveScreenPhase(phase, time); state.ObserveRoomId(room);
            f.Client.UpdateGameState(new ChatGameState
            {
                Available = state.ChatAvailable, Phase = state.Phase,
                RoomId = state.RoomId, Nickname = "tester", CharacterId = "spectator", Order = state.Order
            });
        }
        Observe("방", 1, "123456"); await f.Joined(); var oldSocket = f.Latest;
        Observe("기타", 2); Observe("기타", 18); Observe("기타", 30);
        Check.True(!state.ChatAvailable && state.RoomId == "", "Chat stayed active across an unrecognized screen.");
        Observe("캐릭터 선택", 32);
        await Check.Eventually(() => f.Sockets.Count == 2); await f.Joined();
        Check.True(state.RoomId == "123456" && !ReferenceEquals(oldSocket, f.Latest), "Long transition lost the recoverable room.");
        f.Client.SendChat("after long transition");
        await Check.Eventually(() => f.Latest.Count("CHAT") == 1);
    }),
    ("A joined connection removed from the server room recreates it", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        f.Latest.Feed(new { type = "ERROR", code = "NOT_IN_ROOM", message = "room was removed" });
        await Check.Eventually(() => f.Sockets.Count == 2); await f.Joined();
        Check.True(f.Handler.RoomRequests == 2, "Missing server membership never refreshed the room.");
        f.Client.SendChat("joined again");
        await Check.Eventually(() => f.Latest.Count("CHAT") == 1);
    }),
    ("A correlated NOT_IN_ROOM chat error reconnects without resending failed text", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        var oldSocket = f.Latest; oldSocket.AutoChat = false;
        f.Client.SendChat("failed chat"); await Check.Eventually(() => oldSocket.Count("CHAT") == 1);
        var id = oldSocket.Sent.Last(payload => payload.GetProperty("type").GetString() == "CHAT")
            .GetProperty("clientMessageId").GetString();
        oldSocket.Feed(new { type = "ERROR", code = "NOT_IN_ROOM", message = "room was removed", clientMessageId = id });
        await Check.Eventually(() => f.Sockets.Count == 2); await f.Joined();
        Check.True(f.Handler.RoomRequests == 2, "Correlated membership error did not refresh the room.");
        Check.True(f.Client.GetUiSnapshot().Messages.Any(message => message.Text.StartsWith("failed chat")
            && message.Text.Contains("room was removed")), "Failed chat text was lost on reconnect.");
        Check.True(f.Latest.Count("CHAT") == 0, "Failed chat was resent automatically.");
        f.Client.SendChat("recovered chat");
        await Check.Eventually(() => f.Latest.Count("CHAT") == 1);
    }),
    ("Initial HTTP 503 retries and joins", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (count, _) => Task.FromResult(FakeHttp.Response(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        f.Start(); await f.Joined();
        Check.True(f.Handler.RoomRequests == 2, "Expected a retry after 503.");
    }),
    ("HTTP timeout retries without waiting for a game-state change", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (count, _) => count == 1 ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")) : Task.FromResult(FakeHttp.Response(HttpStatusCode.OK));
        f.Start(); await f.Joined();
        Check.True(f.Handler.RoomRequests == 2, "Timeout did not retry.");
    }),
    ("HTTP rate limits respect cooldown before retrying", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (count, _) => Task.FromResult(FakeHttp.Response(count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
        f.Start(); await Task.Delay(70);
        Check.True(f.Handler.RoomRequests == 1, "Rate limit retried without cooldown.");
        await f.Joined(); Check.True(f.Handler.RoomRequests == 2, "Rate limit did not recover.");
    }),
    ("Invalid HTTP input stops without a retry storm", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (_, _) => Task.FromResult(FakeHttp.Response(HttpStatusCode.BadRequest, "{\"message\":\"invalid\"}"));
        f.Start(); await Task.Delay(150);
        for (var i = 0; i < 100; i++) f.Start();
        Check.True(f.Handler.RoomRequests == 1 && f.Sockets.Count == 0, "Permanent input failure retried.");
    }),
    ("Transient WebSocket JOIN storage failure reconnects", async () =>
    {
        using var f = new Fixture(); var count = 0;
        f.ConfigureSocket = socket =>
        {
            if (Interlocked.Increment(ref count) != 1) return;
            socket.AutoJoin = false;
            socket.OnSend = payload => { if (payload.GetProperty("type").GetString() == "JOIN") socket.Feed(new { type = "ERROR", code = "STORAGE_UNAVAILABLE", message = "temporary" }); return Task.CompletedTask; };
        };
        f.Start(); await f.Joined();
        Check.True(f.Sockets.Count == 2, "JOIN did not recover.");
    }),
    ("Unknown non-spectator order is omitted from JOIN", async () =>
    {
        using var f = new Fixture(); f.Start(order: ""); await f.Joined();
        Check.True(f.Latest.JoinOrderPresent == false, "Null order would be rejected by this server.");
    }),
    ("Missing JOINED response reconnects instead of waiting forever", async () =>
    {
        using var f = new Fixture(joinTimeout: TimeSpan.FromMilliseconds(80)); var count = 0;
        f.ConfigureSocket = socket => socket.AutoJoin = Interlocked.Increment(ref count) != 1;
        f.Start(); await f.Joined(); Check.True(f.Sockets.Count == 2, "Missing join acknowledgement stalled.");
    }),
    ("Slow handshake does not consume the JOIN acknowledgement deadline", async () =>
    {
        using var f = new Fixture(joinTimeout: TimeSpan.FromMilliseconds(80)); f.ConnectDelay = TimeSpan.FromMilliseconds(150);
        f.ConfigureSocket = socket =>
        {
            socket.AutoJoin = false;
            socket.OnSend = payload =>
            {
                if (payload.GetProperty("type").GetString() == "JOIN")
                    _ = Task.Run(async () => { await Task.Delay(50); socket.Feed(new { type = "JOINED", participantId = socket.ParticipantId, messages = Array.Empty<object>(), participants = new[] { socket.Self() } }); });
                return Task.CompletedTask;
            };
        };
        f.Start(character: "spectator"); await f.Joined();
        Check.True(f.Sockets.Count == 1, "Handshake consumed the join response window.");
    }),
    ("Cancelled connection cannot apply any server message", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var old = typeof(PartyChatClient).GetField("_connection", flags)!.GetValue(f.Client)!;
        f.Start("222222"); await f.Joined();
        var before = f.Client.GetUiSnapshot();
        var process = typeof(PartyChatClient).GetMethod("ProcessServerMessage", flags)!;
        foreach (var json in new[]
        {
            "{\"type\":\"CHAT\",\"message\":{\"id\":\"old\",\"text\":\"old room\"}}",
            "{\"type\":\"JOIN_NOTICE\",\"notice\":{\"id\":\"old-join\",\"nickname\":\"old\"}}",
            "{\"type\":\"LEAVE_NOTICE\",\"notice\":{\"id\":\"old-leave\",\"nickname\":\"old\"}}",
            "{\"type\":\"HISTORY\",\"messages\":[{\"kind\":\"chat\",\"id\":\"old-history\",\"text\":\"old\"}]}",
            "{\"type\":\"ERROR\",\"code\":\"INVALID_ORDER\",\"message\":\"old error\"}",
            "{\"type\":\"PARTICIPANTS\",\"participants\":[]}",
            "{\"type\":\"JOINED\",\"messages\":[],\"participants\":[]}"
        }) process.Invoke(f.Client, new[] { json, old });
        var after = f.Client.GetUiSnapshot();
        Check.True(after.Revision == before.Revision && after.Status == before.Status && after.Messages.Count == 0, "Old socket mutated the new room.");
    }),
    ("A replaced socket in the same room cannot apply old responses", async () =>
    {
        using var f = new Fixture(presenceTimeout: TimeSpan.FromMilliseconds(80)); f.Start(character: "spectator"); await f.Joined();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var old = typeof(PartyChatClient).GetField("_connection", flags)!.GetValue(f.Client)!;
        f.Latest.AutoPresence = false; f.Start(character: "102");
        await Check.Eventually(() => f.Sockets.Count >= 2); await f.Joined();
        typeof(PartyChatClient).GetMethod("ProcessServerMessage", flags)!.Invoke(f.Client,
            new object[] { "{\"type\":\"ERROR\",\"code\":\"INVALID_ORDER\",\"message\":\"old socket\"}", old });
        Check.True(f.Client.GetUiSnapshot().Status == "연결됨", "Previous connection in same session mutated status.");
    }),
    ("Late send failure from an old room cannot modify the current room", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSocket = f.Latest;
        oldSocket.OnSend = async payload => { if (payload.GetProperty("type").GetString() == "CHAT") { await release.Task; throw new IOException("late old failure"); } };
        f.Client.SendChat("old send"); await Check.Eventually(() => oldSocket.Count("CHAT") == 1);
        f.Start("222222"); await f.Joined();
        release.SetResult(); await Task.Delay(80);
        Check.True(f.Client.GetUiSnapshot().Messages.Count == 0 && f.Client.GetUiSnapshot().Status == "연결됨", "Stale send changed current state.");
    }),
    ("Late HTTP failure from an old session cannot change the new status", async () =>
    {
        using var f = new Fixture();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Respond = (count, _) => count == 1 ? release.Task : Task.FromResult(FakeHttp.Response(HttpStatusCode.OK));
        f.Start(); await Check.Eventually(() => f.Handler.RoomRequests == 1);
        f.Start("222222"); await f.Joined();
        release.SetResult(FakeHttp.Response(HttpStatusCode.BadRequest, "{\"message\":\"old error\"}"));
        await Task.Delay(80);
        Check.True(f.Client.GetUiSnapshot().Status == "연결됨", "Old HTTP response overwrote status.");
    }),
    ("Unrelated presence broadcast does not unlock SET_ORDER", async () =>
    {
        using var f = new Fixture(); f.Start(character: "spectator", order: ""); await f.Joined();
        var socket = f.Latest; socket.AutoPresence = false;
        f.Start(character: "102", order: "P3"); await Check.Eventually(() => socket.Count("SET_CHARACTER") == 1);
        socket.Participants(); await Task.Delay(60);
        Check.True(socket.Count("SET_ORDER") == 0 && socket.Count("SET_CHARACTER") == 1, "Unrelated presence acknowledged the mutation.");
        socket.Character = "102"; socket.Order = "P1"; socket.Participants();
        await Check.Eventually(() => socket.Count("SET_ORDER") == 1);
        socket.Order = "P3"; socket.Participants();
        await Task.Delay(60);
        Check.True(socket.Count("SET_CHARACTER") == 1 && socket.Count("SET_ORDER") == 1, "Presence did not settle.");
    }),
    ("Presence storage ERROR clears pending and retries with delay", async () =>
    {
        using var f = new Fixture(); f.Start(character: "spectator"); await f.Joined();
        var socket = f.Latest; socket.AutoPresence = false;
        f.Start(character: "102", order: "P3"); await Check.Eventually(() => socket.Count("SET_CHARACTER") == 1);
        socket.Feed(new { type = "ERROR", code = "STORAGE_UNAVAILABLE", message = "temporary" });
        await Check.Eventually(() => f.Client.GetUiSnapshot().Status.Contains("temporary"));
        Check.True(socket.Count("SET_CHARACTER") == 1, "ERROR retried immediately.");
        socket.AutoPresence = true;
        await Check.Eventually(() => socket.Count("SET_ORDER") == 1);
        Check.True(socket.Character == "102" && socket.Order == "P3", "Presence stayed blocked.");
    }),
    ("Presence acknowledgement timeout reconnects", async () =>
    {
        using var f = new Fixture(presenceTimeout: TimeSpan.FromMilliseconds(100));
        f.Start(character: "spectator"); await f.Joined();
        f.Latest.AutoPresence = false; f.Start(character: "102", order: "P3");
        await Check.Eventually(() => f.Sockets.Count >= 2); await f.Joined();
        Check.True(f.Latest.Character == "102" && f.Latest.Order == "P3", "Timeout did not recover.");
    }),
    ("Permanent presence errors wait for a changed game state", async () =>
    {
        using var f = new Fixture(); f.Start(character: "spectator"); await f.Joined();
        var socket = f.Latest; socket.AutoPresence = false;
        f.Start(character: "102"); await Check.Eventually(() => socket.Count("SET_CHARACTER") == 1);
        socket.Feed(new { type = "ERROR", code = "INVALID_CHARACTER", message = "invalid character" });
        await Task.Delay(100); for (var i = 0; i < 50; i++) f.Start(character: "102");
        Check.True(socket.Count("SET_CHARACTER") == 1, "Rejected presence retried continuously.");
        socket.AutoPresence = true; f.Start(character: "103");
        await Check.Eventually(() => socket.Character == "103");
    }),
    ("Chat ERROR correlates clientMessageId without cancelling presence", async () =>
    {
        using var f = new Fixture(); f.Start(character: "spectator"); await f.Joined();
        var socket = f.Latest; socket.AutoPresence = false; socket.AutoChat = false;
        f.Start(character: "102", order: "P2"); await Check.Eventually(() => socket.Count("SET_CHARACTER") == 1);
        f.Client.SendChat("keep this draft"); await Check.Eventually(() => socket.Count("CHAT") == 1);
        var id = socket.Sent.Last(payload => payload.GetProperty("type").GetString() == "CHAT").GetProperty("clientMessageId").GetString();
        socket.Feed(new { type = "ERROR", code = "STORAGE_UNAVAILABLE", message = "chat rejected", clientMessageId = id });
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.Any(message => message.Text.Contains("chat rejected")));
        await Task.Delay(60);
        Check.True(socket.Count("SET_CHARACTER") == 1 && socket.Count("SET_ORDER") == 0, "Chat error unlocked presence.");
    }),
    ("Chat echoes replace optimistic messages and deduplicate", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        f.Client.SendChat("안녕"); await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.Any(message => !message.Id.StartsWith("local:")));
        Check.True(f.Client.GetUiSnapshot().Messages.Count == 1, "Echo duplicated the local message.");
        var message = f.Client.GetUiSnapshot().Messages[0];
        f.Latest.Feed(new { type = "CHAT", message = new { id = message.Id, clientMessageId = message.ClientMessageId, text = message.Text, participant = f.Latest.Self() } });
        await Task.Delay(40);
        Check.True(f.Client.GetUiSnapshot().Messages.Count == 1, "Repeated echo duplicated a message.");
    }),
    ("Unacknowledged chat preserves text and marks uncertainty", async () =>
    {
        using var f = new Fixture(messageTimeout: TimeSpan.FromMilliseconds(80), joinTimeout: TimeSpan.FromMilliseconds(100));
        f.Start(); await f.Joined(); f.Latest.AutoChat = false; f.Client.SendChat("preserved");
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.Any(message => message.Text.Contains("전송 확인")));
        Check.True(f.Client.GetUiSnapshot().Messages[0].Text.StartsWith("preserved"), "Unconfirmed text was lost.");
    }),
    ("Participant updates refresh retained message labels", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined(); f.Client.SendChat("label");
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.Count == 1 && !f.Client.GetUiSnapshot().Messages[0].Id.StartsWith("local:"));
        f.Start(character: "102", order: "P4");
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages[0].CharacterId == "102" && f.Client.GetUiSnapshot().Messages[0].Order == "P4");
    }),
    ("All sends are serialized per connection", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        f.Latest.OnSend = _ => Task.Delay(15);
        for (var i = 0; i < 8; i++) f.Client.SendChat("message " + i);
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.Count == 8 && f.Client.GetUiSnapshot().Messages.All(message => !message.Id.StartsWith("local:")));
        Check.True(f.Latest.MaxConcurrentSends == 1, "Concurrent WebSocket sends.");
    }),
    ("Message history and snapshot allocation are bounded", async () =>
    {
        using var f = new Fixture(); f.Start(); await f.Joined();
        for (var i = 0; i < 350; i++) f.Latest.Feed(new { type = "JOIN_NOTICE", notice = new { id = i.ToString(), nickname = "x" } });
        await Check.Eventually(() => f.Client.GetUiSnapshot().Messages.LastOrDefault()?.Id == "349");
        var first = f.Client.GetUiSnapshot(); var second = f.Client.GetUiSnapshot();
        Check.True(first.Messages.Count == 300 && first.Messages[0].Id == "50", "History limit failed.");
        Check.True(ReferenceEquals(first.Messages, second.Messages), "Unchanged snapshots reallocated history.");
    }),
    ("Protocol validates ASCII room ids and counts Unicode code points", async () =>
    {
        Check.True(!PartyProtocol.IsRoomId("１２３４５６") && PartyProtocol.IsRoomId("123456"), "Room id mismatch.");
        var emojis = string.Concat(Enumerable.Repeat("😀", 20));
        Check.True(PartyProtocol.TextLength(emojis) == 20 && PartyProtocol.LimitText(emojis, 1) == "😀", "Unicode truncation split a surrogate pair.");
        using var f = new Fixture(); f.Start(nickname: emojis); await f.Joined();
    }),
    ("Rapid room switch clears previous player identity even without lobby scan", () =>
    {
        var state = new GameSessionState(); state.ObservePhase("방"); state.ObserveRoomId("111111");
        state.SetOrder("P4"); state.ObservePhase("플레이"); state.SetCharacter("101"); state.ObservePhase("방");
        Check.True(state.ObserveRoomId("222222") && state.RoomId == "222222" && state.Order == "" && state.Character == "", "Stale room state remained.");
        state.SetOrder("P2"); Check.True(!state.ObserveRoomId("222222") && state.Order == "P2", "Same room reset identity.");
        return Task.CompletedTask;
    }),
    ("Next round and leaving a room invalidate appropriate state", () =>
    {
        var state = new GameSessionState(); state.ObservePhase("방"); state.ObserveRoomId("111111"); state.SetOrder("P2");
        state.ObservePhase("플레이"); state.SetCharacter("101"); state.ObservePhase("캐릭터 선택");
        Check.True(state.RoomId == "111111" && state.Order == "P2" && state.Character == "", "Next round kept stale character or lost room.");
        state.ObservePhase("기타"); Check.True(state.RoomId == "" && state.Order == "", "Leaving kept room identity."); return Task.CompletedTask;
    }),
    ("Missing room label during an active UI scan retains confirmed identity", () =>
    {
        var state = new GameSessionState(); state.ObservePhase("방"); state.ObserveRoomId("111111"); state.SetOrder("P2");
        Check.True(!state.ObserveRoomId("") && state.RoomId == "111111" && state.Order == "P2", "A transient UI read reset the session.");
        return Task.CompletedTask;
    }),
    ("Unknown screen expires without extending its grace on each scan", () =>
    {
        var state = new GameSessionState(); state.ObserveScreenPhase("방", 0); state.ObserveRoomId("111111"); state.SetOrder("P2");
        state.ObserveScreenPhase("기타", 2); state.ObserveScreenPhase("기타", 10); state.ObserveRoomId("");
        Check.True(state.ChatAvailable && state.RoomId == "111111", "Transition was cleared before the grace expired.");
        state.ObserveScreenPhase("기타", 2 + GameSessionState.TransitionGraceSeconds);
        Check.True(!state.ChatAvailable && state.RoomId == "" && state.Order == "", "Unknown screen retained a stale room forever.");
        return Task.CompletedTask;
    }),
    ("Recognized chat screens recover only the room after a long transition", () =>
    {
        foreach (var phase in new[] { "방", "캐릭터 선택", "플레이" })
        {
            var state = new GameSessionState(); state.ObserveScreenPhase("방", 0); state.ObserveRoomId("111111");
            state.SetOrder("P2"); state.SetCharacter("101");
            state.ObserveScreenPhase("기타", 1); state.ObserveScreenPhase("기타", 16); state.ObserveRoomId("");
            state.ObserveScreenPhase("기타", 40); state.ObserveRoomId("");
            Check.True(!state.ChatAvailable && state.RoomId == "", "Suspended identity reactivated on an unknown scan.");
            Check.True(state.ObserveScreenPhase(phase, 41) && state.ChatAvailable && state.RoomId == "111111"
                && state.Order == "" && state.Character == "", "Recovery lost the room or reused stale player details.");
        }
        return Task.CompletedTask;
    }),
    ("Explicit exit and reset discard suspended room recovery", () =>
    {
        foreach (var exit in new Action<GameSessionState>[]
        {
            state => state.ObserveScreenPhase("방 설정", 20),
            state => state.ObservePhase("기타"),
            state => state.Reset()
        })
        {
            var state = new GameSessionState(); state.ObserveScreenPhase("방", 0); state.ObserveRoomId("111111");
            state.ObserveScreenPhase("기타", 1); state.ObserveScreenPhase("기타", 16);
            exit(state); state.ObserveScreenPhase("캐릭터 선택", 40);
            Check.True(state.RoomId == "", "An explicit exit resurrected the previous room.");
        }
        return Task.CompletedTask;
    }),
    ("A newly observed room supersedes suspended recovery", () =>
    {
        var state = new GameSessionState(); state.ObserveScreenPhase("방", 0); state.ObserveRoomId("111111");
        state.ObserveScreenPhase("기타", 1); state.ObserveScreenPhase("기타", 16);
        state.ObserveRoomId("222222"); state.ObserveScreenPhase("캐릭터 선택", 30);
        Check.True(state.RoomId == "222222", "Recovery overwrote the newly observed room.");
        return Task.CompletedTask;
    }),
    ("Explicit room exit bypasses transition grace and later gaps start fresh", () =>
    {
        var state = new GameSessionState(); state.ObserveScreenPhase("방", 0); state.ObserveRoomId("111111");
        state.ObserveScreenPhase("기타", 1); state.ObserveScreenPhase("방 설정", 2);
        Check.True(!state.ChatAvailable && state.RoomId == "", "Explicit exit retained the old session.");
        state.ObserveScreenPhase("방", 20); state.ObserveRoomId("222222"); state.ObserveScreenPhase("기타", 21);
        Check.True(state.ChatAvailable && state.RoomId == "222222", "New room inherited the old transition timer.");
        state.Reset(); state.ObserveScreenPhase("기타", 22);
        Check.True(!state.ChatAvailable && state.RoomId == "", "Reset preserved a transition session.");
        return Task.CompletedTask;
    }),
    ("IME Enter sends committed text without another key press", () =>
    {
        var input = new ChatInputSubmitState();
        var token = input.RequestEnter(10, true);
        Check.True(token != 0 && !input.TryTakeReady(11, true, out _), "Composition text was sent before it committed.");
        Check.True(!input.TryTakeReady(12, false, out _) && input.TryTakeReady(13, false, out var taken)
            && taken == token && !input.TryTakeReady(14, false, out _), "One Enter did not send exactly once after commit.");
        return Task.CompletedTask;
    }),
    ("Explicit IME send waits for committed text and consumes only once", () =>
    {
        var input = new ChatInputSubmitState(); input.ObserveFrame(1, true); input.RequestExplicitSend(1);
        Check.True(!input.TryTakeReady(2, true, out _) && !input.TryTakeReady(3, false, out _)
            && input.TryTakeReady(4, false, out _) && !input.TryTakeReady(4, false, out _), "Explicit send was early or repeated.");
        input.RequestExplicitSend(5); input.Reset(); Check.True(!input.TryTakeReady(7, false, out _), "Closed overlay retained a send token."); return Task.CompletedTask;
    }),
    ("An Enter arriving on the IME commit frame sends on the next frame", () =>
    {
        var input = new ChatInputSubmitState();
        input.ObserveFrame(10, true);
        Check.True(input.RequestEnter(11, false) != 0 && !input.TryTakeReady(11, false, out _), "Commit frame submitted before text settled.");
        Check.True(input.TryTakeReady(12, false, out _) && !input.TryTakeReady(13, false, out _), "Commit-frame Enter needed another press.");
        return Task.CompletedTask;
    }),
    ("Native IME reports on a later frame retain the original pending send", () =>
    {
        var input = new ChatInputSubmitState(); var token = input.RequestEnter(10, true);
        Check.True(input.RequestEnter(11, false) == token && !input.TryTakeReady(11, false, out _)
            && input.TryTakeReady(12, false, out var taken) && taken == token, "IME reports replaced or delayed the pending send.");
        return Task.CompletedTask;
    }),
    ("Unfinished IME send expires and reset cancels a waiting Enter", () =>
    {
        var input = new ChatInputSubmitState(); input.RequestEnter(1, true);
        Check.True(!input.TryTakeReady(302, true, out _) && !input.TryTakeReady(304, false, out _), "Expired composition sent later.");
        input.RequestEnter(305, true); input.Reset();
        Check.True(!input.TryTakeReady(307, false, out _), "Reset left a pending IME send.");
        return Task.CompletedTask;
    }),
    ("Native key event and raw polling for one Enter share a token", () =>
    {
        var input = new ChatInputSubmitState();
        var first = input.RequestEnter(1, false);
        Check.True(first != 0 && input.RequestEnter(1, false) == first, "Native/raw duplicate restarted the token.");
        Check.True(input.TryTakeReady(2, false, out var taken) && taken == first && !input.TryTakeReady(2, false, out _), "Duplicate Enter submitted twice.");
        return Task.CompletedTask;
    }),
    ("Dispose aborts sockets and prevents new sessions", async () =>
    {
        var f = new Fixture(); f.Start(); await f.Joined(); var socket = f.Latest;
        f.Dispose(); f.Start("222222"); await Task.Delay(60);
        Check.True(socket.State != System.Net.WebSockets.WebSocketState.Open && f.Handler.RoomRequests == 1, "Dispose left a live session.");
    })
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} regression checks passed. No game or external server was used.");
return failures == 0 ? 0 : 1;
