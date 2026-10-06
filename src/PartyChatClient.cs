using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using BepInEx.Logging;

namespace AstralPartyChatPlugin;

internal sealed class PartyUiSnapshot
{
    public PartyUiSnapshot(int revision, string status, IReadOnlyList<ChatUiMessage> messages)
    {
        Revision = revision;
        Status = status;
        Messages = messages;
    }
    public int Revision { get; }
    public string Status { get; }
    public IReadOnlyList<ChatUiMessage> Messages { get; }
}

internal sealed partial class PartyChatClient : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualLogSource _log;
    private readonly HttpClient _http;
    private readonly PartyChatOptions _options;
    private readonly List<ChatUiMessage> _messages = new();
    private readonly Dictionary<string, string> _characterNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _pendingMessages = new(StringComparer.Ordinal);
    private PartySession? _session;
    private PartyConnection? _connection;
    private string _desiredCharacter = "spectator";
    private string _desiredOrder = string.Empty;
    private string _status = "게임 방 대기 중";
    private int _revision;
    private int _snapshotRevision = -1;
    private IReadOnlyList<ChatUiMessage> _snapshotMessages = Array.Empty<ChatUiMessage>();
    private PartyUiSnapshot? _snapshot;
    private bool _disposed;

    public PartyChatClient(ManualLogSource log, HttpClient? http = null, PartyChatOptions? options = null)
    {
        _log = log;
        _http = http ?? new HttpClient();
        _options = options ?? new PartyChatOptions();
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AstralPartyChatPlugin/" + AstralPartyChatPlugin.PluginVersion);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", _options.SiteOrigin);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public void UpdateGameState(ChatGameState state)
    {
        var roomId = (state.RoomId ?? string.Empty).Trim();
        var nickname = (state.Nickname ?? string.Empty).Trim();
        if (!state.Available || !PartyProtocol.IsRoomId(roomId)
            || nickname.Length == 0 || PartyProtocol.TextLength(nickname) > 20)
        {
            StopSession("게임 방 대기 중");
            return;
        }
        PartySession? oldSession = null;
        PartyConnection? oldConnection = null;
        PartySession? newSession = null;
        PartyConnection? connection;
        lock (_gate)
        {
            if (_disposed) return;
            var character = PartyProtocol.NormalizeCharacter(state.CharacterId);
            var order = character == "spectator" ? string.Empty : PartyProtocol.NormalizeOrder(state.Order);
            if (_connection != null && (character != _desiredCharacter || order != _desiredOrder))
            {
                _connection.BlockedPresence = string.Empty;
                _connection.PresenceRetryAt = default;
            }
            _desiredCharacter = character;
            _desiredOrder = order;
            if (_session == null || _session.RoomId != roomId || _session.Nickname != nickname)
            {
                oldSession = _session;
                oldConnection = _connection;
                newSession = _session = new PartySession(roomId, nickname);
                _connection = null;
                _messages.Clear();
                _pendingMessages.Clear();
                _revision++;
                _status = "채팅방 연결 준비 중";
            }
            connection = _connection;
        }
        oldSession?.Cancel();
        oldConnection?.Abort();
        if (newSession != null) _ = RunSessionAsync(newSession);
        if (connection != null) SchedulePresenceUpdate(connection);
    }

    public void SendChat(string text)
    {
        var trimmed = PartyProtocol.LimitText((text ?? string.Empty).Trim(), PartyProtocol.MaxTextLength);
        if (trimmed.Length == 0) return;
        PartyConnection connection;
        string clientMessageId;
        lock (_gate)
        {
            if (_disposed || _connection == null || !_connection.Joined || _connection.Socket.State != WebSocketState.Open)
            {
                if (!_disposed) _status = "채팅 서버에 연결된 뒤 전송할 수 있습니다.";
                return;
            }
            connection = _connection;
            clientMessageId = Guid.NewGuid().ToString("D");
            _messages.Add(new ChatUiMessage
            {
                Id = "local:" + clientMessageId,
                ClientMessageId = clientMessageId,
                ParticipantId = connection.ParticipantId,
                Sender = connection.Session.Nickname,
                CharacterId = _desiredCharacter,
                CharacterName = ResolveCharacterNameLocked(_desiredCharacter),
                Order = _desiredOrder,
                Text = trimmed
            });
            _pendingMessages[clientMessageId] = DateTime.UtcNow + _options.MessageTimeout;
            TrimMessagesLocked();
            _revision++;
        }
        _ = SendChatAsync(connection, trimmed, clientMessageId);
    }

    public PartyUiSnapshot GetUiSnapshot()
    {
        lock (_gate)
        {
            if (_snapshotRevision != _revision)
            {
                _snapshotMessages = Array.AsReadOnly(_messages.ToArray());
                _snapshotRevision = _revision;
            }
            if (_snapshot == null || _snapshot.Revision != _revision
                || !string.Equals(_snapshot.Status, _status, StringComparison.Ordinal))
                _snapshot = new PartyUiSnapshot(_revision, _status, _snapshotMessages);
            return _snapshot;
        }
    }

    private bool StopSession(string status, bool disposing = false)
    {
        PartySession? session;
        PartyConnection? connection;
        lock (_gate)
        {
            if (_disposed) return false;
            if (disposing) _disposed = true;
            session = _session;
            connection = _connection;
            _session = null;
            _connection = null;
            if (session != null)
            {
                _messages.Clear();
                _pendingMessages.Clear();
                _revision++;
            }
            _status = status;
        }
        session?.Cancel();
        connection?.Abort();
        return true;
    }

    public void Dispose()
    {
        if (StopSession("채팅 종료됨", disposing: true)) _http.Dispose();
    }

    private void SetStatus(PartySession session, string status)
    {
        lock (_gate) { if (IsCurrentSessionLocked(session)) _status = status; }
    }
    private bool IsCurrentSessionLocked(PartySession session) => !_disposed
        && ReferenceEquals(_session, session) && !session.Token.IsCancellationRequested;
    private bool IsCurrentConnectionLocked(PartyConnection connection) =>
        IsCurrentSessionLocked(connection.Session) && ReferenceEquals(_connection, connection)
        && !connection.Token.IsCancellationRequested;

    private void TrimMessagesLocked()
    {
        if (_messages.Count > PartyProtocol.HistoryLimit)
            _messages.RemoveRange(0, _messages.Count - PartyProtocol.HistoryLimit);
        var retained = new HashSet<string>(_messages.Select(message => message.ClientMessageId), StringComparer.Ordinal);
        foreach (var id in _pendingMessages.Keys.Where(id => !retained.Contains(id)).ToArray()) _pendingMessages.Remove(id);
    }
    private string ResolveCharacterNameLocked(string character)
    {
        var normalized = PartyProtocol.NormalizeCharacter(character);
        if (normalized == "spectator") return "관전";
        return _characterNames.TryGetValue(normalized, out var name) ? name : normalized;
    }
    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string ExtractHttpError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var message = GetString(root, "message");
            if (message.Length == 0) message = GetString(root, "statusMessage");
            if (message.Length > 0) return PartyProtocol.LimitText(message, 200);
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? "서버 오류" : PartyProtocol.LimitText(body.Trim(), 200);
    }

    private sealed class PartySession : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        public PartySession(string roomId, string nickname)
        {
            RoomId = roomId;
            Nickname = nickname;
            Token = _cancellation.Token;
        }
        public string RoomId { get; }
        public string Nickname { get; }
        public bool RoomEnsured { get; set; }
        public CancellationToken Token { get; }
        public void Cancel() { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() => _cancellation.Dispose();
    }
    private sealed class PartyConnection : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        public PartyConnection(PartySession session, WebSocket socket)
        {
            Session = session;
            Socket = socket;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            Token = _cancellation.Token;
        }
        public PartySession Session { get; }
        public WebSocket Socket { get; }
        public CancellationToken Token { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public bool Joined { get; set; }
        public string ParticipantId { get; set; } = string.Empty;
        public string ServerCharacter { get; set; } = "spectator";
        public string ServerOrder { get; set; } = string.Empty;
        public PresenceMutation? PendingPresence { get; set; }
        public DateTime PresenceRetryAt { get; set; }
        public string BlockedPresence { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastReceivedAt { get; set; } = DateTime.UtcNow;
        public void Abort()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            Abort();
            Socket.Dispose();
            _cancellation.Dispose();
            // In-flight send tasks can still be unwinding their finally block.
        }
    }
    private sealed record PresenceMutation(string Type, string Value, DateTime Deadline);
    private sealed class FatalPartyException : Exception
    {
        public FatalPartyException(string message) : base(message) { }
    }
    private sealed class RetryPartyException : Exception
    {
        public RetryPartyException(string message, TimeSpan? delay = null) : base(message) => Delay = delay;
        public TimeSpan? Delay { get; }
    }
}
