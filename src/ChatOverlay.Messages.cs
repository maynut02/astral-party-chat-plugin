using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    private const int MaximumRenderedMessages = 300;
    private const int MaximumDisplayedMessageLength = 1400;
    private const float MinimumMessageRowHeight = 90f;
    private const float MaximumMessageRowHeight = 42000f;
    private const float ChatWheelStep = 100f;
    private const float ChatBottomFollowDistance = 32f;
    private static List<ChatUiMessage> _messages = new();
    private static readonly List<RenderedChatRow> RenderedChatRows = new();
    private static string _renderedPortraitRoomId = string.Empty;
    private static string _renderedPortraitPhase = string.Empty;
    private static float _nextRenderedPortraitRefreshAt;

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
        ObserveChatScrollMessages(_messages, next);
        _messages = next;
        RenderChatMessages(force: false);
    }

    private static List<string> GetChatMessageIdentities(IReadOnlyList<ChatUiMessage> messages)
    {
        var identities = new List<string>(messages.Count);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
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
        IReadOnlyList<ChatUiMessage> previous,
        IReadOnlyList<ChatUiMessage> next)
    {
        ObserveChatScrollConnectionStatus();
        var nextIdentities = GetChatMessageIdentities(next);
        var present = new HashSet<string>(nextIdentities, StringComparer.Ordinal);
        UnreadChatMessageIdentities.RemoveWhere(identity => !present.Contains(identity));
        var previousIdentities = GetChatMessageIdentities(previous);
        var tailIndex = previousIdentities.Count == 0
            ? -1 : nextIdentities.LastIndexOf(previousIdentities[previousIdentities.Count - 1]);

        // A prepend, replay, echo acknowledgement or participant update is not
        // a new arrival. Only unknown identities after the previous tail count.
        // A non-overlapping replacement establishes a fresh history baseline.
        if (next.Count == 0 || previous.Count == 0 || tailIndex < 0)
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
        if (_chatContentRect == null)
            return;

        var nextMessages = _messages.Count <= MaximumRenderedMessages
            ? _messages
            : _messages.Skip(_messages.Count - MaximumRenderedMessages).ToList();
        var oldRows = RenderedChatRows.ToArray();
        var previousContentHeight = _chatContentRect.rect.height;
        var viewportHeight = GetChatScrollViewportHeight();
        var rowWidth = GetChatMessageRowWidth();
        var widthChanged = oldRows.Any(row => Math.Abs(row.Rect.rect.width - rowWidth) > 0.5f);
        var previousScrollable = Math.Max(0f, previousContentHeight - viewportHeight);
        var wasAtBottom = _chatScrollRect == null
            || Mathf.Clamp01(_chatScrollRect.verticalNormalizedPosition)
                * previousScrollable <= ChatBottomFollowDistance
            || (oldRows.Length == 0 && nextMessages.Count > 0);
        var previousTopOffset = 0f;
        if (_chatScrollRect != null)
        {
            previousTopOffset = (1f - _chatScrollRect.verticalNormalizedPosition)
                * previousScrollable;
        }

        var available = new Dictionary<string, Queue<RenderedChatRow>>(StringComparer.Ordinal);
        foreach (var row in oldRows)
        {
            if (!available.TryGetValue(row.Identity, out var rows))
            {
                rows = new Queue<RenderedChatRow>();
                available.Add(row.Identity, rows);
            }
            rows.Enqueue(row);
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextRows = new List<RenderedChatRow>(nextMessages.Count);
        var retained = new HashSet<RenderedChatRow>();
        var changed = force || widthChanged || oldRows.Length != nextMessages.Count;

        foreach (var message in nextMessages)
        {
            var identity = BuildMessageIdentity(message);
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            if (occurrence != 0)
                identity += "\u001e" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var renderKey = BuildMessageRenderKey(message, identity);
            RenderedChatRow row;
            if (available.TryGetValue(identity, out var matchingRows)
                && matchingRows.Count > 0)
            {
                var previous = matchingRows.Dequeue();
                if (string.Equals(previous.RenderKey, renderKey, StringComparison.Ordinal))
                {
                    row = previous;
                }
                else
                {
                    DestroyRenderedRow(previous);
                    retained.Add(previous);
                    row = CreateRenderedRow(message, identity, renderKey);
                    changed = true;
                }
            }
            else
            {
                row = CreateRenderedRow(message, identity, renderKey);
                changed = true;
            }

            if (!retained.Add(row))
                changed = true;
            nextRows.Add(row);
        }

        foreach (var row in oldRows)
        {
            if (retained.Contains(row))
                continue;

            DestroyRenderedRow(row);
            changed = true;
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
            return;
        }

        var removedAboveViewport = GetRemovedLeadingHeight(oldRows, nextRows);
        RenderedChatRows.Clear();
        RenderedChatRows.AddRange(nextRows);

        var y = 6f;
        for (var index = 0; index < RenderedChatRows.Count; index++)
        {
            var row = RenderedChatRows[index];
            if (force || widthChanged)
            {
                MeasureRenderedRow(row);
                ApplyCdnPortrait(row);
            }

            var height = SanitizeRowHeight(row.Height);
            SetTopLeftRect(
                row.Rect,
                new Vector2(0f, y),
                new Vector2(rowWidth, height));
            row.Root.transform.SetSiblingIndex(index);
            y += height + 6f;
        }

        var contentHeight = Math.Max(viewportHeight, y + 2f);
        if (float.IsNaN(contentHeight) || float.IsInfinity(contentHeight))
            contentHeight = Math.Max(502f, viewportHeight);
        _chatContentRect.sizeDelta = new Vector2(0f, contentHeight);

        if (_chatScrollRect != null)
        {
            Canvas.ForceUpdateCanvases();
            var scrollable = Math.Max(0f, contentHeight - viewportHeight);
            if (wasAtBottom || scrollable <= 0.5f)
            {
                _chatScrollRect.verticalNormalizedPosition = 0f;
            }
            else
            {
                var topOffset = Math.Max(0f, previousTopOffset - removedAboveViewport);
                _chatScrollRect.verticalNormalizedPosition = Mathf.Clamp01(
                    1f - (topOffset / scrollable));
            }
        }
        RefreshChatScrollControlPresentation();
    }

    private static float GetRemovedLeadingHeight(
        IReadOnlyList<RenderedChatRow> oldRows,
        IReadOnlyList<RenderedChatRow> nextRows)
    {
        if (oldRows.Count == 0 || nextRows.Count == 0)
            return 0f;

        var firstRetained = -1;
        for (var index = 0; index < oldRows.Count; index++)
        {
            if (nextRows.Contains(oldRows[index]))
            {
                firstRetained = index;
                break;
            }
        }

        if (firstRetained <= 0)
            return 0f;

        var removedHeight = 0f;
        for (var index = 0; index < firstRetained; index++)
            removedHeight += SanitizeRowHeight(oldRows[index].Height) + 6f;
        return removedHeight;
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
        string identity)
    {
        return string.Concat(
            identity, "\u001f",
            message.Sender ?? string.Empty, "\u001f",
            message.CharacterId ?? string.Empty, "\u001f",
            message.CharacterName ?? string.Empty, "\u001f",
            message.Order ?? string.Empty, "\u001f",
            message.Text ?? string.Empty, "\u001f",
            message.IsSystem ? "1" : "0");
    }

    private static RenderedChatRow CreateRenderedRow(
        ChatUiMessage message,
        string identity,
        string renderKey)
    {
        var content = _chatContentRect;
        if (content == null)
            throw new InvalidOperationException("Chat message content is unavailable.");

        var firstTextIndex = _chatTexts.Count;
        GameObject? root = null;
        try
        {
            if (message.IsSystem)
            {
                root = new GameObject("SystemMessageRow");
                var rootRect = root.AddComponent<RectTransform>();
                root.transform.SetParent(content, false);
                var text = CreateUiText(
                    root.transform,
                    "SystemMessage",
                    GetDisplayMessageText(message.Text),
                    18,
                    new Color32(175, 175, 175, 255),
                    TextAnchor.MiddleCenter,
                    Vector2.zero,
                    new Vector2(GetChatMessageRowWidth(), 1f));
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;

                var row = new RenderedChatRow(
                    identity,
                    renderKey,
                    root,
                    rootRect,
                    _chatTexts.Skip(firstTextIndex).ToList(),
                    null,
                    null,
                    string.Empty);
                MeasureSystemRow(row, text);
                return row;
            }

            root = CreateFlatImage(
                content,
                "ChatMessage",
                new Color32(0, 0, 0, 0),
                false);

            var portraitObject = new GameObject("CharacterImage");
            portraitObject.transform.SetParent(root.transform, false);
            var portrait = portraitObject.AddComponent<RawImage>();
            portrait.color = Color.white;
            portrait.raycastTarget = false;
            portraitObject.SetActive(false);
            SetTopLeftRect(
                portraitObject.GetComponent<RectTransform>(),
                new Vector2(0f, 8f),
                new Vector2(60f, 60f));

            var participant = CreateUiText(
                root.transform,
                "Participant",
                BuildParticipantLabel(message.Order, message.CharacterName, message.CharacterId),
                18,
                GetOrderTextColor(message.Order),
                TextAnchor.MiddleLeft,
                new Vector2(72f, 8f),
                new Vector2(Math.Max(1f, GetChatMessageRowWidth() - 250f), 26f));
            participant.supportRichText = false;

            var sender = CreateUiText(
                root.transform,
                "Sender",
                message.Sender ?? string.Empty,
                18,
                new Color32(145, 145, 145, 255),
                TextAnchor.MiddleRight,
                new Vector2(Math.Max(72f, GetChatMessageRowWidth() - 170f), 8f),
                new Vector2(170f, 26f));
            sender.supportRichText = false;

            var body = CreateUiText(
                root.transform,
                "Body",
                GetDisplayMessageText(message.Text),
                21,
                new Color32(235, 235, 235, 255),
                TextAnchor.UpperLeft,
                new Vector2(72f, 40f),
                new Vector2(Math.Max(1f, GetChatMessageRowWidth() - 72f), 1f));
            body.supportRichText = false;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Truncate;

            var rendered = new RenderedChatRow(
                identity,
                renderKey,
                root,
                root.GetComponent<RectTransform>() ?? throw new InvalidOperationException("Chat row requires a RectTransform."),
                _chatTexts.Skip(firstTextIndex).ToList(),
                body,
                portrait,
                message.CharacterId ?? string.Empty);

            ApplyCdnPortrait(rendered);

            MeasureRenderedRow(rendered);
            return rendered;
        }
        catch
        {
            for (var index = _chatTexts.Count - 1; index >= firstTextIndex; index--)
                _chatTexts.RemoveAt(index);
            if (root != null)
            {
                try { UnityEngine.Object.Destroy(root); }
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

        row.Portrait.texture = texture;
        row.Portrait.uvRect = new Rect(0f, 0f, 1f, 1f);
        row.Portrait.gameObject.SetActive(true);
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

        row.Portrait.texture = null;
        row.Portrait.gameObject.SetActive(false);
    }

    private static void MeasureRenderedRow(RenderedChatRow row)
    {
        if (row.Body == null)
        {
            var text = row.Texts.Count == 0 ? null : row.Texts[0];
            if (text != null)
                MeasureSystemRow(row, text);
            return;
        }

        var rowWidth = GetChatMessageRowWidth();
        var bodyWidth = Math.Max(1f, rowWidth - 72f);
        row.Body.rectTransform.sizeDelta = new Vector2(bodyWidth, row.Body.rectTransform.sizeDelta.y);
        foreach (var text in row.Texts)
        {
            if (text.gameObject.name == "Participant")
                SetTopLeftRect(text.rectTransform, new Vector2(72f, 8f),
                    new Vector2(Math.Max(1f, rowWidth - 250f), 26f));
            else if (text.gameObject.name == "Sender")
                SetTopLeftRect(text.rectTransform, new Vector2(Math.Max(72f, rowWidth - 170f), 8f),
                    new Vector2(170f, 26f));
        }
        var preferredHeight = GetPreferredTextHeight(row.Body);
        var bodyHeight = Mathf.Clamp(
            preferredHeight,
            Math.Max(1f, row.Body.fontSize * 1.2f),
            MaximumMessageRowHeight - 52f);
        var bodyRect = row.Body.rectTransform;
        bodyRect.sizeDelta = new Vector2(bodyWidth, bodyHeight);
        row.Height = Mathf.Clamp(
            40f + bodyHeight + 12f,
            MinimumMessageRowHeight,
            MaximumMessageRowHeight);
    }

    private static void MeasureSystemRow(RenderedChatRow row, Text text)
    {
        var rowWidth = GetChatMessageRowWidth();
        text.rectTransform.sizeDelta = new Vector2(rowWidth, text.rectTransform.sizeDelta.y);
        var preferredHeight = GetPreferredTextHeight(text);
        var height = Mathf.Clamp(
            preferredHeight + 12f,
            50f,
            MaximumMessageRowHeight);
        text.rectTransform.sizeDelta = new Vector2(rowWidth, height);
        row.Height = height;
    }

    private static float GetPreferredTextHeight(Text text)
    {
        try
        {
            var preferred = text.preferredHeight;
            if (!float.IsNaN(preferred) && !float.IsInfinity(preferred) && preferred > 0f)
                return preferred;
        }
        catch { }

        return Math.Max(1f, text.fontSize * 1.25f);
    }

    private static float SanitizeRowHeight(float height)
    {
        if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
            return MinimumMessageRowHeight;
        return Mathf.Clamp(height, 1f, MaximumMessageRowHeight);
    }

    private static void DestroyRenderedRow(RenderedChatRow row)
    {
        foreach (var text in row.Texts)
            _chatTexts.Remove(text);

        try
        {
            if (row.Root != null)
            {
                row.Root.SetActive(false);
                UnityEngine.Object.Destroy(row.Root);
            }
        }
        catch { }
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

    private static void ResetMessagesPresentation()
    {
        UnreadChatMessageIdentities.Clear();
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
            _ => new Color32(175, 175, 175, 255)
        };
    }

    private sealed class RenderedChatRow
    {
        public RenderedChatRow(
            string identity,
            string renderKey,
            GameObject root,
            RectTransform rect,
            List<Text> texts,
            Text? body,
            RawImage? portrait,
            string characterId)
        {
            Identity = identity;
            RenderKey = renderKey;
            Root = root;
            Rect = rect;
            Texts = texts;
            Body = body;
            Portrait = portrait;
            CharacterId = PartyProtocol.NormalizeCharacter(characterId);
        }

        public string Identity { get; }
        public string RenderKey { get; }
        public GameObject Root { get; }
        public RectTransform Rect { get; }
        public List<Text> Texts { get; }
        public Text? Body { get; }
        public RawImage? Portrait { get; }
        public string CharacterId { get; }
        public float Height { get; set; }
    }
}
