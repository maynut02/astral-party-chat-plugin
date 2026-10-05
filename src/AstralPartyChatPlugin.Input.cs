using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AstralParty.Chat;

public sealed partial class AstralPartyChatPlugin
{
    private static Event? _gameGuiEvent;
    private static Event? _ignoredGameGuiEvent;
    private static bool _reportedNativeGuiCapture;
    private static bool _reportedCurrentGuiCapture;
    private static bool _reportedGameSubmitCapture;

    private static void ResetChatKeyboardInputState()
    {
        _gameGuiEvent = null;
        _ignoredGameGuiEvent = null;
        _reportedNativeGuiCapture = false;
        _reportedCurrentGuiCapture = false;
        _reportedGameSubmitCapture = false;
    }

    private static void PatchChatKeyboardInput(Harmony harmony)
    {
        ResetChatKeyboardInputState();
        var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(AstralPartyChatPlugin);
        HarmonyMethod Patch(string name) => new(type.GetMethod(name, flags)
            ?? throw new MissingMethodException(type.FullName, name));
        var boolPrefix = Patch(nameof(InputKeyboardBoolPrefix));
        var axisPrefix = Patch(nameof(InputKeyboardAxisPrefix));
        var names = new[] { "GetKey", "GetKeyDown", "GetKeyUp", "GetButton", "GetButtonDown", "GetButtonUp", "get_anyKey", "get_anyKeyDown" };
        foreach (var method in typeof(Input).GetMethods(flags).Where(method => names.Contains(method.Name)))
            harmony.Patch(method, prefix: boolPrefix);
        foreach (var name in new[] { "GetAxis", "GetAxisRaw" })
            harmony.Patch(typeof(Input).GetMethod(name, new[] { typeof(string) })
                ?? throw new MissingMethodException(typeof(Input).FullName, name), prefix: axisPrefix);
        harmony.Patch(typeof(Input).GetProperty(nameof(Input.inputString))!.GetMethod!, prefix: Patch(nameof(InputKeyboardStringPrefix)));

        // FairyGUI/IMGUI can otherwise drain key events before uGUI sees them.
        // The chat InputField receives the queue only inside the guarded scope below.
        harmony.Patch(typeof(Event).GetMethod(nameof(Event.PopEvent), flags, null, new[] { typeof(Event) }, null)
            ?? throw new MissingMethodException(typeof(Event).FullName, nameof(Event.PopEvent)), prefix: boolPrefix);

        // FairyGUI StageEngine.OnGUI reads Event.current and emits onKeyDown/
        // onKeyUp directly. It does not need Input.GetKey or Event.PopEvent.
        // These Unity methods have native MethodInfo entries in the game's
        // generated interop, so Harmony's IL2CPP backend can intercept the
        // engine callback even though FairyGUI is loaded later by HybridCLR.
        var processEvent = typeof(GUIUtility).GetMethod("ProcessEvent", flags, null,
            new[] { typeof(int), typeof(IntPtr), typeof(bool).MakeByRefType() }, null);
        // __2 is the callback's handled output, not its return value.
        if (processEvent?.ReturnType != typeof(void)) processEvent = null;
        PatchGameKeyboardRoute(processEvent,
            "GUIUtility.ProcessEvent", prefix: Patch(nameof(GameGuiProcessEventPrefix)));
        PatchGameKeyboardRoute(typeof(Event).GetProperty(nameof(Event.current), flags)?.GetMethod,
            "Event.current (FairyGUI OnGUI)", postfix: Patch(nameof(GameGuiCurrentEventPostfix)));
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

        var keyPressed = typeof(InputField).GetMethod("KeyPressed", flags, null, new[] { typeof(Event) }, null)
            ?? throw new MissingMethodException(typeof(InputField).FullName, "KeyPressed");
        harmony.Patch(keyPressed, prefix: Patch(nameof(ChatInputKeyPressedPrefix)));
        foreach (var name in new[] { "OnUpdateSelected", "LateUpdate" })
        {
            var method = typeof(InputField).GetMethod(name, flags)
                ?? throw new MissingMethodException(typeof(InputField).FullName, name);
            harmony.Patch(method, prefix: Patch(nameof(ChatInputReadPrefix)),
                postfix: Patch(nameof(ChatInputReadPostfix)), finalizer: Patch(nameof(ChatInputReadFinalizer)));
        }
    }

    private static bool IsGuiKeyboardEvent(Event? keyEvent) => keyEvent != null
        && (keyEvent.rawType == EventType.KeyDown || keyEvent.rawType == EventType.KeyUp);

    private static bool GameGuiProcessEventPrefix(IntPtr __1, ref bool __2)
    {
        if (__1 == IntPtr.Zero || !ChatOverlay.ShouldBlockGameKeyboardInput()) return true;
        var keyEvent = _gameGuiEvent ??= new Event();
        // nativeEventPtr is a Unity event buffer, not an IL2CPP object pointer.
        // Copy only for inspection; never consume or mutate the original event
        // or the queue that the chat InputField uses for text and IME commits.
        keyEvent.CopyFromPtr(__1);
        if (!IsGuiKeyboardEvent(keyEvent)) return true;
        __2 = true;
        if (!_reportedNativeGuiCapture)
        {
            _reportedNativeGuiCapture = true;
            LogKeyboardHook("captured: GUIUtility.ProcessEvent keyboard event");
        }
        return false;
    }

    private static void GameGuiCurrentEventPostfix(ref Event? __result)
    {
        if (!ChatOverlay.ShouldBlockGameKeyboardInput() || !IsGuiKeyboardEvent(__result)) return;
        // A second guard covers callers that reach OnGUI independently of the
        // engine callback. Event.Use() is insufficient: FairyGUI checks rawType.
        // Return a separate Ignore event instead of altering the native event.
        var ignored = _ignoredGameGuiEvent ??= new Event();
        // GUI consumers may modify the reused event. Restore its neutral
        // contents on every read before exposing it to another consumer.
        ignored.type = EventType.Ignore;
        ignored.keyCode = KeyCode.None;
        ignored.character = '\0';
        ignored.modifiers = EventModifiers.None;
        __result = ignored;
        if (!_reportedCurrentGuiCapture)
        {
            _reportedCurrentGuiCapture = true;
            LogKeyboardHook("captured: Event.current FairyGUI/IMGUI keyboard event");
        }
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

    private static bool ChatInputKeyPressedPrefix(InputField __instance, Event __0, ref InputField.EditState __result)
    {
        if (!ChatOverlay.InterceptChatInputKeyPress(__instance, __0, out var result)) return true;
        __result = result;
        return false;
    }

    private static void ChatInputReadPrefix(InputField __instance, out bool __state)
    {
        __state = ChatOverlay.IsChatInputField(__instance);
        if (__state) ChatOverlay.EnterChatInputRead();
    }

    private static void ChatInputReadPostfix(ref bool __state)
    {
        if (!__state) return;
        __state = false;
        ChatOverlay.ExitChatInputRead();
    }

    private static Exception? ChatInputReadFinalizer(Exception? __exception, bool __state)
    {
        if (__state) ChatOverlay.ExitChatInputRead();
        return __exception;
    }
}
