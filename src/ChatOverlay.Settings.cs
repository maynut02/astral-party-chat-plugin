using System.Collections.Generic;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class ChatOverlay
{
    // Version 4 stores FairyGUI GRoot geometry. Earlier uGUI canvas offsets
    // use a different coordinate system and are removed instead of converted.
    private const string WindowWidthPref = "AstralPartyChat.Window.v4.Width";
    private const string WindowHeightPref = "AstralPartyChat.Window.v4.Height";
    private const string WindowRightPref = "AstralPartyChat.Window.v4.Right";
    private const string WindowCenterYPref = "AstralPartyChat.Window.v4.CenterY";
    private const string TextScalePref = "AstralPartyChat.TextScale";
    private const string OpacityPref = "AstralPartyChat.Opacity";
    private static readonly string[] LegacyPrefs =
    {
        "AstralPartyChat.WindowX", "AstralPartyChat.WindowY",
        "AstralPartyChat.WindowX.v2", "AstralPartyChat.WindowY.v2",
        "AstralPartyChat.WindowX.v3", "AstralPartyChat.WindowY.v3",
        "AstralPartyChat.WindowScale"
    };
    private const float StepperSize = 46f;
    private const float StepperValueWidth = 96f;

    private static readonly Color SettingsPanelColor = new Color32(32, 32, 32, 250);
    private static readonly Dictionary<ChatControl, FguiNode> SettingsBackgrounds = new();
    private static ChatWindowGeometry _windowGeometry = ChatWindowGeometry.Default;
    private static float _textScale = 1f;
    private static float _windowOpacity = 1f;
    private static bool _settingsOpen;
    private static FguiNode? _settingsPanel;
    private static FguiNode? _settingsPanelBackground;
    private static FguiNode? _textScaleValue;
    private static FguiNode? _opacityValue;
    private static FguiNode? _settingsHint;
    private static readonly List<FguiNode> SettingsRows = new();

    private static void LoadSettings()
    {
        var removedLegacy = false;
        foreach (var key in LegacyPrefs)
        {
            if (!PlayerPrefs.HasKey(key)) continue;
            PlayerPrefs.DeleteKey(key);
            removedLegacy = true;
        }
        if (removedLegacy)
            PlayerPrefs.Save();

        var defaults = ChatWindowGeometry.Default;
        var geometry = new ChatWindowGeometry(
            PlayerPrefs.GetFloat(WindowWidthPref, defaults.Width),
            PlayerPrefs.GetFloat(WindowHeightPref, defaults.Height),
            PlayerPrefs.GetFloat(WindowRightPref, defaults.Right),
            PlayerPrefs.GetFloat(WindowCenterYPref, defaults.CenterY));
        _windowGeometry = geometry.IsFinite ? geometry : defaults;
        _textScale = ChatLayout.ClampTextScale(PlayerPrefs.GetFloat(TextScalePref, 1f));
        _windowOpacity = ChatLayout.ClampOpacity(PlayerPrefs.GetFloat(OpacityPref, 1f));
    }

    private static void SaveWindowGeometry()
    {
        PlayerPrefs.SetFloat(WindowWidthPref, _windowGeometry.Width);
        PlayerPrefs.SetFloat(WindowHeightPref, _windowGeometry.Height);
        PlayerPrefs.SetFloat(WindowRightPref, _windowGeometry.Right);
        PlayerPrefs.SetFloat(WindowCenterYPref, _windowGeometry.CenterY);
        PlayerPrefs.Save();
    }

    private static void SaveAppearance()
    {
        PlayerPrefs.SetFloat(TextScalePref, _textScale);
        PlayerPrefs.SetFloat(OpacityPref, _windowOpacity);
        PlayerPrefs.Save();
    }

    private static void CreateSettingsPanel(FguiNode window)
    {
        var panel = _settingsPanel = AddButton(window, ChatControl.SettingsPanel,
            ChatLayout.SidePadding, ChatLayout.MessagesTop, 1f, 1f);
        panel.ClipChildren();
        _settingsPanelBackground = AddGraph(panel, 0f, 0f, 1f, 1f, SettingsPanelColor, 12f);
        AddText(panel, "채팅 설정", 22, Color.white, FguiAlign.Left, 20f, 12f, 300f, 36f);
        _textScaleValue = CreateStepperRow(panel, "글자 크기", 62f, ChatControl.TextSmaller, ChatControl.TextLarger);
        _opacityValue = CreateStepperRow(panel, "배경 불투명도", 118f, ChatControl.OpacityDown, ChatControl.OpacityUp);
        var reset = AddButton(panel, ChatControl.ResetLayout, 20f, 180f, 1f, StepperSize);
        SettingsRows.Add(reset);
        SettingsBackgrounds[ChatControl.ResetLayout] = AddGraph(reset, 0f, 0f, 1f, StepperSize, HeaderButtonColor, 10f);
        SettingsRows.Add(AddText(reset, "창 크기·위치 초기화", 19, Color.white, FguiAlign.Center, 0f, 0f, 1f, StepperSize));
        _settingsHint = AddText(panel, "창 오른쪽 아래 모서리를 끌어 크기를 바꿀 수 있습니다.", 16, MutedTextColor,
            FguiAlign.Left, 20f, 238f, 1f, 1f, FguiVerticalAlign.Top, wrap: true);
        panel.SetVisible(false);
        RefreshSettingsValues();
    }

    private static FguiNode CreateStepperRow(FguiNode panel, string label, float y, ChatControl smaller, ChatControl larger)
    {
        AddText(panel, label, 19, MutedTextColor, FguiAlign.Left, 20f, y, 180f, StepperSize);
        SettingsRows.Add(CreateStepper(panel, smaller, "-", y));
        var value = AddText(panel, string.Empty, 19, Color.white, FguiAlign.Center, 0f, y, StepperValueWidth, StepperSize);
        SettingsRows.Add(value);
        SettingsRows.Add(CreateStepper(panel, larger, "+", y));
        return value;
    }

    private static FguiNode CreateStepper(FguiNode panel, ChatControl control, string glyph, float y)
    {
        var button = AddButton(panel, control, 0f, y, StepperSize, StepperSize);
        SettingsBackgrounds[control] = AddGraph(button, 0f, 0f, StepperSize, StepperSize, HeaderButtonColor, 10f);
        AddText(button, glyph, 26, Color.white, FguiAlign.Center, 0f, 0f, StepperSize, StepperSize);
        return button;
    }

    private static void LayoutSettingsPanel(float windowWidth, float viewportHeight)
    {
        if (_settingsPanel == null)
            return;
        var width = ChatLayout.ReadingWidth(windowWidth);
        _settingsPanel.SetSize(width, viewportHeight);
        _settingsPanelBackground?.SetSize(width, viewportHeight);
        // Rows: [-] value [+] for each stepper, then the reset button and its label.
        var plusX = width - 20f - StepperSize;
        var valueX = plusX - StepperValueWidth;
        var minusX = valueX - StepperSize;
        for (var row = 0; row < 2; row++)
        {
            var y = row == 0 ? 62f : 118f;
            SettingsRows[row * 3].SetXY(minusX, y);
            SettingsRows[row * 3 + 1].SetXY(valueX, y);
            SettingsRows[row * 3 + 2].SetXY(plusX, y);
        }
        var resetWidth = width - 40f;
        SettingsRows[6].SetSize(resetWidth, StepperSize);
        SettingsBackgrounds[ChatControl.ResetLayout].SetSize(resetWidth, StepperSize);
        SettingsRows[7].SetSize(resetWidth, StepperSize);
        _settingsHint?.SetSize(resetWidth, 1f);
    }

    private static void RefreshSettingsValues()
    {
        _textScaleValue?.SetText(ChatLayout.Percent(_textScale));
        _opacityValue?.SetText(ChatLayout.Percent(_windowOpacity));
    }

    private static void SetSettingsOpen(bool open)
    {
        _settingsOpen = open;
        _settingsPanel?.SetVisible(open);
        RefreshSettingsButton(_hoveredControl == ChatControl.Settings);
        if (open)
        {
            CancelChatScrollDrags();
            RefreshSettingsValues();
        }
    }

    private static void RefreshSettingsButton(bool hovered) =>
        _settingsBackground?.Fill(hovered || _settingsOpen ? AccentColor : HeaderButtonColor, 10f);

    private static void SetSettingsControlHover(ChatControl control, bool hovered)
    {
        if (SettingsBackgrounds.TryGetValue(control, out var background))
            background.Fill(hovered ? AccentColor : HeaderButtonColor, 10f);
    }

    private static void HandleSettingsControl(ChatControl control, FguiNode root)
    {
        switch (control)
        {
            case ChatControl.TextSmaller:
            case ChatControl.TextLarger:
                var textScale = ChatLayout.StepTextScale(_textScale, control == ChatControl.TextLarger ? 1 : -1);
                if (textScale == _textScale) return;
                _textScale = textScale;
                SaveAppearance();
                // Font sizes are part of each row's render key: rows are rebuilt.
                RenderChatMessages(force: true);
                break;
            case ChatControl.OpacityDown:
            case ChatControl.OpacityUp:
                var opacity = ChatLayout.StepOpacity(_windowOpacity, control == ChatControl.OpacityUp ? 1 : -1);
                if (opacity == _windowOpacity) return;
                _windowOpacity = opacity;
                SaveAppearance();
                ApplyWindowOpacity();
                break;
            case ChatControl.ResetLayout:
                _windowGeometry = ChatWindowGeometry.Default;
                SaveWindowGeometry();
                if (ApplyWindowLayout(root, force: false))
                    RenderChatMessages(force: true);
                break;
        }
        RefreshSettingsValues();
    }

    private static void ResetSettingsPanel()
    {
        SettingsBackgrounds.Clear();
        SettingsRows.Clear();
        _settingsOpen = false;
        _settingsPanel = _settingsPanelBackground = _textScaleValue = _opacityValue = _settingsHint = null;
    }
}
