#if OVERLAY_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using AstralPartyChatPlugin;

namespace UnityEngine
{
    public class Object
    {
        public bool IsDestroyed { get; private set; }
        public static int DestroyCallCount { get; private set; }

        public static void Destroy(Object target)
        {
            DestroyCallCount++;
            if (target is GameObject gameObject)
                gameObject.DestroyHierarchy();
            else
                target.IsDestroyed = true;
        }

        internal void MarkDestroyed() => IsDestroyed = true;
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform => gameObject.transform;
    }

    public class Transform : Component
    {
        private readonly List<Transform> _children = new();
        private Transform? _parent;

        public Transform? parent => _parent;
        public IReadOnlyList<Transform> children => _children;
        public Vector2 anchoredPosition { get; set; }

        public void SetParent(Transform? parent, bool worldPositionStays)
        {
            _parent?._children.Remove(this);
            _parent = parent;
            if (parent != null && !parent._children.Contains(this))
                parent._children.Add(this);
        }

        public void SetSiblingIndex(int index)
        {
            if (_parent == null)
                return;

            _parent._children.Remove(this);
            _parent._children.Insert(Math.Clamp(index, 0, _parent._children.Count), this);
        }

        public void SetAsLastSibling() => SetSiblingIndex(_parent?._children.Count ?? 0);
    }

    public sealed class RectTransform : Transform
    {
        public Vector2 anchorMin { get; set; }
        public Vector2 anchorMax { get; set; }
        public Vector2 pivot { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Rect rect => new(-pivot.x * sizeDelta.x, -pivot.y * sizeDelta.y, sizeDelta.x, sizeDelta.y);
    }

    public sealed class GameObject : Object
    {
        private readonly List<Component> _components = new();

        public GameObject(string name, params Type[] componentTypes)
        {
            this.name = name;
            CreatedCount++;
            transform = new RectTransform { gameObject = this };
            _components.Add(transform);

            foreach (var type in componentTypes)
            {
                if (type == typeof(Transform) || type == typeof(RectTransform))
                    continue;
                AddComponent(type);
            }
        }

        public static int CreatedCount { get; private set; }
        public string name { get; }
        public Transform transform { get; }
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);

        public T GetComponent<T>() where T : Component =>
            _components.OfType<T>().FirstOrDefault()!;

        public T AddComponent<T>() where T : Component, new()
        {
            var existing = GetComponent<T>();
            if (existing != null)
                return existing;

            var component = new T { gameObject = this };
            _components.Add(component);
            return component;
        }

        private Component AddComponent(Type type)
        {
            var existing = _components.FirstOrDefault(type.IsInstanceOfType);
            if (existing != null)
                return existing;

            var component = (Component)Activator.CreateInstance(type)!;
            component.gameObject = this;
            _components.Add(component);
            return component;
        }

        public void SetActive(bool active) => activeSelf = active;

        internal void DestroyHierarchy()
        {
            activeSelf = false;
            foreach (var child in transform.children.ToArray())
                child.gameObject.DestroyHierarchy();
            transform.SetParent(null, false);
            foreach (var component in _components)
                component.MarkDestroyed();
            MarkDestroyed();
        }
    }

    public struct Vector2
    {
        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public float x;
        public float y;
        public static Vector2 zero => new(0f, 0f);
        public static Vector2 operator +(Vector2 left, Vector2 right) => new(left.x + right.x, left.y + right.y);
        public static Vector2 operator -(Vector2 left, Vector2 right) => new(left.x - right.x, left.y - right.y);
    }

    public readonly struct Rect
    {
        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }

        public float x { get; }
        public float y { get; }
        public float width { get; }
        public float height { get; }
        public float yMax => y + height;
    }

    public readonly struct Color
    {
        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public float r { get; }
        public float g { get; }
        public float b { get; }
        public float a { get; }
        public static Color white => new(1f, 1f, 1f, 1f);
        public static implicit operator Color(Color32 value) =>
            new(value.r / 255f, value.g / 255f, value.b / 255f, value.a / 255f);
    }

    public readonly struct Color32
    {
        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public byte r { get; }
        public byte g { get; }
        public byte b { get; }
        public byte a { get; }
    }

    public class Texture : Object
    {
        public int width { get; set; } = 64;
        public int height { get; set; } = 64;
    }

    public sealed class Sprite : Object
    {
        public Sprite(Texture texture) => this.texture = texture;
        public Texture texture { get; }
    }

    public enum TextAnchor { UpperLeft, MiddleLeft, MiddleRight, MiddleCenter }
    public enum FontStyle { Normal, Bold }
    public enum HorizontalWrapMode { Wrap, Overflow }
    public enum VerticalWrapMode { Truncate, Overflow }

    public static class Mathf
    {
        public static float Clamp(float value, float min, float max) =>
            Math.Max(min, Math.Min(max, value));

        public static float Clamp01(float value) => Clamp(value, 0f, 1f);
    }

    public static class Canvas
    {
        public static int ForceUpdateCount { get; private set; }
        public static void ForceUpdateCanvases() => ForceUpdateCount++;
    }

    public static class Time
    {
        public static float unscaledTime { get; set; }
        public static int frameCount { get; set; }
    }

    public enum KeyCode { None, Return, KeypadEnter, Escape }
    public enum EventType { KeyDown, KeyUp, Layout, Repaint, MouseDown, ScrollWheel, Ignore, Used }
    public enum EventModifiers { None, Shift }

    public sealed class Event
    {
        public KeyCode keyCode { get; set; }
        public char character { get; set; }
        public EventModifiers modifiers { get; set; }
        public EventType rawType { get; set; }
        public EventType type { get => rawType; set => rawType = value; }
        public static Event? current { get; set; }
        public static Event? NativeEventForTest { get; set; }
        public void CopyFromPtr(IntPtr nativeEventPtr)
        {
            if (nativeEventPtr == IntPtr.Zero || NativeEventForTest == null)
                throw new InvalidOperationException("Missing simulated native GUI event.");
            rawType = NativeEventForTest.rawType;
            keyCode = NativeEventForTest.keyCode;
        }
        public static bool PopEvent(Event keyEvent) => false;
    }

    public static class GUIUtility
    {
        public static void ProcessEvent(int instanceId, IntPtr nativeEventPtr, out bool result) => result = false;
    }

    public static class Input
    {
        public static Vector2 mousePosition { get; set; }
        public static Vector2 mouseScrollDelta { get; set; }
        public static string compositionString { get; set; } = string.Empty;
        public static string inputString => string.Empty;
        public static KeyCode? PressedKey { get; set; }
        public static bool MouseButtonHeld { get; set; }
        public static bool BlockedMouseReadObserved { get; private set; }
        public static int ResetInputAxesCallCount { get; private set; }
        public static bool GetMouseButton(int button)
        {
            if (ChatOverlay.ShouldBlockRawMouseInput())
            {
                BlockedMouseReadObserved = true;
                return false;
            }
            return button == 0 && MouseButtonHeld;
        }
        public static bool GetKeyDown(KeyCode key) =>
            !ChatOverlay.ShouldBlockGameKeyboardInput() && PressedKey == key;
        public static float GetAxis(string name) => 0f;
        public static float GetAxisRaw(string name) => 0f;
        public static void ResetInputAxes()
        {
            ResetInputAxesCallCount++;
            mouseScrollDelta = Vector2.zero;
            MouseButtonHeld = false;
        }

        internal static void ResetHarness()
        {
            mousePosition = Vector2.zero;
            mouseScrollDelta = Vector2.zero;
            compositionString = string.Empty;
            PressedKey = null;
            MouseButtonHeld = false;
            BlockedMouseReadObserved = false;
            ResetInputAxesCallCount = 0;
            Time.frameCount = 0;
            Time.unscaledTime = 0f;
            RectTransformUtility.HitRect = null;
        }
    }

    public static class RectTransformUtility
    {
        // Hit testing is supplied by the test; this harness does not render UI.
        public static RectTransform? HitRect { get; set; }
        public static bool RectangleContainsScreenPoint(RectTransform rect, Vector2 point, object? camera) =>
            ReferenceEquals(rect, HitRect);
        public static bool ScreenPointToLocalPointInRectangle(
            RectTransform rect, Vector2 point, object? camera, out Vector2 localPoint)
        {
            localPoint = point;
            return true;
        }
    }

    public static class Screen
    {
        public static int width => 1920;
        public static int height => 1080;
    }

    public static class PlayerPrefs
    {
        public static void SetFloat(string key, float value) { }
        public static void Save() { }
    }
}

namespace UnityEngine.EventSystems
{
    public sealed class StandaloneInputModule
    {
        public bool SendSubmitEventToSelectedObject() => true;
    }
    public sealed class EventSystem
    {
        public static EventSystem? current { get; set; }
        public int pixelDragThreshold { get; set; } = 5;
        public UnityEngine.GameObject? currentSelectedGameObject { get; private set; }
        public void SetSelectedGameObject(UnityEngine.GameObject? gameObject) => currentSelectedGameObject = gameObject;
    }
    public sealed class PointerEventData
    {
        public enum InputButton { Left, Right, Middle }
        public PointerEventData(EventSystem eventSystem) { }
        public InputButton button { get; set; }
        public UnityEngine.Vector2 position { get; set; }
        public UnityEngine.Vector2 pressPosition { get; set; }
        public UnityEngine.Vector2 delta { get; set; }
        public UnityEngine.GameObject? pointerPress { get; set; }
        public UnityEngine.GameObject? pointerDrag { get; set; }
        public bool useDragThreshold { get; set; }
        public bool dragging { get; set; }
    }
}

namespace UnityEngine.UI
{
    using UnityEngine;

    public sealed class Text : Component
    {
        public static int PreferredHeightReadCount { get; private set; }
        public string text { get; set; } = string.Empty;
        public bool enabled { get; set; } = true;
        public int fontSize { get; set; } = 14;
        public Color color { get; set; }
        public TextAnchor alignment { get; set; }
        public FontStyle fontStyle { get; set; }
        public bool supportRichText { get; set; }
        public HorizontalWrapMode horizontalOverflow { get; set; }
        public VerticalWrapMode verticalOverflow { get; set; }
        public RectTransform rectTransform => (RectTransform)transform;

        public float preferredHeight
        {
            get
            {
                PreferredHeightReadCount++;
                var charsPerLine = Math.Max(
                    1,
                    (int)(rectTransform.rect.width / (Math.Max(1, fontSize) * 0.5f)));
                var lines = 0;
                foreach (var line in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
                {
                    var length = Math.Max(1, line.Length);
                    lines += Math.Max(1, (int)Math.Ceiling(length / (double)charsPerLine));
                }

                return lines * Math.Max(1, fontSize) * 1.2f;
            }
        }
    }

    public sealed class RawImage : Component
    {
        public Texture? texture { get; set; }
        public Rect uvRect { get; set; }
        public Color color { get; set; }
        public bool raycastTarget { get; set; }
    }

    public sealed class ScrollRect
    {
        public float verticalNormalizedPosition { get; set; }
        public float scrollSensitivity { get; set; }
        public int StopMovementCount { get; private set; }
        public void StopMovement() => StopMovementCount++;
    }

    public sealed class Image : Component
    {
        public Color color { get; set; }
        public bool raycastTarget { get; set; }
    }

    public sealed class InputField : Component
    {
        public enum EditState { Continue, Finish }
        private string _text = string.Empty;
        public int TextWriteCount { get; private set; }
        public string text
        {
            get => _text;
            set { _text = value; TextWriteCount++; }
        }
        public bool isFocused { get; private set; }
        public int selectionAnchorPosition { get; private set; }
        public int selectionFocusPosition { get; private set; }
        public int PointerDownCount { get; private set; }
        public int BeginDragCount { get; private set; }
        public int DragCount { get; private set; }
        public int EndDragCount { get; private set; }
        public int PointerUpCount { get; private set; }
        public bool PointerReadScopeObserved { get; private set; }
        public bool ThrowOnPointerDown { get; set; }
        public void ActivateInputField() => isFocused = true;
        public void DeactivateInputField() => isFocused = false;
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData data)
        {
            PointerDownCount++;
            PointerReadScopeObserved = !ChatOverlay.ShouldBlockGameKeyboardInput();
            if (ThrowOnPointerDown) throw new InvalidOperationException("Simulated field pointer failure.");
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(gameObject);
            if (isFocused)
                selectionAnchorPosition = selectionFocusPosition = CaretAt(data.position);
        }
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData data) => ActivateInputField();
        public void OnBeginDrag(UnityEngine.EventSystems.PointerEventData data) => BeginDragCount++;
        public void OnDrag(UnityEngine.EventSystems.PointerEventData data)
        {
            DragCount++;
            selectionFocusPosition = CaretAt(data.position);
        }
        public void OnEndDrag(UnityEngine.EventSystems.PointerEventData data) => EndDragCount++;
        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData data) => PointerUpCount++;
        private int CaretAt(Vector2 pointer) => Math.Clamp((int)pointer.x, 0, text.Length);
        public EditState KeyPressed(Event keyEvent) => EditState.Finish;
        public void OnUpdateSelected() { }
        public void LateUpdate() { }
    }
}

namespace AstralPartyChatPlugin
{
    using UnityEngine;
    using UnityEngine.UI;

    internal sealed class ChatSnapshot
    {
        public string RoomId { get; set; } = string.Empty;
        public string ScreenPhase { get; set; } = string.Empty;
        public bool ChatButtonVisible { get; set; }
    }


    internal static partial class ChatOverlay
    {
        private static List<Text> _chatTexts = new();
        private static RectTransform _chatContentRect = NewRect("HarnessContent", ChatMessageViewportWidth, 0f);
        private static RectTransform _chatViewportRect = NewRect("HarnessViewport", ChatMessageViewportWidth, 502f);
        private static ScrollRect? _chatScrollRect = new();
        private static GameObject _root = NewRect("HarnessRoot", 1920f, 1080f).gameObject;
        private static GameObject? _chatButton = null;
        private static RectTransform? _chatButtonRect = null;
        private static GameObject _chatWindow = NewRect("HarnessWindow", 560f, 700f).gameObject;
        private static RectTransform _chatWindowRect = _chatWindow.GetComponent<RectTransform>();
        private static RectTransform? _chatHeaderRect = null;
        private static RectTransform? _chatCloseRect = null;
        private static RectTransform? _chatInputRect = null;
        private static RectTransform _chatSendRect = NewRect("HarnessSend", 98f, 60f);
        private static Image? _chatCloseBackground = null;
        private static Text? _chatTitleText = null;
        private static Text _chatRoomText = new GameObject("HarnessStatus").AddComponent<Text>();
        private static InputField _chatInputField = new GameObject("HarnessInput").AddComponent<InputField>();
        private static string _chatStatus = "연결됨";
        private const string ChatWindowXPref = "Harness.WindowX";
        private const string ChatWindowYPref = "Harness.WindowY";
        private static readonly Dictionary<string, Sprite> CdnSprites = new(StringComparer.Ordinal);
        private static readonly List<string> CharacterImageRequests = new();

        internal readonly record struct RowSnapshot(
            GameObject Root,
            string Identity,
            string RenderKey,
            IReadOnlyList<Text> Texts,
            Text? Body,
            RawImage? Portrait,
            float Height);

        private static RectTransform NewRect(string name, float width, float height)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            var rect = gameObject.GetComponent<RectTransform>()!;
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private static GameObject CreateFlatImage(
            Transform parent,
            string name,
            Color32 color,
            bool raycastTarget)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            var image = gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycastTarget;
            return gameObject;
        }

        private static Text CreateUiText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            Color color,
            TextAnchor alignment,
            Vector2 topLeft,
            Vector2 size)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            var text = gameObject.AddComponent<Text>();
            text.text = value;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.fontStyle = FontStyle.Normal;
            SetTopLeftRect(text.rectTransform, topLeft, size);
            _chatTexts.Add(text);
            return text;
        }

        private static void SetTopLeftRect(
            RectTransform? rect,
            Vector2 topLeft,
            Vector2 size)
        {
            if (rect == null)
                return;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
            rect.sizeDelta = size;
        }

        private static void RequestCharacterImage(string characterId) => CharacterImageRequests.Add(characterId);
        private static void ProcessCharacterImageDownloads() { }

        private static bool TryGetCharacterTexture(string characterId, out Texture? texture)
        {
            texture = CdnSprites.TryGetValue(characterId, out var sprite) ? sprite.texture : null;
            return texture != null;
        }

        internal static IReadOnlyList<RowSnapshot> InspectRows() => RenderedChatRows
            .Select(row => new RowSnapshot(
                row.Root,
                row.Identity,
                row.RenderKey,
                row.Texts.ToArray(),
                row.Body,
                row.Portrait,
                row.Height))
            .ToArray();

        internal static IReadOnlyList<Text> InspectChatTexts() => _chatTexts.ToArray();

        internal static void SetViewportHeight(float height) =>
            _chatViewportRect.sizeDelta = new Vector2(_chatViewportRect.sizeDelta.x, height);

        internal static float GetScrollPosition() =>
            _chatScrollRect?.verticalNormalizedPosition ?? 0f;

        internal static void SetScrollPosition(float value)
        {
            if (_chatScrollRect != null)
                _chatScrollRect.verticalNormalizedPosition = value;
        }

        internal static float GetContentHeight() => _chatContentRect.rect.height;

        internal readonly record struct ScrollControlsSnapshot(
            RectTransform Track, RectTransform Thumb, Color ThumbColor,
            RectTransform LatestButton, string LatestText, int UnreadCount);

        internal static ScrollControlsSnapshot InspectScrollControls() => new(
            _chatScrollbarTrackRect!, _chatScrollbarThumbRect!, _chatScrollbarThumbImage!.color,
            _chatLatestButtonRect!, _chatLatestButtonText!.text, UnreadChatMessageIdentities.Count);

        internal static void UpdateScrollControlsForTest() => UpdateChatScrollControls();
        internal static void BeginOtherChatDragsForTest()
        {
            _chatDragging = true;
            _chatWindowDragging = true;
        }
        internal static bool HasOtherChatDragForTest() => _chatDragging || _chatWindowDragging;
        internal static bool IsReadingOverlayInputForTest() => _readingOverlayInput || _chatInputReadDepth > 0;
        internal static void RenderMessagesForTest() => RenderChatMessages(force: true);
        internal static float GetViewportWidthForTest() => _chatViewportRect.rect.width;
        internal static void SetViewportWidthForTest(float width) =>
            _chatViewportRect.sizeDelta = new Vector2(width, _chatViewportRect.sizeDelta.y);

        internal static bool ClickScrollbarForTest(float distanceFromTop)
        {
            Input.mousePosition = new Vector2(4f, -distanceFromTop);
            RectTransformUtility.HitRect = _chatScrollbarTrackRect;
            var wasReading = _readingOverlayInput;
            SetReadingOverlayInput(true);
            try { return HandlePointerClick(); }
            finally
            {
                SetReadingOverlayInput(wasReading);
                RectTransformUtility.HitRect = null;
            }
        }

        internal static bool ClickLatestForTest()
        {
            RectTransformUtility.HitRect = _chatLatestButtonRect;
            try { return HandlePointerClick(); }
            finally { RectTransformUtility.HitRect = null; }
        }

        internal static void DragScrollbarForTest(float distanceFromTop, bool held = true)
        {
            Time.frameCount++;
            Input.MouseButtonHeld = held;
            Input.mousePosition = new Vector2(4f, -distanceFromTop);
            UpdateChatScrollControls();
        }

        internal static void ResetScrollControlsForTest()
        {
            var track = _chatScrollbarTrackRect?.gameObject;
            var latest = _chatLatestButtonRect?.gameObject;
            if (_chatLatestButtonText != null)
                _chatTexts.Remove(_chatLatestButtonText);
            ResetChatScrollControls();
            if (track != null) UnityEngine.Object.Destroy(track);
            if (latest != null) UnityEngine.Object.Destroy(latest);
        }

        internal static void RecreateScrollControlsForTest() => CreateChatScrollControls();

        internal static void SetCdnSprite(string characterId, Sprite sprite) =>
            CdnSprites[characterId] = sprite;

        internal static IReadOnlyList<string> InspectCharacterImageRequests() => CharacterImageRequests.ToArray();
        internal static void PublishCdnPortraitForTest(string characterId, Sprite sprite)
        {
            CdnSprites[characterId] = sprite;
            RefreshRenderedCharacterImage(characterId, sprite.texture);
        }

        internal static void RefreshPortraitsForTest(ChatSnapshot snapshot) =>
            RefreshRenderedPortraits(snapshot);

        internal static string GetDraft() => _chatInputField.text;
        internal static bool GetChatInputFocusedForTest() => _chatInputField.isFocused;
        internal static InputField GetChatInputFieldForTest() => _chatInputField;
        internal static void CloseChatWindowForTest() => SetChatWindowOpen(false);
        internal static int GetDraftWriteCount() => _chatInputField.TextWriteCount;
        internal static string GetStatusText() => _chatRoomText.text;
        internal static void RefreshInputStatusForTest() => UpdateChatInputStatus();
        internal static void SetDraft(string text) => _chatInputField.text = text;

        internal static void TickInputForTest(KeyCode? key = null)
        {
            Time.frameCount++;
            Input.PressedKey = key;
            UpdateChatWindow(new ChatSnapshot { ChatButtonVisible = true });
            Input.PressedKey = null;
        }

        internal static void ClickSendForTest()
        {
            RectTransformUtility.HitRect = _chatSendRect;
            HandlePointerClick();
            RectTransformUtility.HitRect = null;
        }

        internal static void ClickInputForTest(float x)
        {
            _chatInputRect ??= NewRect("HarnessInputRect", 410f, 60f);
            Input.mousePosition = new Vector2(x, 0f);
            RectTransformUtility.HitRect = _chatInputRect;
            try { HandlePointerClick(); }
            finally { RectTransformUtility.HitRect = null; }
        }

        internal static void DragInputForTest(float x, bool held = true)
        {
            Time.frameCount++;
            Input.MouseButtonHeld = held;
            Input.mousePosition = new Vector2(x, 0f);
            UpdateChatWindow(new ChatSnapshot { ChatButtonVisible = true });
        }

        internal static bool HasInputPointerForTest() => _chatInputPointer != null;
        internal static void RefreshInputPointerForTest() =>
            UpdateChatWindow(new ChatSnapshot { ChatButtonVisible = true });
        internal static void ClickOutsideForTest()
        {
            RectTransformUtility.HitRect = null;
            HandlePointerClick();
        }

        internal static void WheelForTest(float notches, bool insideViewport = true)
        {
            RectTransformUtility.HitRect = insideViewport ? _chatViewportRect : null;
            Input.mouseScrollDelta = new Vector2(0f, notches);
            TickInputForTest();
            Input.mouseScrollDelta = Vector2.zero;
            RectTransformUtility.HitRect = null;
        }

        internal static void ResetHarness()
        {
            ResetInputState();
            ResetScrollControlsForTest();
            Input.ResetHarness();
            UnityEngine.EventSystems.EventSystem.current = new UnityEngine.EventSystems.EventSystem();
            _chatInputField = new GameObject("HarnessInput").AddComponent<InputField>();
            _chatWindow.SetActive(true);
            _chatWindowOpen = true;
            _chatInputField.text = string.Empty;
            _chatInputField.ActivateInputField();
            SetChatStatus("연결됨");
            foreach (var row in RenderedChatRows.ToArray())
                DestroyRenderedRow(row);
            ResetMessagesPresentation();
            _messages = new List<ChatUiMessage>();
            _chatTexts.Clear();
            _chatContentRect.sizeDelta = new Vector2(ChatMessageViewportWidth, 0f);
            _chatViewportRect.sizeDelta = new Vector2(ChatMessageViewportWidth, 502f);
            if (_chatScrollRect != null)
            {
                _chatScrollRect.verticalNormalizedPosition = 0f;
                _chatScrollRect.scrollSensitivity = ChatWheelStep;
            }
            CdnSprites.Clear();
            CharacterImageRequests.Clear();
        }
    }
}
#endif
