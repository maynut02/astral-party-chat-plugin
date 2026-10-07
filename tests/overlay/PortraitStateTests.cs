#if OVERLAY_HARNESS
using System;
using System.Linq;
using AstralPartyChatPlugin;
using UnityEngine;

internal static class PortraitStateTests
{
    private static int _passed;
    private static int _assertions;

    internal static void Run()
    {
        Check("unselected and spectator render distinct labels with no portrait requests", SpecialStatesHaveNoPortrait);
        Check("selection and deselection use exact-ID CDN portraits while retaining message identity", SelectionTransitions);
        Check("duplicate nicknames and mixed history render distinct exact-ID CDN portraits", MixedHistory);
        Check("late CDN callbacks cannot attach portraits to unselected or spectator rows", LateCallbacks);
        Check("late CDN callbacks for the old character cannot overwrite a changed selection", LateSelectionCallback);
        Check("selected portraits stay visible on retained rows through room-selection-play transitions", SceneTransitions);
        Console.WriteLine($"{_passed} portrait state regression checks passed ({_assertions} assertions).");
    }

    private static void Check(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            throw;
        }
    }

    private static ChatUiMessage Message(string characterId, string characterName = "", string id = "state-row") => new()
    {
        Id = id, Sender = "Mina", CharacterId = characterId, CharacterName = characterName, Order = "P2", Text = "상태 확인"
    };

    private static string Label(ChatOverlay.RowSnapshot row) =>
        row.Texts.Single(text => text.gameObject.name == "Participant").text;

    private static void AssertNoPortrait(ChatOverlay.RowSnapshot row)
    {
        True(row.Portrait != null, "portrait slot exists");
        True(row.Portrait!.texture == null && !row.Portrait.gameObject.activeSelf, "special-state portrait stays empty");
    }

    private static void SpecialStatesHaveNoPortrait()
    {
        foreach (var id in new[] { "unselected", " unselected ", "spectator" })
        foreach (var name in new[] { "", "미선택", "이전 캐릭터 이름" })
        {
            ChatOverlay.ResetHarness();
            var texture = new Texture();
            ChatOverlay.SetCdnSprite(id, new Sprite(texture));
            ChatOverlay.SetChatMessages(new[] { Message(id, name) });
            var row = ChatOverlay.InspectRows().Single();
            Equal(id.Trim() == "unselected" ? "P2 · 미선택" : "P2 · 관전", Label(row), "state label including pick order");
            AssertNoPortrait(row);
            foreach (var phase in new[] { "방", "캐릭터 선택", "플레이" })
            {
                Time.unscaledTime += 3f;
                ChatOverlay.RefreshPortraitsForTest(new ChatSnapshot { RoomId = "123456", ScreenPhase = phase });
                AssertNoPortrait(row);
            }
            Equal(0, ChatOverlay.InspectCharacterImageRequests().Count, "special states never request a CDN portrait");
            True(ReferenceEquals(row.Root, ChatOverlay.InspectRows().Single().Root), "refresh retains message row");
        }
    }

    private static void SelectionTransitions()
    {
        ChatOverlay.ResetHarness();
        var cdnTexture = new Texture();
        ChatOverlay.SetCdnSprite("101", new Sprite(cdnTexture));
        ChatOverlay.RowSnapshot? previous = null;
        foreach (var id in new[] { "unselected", "101", "unselected", "spectator", "101" })
        {
            ChatOverlay.SetChatMessages(new[] { Message(id, "선택 캐릭터") });
            var row = ChatOverlay.InspectRows().Single();
            if (previous.HasValue)
            {
                Equal(previous.Value.Identity, row.Identity, "message identity retained across selection");
                True(previous.Value.Root.IsDestroyed, "previous state row released");
            }
            if (id == "101")
            {
                True(ReferenceEquals(cdnTexture, row.Portrait!.texture) && row.Portrait.gameObject.activeSelf,
                    "selected character uses exact-ID CDN portrait");
                Equal("P2 · 선택 캐릭터", Label(row), "selected character label");
            }
            else AssertNoPortrait(row);
            previous = row;
        }
        True(ChatOverlay.InspectCharacterImageRequests().Count > 0, "selected portraits request CDN assets");
        True(ChatOverlay.InspectCharacterImageRequests().All(id => id == "101"), "only selected IDs request assets");
    }

    private static void MixedHistory()
    {
        ChatOverlay.ResetHarness();
        var firstTexture = new Texture();
        var secondTexture = new Texture();
        ChatOverlay.SetCdnSprite("101", new Sprite(firstTexture));
        ChatOverlay.SetCdnSprite("102", new Sprite(secondTexture));
        ChatOverlay.SetChatMessages(new[] { Message("unselected", id: "unselected-row"),
            Message("101", "첫 캐릭터", "selected-row"), Message("spectator", id: "spectator-row"),
            Message("102", "둘째 캐릭터", "other-selected-row") });
        var rows = ChatOverlay.InspectRows();
        // All rows have the same nickname. Refresh must use each row's ID,
        // clear special states and replace live-looking but mismatched textures.
        foreach (var row in new[] { rows[0], rows[2] })
        {
            row.Portrait!.texture = firstTexture;
            row.Portrait.gameObject.SetActive(true);
        }
        rows[1].Portrait!.texture = secondTexture;
        rows[3].Portrait!.texture = firstTexture;
        ChatOverlay.RefreshPortraitsForTest(new ChatSnapshot { RoomId = "123456", ScreenPhase = "플레이" });
        AssertNoPortrait(rows[0]);
        AssertNoPortrait(rows[2]);
        True(ReferenceEquals(firstTexture, rows[1].Portrait!.texture), "first character uses ID 101");
        True(ReferenceEquals(secondTexture, rows[3].Portrait!.texture), "same nickname with another character uses ID 102");
        True(ChatOverlay.InspectCharacterImageRequests().All(id => id is "101" or "102"), "only selected IDs request images");
        Equal(4, ChatOverlay.InspectRows().Count, "mixed history retains all rows");
    }

    private static void LateCallbacks()
    {
        foreach (var id in new[] { "unselected", "spectator" })
        {
            ChatOverlay.ResetHarness();
            ChatOverlay.SetChatMessages(new[] { Message(id) });
            var row = ChatOverlay.InspectRows().Single();
            ChatOverlay.PublishCdnPortraitForTest(id, new Sprite(new Texture()));
            AssertNoPortrait(row);
            ChatOverlay.RenderMessagesForTest();
            AssertNoPortrait(row);
            Equal(0, ChatOverlay.InspectCharacterImageRequests().Count, "late callback does not trigger another request");
            True(ReferenceEquals(row.Root, ChatOverlay.InspectRows().Single().Root), "callback and reflow retain row");
        }
    }

    private static void LateSelectionCallback()
    {
        ChatOverlay.ResetHarness();
        var oldTexture = new Texture();
        ChatOverlay.SetCdnSprite("101", new Sprite(oldTexture));
        ChatOverlay.SetChatMessages(new[] { Message("101", "첫 캐릭터") });
        var oldRow = ChatOverlay.InspectRows().Single();
        True(ReferenceEquals(oldTexture, oldRow.Portrait!.texture), "initial CDN image");
        ChatOverlay.SetChatMessages(new[] { Message("102", "둘째 캐릭터") });
        var row = ChatOverlay.InspectRows().Single();
        Equal(oldRow.Identity, row.Identity, "message identity retained");
        True(oldRow.Root.IsDestroyed, "previous character row released");
        AssertNoPortrait(row);
        ChatOverlay.PublishCdnPortraitForTest("101", new Sprite(new Texture()));
        AssertNoPortrait(row);
        var currentTexture = new Texture();
        ChatOverlay.PublishCdnPortraitForTest("102", new Sprite(currentTexture));
        True(ReferenceEquals(currentTexture, row.Portrait!.texture) && row.Portrait.gameObject.activeSelf,
            "current ID callback displays correct portrait");
        True(ReferenceEquals(row.Root, ChatOverlay.InspectRows().Single().Root), "CDN callback retains current row");
    }

    private static void SceneTransitions()
    {
        ChatOverlay.ResetHarness();
        var texture = new Texture();
        ChatOverlay.SetCdnSprite("101", new Sprite(texture));
        ChatOverlay.SetChatMessages(new[] { Message("101", "선택 캐릭터") });
        var row = ChatOverlay.InspectRows().Single();
        foreach (var phase in new[] { "방", "캐릭터 선택", "플레이", "방" })
        {
            Time.unscaledTime += 3f;
            ChatOverlay.RefreshPortraitsForTest(new ChatSnapshot { RoomId = "123456", ScreenPhase = phase });
            var refreshed = ChatOverlay.InspectRows().Single();
            True(ReferenceEquals(row.Root, refreshed.Root), "scene transition keeps the message row");
            True(ReferenceEquals(texture, refreshed.Portrait!.texture) && refreshed.Portrait.gameObject.activeSelf,
                "selected portrait stays visible in " + phase);
        }
    }

    private static void True(bool value, string description)
    {
        _assertions++;
        if (!value) throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        _assertions++;
        if (!Equals(expected, actual)) throw new InvalidOperationException($"{description}: expected {expected}, got {actual}");
    }
}
#endif
