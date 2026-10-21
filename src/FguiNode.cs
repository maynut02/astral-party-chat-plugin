using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AstralPartyChatPlugin;

internal enum FguiAlign { Left = 0, Center = 1, Right = 2 }
internal enum FguiVerticalAlign { Top = 0, Middle = 1, Bottom = 2 }
internal enum FguiAutoSize { None = 0, Both = 1, Height = 2, Shrink = 3 }

/// <summary>
/// One FairyGUI object. Owned nodes keep a GC handle so IL2CPP cannot collect
/// an object before the game's display tree references it; borrowed nodes are
/// transient views of game objects and must not outlive the current frame.
/// Setters skip unchanged values to avoid redundant interpreter calls.
/// </summary>
internal sealed class FguiNode
{
    private const string GObject = "FairyGUI.GObject";
    private const string Ns = "FairyGUI";
    private static readonly Dictionary<IntPtr, (IntPtr Pointer, nint Handle)> Textures = new();
    private static IntPtr _registeredFont;
    private static nint _registeredFontHandle;
    private nint _handle;
    private float _x = float.NaN, _y = float.NaN, _width = float.NaN, _height = float.NaN;
    private float _scale = float.NaN, _alpha = float.NaN, _rotation = float.NaN;
    private int _visible = -1, _touchable = -1, _opaque = -1, _sortingOrder = int.MinValue;
    private string? _text;
    private IntPtr _texture;
    private Color _color = new(float.NaN, 0f, 0f, 0f);

    private FguiNode(IntPtr pointer, bool owned)
    {
        Pointer = pointer;
        if (owned) _handle = IL2CPP.il2cpp_gchandle_new(pointer, false);
    }

    public IntPtr Pointer { get; }

    public static FguiNode Component(float width, float height) => Create("GComponent", width, height);
    public static FguiNode Graph(float width, float height) => Create("GGraph", width, height);
    public static FguiNode Loader(float width, float height) => Create("GLoader", width, height);
    public static FguiNode Text(float width, float height) => Create("GTextField", width, height);
    public static FguiNode Input(float width, float height) => Create("GTextInput", width, height);

    private static FguiNode Create(string type, float width, float height)
    {
        var node = new FguiNode(FairyGui.New(FairyGui.Class(Ns, type)), owned: true);
        try
        {
            node.SetSize(width, height);
            return node;
        }
        catch
        {
            try { node.Dispose(); }
            catch { }
            throw;
        }
    }

    public static FguiNode Borrow(IntPtr pointer) => new(pointer, owned: false);

    /// <summary>
    /// Keeps a game object alive while it is cached across frames. Release it
    /// with <see cref="ReleaseHandle"/>; never <see cref="Dispose"/> a game object.
    /// </summary>
    public static FguiNode Retain(IntPtr pointer) => new(pointer, owned: true);

    /// <summary>The game's GRoot, or null before FairyGUI has started.</summary>
    public static FguiNode? Root
    {
        get
        {
            if (!FairyGui.TryClass(Ns, "GRoot", out var klass)) return null;
            var root = FairyGui.CallStatic(klass, "get_inst");
            return root == IntPtr.Zero ? null : Borrow(root);
        }
    }

    /// <summary>The topmost touchable object under the pointer, as FairyGUI hit-tests it.</summary>
    public static IntPtr TouchTarget(FguiNode root) => FairyGui.Call(root.Pointer, "get_touchTarget");

    public static IntPtr ParentOf(IntPtr gObject) =>
        gObject == IntPtr.Zero ? IntPtr.Zero : FairyGui.Call(gObject, "get_parent");

    /// <summary>Converts the Unity mouse position to GRoot's scaled UI coordinates.</summary>
    public static Vector2 PointerInRoot(FguiNode root, Vector3 mouse) =>
        FairyGui.Unbox<Vector2>(FairyGui.Call(root.Pointer, "GlobalToLocal", FairyGui.Vector2Type,
            new Vector2(mouse.x, Screen.height - mouse.y)));

    public static void RegisterFont(string alias, Font font)
    {
        var name = FairyGui.InternString(alias);
        var dynamicFont = FairyGui.NewWithObjects(FairyGui.Class(Ns, "DynamicFont"),
            FairyGui.String + ",UnityEngine.Font", name, font.Pointer);
        var handle = IL2CPP.il2cpp_gchandle_new(dynamicFont, false);
        try
        {
            FairyGui.CallStaticObjects(FairyGui.Class(Ns, "FontManager"), "RegisterFont",
                "FairyGUI.BaseFont," + FairyGui.String, dynamicFont, name);
        }
        catch
        {
            DisposeNative(dynamicFont, handle);
            throw;
        }
        var previous = _registeredFont;
        var previousHandle = _registeredFontHandle;
        _registeredFont = dynamicFont;
        _registeredFontHandle = handle;
        // Registering the replacement first preserves the alias. Unregistering
        // the old font here could remove the replacement's alias as well.
        if (previous != IntPtr.Zero)
            DisposeNative(previous, previousHandle);
    }

    public bool IsDisposed => FairyGui.Get<bool>(Pointer, "get_isDisposed");
    public float X => FairyGui.Get<float>(Pointer, "get_x");
    public float Y => FairyGui.Get<float>(Pointer, "get_y");
    public float Width => FairyGui.Get<float>(Pointer, "get_width");
    public float Height => FairyGui.Get<float>(Pointer, "get_height");

    public void AddChild(FguiNode child) =>
        FairyGui.CallObject(Pointer, "AddChild", GObject, child.Pointer);

    public bool IsChildOf(FguiNode parent) => ParentOf(Pointer) == parent.Pointer;

    public void SetXY(float x, float y)
    {
        if (x == _x && y == _y) return;
        FairyGui.Call(Pointer, "SetXY", FairyGui.Float + "," + FairyGui.Float, x, y);
        _x = x;
        _y = y;
    }

    public void SetSize(float width, float height)
    {
        if (width == _width && height == _height) return;
        FairyGui.Call(Pointer, "SetSize", FairyGui.Float + "," + FairyGui.Float, width, height);
        _width = width;
        _height = height;
    }

    public void SetScale(float scale)
    {
        if (scale == _scale) return;
        FairyGui.Call(Pointer, "SetScale", FairyGui.Float + "," + FairyGui.Float, scale, scale);
        _scale = scale;
    }

    public void SetCenterPivot() =>
        FairyGui.Call(Pointer, "SetPivot", FairyGui.Float + "," + FairyGui.Float + "," + FairyGui.Bool, 0.5f, 0.5f, true);

    public void SetVisible(bool visible)
    {
        var value = visible ? 1 : 0;
        if (value == _visible) return;
        FairyGui.Set(Pointer, "set_visible", FairyGui.Bool, visible);
        _visible = value;
    }

    public void SetAlpha(float alpha)
    {
        if (alpha == _alpha) return;
        FairyGui.Set(Pointer, "set_alpha", FairyGui.Float, alpha);
        _alpha = alpha;
    }

    public void SetRotation(float degrees)
    {
        if (degrees == _rotation) return;
        FairyGui.Set(Pointer, "set_rotation", FairyGui.Float, degrees);
        _rotation = degrees;
    }

    public void SetTouchable(bool touchable)
    {
        var value = touchable ? 1 : 0;
        if (value == _touchable) return;
        FairyGui.Set(Pointer, "set_touchable", FairyGui.Bool, touchable);
        _touchable = value;
    }

    /// <summary>An opaque component hit-tests its whole rectangle, blocking game UI beneath it.</summary>
    public void SetOpaque(bool opaque)
    {
        var value = opaque ? 1 : 0;
        if (value == _opaque) return;
        FairyGui.Set(Pointer, "set_opaque", FairyGui.Bool, opaque);
        _opaque = value;
    }

    public void SetSortingOrder(int order)
    {
        if (order == _sortingOrder) return;
        FairyGui.Set(Pointer, "set_sortingOrder", FairyGui.Int, order);
        _sortingOrder = order;
    }

    /// <summary>Clips children to this component's rectangle (FairyGUI OverflowType.Hidden).</summary>
    public void ClipChildren() =>
        FairyGui.Call(Pointer, "SetupOverflow", "FairyGUI.OverflowType", 1);

    public void Fill(Color fill, float radius = 0f, float lineSize = 0f, Color lineColor = default)
    {
        var shape = FairyGui.Call(Pointer, "get_shape");
        if (radius > 0f)
            FairyGui.DrawRoundRect(shape, lineSize, lineColor, fill, radius);
        else
            FairyGui.Call(shape, "DrawRect", FairyGui.Float + "," + FairyGui.ColorType + "," + FairyGui.ColorType,
                lineSize, lineColor, fill);
    }

    public void SetTextStyle(string font, int size, Color color, FguiAlign align,
        FguiVerticalAlign verticalAlign, bool singleLine, FguiAutoSize autoSize)
    {
        FairyGui.Set(Pointer, "set_autoSize", "FairyGUI.AutoSizeType", (int)autoSize);
        FairyGui.Set(Pointer, "set_singleLine", FairyGui.Bool, singleLine);
        FairyGui.Set(Pointer, "set_align", "FairyGUI.AlignType", (int)align);
        FairyGui.Set(Pointer, "set_verticalAlign", "FairyGUI.VertAlignType", (int)verticalAlign);
        SetTextFormat(font, size, color);
    }

    /// <summary>Re-applies font, size and color; FairyGUI resolves the font name again.</summary>
    public void SetTextFormat(string font, int size, Color color)
    {
        var format = FairyGui.Call(Pointer, "get_textFormat");
        FairyGui.WriteStringField(format, "font", font);
        FairyGui.WriteField(format, "size", size);
        FairyGui.WriteField(format, "color", color);
        FairyGui.CallObject(Pointer, "set_textFormat", "FairyGUI.TextFormat", format);
        _color = color;
    }

    /// <summary>Points this text at another registered font, keeping its size and color.</summary>
    public void SetFont(string font)
    {
        var format = FairyGui.Call(Pointer, "get_textFormat");
        FairyGui.WriteStringField(format, "font", font);
        FairyGui.CallObject(Pointer, "set_textFormat", "FairyGUI.TextFormat", format);
    }

    public void SetColor(Color color)
    {
        if (color == _color) return;
        FairyGui.Set(Pointer, "set_color", FairyGui.ColorType, color);
        _color = color;
    }

    public void SetText(string text)
    {
        if (string.Equals(text, _text, StringComparison.Ordinal)) return;
        FairyGui.SetString(Pointer, "set_text", text);
        _text = text;
    }

    /// <summary>Reads committed input text. IME composition is not part of this value.</summary>
    public string GetText()
    {
        var text = FairyGui.GetString(Pointer, "get_text");
        _text = text;
        return text;
    }

    public void SetPrompt(string prompt) => FairyGui.SetString(Pointer, "set_promptText", prompt);

    public void SetMaxLength(int length) => FairyGui.Set(Pointer, "set_maxLength", FairyGui.Int, length);

    public bool Focused => FairyGui.Get<bool>(Pointer, "get_focused");

    public void Focus() => FairyGui.Call(Pointer, "RequestFocus");

    /// <summary>Clears FairyGUI focus when this node holds it.</summary>
    public void Blur()
    {
        if (!Focused) return;
        var stage = FairyGui.CallStatic(FairyGui.Class(Ns, "Stage"), "get_inst");
        FairyGui.CallObject(stage, "set_focus", "FairyGUI.DisplayObject", IntPtr.Zero);
    }

    public void SetTexture(Texture? texture)
    {
        var pointer = texture == null ? IntPtr.Zero : texture.Pointer;
        if (pointer == _texture) return;
        var native = pointer == IntPtr.Zero ? IntPtr.Zero : GetNativeTexture(pointer);
        FairyGui.CallObject(Pointer, "set_texture", "FairyGUI.NTexture", native);
        _texture = pointer;
    }

    private static IntPtr GetNativeTexture(IntPtr texture)
    {
        if (Textures.TryGetValue(texture, out var cached))
            return cached.Pointer;
        var native = FairyGui.NewWithObject(FairyGui.Class(Ns, "NTexture"), "UnityEngine.Texture", texture);
        var handle = IL2CPP.il2cpp_gchandle_new(native, false);
        try
        {
            // NTexture owns its materials, never the Unity texture supplied by
            // the plugin. Its constructor otherwise defaults to Destroy (0).
            FairyGui.WriteField(native, "destroyMethod", 2);
            Textures.Add(texture, (native, handle));
            return native;
        }
        catch
        {
            // Do not Dispose if setting None failed: the default destroys the
            // Unity texture, which this wrapper must never own.
            IL2CPP.il2cpp_gchandle_free(handle);
            throw;
        }
    }

    public static void ReleaseTexture(Texture texture)
    {
        if (!Textures.TryGetValue(texture.Pointer, out var cached))
            return;
        Textures.Remove(texture.Pointer);
        DisposeNative(cached.Pointer, cached.Handle);
    }

    public static void ShutdownResources()
    {
        foreach (var texture in Textures.Values)
        {
            try { DisposeNative(texture.Pointer, texture.Handle); }
            catch { }
        }
        Textures.Clear();
        var font = _registeredFont;
        var handle = _registeredFontHandle;
        _registeredFont = IntPtr.Zero;
        _registeredFontHandle = 0;
        if (font == IntPtr.Zero)
            return;
        try
        {
            FairyGui.CallStaticObject(FairyGui.Class(Ns, "FontManager"), "UnregisterFont", "FairyGUI.BaseFont", font);
        }
        finally { DisposeNative(font, handle); }
    }

    private static void DisposeNative(IntPtr pointer, nint handle)
    {
        try { FairyGui.Call(pointer, "Dispose"); }
        finally { IL2CPP.il2cpp_gchandle_free(handle); }
    }

    /// <summary>Stretches loader content to the loader size (FairyGUI FillType.ScaleFree).</summary>
    public void StretchContent() => FairyGui.Set(Pointer, "set_fill", "FairyGUI.FillType", 4);

    public void Dispose()
    {
        try
        {
            if (!IsDisposed) FairyGui.Call(Pointer, "Dispose");
        }
        finally { ReleaseHandle(); }
    }

    /// <summary>Drops this wrapper's GC handle after the game disposed the object.</summary>
    public void ReleaseHandle()
    {
        if (_handle == 0) return;
        IL2CPP.il2cpp_gchandle_free(_handle);
        _handle = 0;
    }
}
