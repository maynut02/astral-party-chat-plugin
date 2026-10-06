using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AstralPartyChatPlugin;

internal sealed partial class PartyChatClient
{
    private void ProcessServerMessage(string json, PartyConnection connection)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var updatePresence = false;
        lock (_gate)
        {
            // Check and apply atomically: cancellation can race any incoming frame.
            if (!IsCurrentConnectionLocked(connection)) return;
            connection.LastReceivedAt = DateTime.UtcNow;
            switch (GetString(root, "type"))
            {
                case "JOINED":
                {
                    var messages = root.TryGetProperty("messages", out var history) ? ParseRoomMessagesLocked(history) : new();
                    var confirmedIds = new HashSet<string>(messages.Select(message => message.ClientMessageId), StringComparer.Ordinal);
                    var unconfirmed = _messages.Where(message => message.Id.StartsWith("local:", StringComparison.Ordinal)
                        && !confirmedIds.Contains(message.ClientMessageId)).ToArray();
                    _messages.Clear();
                    _messages.AddRange(messages);
                    _messages.AddRange(unconfirmed);
                    foreach (var id in confirmedIds) _pendingMessages.Remove(id);
                    connection.ParticipantId = GetString(root, "participantId");
                    connection.Joined = true;
                    connection.PendingPresence = null;
                    if (root.TryGetProperty("participants", out var participants)) ApplyParticipantsLocked(participants, connection);
                    _status = "연결됨";
                    TrimMessagesLocked();
                    _revision++;
                    updatePresence = true;
                    break;
                }
                case "CHAT":
                    if (root.TryGetProperty("message", out var chat)) ApplyChatMessageLocked(chat);
                    break;
                case "JOIN_NOTICE":
                case "LEAVE_NOTICE":
                    if (root.TryGetProperty("notice", out var notice)) AddNoticeLocked(notice, GetString(root, "type") == "JOIN_NOTICE");
                    break;
                case "HISTORY":
                    if (root.TryGetProperty("messages", out var older)) PrependHistoryLocked(older);
                    break;
                case "PARTICIPANTS":
                    if (root.TryGetProperty("participants", out var participantArray))
                    {
                        ApplyParticipantsLocked(participantArray, connection);
                        updatePresence = true;
                    }
                    break;
                case "ERROR":
                    HandleServerErrorLocked(root, connection);
                    break;
                case "PONG": break;
            }
        }
        if (updatePresence) SchedulePresenceUpdate(connection);
    }

    private void HandleServerErrorLocked(JsonElement root, PartyConnection connection)
    {
        var message = GetString(root, "message");
        if (message.Length == 0) message = "알 수 없는 오류";
        var code = GetString(root, "code");
        _status = "오류: " + message;
        if (!connection.Joined)
        {
            if (code == "ROOM_NOT_FOUND") connection.Session.RoomEnsured = false;
            if (code is "STORAGE_UNAVAILABLE" or "ROOM_NOT_FOUND" or "NICKNAME_TAKEN" or "CONNECTION_CLOSED" or "JOIN_RATE_LIMITED")
                throw new RetryPartyException(code == "NICKNAME_TAKEN"
                    ? "동일 닉네임의 접속이 종료되기를 기다리는 중입니다. " + message
                    : message, code == "JOIN_RATE_LIMITED" ? _options.RateLimitRetryDelay : null);
            throw new FatalPartyException(message);
        }
        var clientMessageId = GetString(root, "clientMessageId");
        if (clientMessageId.Length > 0)
            FailMessageLocked(clientMessageId, "메시지를 보내지 못했습니다: " + message);
        // A correlated chat error can also invalidate the whole room.
        // Retain the failed message, then let the connection recover.
        if (code == "NOT_IN_ROOM")
        {
            connection.Session.RoomEnsured = false;
            throw new RetryPartyException(message);
        }
        if (clientMessageId.Length > 0) return;
        if (connection.PendingPresence is { } pending)
        {
            connection.PendingPresence = null;
            connection.PresenceRetryAt = DateTime.UtcNow + _options.PresenceRetryDelay;
            // Invalid inputs wait for a different detected state instead of retrying every frame.
            if (code is "INVALID_CHARACTER" or "INVALID_ORDER" or "SPECTATOR_ORDER_DISABLED")
                connection.BlockedPresence = PresenceKey(pending.Type, pending.Value);
        }
    }

    private void ApplyChatMessageLocked(JsonElement element)
    {
        var parsed = ParseChatMessageLocked(element);
        if (parsed == null) return;
        var existing = _messages.FindIndex(message => message.Id == parsed.Id
            || (parsed.ClientMessageId.Length > 0 && message.ClientMessageId == parsed.ClientMessageId
                && message.Id.StartsWith("local:", StringComparison.Ordinal)));
        if (existing >= 0) _messages[existing] = parsed;
        else _messages.Add(parsed);
        _pendingMessages.Remove(parsed.ClientMessageId);
        TrimMessagesLocked();
        _revision++;
    }

    private void AddNoticeLocked(JsonElement element, bool joining)
    {
        var id = GetString(element, "id");
        if (id.Length == 0) id = Guid.NewGuid().ToString("D");
        if (_messages.Any(message => message.Id == id)) return;
        _messages.Add(ParseNotice(element, id, joining));
        TrimMessagesLocked();
        _revision++;
    }

    private static ChatUiMessage ParseNotice(JsonElement element, string id, bool joining)
    {
        var nickname = GetString(element, "nickname");
        return new ChatUiMessage
        {
            Id = id,
            Text = nickname.Length == 0
                ? (joining ? "참가자가 채팅방에 입장했습니다." : "참가자가 채팅방에서 나갔습니다.")
                : nickname + (joining ? "님이 채팅방에 입장했습니다." : "님이 채팅방에서 나갔습니다."),
            IsSystem = true
        };
    }

    private void PrependHistoryLocked(JsonElement history)
    {
        var older = ParseRoomMessagesLocked(history);
        var confirmed = new HashSet<string>(older.Select(message => message.ClientMessageId).Where(id => id.Length > 0), StringComparer.Ordinal);
        var removed = _messages.RemoveAll(message => message.Id.StartsWith("local:", StringComparison.Ordinal)
            && confirmed.Contains(message.ClientMessageId));
        foreach (var id in confirmed) _pendingMessages.Remove(id);
        var known = new HashSet<string>(_messages.Select(message => message.Id), StringComparer.Ordinal);
        var unique = older.Where(message => known.Add(message.Id)).ToList();
        if (unique.Count == 0 && removed == 0) return;
        _messages.InsertRange(0, unique);
        TrimMessagesLocked();
        _revision++;
    }

    private List<ChatUiMessage> ParseRoomMessagesLocked(JsonElement array)
    {
        var messages = new List<ChatUiMessage>();
        if (array.ValueKind != JsonValueKind.Array) return messages;
        foreach (var item in array.EnumerateArray())
        {
            switch (GetString(item, "kind"))
            {
                case "chat":
                    var chat = ParseChatMessageLocked(item);
                    if (chat != null) messages.Add(chat);
                    break;
                case "join": case "leave":
                    var id = GetString(item, "id");
                    if (id.Length > 0) messages.Add(ParseNotice(item, id, GetString(item, "kind") == "join"));
                    break;
            }
        }
        return messages;
    }

    private ChatUiMessage? ParseChatMessageLocked(JsonElement element)
    {
        var id = GetString(element, "id");
        var text = GetString(element, "text");
        if (id.Length == 0 || text.Length == 0) return null;
        var participant = element.TryGetProperty("participant", out var value) ? value : default;
        var character = PartyProtocol.NormalizeCharacter(GetString(participant, "characterId"));
        var sender = GetString(participant, "nickname");
        return new ChatUiMessage
        {
            Id = id, ClientMessageId = GetString(element, "clientMessageId"),
            ParticipantId = GetString(participant, "id"), Sender = sender.Length > 0 ? sender : "알 수 없음",
            CharacterId = character, CharacterName = ResolveCharacterNameLocked(character),
            Order = PartyProtocol.NormalizeOrder(GetString(participant, "order")), Text = text
        };
    }

    private async Task SendChatAsync(PartyConnection connection, string text, string clientMessageId)
    {
        try { await SendJsonAsync(connection, new { type = "CHAT", text, clientMessageId }).ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(connection)) return;
                FailMessageLocked(clientMessageId, "메시지를 보내지 못했습니다: " + ex.Message);
                _status = "메시지 전송 실패";
            }
            connection.Abort();
        }
    }

    private void FailPendingMessagesLocked(string reason)
    {
        foreach (var id in _pendingMessages.Keys.ToArray()) FailMessageLocked(id, reason);
    }

    private void FailMessageLocked(string clientMessageId, string reason)
    {
        if (!_pendingMessages.Remove(clientMessageId)) return;
        var index = _messages.FindIndex(message => message.Id == "local:" + clientMessageId);
        if (index < 0) return;
        var original = _messages[index];
        _messages[index] = new ChatUiMessage
        {
            Id = original.Id, ClientMessageId = original.ClientMessageId, ParticipantId = original.ParticipantId,
            Sender = original.Sender, CharacterId = original.CharacterId, CharacterName = original.CharacterName,
            Order = original.Order, Text = original.Text + "\n[" + PartyProtocol.LimitText(reason, 200) + "]"
        };
        _revision++;
    }
}
