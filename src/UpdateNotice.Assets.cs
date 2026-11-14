using UnityEngine;

namespace AstralPartyChatPlugin;

internal static partial class UpdateNotice
{
    private static Font? _font;
    private static Font? _ownedFont;

    // The notice can appear on the login screen, before the game has loaded
    // any font the chat window borrows, so it always uses a Windows font.
    private static Font GetFont()
    {
        if (_font != null)
            return _font;

        foreach (var name in new[] { "Malgun Gothic", "맑은 고딕" })
        {
            try { _ownedFont = Font.CreateDynamicFontFromOSFont(name, 24); }
            catch { }
            if (_ownedFont != null)
                break;
        }
        if (_ownedFont == null)
            return _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        // Survive Unity's unused-asset sweep on scene changes; Shutdown destroys it.
        _ownedFont.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return _font = _ownedFont;
    }

    private static void ReleaseFontAsset()
    {
        var owned = _ownedFont;
        _font = _ownedFont = null;
        try
        {
            if (owned != null)
                Object.Destroy(owned);
        }
        catch { }
    }
}
