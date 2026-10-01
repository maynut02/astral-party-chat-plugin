using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace AstralParty.Chat;

internal static partial class ChatOverlay
{
    private static GameObject? _root;
    private static GameObject? _chatButton;
    private static RectTransform? _chatButtonRect;
    private static GameObject? _chatWindow;
    private static RectTransform? _chatWindowRect;
    private static RectTransform? _chatHeaderRect;
    private static RectTransform? _chatCloseRect;
    private static RectTransform? _chatInputRect;
    private static RectTransform? _chatSendRect;
    private static RectTransform? _chatViewportRect;
    private static Image? _chatCloseBackground;
    private static Text? _chatTitleText;
    private static Text? _chatRoomText;
    private static InputField? _chatInputField;
    private static ScrollRect? _chatScrollRect;
    private static RectTransform? _chatContentRect;
    private static readonly List<Text> _chatTexts = new();
    private static Image? _chatButtonImage;
    private static Image? _chatFillImage;
    private static Image? _chatIcon;
    private static Sprite? _buttonBorderSprite;
    private static Sprite? _buttonFillSprite;
    private static Sprite? _chatIconSprite;
    private static Sprite? _chatWindowBackgroundSprite;
    private static bool _chatHovered;
    private static string _chatStatus = "연결 대기 중";
    private const string ChatWindowXPref = "AstralPartyChat.WindowX.v3";
    private const string ChatWindowYPref = "AstralPartyChat.WindowY.v3";
    private const string LegacyChatWindowXPref = "AstralPartyChat.WindowX";
    private const string LegacyChatWindowYPref = "AstralPartyChat.WindowY";
    private const string LegacyChatWindowXPrefV2 = "AstralPartyChat.WindowX.v2";
    private const string LegacyChatWindowYPrefV2 = "AstralPartyChat.WindowY.v2";
    private const string LegacyChatWindowScalePref = "AstralPartyChat.WindowScale";

    public static void EnsureCreated()
    {
        if (_root != null) return;

        EnsureAssetsRunning();

        _root = new GameObject("AstralPartyChatOverlay");
        UnityEngine.Object.DontDestroyOnLoad(_root);

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32766;

        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        _root.AddComponent<GraphicRaycaster>();

        CreateChatButton();
        CreateChatWindow();
    }

    /// <summary>
    /// Tears down overlay-owned objects and cancels outstanding portrait
    /// requests. Game-owned portrait textures and fonts are never destroyed.
    /// Call on Unity's main thread.
    /// </summary>
    public static void Shutdown()
    {
        ResetInputState();
        ResetChatScrollControls();
        if (_root != null)
        {
            try
            {
                _root.SetActive(false);
                UnityEngine.Object.Destroy(_root);
            }
            catch { }
        }

        ShutdownAssets();
        ResetMessagesPresentation();

        _root = null;
        _chatButton = null;
        _chatButtonRect = null;
        _chatWindow = null;
        _chatWindowRect = null;
        _chatHeaderRect = null;
        _chatCloseRect = null;
        _chatInputRect = null;
        _chatSendRect = null;
        _chatViewportRect = null;
        _chatCloseBackground = null;
        _chatTitleText = null;
        _chatRoomText = null;
        _chatInputField = null;
        _chatScrollRect = null;
        _chatContentRect = null;
        _chatButtonImage = null;
        _chatFillImage = null;
        _chatIcon = null;
        _buttonBorderSprite = null;
        _buttonFillSprite = null;
        _chatIconSprite = null;
        _chatWindowBackgroundSprite = null;
        _chatHovered = false;
        _chatCloseHovered = false;
        _messages = new List<ChatUiMessage>();
        _chatStatus = "연결 대기 중";
    }

    public static void Refresh(ChatSnapshot snapshot)
    {
        if (_root == null) return;

        UpdateChatButton(snapshot);
        RefreshGameKoreanFont();
        UpdateChatWindow(snapshot);
        RefreshRenderedPortraits(snapshot);
    }

    private static void CreateChatButton()
    {
        if (_root == null || _chatButton != null)
            return;

        _chatButton = new GameObject("ChatButton");
        _chatButton.transform.SetParent(_root.transform, false);

        _buttonBorderSprite ??= CreateRoundedSquareSprite(
            96,
            14,
            0,
            new Color32(137, 137, 137, 255),
            new Color32(137, 137, 137, 255));

        _buttonFillSprite ??= CreateRoundedSquareSprite(
            96,
            10,
            0,
            Color.white,
            Color.white);

        _chatButtonImage = _chatButton.AddComponent<Image>();
        _chatButtonImage.sprite = _buttonBorderSprite;
        _chatButtonImage.type = Image.Type.Simple;
        _chatButtonImage.preserveAspect = true;
        _chatButtonImage.color = Color.white;
        _chatButtonImage.raycastTarget = true;

        _chatButtonRect = _chatButton.GetComponent<RectTransform>();
        _chatButtonRect.anchorMin = new Vector2(0.5f, 0.5f);
        _chatButtonRect.anchorMax = new Vector2(0.5f, 0.5f);
        _chatButtonRect.pivot = new Vector2(0.5f, 0.5f);
        _chatButtonRect.sizeDelta = new Vector2(72f, 72f);

        var fillObject = new GameObject("Fill");
        fillObject.transform.SetParent(_chatButton.transform, false);

        _chatFillImage = fillObject.AddComponent<Image>();
        _chatFillImage.sprite = _buttonFillSprite;
        _chatFillImage.type = Image.Type.Simple;
        _chatFillImage.preserveAspect = true;
        _chatFillImage.color = new Color32(34, 34, 34, 255);
        _chatFillImage.raycastTarget = false;

        var fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = new Vector2(0.5f, 0.5f);
        fillRect.anchorMax = new Vector2(0.5f, 0.5f);
        fillRect.pivot = new Vector2(0.5f, 0.5f);
        fillRect.anchoredPosition = Vector2.zero;
        fillRect.sizeDelta = new Vector2(64f, 64f);

        var iconObject = new GameObject("ChatIcon");
        iconObject.transform.SetParent(_chatButton.transform, false);

        _chatIconSprite ??= CreateChatIconSprite(64);

        _chatIcon = iconObject.AddComponent<Image>();
        _chatIcon.sprite = _chatIconSprite;
        _chatIcon.type = Image.Type.Simple;
        _chatIcon.preserveAspect = true;
        _chatIcon.color = new Color32(155, 155, 155, 255);
        _chatIcon.raycastTarget = false;

        var iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(38f, 38f);

        _chatButton.SetActive(false);
    }

    private static void CreateChatWindow()
    {
        if (_root == null || _chatWindow != null)
            return;

        _chatWindow = new GameObject("ChatWindow");
        _chatWindow.transform.SetParent(_root.transform, false);
        // Hide first so a partial construction failure can never leak onto startup.
        _chatWindow.SetActive(false);

        var panelImage = _chatWindow.AddComponent<Image>();
        _chatWindowBackgroundSprite ??= CreateRoundedRectSprite(
            560,
            700,
            18,
            4,
            new Color32(28, 28, 28, 252),
            new Color32(120, 120, 120, 255));
        panelImage.sprite = _chatWindowBackgroundSprite;
        panelImage.type = Image.Type.Simple;
        panelImage.color = Color.white;
        panelImage.raycastTarget = true;

        _chatWindowRect = _chatWindow.GetComponent<RectTransform>();
        _chatWindowRect.anchorMin = new Vector2(1f, 0.5f);
        _chatWindowRect.anchorMax = new Vector2(1f, 0.5f);
        _chatWindowRect.pivot = new Vector2(1f, 0.5f);
        // v2 position keys intentionally ignore coordinates saved by older
        // builds, which could leave the whole overlay outside the screen.
        var savedX = PlayerPrefs.GetFloat(ChatWindowXPref, -92f);
        var savedY = PlayerPrefs.GetFloat(ChatWindowYPref, 0f);
        _chatWindowRect.anchoredPosition =
            IsSaneChatWindowPosition(savedX, savedY)
                ? new Vector2(savedX, savedY)
                : new Vector2(-92f, 0f);
        _chatWindowRect.sizeDelta = new Vector2(560f, 700f);
        _chatWindowRect.localScale = Vector3.one;

        var removedLegacyPrefs = false;
        foreach (var key in new[]
        {
            LegacyChatWindowXPref,
            LegacyChatWindowYPref,
            LegacyChatWindowXPrefV2,
            LegacyChatWindowYPrefV2,
            LegacyChatWindowScalePref
        })
        {
            if (!PlayerPrefs.HasKey(key))
                continue;

            PlayerPrefs.DeleteKey(key);
            removedLegacyPrefs = true;
        }

        if (removedLegacyPrefs)
            PlayerPrefs.Save();

        var header = CreateRoundedImage(
            _chatWindow.transform,
            "Header",
            new Vector2(536f, 82f),
            new Vector2(12f, 12f),
            12,
            0,
            new Color32(38, 38, 38, 255),
            new Color32(38, 38, 38, 255),
            true);
        _chatHeaderRect = header.GetComponent<RectTransform>();

        var headerIcon = new GameObject("HeaderIcon");
        headerIcon.transform.SetParent(header.transform, false);
        var headerIconImage = headerIcon.AddComponent<Image>();
        headerIconImage.sprite = _chatIconSprite ??= CreateChatIconSprite(64);
        headerIconImage.type = Image.Type.Simple;
        headerIconImage.preserveAspect = true;
        headerIconImage.color = Color.white;
        headerIconImage.raycastTarget = false;
        var headerIconRect = headerIcon.GetComponent<RectTransform>();
        SetTopLeftRect(headerIconRect, new Vector2(16f, 22f), new Vector2(34f, 34f));

        // Keep title/subtitle inside Header so they are guaranteed to render
        // above the header background in the same local coordinate space.
        _chatTitleText = CreateUiText(
            header.transform,
            "Title",
            "채팅",
            24,
            Color.white,
            TextAnchor.MiddleLeft,
            new Vector2(64f, 6f),
            new Vector2(330f, 44f),
            FontStyle.Normal);
        _chatTitleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        _chatTitleText.verticalOverflow = VerticalWrapMode.Overflow;

        _chatRoomText = CreateUiText(
            header.transform,
            "Room",
            "더미 채팅 UI",
            17,
            new Color32(175, 175, 175, 255),
            TextAnchor.MiddleLeft,
            new Vector2(64f, 46f),
            new Vector2(390f, 26f),
            FontStyle.Normal);

        var closeObject = new GameObject("Close");
        closeObject.transform.SetParent(_chatWindow.transform, false);
        _chatCloseBackground = closeObject.AddComponent<Image>();
        _chatCloseBackground.sprite = _buttonFillSprite ??= CreateRoundedSquareSprite(
            96,
            10,
            0,
            Color.white,
            Color.white);
        _chatCloseBackground.type = Image.Type.Simple;
        _chatCloseBackground.color = new Color32(52, 52, 52, 255);
        _chatCloseBackground.raycastTarget = true;
        _chatCloseRect = closeObject.GetComponent<RectTransform>();
        _chatCloseRect.anchorMin = new Vector2(1f, 1f);
        _chatCloseRect.anchorMax = new Vector2(1f, 1f);
        _chatCloseRect.pivot = new Vector2(0.5f, 0.5f);
        _chatCloseRect.anchoredPosition = new Vector2(-42f, -53f);
        _chatCloseRect.sizeDelta = new Vector2(46f, 46f);
        CreateCloseSvgStroke(closeObject, "CloseStrokeA", 45f);
        CreateCloseSvgStroke(closeObject, "CloseStrokeB", -45f);

        // Transparent masked viewport: messages render directly on the overlay
        // without a visible container box.
        var viewport = CreateFlatImage(
            _chatWindow.transform,
            "MessageViewport",
            Color.white,
            true);
        _chatViewportRect = viewport.GetComponent<RectTransform>();
        SetTopLeftRect(
            _chatViewportRect,
            new Vector2(20f, 110f),
            new Vector2(ChatMessageViewportWidth, 502f));

        var mask = viewport.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        _chatScrollRect = viewport.AddComponent<ScrollRect>();
        _chatScrollRect.horizontal = false;
        _chatScrollRect.vertical = true;
        _chatScrollRect.movementType = ScrollRect.MovementType.Clamped;
        _chatScrollRect.inertia = true;
        _chatScrollRect.decelerationRate = 0.135f;
        _chatScrollRect.scrollSensitivity = ChatWheelStep;
        _chatScrollRect.viewport = _chatViewportRect;

        var content = CreateFlatImage(
            viewport.transform,
            "Content",
            new Color32(0, 0, 0, 0),
            false);
        _chatContentRect = content.GetComponent<RectTransform>();
        _chatContentRect.anchorMin = new Vector2(0f, 1f);
        _chatContentRect.anchorMax = new Vector2(1f, 1f);
        _chatContentRect.pivot = new Vector2(0.5f, 1f);
        _chatContentRect.anchoredPosition = Vector2.zero;
        _chatContentRect.sizeDelta = new Vector2(0f, 502f);
        _chatScrollRect.content = _chatContentRect;

        CreateChatScrollControls();

        var inputBackground = CreateRoundedImage(
            _chatWindow.transform,
            "InputBackground",
            new Vector2(410f, 60f),
            new Vector2(20f, 618f),
            12,
            3,
            new Color32(22, 22, 22, 255),
            new Color32(92, 92, 92, 255),
            true);
        _chatInputRect = inputBackground.GetComponent<RectTransform>();

        var inputText = CreateUiText(
            inputBackground.transform,
            "InputText",
            string.Empty,
            21,
            new Color32(235, 235, 235, 255),
            TextAnchor.MiddleLeft,
            new Vector2(16f, 0f),
            new Vector2(378f, 60f),
            FontStyle.Normal);
        inputText.horizontalOverflow = HorizontalWrapMode.Wrap;
        inputText.verticalOverflow = VerticalWrapMode.Truncate;

        var placeholder = CreateUiText(
            inputBackground.transform,
            "Placeholder",
            "메시지를 입력하세요...",
            21,
            new Color32(125, 125, 125, 255),
            TextAnchor.MiddleLeft,
            new Vector2(16f, 0f),
            new Vector2(378f, 60f),
            FontStyle.Normal);

        _chatInputField = inputBackground.AddComponent<InputField>();
        _chatInputField.targetGraphic = inputBackground.GetComponent<Image>();
        _chatInputField.textComponent = inputText;
        _chatInputField.placeholder = placeholder;
        _chatInputField.lineType = InputField.LineType.SingleLine;
        _chatInputField.contentType = InputField.ContentType.Standard;
        _chatInputField.characterLimit = PartyProtocol.MaxTextLength * 2;
        _chatInputField.customCaretColor = true;
        _chatInputField.caretColor = Color.white;
        _chatInputField.selectionColor = new Color32(255, 0, 180, 110);
        _chatInputField.caretWidth = 2;

        var sendButton = CreateRoundedImage(
            _chatWindow.transform,
            "SendButton",
            new Vector2(98f, 60f),
            new Vector2(442f, 618f),
            12,
            3,
            new Color32(255, 0, 180, 255),
            new Color32(137, 137, 137, 255),
            true);
        _chatSendRect = sendButton.GetComponent<RectTransform>();

        CreateUiText(
            sendButton.transform,
            "SendText",
            "전송",
            21,
            Color.white,
            TextAnchor.MiddleCenter,
            Vector2.zero,
            new Vector2(98f, 60f),
            FontStyle.Normal);

        RenderChatMessages(force: true);
        _chatWindow.SetActive(false);
    }

    private static GameObject CreateFlatImage(
        Transform parent,
        string name,
        Color32 color,
        bool raycastTarget)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);

        var image = gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycastTarget;

        return gameObject;
    }

    private static GameObject CreateRoundedImage(
        Transform parent,
        string name,
        Vector2 size,
        Vector2 topLeft,
        int radius,
        int border,
        Color32 fill,
        Color32 borderColor,
        bool raycastTarget)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);

        var image = gameObject.AddComponent<Image>();
        image.sprite = CreateRoundedRectSprite(
            Math.Max(4, (int)Math.Round(size.x)),
            Math.Max(4, (int)Math.Round(size.y)),
            radius,
            border,
            fill,
            borderColor);
        image.type = Image.Type.Simple;
        image.color = Color.white;
        image.raycastTarget = raycastTarget;

        SetTopLeftRect(
            gameObject.GetComponent<RectTransform>(),
            topLeft,
            size);

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
        Vector2 size,
        FontStyle style)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);

        var text = gameObject.AddComponent<Text>();
        text.supportRichText = false;
        text.font = GetChatFont();
        text.fontSize = fontSize;
        // Chat UI always uses the game's regular font weight.
        text.fontStyle = FontStyle.Normal;
        text.color = color;
        text.alignment = alignment;
        text.text = value;
        text.raycastTarget = false;

        SetTopLeftRect(
            gameObject.GetComponent<RectTransform>(),
            topLeft,
            size);

        _chatTexts.Add(text);
        return text;
    }

    private static void SetTopLeftRect(
        RectTransform rect,
        Vector2 topLeft,
        Vector2 size)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
        rect.sizeDelta = size;
    }

    private static Sprite CreateRoundedRectSprite(
        int width,
        int height,
        int radius,
        int border,
        Color32 fill,
        Color32 borderColor)
    {
        var texture = TrackOwnedUnityResource(
            new Texture2D(width, height, TextureFormat.RGBA32, false));
        texture.name = "AstralPartyChatRoundedRect";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        var pixels = new Color32[width * height];
        var transparent = new Color32(0, 0, 0, 0);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var insideOuter = InsideRoundedRect(
                    x,
                    y,
                    0,
                    0,
                    width - 1,
                    height - 1,
                    radius);

                if (!insideOuter)
                {
                    pixels[y * width + x] = transparent;
                    continue;
                }

                if (border <= 0)
                {
                    pixels[y * width + x] = fill;
                    continue;
                }

                var insideInner = InsideRoundedRect(
                    x,
                    y,
                    border,
                    border,
                    width - 1 - border,
                    height - 1 - border,
                    Math.Max(2, radius - border));

                pixels[y * width + x] = insideInner ? fill : borderColor;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        return CreateOwnedSprite(
            texture,
            new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    private static Sprite CreateRoundedSquareSprite(
        int size,
        int radius,
        int border,
        Color32 fill,
        Color32 borderColor)
    {
        var texture = TrackOwnedUnityResource(
            new Texture2D(size, size, TextureFormat.RGBA32, false));
        texture.name = "AstralPartyChatRoundedButton";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        var pixels = new Color32[size * size];
        var transparent = new Color32(0, 0, 0, 0);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var insideOuter = InsideRoundedSquare(x, y, size, radius, 0);
                if (!insideOuter)
                {
                    pixels[y * size + x] = transparent;
                    continue;
                }

                var insideInner = InsideRoundedSquare(
                    x,
                    y,
                    size,
                    Math.Max(2, radius - border),
                    border);

                pixels[y * size + x] = insideInner ? fill : borderColor;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        return CreateOwnedSprite(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    private static Sprite CreateChatIconSprite(int size)
    {
        var texture = TrackOwnedUnityResource(
            new Texture2D(2, 2, TextureFormat.RGBA32, false));
        texture.name = "AstralPartyChatMessagesIcon";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        var pngBytes = Convert.FromBase64String(MessagesIconData.PngBase64);
        var il2CppBytes = new Il2CppStructArray<byte>(pngBytes);

        if (!ImageConversion.LoadImage(texture, il2CppBytes, true))
            throw new InvalidOperationException("Failed to load embedded messages.svg raster.");

        return CreateOwnedSprite(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    private static void CreateCloseSvgStroke(
        GameObject parent,
        string name,
        float rotation)
    {
        var stroke = new GameObject(name);
        stroke.transform.SetParent(parent.transform, false);

        var image = stroke.AddComponent<Image>();
        image.sprite = CreateRoundedSquareSprite(
            16,
            7,
            0,
            Color.white,
            Color.white);
        image.type = Image.Type.Simple;
        image.color = Color.white;
        image.raycastTarget = false;

        var rect = stroke.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(20f, 2.8f);
        rect.localEulerAngles = new Vector3(0f, 0f, rotation);
    }

    private static bool InsideRoundedSquare(
        int x,
        int y,
        int size,
        int radius,
        int inset)
    {
        return InsideRoundedRect(
            x,
            y,
            inset,
            inset,
            size - 1 - inset,
            size - 1 - inset,
            radius);
    }

    private static bool InsideRoundedRect(
        int x,
        int y,
        int left,
        int bottom,
        int right,
        int top,
        int radius)
    {
        if (x < left || x > right || y < bottom || y > top)
            return false;

        if (x >= left + radius && x <= right - radius)
            return true;

        if (y >= bottom + radius && y <= top - radius)
            return true;

        var centerX = x < left + radius ? left + radius : right - radius;
        var centerY = y < bottom + radius ? bottom + radius : top - radius;
        var dx = x - centerX;
        var dy = y - centerY;

        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    

    

    

    private static void UpdateChatButton(ChatSnapshot snapshot)
    {
        if (_root == null || _chatButton == null || _chatButtonRect == null)
            return;

        if (!snapshot.ChatButtonVisible)
        {
            if (_chatButton.activeSelf)
                _chatButton.SetActive(false);

            SetChatButtonHover(false);
            return;
        }

        var rootRect = _root.GetComponent<RectTransform>();
        if (rootRect == null)
            return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect,
                snapshot.ChatButtonScreenPoint,
                null,
                out var localPoint))
            return;

        _chatButtonRect.anchoredPosition = localPoint;

        var buttonScale = 1f;
        if (snapshot.ChatButtonScreenSize > 1f
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect,
                Vector2.zero,
                null,
                out var localOrigin)
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect,
                new Vector2(snapshot.ChatButtonScreenSize, 0f),
                null,
                out var localSizePoint))
        {
            var desiredLocalSize = Math.Abs(
                localSizePoint.x - localOrigin.x);
            if (desiredLocalSize > 1f)
                buttonScale = Mathf.Clamp(
                    desiredLocalSize / 72f,
                    0.45f,
                    1.6f);
        }

        _chatButtonRect.localScale = new Vector3(
            buttonScale,
            buttonScale,
            1f);

        if (!_chatButton.activeSelf)
            _chatButton.SetActive(true);

        SetChatButtonHover(
            RectTransformUtility.RectangleContainsScreenPoint(
                _chatButtonRect,
                Input.mousePosition,
                null));

    }

    private static void SetChatButtonHover(bool hovered)
    {
        if (_chatHovered == hovered)
            return;

        _chatHovered = hovered;

        if (_chatFillImage != null)
            _chatFillImage.color = hovered
                ? new Color32(255, 0, 180, 255)
                : new Color32(34, 34, 34, 255);

        if (_chatIcon != null)
            _chatIcon.color = hovered
                ? Color.white
                : new Color32(155, 155, 155, 255);
    }

}
