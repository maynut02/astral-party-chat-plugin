using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

[assembly: AssemblyVersion(global::AstralPartyChatPlugin.AstralPartyChatPlugin.PluginVersion)]
[assembly: AssemblyFileVersion(global::AstralPartyChatPlugin.AstralPartyChatPlugin.PluginVersion)]
[assembly: AssemblyInformationalVersion(global::AstralPartyChatPlugin.AstralPartyChatPlugin.PluginVersion)]

namespace AstralPartyChatPlugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed partial class AstralPartyChatPlugin : BasePlugin
{
    // Keep the installed plugin identity stable across display/assembly renames.
    public const string PluginGuid = "astral-party.chat";
    public const string PluginName = "AstralPartyChatPlugin";
    public const string PluginVersion = global::AstralBuildVersion.Value;

    private Harmony? _harmony;
    private PartyChatClient? _client;
    private int _lastUiRevision = -1;
    private string _lastUiStatus = string.Empty;
    private int _lastTickFrame = -1;
    private PluginUpdateClient? _updates;
    private RuntimeHttp? _updateHttp;
    private Action? _restart;
    private Action<string>? _noticeWarning;
    private bool _restartRequested;

    public override void Load()
    {
        try
        {
            GameChatRuntime.Initialize(Log);
            _client = new PartyChatClient(Log);

            var update = typeof(EventSystem).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (update == null)
                throw new MissingMethodException(typeof(EventSystem).FullName, "Update");

            var prefix = typeof(AstralPartyChatPlugin).GetMethod(
                nameof(EventSystemUpdatePrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix == null)
                throw new MissingMethodException(nameof(EventSystemUpdatePrefix));

            _instance = this;
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(update, prefix: new HarmonyMethod(prefix));

            PatchChatKeyboardInput(_harmony);
            StartUpdateCheck();

            Log.LogInfo("AstralPartyChatPlugin " + PluginVersion + " loaded.");
        }
        catch (Exception ex)
        {
            Unload();
            Log.LogError("AstralPartyChatPlugin initialization failed: " + ex);
        }
    }

    public override bool Unload()
    {
        _instance = null;
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { Log.LogWarning("Chat patch cleanup failed: " + ex.Message); }
        _harmony = null;
        ResetChatKeyboardInputState();
        _client?.Dispose();
        _client = null;
        _updates?.Dispose();
        _updates = null;
        _updateHttp?.Dispose();
        _updateHttp = null;
        UpdateNotice.Shutdown();
        ChatOverlay.Shutdown();
        GameChatRuntime.Shutdown();
        _lastUiRevision = -1;
        _lastUiStatus = string.Empty;
        _lastTickFrame = -1;
        _lastStageError.Clear();
        return true;
    }

    // Runs last in Load: an update problem must never keep the chat from starting.
    private void StartUpdateCheck()
    {
        try
        {
            var pluginPath = typeof(AstralPartyChatPlugin).Assembly.Location;
            if (string.IsNullOrEmpty(pluginPath))
            {
                Log.LogWarning("Plugin file location is unknown; update check skipped.");
                return;
            }
            var installer = new UpdateInstaller(pluginPath, message => Log.LogInfo(message));
            installer.PrepareStartup();
            var autoUpdate = Config.Bind("Update", "AutoUpdate", true,
                "게임 시작 시 서명된 최신 플러그인을 자동 설치합니다. 적용하려면 게임을 다시 시작해야 합니다.");
            string bepInExVersion;
            try
            {
                // The API returns SemanticVersioning.Version; avoid adding that DLL to the plugin references.
                bepInExVersion = typeof(Paths).GetProperty("BepInExVersion", BindingFlags.Public | BindingFlags.Static)?
                    .GetValue(null)?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                bepInExVersion = string.Empty;
                Log.LogWarning("Could not read the running BepInEx version; plugin auto-install disabled: " + ex.Message);
            }
            _restart = Restart;
            _noticeWarning = message => Log.LogWarning(message);
            _updateHttp = new RuntimeHttp(PluginVersion);
            _updates = new PluginUpdateClient(PluginVersion, bepInExVersion, autoUpdate.Value,
                _updateHttp.DownloadAsync, installer.Install, message => Log.LogInfo(message));
            Log.LogInfo("Plugin update check started; BepInEx=" + bepInExVersion + "; AutoUpdate=" + autoUpdate.Value);
        }
        catch (Exception ex)
        {
            Log.LogWarning("Plugin update check unavailable: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void Restart()
    {
        var updates = _updates;
        if (_restartRequested || updates == null || !updates.Snapshot().RestartRequired)
            return;
        try
        {
            using var current = Process.GetCurrentProcess();
            var executable = Paths.ExecutablePath;
            if (string.IsNullOrEmpty(executable))
                executable = current.MainModule?.FileName ?? string.Empty;
            GameRestart.Start(executable, current.Id);
            _restartRequested = true;
            Log.LogInfo("Restart helper waiting for game PID " + current.Id + "; executable=" + executable);
            Application.Quit();
        }
        catch (Exception ex) { updates.RestartFailed(ex); }
    }

    private static AstralPartyChatPlugin? _instance;

    private static void EventSystemUpdatePrefix()
    {
        _instance?.Tick();
    }

    private void Tick()
    {
        var frame = Time.frameCount;
        if (_lastTickFrame == frame)
            return;
        _lastTickFrame = frame;

        var client = _client;
        if (client == null)
            return;

        try
        {
            GameChatRuntime.Tick();
            var state = GameChatRuntime.GetChatGameState();
            client.UpdateGameState(state);
        }
        catch (Exception ex)
        {
            LogStageError("game-state", ex);
        }

        var updates = _updates;
        if (updates != null)
            UpdateNotice.Tick(updates.Revision, updates.Snapshot(), _restart!, _noticeWarning!);

        try
        {
            while (ChatOverlay.TryDequeueOutgoing(out var text))
                client.SendChat(text);
        }
        catch (Exception ex)
        {
            LogStageError("outgoing", ex);
        }

        PartyUiSnapshot snapshot;
        try
        {
            snapshot = client.GetUiSnapshot();
        }
        catch (Exception ex)
        {
            LogStageError("snapshot", ex);
            return;
        }

        if (!string.Equals(snapshot.Status, _lastUiStatus, StringComparison.Ordinal))
        {
            try
            {
                ChatOverlay.SetChatStatus(snapshot.Status);
                _lastUiStatus = snapshot.Status;
            }
            catch (Exception ex)
            {
                LogStageError("status-ui", ex);
            }
        }

        if (snapshot.Revision != _lastUiRevision)
        {
            try
            {
                ChatOverlay.SetChatMessages(snapshot.Messages);
                _lastUiRevision = snapshot.Revision;
            }
            catch (Exception ex)
            {
                LogStageError("messages-ui", ex);
            }
        }
    }

    private readonly Dictionary<string, DateTime> _lastStageError = new();

    private void LogStageError(string stage, Exception ex)
    {
        var now = DateTime.UtcNow;
        if (_lastStageError.TryGetValue(stage, out var last)
            && (now - last).TotalSeconds < 2)
            return;

        _lastStageError[stage] = now;
        Log.LogWarning("Chat stage failed [" + stage + "]: " + ex);
    }
}
