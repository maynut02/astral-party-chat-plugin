using System.Reflection;
using UnityEngine;

namespace UnityEngine
{
    public readonly record struct Vector2(float x, float y)
    {
        public static Vector2 zero => default;
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
    }
    public readonly record struct Vector3(float x, float y, float z = 0);
    public readonly record struct Color(float r, float g, float b, float a = 1)
    {
        public static Color white => new(1, 1, 1, 1);
    }
    public readonly record struct Color32(byte r, byte g, byte b, byte a)
    {
        public static implicit operator Color(Color32 c) => new(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
    }
    public class Texture { }
    public class Texture2D : Texture { }
    public class Font { }
    public static class Time
    {
        public static int frameCount { get; set; }
        public static float unscaledTime { get; set; }
    }
    public static class Screen
    {
        public static int width => 1920;
        public static int height => 1080;
    }
    public enum KeyCode
    {
        None, Return, KeypadEnter, Escape, A, Space,
        LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt, LeftCommand, RightCommand
    }
    public enum EventType { KeyDown, KeyUp, MouseDown, MouseUp, ScrollWheel, Layout, Repaint, Ignore, Used }
    [Flags] public enum EventModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Command = 8 }
    public sealed class Event
    {
        public static Event? current { get; set; }
        public EventType rawType { get; set; }
        public EventType type { get; set; }
        public KeyCode keyCode { get; set; }
        public char character { get; set; }
        public EventModifiers modifiers { get; set; }
    }
    public static class Input
    {
        internal static bool Pressed, Held, RightPressed, MiddlePressed;
        internal static KeyCode Down;
        internal static readonly HashSet<KeyCode> HeldKeys = new();
        public static Vector3 mousePosition { get; set; }
        public static Vector2 mouseScrollDelta { get; set; }
        public static string compositionString { get; set; } = string.Empty;
        public static string inputString { get; set; } = string.Empty;
        public static bool anyKey => HeldKeys.Count > 0 || Down != KeyCode.None;
        public static bool anyKeyDown => Down != KeyCode.None;
        public static bool GetKey(KeyCode key) => HeldKeys.Contains(key);
        public static bool GetKey(string key) => false;
        public static bool GetKeyDown(KeyCode key) => Down == key;
        public static bool GetKeyDown(string key) => false;
        public static bool GetKeyUp(KeyCode key) => false;
        public static bool GetKeyUp(string key) => false;
        public static bool GetButton(string button) => false;
        public static bool GetButtonDown(string button) => false;
        public static bool GetButtonUp(string button) => false;
        public static bool GetMouseButtonDown(int button) => button switch
        {
            0 => Pressed,
            1 => RightPressed,
            2 => MiddlePressed,
            _ => false
        };
        public static bool GetMouseButton(int button) => button == 0 && Held;
        public static float GetAxis(string name) => 0;
        public static float GetAxisRaw(string name) => 0;
        internal static void Reset()
        {
            Pressed = Held = RightPressed = MiddlePressed = false;
            Down = KeyCode.None;
            HeldKeys.Clear();
            mousePosition = default;
            mouseScrollDelta = default;
            compositionString = inputString = string.Empty;
            Event.current = null;
        }
    }
    public static class PlayerPrefs
    {
        private static readonly Dictionary<string, float> Values = new();
        public static bool HasKey(string key) => Values.ContainsKey(key);
        public static float GetFloat(string key, float fallback = 0) => Values.TryGetValue(key, out var value) ? value : fallback;
        public static void SetFloat(string key, float value) => Values[key] = value;
        public static void DeleteKey(string key) => Values.Remove(key);
        public static void DeleteAll() => Values.Clear();
        public static void Save() { }
    }
}

namespace UnityEngine.EventSystems
{
    public sealed class StandaloneInputModule
    {
        private bool SendSubmitEventToSelectedObject() => true;
    }
}

namespace HarmonyLib
{
    public sealed class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method) => Method = method;
        public MethodInfo Method { get; }
    }
    public sealed class Harmony
    {
        public string? FailMethodName { get; set; }
        public readonly List<(MethodInfo Method, HarmonyMethod? Prefix, HarmonyMethod? Postfix)> Patches = new();
        public void Patch(MethodInfo original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
        {
            if (original.Name == FailMethodName) throw new InvalidOperationException("Optional route unavailable");
            Patches.Add((original, prefix, postfix));
        }
    }
}

namespace AstralPartyChatPlugin
{
    internal enum FguiAlign { Left, Center, Right }
    internal enum FguiVerticalAlign { Top, Middle, Bottom }
    internal enum FguiAutoSize { None, Both, Height, Shrink }

    // A deterministic display tree, not a second overlay implementation. Layout,
    // reconciliation and input transitions are executed by the linked src files.
    internal sealed class FguiNode
    {
        private static long _nextPointer;
        private static readonly Dictionary<IntPtr, FguiNode> Nodes = new();
        private static FguiNode? _focused;
        private float _height;
        private string _text = string.Empty;
        private FguiAutoSize _autoSize;
        private FguiNode(float width, float height)
        {
            Pointer = new IntPtr(++_nextPointer);
            Nodes.Add(Pointer, this);
            SetSize(width, height);
        }
        public static FguiNode Component(float width, float height) => new(width, height);
        public static FguiNode Graph(float width, float height) => new(width, height);
        public static FguiNode Loader(float width, float height) => new(width, height);
        public static FguiNode Text(float width, float height) => new(width, height);
        public static FguiNode Input(float width, float height) => new(width, height);
        public static FguiNode Borrow(IntPtr pointer) => Nodes[pointer];
        public static FguiNode? Root { get; internal set; }
        internal static FguiNode? Target { get; set; }
        internal static Vector2 PointerPosition { get; set; }
        public static IntPtr TouchTarget(FguiNode root) => Target?.Pointer ?? IntPtr.Zero;
        public static IntPtr ParentOf(IntPtr pointer) => Nodes.TryGetValue(pointer, out var node) ? node.Parent?.Pointer ?? IntPtr.Zero : IntPtr.Zero;
        public static Vector2 PointerInRoot(FguiNode root, Vector3 mouse) => PointerPosition;
        public static void RegisterFont(string alias, Font font) { }
        public IntPtr Pointer { get; }
        public FguiNode? Parent { get; private set; }
        public List<FguiNode> Children { get; } = new();
        public bool IsDisposed { get; private set; }
        public bool HandleReleased { get; private set; }
        internal static int LiveHandles => Nodes.Values.Count(node => !node.HandleReleased);
        internal static string? FaultText { get; set; }
        internal bool ThrowAfterDispose { get; set; }
        internal int TextReads { get; private set; }
        internal FguiAutoSize AutoSize => _autoSize;
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Width { get; private set; }
        public float Height => _autoSize == FguiAutoSize.Height ? MeasuredHeight() : _height;
        public bool Visible { get; private set; } = true;
        public bool Touchable { get; private set; } = true;
        public bool Opaque { get; private set; }
        public bool Clipped { get; private set; }
        public float Scale { get; private set; } = 1;
        public float Alpha { get; private set; } = 1;
        public float Rotation { get; private set; }
        public int SortingOrder { get; private set; }
        public int FontSize { get; private set; } = 14;
        public string FontName { get; private set; } = string.Empty;
        public Color FillColor { get; private set; }
        public Color TextColor { get; private set; }
        public Texture? Texture { get; private set; }
        public string Prompt { get; private set; } = string.Empty;
        public int MaxLength { get; private set; }
        public void AddChild(FguiNode child)
        {
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
        }
        public bool IsChildOf(FguiNode parent) => Parent == parent;
        public void SetXY(float x, float y) { RequireHandle(); X = x; Y = y; }
        public void SetSize(float width, float height) { RequireHandle(); Width = width; _height = height; }
        public void SetScale(float scale) => Scale = scale;
        public void SetCenterPivot() { }
        public void SetVisible(bool visible) { RequireHandle(); Visible = visible; }
        public void SetAlpha(float alpha) => Alpha = alpha;
        public void SetRotation(float rotation) => Rotation = rotation;
        public void SetTouchable(bool touchable) => Touchable = touchable;
        public void SetOpaque(bool opaque) => Opaque = opaque;
        public void SetSortingOrder(int order) => SortingOrder = order;
        public void ClipChildren() => Clipped = true;
        public void Fill(Color fill, float radius = 0, float lineSize = 0, Color lineColor = default)
        { RequireHandle(); FillColor = fill; }
        public void SetTextStyle(string font, int size, Color color, FguiAlign align,
            FguiVerticalAlign verticalAlign, bool singleLine, FguiAutoSize autoSize)
        {
            _autoSize = autoSize;
            SetTextFormat(font, size, color);
        }
        public void SetTextFormat(string font, int size, Color color) { FontName = font; FontSize = size; TextColor = color; }
        public void SetFont(string font) => FontName = font;
        public void SetColor(Color color) => TextColor = color;
        public void SetText(string text)
        {
            if (FaultText != null && string.Equals(text, FaultText, StringComparison.Ordinal))
                throw new InvalidOperationException("Injected text failure.");
            _text = text;
        }
        public string GetText() { RequireHandle(); TextReads++; return _text; }
        public void SetPrompt(string prompt) => Prompt = prompt;
        public void SetMaxLength(int length) => MaxLength = length;
        public bool Focused => _focused == this && !IsDisposed;
        public void Focus() => _focused = this;
        public void Blur() { if (_focused == this) _focused = null; }
        public void SetTexture(Texture? texture) => Texture = texture;
        public void StretchContent() { }
        public void Dispose()
        {
            try
            {
                NativeDispose();
                if (ThrowAfterDispose) throw new InvalidOperationException("Injected native disposal failure.");
            }
            finally { ReleaseHandle(); }
        }
        private void NativeDispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Blur();
            // FairyGUI can dispose native children, not the handles owned by
            // independent CLR wrappers retained by the plugin.
            foreach (var child in Children.ToArray()) child.NativeDispose();
            Parent?.Children.Remove(this);
            Parent = null;
        }
        public void ReleaseHandle() => HandleReleased = true;
        private void RequireHandle()
        {
            if (HandleReleased) throw new InvalidOperationException("Node accessed after ReleaseHandle.");
        }
        private float MeasuredHeight()
        {
            var columns = Math.Max(1, (int)(Width / Math.Max(1f, FontSize * 0.55f)));
            var lines = _text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.EnumerateRunes().Count() / (double)columns)));
            return lines * FontSize * 1.25f;
        }
        internal static void ResetTree()
        {
            Nodes.Clear();
            _focused = Target = Root = null;
            PointerPosition = default;
            FaultText = null;
        }
    }

    internal sealed class ChatSnapshot
    {
        public string ScreenPhase { get; set; } = "방";
        public bool ChatAvailable { get; set; } = true;
        public string RoomId { get; set; } = "room-a";
        public bool ChatAvailableOnScreen { get; set; } = true;
        public bool ChatButtonVisible { get; set; } = true;
        public FguiNode? ChatButtonHost { get; set; }
        public Vector2 ChatButtonPosition { get; set; } = new(20, 20);
        public float ChatButtonSize { get; set; } = 72;
    }

    internal static partial class ChatOverlay
    {
        internal static readonly Dictionary<string, Texture> CharacterTextures = new(StringComparer.Ordinal);
        internal static readonly List<string> CharacterImageRequests = new();
        private const string ChatFontName = "HarnessFont";
        private static void EnsureAssetsRunning() { }
        private static void ShutdownAssets() { CharacterTextures.Clear(); CharacterImageRequests.Clear(); }
        private static void RefreshChatFont() { }
        private static void RefreshGameKoreanFont() { }
        private static void ProcessCharacterImageDownloads() { }
        private static Texture? GetChatIconTexture() => null;
        private static void RequestCharacterImage(string id) => CharacterImageRequests.Add(id);
        private static bool TryGetCharacterTexture(string id, out Texture? texture) => CharacterTextures.TryGetValue(id, out texture);
        internal static void DeliverPortrait(string id, Texture texture) => RefreshRenderedCharacterImage(id, texture);
        internal static FguiNode TestRoot { get; private set; } = null!;
        internal static ChatSnapshot TestSnapshot { get; private set; } = null!;
        internal static void ResetHarness(bool open = true)
        {
            Shutdown();
            Input.Reset();
            PlayerPrefs.DeleteAll();
            Time.frameCount = 100;
            Time.unscaledTime = 10;
            FguiNode.ResetTree();
            TestRoot = FguiNode.Component(1920, 1080);
            FguiNode.Root = TestRoot;
            TestSnapshot = new ChatSnapshot { ChatButtonHost = TestRoot };
            Refresh(TestSnapshot, TestRoot);
            if (open) { Click("Button"); Tick(); }
        }
        internal static void Tick(FguiNode? target = null, Vector2? pointer = null,
            bool pressed = false, bool held = false, float wheel = 0, KeyCode key = KeyCode.None,
            int mouseButton = 0)
        {
            Time.frameCount++;
            Time.unscaledTime += 1f / 60;
            FguiNode.Target = target;
            if (pointer.HasValue) FguiNode.PointerPosition = pointer.Value;
            Input.mousePosition = new Vector3(FguiNode.PointerPosition.x, Screen.height - FguiNode.PointerPosition.y);
            Input.Pressed = pressed && mouseButton == 0;
            Input.RightPressed = pressed && mouseButton == 1;
            Input.MiddlePressed = pressed && mouseButton == 2;
            Input.Held = held;
            Input.Down = key;
            Input.mouseScrollDelta = new Vector2(0, wheel);
            Refresh(TestSnapshot, TestRoot);
            Input.Pressed = Input.RightPressed = Input.MiddlePressed = false;
            Input.Down = KeyCode.None;
            Input.mouseScrollDelta = default;
        }
        internal static FguiNode Node(string control)
        {
            var value = Enum.Parse<ChatControl>(control);
            return FguiNode.Borrow(Controls.Single(pair => pair.Value == value).Key);
        }
        internal static Vector2 GlobalPosition(FguiNode node)
        {
            var position = new Vector2(node.X, node.Y);
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
                position += new Vector2(parent.X, parent.Y);
            return position;
        }
        internal static void Click(string control)
        {
            var node = Node(control);
            Tick(node, GlobalPosition(node) + new Vector2(node.Width / 2, node.Height / 2), pressed: true);
        }
        internal static void SetDraft(string text) => _chatInput!.SetText(text);
        internal static string GetDraft() => _chatInput!.GetText();
        internal static string Status => _chatStatusText!.GetText();
        internal static bool WindowOpen => _chatWindowOpen;
        internal static ChatWindowRect WindowRect => _windowRect;
        internal static float TextScale => _textScale;
        internal static float WindowOpacity => _windowOpacity;
        internal static FguiNode WindowBackground => _windowBackground!;
        internal static float ScrollTop { get => _chatScrollTop; set => SetChatScrollTop(value); }
        internal static float Scrollable => GetChatScrollableHeight();
        internal static float ContentHeight => _chatContentHeight;
        internal static float ViewportHeight => _chatViewportHeight;
        internal static float ViewportWidth => _chatViewportWidth;
        internal static float ContentY => _chatContent!.Y;
        internal static int Unread => UnreadChatMessageIdentities.Count;
        internal static bool ScrollbarDragging => _chatScrollbarDragging;
        internal static bool ViewportDragging => _chatViewportDragging;
        internal static FguiNode ScrollThumb => _scrollThumb!;
        internal static FguiNode[] InspectChatTexts() => _chatTexts.ToArray();
        internal readonly record struct RowSnapshot(string Identity, string RenderKey, FguiNode Root,
            FguiNode Body, FguiNode? Participant, FguiNode? Sender, FguiNode? Portrait,
            float Width, float Height, FguiNode[] Texts);
        internal static RowSnapshot[] InspectRows() => RenderedChatRows.Select(row => new RowSnapshot(
            row.Identity, row.RenderKey, row.Root, row.Body, row.Participant, row.Sender,
            row.Portrait, row.Width, row.Height, row.Texts.ToArray())).ToArray();
    }
}
