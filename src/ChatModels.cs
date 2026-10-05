namespace AstralParty.Chat;

public sealed class ChatGameState
{
    public bool Available { get; init; }
    public string Phase { get; init; } = string.Empty;
    public string RoomId { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public string CharacterId { get; init; } = string.Empty;
    public string Order { get; init; } = string.Empty;
}

public sealed class ChatUiMessage
{
    public string Id { get; init; } = string.Empty;
    public string ClientMessageId { get; init; } = string.Empty;
    public string ParticipantId { get; init; } = string.Empty;
    public string Sender { get; init; } = string.Empty;
    public string CharacterId { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public string Order { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
}
