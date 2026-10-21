using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    private const int MaximumRenderedMessages = 300;
    private const int MaximumDisplayedMessageLength = 1400;
    private const float MinimumMessageRowHeight = 90f;
    private const float MaximumMessageRowHeight = 42000f;
    private const float MessageTextLeft = 72f;
    private const float PortraitSize = 60f;
    private const float RowGap = 6f;
    private const float ChatWheelStep = 100f;
    private const float ChatBottomFollowDistance = 32f;
    private static readonly Color SenderTextColor = new Color32(145, 145, 145, 255);
    private static List<ChatUiMessage> _messages = new();
    private static List<string> _messageIdentities = new();
    private static readonly List<RenderedChatRow> RenderedChatRows = new(MaximumRenderedMessages);
    private static string _renderedPortraitRoomId = string.Empty;
    private static string _renderedPortraitPhase = string.Empty;
    private static float _nextRenderedPortraitRefreshAt;
    private static bool _messagesPresentationInvalid;

    public static void SetChatMessages(IEnumerable<ChatUiMessage> messages)
    {
        var bounded = new Queue<ChatUiMessage>(MaximumRenderedMessages);
        if (messages != null)
        {
            foreach (var message in messages)
            {
                if (message == null)
                    continue;

                if (bounded.Count == MaximumRenderedMessages)
                    bounded.Dequeue();
                bounded.Enqueue(message);
            }
        }

        var next = bounded.ToList();
        var nextIdentities = GetChatMessageIdentities(next);
        ObserveChatScrollMessages(_messageIdentities, nextIdentities);
        _messages = next;
        _messageIdentities = nextIdentities;
        RenderChatMessages(force: false);
    }

    private static List<string> GetChatMessageIdentities(IReadOnlyList<ChatUiMessage> messages)
    {
        var identities = new List<string>(messages.Count);
        var occurrences = new Dictionary<string, int>(messages.Count, StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var identity = BuildMessageIdentity(message);
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            identities.Add(occurrence == 0 ? identity : identity + "\u001e"
                + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return identities;
    }

    private static void ObserveChatScrollMessages(
        List<string> previousIdentities,
        List<string> nextIdentities)
    {
        ObserveChatScrollConnectionStatus();
        var present = new HashSet<string>(nextIdentities, StringComparer.Ordinal);
        UnreadChatMessageIdentities.RemoveWhere(identity => !present.Contains(identity));
        var tailIndex = previousIdentities.Count == 0
            ? -1 : nextIdentities.LastIndexOf(previousIdentities[previousIdentities.Count - 1]);

        // A prepend, replay, echo acknowledgement or participant update is not
        // a new arrival. Only unknown identities after the previous tail count.
        // A non-overlapping replacement establishes a fresh history baseline.
        if (nextIdentities.Count == 0 || previousIdentities.Count == 0 || tailIndex < 0)
            UnreadChatMessageIdentities.Clear();
        else if (!_chatScrollHistoryPending && !IsChatNearBottom())
        {
            var known = new HashSet<string>(previousIdentities, StringComparer.Ordinal);
            for (var index = tailIndex + 1; index < nextIdentities.Count; index++)
                if (!known.Contains(nextIdentities[index]))
                    UnreadChatMessageIdentities.Add(nextIdentities[index]);
        }

        if (IsChatNearBottom())
            UnreadChatMessageIdentities.Clear();
        if (_chatScrollHistoryPending && string.Equals(_chatStatus, "연결됨", StringComparison.Ordinal))
            _chatScrollHistoryPending = false;
    }

    private static void RenderChatMessages(bool force)
    {
        if (_chatContent == null)
            return;

        var oldRows = RenderedChatRows.ToArray();
        var rowWidth = GetChatMessageRowWidth();
        var widthChanged = oldRows.Any(row => Math.Abs(row.Width - rowWidth) > 0.5f);
        var wasAtBottom = IsChatNearBottom() || (oldRows.Length == 0 && _messages.Count > 0);
        var previousTopOffset = _chatScrollTop;
        var previousAnchorY = oldRows.Length == 0 ? 0f : oldRows[0].Y;

        var available = new Dictionary<string, Queue<RenderedChatRow>>(oldRows.Length, StringComparer.Ordinal);
        foreach (var row in oldRows)
        {
            if (!available.TryGetValue(row.Identity, out var rows))
            {
                rows = new Queue<RenderedChatRow>(1);
                available.Add(row.Identity, rows);
            }
            rows.Enqueue(row);
            if (row.Y <= previousTopOffset)
                previousAnchorY = row.Y;
        }

        var nextRows = new List<RenderedChatRow>(_messages.Count);
        var retained = new HashSet<RenderedChatRow>(_messages.Count);
        var changed = force || widthChanged || oldRows.Length != _messages.Count;
        var textScalePercent = ChatLayout.Percent(_textScale);
        RenderedChatRow? anchorRow = null;
        var anchorPreviousY = float.PositiveInfinity;

        try
        {
            for (var index = 0; index < _messages.Count; index++)
            {
                var message = _messages[index];
                var identity = _messageIdentities[index];
                var renderKey = BuildMessageRenderKey(message, identity, textScalePercent);
                RenderedChatRow? previous = null;
                if (available.TryGetValue(identity, out var matchingRows)
                    && matchingRows.Count > 0)
                    previous = matchingRows.Dequeue();

                var row = previous != null
                    && string.Equals(previous.RenderKey, renderKey, StringComparison.Ordinal)
                    ? previous : CreateRenderedRow(message, identity, renderKey);
                // Own every completed creation before another row can fail.
                if (!retained.Add(row))
                    changed = true;
                nextRows.Add(row);
                if (!ReferenceEquals(previous, row))
                    changed = true;

                // Presentation replacement preserves the logical FIFO match.
                // If the top row was trimmed, use its first surviving successor.
                if (previous != null && previous.Y >= previousAnchorY && previous.Y < anchorPreviousY)
                {
                    anchorRow = row;
                    anchorPreviousY = previous.Y;
                }
            }

            if (!changed && oldRows.Length == nextRows.Count)
            {
                for (var index = 0; index < oldRows.Length; index++)
                {
                    if (ReferenceEquals(oldRows[index], nextRows[index]))
                        continue;

                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                RefreshChatScrollControlPresentation();
                _messagesPresentationInvalid = false;
                return;
            }

            var y = RowGap;
            foreach (var row in nextRows)
            {
                if (force || widthChanged)
                {
                    MeasureRenderedRow(row);
                    ApplyCdnPortrait(row);
                }

                var height = SanitizeRowHeight(row.Height);
                row.Y = y;
                row.Root.SetXY(0f, y);
                row.Root.SetSize(rowWidth, height);
                y += height + RowGap;
            }

            var viewportHeight = GetChatScrollViewportHeight();
            _chatRowsHeight = float.IsFinite(y) ? y + 2f : 0f;
            _chatContentHeight = Math.Max(viewportHeight, _chatRowsHeight);
            _chatContent.SetSize(rowWidth, _chatContentHeight);

            RenderedChatRows.Clear();
            RenderedChatRows.AddRange(nextRows);
            var anchoredTopOffset = anchorRow == null ? previousTopOffset
                : anchorRow.Y + (anchorPreviousY == previousAnchorY ? previousTopOffset - anchorPreviousY : 0f);
            var scrollable = GetChatScrollableHeight();
            SetChatScrollTop(wasAtBottom || scrollable <= 0.5f ? scrollable : anchoredTopOffset);
        }
        catch
        {
            _messagesPresentationInvalid = true;
            // A failed native update can leave both reused and new rows partly
            // laid out. Drop the whole presentation, but keep messages/identities
            // so the next refresh or UI creation retry can render the snapshot.
            RenderedChatRows.Clear();
            foreach (var row in oldRows)
            {
                retained.Remove(row);
                DestroyRenderedRow(row);
            }
            foreach (var row in retained)
                DestroyRenderedRow(row);
            _chatScrollTop = 0f;
            _chatRowsHeight = 0f;
            _chatContentHeight = 0f;
            ResetChatScrollDrags();
            throw;
        }

        // Old rows remain owned until creation, layout and native presentation
        // all succeed, including rows whose presentation was replaced.
        foreach (var row in oldRows)
            if (!retained.Contains(row))
                DestroyRenderedRow(row);
        _messagesPresentationInvalid = false;
    }

    private static void RefreshChatRowVisibility()
    {
        var bottom = _chatScrollTop + GetChatScrollViewportHeight();
        foreach (var row in RenderedChatRows)
            row.Root.SetVisible(row.Y + row.Height >= _chatScrollTop && row.Y <= bottom);
    }

    private static string BuildMessageIdentity(ChatUiMessage message)
    {
        var clientMessageId = message.ClientMessageId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(clientMessageId))
            return "client:" + clientMessageId;

        var messageId = message.Id ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(messageId))
            return "id:" + messageId;

        return "anonymous:" + string.Concat(
            message.Sender ?? string.Empty, "\u001f",
            message.Text ?? string.Empty, "\u001f",
            message.IsSystem ? "1" : "0");
    }

    private static string BuildMessageRenderKey(
        ChatUiMessage message,
        string identity,
        string textScalePercent)
    {
        return string.Concat(
            identity, "\u001f",
            message.Sender ?? string.Empty, "\u001f",
            message.CharacterId ?? string.Empty, "\u001f",
            message.CharacterName ?? string.Empty, "\u001f",
            message.Order ?? string.Empty, "\u001f",
            message.Text ?? string.Empty, "\u001f",
            message.IsSystem ? "1" : "0", "\u001f",
            textScalePercent);
    }

    private static float NameLineHeight => 26f * _textScale;
    private static float BodyTop => 8f + NameLineHeight + 6f;

    private static RenderedChatRow CreateRenderedRow(
        ChatUiMessage message,
        string identity,
        string renderKey)
    {
        var content = _chatContent ?? throw new InvalidOperationException("Chat message content is unavailable.");
        var nodes = new List<FguiNode>();
        var firstTextIndex = _chatTexts.Count;
        var rowWidth = GetChatMessageRowWidth();
        FguiNode? root = null;
        try
        {
            root = AddComponent(content, 0f, 0f, rowWidth, 1f, nodes);
            root.SetTouchable(false);
            if (message.IsSystem)
            {
                var text = AddText(root, GetDisplayMessageText(message.Text),
                    ChatLayout.FontSize(18, _textScale), MutedTextColor, FguiAlign.Center,
                    0f, 6f, rowWidth, 1f, FguiVerticalAlign.Top, wrap: true, owner: nodes);
                var system = new RenderedChatRow(identity, renderKey, root, nodes,
                    _chatTexts.Skip(firstTextIndex).ToList(), text, null, null, null, string.Empty, isSystem: true);
                MeasureRenderedRow(system);
                return system;
            }

            var portrait = AddLoader(root, 0f, 8f, PortraitSize, nodes);
            portrait.SetVisible(false);
            var nameSize = ChatLayout.FontSize(18, _textScale);
            var participant = AddText(root,
                BuildParticipantLabel(message.Order, message.CharacterName, message.CharacterId),
                nameSize, GetOrderTextColor(message.Order), FguiAlign.Left,
                MessageTextLeft, 8f, Math.Max(1f, rowWidth - 250f), NameLineHeight, owner: nodes);
            var sender = AddText(root, message.Sender ?? string.Empty, nameSize, SenderTextColor, FguiAlign.Right,
                Math.Max(MessageTextLeft, rowWidth - 170f), 8f, 170f, NameLineHeight, owner: nodes);
            var body = AddText(root, GetDisplayMessageText(message.Text),
                ChatLayout.FontSize(21, _textScale), BodyTextColor, FguiAlign.Left,
                MessageTextLeft, BodyTop, Math.Max(1f, rowWidth - MessageTextLeft), 1f,
                FguiVerticalAlign.Top, wrap: true, owner: nodes);

            var rendered = new RenderedChatRow(identity, renderKey, root, nodes,
                _chatTexts.Skip(firstTextIndex).ToList(), body, participant, sender, portrait,
                message.CharacterId ?? string.Empty, isSystem: false);
            ApplyCdnPortrait(rendered);
            MeasureRenderedRow(rendered);
            return rendered;
        }
        catch
        {
            _chatTexts.RemoveRange(firstTextIndex, _chatTexts.Count - firstTextIndex);
            // AddComponent can fail after tracking/attaching the root but
            // before assigning it to the local variable.
            var rowRoot = root ?? (nodes.Count > 0 ? nodes[0] : null);
            try { rowRoot?.Dispose(); }
            catch { }
            foreach (var node in nodes)
            {
                try { node.ReleaseHandle(); }
                catch { }
            }
            throw;
        }
    }

    private static bool HasCharacterPortrait(string? characterId) =>
        PartyProtocol.NormalizeCharacter(characterId) is not ("spectator" or "unselected");

    private static void SetRenderedPortrait(RenderedChatRow row, Texture texture)
    {
        if (row.Portrait == null || !HasCharacterPortrait(row.CharacterId))
            return;

        row.Portrait.SetTexture(texture);
        row.Texture = texture;
        row.Portrait.SetVisible(true);
    }

    private static void RefreshRenderedPortraits(ChatSnapshot snapshot)
    {
        ObserveChatScrollContext(snapshot);
        var roomId = snapshot.RoomId ?? string.Empty;
        var phase = snapshot.ScreenPhase ?? string.Empty;
        var contextChanged = !string.Equals(roomId, _renderedPortraitRoomId, StringComparison.Ordinal)
            || !string.Equals(phase, _renderedPortraitPhase, StringComparison.Ordinal);
        var now = Time.unscaledTime;

        if (!contextChanged && now < _nextRenderedPortraitRefreshAt)
            return;

        _renderedPortraitRoomId = roomId;
        _renderedPortraitPhase = phase;
        _nextRenderedPortraitRefreshAt = now + 2f;

        // Reconcile every retained row with its exact ID, even when it already
        // has a texture. A stale game/UI portrait must never survive a refresh.
        foreach (var row in RenderedChatRows)
            ApplyCdnPortrait(row);
    }

    private static void ApplyCdnPortrait(RenderedChatRow row)
    {
        if (row.Portrait == null)
            return;

        if (HasCharacterPortrait(row.CharacterId))
        {
            RequestCharacterImage(row.CharacterId);
            if (TryGetCharacterTexture(row.CharacterId, out var texture)
                && texture != null)
            {
                SetRenderedPortrait(row, texture);
                return;
            }
        }

        row.Portrait.SetTexture(null);
        row.Portrait.SetVisible(false);
        row.Texture = null;
    }

    private static void MeasureRenderedRow(RenderedChatRow row)
    {
        // A previously culled row still needs its text measured after a reflow.
        // Visibility is reconciled again before this update reaches the screen.
        row.Root.SetVisible(true);
        var rowWidth = GetChatMessageRowWidth();
        row.Width = rowWidth;
        if (row.IsSystem)
        {
            row.Body.SetSize(rowWidth, 1f);
            row.Height = Math.Clamp(SanitizeTextHeight(row.Body) + 12f, 50f, MaximumMessageRowHeight);
            return;
        }

        var bodyWidth = Math.Max(1f, rowWidth - MessageTextLeft);
        row.Participant?.SetSize(Math.Max(1f, rowWidth - 250f), NameLineHeight);
        row.Sender?.SetXY(Math.Max(MessageTextLeft, rowWidth - 170f), 8f);
        row.Body.SetSize(bodyWidth, 1f);
        var bodyHeight = Math.Clamp(
            SanitizeTextHeight(row.Body),
            ChatLayout.FontSize(21, _textScale) * 1.2f,
            MaximumMessageRowHeight - BodyTop - 12f);
        row.Height = Math.Clamp(BodyTop + bodyHeight + 12f, MinimumMessageRowHeight, MaximumMessageRowHeight);
    }

    private static float SanitizeTextHeight(FguiNode text)
    {
        var height = text.Height;
        return float.IsFinite(height) && height > 0f ? height : 26f * _textScale;
    }

    private static float SanitizeRowHeight(float height)
    {
        if (!float.IsFinite(height) || height <= 0f)
            return MinimumMessageRowHeight;
        return Math.Clamp(height, 1f, MaximumMessageRowHeight);
    }

    private static void DestroyRenderedRow(RenderedChatRow row)
    {
        foreach (var text in row.Texts)
            _chatTexts.Remove(text);

        try { row.Root.Dispose(); }
        catch { }
        row.ReleaseHandles();
    }

    private static void RefreshRenderedCharacterImage(string characterId, Texture texture)
    {
        if (texture == null)
            return;

        foreach (var row in RenderedChatRows)
        {
            if (string.Equals(row.CharacterId, characterId, StringComparison.Ordinal))
                SetRenderedPortrait(row, texture);
        }
    }

    private static void ClearRenderedTexture(Texture texture)
    {
        foreach (var row in RenderedChatRows)
        {
            if (!ReferenceEquals(row.Texture, texture))
                continue;
            row.Portrait?.SetTexture(null);
            row.Portrait?.SetVisible(false);
            row.Texture = null;
        }
    }

    private static void ResetMessagesPresentation()
    {
        _messagesPresentationInvalid = false;
        UnreadChatMessageIdentities.Clear();
        _messageIdentities.Clear();
        foreach (var row in RenderedChatRows)
            row.ReleaseHandles();
        RenderedChatRows.Clear();
        _chatTexts.Clear();
        _renderedPortraitRoomId = string.Empty;
        _renderedPortraitPhase = string.Empty;
        _nextRenderedPortraitRefreshAt = 0f;
    }

    private static string GetDisplayMessageText(string? value)
    {
        var text = value ?? string.Empty;
        if (PartyProtocol.TextLength(text) <= MaximumDisplayedMessageLength)
            return text;

        return PartyProtocol.LimitText(text, MaximumDisplayedMessageLength - 1) + "…";
    }

    private static string BuildParticipantLabel(string order, string characterName, string characterId)
    {
        var normalizedOrder = (order ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedCharacter = PartyProtocol.NormalizeCharacter(characterId);
        var name = normalizedCharacter == "unselected" ? "미선택"
            : normalizedCharacter == "spectator" || string.IsNullOrWhiteSpace(characterName)
                ? "관전" : characterName.Trim();

        return normalizedOrder is "P1" or "P2" or "P3" or "P4"
            ? normalizedOrder + " · " + name
            : name;
    }

    private static Color GetOrderTextColor(string order)
    {
        return (order ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "P1" => new Color32(251, 113, 133, 255),
            "P2" => new Color32(74, 222, 128, 255),
            "P3" => new Color32(96, 165, 250, 255),
            "P4" => new Color32(250, 204, 21, 255),
            _ => MutedTextColor
        };
    }

    private sealed class RenderedChatRow
    {
        public RenderedChatRow(
            string identity,
            string renderKey,
            FguiNode root,
            List<FguiNode> nodes,
            List<FguiNode> texts,
            FguiNode body,
            FguiNode? participant,
            FguiNode? sender,
            FguiNode? portrait,
            string characterId,
            bool isSystem)
        {
            Identity = identity;
            RenderKey = renderKey;
            Root = root;
            Nodes = nodes;
            Texts = texts;
            Body = body;
            Participant = participant;
            Sender = sender;
            Portrait = portrait;
            CharacterId = PartyProtocol.NormalizeCharacter(characterId);
            IsSystem = isSystem;
        }

        public string Identity { get; }
        public string RenderKey { get; }
        public FguiNode Root { get; }
        public List<FguiNode> Nodes { get; }
        public List<FguiNode> Texts { get; }
        public FguiNode Body { get; }
        public FguiNode? Participant { get; }
        public FguiNode? Sender { get; }
        public FguiNode? Portrait { get; }
        public string CharacterId { get; }
        public bool IsSystem { get; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Y { get; set; }
        public Texture? Texture { get; set; }

        public void ReleaseHandles()
        {
            foreach (var node in Nodes)
            {
                try { node.ReleaseHandle(); }
                catch { }
            }
        }
    }
}
