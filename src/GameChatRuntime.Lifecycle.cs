using System;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class GameChatRuntime
{
    private const float RuntimeWarningIntervalSeconds = 30f;

    public static void Shutdown()
    {
        lock (Sync)
        {
            _initialized = false;
            _snapshot = new ChatSnapshot();
            Session.Reset();
            ReleaseExpressionWindow();
            _nextExpressionScanAt = 0f;
            _loggedAnchorPhase = string.Empty;
            _loggedAnchor = string.Empty;

            WarningNextAt.Clear();

            NativeData.Clear();
            FairyGui.Clear();
            _gameState = GameStateReader.Pending();
            _nextGameDataAt = 0f;

            _log = null;
        }
    }

    private static void LogRuntimeWarning(string key, string message)
    {
        var logger = _log;
        if (logger == null)
            return;

        float now;
        try
        {
            now = Time.unscaledTime;
        }
        catch
        {
            now = Environment.TickCount64 / 1000f;
        }

        lock (Sync)
        {
            if (WarningNextAt.TryGetValue(key, out var nextAt) && now < nextAt)
                return;

            WarningNextAt[key] = now + RuntimeWarningIntervalSeconds;
        }

        logger.LogWarning(message);
    }
}
