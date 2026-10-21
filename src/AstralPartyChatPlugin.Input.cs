using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AstralPartyChatPlugin;

public sealed partial class AstralPartyChatPlugin
{
    private static bool _reportedGameSubmitCapture;

    private static void ResetChatKeyboardInputState()
    {
        _reportedGameSubmitCapture = false;
    }

    /// <summary>
    /// The chat input is a FairyGUI GTextInput: FairyGUI routes typed keys to
    /// it and stops their propagation, so game UI listeners stay quiet. These
    /// patches mask the remaining game code that polls Unity Input directly
    /// (camera keys, hotkeys) while the input has focus.
    /// </summary>
    private static void PatchChatKeyboardInput(Harmony harmony)
    {
        ResetChatKeyboardInputState();
        var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(AstralPartyChatPlugin);
        HarmonyMethod Patch(string name) => new(type.GetMethod(name, flags)
            ?? throw new MissingMethodException(type.FullName, name));

        var keyCodeGetKey = typeof(Input).GetMethod(nameof(Input.GetKey), new[] { typeof(KeyCode) })
            ?? throw new MissingMethodException(typeof(Input).FullName, nameof(Input.GetKey));
        harmony.Patch(keyCodeGetKey, prefix: Patch(nameof(InputKeyHeldPrefix)));
        var boolPrefix = Patch(nameof(InputKeyboardBoolPrefix));
        var names = new[] { "GetKey", "GetKeyDown", "GetKeyUp", "GetButton", "GetButtonDown", "GetButtonUp", "get_anyKey", "get_anyKeyDown" };
        foreach (var method in typeof(Input).GetMethods(flags).Where(method => names.Contains(method.Name) && method != keyCodeGetKey))
            harmony.Patch(method, prefix: boolPrefix);
        foreach (var name in new[] { "GetAxis", "GetAxisRaw" })
            harmony.Patch(typeof(Input).GetMethod(name, new[] { typeof(string) })
                ?? throw new MissingMethodException(typeof(Input).FullName, name), prefix: Patch(nameof(InputKeyboardAxisPrefix)));
        harmony.Patch(typeof(Input).GetProperty(nameof(Input.inputString))!.GetMethod!, prefix: Patch(nameof(InputKeyboardStringPrefix)));

        // Observation only: FairyGUI's StageEngine.OnGUI reads key events from
        // Event.current, which is where Windows reports an IME-committing Enter.
        PatchGameKeyboardRoute(typeof(Event).GetProperty(nameof(Event.current), flags)?.GetMethod,
            "Event.current (FairyGUI OnGUI)", postfix: Patch(nameof(GuiCurrentEventPostfix)));
        PatchGameKeyboardRoute(typeof(StandaloneInputModule).GetMethod("SendSubmitEventToSelectedObject", flags),
            "StandaloneInputModule.SendSubmitEventToSelectedObject", prefix: Patch(nameof(GameSubmitEventPrefix)));

        void PatchGameKeyboardRoute(MethodInfo? method, string route,
            HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
        {
            if (method == null)
            {
                LogKeyboardHook("unavailable: " + route, warning: true);
                return;
            }
            try
            {
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                LogKeyboardHook("registered: " + route);
            }
            catch (Exception ex)
            {
                // A changed game version should retain chat and the remaining
                // input guards, with the failed route visible in the game log.
                LogKeyboardHook("failed: " + route + " [" + ex.GetType().Name + "]", warning: true);
            }
        }
    }

    private static void GuiCurrentEventPostfix(Event? __result)
    {
        if (__result != null && __result.rawType == EventType.KeyDown)
            ChatOverlay.ObserveGuiKeyDown(__result.keyCode);
    }

    private static bool GameSubmitEventPrefix(ref bool __result)
    {
        if (!ChatOverlay.ShouldBlockGameKeyboardInput()) return true;
        __result = false;
        if (!_reportedGameSubmitCapture)
        {
            _reportedGameSubmitCapture = true;
            LogKeyboardHook("captured: uGUI selected-object submit");
        }
        return false;
    }

    private static void LogKeyboardHook(string message, bool warning = false)
    {
#if !OVERLAY_HARNESS
        if (warning) _instance?.Log.LogWarning("Chat keyboard hook " + message);
        else _instance?.Log.LogInfo("Chat keyboard hook " + message);
#endif
    }

    // FairyGUI reads held modifiers through Input.GetKey for Ctrl+C/V/A and
    // Shift selection in the chat input; only those stay visible while typing.
    private static bool IsModifierKey(KeyCode key) => key is KeyCode.LeftShift or KeyCode.RightShift
        or KeyCode.LeftControl or KeyCode.RightControl or KeyCode.LeftAlt or KeyCode.RightAlt
        or KeyCode.LeftCommand or KeyCode.RightCommand;

    private static bool InputKeyHeldPrefix(KeyCode __0, ref bool __result)
    {
        if (IsModifierKey(__0) || !ChatOverlay.ShouldBlockGameKeyboardInput()) return true;
        __result = false;
        return false;
    }

    private static bool InputKeyboardBoolPrefix(ref bool __result)
    {
        if (!ChatOverlay.ShouldBlockGameKeyboardInput()) return true;
        __result = false;
        return false;
    }

    private static bool InputKeyboardAxisPrefix(ref float __result)
    {
        if (!ChatOverlay.ShouldBlockGameKeyboardInput()) return true;
        __result = 0f;
        return false;
    }

    private static bool InputKeyboardStringPrefix(ref string __result)
    {
        if (!ChatOverlay.ShouldBlockGameKeyboardInput()) return true;
        __result = string.Empty;
        return false;
    }
}
