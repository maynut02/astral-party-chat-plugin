#if OVERLAY_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AstralPartyChatPlugin;
using PluginEntryPoint = AstralPartyChatPlugin.AstralPartyChatPlugin;
using UnityEngine;
using UnityEngine.UI;

internal static class KeyboardInputTests
{
    private static int _passed;
    private static int _assertions;

    internal static void Run()
    {
        Case("native GUI callback, FairyGUI current event and uGUI submit routes are registered", () =>
        {
            var harmony = new HarmonyLib.Harmony();
            Method("PatchChatKeyboardInput").Invoke(null, new object[] { harmony });
            Assert(harmony.Patches.Any(p => p.Method.DeclaringType == typeof(GUIUtility)
                && p.Method.Name == "ProcessEvent" && p.Method.ReturnType == typeof(void)
                && p.Method.GetParameters()[2].ParameterType == typeof(bool).MakeByRefType()
                && p.Prefix?.Method.Name == "GameGuiProcessEventPrefix"), "Native void GUI entry with handled output was not patched.");
            Assert(harmony.Patches.Any(p => p.Method.DeclaringType == typeof(Event)
                && p.Method.Name == "get_current" && p.Postfix?.Method.Name == "GameGuiCurrentEventPostfix"), "FairyGUI current event route was not patched.");
            Assert(harmony.Patches.Any(p => p.Method.Name == "SendSubmitEventToSelectedObject"
                && p.Prefix?.Method.Name == "GameSubmitEventPrefix"), "Selected game object submit was not patched.");
        });
        Case("one unavailable native route retains the other high-level input guards", () =>
        {
            var harmony = new HarmonyLib.Harmony { FailMethodName = "ProcessEvent" };
            Method("PatchChatKeyboardInput").Invoke(null, new object[] { harmony });
            Assert(harmony.Patches.Any(p => p.Method.Name == "get_current"), "Failed native entry disabled the FairyGUI fallback.");
            Assert(harmony.Patches.Any(p => p.Method.Name == "SendSubmitEventToSelectedObject"), "Failed native entry disabled game submit capture.");
            Assert(harmony.Patches.Any(p => p.Method.Name == "KeyPressed"), "Failed optional guard disabled chat Enter handling.");
        });
        Case("native GUI keyboard events are handled before FairyGUI receives them", () =>
        {
            ChatOverlay.ResetHarness();
            foreach (var kind in new[] { EventType.KeyDown, EventType.KeyUp })
            {
                var original = new Event { rawType = kind, keyCode = KeyCode.Return };
                Event.NativeEventForTest = original;
                var arguments = new object?[] { new IntPtr(1), false };
                var runOriginal = (bool)Method("GameGuiProcessEventPrefix").Invoke(null, arguments)!;
                Assert(!runOriginal && arguments[1] is true, "Native keyboard event reached the game GUI callback.");
                Assert(original.rawType == kind && original.keyCode == KeyCode.Return, "Capture mutated the shared input event.");
            }
        });
        Case("native GUI capture preserves mouse, wheel, layout and repaint events", () =>
        {
            ChatOverlay.ResetHarness();
            foreach (var kind in new[] { EventType.MouseDown, EventType.ScrollWheel, EventType.Layout, EventType.Repaint })
            {
                Event.NativeEventForTest = new Event { rawType = kind };
                var arguments = new object?[] { new IntPtr(1), false };
                Assert((bool)Method("GameGuiProcessEventPrefix").Invoke(null, arguments)! && arguments[1] is false,
                    "Non-keyboard GUI behavior was swallowed: " + kind);
            }
            Event.NativeEventForTest = null;
            var empty = new object?[] { IntPtr.Zero, false };
            Assert((bool)Method("GameGuiProcessEventPrefix").Invoke(null, empty)! && empty[1] is false, "Null event pointer was inspected.");
        });
        Case("FairyGUI raw key events cannot activate buttons even without legacy polling", () =>
        {
            ChatOverlay.ResetHarness();
            foreach (var key in new[] { KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Escape })
            {
                var original = KeyEvent(key);
                Assert(FairyGuiWouldReceiveKey(original), "The modeled GUI route did not bypass legacy Input polling.");
                var masked = CurrentGuiEvent(original);
                Assert(masked != null && masked.rawType == EventType.Ignore && !FairyGuiWouldReceiveKey(masked), "FairyGUI still received a raw key event.");
                Assert(original.rawType == EventType.KeyDown && original.keyCode == key, "Current-event guard altered chat's shared event.");
            }
            var keyUp = new Event { rawType = EventType.KeyUp, keyCode = KeyCode.Return };
            Assert(CurrentGuiEvent(keyUp)?.rawType == EventType.Ignore, "Key release leaked into FairyGUI.");
            var repaint = new Event { rawType = EventType.Repaint };
            Assert(ReferenceEquals(CurrentGuiEvent(repaint), repaint), "Rendering event was replaced.");
            Assert(CurrentGuiEvent(null) == null, "Missing GUI event was replaced.");
        });
        Case("selected game buttons cannot receive uGUI submit while chat has focus", () =>
        {
            ChatOverlay.ResetHarness();
            var result = InvokePrefix("GameSubmitEventPrefix", true);
            Assert(!result.RunOriginal && result.Value is false, "Selected game button handled chat Enter.");
        });
        Case("a GUI consumer cannot poison the cached Ignore event for the next read", () =>
        {
            ChatOverlay.ResetHarness();
            var ignored = CurrentGuiEvent(KeyEvent(KeyCode.Return))!;
            ignored.type = EventType.KeyDown; ignored.keyCode = KeyCode.Return;
            ignored.character = 'x'; ignored.modifiers = EventModifiers.Shift;
            var original = KeyEvent(KeyCode.KeypadEnter);
            var next = CurrentGuiEvent(original)!;
            Assert(ReferenceEquals(next, ignored), "Each current-event read allocated another mask.");
            Assert(next.rawType == EventType.Ignore && !FairyGuiWouldReceiveKey(next), "Modified mask leaked a key on the next read.");
            Assert(next.keyCode == KeyCode.None && next.character == '\0' && next.modifiers == EventModifiers.None,
                "Modified mask retained keyboard contents.");
            Assert(original.rawType == EventType.KeyDown && original.keyCode == KeyCode.KeypadEnter, "Reset altered the original chat event.");
        });
        Case("keyboard cleanup releases event caches and capture diagnostics before reload", () =>
        {
            ChatOverlay.ResetHarness();
            var ignored = CurrentGuiEvent(KeyEvent(KeyCode.Return))!;
            Event.NativeEventForTest = KeyEvent(KeyCode.Return);
            Method("GameGuiProcessEventPrefix").Invoke(null, new object?[] { new IntPtr(1), false });
            InvokePrefix("GameSubmitEventPrefix", true);
            Method("ResetChatKeyboardInputState").Invoke(null, null);
            foreach (var name in new[] { "_gameGuiEvent", "_ignoredGameGuiEvent" })
                Assert(typeof(PluginEntryPoint).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) == null,
                    "Cleanup retained the Unity event cache: " + name);
            foreach (var name in new[] { "_reportedNativeGuiCapture", "_reportedCurrentGuiCapture", "_reportedGameSubmitCapture" })
                Assert(typeof(PluginEntryPoint).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is false,
                    "Cleanup retained capture diagnostics: " + name);
            Assert(!ReferenceEquals(CurrentGuiEvent(KeyEvent(KeyCode.Return)), ignored), "Reload reused the previous Unity event.");
        });
        Case("native GUI guards allow the chat's own input scope and restore capture", () =>
        {
            ChatOverlay.ResetHarness();
            var original = KeyEvent(KeyCode.Return); Event.NativeEventForTest = original;
            ChatOverlay.EnterChatInputRead();
            try
            {
                var arguments = new object?[] { new IntPtr(1), false };
                Assert((bool)Method("GameGuiProcessEventPrefix").Invoke(null, arguments)! && arguments[1] is false, "Chat scope could not inspect its own event.");
                Assert(ReferenceEquals(CurrentGuiEvent(original), original), "Chat scope received a masked key.");
            }
            finally { ChatOverlay.ExitChatInputRead(); }
            Assert(CurrentGuiEvent(original)?.rawType == EventType.Ignore, "Chat read scope leaked to the game GUI.");
        });
        Case("native GUI guards restore game input when focus ends or chat closes", () =>
        {
            ChatOverlay.ResetHarness();
            foreach (var closeWindow in new[] { false, true })
            {
                ChatOverlay.ResetHarness();
                var original = KeyEvent(KeyCode.Return); Event.NativeEventForTest = original;
                if (closeWindow) ChatOverlay.CloseChatWindowForTest();
                else ChatOverlay.GetChatInputFieldForTest().DeactivateInputField();
                var arguments = new object?[] { new IntPtr(1), false };
                Assert((bool)Method("GameGuiProcessEventPrefix").Invoke(null, arguments)! && arguments[1] is false, "Game GUI remained captured.");
                Assert(ReferenceEquals(CurrentGuiEvent(original), original), "Game current event remained masked.");
                Assert(InvokePrefix("GameSubmitEventPrefix", true).RunOriginal, "Game submit remained blocked.");
            }
        });
        Case("FairyGUI capture preserves single-Enter Korean commit and send", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("한"); Input.compositionString = "글";
            var commit = KeyEvent(KeyCode.Return);
            Assert(CurrentGuiEvent(commit)?.rawType == EventType.Ignore && commit.rawType == EventType.KeyDown,
                "IME Enter either leaked to FairyGUI or was consumed from chat.");
            NativeKey(KeyCode.Return);
            Input.compositionString = string.Empty; ChatOverlay.SetDraft("한글"); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetChatInputFocusedForTest(), "IME commit sent or blurred chat.");
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "Committed Korean draft needed a second Enter.");
        });
        Case("plain Enter is sent once and keeps input focused", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("hello");
            NativeKey(KeyCode.Return);
            Assert(ChatOverlay.GetChatInputFocusedForTest(), "Native Enter deactivated chat.");
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "hello", "Single Enter was not sent.");
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Enter duplicated the message.");
            Assert(ChatOverlay.GetChatInputFocusedForTest(), "Sending lost focus.");
        });
        Case("keypad Enter uses the same single press path", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("keypad");
            NativeKey(KeyCode.KeypadEnter); ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "keypad", "Keypad Enter failed.");
        });
        Case("raw polling and native Enter event never send twice", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("one press");
            ChatOverlay.TickInputForTest(KeyCode.Return);
            NativeKey(KeyCode.Return, advanceFrame: false);
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "one press", "Combined Enter did not send.");
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Combined Enter sent twice.");
        });
        Case("native IME Enter retains focus and sends once after commit", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("한");
            Input.compositionString = "글";
            NativeKey(KeyCode.Return);
            Assert(ChatOverlay.GetChatInputFocusedForTest(), "IME commit lost focus.");
            Input.compositionString = string.Empty; ChatOverlay.SetDraft("한글");
            ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "IME commit sent the draft.");
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "First Enter did not send committed text.");
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "IME Enter submitted twice.");
            Assert(ChatOverlay.GetChatInputFocusedForTest(), "IME send lost input focus.");
        });
        Case("composition ending on the Enter frame submits without another press", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("한");
            Input.compositionString = "글"; ChatOverlay.TickInputForTest();
            Input.compositionString = string.Empty; ChatOverlay.SetDraft("한글");
            NativeKey(KeyCode.Return);
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Commit frame sent before text settled.");
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "Commit-frame Enter needed another press.");
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Commit-frame Enter duplicated its send.");
        });
        Case("IME Enter waits across several frames without rewriting composition", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("안녕"); Input.compositionString = "가";
            NativeKey(KeyCode.Return);
            for (var i = 0; i < 8; i++) ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetDraft() == "안녕"
                && Input.compositionString == "가", "Pending Enter modified or sent uncommitted text.");
            Input.compositionString = string.Empty; ChatOverlay.SetDraft("안녕가");
            ChatOverlay.TickInputForTest(); ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "안녕가", "Slow IME commit dropped the Enter request.");
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Slow commit submitted twice.");
        });
        Case("one IME Enter validates the completed draft against the text limit", () =>
        {
            ChatOverlay.ResetHarness(); var draft = new string('가', 1000);
            ChatOverlay.SetDraft(draft); Input.compositionString = "나"; NativeKey(KeyCode.Return);
            Input.compositionString = string.Empty; ChatOverlay.SetDraft(draft + "나");
            ChatOverlay.TickInputForTest(); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetDraft() == draft + "나", "IME bypassed validation or lost the completed draft.");
            Assert(ChatOverlay.GetChatInputFocusedForTest(), "Rejected IME draft lost focus.");
        });
        Case("over-limit native Enter preserves the focused draft", () =>
        {
            ChatOverlay.ResetHarness(); var draft = new string('가', 1001); ChatOverlay.SetDraft(draft);
            NativeKey(KeyCode.Return); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetDraft() == draft, "Over-limit Enter lost draft.");
            Assert(ChatOverlay.GetChatInputFocusedForTest(), "Rejected draft lost focus.");
        });
        Case("other fields and Escape keep native editing behavior", () =>
        {
            ChatOverlay.ResetHarness();
            var other = new GameObject("OtherInput").AddComponent<InputField>();
            other.ActivateInputField();
            Assert(!ChatOverlay.InterceptChatInputKeyPress(other, KeyEvent(KeyCode.Return), out _), "Patched another field's Enter.");
            Assert(!ChatOverlay.InterceptChatInputKeyPress(ChatOverlay.GetChatInputFieldForTest(), KeyEvent(KeyCode.Escape), out _), "Captured an unrelated editing key.");
        });
        Case("game bool, axis and text polling is masked while chat has focus", () =>
        {
            ChatOverlay.ResetHarness();
            Assert(ChatOverlay.ShouldBlockGameKeyboardInput(), "Focused field did not capture keyboard.");
            var result = InvokePrefix("InputKeyboardBoolPrefix", true);
            Assert(!result.RunOriginal && result.Value is false, "Game key/button was not masked.");
            result = InvokePrefix("InputKeyboardAxisPrefix", 0.9f);
            Assert(!result.RunOriginal && result.Value is float axis && axis == 0f, "Game axis was not masked.");
            result = InvokePrefix("InputKeyboardStringPrefix", "typed");
            Assert(!result.RunOriginal && result.Value as string == string.Empty, "Game typed text was not masked.");
        });
        Case("chat can read keys through nested input scopes and restores blocking", () =>
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.EnterChatInputRead(); ChatOverlay.EnterChatInputRead();
            try
            {
                Assert(!ChatOverlay.ShouldBlockGameKeyboardInput(), "Chat's own keys were masked.");
                Assert(InvokePrefix("InputKeyboardBoolPrefix", true).RunOriginal, "Chat's native input could not read keys.");
                ChatOverlay.ExitChatInputRead();
                Assert(!ChatOverlay.ShouldBlockGameKeyboardInput(), "Nested scope exited too early.");
            }
            finally { ChatOverlay.ExitChatInputRead(); }
            Assert(ChatOverlay.ShouldBlockGameKeyboardInput(), "Input capture scope leaked.");
        });
        Case("InputField scope is released after success and exception", () =>
        {
            ChatOverlay.ResetHarness(); var field = ChatOverlay.GetChatInputFieldForTest();
            var prefix = Method("ChatInputReadPrefix"); var postfix = Method("ChatInputReadPostfix"); var finalizer = Method("ChatInputReadFinalizer");
            var arguments = new object?[] { field, false };
            prefix.Invoke(null, arguments);
            Assert(!ChatOverlay.ShouldBlockGameKeyboardInput() && arguments[1] is true, "Native field scope did not open.");
            var postArguments = new[] { arguments[1] }; postfix.Invoke(null, postArguments);
            Assert(ChatOverlay.ShouldBlockGameKeyboardInput() && postArguments[0] is false, "Normal postfix did not release scope.");
            finalizer.Invoke(null, new object?[] { null, postArguments[0] });
            Assert(ChatOverlay.ShouldBlockGameKeyboardInput(), "Finalizer released scope twice.");
            prefix.Invoke(null, arguments);
            var error = new InvalidOperationException("test");
            Assert(ReferenceEquals(finalizer.Invoke(null, new object?[] { error, arguments[1] }), error), "Finalizer swallowed error.");
            Assert(ChatOverlay.ShouldBlockGameKeyboardInput(), "Exception leaked native read scope.");
        });
        Case("closing chat and ending focus restores game keyboard polling", () =>
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.GetChatInputFieldForTest().DeactivateInputField();
            Assert(!ChatOverlay.ShouldBlockGameKeyboardInput(), "Unfocused chat blocked game input.");
            ChatOverlay.GetChatInputFieldForTest().ActivateInputField();
            ChatOverlay.CloseChatWindowForTest();
            Assert(!ChatOverlay.ShouldBlockGameKeyboardInput(), "Closed chat blocked game input.");
            var result = InvokePrefix("InputKeyboardBoolPrefix", true);
            Assert(result.RunOriginal && result.Value is true, "Game input did not restore after close.");
        });
        Case("closing chat cancels a pending native Enter", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("pending");
            NativeKey(KeyCode.Return); ChatOverlay.CloseChatWindowForTest(); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Hidden chat sent pending Enter.");
        });
        Case("focus loss cancels pending Enter without sending or restoring focus", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("keep this draft");
            NativeKey(KeyCode.Return);
            var field = ChatOverlay.GetChatInputFieldForTest();
            field.DeactivateInputField();
            ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetDraft() == "keep this draft", "Unfocused Enter sent or cleared the draft.");
            Assert(!field.isFocused && !ChatOverlay.ShouldBlockGameKeyboardInput(), "Canceled Enter stole focus back from the game.");
            field.ActivateInputField(); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Refocusing resurrected canceled Enter.");
        });
        Case("focus loss cancels IME Enter before composition settles", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("한"); Input.compositionString = "글";
            NativeKey(KeyCode.Return);
            ChatOverlay.GetChatInputFieldForTest().DeactivateInputField(); ChatOverlay.TickInputForTest();
            Input.compositionString = string.Empty; ChatOverlay.SetDraft("한글");
            ChatOverlay.TickInputForTest(); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetDraft() == "한글", "IME commit resurrected an unfocused Enter.");
            Assert(!ChatOverlay.GetChatInputFocusedForTest(), "IME cancellation restored focus.");
        });
        Case("Escape cancels Enter even if focus returns before the next poll", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("cancel");
            NativeKey(KeyCode.Return); NativeKey(KeyCode.Escape, advanceFrame: false);
            ChatOverlay.GetChatInputFieldForTest().ActivateInputField(); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Escape-canceled Enter survived rapid refocus.");
        });
        Case("outside click cancels Enter before the EventSystem tick can submit", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("cancel on click");
            NativeKey(KeyCode.Return); ChatOverlay.ClickOutsideForTest(); ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _) && !ChatOverlay.GetChatInputFocusedForTest(), "Outside click submitted before deselection.");
            Assert(ChatOverlay.GetDraft() == "cancel on click", "Outside click cleared committed draft.");
        });
        Case("mouse refocus before polling cannot resurrect an unfocused Enter", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("rapid refocus"); NativeKey(KeyCode.Return);
            ChatOverlay.GetChatInputFieldForTest().DeactivateInputField();
            ChatOverlay.ClickInputForTest(1f); ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.GetChatInputFocusedForTest() && !ChatOverlay.TryDequeueOutgoing(out _), "Immediate mouse refocus resurrected canceled Enter.");
            Assert(ChatOverlay.GetDraft() == "rapid refocus", "Refocus lost the retained draft.");
        });
        Case("explicit send replaces pending Enter and survives loss of focus", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("explicit"); NativeKey(KeyCode.Return);
            ChatOverlay.ClickSendForTest(); ChatOverlay.GetChatInputFieldForTest().DeactivateInputField();
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "explicit", "Focus-loss cancellation removed an explicit send.");
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Explicit replacement sent twice.");
        });
        Case("explicit IME send survives focus loss and waits for committed text", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("한"); Input.compositionString = "글";
            ChatOverlay.ClickSendForTest(); ChatOverlay.GetChatInputFieldForTest().DeactivateInputField();
            ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Explicit send used an unfinished IME draft.");
            Input.compositionString = string.Empty; ChatOverlay.SetDraft("한글");
            ChatOverlay.TickInputForTest();
            Assert(!ChatOverlay.TryDequeueOutgoing(out _), "Explicit send raced the IME commit frame.");
            ChatOverlay.TickInputForTest();
            Assert(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "Unfocused explicit send lost its committed Korean text.");
        });
        Case("input click forwards caret position and retains game input masking", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("abcdefghij");
            var field = ChatOverlay.GetChatInputFieldForTest(); ChatOverlay.ClickInputForTest(3f);
            Assert(field.PointerDownCount == 1 && field.selectionAnchorPosition == 3 && field.selectionFocusPosition == 3, "Click only focused the field instead of forwarding its caret event.");
            Assert(field.PointerReadScopeObserved, "Own pointer callback could not read Shift/editing keys.");
            Assert(ChatOverlay.ShouldBlockGameKeyboardInput() && ChatOverlay.ShouldBlockRawMouseInput(), "Forwarding own pointer events exposed input to the game.");
            Assert(!ChatOverlay.IsReadingOverlayInputForTest(), "Pointer callback leaked its read scope.");
            ChatOverlay.RefreshInputPointerForTest();
            Assert(ChatOverlay.HasInputPointerForTest(), "Same-frame ResetInputAxes canceled the captured pointer.");
        });
        Case("input drag selects text outside the window and releases game mouse capture", () =>
        {
            ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("abcdefghij");
            var field = ChatOverlay.GetChatInputFieldForTest(); ChatOverlay.ClickInputForTest(2f);
            ChatOverlay.DragInputForTest(4f);
            Assert(field.BeginDragCount == 0 && field.selectionFocusPosition == 2, "Sub-threshold movement changed selection.");
            ChatOverlay.DragInputForTest(9f);
            Assert(field.BeginDragCount == 1 && field.DragCount == 1 && field.selectionAnchorPosition == 2 && field.selectionFocusPosition == 9, "Drag did not forward text selection to InputField.");
            Assert(ChatOverlay.ShouldBlockRawMouseInput() && !Input.BlockedMouseReadObserved, "Captured drag either leaked to the game or failed to read physical input.");
            ChatOverlay.DragInputForTest(40f);
            Assert(field.selectionFocusPosition == 10 && ChatOverlay.ShouldBlockRawMouseInput(), "Dragging beyond the input/window dropped its pointer capture.");
            ChatOverlay.DragInputForTest(40f, held: false);
            Assert(field.EndDragCount == 1 && field.PointerUpCount == 1 && !ChatOverlay.HasInputPointerForTest(), "Release retained the native drag or pointer capture.");
            Assert(!ChatOverlay.ShouldBlockRawMouseInput() && ChatOverlay.ShouldBlockGameKeyboardInput(), "Release failed to restore mouse or maintain focused keyboard capture.");
        });
        Case("closing or losing focus ends an input drag exactly once", () =>
        {
            foreach (var close in new[] { false, true })
            {
                ChatOverlay.ResetHarness(); ChatOverlay.SetDraft("abcdefghij");
                var field = ChatOverlay.GetChatInputFieldForTest(); ChatOverlay.ClickInputForTest(1f); ChatOverlay.DragInputForTest(9f);
                if (close) ChatOverlay.CloseChatWindowForTest(); else field.DeactivateInputField();
                ChatOverlay.TickInputForTest(); ChatOverlay.TickInputForTest();
                Assert(field.EndDragCount == 1 && field.PointerUpCount == 1 && !ChatOverlay.HasInputPointerForTest(), "Close/focus loss retained or ended the drag twice.");
                Assert(!ChatOverlay.IsReadingOverlayInputForTest(), "Drag cleanup leaked its read scope.");
            }
        });
        Case("pointer callback exceptions release capture and the field read scope", () =>
        {
            ChatOverlay.ResetHarness(); var field = ChatOverlay.GetChatInputFieldForTest(); field.ThrowOnPointerDown = true;
            var threw = false;
            try { ChatOverlay.ClickInputForTest(1f); } catch (InvalidOperationException) { threw = true; }
            Assert(threw && !ChatOverlay.HasInputPointerForTest(), "Failed pointer callback retained capture.");
            Assert(!ChatOverlay.IsReadingOverlayInputForTest() && ChatOverlay.ShouldBlockGameKeyboardInput(), "Failed pointer callback leaked keyboard input to the game.");
        });
        Console.WriteLine($"{_passed} keyboard regression checks passed ({_assertions} assertions).");
    }

    private static Event KeyEvent(KeyCode key) => new() { keyCode = key, rawType = EventType.KeyDown };
    private static bool FairyGuiWouldReceiveKey(Event keyEvent) =>
        keyEvent.rawType == EventType.KeyDown || keyEvent.rawType == EventType.KeyUp;
    private static Event? CurrentGuiEvent(Event? original)
    {
        var arguments = new object?[] { original };
        Method("GameGuiCurrentEventPostfix").Invoke(null, arguments);
        return arguments[0] as Event;
    }
    private static void NativeKey(KeyCode key, bool advanceFrame = true)
    {
        if (advanceFrame) Time.frameCount++;
        var field = ChatOverlay.GetChatInputFieldForTest();
        var args = new object?[] { field, KeyEvent(key), InputField.EditState.Finish };
        var runOriginal = (bool)Method("ChatInputKeyPressedPrefix").Invoke(null, args)!;
        // Model the stock InputField's Finish -> Deactivate branch. The real
        // patch must keep this branch from running for chat Enter/IME commits.
        if (runOriginal || (InputField.EditState)args[2]! == InputField.EditState.Finish)
            field.DeactivateInputField();
    }
    private static MethodInfo Method(string name) => typeof(PluginEntryPoint).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static (bool RunOriginal, object? Value) InvokePrefix(string methodName, object value)
    {
        var args = new[] { value };
        return ((bool)Method(methodName).Invoke(null, args)!, args[0]);
    }
    private static void Case(string name, Action run)
    {
        run(); _passed++; Console.WriteLine("PASS keyboard: " + name);
    }
    private static void Assert(bool condition, string message)
    {
        _assertions++; if (!condition) throw new InvalidOperationException(message);
    }
}

// The game-reference build checks API signatures, but native patch registration
// still needs an in-game check. These checks execute the real prefix behavior
// without Harmony or Unity binaries in the public CI runner.
namespace HarmonyLib
{
    public sealed class HarmonyMethod
    {
        public MethodInfo Method { get; }
        public HarmonyMethod(MethodInfo method) => Method = method;
    }
    public sealed class Harmony
    {
        public string? FailMethodName { get; set; }
        public List<(MethodInfo Method, HarmonyMethod? Prefix, HarmonyMethod? Postfix)> Patches { get; } = new();
        public void Patch(MethodInfo method, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
        {
            if (method.Name == FailMethodName) throw new InvalidOperationException("Simulated missing native route.");
            Patches.Add((method, prefix, postfix));
        }
    }
}
#endif
