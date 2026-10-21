using System.Reflection;
using AstralPartyChatPlugin;
using UnityEngine;
using PluginEntry = AstralPartyChatPlugin.AstralPartyChatPlugin;

internal static class KeyboardInputTests
{
    internal static void Run()
    {
        Check.Run("keyboard guards require an open focused input and allow nested chat reads", GuardScope);
        Check.Run("held modifiers pass through while other held keys are masked", ModifierPrecedence);
        Check.Run("bool axis string and game submit masks restore after blur or close", MasksRestore);
        Check.Run("GUI current event observes only Enter KeyDown without mutating events", ObserveOnlyEnter);
        Check.Run("opening focuses next frame and closing blurs and stops masking", OpenCloseFocus);
        Check.Run("plain and keypad Enter survive FairyGUI blur and refocus after one send", EnterSurvivesItsBlur);
        Check.Run("raw and GUI reports of one Enter never duplicate a send", DuplicateEnterReports);
        Check.Run("IME Enter waits for committed text and sends once", ImeEnter);
        Check.Run("IME Enter survives native blur through commit and refocuses once", ImeEnterSurvivesBlur);
        Check.Run("refocusing then leaving or clicking outside cancels pending keyboard Enter", IndependentEnterCancellation);
        Check.Run("outside right and middle clicks cancel already-blurred IME Enter", NonPrimaryEnterCancellation);
        Check.Run("IME Enter waits across multiple composition frames", SlowImeCommit);
        Check.Run("composition ending on the Enter frame waits for settled text", CommitFrameEnter);
        Check.Run("unrelated focus loss cancels pending Enter without stealing focus", UnrelatedFocusLoss);
        Check.Run("explicit Send survives focus loss and replaces pending Enter", ExplicitSendSurvivesBlur);
        Check.Run("closing cancels a pending Enter and does not send after reopening", CloseCancelsEnter);
        Check.Run("idle unfocused input avoids native text reads", IdleInputTextReads);
    }

    private static MethodInfo Method(string name) => typeof(PluginEntry).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing keyboard patch " + name);
    private static (bool RunOriginal, object Value) Prefix(string name, object value)
    {
        var args = new[] { value };
        var run = (bool)Method(name).Invoke(null, args)!;
        return (run, args[0]);
    }
    private static (bool RunOriginal, bool Value) HeldPrefix(KeyCode key)
    {
        object[] args = { key, true };
        var run = (bool)Method("InputKeyHeldPrefix").Invoke(null, args)!;
        return (run, (bool)args[1]);
    }
    private static void Observe(Event? value) => Method("GuiCurrentEventPostfix").Invoke(null, new object?[] { value });

    private static void GuardScope()
    {
        ChatOverlay.ResetHarness(open: false);
        Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "closed chat allows game keys");
        ChatOverlay.Click("Button");
        Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "opening has not yet focused input");
        ChatOverlay.Tick();
        Check.True(ChatOverlay.ShouldBlockGameKeyboardInput(), "open focused chat blocks keys");
        ChatOverlay.EnterChatInputRead();
        ChatOverlay.EnterChatInputRead();
        try
        {
            Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "nested chat read bypasses mask");
            Check.True(Prefix("InputKeyboardBoolPrefix", true).RunOriginal, "own read runs original");
            ChatOverlay.ExitChatInputRead();
            Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "outer scope still active");
        }
        finally { ChatOverlay.ExitChatInputRead(); }
        Check.True(ChatOverlay.ShouldBlockGameKeyboardInput(), "scope restores mask");
        ChatOverlay.Node("Input").Blur();
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "open but unfocused does not block");
    }

    private static void ModifierPrecedence()
    {
        ChatOverlay.ResetHarness();
        foreach (var key in new[] { KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftCommand, KeyCode.RightCommand })
        {
            var result = HeldPrefix(key);
            Check.True(result.RunOriginal && result.Value, "modifier available to FairyGUI editing: " + key);
        }
        foreach (var key in new[] { KeyCode.A, KeyCode.Space, KeyCode.Return, KeyCode.Escape })
        {
            var result = HeldPrefix(key);
            Check.True(!result.RunOriginal && !result.Value, "nonmodifier masked: " + key);
        }
        ChatOverlay.Node("Input").Blur();
        ChatOverlay.Tick();
        Check.True(HeldPrefix(KeyCode.A).RunOriginal, "other keys restored after blur");
    }

    private static void AssertMasks(bool blocking)
    {
        var boolean = Prefix("InputKeyboardBoolPrefix", true);
        Check.Equal(!blocking, boolean.RunOriginal, "boolean original execution");
        Check.Equal(!blocking, (bool)boolean.Value, "boolean masked value");
        var axis = Prefix("InputKeyboardAxisPrefix", 0.75f);
        Check.Equal(!blocking, axis.RunOriginal, "axis original execution");
        Check.Near(blocking ? 0 : 0.75f, (float)axis.Value, "axis masked value");
        var text = Prefix("InputKeyboardStringPrefix", "typed");
        Check.Equal(!blocking, text.RunOriginal, "string original execution");
        Check.Equal(blocking ? string.Empty : "typed", (string)text.Value, "string masked value");
        var submit = Prefix("GameSubmitEventPrefix", true);
        Check.Equal(!blocking, submit.RunOriginal, "game submit original execution");
        Check.Equal(!blocking, (bool)submit.Value, "game submit masked value");
    }

    private static void MasksRestore()
    {
        foreach (var close in new[] { false, true })
        {
            ChatOverlay.ResetHarness();
            AssertMasks(blocking: true);
            if (close) ChatOverlay.Click("Close");
            else { ChatOverlay.Node("Input").Blur(); ChatOverlay.Tick(); }
            AssertMasks(blocking: false);
        }
    }

    private static void ObserveOnlyEnter()
    {
        foreach (var key in new[] { KeyCode.Return, KeyCode.KeypadEnter, KeyCode.A, KeyCode.Escape })
        foreach (var kind in new[] { EventType.KeyDown, EventType.KeyUp, EventType.Layout, EventType.MouseDown })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft("observed");
            var value = new Event { rawType = kind, type = kind, keyCode = key, character = 'x', modifiers = EventModifiers.Shift };
            Observe(value);
            Check.Equal(kind, value.rawType, "raw event type unchanged");
            Check.Equal(kind, value.type, "event type unchanged");
            Check.Equal(key, value.keyCode, "key unchanged");
            Check.Equal('x', value.character, "character unchanged");
            Check.Equal(EventModifiers.Shift, value.modifiers, "modifiers unchanged");
            ChatOverlay.Tick();
            var expected = kind == EventType.KeyDown && key is KeyCode.Return or KeyCode.KeypadEnter;
            Check.Equal(expected, ChatOverlay.TryDequeueOutgoing(out var sent), "only Enter KeyDown observed");
            if (expected) Check.Equal("observed", sent, "observed Enter sends draft");
        }
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("not focused");
        ChatOverlay.Node("Input").Blur();
        ChatOverlay.Tick();
        Observe(new Event { rawType = EventType.KeyDown, keyCode = KeyCode.Return });
        Observe(null);
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "unfocused GUI Enter ignored");
    }

    private static void OpenCloseFocus()
    {
        ChatOverlay.ResetHarness(open: false);
        ChatOverlay.Click("Button");
        Check.True(ChatOverlay.WindowOpen && !ChatOverlay.Node("Input").Focused, "open defers focus");
        ChatOverlay.Tick();
        Check.True(ChatOverlay.Node("Input").Focused && ChatOverlay.ShouldBlockGameKeyboardInput(), "next frame focuses");
        ChatOverlay.Click("Close");
        Check.True(!ChatOverlay.WindowOpen && !ChatOverlay.Node("Input").Focused, "close blurs hidden input");
        Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "close restores game keys");
        ChatOverlay.Click("Button");
        ChatOverlay.Tick();
        ChatOverlay.TestSnapshot.ChatButtonVisible = false;
        ChatOverlay.TestSnapshot.ChatAvailableOnScreen = false;
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.WindowOpen && !ChatOverlay.Node("Input").Focused, "leaving chat screen closes and blurs");
    }

    private static void EnterSurvivesItsBlur()
    {
        foreach (var key in new[] { KeyCode.Return, KeyCode.KeypadEnter })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft("hello");
            ChatOverlay.Tick(key: key);
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "Enter waits a frame");
            ChatOverlay.Node("Input").Blur();
            ChatOverlay.Tick();
            Check.True(ChatOverlay.TryDequeueOutgoing(out var text), "Enter-associated FairyGUI blur does not cancel");
            Check.Equal("hello", text, "draft sent");
            Check.True(ChatOverlay.Node("Input").Focused, "submit refocuses input");
            ChatOverlay.Tick();
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "Enter sends once");
        }
    }

    private static void DuplicateEnterReports()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("one press");
        ChatOverlay.Tick(key: KeyCode.Return);
        Observe(new Event { rawType = EventType.KeyDown, keyCode = KeyCode.Return });
        Observe(new Event { rawType = EventType.KeyDown, keyCode = KeyCode.Return });
        ChatOverlay.Tick();
        Check.True(ChatOverlay.TryDequeueOutgoing(out var text) && text == "one press", "combined Enter sends");
        ChatOverlay.SetDraft("next draft");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "same press cannot send new draft");
    }

    private static void ImeEnter()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("한");
        Input.compositionString = "글";
        Observe(new Event { rawType = EventType.KeyDown, keyCode = KeyCode.Return });
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.GetDraft() == "한", "IME composition not submitted or rewritten");
        Input.compositionString = string.Empty;
        ChatOverlay.SetDraft("한글");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "commit frame waits");
        ChatOverlay.Tick();
        Check.True(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "committed Korean sent");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "IME Enter sends once");
    }

    private static void ImeEnterSurvivesBlur()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("한");
        Input.compositionString = "글";
        ChatOverlay.Tick(key: KeyCode.Return);
        ChatOverlay.Node("Input").Blur();
        ChatOverlay.SetDraft("한글");
        Input.compositionString = string.Empty;
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "native blur and commit frame wait");
        ChatOverlay.Tick();
        Check.True(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "committed draft sends after native blur");
        Check.True(ChatOverlay.Node("Input").Focused, "IME submit refocuses");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "IME blur sends exactly once");
    }

    private static void IndependentEnterCancellation()
    {
        foreach (var refocus in new[] { false, true })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft("한");
            Input.compositionString = "글";
            ChatOverlay.Tick(key: KeyCode.Return);
            ChatOverlay.Node("Input").Blur();
            ChatOverlay.Tick();
            if (refocus)
            {
                ChatOverlay.Node("Input").Focus();
                ChatOverlay.Tick();
                ChatOverlay.Node("Input").Blur();
                ChatOverlay.Tick();
            }
            else
                ChatOverlay.Tick(pressed: true);
            Input.compositionString = string.Empty;
            ChatOverlay.SetDraft("한글");
            ChatOverlay.Tick();
            ChatOverlay.Tick();
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "independent departure cancels pending Enter");
            Check.Equal("한글", ChatOverlay.GetDraft(), "cancelled IME draft retained");
            Check.True(!ChatOverlay.Node("Input").Focused, "cancelled submit does not refocus");
        }
    }

    private static void NonPrimaryEnterCancellation()
    {
        foreach (var button in new[] { 1, 2 })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetDraft("한");
            Input.compositionString = "글";
            ChatOverlay.Tick(key: KeyCode.Return);
            var input = ChatOverlay.Node("Input");
            input.Blur();
            ChatOverlay.Tick();
            ChatOverlay.Tick(pressed: true, mouseButton: button);
            Input.compositionString = string.Empty;
            ChatOverlay.SetDraft("한글");
            ChatOverlay.Tick();
            ChatOverlay.Tick();
            Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "outside click cancels pending Enter for button " + button);
            Check.Equal("한글", ChatOverlay.GetDraft(), "canceled committed draft retained");
            Check.True(!input.Focused, "outside click does not steal focus back");
        }
    }

    private static void IdleInputTextReads()
    {
        ChatOverlay.ResetHarness();
        var input = ChatOverlay.Node("Input");
        input.Blur();
        ChatOverlay.Tick();
        var reads = input.TextReads;
        for (var frame = 0; frame < 10; frame++)
            ChatOverlay.Tick();
        Check.Equal(reads, input.TextReads, "idle unfocused frames do not marshal draft text");
        ChatOverlay.SetChatStatus("새 상태");
        Check.Equal(reads + 1, input.TextReads, "status change revalidates the draft");
        ChatOverlay.SetChatStatus("새 상태");
        Check.Equal(reads + 1, input.TextReads, "unchanged status avoids another read");
        input.Focus();
        ChatOverlay.Tick();
        Check.Equal(reads + 2, input.TextReads, "focused input samples committed text");
    }

    private static void SlowImeCommit()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("안녕");
        Input.compositionString = "가";
        ChatOverlay.Tick(key: KeyCode.Return);
        for (var i = 0; i < 8; i++) ChatOverlay.Tick();
        Check.Equal("안녕", ChatOverlay.GetDraft(), "pending Enter preserves draft");
        Check.Equal("가", Input.compositionString, "composition untouched");
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "slow composition not sent");
        Input.compositionString = string.Empty;
        ChatOverlay.SetDraft("안녕가");
        ChatOverlay.Tick();
        ChatOverlay.Tick();
        Check.True(ChatOverlay.TryDequeueOutgoing(out var text) && text == "안녕가", "slow composition sends without second Enter");
    }

    private static void CommitFrameEnter()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("한");
        Input.compositionString = "글";
        ChatOverlay.Tick();
        Input.compositionString = string.Empty;
        ChatOverlay.SetDraft("한글");
        ChatOverlay.Tick(key: KeyCode.Return);
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "same-frame commit waits");
        ChatOverlay.Tick();
        Check.True(ChatOverlay.TryDequeueOutgoing(out var text) && text == "한글", "commit frame Enter retained");
    }

    private static void UnrelatedFocusLoss()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("한");
        Input.compositionString = "글";
        ChatOverlay.Tick(key: KeyCode.Return);
        ChatOverlay.Tick();
        ChatOverlay.Node("Input").Blur();
        ChatOverlay.Tick();
        Input.compositionString = string.Empty;
        ChatOverlay.SetDraft("한글");
        ChatOverlay.Tick();
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "unrelated focus loss cancels pending Enter");
        Check.Equal("한글", ChatOverlay.GetDraft(), "canceled draft retained");
        Check.True(!ChatOverlay.Node("Input").Focused && !ChatOverlay.ShouldBlockGameKeyboardInput(), "canceled Enter does not steal focus");
        ChatOverlay.Node("Input").Focus();
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "refocus cannot resurrect Enter");
    }

    private static void ExplicitSendSurvivesBlur()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("explicit");
        Input.compositionString = "pending";
        ChatOverlay.Tick(key: KeyCode.Return);
        ChatOverlay.Click("Send");
        ChatOverlay.Node("Input").Blur();
        ChatOverlay.Tick();
        ChatOverlay.Tick(pressed: true);
        ChatOverlay.Tick(pressed: true, mouseButton: 1);
        ChatOverlay.Tick(pressed: true, mouseButton: 2);
        Input.compositionString = string.Empty;
        ChatOverlay.SetDraft("explicit final");
        ChatOverlay.Tick();
        ChatOverlay.Tick();
        Check.True(ChatOverlay.TryDequeueOutgoing(out var text) && text == "explicit final", "explicit send survives unrelated blur and outside click");
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _) && ChatOverlay.Node("Input").Focused, "send once and refocus");
    }

    private static void CloseCancelsEnter()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetDraft("pending");
        ChatOverlay.Tick(key: KeyCode.Return);
        ChatOverlay.Click("Close");
        ChatOverlay.Tick();
        ChatOverlay.Click("Button");
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.TryDequeueOutgoing(out _), "closed pending Enter never sends");
        Check.Equal("pending", ChatOverlay.GetDraft(), "close retains draft");
    }
}
