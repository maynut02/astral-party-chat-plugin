using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AstralPartyChatPlugin;

internal static class GameStateReaderTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Cases { get; } =
        new (string, Func<Task>)[]
        {
            Case("Game data identifies self despite duplicate and unrelated Steam names", DuplicateNames),
            Case("Cold in-battle rejoin reads the server room without visiting the lobby", ColdRejoin),
            Case("Server slot swap refreshes P1-P4 without changing player identity", SlotSwap),
            Case("Missing self during synchronization does not advertise a spectator", MissingSelf),
            Case("An account switch replaces player identity and live room objects", AccountSwitch),
            Case("A stale self returned for another account is rejected", StaleSelf),
            Case("WAIT hides a previous hero while retaining the player's order", WaitingRoom),
            Case("A missing or zero selection bar is unselected despite a previous battle hero", UnselectedHero),
            Case("CHOICE refreshes the current player's HeroBar through selection box updates", SelectionUpdates),
            Case("WAIT, CHOICE and battle phases read their respective character sources", SelectionPhaseTransitions),
            Case("CHOICE follows account and room replacement without caching selection bars", SelectionLiveIdentity),
            Case("Unsupported selection heroes remain pending without using a previous battle hero", UnsupportedSelection),
            Case("A confirmed watcher uses the in-game nickname and no player order", Watcher),
            Case("A watcher waits for matching account player information", WatcherIdentityLoading),
            Case("An existing room in reconnect NONE is pending rather than a confirmed exit", ReconnectPending),
            Case("Unloaded hot-update types are retried on later observations", LateAssembly),
            Case("Unknown modes, slots and heroes remain explicitly pending", UnsupportedData),
            Case("Missing battle heroes and blank participant names remain pending until refreshed", EmptyRuntimeData)
        };

    private static (string, Func<Task>) Case(string name, Action run) =>
        (name, () => { run(); return Task.CompletedTask; });

    private static void DuplicateNames()
    {
        var game = new FakeGameData();
        game.AddPlayer(9002, "shared nickname", 0, 105);
        game.AddPlayer(9001, "shared nickname", 2, 101);
        game.SetAccount(9001, "shared nickname", "different Steam/account nickname");
        var state = new GameStateReader(game).Read();
        Ready(state, "123456", "shared nickname", "101", "P3");
        True(game.GetNickArguments.Count == 1 && !game.GetNickArguments[0],
            "The game nickname getter must be called with showRemark=false.");
    }

    private static void ColdRejoin()
    {
        var game = new FakeGameData();
        game.ReplaceRoom(654321, 4);
        game.AddPlayer(9001, "in-game nickname", 3, 109);
        // New reader and new native object graph; no prior WAIT or room cache exists.
        Ready(new GameStateReader(game).Read(), "654321", "in-game nickname", "109", "P4");
    }

    private static void SlotSwap()
    {
        var game = new FakeGameData();
        var other = game.AddPlayer(9002, "teammate", 3, 105);
        var reader = new GameStateReader(game);
        Ready(reader.Read(), "123456", "in-game nickname", "101", "P1");
        game.SetSlot(game.Self, 3);
        game.SetSlot(other, 0);
        Ready(reader.Read(), "123456", "in-game nickname", "101", "P4");
    }

    private static void MissingSelf()
    {
        var game = new FakeGameData();
        var reader = new GameStateReader(game);
        game.RemovePlayer(9001);
        foreach (var mode in new[] { 1, 2, 3, 4 })
        {
            game.Mode = mode;
            Pending(reader.Read(), "A missing participant must not become a spectator.");
        }
        game.AddPlayer(9001, "in-game nickname", 1, 105);
        Ready(reader.Read(), "123456", "in-game nickname", "105", "P2");
    }

    private static void AccountSwitch()
    {
        var game = new FakeGameData();
        var reader = new GameStateReader(game);
        Ready(reader.Read(), "123456", "in-game nickname", "101", "P1");
        game.ReplaceRoom(222222, 4);
        game.SetAccount(7777, "second in-game nickname", "second account nickname");
        game.AddPlayer(7777, "second in-game nickname", 2, 106);
        Ready(reader.Read(), "222222", "second in-game nickname", "106", "P3");
    }

    private static void StaleSelf()
    {
        var game = new FakeGameData();
        game.SelfOverride = game.AddPlayer(9002, "in-game nickname", 1, 105);
        Pending(new GameStateReader(game).Read(), "Another player's native object was accepted as self.");
    }

    private static void WaitingRoom()
    {
        var game = new FakeGameData { Mode = 1 };
        game.SetSlot(game.Self, 2);
        Ready(new GameStateReader(game).Read(), "123456", "in-game nickname", "unselected", "P3");
    }

    private static void UnselectedHero()
    {
        var game = new FakeGameData { Mode = 2 };
        var reader = new GameStateReader(game);
        game.SetHero(game.Self, 109);
        game.SetSelectionHero(9001, 0);
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P1");
        game.SetSelectionHero(9001, null);
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P1");
        game.ReplaceSelectionBox(false);
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P1");
    }

    private static void SelectionUpdates()
    {
        var game = new FakeGameData { Mode = 2 };
        game.AddPlayer(9002, "in-game nickname", 1, 109);
        game.SetSelectionHero(9002, 109);
        game.SetSelectionHero(9001, 105);
        var reader = new GameStateReader(game);
        Ready(reader.Read(), "123456", "in-game nickname", "105", "P1");
        // HeroBarBoxChange replaces selection data without changing Player.Hero.
        game.SetSelectionHero(9001, 106);
        Ready(reader.Read(), "123456", "in-game nickname", "106", "P1");
        game.ReplaceSelectionBox();
        game.SetSelectionHero(9001, 107);
        Ready(reader.Read(), "123456", "in-game nickname", "107", "P1");
        game.SetSelectionHero(9001, null);
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P1");
        True(game.SelectionPlayerIds.Count == 4 && game.SelectionPlayerIds.TrueForAll(id => id == 9001),
            "The current account ID must select the HeroBar, even when names are duplicated.");
    }

    private static void SelectionPhaseTransitions()
    {
        var game = new FakeGameData { Mode = 1 };
        var reader = new GameStateReader(game);
        game.SetSlot(game.Self, 2);
        game.SetSelectionHero(9001, 105);
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P3");
        game.Mode = 2;
        Ready(reader.Read(), "123456", "in-game nickname", "105", "P3");
        game.SetHero(game.Self, 109);
        // The old selection box remains; confirmed battle data is now authoritative.
        game.SetSelectionHero(9001, 106);
        foreach (var mode in new[] { 3, 4, 5 })
        {
            game.Mode = mode;
            Ready(reader.Read(), "123456", "in-game nickname", "109", "P3");
        }
        game.Mode = 1;
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P3");
        game.ReplaceSelectionBox();
        game.Mode = 2;
        Ready(reader.Read(), "123456", "in-game nickname", "unselected", "P3");
        game.SetSelectionHero(9001, 107);
        Ready(reader.Read(), "123456", "in-game nickname", "107", "P3");
    }

    private static void SelectionLiveIdentity()
    {
        var game = new FakeGameData { Mode = 2 };
        var reader = new GameStateReader(game);
        game.SetSelectionHero(9001, 105);
        Ready(reader.Read(), "123456", "in-game nickname", "105", "P1");
        const long secondId = (1L << 40) + 9001;
        game.SetAccount(secondId, "second in-game nickname", "different account nickname");
        game.AddPlayer(secondId, "second in-game nickname", 3, 106);
        Ready(reader.Read(), "123456", "second in-game nickname", "unselected", "P4");
        game.SetSelectionHero(secondId, 109);
        Ready(reader.Read(), "123456", "second in-game nickname", "109", "P4");
        game.ReplaceRoom(654321, 2);
        game.AddPlayer(secondId, "second in-game nickname", 1, 101);
        Ready(reader.Read(), "654321", "second in-game nickname", "unselected", "P2");
        game.SetSelectionHero(secondId, 106);
        Ready(reader.Read(), "654321", "second in-game nickname", "106", "P2");
        True(game.SelectionPlayerIds.GetRange(1, 4).TrueForAll(id => id == secondId),
            "A new Int64 player ID must replace the prior selection identity.");
    }

    private static void UnsupportedSelection()
    {
        var game = new FakeGameData { Mode = 2 };
        var reader = new GameStateReader(game);
        foreach (var heroId in new[] { -1, 999 })
        {
            game.SetSelectionHero(9001, heroId);
            Pending(reader.Read(), "An unsupported selection was replaced with the old battle hero.");
        }
        game.SetSelectionHero(9001, 105);
        Ready(reader.Read(), "123456", "in-game nickname", "105", "P1");
    }

    private static void Watcher()
    {
        var game = new FakeGameData();
        game.RemovePlayer(9001);
        game.IsWatcher = true;
        var state = new GameStateReader(game).Read();
        Ready(state, "123456", "in-game nickname", "spectator", "");
    }

    private static void WatcherIdentityLoading()
    {
        var game = new FakeGameData();
        game.RemovePlayer(9001);
        game.IsWatcher = true;
        var reader = new GameStateReader(game);
        game.ReplaceAccountPlayerInfo(null);
        Pending(reader.Read(), "An unloaded account player was treated as a ready watcher.");
        game.ReplaceAccountPlayerInfo(9002);
        Pending(reader.Read(), "A previous account's player information was accepted for the watcher.");
        game.SetAccount(9001, "in-game nickname", "different Steam/account nickname");
        Ready(reader.Read(), "123456", "in-game nickname", "spectator", "");
    }

    private static void ReconnectPending()
    {
        var game = new FakeGameData();
        var reader = new GameStateReader(game);
        Ready(reader.Read(), "123456", "in-game nickname", "101", "P1");
        // OnlineSyncRoomIdS2C sets NONE while localRoom still contains the old graph.
        game.Mode = 0;
        Pending(reader.Read(), "NONE during server resync was treated as an explicit room exit.");
        game.ReplaceRoom(123456, 4);
        game.AddPlayer(9001, "in-game nickname", 1, 105);
        Ready(reader.Read(), "123456", "in-game nickname", "105", "P2");
    }

    private static void LateAssembly()
    {
        var game = new FakeGameData { AssemblyLoaded = false };
        var reader = new GameStateReader(game);
        Pending(reader.Read(), "Missing hot-update metadata should be retried.");
        game.AssemblyLoaded = true;
        Ready(reader.Read(), "123456", "in-game nickname", "101", "P1");
    }

    private static void UnsupportedData()
    {
        var game = new FakeGameData();
        var reader = new GameStateReader(game);
        game.Mode = 99;
        Pending(reader.Read(), "An unknown mode was guessed.");
        game.Mode = 4;
        foreach (var slot in new[] { -1, 4, 7 })
        {
            game.SetSlot(game.Self, slot);
            Pending(reader.Read(), "An unsupported slot was silently clamped.");
        }
        game.SetSlot(game.Self, 0);
        game.SetHero(game.Self, 999);
        Pending(reader.Read(), "An unsupported game character was turned into a spectator.");
    }

    private static void EmptyRuntimeData()
    {
        var game = new FakeGameData();
        var reader = new GameStateReader(game);
        foreach (var mode in new[] { 3, 4, 5 })
        {
            game.Mode = mode;
            foreach (var hero in new int?[] { 0, null })
            {
                game.SetHero(game.Self, hero);
                Pending(reader.Read(), "A missing battle hero was advertised as a ready participant.");
            }
        }
        game.Mode = 4;
        game.SetHero(game.Self, 101);
        foreach (var nickname in new[] { "", " ", "\t\r\n" })
        {
            game.SetNickname(game.Self, nickname);
            Pending(reader.Read(), "A blank participant nickname was accepted.");
        }
        game.SetNickname(game.Self, "in-game nickname");
        Ready(reader.Read(), "123456", "in-game nickname", "101", "P1");

        game.RemovePlayer(9001);
        game.IsWatcher = true;
        foreach (var nickname in new[] { "", " ", "\t\r\n" })
        {
            game.SetAccount(9001, nickname, "different Steam/account nickname");
            Pending(reader.Read(), "A blank in-game watcher nickname was replaced with an account name.");
        }
        game.SetAccount(9001, "in-game nickname", "different Steam/account nickname");
        Ready(reader.Read(), "123456", "in-game nickname", "spectator", "");
    }

    private static void Ready(ChatGameState state, string room, string nickname, string character, string order)
    {
        True(state.InformationReady && state.Available, "Authoritative game information was unavailable.");
        True(state.RoomId == room, "Wrong server room identity.");
        True(state.Nickname == nickname, "Wrong in-game nickname.");
        True(state.CharacterId == character, "Wrong player character/status.");
        True(state.Order == order, "Wrong zero-based slot conversion.");
    }

    private static void Pending(ChatGameState state, string message)
    {
        True(!state.InformationReady && !state.Available && state.CharacterId != "spectator", message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // No Steam/UI input exists here. Each pointer represents a replaceable live
    // object, and GetSelfInfo resolves only by the account's stable player ID.
    private sealed class FakeGameData : IGameDataAccess
    {
        private long _next = 10;
        private readonly Dictionary<(IntPtr Object, string Member), object> _members = new();
        private readonly Dictionary<IntPtr, Dictionary<long, IntPtr>> _rosters = new();
        private readonly Dictionary<IntPtr, Dictionary<long, IntPtr>> _selectionBoxes = new();
        private readonly IntPtr _class = new(1);
        private readonly IntPtr _manager = new(2);
        private readonly IntPtr _roomLogic = new(3);
        private readonly IntPtr _controller = new(4);
        private readonly IntPtr _watch = new(5);
        private IntPtr _account;
        private long _accountId;
        public IntPtr Room { get; private set; }
        public IntPtr Self => _rosters[Room].GetValueOrDefault(_accountId);
        public IntPtr? SelfOverride { get; set; }
        public bool AssemblyLoaded { get; set; } = true;
        public bool IsWatcher { get; set; }
        public List<bool> GetNickArguments { get; } = new();
        public List<long> SelectionPlayerIds { get; } = new();
        public int Mode
        {
            get => Value<int>(_controller, "roomStateType");
            set => Set(_controller, "roomStateType", value);
        }

        public FakeGameData()
        {
            Set(_manager, "<room>k__BackingField", _roomLogic);
            Set(_manager, "<watch>k__BackingField", _watch);
            Set(_roomLogic, "roomController", _controller);
            SetAccount(9001, "in-game nickname", "different Steam/account nickname");
            ReplaceRoom(123456, 4);
            AddPlayer(9001, "in-game nickname", 0, 101);
        }

        public void SetAccount(long id, string gameNickname, string accountNickname)
        {
            _accountId = id;
            _account = New();
            var player = New();
            Set(_manager, "<account>k__BackingField", _account);
            Set(_account, "player", player);
            Set(_account, "GetPlayerInfo", player);
            Set(_account, "GetPlayerID", id);
            Set(_account, "GetName", accountNickname);
            Set(player, "get_Id", id);
            Set(player, "get_Nick", gameNickname);
            Set(player, "nick_", gameNickname);
        }

        public void ReplaceRoom(long id, int mode)
        {
            Room = New();
            Set(Room, "get_Id", id);
            _rosters.Add(Room, new Dictionary<long, IntPtr>());
            ReplaceSelectionBox();
            Set(_controller, "_LocalRoom", Room);
            Mode = mode;
            SelfOverride = null;
        }

        public void ReplaceAccountPlayerInfo(long? id)
        {
            var player = id.HasValue ? New() : IntPtr.Zero;
            Set(_account, "GetPlayerInfo", player);
            if (id.HasValue)
            {
                Set(player, "get_Id", id.Value);
                Set(player, "get_Nick", "another player's nickname");
            }
        }

        public IntPtr AddPlayer(long id, string nickname, int slot, int heroId)
        {
            var self = New();
            Set(self, "get_Id", id);
            Set(self, "GetNick", nickname);
            SetSlot(self, slot);
            SetHero(self, heroId);
            _rosters[Room][id] = self;
            return self;
        }

        public void RemovePlayer(long id) => _rosters[Room].Remove(id);
        public void SetNickname(IntPtr self, string nickname) => Set(self, "GetNick", nickname);
        public void SetSlot(IntPtr self, int slot) => Set(self, "get_Slot", slot);
        public void SetHero(IntPtr self, int? id)
        {
            var hero = id.HasValue ? New() : IntPtr.Zero;
            Set(self, "get_Hero", hero);
            if (id.HasValue) Set(hero, "get_HeroId", id.Value);
        }

        public void ReplaceSelectionBox(bool exists = true)
        {
            var box = exists ? New() : IntPtr.Zero;
            Set(Room, "get_Box", box);
            if (exists) _selectionBoxes.Add(box, new Dictionary<long, IntPtr>());
        }

        public void SetSelectionHero(long playerId, int? heroId)
        {
            var box = Value<IntPtr>(Room, "get_Box");
            var entries = _selectionBoxes[box];
            if (!heroId.HasValue)
            {
                entries.Remove(playerId);
                return;
            }
            var bar = New();
            Set(bar, "get_HeroId", heroId.Value);
            entries[playerId] = bar;
        }

        private IntPtr New() => new(_next++);
        private void Set(IntPtr pointer, string member, object value) => _members[(pointer, member)] = value;
        private T Value<T>(IntPtr pointer, string member) => _members.TryGetValue((pointer, member), out var value)
            ? (T)value : throw new InvalidOperationException("Unexpected game access: " + member);

        public IntPtr FindClass(string assemblyName, string namespaze, string name)
        {
            True(assemblyName == "AstralParty.Runtime" && namespaze == "GameLogic" && name == "GameLogicManager",
                "Reader requested an unrelated assembly/type.");
            return AssemblyLoaded ? _class : IntPtr.Zero;
        }

        public IntPtr ReadStaticObject(IntPtr klass, string fieldName)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            True(klass == _class && fieldName == "_inst", "Reader bypassed the game singleton.");
            return _manager;
        }

        public IntPtr ReadObject(IntPtr instance, string fieldName) => Value<IntPtr>(instance, fieldName);
        public int ReadInt32(IntPtr instance, string fieldName) => Value<int>(instance, fieldName);
        public IntPtr Invoke(IntPtr instance, string methodName) => methodName == "GetSelfInfo"
            ? SelfOverride ?? _rosters[instance].GetValueOrDefault(_accountId)
            : Value<IntPtr>(instance, methodName);
        public IntPtr Invoke(IntPtr instance, string methodName, long argumentValue)
        {
            True(methodName == "GetHeroBarById", "Wrong selection data getter.");
            SelectionPlayerIds.Add(argumentValue);
            var box = Value<IntPtr>(instance, "get_Box");
            return box == IntPtr.Zero ? IntPtr.Zero : _selectionBoxes[box].GetValueOrDefault(argumentValue);
        }
        public long InvokeInt64(IntPtr instance, string methodName) => Value<long>(instance, methodName);
        public int InvokeInt32(IntPtr instance, string methodName) => Value<int>(instance, methodName);
        public bool InvokeBoolean(IntPtr instance, string methodName)
        {
            True(instance == _watch && methodName == "PlayerIsWatcher", "Wrong watcher identification path.");
            return IsWatcher;
        }
        public string InvokeString(IntPtr instance, string methodName) => Value<string>(instance, methodName);
        public string InvokeString(IntPtr instance, string methodName, bool argumentValue)
        {
            GetNickArguments.Add(argumentValue);
            True(methodName == "GetNick", "Wrong display nickname method.");
            return Value<string>(instance, methodName);
        }
    }
}
