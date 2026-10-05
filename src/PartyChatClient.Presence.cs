using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace AstralParty.Chat;

internal sealed partial class PartyChatClient
{
    private void ApplyParticipantsLocked(JsonElement participants, PartyConnection connection)
    {
        if (participants.ValueKind != JsonValueKind.Array) return;
        var states = new Dictionary<string, (string Character, string Order)>(StringComparer.Ordinal);
        foreach (var participant in participants.EnumerateArray())
        {
            var id = GetString(participant, "id");
            if (id.Length == 0) continue;
            states[id] = (PartyProtocol.NormalizeCharacter(GetString(participant, "characterId")),
                PartyProtocol.NormalizeOrder(GetString(participant, "order")));
        }
        if (states.TryGetValue(connection.ParticipantId, out var self))
        {
            connection.ServerCharacter = self.Character;
            connection.ServerOrder = self.Order;
            // An unrelated presence broadcast must not acknowledge our outstanding mutation.
            if (connection.PendingPresence is { } pending
                && (pending.Type == "SET_CHARACTER" ? self.Character == pending.Value : self.Order == pending.Value))
            {
                connection.PendingPresence = null;
                connection.BlockedPresence = string.Empty;
                connection.PresenceRetryAt = default;
                _status = "연결됨";
            }
        }
        var changed = false;
        for (var index = 0; index < _messages.Count; index++)
        {
            var message = _messages[index];
            if (message.IsSystem || !states.TryGetValue(message.ParticipantId, out var state)
                || (state.Character == message.CharacterId && state.Order == message.Order)) continue;
            _messages[index] = new ChatUiMessage
            {
                Id = message.Id, ClientMessageId = message.ClientMessageId, ParticipantId = message.ParticipantId,
                Sender = message.Sender, CharacterId = state.Character,
                CharacterName = ResolveCharacterNameLocked(state.Character), Order = state.Order, Text = message.Text
            };
            changed = true;
        }
        if (changed) _revision++;
    }

    private static string PresenceKey(string type, string value) => type + ":" + value;

    private void SchedulePresenceUpdate(PartyConnection connection)
    {
        PresenceMutation mutation;
        lock (_gate)
        {
            if (!IsCurrentConnectionLocked(connection) || !connection.Joined
                || connection.PendingPresence != null || DateTime.UtcNow < connection.PresenceRetryAt) return;
            string type;
            string value;
            if (_desiredCharacter != connection.ServerCharacter) { type = "SET_CHARACTER"; value = _desiredCharacter; }
            else if (_desiredCharacter != "spectator" && _desiredOrder.Length > 0 && _desiredOrder != connection.ServerOrder)
            { type = "SET_ORDER"; value = _desiredOrder; }
            else return;
            if (connection.BlockedPresence == PresenceKey(type, value)) return;
            mutation = new PresenceMutation(type, value, DateTime.UtcNow + _options.PresenceTimeout);
            connection.PendingPresence = mutation;
        }
        _ = PushPresenceAsync(connection, mutation);
    }

    private async Task PushPresenceAsync(PartyConnection connection, PresenceMutation mutation)
    {
        try
        {
            object payload = mutation.Type == "SET_CHARACTER"
                ? new { type = mutation.Type, characterId = mutation.Value }
                : new { type = mutation.Type, order = mutation.Value };
            await SendJsonAsync(connection, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(connection)) return;
                if (ReferenceEquals(connection.PendingPresence, mutation)) connection.PendingPresence = null;
                connection.PresenceRetryAt = DateTime.UtcNow + _options.PresenceRetryDelay;
                _status = "참가 정보 갱신 실패: " + ex.Message;
            }
            connection.Abort();
        }
    }
}
