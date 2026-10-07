using System;
using System.Globalization;

namespace AstralPartyChatPlugin;

internal interface IGameDataAccess
{
    IntPtr FindClass(string assemblyName, string namespaze, string name);
    IntPtr ReadStaticObject(IntPtr klass, string fieldName);
    IntPtr ReadObject(IntPtr instance, string fieldName);
    int ReadInt32(IntPtr instance, string fieldName);
    IntPtr Invoke(IntPtr instance, string methodName);
    IntPtr Invoke(IntPtr instance, string methodName, long argumentValue);
    long InvokeInt64(IntPtr instance, string methodName);
    int InvokeInt32(IntPtr instance, string methodName);
    bool InvokeBoolean(IntPtr instance, string methodName);
    string InvokeString(IntPtr instance, string methodName);
    string InvokeString(IntPtr instance, string methodName, bool argumentValue);
}

// These paths were traced in AstralParty.Runtime: RoomInfo.GetSelfInfo uses
// AccountLogic.GetPlayerID, RoomPlayer.GetNick is also used by the game's UI,
// and SyncRoomS2C rebuilds RoomController.localRoom during an in-battle rejoin.
internal sealed class GameStateReader
{
    private readonly IGameDataAccess _data;
    public GameStateReader(IGameDataAccess data) => _data = data;

    public ChatGameState Read()
    {
        var klass = _data.FindClass("AstralParty.Runtime", "GameLogic", "GameLogicManager");
        var manager = _data.ReadStaticObject(klass, "_inst");
        if (manager == IntPtr.Zero) return Pending();
        var account = _data.ReadObject(manager, "<account>k__BackingField");
        var roomLogic = _data.ReadObject(manager, "<room>k__BackingField");
        if (account == IntPtr.Zero || roomLogic == IntPtr.Zero) return Pending();
        var controller = _data.ReadObject(roomLogic, "roomController");
        if (controller == IntPtr.Zero) return Pending();
        var room = _data.ReadObject(controller, "_LocalRoom");
        var mode = _data.ReadInt32(controller, "roomStateType");
        if (room == IntPtr.Zero) return new ChatGameState();
        if (mode == 0) return Pending();
        var phase = mode switch
        {
            1 => "방",                 // WAIT
            2 => "캐릭터 선택",        // CHOICE
            3 or 4 or 5 => "플레이",   // READY / RUNNING / SETTLEMENT
            _ => string.Empty
        };
        if (phase.Length == 0) return Pending();
        var roomId = _data.InvokeInt64(room, "get_Id").ToString(CultureInfo.InvariantCulture);
        if (!PartyProtocol.IsRoomId(roomId)) return new ChatGameState();
        var accountId = _data.InvokeInt64(account, "GetPlayerID");
        if (accountId <= 0) return Pending();
        var self = _data.Invoke(room, "GetSelfInfo");
        if (self == IntPtr.Zero)
        {
            // A missing player while reconnect data is loading is not a spectator.
            var watch = _data.ReadObject(manager, "<watch>k__BackingField");
            if (watch == IntPtr.Zero || !_data.InvokeBoolean(watch, "PlayerIsWatcher")) return Pending();
            // GetName reads AccountInfo.Nick; GetPlayerInfo exposes the game's
            // Player.Nick, including when self has no RoomPlayer as a watcher.
            var player = _data.Invoke(account, "GetPlayerInfo");
            if (player == IntPtr.Zero || _data.InvokeInt64(player, "get_Id") != accountId) return Pending();
            var watcherName = _data.InvokeString(player, "get_Nick");
            if (string.IsNullOrWhiteSpace(watcherName)) return Pending();
            return new ChatGameState
            {
                Available = true, Phase = phase, RoomId = roomId,
                Nickname = watcherName, CharacterId = "spectator"
            };
        }
        if (_data.InvokeInt64(self, "get_Id") != accountId) return Pending();
        var slot = _data.InvokeInt32(self, "get_Slot");
        if (slot is < 0 or > 3) return Pending();
        var heroId = 0;
        if (mode == 2)
        {
            // Selection updates RoomInfo.Box independently of serverPlayer.Hero.
            // Use the same account-ID lookup as the game's selection display.
            var bar = _data.Invoke(room, "GetHeroBarById", accountId);
            if (bar != IntPtr.Zero) heroId = _data.InvokeInt32(bar, "get_HeroId");
        }
        else if (mode >= 3)
        {
            var hero = _data.Invoke(self, "get_Hero");
            if (hero != IntPtr.Zero) heroId = _data.InvokeInt32(hero, "get_HeroId");
        }
        if (mode >= 3 && heroId == 0) return Pending();
        // WAIT does not confirm a new hero; CHOICE can have no selection yet.
        var character = heroId == 0 ? "unselected" : heroId.ToString(CultureInfo.InvariantCulture);
        if (character != "unselected" && PartyProtocol.NormalizeCharacter(character) == "spectator")
            return Pending(); // Unknown new game character must never masquerade as an observer.
        var nickname = _data.InvokeString(self, "GetNick", false);
        if (string.IsNullOrWhiteSpace(nickname)) return Pending();
        return new ChatGameState
        {
            Available = true, Phase = phase, RoomId = roomId,
            Nickname = nickname, CharacterId = character,
            Order = "P" + (slot + 1).ToString(CultureInfo.InvariantCulture)
        };
    }

    public static ChatGameState Pending() => new() { InformationReady = false };
}
