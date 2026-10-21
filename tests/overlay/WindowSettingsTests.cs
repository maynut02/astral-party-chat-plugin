using AstralPartyChatPlugin;
using UnityEngine;

internal static class WindowSettingsTests
{
    private const string Prefix = "AstralPartyChat.Window.v4.";
    internal static void Run()
    {
        Check.Run("header drag clamps to root bounds and persists v4 geometry on release", MoveAndPersist);
        Check.Run("resize grip enforces minimum size and root bounds", ResizeBounds);
        Check.Run("resize release remeasures retained rows at the new width", ResizeRemeasures);
        Check.Run("screen closure during resize reflows rows before reopening", InterruptedResizeRemeasures);
        Check.Run("loading v4 geometry deletes all incompatible legacy keys", LoadAndMigrate);
        Check.Run("reset layout restores and persists default geometry", ResetLayout);
        Check.Run("text scale steps clamp at 80 and 150 percent and rebuild rows", TextScaleSteps);
        Check.Run("opacity steps clamp at 30 and 100 percent and persist", OpacitySteps);
        Check.Run("button hosting reparents orders hides and rebuilds disposed buttons", ButtonHosting);
        Check.Run("shutdown releases overlay nodes but preserves game owned hosts", ShutdownOwnership);
        Check.Run("repeated game host disposal releases every button subtree handle", ButtonRebuildHandles);
        Check.Run("shutdown resets scroll state even when native disposal throws", ShutdownDuringScrollDrag);
        Check.Run("hidden play button preserves an open chat window", HiddenButtonKeepsWindow);
        Check.Run("opening applies current root layout before showing the window", OpenAppliesLayout);
        Check.Run("detached chat window rejoins root with its sorting order", DetachedWindow);
        Check.Run("ChatLayout placement capture and movement round trip within bounds", LayoutRoundTrip);
        Check.Run("ChatLayout nonfinite geometry falls back and small roots shrink safely", LayoutFallback);
    }

    private static void AssertInside(ChatWindowRect rect)
    {
        Check.True(rect.X >= 8 && rect.Y >= 8, "window starts inside root margins");
        Check.True(rect.X + rect.Width <= ChatOverlay.TestRoot.Width - 8 + 0.05f, "right edge inside root");
        Check.True(rect.Y + rect.Height <= ChatOverlay.TestRoot.Height - 8 + 0.05f, "bottom edge inside root");
    }
    private static void Drag(string control, Vector2 delta, bool release = true)
    {
        var node = ChatOverlay.Node(control);
        var pointer = ChatOverlay.GlobalPosition(node) + new Vector2(10, 10);
        ChatOverlay.Tick(node, pointer, pressed: true, held: true);
        ChatOverlay.Tick(pointer: pointer + delta, held: true);
        if (release) ChatOverlay.Tick(pointer: pointer + delta);
    }
    private static void Recreate()
    {
        ChatOverlay.Shutdown();
        ChatOverlay.Refresh(ChatOverlay.TestSnapshot, ChatOverlay.TestRoot);
    }

    private static void MoveAndPersist()
    {
        ChatOverlay.ResetHarness();
        var original = ChatOverlay.WindowRect;
        var node = ChatOverlay.Node("Header");
        var pointer = ChatOverlay.GlobalPosition(node) + new Vector2(10, 10);
        ChatOverlay.Tick(node, pointer, pressed: true, held: true);
        ChatOverlay.Tick(pointer: pointer + new Vector2(-5000, -5000), held: true);
        var moved = ChatOverlay.WindowRect;
        Check.Near(8, moved.X, "drag clamps left");
        Check.Near(8, moved.Y, "drag clamps top");
        Check.Near(original.Width, moved.Width, "move does not resize");
        Check.True(!PlayerPrefs.HasKey(Prefix + "Right"), "geometry not persisted mid drag");
        ChatOverlay.Tick(pointer: pointer + new Vector2(-5000, -5000));
        var captured = ChatLayout.Capture(moved, ChatOverlay.TestRoot.Width, ChatOverlay.TestRoot.Height);
        Check.Near(captured.Right, PlayerPrefs.GetFloat(Prefix + "Right"), "right offset saved");
        Check.Near(captured.CenterY, PlayerPrefs.GetFloat(Prefix + "CenterY"), "vertical center saved");
        Check.Near(captured.Width, PlayerPrefs.GetFloat(Prefix + "Width"), "width saved");
        Check.Near(captured.Height, PlayerPrefs.GetFloat(Prefix + "Height"), "height saved");
        Recreate();
        Check.Equal(moved, ChatOverlay.WindowRect, "persisted move survives rebuild");
        ChatOverlay.Click("Button");
        ChatOverlay.Tick();
        Drag("Header", new Vector2(5000, 5000));
        AssertInside(ChatOverlay.WindowRect);
        Check.Near(ChatOverlay.TestRoot.Width - 8 - moved.Width, ChatOverlay.WindowRect.X, "drag clamps right");
        Check.Near(ChatOverlay.TestRoot.Height - 8 - moved.Height, ChatOverlay.WindowRect.Y, "drag clamps bottom");
    }

    private static void ResizeBounds()
    {
        ChatOverlay.ResetHarness();
        Drag("Resize", new Vector2(-5000, -5000));
        Check.Near(420, ChatOverlay.WindowRect.Width, "minimum width");
        Check.Near(480, ChatOverlay.WindowRect.Height, "minimum height");
        AssertInside(ChatOverlay.WindowRect);
        Drag("Resize", new Vector2(5000, 5000));
        AssertInside(ChatOverlay.WindowRect);
        Check.Near(ChatOverlay.TestRoot.Width - 8, ChatOverlay.WindowRect.X + ChatOverlay.WindowRect.Width, "resize clamps right edge");
        Check.Near(ChatOverlay.TestRoot.Height - 8, ChatOverlay.WindowRect.Y + ChatOverlay.WindowRect.Height, "resize clamps bottom edge");
        Check.Near(ChatOverlay.WindowRect.Width, PlayerPrefs.GetFloat(Prefix + "Width"), "resized width persisted");
        Check.Near(ChatOverlay.WindowRect.Height, PlayerPrefs.GetFloat(Prefix + "Height"), "resized height persisted");
    }

    private static void ResizeRemeasures()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(new[] { OverlayTests.Message(0, new string('가', 700)) });
        var old = ChatOverlay.InspectRows().Single();
        var node = ChatOverlay.Node("Resize");
        var pointer = ChatOverlay.GlobalPosition(node) + new Vector2(10, 10);
        ChatOverlay.Tick(node, pointer, pressed: true, held: true);
        ChatOverlay.Tick(pointer: pointer + new Vector2(-140, 0), held: true);
        Check.Near(old.Width, ChatOverlay.InspectRows().Single().Width, "live resize clips without repeated row measurement");
        ChatOverlay.Tick(pointer: pointer + new Vector2(-140, 0));
        var next = ChatOverlay.InspectRows().Single();
        Check.True(ReferenceEquals(old.Root, next.Root), "width change retains row identity");
        Check.Near(ChatOverlay.ViewportWidth, next.Width, "release uses new viewport width");
        Check.Near(next.Width - 72, next.Body.Width, "body uses resized width");
        Check.True(next.Height > old.Height, "narrower body rewrapped and remeasured");
        Check.Near(next.Body.Y + next.Body.Height + 12, next.Height, "resized row fits measured text");
    }

    private static void InterruptedResizeRemeasures()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatStatus("연결됨");
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 12)
            .Select(index => OverlayTests.Message(index, new string('가', 160))));
        var oldRows = ChatOverlay.InspectRows();
        var oldContentHeight = ChatOverlay.ContentHeight;
        var node = ChatOverlay.Node("Resize");
        var pointer = ChatOverlay.GlobalPosition(node) + new Vector2(10, 10);
        ChatOverlay.Tick(node, pointer, pressed: true, held: true);
        ChatOverlay.Tick(pointer: pointer + new Vector2(-140, 0), held: true);
        ChatOverlay.TestSnapshot.ChatAvailableOnScreen = false;
        ChatOverlay.Tick(held: true);
        Check.True(!ChatOverlay.WindowOpen, "unsupported screen interrupts resize");
        ChatOverlay.TestSnapshot.ChatAvailableOnScreen = true;
        ChatOverlay.Click("Button");

        var rows = ChatOverlay.InspectRows();
        Check.True(ChatOverlay.WindowOpen, "chat reopened");
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            Check.Near(ChatOverlay.ViewportWidth, row.Width, "interrupted resize remeasures row width");
            Check.Near(row.Width - 72, row.Body.Width, "body fits the narrowed viewport");
            Check.True(row.Height > oldRows[index].Height, "narrower text reflows before display");
            var intersects = row.Root.Y + row.Height >= ChatOverlay.ScrollTop
                && row.Root.Y <= ChatOverlay.ScrollTop + ChatOverlay.ViewportHeight;
            Check.Equal(intersects, row.Root.Visible, "culling uses reflowed row geometry");
        }
        Check.True(ChatOverlay.ContentHeight > oldContentHeight, "content height includes reflowed text");
        Check.Near(ChatOverlay.Scrollable, ChatOverlay.ScrollTop, "latest view follows reflowed bottom");
        Check.Equal(0, ChatOverlay.Unread, "resize does not create unread messages");
    }

    private static void LoadAndMigrate()
    {
        ChatOverlay.ResetHarness(open: false);
        ChatOverlay.Shutdown();
        var legacy = new[] { "WindowX", "WindowY", "WindowX.v2", "WindowY.v2", "WindowX.v3", "WindowY.v3", "WindowScale" };
        foreach (var key in legacy) PlayerPrefs.SetFloat("AstralPartyChat." + key, 17);
        var geometry = new ChatWindowGeometry(600, 650, 180, -40);
        PlayerPrefs.SetFloat(Prefix + "Width", geometry.Width);
        PlayerPrefs.SetFloat(Prefix + "Height", geometry.Height);
        PlayerPrefs.SetFloat(Prefix + "Right", geometry.Right);
        PlayerPrefs.SetFloat(Prefix + "CenterY", geometry.CenterY);
        ChatOverlay.Refresh(ChatOverlay.TestSnapshot, ChatOverlay.TestRoot);
        Check.Equal(ChatLayout.Place(geometry, 1920, 1080), ChatOverlay.WindowRect, "v4 geometry loaded");
        Check.True(legacy.All(key => !PlayerPrefs.HasKey("AstralPartyChat." + key)), "all old coordinate keys removed");
        ChatOverlay.Shutdown();
        PlayerPrefs.SetFloat(Prefix + "Width", float.NaN);
        ChatOverlay.Refresh(ChatOverlay.TestSnapshot, ChatOverlay.TestRoot);
        Check.Equal(ChatLayout.Place(ChatWindowGeometry.Default, 1920, 1080), ChatOverlay.WindowRect, "invalid persisted geometry falls back as a unit");
    }

    private static void ResetLayout()
    {
        ChatOverlay.ResetHarness();
        Drag("Resize", new Vector2(-140, -220));
        Drag("Header", new Vector2(-400, -100));
        ChatOverlay.Click("Settings");
        ChatOverlay.Click("ResetLayout");
        var expected = ChatLayout.Place(ChatWindowGeometry.Default, 1920, 1080);
        Check.Equal(expected, ChatOverlay.WindowRect, "default rect restored");
        Recreate();
        Check.Equal(expected, ChatOverlay.WindowRect, "default rect persisted");
    }

    private static void TextScaleSteps()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(new[] { OverlayTests.Message(0, new string('가', 200)), new ChatUiMessage { Id = "system", IsSystem = true, Text = "system" } });
        ChatOverlay.Click("Settings");
        var old = ChatOverlay.InspectRows();
        ChatOverlay.Click("TextLarger");
        Check.Near(1.1f, ChatOverlay.TextScale, "one step is ten percent");
        var larger = ChatOverlay.InspectRows();
        Check.True(old.All(row => row.Root.IsDisposed), "font scale rebuilds old rows");
        Check.Equal(23, larger[0].Body.FontSize, "player body font scaled");
        Check.Equal(20, larger[1].Body.FontSize, "system font scaled");
        for (var i = 0; i < 10; i++) ChatOverlay.Click("TextLarger");
        Check.Near(1.5f, ChatOverlay.TextScale, "upper scale bound");
        Check.Equal(32, ChatOverlay.InspectRows()[0].Body.FontSize, "upper body font size");
        var capped = ChatOverlay.InspectRows()[0].Root;
        ChatOverlay.Click("TextLarger");
        Check.True(ReferenceEquals(capped, ChatOverlay.InspectRows()[0].Root), "clamped click does not rebuild rows");
        for (var i = 0; i < 20; i++) ChatOverlay.Click("TextSmaller");
        Check.Near(0.8f, ChatOverlay.TextScale, "lower scale bound");
        Check.Equal(17, ChatOverlay.InspectRows()[0].Body.FontSize, "lower body font size");
        Check.Near(0.8f, PlayerPrefs.GetFloat("AstralPartyChat.TextScale"), "text scale persisted");
        Recreate();
        Check.Near(0.8f, ChatOverlay.TextScale, "text scale reloaded");
    }

    private static void OpacitySteps()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.Click("Settings");
        ChatOverlay.Click("OpacityDown");
        Check.Near(0.9f, ChatOverlay.WindowOpacity, "one opacity step");
        for (var i = 0; i < 20; i++) ChatOverlay.Click("OpacityDown");
        Check.Near(0.3f, ChatOverlay.WindowOpacity, "lower opacity bound");
        Check.Near(252f / 255 * 0.3f, ChatOverlay.WindowBackground.FillColor.a, "background alpha follows opacity");
        Check.Near(0.3f, PlayerPrefs.GetFloat("AstralPartyChat.Opacity"), "opacity persisted");
        Recreate();
        Check.Near(0.3f, ChatOverlay.WindowOpacity, "opacity reloaded");
        ChatOverlay.Click("Button");
        ChatOverlay.Tick();
        ChatOverlay.Click("Settings");
        for (var i = 0; i < 20; i++) ChatOverlay.Click("OpacityUp");
        Check.Near(1, ChatOverlay.WindowOpacity, "upper opacity bound");
        Check.Near(1, PlayerPrefs.GetFloat("AstralPartyChat.Opacity"), "upper opacity persisted");
    }

    private static void ButtonHosting()
    {
        ChatOverlay.ResetHarness(open: false);
        var root = ChatOverlay.TestRoot;
        var button = ChatOverlay.Node("Button");
        Check.True(button.IsChildOf(root) && button.SortingOrder > 1000, "root-hosted button above game panels");
        var host = FguiNode.Component(800, 600);
        root.AddChild(host);
        ChatOverlay.TestSnapshot.ChatButtonHost = host;
        ChatOverlay.TestSnapshot.ChatButtonPosition = new Vector2(33, 44);
        ChatOverlay.TestSnapshot.ChatButtonSize = 108;
        ChatOverlay.Tick();
        Check.True(ReferenceEquals(button, ChatOverlay.Node("Button")) && button.IsChildOf(host), "button reparents without rebuilding");
        Check.Equal(0, button.SortingOrder, "button respects panel sorting");
        Check.Near(33, button.X, "host-relative x");
        Check.Near(44, button.Y, "host-relative y");
        Check.Near(1.5f, button.Scale, "snapshot size applied");
        ChatOverlay.TestSnapshot.ChatButtonVisible = false;
        ChatOverlay.Tick();
        Check.True(!button.Visible, "button hidden when unavailable");
        ChatOverlay.TestSnapshot.ChatButtonVisible = true;
        ChatOverlay.TestSnapshot.ChatButtonHost = null;
        ChatOverlay.Tick();
        Check.True(!button.Visible, "button hidden without host");
        ChatOverlay.TestSnapshot.ChatButtonHost = host;
        ChatOverlay.Tick();
        host.Dispose();
        Check.True(button.IsDisposed, "game panel disposal reaches hosted button");
        ChatOverlay.TestSnapshot.ChatButtonHost = root;
        ChatOverlay.Tick();
        var rebuilt = ChatOverlay.Node("Button");
        Check.True(!ReferenceEquals(button, rebuilt) && !rebuilt.IsDisposed && rebuilt.Visible, "disposed button rebuilt");
        Check.True(rebuilt.IsChildOf(root) && rebuilt.SortingOrder > 1000, "rebuilt root-hosted order restored");
        Check.True(button.HandleReleased, "disposed button handle released");
        // FairyGUI normally reports a visual child, not the registered component.
        ChatOverlay.Tick(rebuilt.Children.Last(), pressed: true);
        Check.True(ChatOverlay.WindowOpen, "hit traversal dispatches child to owning button");
    }

    private static void ShutdownOwnership()
    {
        ChatOverlay.ResetHarness();
        var root = ChatOverlay.TestRoot;
        var host = FguiNode.Component(800, 600);
        root.AddChild(host);
        ChatOverlay.TestSnapshot.ChatButtonHost = host;
        ChatOverlay.Tick();
        ChatOverlay.SetChatMessages(new[] { OverlayTests.Message(0) });
        var window = ChatOverlay.Node("Window");
        var button = ChatOverlay.Node("Button");
        var row = ChatOverlay.InspectRows().Single();
        ChatOverlay.Shutdown();
        Check.True(window.IsDisposed && button.IsDisposed && row.Root.IsDisposed, "owned window button and row disposed");
        Check.True(row.Texts.All(text => text.HandleReleased), "row handles released");
        Check.True(!host.IsDisposed && !root.IsDisposed, "borrowed game hosts preserved");
        Check.True(!ChatOverlay.ShouldBlockGameKeyboardInput(), "shutdown stops game key masking");
    }

    private static void ButtonRebuildHandles()
    {
        ChatOverlay.ResetHarness(open: false);
        var root = ChatOverlay.TestRoot;
        var baseline = FguiNode.LiveHandles;
        for (var rebuild = 0; rebuild < 20; rebuild++)
        {
            var host = FguiNode.Component(800f, 600f);
            root.AddChild(host);
            ChatOverlay.TestSnapshot.ChatButtonHost = host;
            ChatOverlay.Tick(ChatOverlay.Node("Button"));
            var old = ChatOverlay.Node("Button");
            var children = old.Children.ToArray();
            host.Dispose();
            Check.True(children.All(child => child.IsDisposed && !child.HandleReleased),
                "native parent disposal leaves CLR child handles observable");
            ChatOverlay.TestSnapshot.ChatButtonHost = root;
            ChatOverlay.Tick();
            Check.True(old.HandleReleased && children.All(child => child.HandleReleased), "old subtree handles all released");
            Check.Equal(baseline, FguiNode.LiveHandles, "rebuilding button does not grow live handles");
        }
    }

    private static void ShutdownDuringScrollDrag()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.SetChatMessages(Enumerable.Range(0, 30).Select(index => OverlayTests.Message(index)));
        var track = ChatOverlay.Node("ScrollTrack");
        ChatOverlay.Tick(track, ChatOverlay.GlobalPosition(track) + new Vector2(6f, track.Height * 0.5f),
            pressed: true, held: true);
        Check.True(ChatOverlay.ScrollbarDragging, "scrollbar drag active before teardown");
        var window = ChatOverlay.Node("Window");
        window.ThrowAfterDispose = true;
        ChatOverlay.Shutdown();
        Check.Equal(1, FguiNode.LiveHandles, "teardown releases every overlay handle despite native exception");
        Check.True(!ChatOverlay.ScrollbarDragging && !ChatOverlay.ViewportDragging, "teardown clears drag state");
        ChatOverlay.Refresh(ChatOverlay.TestSnapshot, ChatOverlay.TestRoot);
        Check.True(!ReferenceEquals(window, ChatOverlay.Node("Window")), "cleared fields permit complete recreation");
    }

    private static void HiddenButtonKeepsWindow()
    {
        ChatOverlay.ResetHarness();
        ChatOverlay.TestSnapshot.ScreenPhase = "플레이";
        ChatOverlay.TestSnapshot.ChatButtonVisible = false;
        ChatOverlay.Tick();
        Check.True(!ChatOverlay.Node("Button").Visible, "transient play column loss hides only the button");
        Check.True(ChatOverlay.WindowOpen && ChatOverlay.Node("Window").Visible, "already-open chat remains visible");
        Check.Equal("플레이", ChatOverlay.TestSnapshot.ScreenPhase, "button hiding does not change play phase");
    }

    private static void OpenAppliesLayout()
    {
        ChatOverlay.ResetHarness(open: false);
        ChatOverlay.SetChatMessages(new[] { OverlayTests.Message(0, new string('가', 600)) });
        var old = ChatOverlay.InspectRows().Single();
        ChatOverlay.TestRoot.SetSize(460f, 600f);
        ChatOverlay.Click("Button");
        Check.True(ChatOverlay.WindowOpen && ChatOverlay.Node("Window").Visible, "window opens");
        AssertInside(ChatOverlay.WindowRect);
        var next = ChatOverlay.InspectRows().Single();
        Check.True(next.Width < old.Width && next.Height > old.Height, "first visible frame uses current root width");
    }

    private static void DetachedWindow()
    {
        ChatOverlay.ResetHarness();
        var window = ChatOverlay.Node("Window");
        var order = window.SortingOrder;
        var temporaryHost = FguiNode.Component(100f, 100f);
        temporaryHost.AddChild(window);
        ChatOverlay.Tick();
        Check.True(window.IsChildOf(ChatOverlay.TestRoot), "live detached window reattached to root");
        Check.Equal(order, window.SortingOrder, "top-level sorting order preserved");
        Check.True(ChatOverlay.WindowOpen && window.Visible, "reattachment preserves open state");
        temporaryHost.Dispose();
    }

    private static void LayoutRoundTrip()
    {
        foreach (var size in new[] { new Vector2(1920, 1080), new Vector2(1280, 720), new Vector2(800, 600) })
        {
            var placed = ChatLayout.Place(new ChatWindowGeometry(900, 900, -500, -500), size.x, size.y);
            Check.True(placed.X >= 8 && placed.Y >= 8 && placed.X + placed.Width <= size.x - 8 && placed.Y + placed.Height <= size.y - 8, "placed rect clamped");
            var captured = ChatLayout.Capture(placed, size.x, size.y);
            Check.Equal(placed, ChatLayout.Place(captured, size.x, size.y), "capture place round trip");
            var moved = ChatLayout.Place(ChatLayout.Move(placed, 10000, 10000, size.x, size.y), size.x, size.y);
            Check.Near(size.x - 8, moved.X + moved.Width, "move clamps right");
            Check.Near(size.y - 8, moved.Y + moved.Height, "move clamps bottom");
        }
    }

    private static void LayoutFallback()
    {
        var defaults = ChatLayout.Place(ChatWindowGeometry.Default, 1920, 1080);
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        foreach (var geometry in new[] { new ChatWindowGeometry(bad, 600, 100, 0), new ChatWindowGeometry(500, bad, 100, 0),
            new ChatWindowGeometry(500, 600, bad, 0), new ChatWindowGeometry(500, 600, 100, bad) })
            Check.Equal(defaults, ChatLayout.Place(geometry, 1920, 1080), "nonfinite geometry falls back");
        var small = ChatLayout.Place(ChatWindowGeometry.Default, 320, 300);
        Check.Equal(new ChatWindowRect(8, 8, 304, 284), small, "root smaller than minimum still contains window");
        Check.Near(1, ChatLayout.ClampTextScale(float.NaN), "invalid text scale default");
        Check.Near(1, ChatLayout.ClampOpacity(float.PositiveInfinity), "invalid opacity default");
    }
}
