using System;
using UnityEngine;

namespace AstralParty.Chat;

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

            BattlePortraitCache.Clear();
            _portraitCacheGeneration++;
            WarningNextAt.Clear();
            NativeWrapperTypeCache.Clear();

            _steamClientType = null;
            _steamNameProperty = null;
            _cachedSteamName = string.Empty;
            _nextSteamNameRefreshAt = 0f;
            _nextSteamTypeLookupAt = 0f;

            _findObjectsOfTypeAll = null;
            _log = null;
        }
    }

    private static void ResetBattlePortraitCache()
    {
        lock (Sync)
        {
            BattlePortraitCache.Clear();
            _portraitCacheGeneration++;
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
