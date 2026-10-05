using System;
using System.Collections.Generic;
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

    private Harmony? _harmony;
    private PartyChatClient? _client;
    private int _lastUiRevision = -1;
    private string _lastUiStatus = string.Empty;
    private int _lastTickFrame = -1;

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

            var mousePrefix = typeof(AstralPartyChatPlugin).GetMethod(
                nameof(InputMouseButtonPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (mousePrefix != null)
            {
                foreach (var methodName in new[] { "GetMouseButtonDown", "GetMouseButton" })
                {
                    var mouseMethod = typeof(Input).GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(int) },
                        null);
                    if (mouseMethod != null)
                        _harmony.Patch(
                            mouseMethod,
                            prefix: new HarmonyMethod(mousePrefix));
                }
            }

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
        ChatOverlay.Shutdown();
        GameChatRuntime.Shutdown();
        _lastUiRevision = -1;
        _lastUiStatus = string.Empty;
        _lastTickFrame = -1;
        _lastStageError.Clear();
        return true;
    }

    private static AstralPartyChatPlugin? _instance;

    private static void EventSystemUpdatePrefix()
    {
        _instance?.Tick();
    }

    private static bool InputMouseButtonPrefix(int button, ref bool __result)
    {
        if (button == 0 && ChatOverlay.ShouldBlockRawMouseInput())
        {
            __result = false;
            return false;
        }

        return true;
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
