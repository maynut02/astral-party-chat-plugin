using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace AstralPartyChatPlugin;

// Mirrors shared/types/party.ts and server/utils/party-chat.ts in astral-patch-site.
internal static class PartyProtocol
{
    public const int HistoryLimit = 300;
    public const int MaxTextLength = 1000;
    private static readonly HashSet<string> CharacterIds = new(
        Enumerable.Range(101, 29).Concat(Enumerable.Range(301, 6)).Select(id => id.ToString()),
        StringComparer.Ordinal);

    public static bool IsRoomId(string value) => value.Length == 6
        && value.All(character => character is >= '0' and <= '9');

    public static int TextLength(string value)
    {
        var length = 0;
        foreach (var rune in value.EnumerateRunes()) length++;
        return length;
    }

    public static string LimitText(string value, int maxLength)
    {
        var position = 0;
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (length++ == maxLength) break;
            position += rune.Utf16SequenceLength;
        }
        return position == value.Length ? value : value[..position];
    }

    public static string NormalizeCharacter(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized == "unselected") return normalized;
        return CharacterIds.Contains(normalized) ? normalized : "spectator";
    }

    public static string NormalizeOrder(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized is "P1" or "P2" or "P3" or "P4" ? normalized : string.Empty;
    }
}

internal sealed class PartyChatOptions
{
    public string SiteOrigin { get; init; } = "https://astral.maynutlab.com";
    public Uri RoomUri { get; init; } = new("https://astral.maynutlab.com/api/party/rooms");
    public Uri CharactersUri { get; init; } = new("https://astral.maynutlab.com/api/party/characters");
    public Uri WebSocketUri { get; init; } = new("wss://astral.maynutlab.com/ws/party");
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RateLimitRetryDelay { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PresenceTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PresenceRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MessageTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public Func<WebSocket> CreateSocket { get; init; } = () => new ClientWebSocket();
    public Func<WebSocket, Uri, CancellationToken, Task> ConnectSocket { get; init; }
        = (socket, uri, token) => ((ClientWebSocket)socket).ConnectAsync(uri, token);
}
