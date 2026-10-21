#if OVERLAY_HARNESS
using System;
using System.Linq;
using AstralPartyChatPlugin;
using UnityEngine;

internal static class PortraitStateTests
{
    internal static void Run()
    {
        Check.Run("unselected and spectator labels never request portraits", SpecialStatesHaveNoPortrait);
        Check.Run("exact-ID CDN retries update retained portrait rows in place", ExactIdRetryUpdatesInPlace);
        Check.Run("each room-selection-play phase clears stale textures and restores exact IDs", StaleTexturesAreReconciledInEveryPhase);
        Check.Run("selection and deselection preserve identity while replacing presentation", SelectionTransitions);
        Check.Run("duplicate nicknames retain independent exact-ID historical portraits", MixedHistory);
        Check.Run("late CDN delivery cannot attach portraits to special states", LateCallbacks);
        Check.Run("late old-selection delivery cannot overwrite a new selection", LateSelectionCallback);
        Check.Run("selected portraits remain on retained rows across phase transitions", SceneTransitions);
    }

    private static ChatUiMessage Message(string characterId, string characterName = "", string id = "state-row") => new()
    {
        Id = id, Sender = "Mina", CharacterId = characterId, CharacterName = characterName,
        Order = "P2", Text = "상태 확인"
    };

    private static void SetMessages(params ChatUiMessage[] messages)
    {
        ChatOverlay.SetChatMessages(messages);
        ChatOverlay.Tick();
    }

    private static string Label(ChatOverlay.RowSnapshot row) => row.Participant!.GetText();

    private static void Same(object expected, object actual, string description) =>
        Check.True(ReferenceEquals(expected, actual), description);

    private static void AssertNoPortrait(ChatOverlay.RowSnapshot row)
    {
        Check.True(row.Portrait != null, "normal message retains portrait slot");
        Check.True(row.Portrait!.Texture == null && !row.Portrait.Visible, "portrait texture cleared and slot hidden");
    }

    private static void AssertPortrait(ChatOverlay.RowSnapshot row, Texture texture, string description)
    {
        Check.True(row.Portrait != null && row.Portrait.Visible, description + " is visible");
        Same(texture, row.Portrait!.Texture!, description + " uses exact texture");
    }

    private static void SpecialStatesHaveNoPortrait()
    {
        foreach (var id in new[] { "unselected", " unselected ", "spectator", " spectator ", "", "invalid-id" })
        foreach (var name in new[] { "", "미선택", "이전 캐릭터 이름" })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.CharacterTextures[id] = new Texture();
            SetMessages(Message(id, name));
            var row = ChatOverlay.InspectRows().Single();
            Check.Equal(id.Trim() == "unselected" ? "P2 · 미선택" : "P2 · 관전", Label(row),
                "normalized special-state label takes precedence over stale character name");
            Check.Equal("Mina", row.Sender!.GetText(), "nickname remains independent of participant state");
            AssertNoPortrait(row);
            foreach (var phase in new[] { "방", "캐릭터 선택", "플레이" })
            {
                ChatOverlay.TestSnapshot.ScreenPhase = phase;
                Time.unscaledTime += 3f;
                ChatOverlay.Tick();
                AssertNoPortrait(ChatOverlay.InspectRows().Single());
            }
            Check.Equal(0, ChatOverlay.CharacterImageRequests.Count, "special states never request CDN assets");
            Same(row.Root, ChatOverlay.InspectRows().Single().Root, "special-state refresh retains row");
        }
    }

    private static void ExactIdRetryUpdatesInPlace()
    {
        ChatOverlay.ResetHarness();
        var otherTexture = new Texture();
        ChatOverlay.CharacterTextures["102"] = otherTexture;
        SetMessages(Message("101", "선택 캐릭터"));
        var row = ChatOverlay.InspectRows().Single();
        AssertNoPortrait(row);
        Check.True(ChatOverlay.CharacterImageRequests.Count > 0, "missing exact ID requests CDN image");
        Check.True(ChatOverlay.CharacterImageRequests.All(id => id == "101"), "no name, phase or alternate-ID requests");
        var requests = ChatOverlay.CharacterImageRequests.Count;
        Time.unscaledTime += 1f;
        ChatOverlay.Tick();
        Check.Equal(requests, ChatOverlay.CharacterImageRequests.Count, "portrait retry is throttled within two seconds");
        Time.unscaledTime += 2.1f;
        ChatOverlay.Tick();
        Check.True(ChatOverlay.CharacterImageRequests.Count > requests, "missing exact-ID image is retried");
        AssertNoPortrait(ChatOverlay.InspectRows().Single());
        var texture = new Texture();
        ChatOverlay.CharacterTextures["101"] = texture;
        Time.unscaledTime += 2.1f;
        ChatOverlay.Tick();
        var refreshed = ChatOverlay.InspectRows().Single();
        AssertPortrait(refreshed, texture, "retried exact-ID portrait");
        Same(row.Root, refreshed.Root, "retry retains root");
        Same(row.Body, refreshed.Body, "retry retains body");
        Same(row.Portrait!, refreshed.Portrait!, "retry retains portrait loader");
        Check.Equal(row.Identity, refreshed.Identity, "retry retains message identity");
        Check.Near(row.Height, refreshed.Height, "portrait arrival does not remeasure message");
    }

    private static void StaleTexturesAreReconciledInEveryPhase()
    {
        ChatOverlay.ResetHarness();
        var exactTexture = new Texture();
        ChatOverlay.CharacterTextures["101"] = exactTexture;
        ChatOverlay.TestSnapshot.ScreenPhase = "portrait-baseline";
        SetMessages(Message("101", "첫 캐릭터", "cached"), Message("102", "둘째 캐릭터", "missing"),
            Message("unselected", "이전 이름", "unselected"), Message("spectator", "이전 이름", "spectator"));
        var rows = ChatOverlay.InspectRows();
        foreach (var phase in new[] { "방", "캐릭터 선택", "플레이", "방" })
        {
            foreach (var row in rows)
            {
                row.Portrait!.SetTexture(new Texture());
                row.Portrait.SetVisible(true);
            }
            ChatOverlay.TestSnapshot.ScreenPhase = phase;
            // A phase change must reconcile immediately, even before the timed retry.
            ChatOverlay.Tick();
            var refreshed = ChatOverlay.InspectRows();
            AssertPortrait(refreshed[0], exactTexture, "cached exact-ID portrait in " + phase);
            for (var index = 1; index < refreshed.Length; index++)
                AssertNoPortrait(refreshed[index]);
            for (var index = 0; index < refreshed.Length; index++)
                Same(rows[index].Root, refreshed[index].Root, "phase reconciliation keeps row root");
        }
        Check.True(ChatOverlay.CharacterImageRequests.All(id => id is "101" or "102"),
            "stale special-state loaders never request portraits");
    }

    private static void SelectionTransitions()
    {
        ChatOverlay.ResetHarness();
        var texture = new Texture();
        ChatOverlay.CharacterTextures["101"] = texture;
        ChatOverlay.RowSnapshot? previous = null;
        foreach (var id in new[] { "unselected", "101", "unselected", "spectator", "102", "101" })
        {
            SetMessages(Message(id, "선택 캐릭터"));
            var row = ChatOverlay.InspectRows().Single();
            if (previous.HasValue)
            {
                Check.Equal(previous.Value.Identity, row.Identity, "selection preserves logical message identity");
                Check.True(previous.Value.Root.IsDisposed, "changed presentation disposes obsolete row");
                Check.True(!ReferenceEquals(previous.Value.Root, row.Root), "changed selection replaces presentation root");
            }
            Check.Equal(id == "unselected" ? "P2 · 미선택" : id == "spectator" ? "P2 · 관전" : "P2 · 선택 캐릭터",
                Label(row), "selection state updates participant label");
            if (id == "101") AssertPortrait(row, texture, "selected exact-ID image");
            else AssertNoPortrait(row);
            if (id is "unselected" or "spectator")
            {
                ChatOverlay.DeliverPortrait("101", new Texture());
                AssertNoPortrait(ChatOverlay.InspectRows().Single());
            }
            previous = row;
        }
        Check.True(ChatOverlay.CharacterImageRequests.Any(id => id == "101"), "selected image requested");
        Check.True(ChatOverlay.CharacterImageRequests.Any(id => id == "102"), "new missing selection requested");
        Check.True(ChatOverlay.CharacterImageRequests.All(id => id is "101" or "102"), "only valid selected IDs request portraits");
    }

    private static void MixedHistory()
    {
        ChatOverlay.ResetHarness();
        var first = new Texture();
        var second = new Texture();
        ChatOverlay.CharacterTextures["101"] = first;
        ChatOverlay.CharacterTextures["102"] = second;
        SetMessages(Message("unselected", id: "unselected-row"), Message(" 101 ", "첫 캐릭터", "selected-row"),
            Message("spectator", id: "spectator-row"), Message("102", "둘째 캐릭터", "other-selected-row"));
        var rows = ChatOverlay.InspectRows();
        Check.Equal(4, rows.Length, "mixed history keeps all messages");
        Check.True(rows.All(row => row.Sender!.GetText() == "Mina"), "all messages deliberately share nickname");
        AssertNoPortrait(rows[0]);
        AssertPortrait(rows[1], first, "normalized ID 101");
        AssertNoPortrait(rows[2]);
        AssertPortrait(rows[3], second, "same-nickname ID 102");
        Check.Equal("P2 · 첫 캐릭터", Label(rows[1]), "historical first character label");
        Check.Equal("P2 · 둘째 캐릭터", Label(rows[3]), "historical second character label");
        var replacement = new Texture();
        ChatOverlay.DeliverPortrait("101", replacement);
        AssertPortrait(ChatOverlay.InspectRows()[1], replacement, "exact-ID delivery updates matching historical row");
        AssertPortrait(ChatOverlay.InspectRows()[3], second, "delivery leaves other character untouched");
        AssertNoPortrait(ChatOverlay.InspectRows()[0]);
        AssertNoPortrait(ChatOverlay.InspectRows()[2]);
        for (var index = 0; index < rows.Length; index++)
            Same(rows[index].Root, ChatOverlay.InspectRows()[index].Root, "delivery retains all historical roots");
    }

    private static void LateCallbacks()
    {
        foreach (var id in new[] { "unselected", "spectator" })
        {
            ChatOverlay.ResetHarness();
            SetMessages(Message(id));
            var row = ChatOverlay.InspectRows().Single();
            ChatOverlay.DeliverPortrait(id, new Texture());
            ChatOverlay.DeliverPortrait("101", new Texture());
            AssertNoPortrait(ChatOverlay.InspectRows().Single());
            ChatOverlay.SetChatMessages(new[] { Message(id) });
            Time.unscaledTime += 3f;
            ChatOverlay.Tick();
            AssertNoPortrait(ChatOverlay.InspectRows().Single());
            Check.Equal(0, ChatOverlay.CharacterImageRequests.Count, "late special-state callback never triggers image request");
            Same(row.Root, ChatOverlay.InspectRows().Single().Root, "callback, replay and refresh retain special-state row");
        }
    }

    private static void LateSelectionCallback()
    {
        ChatOverlay.ResetHarness();
        var oldTexture = new Texture();
        ChatOverlay.CharacterTextures["101"] = oldTexture;
        SetMessages(Message("101", "첫 캐릭터"));
        var old = ChatOverlay.InspectRows().Single();
        AssertPortrait(old, oldTexture, "initial selection");
        SetMessages(Message("102", "둘째 캐릭터"));
        var current = ChatOverlay.InspectRows().Single();
        Check.Equal(old.Identity, current.Identity, "new selection retains message identity");
        Check.True(old.Root.IsDisposed, "old selection root released");
        AssertNoPortrait(current);
        ChatOverlay.DeliverPortrait("101", new Texture());
        AssertNoPortrait(ChatOverlay.InspectRows().Single());
        var currentTexture = new Texture();
        ChatOverlay.DeliverPortrait("102", currentTexture);
        AssertPortrait(ChatOverlay.InspectRows().Single(), currentTexture, "current selection delivery");
        ChatOverlay.DeliverPortrait("101", new Texture());
        AssertPortrait(ChatOverlay.InspectRows().Single(), currentTexture, "late old selection cannot overwrite current image");
        Same(current.Root, ChatOverlay.InspectRows().Single().Root, "late deliveries retain current row");
        SetMessages(Message("spectator"));
        ChatOverlay.DeliverPortrait("102", new Texture());
        AssertNoPortrait(ChatOverlay.InspectRows().Single());
        Check.True(current.Root.IsDisposed, "deselection releases former selected row");
    }

    private static void SceneTransitions()
    {
        ChatOverlay.ResetHarness();
        var texture = new Texture();
        ChatOverlay.CharacterTextures["101"] = texture;
        SetMessages(Message("101", "선택 캐릭터"));
        var row = ChatOverlay.InspectRows().Single();
        foreach (var phase in new[] { "방", "캐릭터 선택", "플레이", "방" })
        {
            ChatOverlay.TestSnapshot.ScreenPhase = phase;
            Time.unscaledTime += 3f;
            ChatOverlay.Tick();
            var refreshed = ChatOverlay.InspectRows().Single();
            Same(row.Root, refreshed.Root, "scene transition keeps selected message root");
            AssertPortrait(refreshed, texture, "selected portrait in " + phase);
            Check.Equal("P2 · 선택 캐릭터", Label(refreshed), "scene transition retains participant label");
        }
    }
}
#endif
