using AstralPartyChatPlugin;
using UnityEngine;

internal static class UpdateNoticeTests
{
    private static readonly PluginUpdateState Quiet = new(null, UpdateSeverity.Success, false);
    private static readonly PluginUpdateState Installed = new("v0.3.0 설치 완료 · 게임을 다시 시작하면 적용됩니다", UpdateSeverity.Success, true);
    private static readonly PluginUpdateState Manual = new("새 버전 v0.3.0 출시 · 자동 업데이트가 꺼져 있어 직접 설치해야 합니다", UpdateSeverity.Warning, false);
    private static readonly List<string> Warnings = new();
    private static int _restarts;

    internal static void Run()
    {
        Check.Run("update notice creates no UI while there is nothing to report", NothingToReport);
        Check.Run("update notice sits in the top-left corner above the chat window", TopLeftAboveChat);
        Check.Run("restart button exists only for an installed update and restarts on click", RestartButton);
        Check.Run("closing hides first, disposes on the next frame and stays closed for that result", CloseAndReopen);
        Check.Run("a new result replaces the shown notice", ReplacedByNewResult);
        Check.Run("a long message grows the panel instead of overlapping the restart button", LongMessage);
        Check.Run("game font reset rebuilds the notice with a fresh font registration", FontReset);
        Check.Run("font registrations by the chat window or another plugin never rebuild the notice", ForeignFontWrites);
        Check.Run("a notice torn down or detached by the game is rebuilt without leaking handles", GameTeardown);
        Check.Run("a failed notice leaks no UI, is retried after a delay and gives up after three failures", CreationFailure);
        Check.Run("a notice that fails once is shown when the retry succeeds", TransientFailure);
        Check.Run("shutdown releases the notice and its font", ShutdownReleases);
        UpdateNotice.Shutdown();
    }

    private static FguiNode Start(bool chatOpen = false)
    {
        UpdateNotice.Shutdown();
        ChatOverlay.ResetHarness(chatOpen);
        Warnings.Clear();
        _restarts = 0;
        return ChatOverlay.TestRoot;
    }

    private static void Tick(int revision, PluginUpdateState state, FguiNode? target = null, bool pressed = false)
    {
        FguiNode.Target = target;
        Input.Pressed = pressed;
        try { UpdateNotice.Tick(revision, state, () => _restarts++, Warnings.Add); }
        finally
        {
            Input.Pressed = false;
            FguiNode.Target = null;
        }
    }

    private static IEnumerable<FguiNode> Descendants(FguiNode node) =>
        node.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static FguiNode TextNode(FguiNode panel, string text) =>
        Descendants(panel).Single(node => node.GetText() == text);

    private static void NothingToReport()
    {
        var root = Start();
        var children = root.Children.Count;
        var handles = FguiNode.LiveHandles;
        for (var frame = 0; frame < 5; frame++)
            Tick(0, Quiet, pressed: frame == 2);
        Check.True(UpdateNotice.Panel == null, "no panel without a message");
        Check.Equal(children, root.Children.Count, "root untouched");
        Check.Equal(handles, FguiNode.LiveHandles, "no native object created");
        Check.Equal(0, FguiNode.RegisteredFonts.Count, "no font registered");
    }

    private static void TopLeftAboveChat()
    {
        var root = Start(chatOpen: true);
        Tick(1, Manual);
        var panel = UpdateNotice.Panel!;
        Check.True(panel.Parent == root && panel.Visible, "panel shown on the root");
        Check.True(panel.X > 0 && panel.Y > 0, "panel keeps a margin from the corner");
        Check.True(panel.X + panel.Width <= root.Width / 2 && panel.Y + panel.Height <= root.Height / 2, "panel stays in the top-left quadrant");
        Check.True(panel.Opaque, "clicks on the panel do not reach the game");
        Check.True(panel.SortingOrder > root.Children.Where(child => child != panel).Max(child => child.SortingOrder),
            "panel renders above the chat window");
        Check.True(TextNode(panel, Manual.Message!).Visible, "message shown");
        Check.True(UpdateNotice.RestartButton == null, "no restart button for a manual-update notice");
    }

    private static void RestartButton()
    {
        Start();
        Tick(1, Installed);
        var panel = UpdateNotice.Panel!;
        var restart = UpdateNotice.RestartButton!;
        Check.True(restart.Parent == panel && restart.Visible && restart.Opaque, "restart button is clickable");
        Check.True(restart.X >= 0 && restart.X + restart.Width <= panel.Width && restart.Y + restart.Height <= panel.Height, "restart button inside the panel");
        Tick(1, Installed, restart);
        Check.Equal(0, _restarts, "hover does not restart");
        Tick(1, Installed, panel, pressed: true);
        Tick(1, Installed, target: null, pressed: true);
        Check.Equal(0, _restarts, "clicks outside the button do not restart");
        Tick(1, Installed, restart, pressed: true);
        Check.Equal(1, _restarts, "click restarts");
        Check.True(UpdateNotice.Panel == panel && panel.Visible, "notice stays until the game quits");
    }

    private static void CloseAndReopen()
    {
        Start();
        var handles = FguiNode.LiveHandles;
        Tick(1, Installed);
        var panel = UpdateNotice.Panel!;
        Tick(1, Installed, UpdateNotice.CloseButton, pressed: true);
        Check.True(!panel.Visible && !panel.IsDisposed, "the frame's hit target is hidden, not disposed");
        Tick(1, Installed);
        Check.True(panel.IsDisposed && UpdateNotice.Panel == null, "disposed on the following frame");
        Check.Equal(handles, FguiNode.LiveHandles, "all handles released");
        for (var frame = 0; frame < 3; frame++)
            Tick(1, Installed);
        Check.True(UpdateNotice.Panel == null, "a dismissed result stays closed");
        Check.Equal(0, _restarts, "closing never restarts");

        var failed = Installed with { Message = "다시 시작하지 못했습니다 · 게임을 종료한 뒤 다시 실행해 주세요", Severity = UpdateSeverity.Warning };
        Tick(2, failed);
        Check.True(UpdateNotice.Panel != null && TextNode(UpdateNotice.Panel, failed.Message!).Visible, "a later result opens a new notice");
    }

    private static void ReplacedByNewResult()
    {
        var root = Start();
        var handles = FguiNode.LiveHandles;
        Tick(1, Manual);
        var first = UpdateNotice.Panel!;
        var nodes = FguiNode.LiveHandles - handles;
        Tick(2, Installed);
        Tick(2, Installed);
        var second = UpdateNotice.Panel!;
        Check.True(second != first && first.IsDisposed, "old notice disposed");
        Check.Equal(1, root.Children.Count(child => child.SortingOrder == second.SortingOrder), "exactly one notice on screen");
        Check.True(TextNode(second, Installed.Message!).Visible && UpdateNotice.RestartButton != null, "new result rendered");
        Check.True(FguiNode.LiveHandles - handles > nodes, "restart button added");
        Tick(3, Quiet);
        Tick(3, Quiet);
        Check.True(UpdateNotice.Panel == null, "a cleared result removes the notice");
        Check.Equal(handles, FguiNode.LiveHandles, "all handles released");
    }

    private static void LongMessage()
    {
        Start();
        Tick(1, Installed);
        var shortHeight = UpdateNotice.Panel!.Height;
        var longState = Installed with { Message = string.Join(' ', Enumerable.Repeat("v0.3.0 업데이트 안내 문장입니다.", 12)) };
        Tick(2, longState);
        Tick(2, longState);
        var panel = UpdateNotice.Panel!;
        var message = TextNode(panel, longState.Message!);
        var restart = UpdateNotice.RestartButton!;
        Check.True(panel.Height > shortHeight, "panel grows with the message");
        Check.True(restart.Y >= message.Y + message.Height, "restart button below the message");
        Check.True(restart.Y + restart.Height <= panel.Height, "restart button inside the grown panel");
        Check.True(panel.Children[0].Height == panel.Height, "background covers the grown panel");
    }

    private static void FontReset()
    {
        var root = Start();
        Tick(1, Installed);
        var first = UpdateNotice.Panel!;
        var alias = FguiNode.RegisteredFonts.Single();
        Check.True(Descendants(first).Where(node => node.GetText().Length > 0).All(node => node.FontName == alias), "every text uses the notice font");
        for (var frame = 0; frame < 3; frame++)
            Tick(1, Installed);
        Check.True(UpdateNotice.Panel == first, "a stable font table never rebuilds the notice");

        // The game's FontManager.Clear() drops every registered font when the match client starts.
        FguiNode.RegisteredFonts.Clear();
        FguiNode.FontRegistryVersion++;
        Tick(1, Installed);
        Tick(1, Installed);
        var second = UpdateNotice.Panel!;
        Check.True(second != first && first.IsDisposed && second.Visible, "notice rebuilt");
        Check.True(FguiNode.RegisteredFonts.SequenceEqual(new[] { alias }), "font registered again");
        Check.Equal(1, root.Children.Count(child => child.SortingOrder == second.SortingOrder), "exactly one notice on screen");
    }

    private static void ForeignFontWrites()
    {
        Start();
        Tick(1, Installed);
        var panel = UpdateNotice.Panel!;
        var alias = FguiNode.RegisteredFonts.Single();
        // Another panel that rebuilds on every font table write (the Korean plugin's
        // status panel) answers each of our registrations with its own.
        for (var frame = 0; frame < 6; frame++)
        {
            FguiNode.ReleaseFont("OtherPlugin");
            FguiNode.RegisterFont("OtherPlugin", new Font());
            Tick(1, Installed);
        }
        Check.True(UpdateNotice.Panel == panel && !panel.IsDisposed && panel.Visible, "notice kept");
        Check.True(FguiNode.RegisteredFonts.Contains(alias), "notice font kept");
    }

    private static void GameTeardown()
    {
        var root = Start();
        var handles = FguiNode.LiveHandles;
        Tick(1, Installed);
        var first = UpdateNotice.Panel!;
        var owned = FguiNode.LiveHandles - handles;
        first.Dispose();
        Tick(1, Installed);
        var second = UpdateNotice.Panel!;
        Check.True(second != first && second.Parent == root && second.Visible, "disposed notice recreated at once");
        Check.Equal(owned, FguiNode.LiveHandles - handles, "handles of the dead notice released");

        var other = FguiNode.Component(10, 10);
        other.AddChild(second);
        Tick(1, Installed);
        Tick(1, Installed);
        var third = UpdateNotice.Panel!;
        Check.True(third != second && third.Parent == root, "detached notice rebuilt on the root");
        Check.Equal(owned + 1, FguiNode.LiveHandles - handles, "only the foreign host remains extra");
    }

    private static void CreationFailure()
    {
        var root = Start();
        var children = root.Children.Count;
        var handles = FguiNode.LiveHandles;
        FguiNode.FaultText = Installed.Message;
        Tick(1, Installed);
        Check.Equal(1, Warnings.Count, "failure logged");
        Check.True(UpdateNotice.Panel == null, "no half-built panel kept");
        Check.Equal(children, root.Children.Count, "partial UI removed from the root");
        Check.Equal(handles, FguiNode.LiveHandles, "partial UI handles released");

        for (var frame = 0; frame < 3; frame++)
            Tick(1, Installed);
        Check.Equal(1, Warnings.Count, "no attempt on every frame");
        Time.unscaledTime += 6;
        Tick(1, Installed);
        Time.unscaledTime += 6;
        Tick(1, Installed);
        Check.Equal(3, Warnings.Count, "retried after each delay");
        Check.Equal(children, root.Children.Count, "retries leave nothing behind");
        Check.Equal(handles, FguiNode.LiveHandles, "retries release their handles");

        FguiNode.FaultText = null;
        Time.unscaledTime += 60;
        Tick(1, Installed);
        Check.True(UpdateNotice.Panel == null, "gives up after the third failure");
        Check.Equal(3, Warnings.Count, "silent after giving up");
    }

    private static void TransientFailure()
    {
        var root = Start();
        FguiNode.FaultText = Installed.Message;
        Tick(1, Installed);
        FguiNode.FaultText = null;
        Tick(1, Installed);
        Check.True(UpdateNotice.Panel == null, "waits before retrying");
        Time.unscaledTime += 6;
        Tick(1, Installed);
        Check.True(UpdateNotice.Panel is { IsDisposed: false } panel && panel.IsChildOf(root), "notice shown once the failure clears");
        Check.Equal(1, Warnings.Count, "only the failure was logged");
    }

    private static void ShutdownReleases()
    {
        Start();
        var handles = FguiNode.LiveHandles;
        Tick(1, Installed);
        var panel = UpdateNotice.Panel!;
        UpdateNotice.Shutdown();
        Check.True(panel.IsDisposed && UpdateNotice.Panel == null, "panel disposed");
        Check.Equal(handles, FguiNode.LiveHandles, "handles released");
        Check.Equal(0, FguiNode.RegisteredFonts.Count, "font alias released");
        Tick(1, Installed);
        Check.True(UpdateNotice.Panel != null, "a reloaded plugin can show the notice again");
    }
}
