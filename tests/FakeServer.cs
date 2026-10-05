using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AstralPartyChatPlugin;
using BepInEx.Logging;

internal sealed class Fixture : IDisposable
{
    public readonly FakeHttp Handler = new();
    public readonly ConcurrentQueue<FakeSocket> Sockets = new();
    public Action<FakeSocket>? ConfigureSocket;
    public TimeSpan ConnectDelay;
    public readonly PartyChatClient Client;
    public Fixture(TimeSpan? presenceTimeout = null, TimeSpan? messageTimeout = null, TimeSpan? joinTimeout = null)
    {
        Client = new PartyChatClient(new ManualLogSource(), new HttpClient(Handler), new PartyChatOptions
        {
            RetryDelay = TimeSpan.FromMilliseconds(30), MaxRetryDelay = TimeSpan.FromMilliseconds(60),
            RateLimitRetryDelay = TimeSpan.FromMilliseconds(150),
            PresenceRetryDelay = TimeSpan.FromMilliseconds(50),
            PresenceTimeout = presenceTimeout ?? TimeSpan.FromSeconds(2),
            MessageTimeout = messageTimeout ?? TimeSpan.FromSeconds(2),
            JoinTimeout = joinTimeout ?? TimeSpan.FromSeconds(2),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250), HeartbeatTimeout = TimeSpan.FromSeconds(3),
            CreateSocket = () =>
            {
                var socket = new FakeSocket();
                ConfigureSocket?.Invoke(socket);
                Sockets.Enqueue(socket);
                return socket;
            },
            ConnectSocket = async (_, _, token) => { if (ConnectDelay > TimeSpan.Zero) await Task.Delay(ConnectDelay, token); token.ThrowIfCancellationRequested(); }
        });
    }
    public void Start(string room = "123456", string character = "101", string order = "P1", string nickname = "tester") =>
        Client.UpdateGameState(State(room, character, order, nickname));
    public static ChatGameState State(string room, string character = "101", string order = "P1", string nickname = "tester") =>
        new() { Available = true, RoomId = room, Nickname = nickname, CharacterId = character, Order = order };
    public FakeSocket Latest => Sockets.Last();
    public Task Joined() => Check.Eventually(() => Client.GetUiSnapshot().Status == "연결됨");
    public void Dispose() => Client.Dispose();
}

internal sealed class FakeHttp : HttpMessageHandler
{
    public int RoomRequests;
    public readonly ConcurrentQueue<JsonElement> RoomBodies = new();
    public Func<int, CancellationToken, Task<HttpResponseMessage>>? Respond;
    public static HttpResponseMessage Response(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Get)
            return Response(HttpStatusCode.OK, "[{\"id\":\"101\",\"name\":\"test hero\"}]");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        RoomBodies.Enqueue(body.RootElement.Clone());
        var count = Interlocked.Increment(ref RoomRequests);
        return Respond != null ? await Respond(count, token) : Response(HttpStatusCode.OK);
    }
}

internal sealed class FakeSocket : WebSocket
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private WebSocketState _state = WebSocketState.Open;
    public readonly ConcurrentQueue<JsonElement> Sent = new();
    public Func<JsonElement, Task>? OnSend;
    public bool AutoJoin = true;
    public bool AutoPresence = true;
    public bool AutoChat = true;
    public string ParticipantId = Guid.NewGuid().ToString("N");
    public string Character = "spectator";
    public string Order = "";
    public string Nickname = "tester";
    public bool? JoinOrderPresent;
    public int ActiveSends;
    public int MaxConcurrentSends;
    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public void Feed(object value) => _incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
    public object Self() => new { id = ParticipantId, nickname = Nickname, characterId = Character, order = Order.Length > 0 ? Order : null };
    public void Participants() => Feed(new { type = "PARTICIPANTS", participants = new[] { Self() } });
    public int Count(string type) => Sent.Count(element => element.GetProperty("type").GetString() == type);
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
    {
        var sends = Interlocked.Increment(ref ActiveSends);
        MaxConcurrentSends = Math.Max(MaxConcurrentSends, sends);
        try
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            var payload = document.RootElement.Clone();
            Sent.Enqueue(payload);
            if (OnSend != null) await OnSend(payload);
            var kind = payload.GetProperty("type").GetString();
            if (kind == "JOIN" && AutoJoin)
            {
                Character = payload.GetProperty("characterId").GetString()!;
                Nickname = payload.GetProperty("nickname").GetString()!;
                JoinOrderPresent = payload.TryGetProperty("order", out var order);
                Order = Character == "spectator" ? "" : order.ValueKind == JsonValueKind.String ? order.GetString()! : "P1";
                Feed(new { type = "JOINED", participantId = ParticipantId, messages = Array.Empty<object>(), participants = new[] { Self() } });
            }
            else if (kind == "SET_CHARACTER" && AutoPresence)
            {
                Character = payload.GetProperty("characterId").GetString()!;
                Order = Character == "spectator" ? "" : Order.Length > 0 ? Order : "P1";
                Participants();
            }
            else if (kind == "SET_ORDER" && AutoPresence)
            {
                Order = payload.GetProperty("order").GetString()!;
                Participants();
            }
            else if (kind == "CHAT" && AutoChat)
                Feed(new { type = "CHAT", message = new { kind = "chat", id = Guid.NewGuid().ToString(), clientMessageId = payload.GetProperty("clientMessageId").GetString(), text = payload.GetProperty("text").GetString(), participant = Self() } });
            else if (kind == "PING") Feed(new { type = "PONG" });
        }
        finally { Interlocked.Decrement(ref ActiveSends); }
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
    {
        var data = await _incoming.Reader.ReadAsync(token);
        if (data.Length > buffer.Count) throw new InvalidOperationException("Test frame exceeds buffer.");
        data.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(data.Length, WebSocketMessageType.Text, true);
    }
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() => _state = WebSocketState.Closed;
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
    { _state = WebSocketState.Closed; return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => CloseAsync(status, description, token);
}

internal static class Check
{
    public static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task Eventually(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Expected condition was not reached.");
            await Task.Delay(10);
        }
    }
}
