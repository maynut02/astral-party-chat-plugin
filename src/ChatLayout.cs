using System;

namespace AstralPartyChatPlugin;

/// <summary>Window rectangle in GRoot's scaled UI coordinates.</summary>
internal readonly record struct ChatWindowRect(float X, float Y, float Width, float Height);

/// <summary>
/// Persisted window geometry. The position is kept relative to GRoot's right
/// edge and vertical center, matching the default placement, so resolution and
/// aspect changes keep the window where the player left it.
/// </summary>
internal readonly record struct ChatWindowGeometry(float Width, float Height, float Right, float CenterY)
{
    public static ChatWindowGeometry Default => new(
        ChatLayout.DefaultWidth, ChatLayout.DefaultHeight, ChatLayout.DefaultRight, 0f);

    public bool IsFinite => float.IsFinite(Width) && float.IsFinite(Height)
        && float.IsFinite(Right) && float.IsFinite(CenterY);
}

internal static class ChatLayout
{
    public const float DefaultWidth = 560f;
    public const float DefaultHeight = 700f;
    public const float DefaultRight = 92f;
    public const float MinimumWidth = 420f;
    public const float MinimumHeight = 480f;
    public const float ScreenMargin = 8f;

    public const float SidePadding = 20f;
    public const float MessagesTop = 110f;
    public const float FooterHeight = 88f;
    public const float ScrollbarReserve = 20f;
    public const float InputHeight = 60f;
    public const float SendWidth = 98f;

    public const float MinimumTextScale = 0.8f;
    public const float MaximumTextScale = 1.5f;
    public const float MinimumOpacity = 0.3f;
    public const float MaximumOpacity = 1f;
    public const float SettingStep = 0.1f;

    public static float ReadingWidth(float windowWidth) => windowWidth - SidePadding * 2f;
    public static float ViewportWidth(float windowWidth) => ReadingWidth(windowWidth) - ScrollbarReserve;
    public static float ViewportHeight(float windowHeight) => windowHeight - MessagesTop - FooterHeight;

    /// <summary>Places persisted geometry inside the root, shrinking only when the root is smaller.</summary>
    public static ChatWindowRect Place(ChatWindowGeometry geometry, float rootWidth, float rootHeight)
    {
        if (!geometry.IsFinite) geometry = ChatWindowGeometry.Default;
        var maxWidth = Math.Max(1f, rootWidth - ScreenMargin * 2f);
        var maxHeight = Math.Max(1f, rootHeight - ScreenMargin * 2f);
        var width = Math.Clamp(geometry.Width, Math.Min(MinimumWidth, maxWidth), maxWidth);
        var height = Math.Clamp(geometry.Height, Math.Min(MinimumHeight, maxHeight), maxHeight);
        var x = rootWidth - geometry.Right - width;
        var y = rootHeight * 0.5f + geometry.CenterY - height * 0.5f;
        return new ChatWindowRect(
            Math.Clamp(x, ScreenMargin, Math.Max(ScreenMargin, rootWidth - ScreenMargin - width)),
            Math.Clamp(y, ScreenMargin, Math.Max(ScreenMargin, rootHeight - ScreenMargin - height)),
            width,
            height);
    }

    public static ChatWindowGeometry Capture(ChatWindowRect rect, float rootWidth, float rootHeight) => new(
        rect.Width,
        rect.Height,
        rootWidth - rect.X - rect.Width,
        rect.Y + rect.Height * 0.5f - rootHeight * 0.5f);

    public static ChatWindowGeometry Move(ChatWindowRect start, float dx, float dy, float rootWidth, float rootHeight) =>
        Capture(Place(Capture(start with { X = start.X + dx, Y = start.Y + dy }, rootWidth, rootHeight),
            rootWidth, rootHeight), rootWidth, rootHeight);

    /// <summary>Resizes from the bottom-right corner, keeping the top-left corner in place.</summary>
    public static ChatWindowGeometry Resize(ChatWindowRect start, float dx, float dy, float rootWidth, float rootHeight)
    {
        var width = Math.Max(MinimumWidth, Math.Min(start.Width + dx, rootWidth - ScreenMargin - start.X));
        var height = Math.Max(MinimumHeight, Math.Min(start.Height + dy, rootHeight - ScreenMargin - start.Y));
        return Capture(Place(Capture(start with { Width = width, Height = height }, rootWidth, rootHeight),
            rootWidth, rootHeight), rootWidth, rootHeight);
    }

    public static float ClampTextScale(float value) =>
        Snap(float.IsFinite(value) ? value : 1f, MinimumTextScale, MaximumTextScale);

    public static float ClampOpacity(float value) =>
        Snap(float.IsFinite(value) ? value : 1f, MinimumOpacity, MaximumOpacity);

    public static float StepTextScale(float value, int direction) =>
        ClampTextScale(ClampTextScale(value) + direction * SettingStep);

    public static float StepOpacity(float value, int direction) =>
        ClampOpacity(ClampOpacity(value) + direction * SettingStep);

    public static int FontSize(int baseSize, float textScale) =>
        Math.Max(1, (int)Math.Round(baseSize * ClampTextScale(textScale)));

    public static string Percent(float value) =>
        ((int)Math.Round(value * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    // Settings move in 10% steps; snapping keeps repeated steps free of float drift.
    private static float Snap(float value, float minimum, float maximum) =>
        (float)Math.Round(Math.Clamp(value, minimum, maximum) / SettingStep) * SettingStep;
}
