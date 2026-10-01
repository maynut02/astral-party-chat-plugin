using System;

namespace AstralParty.Chat;

/// <summary>
/// Tracks the room and player details observed from the game without depending
/// on Unity, so phase and room transitions can be tested independently.
/// </summary>
public sealed class GameSessionState
{
    public const double TransitionGraceSeconds = 15;
    private double? _unknownPhaseSince;
    private string _suspendedRoomId = string.Empty;

    public string RoomId { get; private set; } = string.Empty;
    public string Order { get; private set; } = string.Empty;
    public string Character { get; private set; } = string.Empty;
    public string Phase { get; private set; } = "기타";
    public bool ChatAvailable => IsChatPhase(Phase);

    /// <summary>
    /// UI roots can disappear between room, character selection, and battle.
    /// Retain the confirmed session during a bounded unknown-screen interval;
    /// Suspend longer gaps until a chat screen returns; an explicit room setup
    /// screen discards the recovery identity immediately.
    /// </summary>
    public bool ObserveScreenPhase(string phase, double now)
    {
        var nextPhase = Normalize(phase);
        if (nextPhase == "기타" && ChatAvailable)
        {
            _unknownPhaseSince ??= now;
            if (now - _unknownPhaseSince.Value < TransitionGraceSeconds)
                return false;

            var roomToRecover = RoomId;
            var changed = ObservePhase(nextPhase);
            _suspendedRoomId = roomToRecover;
            return changed;
        }
        // Repeated unknown scans must neither reactivate chat nor discard the
        // last room needed when selection/battle UI finally becomes visible.
        if (nextPhase == "기타" && Phase == "기타") return false;

        var recoveryRoom = _suspendedRoomId;
        var wasUnavailable = !ChatAvailable;
        var resetCaches = ObservePhase(nextPhase);
        if (wasUnavailable && IsChatPhase(nextPhase) && RoomId.Length == 0 && recoveryRoom.Length > 0)
            resetCaches |= ObserveRoomId(recoveryRoom);
        return resetCaches;
    }

    /// <summary>
    /// Records a new phase and returns whether phase-scoped runtime caches must
    /// be cleared. Returning from battle to the room preserves the same-round
    /// room, order, and character; entering character selection starts a new
    /// round and clears the previous character.
    /// </summary>
    public bool ObservePhase(string phase)
    {
        _unknownPhaseSince = null;
        _suspendedRoomId = string.Empty;
        var nextPhase = Normalize(phase);
        var previousPhase = Phase;
        if (string.Equals(previousPhase, nextPhase, StringComparison.Ordinal))
            return false;

        Phase = nextPhase;
        var resetCaches = IsBattle(previousPhase) != IsBattle(nextPhase);

        if (string.Equals(nextPhase, "캐릭터 선택", StringComparison.Ordinal))
        {
            Character = string.Empty;
            resetCaches = true;
        }

        if (!IsChatPhase(nextPhase) && IsChatPhase(previousPhase))
        {
            RoomId = string.Empty;
            Order = string.Empty;
            Character = string.Empty;
            resetCaches = true;
        }

        return resetCaches;
    }

    /// <summary>
    /// Records the current room identity. A changed identity invalidates the
    /// cached order and character and tells the runtime to clear room caches.
    /// </summary>
    public bool ObserveRoomId(string roomId)
    {
        var nextRoomId = Normalize(roomId);
        if (nextRoomId.Length == 0)
        {
            // A missing RoomWaitPanel label is a transient UI read failure while
            // the session is active, so retain the last confirmed room.
            if (IsChatPhase(Phase))
                return false;

            var changed = RoomId.Length > 0 || Order.Length > 0 || Character.Length > 0;
            RoomId = string.Empty;
            Order = string.Empty;
            Character = string.Empty;
            return changed;
        }

        _suspendedRoomId = string.Empty;
        if (string.Equals(RoomId, nextRoomId, StringComparison.Ordinal))
            return false;

        RoomId = nextRoomId;
        Order = string.Empty;
        Character = string.Empty;
        return true;
    }

    public void SetOrder(string order) => Order = Normalize(order);

    public void SetCharacter(string character) => Character = Normalize(character);

    public void Reset()
    {
        _unknownPhaseSince = null;
        _suspendedRoomId = string.Empty;
        RoomId = string.Empty;
        Order = string.Empty;
        Character = string.Empty;
        Phase = "기타";
    }

    private static bool IsBattle(string phase) =>
        string.Equals(phase, "플레이", StringComparison.Ordinal);

    private static bool IsChatPhase(string phase) =>
        string.Equals(phase, "방", StringComparison.Ordinal)
        || string.Equals(phase, "캐릭터 선택", StringComparison.Ordinal)
        || IsBattle(phase);

    private static string Normalize(string value) => value?.Trim() ?? string.Empty;
}
