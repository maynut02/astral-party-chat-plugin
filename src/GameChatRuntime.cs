using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class GameChatRuntime
{
    private const float GameDataIntervalSeconds = 0.25f;
    private static readonly object Sync = new object();

    private static ManualLogSource? _log;
    private static readonly Dictionary<string, float> WarningNextAt =
        new Dictionary<string, float>(StringComparer.Ordinal);
    private static bool _initialized;
    private static ChatSnapshot _snapshot = new ChatSnapshot();
    private static readonly GameSessionState Session = new GameSessionState();
    private static readonly NativeGameData NativeData = new();
    private static readonly GameStateReader StateReader = new(NativeData);
    private static ChatGameState _gameState = GameStateReader.Pending();
    private static float _nextGameDataAt;

    public static ChatGameState GetChatGameState() => _gameState;

    private static void ReadGameState()
    {
        _nextGameDataAt = Time.unscaledTime + GameDataIntervalSeconds;
        ChatGameState state;
        try { state = StateReader.Read(); }
        catch (Exception ex)
        {
            state = GameStateReader.Pending();
            LogRuntimeWarning("game-data", "Game data read failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        var previous = _gameState;
        _gameState = state;
        var phase = state.Available ? state.Phase : "기타";
        Session.ObservePhase(phase);
        Session.ObserveRoomId(state.Available ? state.RoomId : string.Empty);
        Session.SetOrder(state.Order);
        Session.SetCharacter(state.CharacterId);
        _snapshot.ChatAvailable = state.Available;
        _snapshot.RoomId = state.RoomId;
        if (previous.InformationReady != state.InformationReady || previous.Available != state.Available
            || previous.Phase != state.Phase)
            _log?.LogInfo("Game data: " + (state.InformationReady ? (state.Available ? state.Phase : "outside room") : "pending") + ".");
    }

    public static void Initialize(ManualLogSource log)
    {
        if (_initialized) return;
        _initialized = true;
        _log = log;
    }

    public static void Tick()
    {
        if (!_initialized) return;

        try
        {
            if (Time.unscaledTime >= _nextGameDataAt)
                ReadGameState();

            // FairyGUI is created by the game after the plugin loads.
            var root = FguiNode.Root;
            if (root == null)
                return;

            ResolveChatScreen(root);
            ChatOverlay.Refresh(_snapshot, root);
        }
        catch (Exception ex)
        {
            LogRuntimeWarning(
                "tick",
                "Chat runtime tick failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

internal sealed class ChatSnapshot
{
    public string ScreenPhase { get; set; } = "기타";
    public bool ChatAvailable { get; set; }
    public string RoomId { get; set; } = string.Empty;
    /// <summary>Whether this screen can keep an open chat window, independently of button visibility.</summary>
    public bool ChatAvailableOnScreen { get; set; }
    public bool ChatButtonVisible { get; set; }
    /// <summary>Component that owns the chat button; a game panel, or GRoot as a fallback.</summary>
    public FguiNode? ChatButtonHost { get; set; }
    /// <summary>Top-left of the chat button in <see cref="ChatButtonHost"/> coordinates.</summary>
    public Vector2 ChatButtonPosition { get; set; }
    public float ChatButtonSize { get; set; }
}
