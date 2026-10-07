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
            _scanning = false;
            _nextScanAt = 0f;
            _snapshot = new ChatSnapshot();
            Session.Reset();

            _scanCameras = Array.Empty<Camera>();

            WarningNextAt.Clear();

            NativeData.Clear();
            _gameState = GameStateReader.Pending();
            _nextGameDataAt = 0f;

            _findObjectsOfTypeAll = null;
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
