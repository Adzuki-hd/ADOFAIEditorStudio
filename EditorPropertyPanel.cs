using System;
using System.Collections.Generic;
using ADOFAI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 右侧参数面板。行的内容从游戏自带的 PropertyInfo 元数据生成，
    /// 而不是为每种事件类型手写一张键表；这样未来游戏版本新增的事件
    /// 或新增属性，这里都不必改代码就能显示出来。
    ///
    /// 下面这些细节都是拿真实的 MoveDecorations 事件实测出来的，不是从
    /// PropertyType 枚举推断来的，它们决定了下面代码怎么写：
    ///   - 每个属性的 PropertyInfo.order 都是 0，所以排序改用
    ///     propertiesInfo 的枚举顺序（Dictionary 保留插入顺序）。
    ///   - 就算原生面板会隐藏某些属性，PropertyInfo.invisible 也仍是 false
    ///     （originalSize、maskingTarget），这些改用 ControlType.Hidden 标记。
    ///   - 本地化查表在 26 个属性里有 11 个查不到，所以还需要一层
    ///     由键名派生的可读兜底，压在原生那三层兜底之上。
    ///   - Vector2 属性的 ControlType 是 NotAssigned，所以分发只能按
    ///     PropertyType 判断。
    /// </summary>
    internal sealed class EditorPropertyPanel : MonoBehaviour
    {
        private const float RowHeight = 40f;
        private const float Padding = 10f;
        private const float CheckWidth = 22f;
        private const float LabelWidth = 104f;
        private const float ControlHeight = 30f;
        private const float PreviewHeight = 116f;

        // 工作台调色板的镜像，而不是共享：本面板和工作台是两个不同的
        // MonoBehaviour，为了取颜色向对方伸手，只会把两者绑死，没有任何好处。
        // 这里用的是各面板和时间轴共用的同一组中性色，
        // 好让参数栏读起来是同一块界面的一部分，而不是一套旧皮肤。
        private static readonly Color RowStripe = new Color(1f, 1f, 1f, 0.030f);
        private static readonly Color FieldFill = Palette.FieldWell;
        private static readonly Color FieldBorder = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color FieldBorderHover = new Color(1f, 1f, 1f, 0.20f);
        private static readonly Color FieldBorderFocus = new Color(0.286f, 0.827f, 0.796f, 0.85f);
        private static readonly Color LabelColor = Palette.TextSecondary;
        private static readonly Color LabelDisabled = new Color(0.420f, 0.420f, 0.420f, 1f);
        private static readonly Color TextPrimary = Palette.TextPrimary;
        private static readonly Color Placeholder = new Color(0.420f, 0.420f, 0.420f, 1f);
        private static readonly Color Accent = Palette.Accent;
        // caret 用白色，理由和面板边框一样：在近黑底色上，
        // 有颜色的 caret 会和自己要指示的文字抢注意力。
        private static readonly Color CaretColor = new Color(0.960f, 0.960f, 0.960f, 1f);
        private static readonly Color SelectionColor = new Color(0.310f, 0.839f, 0.788f, 0.30f);
        private const float FieldFontSize = 13f;


        // 控件的可用宽度低于这个值后，成对并排的行（Vector2 对、颜色通道）
        // 就排不下了：每一半都会窄到连一个数字都读不清，
        // 而输入框自身的最小宽度让它们互相重叠，而不是收缩。这时该行
        // 改为纵向堆叠控件，高度翻倍。
        private const float MinimumTwoAcrossWidth = 190f;

        // 堆叠行里两个字段的纵向位置。行高 76，
        // 每个字段 30，所以 3 + 30 + 10 + 30 在底部还剩 3。
        private const float StackedFieldTop = 3f;
        private const float StackedFieldGap = 10f;

        // 缓速曲线图。原版 EaseGraphRenderer 的 pointCount 是 100；
        // 缩略图这个尺寸用不到那么多点，24 个足够圆滑。
        private const int EaseGraphPoints = 24;
        private const float EaseGraphHeight = 16f;
        // 下拉行左侧那条缩略曲线占的宽度，以及它相对行左边缘的起点。
        // 勾画在 PopupCheckColumn 那一段里，曲线跟在它后面，
        // 标签再跟在曲线后面——三者错开。
        private const float OptionGraphX = 20f;
        private const float OptionGraphWidth = 44f;

        private static readonly Color CurveLine = new Color(0.94f, 0.96f, 1f, 0.90f);

        private sealed class Row
        {
            internal GameObject Root;
            internal RectTransform Rect;
            internal Toggle Enable;
            internal TextMeshProUGUI Label;
            internal RectTransform ControlHost;
            internal readonly List<GameObject> Controls = new List<GameObject>();
        }

        private EditorStudioOverlay owner;
        private TMP_FontAsset font;
        private RectTransform panelRoot;
        // 参数栏的外壳。缩放 = 1 时它和 panelRoot 是同一个；
        // 缩放 > 1 时 panelRoot 是内层容器，panelRect 才是外壳。
        private RectTransform panelRect;
        private ScrollRect scroll;
        private RectTransform scrollRectTransform;
        private RectTransform content;
        private RectTransform viewport;
        private RectTransform heading;
        private RectTransform previewHost;
        private RawImage previewImage;
        private TextMeshProUGUI previewCaption;
        private Image previewPlaceholder;

        private readonly List<Row> rows = new List<Row>();
        private readonly List<float> rowY = new List<float>();
        private LevelEvent currentEvent;
        private float nextSelectionPoll;
        private bool built;
        private bool syncing;
        private bool refreshQueued;

        private readonly List<GameObject> enumPopup = new List<GameObject>();
        private LevelEvent enumTarget;
        // 弹出层自己的状态，不塞进 enumPopup 那个列表里 ——
        // 那个列表是给「一次性销毁全部」用的，多一样东西就多一处要对齐。
        private RectTransform enumPopupRoot;
        private Button enumAnchorButton;
        private Image enumAnchorCaret;

        internal void Initialize(
            RectTransform inspectorRoot,
            TMP_FontAsset panelFont,
            EditorStudioOverlay panelOwner)
        {
            panelRoot = ConfigurePanelRect(inspectorRoot);
            font = panelFont;
            owner = panelOwner;
            EnsureShell();
            built = true;
        }

        /// <summary>
        /// 建出内层容器：外壳保持布局给定的矩形，内部按 外壳 / 缩放 排版，
        /// 再用 <c>localScale</c> 放大回去。两者叠加后屏幕上占的面积不变，
        /// 而里面的一切等比变大。
        ///
        /// 内层**总是**创建，不按"缩放是否为 1"来决定要不要建。
        /// 缩放是从 <c>Settings</c> 读的，而设置注入和 UI 构建的先后顺序
        /// 并不由这里掌控；按它决定结构会让"这次没建、下次又想用"变成
        /// 一个只在特定启动顺序下才复现的 bug。多一个 GameObject 便宜得多。
        /// </summary>
        private RectTransform ConfigurePanelRect(RectTransform rect)
        {
            panelRect = rect;
            float scale = PanelScale;

            GameObject inner = new GameObject("PanelScale", typeof(RectTransform));
            RectTransform innerRect = inner.GetComponent<RectTransform>();
            innerRect.SetParent(rect, false);
            innerRect.anchorMin = new Vector2(0f, 1f);
            innerRect.anchorMax = new Vector2(0f, 1f);
            innerRect.pivot = new Vector2(0f, 1f);
            innerRect.anchoredPosition = Vector2.zero;
            innerRect.sizeDelta = new Vector2(rect.rect.width / scale, rect.rect.height / scale);
            inner.transform.localScale = new Vector3(scale, scale, 1f);
            return innerRect;
        }

        /// <summary>
        /// 参数栏整体比工作台其它地方小了一号。与其把上百个字面量逐个
        /// 乘一遍（而且以后每加一个控件都要记得乘），这里给面板套一层
        /// 内层容器：内部一律按 <c>1 / scale</c> 的尺寸排版，再把整个容器
        /// 用 <c>localScale</c> 放大回原样。
        ///
        /// 这样外壳在屏幕上占的面积没变，而里面的一切——字号、控件高度、
        /// 行高、圆角、图标——等比放大，并且**所有既有代码都不用动**。
        /// 缩放系数是"面向用户"的，作者迟早会想再调，所以放在设置里。
        /// </summary>
        private const float MinimumPanelScale = 1f;
        private const float MaximumPanelScale = 2f;

        private float PanelScale
        {
            get
            {
                float scale = owner == null || owner.Settings == null
                    ? 1f
                    : owner.Settings.PropertyPanelScale;
                return Mathf.Clamp(scale, MinimumPanelScale, MaximumPanelScale);
            }
        }

        /// <summary>
        /// 一次性搭好固定的外壳。内容行随选中项变化而创建和销毁，
        /// 但滚动容器和装饰物预览槽是稳定的，
        /// 所以它们能扛过内容行的重建。
        /// </summary>
        private void EnsureShell()
        {
            if (scroll != null)
            {
                return;
            }

            heading = EnsureChildRect("Heading");
            BuildPreviewSlot();

            RectTransform scrollRect = EnsureChildRect("PropertyScroll");
            scrollRectTransform = scrollRect;
            float scrollTop = PreviewHeight + 8f;
            float scrollHeight = Mathf.Max(
                60f,
                panelRoot.rect.height - scrollTop - 8f - 8f);
            SetTopLeft(scrollRect, 0f, scrollTop, panelRoot.rect.width, scrollHeight);

            Image background = scrollRect.gameObject.AddComponent<Image>();
            background.color = new Color(0.055f, 0.060f, 0.074f, 0.35f);
            background.raycastTarget = true;

            scroll = scrollRect.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            scroll.scrollSensitivity = 28f;

            GameObject viewportObject = new GameObject(
                "Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollRect, false);
            viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            Image viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewportImage.raycastTarget = true;

            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);
            content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, scrollHeight);

            scroll.viewport = viewport;
            scroll.content = content;
        }

        private void BuildPreviewSlot()
        {
            RectTransform slot = EnsureChildRect("DecorationPreview");
            SetTopLeft(slot, Padding, 34f, panelRoot.rect.width - Padding * 2f, PreviewHeight - 10f);
            previewHost = slot;

            Image frame = slot.gameObject.AddComponent<Image>();
            frame.color = new Color(0.055f, 0.060f, 0.074f, 1f);
            frame.raycastTarget = false;

            previewPlaceholder = CreateChildImage("Placeholder", slot, 1f, 1f, 1f, 1f);
            previewPlaceholder.color = new Color(0.13f, 0.14f, 0.17f, 1f);

            previewImage = CreateChildRawImage("Image", slot, 1f, 1f, 1f, 1f);
            previewImage.color = Color.white;

            previewCaption = CreateChildText("Caption", slot, 6f, PreviewHeight - 30f, 1f, 22f, string.Empty, 11f);
            slot.gameObject.SetActive(false);

            LayoutPreviewSlot();
        }

        /// <summary>
        /// 按面板当前宽度给预览自己的子节点定尺寸。
        ///
        /// BuildPreviewSlot 只跑一次，那时面板还处于构造宽度，
        /// 此后没有任何东西重跑摆放 —— 所以改变尺寸（拖动时间轴）时，
        /// 图片和标题留在原宽度，而它们所在的外层
        /// 变了，画面就是这样被压扁的。
        ///
        /// 图片本身也按纹理的宽高比贴合，而不是拉伸填满框：
        /// 它是 RawImage，默认行为就是把纹理铺满整个 rect。
        /// </summary>
        private void LayoutPreviewSlot()
        {
            LayoutPreviewSlot(panelRoot == null ? 0f : panelRoot.rect.width);
        }

        private void LayoutPreviewSlot(float panelWidth)
        {
            if (previewHost == null || previewPlaceholder == null || previewImage == null)
            {
                return;
            }

            float slotWidth = Mathf.Max(1f, panelWidth - Padding * 2f);
            float boxWidth = Mathf.Max(1f, slotWidth - 2f);
            float boxHeight = Mathf.Max(1f, PreviewHeight - 34f);
            const float boxY = 1f;

            SetTopLeft(previewPlaceholder.rectTransform, 1f, boxY, boxWidth, boxHeight);
            SetTopLeft(previewCaption.rectTransform, 6f, PreviewHeight - 30f,
                Mathf.Max(1f, slotWidth - 12f), 22f);

            float width = boxWidth;
            float height = boxHeight;
            Texture texture = previewImage.texture;
            if (texture != null && texture.width > 0 && texture.height > 0)
            {
                float imageAspect = texture.width / (float)texture.height;
                float boxAspect = boxWidth / boxHeight;
                if (imageAspect > boxAspect)
                {
                    height = boxWidth / imageAspect;
                }
                else
                {
                    width = boxHeight * imageAspect;
                }
            }

            SetTopLeft(
                previewImage.rectTransform,
                1f + (boxWidth - width) * 0.5f,
                boxY + (boxHeight - height) * 0.5f,
                width,
                height
            );
        }

        private RectTransform EnsureChildRect(string name)
        {
            Transform existing = panelRoot.Find(name);
            if (existing != null)
            {
                return existing.GetComponent<RectTransform>();
            }

            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(panelRoot, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();

        /// <summary>
        /// 圆角矩形，以距离场方式绘制，这样精灵切片之后，无论画多大，
        /// 圆角都保持圆弧。设了 <paramref name="frame"/> 时，画的只有
        /// 外缘与内缩副本之间的那圈环；边框就是这样画的，
        /// 它的圆角能真正跟随填充的圆角；而 Outline 组件只是把整个图形
        /// 盖上一份偏移副本，圆角永远对不齐。
        /// </summary>
        private static Sprite GetRoundedSprite(int size, float radius, bool frame)
        {
            string key = size + "/" + radius + "/" + (frame ? "f" : "s");
            Sprite cached;
            if (SpriteCache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float cx = Mathf.Clamp(px, radius, size - radius);
                    float cy = Mathf.Clamp(py, radius, size - radius);
                    float dx = px - cx;
                    float dy = py - cy;
                    float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);

                    if (frame)
                    {
                        float insetRadius = Mathf.Max(0.001f, radius - 1.5f);
                        float icx = Mathf.Clamp(px, insetRadius + 1.5f, size - insetRadius - 1.5f);
                        float icy = Mathf.Clamp(py, insetRadius + 1.5f, size - insetRadius - 1.5f);
                        float idx = px - icx;
                        float idy = py - icy;
                        float innerAlpha = Mathf.Clamp01(insetRadius - Mathf.Sqrt(idx * idx + idy * idy) + 0.5f);
                        alpha = Mathf.Clamp01(alpha - innerAlpha);
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(radius + 1f, radius + 1f, radius + 1f, radius + 1f));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            SpriteCache[key] = sprite;
            return sprite;
        }

        private static Sprite chevronSprite;

        /// <summary>
        /// 枚举下拉框用的小 V 形箭头。画出来而不是打字符：工作台字体
        /// 没有箭头字形，所以 "▾" 会变成一个豆腐块，
        /// 时间轴自己画箭头也是同样的原因。
        /// </summary>
        private static Sprite GetChevronSprite()
        {
            if (chevronSprite != null)
            {
                return chevronSprite;
            }

            const int width = 24;
            const int height = 12;
            const float halfStroke = 1.1f;
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float d1 = DistanceToSegment(px, py, 0f, height * 0.78f, width * 0.5f, height * 0.22f);
                    float d2 = DistanceToSegment(px, py, width, height * 0.78f, width * 0.5f, height * 0.22f);
                    float alpha = Mathf.Clamp01(halfStroke - Mathf.Min(d1, d2) + 0.5f);
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            chevronSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            chevronSprite.hideFlags = HideFlags.HideAndDontSave;
            return chevronSprite;
        }

        private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax;
            float vy = by - ay;
            float wx = px - ax;
            float wy = py - ay;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 0.0001f ? 0f : Mathf.Clamp01((wx * vx + wy * vy) / len2);
            float dx = wx - vx * t;
            float dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private Image CreateChildImage(string name, RectTransform parent, float x, float y, float width, float height)
        {
            RectTransform rect = NewChildRect(name, parent);
            SetTopLeft(rect, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private RawImage CreateChildRawImage(string name, RectTransform parent, float x, float y, float width, float height)
        {
            RectTransform rect = NewChildRect(name, parent);
            SetTopLeft(rect, x, y, width, height);
            RawImage image = rect.gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
            return image;
        }

        private TextMeshProUGUI CreateChildText(
            string name, RectTransform parent, float x, float y, float width, float height,
            string value, float size)
        {
            RectTransform rect = NewChildRect(name, parent);
            SetTopLeft(rect, x, y, width, height);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.text = value;
            text.fontSize = size;
            text.color = new Color(0.78f, 0.81f, 0.87f, 1f);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        private static RectTransform NewChildRect(string name, RectTransform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            return gameObject.GetComponent<RectTransform>();
        }

        private void Update()
        {
            if (!built || owner == null || !owner.IsVisible)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (refreshQueued)
            {
                refreshQueued = false;
                Show(currentEvent);
                nextSelectionPoll = now + 0.12f;
                return;
            }

            // 弹出层一旦被别处销毁（面板被收起、控件被重建），
            // 指针离开就发不出来了，锚点按钮会永远停在"已展开"的形态。
            // 列表本身是面板的子节点，所以 GameObject 判空就够。
            if (enumPopupRoot == null && enumTarget != null)
            {
                enumTarget = null;
                enumAnchorButton = null;
                enumAnchorCaret = null;
                enumPopup.Clear();
            }
            HandleEnumPopupOutsideClick();
            ProbeGeometry();

            if (now < nextSelectionPoll)
            {
                return;
            }
            nextSelectionPoll = now + 0.12f;

            LevelEvent selected = owner == null ? null : owner.TimelineSelection;
            if (!ReferenceEquals(selected, currentEvent))
            {
                Show(selected);
                return;
            }

            // 原生撤销会整体替换 customLevel.levelData，
            // 面板持有的每个 LevelEvent 随之变成悬空引用。出现这种情况时，
            // 时间轴会清掉自己的选中项；在它清掉之前，在这里读一个失效事件
            // 得不到有用信息，写进去则会弄坏无关数据。
            if (currentEvent != null && !IsStillLive(currentEvent))
            {
                Show(null);
            }
        }

        // ---------------------------------------------------------------- 临时探针
        // 【临时】只读几何探针。定位"拖动时间轴高度时参数栏输入框被裁剪"。
        // 只打印尺寸变化的那一帧，不写任何状态、不改任何布局。查完即删。
        private float probeLastPanelHeight = float.NaN;
        private float probeLastPanelWidth = float.NaN;
        private float probeLastViewportHeight = float.NaN;
        private float probeLastViewportWidth = float.NaN;

        private void ProbeGeometry()
        {
            if (panelRoot == null || viewport == null)
            {
                return;
            }
            float ph = panelRoot.rect.height;
            float pw = panelRoot.rect.width;
            float vh = viewport.rect.height;
            float vw = viewport.rect.width;
            if (Mathf.Approximately(ph, probeLastPanelHeight)
                && Mathf.Approximately(pw, probeLastPanelWidth)
                && Mathf.Approximately(vh, probeLastViewportHeight)
                && Mathf.Approximately(vw, probeLastViewportWidth))
            {
                return;
            }
            probeLastPanelHeight = ph;
            probeLastPanelWidth = pw;
            probeLastViewportHeight = vh;
            probeLastViewportWidth = vw;

            int visible = 0;
            int below = 0;
            float worstControlOverflow = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (row == null || row.Root == null || !row.Root.activeSelf)
                {
                    continue;
                }
                float top = row.Rect.anchoredPosition.y;
                if (-top < vh)
                {
                    visible++;
                }
                else
                {
                    below++;
                }

                // 真正要盯的量：ControlHost 的实际宽度 vs 它按当前行宽
                // 应有的宽度。不相等就说明行被复用时没跟着新宽度重算，
                // 输入框就会溢出到面板右边被裁掉。
                if (row.ControlHost != null)
                {
                    float expected = Mathf.Max(60f, vw - CheckWidth - LabelWidth - Padding);
                    float overflow = row.ControlHost.rect.width - expected;
                    if (overflow > worstControlOverflow)
                    {
                        worstControlOverflow = overflow;
                    }
                }
            }

            Main.Logger.Log("[PanelGeom] panel=" + pw.ToString("0.0") + "x" + ph.ToString("0.0")
                + " viewport=" + vw.ToString("0.0") + "x" + vh.ToString("0.0")
                + " scrollRect=" + (scrollRectTransform == null ? -1f : scrollRectTransform.rect.height).ToString("0.0")
                + " contentH=" + (content == null ? -1f : content.rect.height).ToString("0.0")
                + " rows=" + rows.Count + " visible=" + visible + " below=" + below
                + " ctrlOverflow=" + worstControlOverflow.ToString("0.0"));
        }

        private static bool IsStillLive(LevelEvent levelEvent)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return false;
            }
            if (editor.events != null)
            {
                for (int i = 0; i < editor.events.Count; i++)
                {
                    if (ReferenceEquals(editor.events[i], levelEvent))
                    {
                        return true;
                    }
                }
            }
            if (editor.decorations != null)
            {
                for (int i = 0; i < editor.decorations.Count; i++)
                {
                    if (ReferenceEquals(editor.decorations[i], levelEvent))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 面板尺寸变化后（拖动时间轴高度、分辨率改变）重新收拢外壳。
        /// 工作台在 ApplyWorkspaceLayout 里重建自己的 rect；
        /// 这里为标题下方那两个容器做同样的事。
        /// </summary>
        internal void Relayout(float width, float height)
        {
            if (!built || panelRoot == null)
            {
                return;
            }

            // 内层容器按 外壳 / 缩放 排版，再由 localScale 放大回去，
            // 于是屏幕上占的还是 (width, height)，里面的一切等比变大。
            float scale = PanelScale;
            float innerWidth = width / scale;
            float innerHeight = height / scale;
            if (panelRect != null && !ReferenceEquals(panelRect, panelRoot))
            {
                SetTopLeft(panelRoot, 0f, 0f, innerWidth, innerHeight);
                panelRoot.localScale = new Vector3(scale, scale, 1f);
                RelayoutInner(innerWidth, innerHeight);
                return;
            }

            RelayoutInner(width, height);
        }

        private void RelayoutInner(float width, float height)
        {
            SetTopLeft(previewHost, Padding, 34f, width - Padding * 2f, PreviewHeight - 10f);
            LayoutPreviewSlot(width);

            float scrollTop = PreviewHeight + 8f;
            float scrollHeight = Mathf.Max(60f, height - scrollTop - 16f);
            SetTopLeft(scrollRectTransform, 0f, scrollTop, width, scrollHeight);

            if (currentEvent != null)
            {
                Show(currentEvent);
            }
        }

        /// <summary>
        /// 把重建推迟到下一帧。控件是在自己的
        /// OnValueChanged / OnEndEdit 处理器内部提交的，事件触发时
        /// 它们的点击处理器还在调用栈上，所以同步重建会销毁那个
        /// 正在派发事件的控件本身。
        /// </summary>
        private void QueueRefresh()
        {
            refreshQueued = true;
        }

        internal void Show(LevelEvent levelEvent)
        {
            currentEvent = levelEvent;
            CloseEnumPopup();
            ClearRows();

            if (levelEvent == null || levelEvent.info == null || levelEvent.info.propertiesInfo == null)
            {
                ShowPreview(null);
                content.sizeDelta = new Vector2(0f, viewport == null ? 0f : viewport.rect.height);
                return;
            }

            ShowPreview(levelEvent);

            // 行宽从 viewport 量，而不是从 content.rect：content 会被横向
            // 拉伸到 viewport，而 rect 自己的宽度在构建行的那一刻
            // 并不保证是最新的。
            float rowWidth = viewport == null || viewport.rect.width < 1f
                ? Mathf.Max(120f, panelRoot.rect.width - Padding * 2f)
                : viewport.rect.width;

            float y = 0f;
            syncing = true;
            try
            {
                int rowIndex = 0;
                foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in levelEvent.info.propertiesInfo)
                {
                    ADOFAI.PropertyInfo info = pair.Value;
                    if (info == null || string.IsNullOrEmpty(info.name))
                    {
                        continue;
                    }
                    if (info.invisible || info.controlType == ControlType.Hidden)
                    {
                        continue;
                    }

                    Row row = ObtainRow(rowIndex, rowWidth);
                    float rowHeight = RowHeightFor(info, rowWidth);
                    BuildRow(row, levelEvent, info, y, rowWidth, rowHeight);
                    rowY.Add(y);
                    y += rowHeight;
                    rowIndex++;
                }
            }
            finally
            {
                syncing = false;
            }

            content.sizeDelta = new Vector2(
                0f,
                Mathf.Max(viewport == null ? 0f : viewport.rect.height, y));
        }

        private void ClearRows()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Root.SetActive(false);
            }
            rowY.Clear();
        }

        private Row ObtainRow(int index, float rowWidth)
        {
            while (rows.Count <= index)
            {
                rows.Add(CreateRow(rows.Count, rowWidth));
            }
            Row row = rows[index];
            row.Root.SetActive(true);
            return row;
        }

        private Row CreateRow(int index, float rowWidth)
        {
            Row row = new Row();
            RectTransform rect = NewChildRect("Row" + index, content);
            row.Rect = rect;
            row.Root = rect.gameObject;

            // 一条淡条纹，而不是填充整行。旧值是近乎不透明的深色实心，
            // 让每隔一行都变成一块板，与面板自身的底色打架；
            // 改用低透明度白色来提亮交替行，不引入第二种底色。
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = (index % 2 == 0)
                ? RowStripe
                : new Color(RowStripe.r, RowStripe.g, RowStripe.b, 0f);
            background.raycastTarget = false;

            row.Enable = CreateEnableToggle(rect);
            row.Label = CreateChildText("Label", rect, CheckWidth + 4f, 0f, LabelWidth - 6f, RowHeight, string.Empty, 12f);

            row.ControlHost = NewChildRect("Control", rect);
            SetTopLeft(row.ControlHost, CheckWidth + LabelWidth, 0f,
                Mathf.Max(40f, rowWidth - CheckWidth - LabelWidth - Padding), RowHeight);
            return row;
        }

        /// <summary>
        /// 复选框：一个圆角凹槽，里面画个对勾。对勾用单个精灵，
        /// 而不是两根旋转的条，因为 <see cref="Toggle.graphic"/> 只驱动一个
        /// Graphic —— 一组条需要手工接线控制显隐，
        /// 第一次程序化调用 SetIsOnWithoutNotify 就会和开关状态脱节。
        /// </summary>
        private Toggle CreateCheckbox(RectTransform parent, string name, float x, float y, float size)
        {
            RectTransform rect = NewChildRect(name, parent);
            SetTopLeft(rect, x, y, size, size);

            Toggle toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.navigation = new Navigation { mode = Navigation.Mode.None };
            toggle.transition = Selectable.Transition.None;

            Image box = CreateChildImage("Box", rect, 0f, 0f, size, size);
            box.sprite = GetRoundedSprite(Mathf.CeilToInt(size * 2f) + 4, 4f, true);
            box.type = Image.Type.Sliced;
            box.fillCenter = true;
            box.color = FieldBorder;
            box.raycastTarget = true;

            Image tick = CreateChildImage("Tick", rect, size * 0.18f, size * 0.18f, size * 0.64f, size * 0.64f);
            tick.sprite = GetCheckSprite();
            tick.type = Image.Type.Simple;
            tick.preserveAspect = true;
            tick.color = Accent;

            toggle.targetGraphic = box;
            toggle.graphic = tick;
            return toggle;
        }

        private static Sprite checkSprite;

        /// <summary>对勾，画成在低角相交的两笔。</summary>
        private static Sprite GetCheckSprite()
        {
            if (checkSprite != null)
            {
                return checkSprite;
            }

            const int size = 32;
            const float halfStroke = 2.4f;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    // 纹理行是从下往上走的，而画的形状是按从上往下的坐标描述的，
                    // 所以在这里把纵轴镜像一次，而不是在下面每个坐标上做。
                    float py = size - (y + 0.5f);
                    float d1 = DistanceToSegment(px, py, size * 0.20f, size * 0.52f, size * 0.42f, size * 0.76f);
                    float d2 = DistanceToSegment(px, py, size * 0.42f, size * 0.76f, size * 0.82f, size * 0.26f);
                    float alpha = Mathf.Clamp01(halfStroke - Mathf.Min(d1, d2) + 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            checkSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            checkSprite.hideFlags = HideFlags.HideAndDontSave;
            return checkSprite;
        }

        private Toggle CreateEnableToggle(RectTransform parent)
        {
            return CreateCheckbox(parent, "Enable", 8f, (RowHeight - 18f) * 0.5f, 18f);
        }

        /// <summary>
        /// 一行的高度。多值行在控件无法并排后会纵向堆叠，
        /// 所以它们需要两倍空间。
        /// </summary>
        private static float RowHeightFor(ADOFAI.PropertyInfo info, float rowWidth)
        {
            if (!StacksVertically(info, rowWidth))
            {
                return RowHeight;
            }
            return RowHeight * 2f - 4f;
        }

        /// <summary>
        /// 该行的控件是否必须堆叠，而不是并排。
        ///
        /// 只有 Vector2（以及颜色，它要摆四个通道加一个色块）有不止
        /// 一个控件。阈值卡的是留给控件的宽度，不是面板宽度：
        /// 标签和启用复选框先分走自己那份，所以看起来够宽的面板
        /// 也可能剩下太少。
        /// </summary>
        private static bool StacksVertically(ADOFAI.PropertyInfo info, float rowWidth)
        {
            if (info == null || info.type != PropertyType.Vector2)
            {
                return false;
            }

            float controlWidth = rowWidth - CheckWidth - LabelWidth - Padding;
            return controlWidth < MinimumTwoAcrossWidth;
        }

        private void BuildRow(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info, float y,
            float rowWidth, float rowHeight)
        {
            SetTopLeft(row.Rect, 0f, y, rowWidth, rowHeight);

            bool canDisable = info.canBeDisabled;
            row.Enable.gameObject.SetActive(canDisable);
            row.Enable.SetIsOnWithoutNotify(canDisable && !IsDisabled(levelEvent, info.name));

            row.Label.text = ResolveLabel(levelEvent, info);
            row.Label.color = canDisable && IsDisabled(levelEvent, info.name)
                ? LabelDisabled
                : LabelColor;

            if (canDisable)
            {
                string key = info.name;
                LevelEvent bound = levelEvent;
                row.Enable.onValueChanged.RemoveAllListeners();
                row.Enable.onValueChanged.AddListener(delegate(bool value)
                {
                    if (syncing || bound == null)
                    {
                        return;
                    }
                    SetDisabled(bound, key, !value);
                    QueueRefresh();
                });
            }
            else
            {
                row.Enable.onValueChanged.RemoveAllListeners();
            }

            ReleaseControls(row);

            // 控件宽度一律从当前这一帧的 rowWidth 算，**不要读回
            // row.ControlHost.rect.width**。
            //
            // 行是按索引复用的（ObtainRow 从对象池取），而 ControlHost 是
            // 左锚定的固定宽度，不会跟着父节点拉伸。旧写法先把旧宽度读出来，
            // 只有在"宽度<=1 或高度不匹配"时才重算，于是普通高度的行被复用时
            // 两个条件都不成立，直接沿用了上一次的宽度——拖动时间轴改变参数栏
            // 宽度后，行本身的 rect 更新了，控件却还按旧宽度摆，右边的输入框
            // 就被面板边缘裁掉。
            float controlWidth = Mathf.Max(60f, rowWidth - CheckWidth - LabelWidth - Padding);
            SetTopLeft(row.ControlHost, CheckWidth + LabelWidth, 0f, controlWidth, rowHeight);

            // 'floor' 是唯一不在事件 data 字典里的属性：
            // 它挂在 LevelEvent 字段上，所以通用 Get 什么都读不到。
            if (info.type == PropertyType.Int && info.name == "floor")
            {
                BuildFloor(row, levelEvent, info, controlWidth);
                return;
            }

            BuildControl(row, levelEvent, info, 0f, controlWidth);
        }

        private void BuildFloor(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info, float width)
        {
            TMP_InputField field = CreateInputField(
                row, "Floor", 0f, width, "0", levelEvent.floor.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            row.Controls.Add(field.gameObject);

            LevelEvent bound = levelEvent;
            field.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                int parsed;
                if (!int.TryParse(text, out parsed))
                {
                    QueueRefresh();
                    return;
                }
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                {
                    return;
                }
                editor.SaveState();
                bound.floor = parsed;
                QueueRefresh();
            });
        }

        private static void ReleaseControls(Row row)
        {
            for (int i = 0; i < row.Controls.Count; i++)
            {
                if (row.Controls[i] != null)
                {
                    DestroyImmediate(row.Controls[i]);
                }
            }
            row.Controls.Clear();
        }

        private bool IsDisabled(LevelEvent levelEvent, string key)
        {
            bool disabled;
            return levelEvent.disabled != null
                && levelEvent.disabled.TryGetValue(key, out disabled)
                && disabled;
        }

        private void SetDisabled(LevelEvent levelEvent, string key, bool disabled)
        {
            if (levelEvent.disabled == null || !levelEvent.disabled.ContainsKey(key))
            {
                return;
            }
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.SaveState();
            }
            levelEvent.disabled[key] = disabled;
        }

        /// <summary>
        /// 解析行标签。先试原生那三步查表，因为它
        /// 跟随游戏自身的语言；在真实事件上实测，26 个属性里漏掉 11 个，
        /// 所以最后兜底渲染键名本身，而不是让这一行
        /// 没有标签。
        /// </summary>
        private static string ResolveLabel(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
        {
            bool exists;
            string label = null;
            if (info.levelEventInfo != null && !string.IsNullOrEmpty(info.levelEventInfo.name))
            {
                label = RDString.GetWithCheck(
                    "editor." + info.levelEventInfo.name + "." + info.name, out exists);
                if (exists && !string.IsNullOrEmpty(label))
                {
                    return label;
                }
            }

            label = RDString.GetWithCheck("editor." + info.name, out exists);
            if (exists && !string.IsNullOrEmpty(label))
            {
                return label;
            }

            if (!string.IsNullOrEmpty(info.customLabel))
            {
                return info.customLabel;
            }

            return HumaniseKey(info.name);
        }

        internal static string HumaniseKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(key.Length + 6);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (i == 0)
                {
                    builder.Append(char.ToUpperInvariant(c));
                    continue;
                }
                if (char.IsUpper(c) && !char.IsUpper(key[i - 1]))
                {
                    builder.Append(' ');
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        // ---------------------------------------------------------------- 控件

        private void BuildControl(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            try
            {
                switch (info.type)
                {
                    case PropertyType.Bool:
                        BuildBool(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.Int:
                        BuildNumeric(row, levelEvent, info, x, width, true);
                        break;
                    case PropertyType.Float:
                        BuildNumeric(row, levelEvent, info, x, width, false);
                        break;
                    case PropertyType.String:
                    case PropertyType.LongString:
                    case PropertyType.File:
                        BuildString(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.Enum:
                        BuildEnum(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.Vector2:
                        BuildVector2(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.Color:
                        BuildColor(row, levelEvent, info, x, width);
                        break;
                    default:
                        BuildUnsupported(row, levelEvent, info, x, width);
                        break;
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PropertyPanel] row " + info.name + " failed: " + exception);
                BuildUnsupported(row, levelEvent, info, x, width);
            }
        }

        private void BuildUnsupported(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            string text = "<暂不支持 " + info.type + ">";
            row.Controls.Add(CreateChildText("Unsupported", row.ControlHost, x, 0f, width, RowHeight, text, 12.5f).gameObject);
        }

        private void BuildBool(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            bool value = SafeGet(levelEvent, info, false);
            float boxSize = 20f;

            Toggle toggle = CreateCheckbox(row.ControlHost, "Bool", x + 2f, (RowHeight - boxSize) * 0.5f, boxSize);
            toggle.isOn = value;
            row.Controls.Add(toggle.gameObject);

            string key = info.name;
            LevelEvent bound = levelEvent;

            toggle.onValueChanged.AddListener(delegate(bool v)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                Commit(bound, key, v);
            });
        }

        private void BuildNumeric(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width, bool integer)
        {
            float value = integer
                ? SafeGet(levelEvent, info, 0)
                : SafeGet(levelEvent, info, 0f);
            float suffixWidth = string.IsNullOrEmpty(info.unit) ? 0f : 22f;

            TMP_InputField field = CreateInputField(
                row, "Num", x, width - suffixWidth, integer ? "0" : "0.###",
                integer ? FormatInt(value) : FormatFloat(value));
            row.Controls.Add(field.gameObject);

            if (suffixWidth > 0f)
            {
                row.Controls.Add(CreateChildText("Unit", row.ControlHost,
                    x + width - suffixWidth, 0f, suffixWidth, RowHeight, info.unit, 10f).gameObject);
            }

            string key = info.name;
            LevelEvent bound = levelEvent;
            field.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                if (integer)
                {
                    int parsed;
                    if (int.TryParse(text, out parsed))
                    {
                        Commit(bound, key, parsed);
                    }
                    else
                    {
                        QueueRefresh();
                    }
                }
                else
                {
                    float parsed;
                    if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        Commit(bound, key, parsed);
                    }
                    else
                    {
                        QueueRefresh();
                    }
                }
            });
        }

        private void BuildString(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            string value = SafeGet(levelEvent, info, string.Empty);
            TMP_InputField field = CreateInputField(row, "Text", x, width, null, value ?? string.Empty);
            row.Controls.Add(field.gameObject);

            string key = info.name;
            LevelEvent bound = levelEvent;
            field.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                Commit(bound, key, text ?? string.Empty);
            });
        }

        private void BuildEnum(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            string current = ReadEnumName(levelEvent, info);
            string display = DisplayEnumName(levelEvent, info, current);

            RectTransform rect = NewChildRect("Enum", row.ControlHost);
            SetTopLeft(rect, x, (RowHeight - ControlHeight) * 0.5f, width - 4f, ControlHeight);
            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = GetRoundedSprite(24, 5f, false);
            background.type = Image.Type.Sliced;
            background.fillCenter = true;
            background.color = FieldFill;
            background.raycastTarget = true;
            Image rim = CreateChildImage("Rim", rect, 0f, 0f, width - 4f, ControlHeight);
            rim.sprite = GetRoundedSprite(24, 5f, true);
            rim.type = Image.Type.Sliced;
            rim.fillCenter = true;
            rim.color = FieldBorder;
            rim.raycastTarget = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock palette = button.colors;
            palette.normalColor = Color.white;
            palette.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            palette.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            palette.selectedColor = palette.highlightedColor;
            palette.colorMultiplier = 1f;
            button.colors = palette;
            row.Controls.Add(rect.gameObject);

            TextMeshProUGUI valueLabel = CreateChildText("Value", rect, 9f, 0f,
                width - 34f, ControlHeight, display, 12.5f);
            valueLabel.color = TextPrimary;
            // 画出来的 V 形箭头，与时间轴的溢出标记一致：工作台字体没有
            // 箭头字形，所以打出来的会变成豆腐块。
            Image caret = CreateChildImage("Caret", rect, width - 26f, 0f, 14f, 10f);
            caret.sprite = GetChevronSprite();
            caret.color = Placeholder;
            caret.raycastTarget = false;
            LoadoutCaret(caret, rect, (ControlHeight - 10f) * 0.5f);

            LevelEvent bound = levelEvent;
            button.onClick.AddListener(delegate
            {
                if (syncing || bound == null)
                {
                    return;
                }
                ToggleEnumPopup(bound, info, rect, display);
            });
        }

        private void BuildVector2(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            Vector2 value = SafeGet(levelEvent, info, Vector2.zero);
            float gap = 6f;

            // 通常并排；行窄到放不下两个可用字段时改为堆叠。
            // 堆叠形态每个字段用 ControlHeight，
            // 这正是行高翻倍预留出来的。
            //
            // 堆叠行要让两个字段之间留出明显空隙：2px 时它们读起来像
            // 一个被切开的控件，而不是两个独立的值。行高翻倍后是 76，
            // 两个 30px 控件，所以 3 + 30 + 10 + 30 + 3 刚好填满。
            bool stacked = width < MinimumTwoAcrossWidth;
            float half = stacked ? width : (width - gap) * 0.5f;
            float firstY = stacked ? StackedFieldTop : (RowHeight - ControlHeight) * 0.5f;
            float secondY = stacked
                ? StackedFieldTop + ControlHeight + StackedFieldGap
                : (RowHeight - ControlHeight) * 0.5f;
            float secondX = stacked ? x : x + half + gap;

            TMP_InputField fieldX = CreateInputFieldAt(row, "X", x, half, firstY, "0.###",
                FormatFloat(value.x));
            TMP_InputField fieldY = CreateInputFieldAt(row, "Y", secondX, half, secondY, "0.###",
                FormatFloat(value.y));
            row.Controls.Add(fieldX.gameObject);
            row.Controls.Add(fieldY.gameObject);

            string key = info.name;
            LevelEvent bound = levelEvent;
            fieldX.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                float parsed;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    Vector2 existing = SafeGet(bound, info, Vector2.zero);
                    Commit(bound, key, new Vector2(parsed, existing.y));
                }
            });
            fieldY.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                float parsed;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    Vector2 existing = SafeGet(bound, info, Vector2.zero);
                    Commit(bound, key, new Vector2(existing.x, parsed));
                }
            });
        }

        private void BuildColor(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            Color value = SafeGetColor(levelEvent, info);
            float swatch = 24f;

            Image preview = CreateChildImage("Swatch", row.ControlHost, x, (RowHeight - swatch) * 0.5f, swatch, swatch);
            preview.color = value;
            preview.raycastTarget = false;
            row.Controls.Add(preview.gameObject);

            TMP_InputField field = CreateInputField(
                row, "Hex", x + swatch + 6f, width - swatch - 6f, null, ColorToHex(value));
            row.Controls.Add(field.gameObject);

            string key = info.name;
            LevelEvent bound = levelEvent;
            field.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                Color parsed;
                if (TryParseHex(text, out parsed))
                {
                    Commit(bound, key, ColorToHex(parsed));
                }
                else
                {
                    QueueRefresh();
                }
            });
        }

        private TMP_InputField CreateInputField(Row row, string name, float x, float width,
            string contentType, string value)
        {
            // 在行内纵向居中，这是单个控件该在的位置。
            return CreateInputFieldAt(row, name, x, width,
                (RowHeight - ControlHeight) * 0.5f, contentType, value);
        }

        /// <summary>
        /// 按显式纵向偏移摆放一个输入框。堆叠行需要它：这类行的两个
        /// 控件各占一行，默认居中会把两者
        /// 叠在翻倍行的正中间。
        /// </summary>
        private TMP_InputField CreateInputFieldAt(Row row, string name, float x, float width,
            float y, string contentType, string value)
        {
            RectTransform rect = NewChildRect(name, row.ControlHost);
            SetTopLeft(rect, x, y, Mathf.Max(24f, width), ControlHeight);
            BuildFieldChrome(rect, out Image background, out Image rim);

            // 先禁用这个字段，配置完再启用。
            // TMP_InputField 在 OnEnable 里建 caret，过程中要读 textComponent 和
            // textViewport；而 AddComponent 会立刻跑 OnEnable，那时两者还是 null，
            // caret 就永远建不出来，字段看起来不可编辑，
            // 哪怕下面设了 caretWidth 和 caretBlinkRate 也没用。
            // 保持不激活直到接线完成，这些设置才会生效。
            rect.gameObject.SetActive(false);
            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };

            field.customCaretColor = true;
            field.caretColor = CaretColor;
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.selectionColor = SelectionColor;

            RectTransform textRect = NewChildRect("Text", rect);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            TextMeshProUGUI text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.fontSize = FieldFontSize;
            text.color = TextPrimary;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);

            field.textViewport = rect;
            field.textComponent = text;
            field.text = value ?? string.Empty;

            HookFieldChrome(field, rim, background);

            if (!string.IsNullOrEmpty(contentType))
            {
                field.contentType = TMP_InputField.ContentType.Standard;
            }

            rect.gameObject.SetActive(true);
            return field;
        }

        /// <summary>
        /// 字段所在的凹槽：圆角深色填充，外圈圆角轮廓用单独精灵画，
        /// 而不是用 <c>Outline</c> 组件。Outline 会把整个图形
        /// 盖上一份偏移副本，它的圆角永远和填充的对不齐，
        /// 那圈环看起来就成了阴影。
        /// </summary>
        private void BuildFieldChrome(RectTransform rect, out Image background, out Image rim)
        {
            background = rect.gameObject.AddComponent<Image>();
            background.sprite = GetRoundedSprite(24, 5f, false);
            background.type = Image.Type.Sliced;
            background.fillCenter = true;
            background.color = FieldFill;
            background.raycastTarget = true;

            rim = CreateChildImage("Rim", rect, 0f, 0f, rect.sizeDelta.x, rect.sizeDelta.y);
            rim.sprite = GetRoundedSprite(24, 5f, true);
            rim.type = Image.Type.Sliced;
            rim.fillCenter = true;
            rim.color = FieldBorder;
            rim.raycastTarget = false;
        }

        /// <summary>
        /// 为字段的边圈接好悬停和聚焦颜色。两者都挂在悬停组件上，
        /// 而不是一个放这里一个放那里：聚焦优先于悬停，两个状态必须
        /// 一起解析，否则指针一离开，聚焦的字段就会丢掉边圈。
        /// </summary>
        private static void HookFieldChrome(TMP_InputField field, Image rim, Image background)
        {
            // 挂在填充上，而不是边圈上。边圈是不挡射线的叠加层，好让点击
            // 落到下面的字段；挂在它上面的指针处理器永远不会触发。
            FieldHover hover = background.gameObject.AddComponent<FieldHover>();
            hover.Rim = rim;
            hover.Field = field;
            hover.Idle = FieldBorder;
            hover.Hovered = FieldBorderHover;
            hover.Focused = FieldBorderFocus;

            field.onSelect.AddListener(delegate
            {
                hover.Refresh();
            });
            field.onDeselect.AddListener(delegate { hover.Refresh(); });
        }

        // ---------------------------------------------------------------- 枚举弹窗

        private void ToggleEnumPopup(LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            RectTransform anchor, string currentDisplay)
        {
            // 为重建路径做保护：Show() 会销毁这里要读的锚点。
            if (syncing)
            {
                return;
            }
            if (enumTarget == levelEvent && enumPopupRoot != null)
            {
                CloseEnumPopup();
                return;
            }
            CloseEnumPopup();
            enumTarget = levelEvent;

            if (info.enumType == null)
            {
                enumTarget = null;
                return;
            }

            string[] names;
            try
            {
                names = Enum.GetNames(info.enumType);
            }
            catch (Exception)
            {
                enumTarget = null;
                return;
            }
            if (names.Length == 0)
            {
                enumTarget = null;
                return;
            }

            // 锚点的世界 width 已经含了面板的缩放，再除以 lossyScale
            // 就换回了面板自己的坐标单位。
            float anchorWidth = Mathf.Max(120f, AnchorWidthInPanelUnits(anchor));

            // 行数按「面板里还剩多少地方」定，而不是拍一个固定值：
            // 属性多的时候列表能排到面板底边，属性少的时候也不会留一截空白。
            Vector3 anchorWorld = anchor.TransformPoint(Vector3.zero);
            float anchorLocalY = panelRoot.InverseTransformPoint(anchorWorld).y;
            float panelHeight = panelRoot.rect.height;
            float spaceBelow = panelHeight + anchorLocalY - PopupMargin;
            float spaceAbove = -anchorLocalY - ControlHeight - PopupMargin * 2f - PopupGap;
            bool dropUp = spaceBelow < PopupMinListHeight && spaceAbove > spaceBelow;

            const float optionHeight = PopupOptionHeight;
            float maxListHeight = Mathf.Max(optionHeight, dropUp ? spaceAbove : spaceBelow);
            int maxVisibleRows = Mathf.Max(1, Mathf.FloorToInt(maxListHeight / optionHeight));
            int visibleRows = Mathf.Min(names.Length, maxVisibleRows);

            float listHeight = visibleRows * optionHeight;
            float popupHeight = listHeight + 2f;                  // 背景层的上下边框
            float popupWidth = anchorWidth;

            // 贴着锚点那一侧的坐标。往上弹时列表的「列表底」对齐锚点顶边，
            // 因为面板原点在左上、y 向下，所以是 anchorTop - 总高。
            float anchorTop = -anchorLocalY + ControlHeight;
            float popupTop = dropUp
                ? anchorTop - popupHeight - PopupGap
                : anchorTop + PopupGap;
            // 最后再夹一次：算出来的空间和真实值可能因为面板正在变尺寸而对不上，
            // 这一步保证列表至少整条在面板里。
            popupTop = Mathf.Clamp(popupTop, PopupMargin, Mathf.Max(PopupMargin, panelHeight - popupHeight - PopupMargin));

            RectTransform popup = NewChildRect("EnumPopup", panelRoot);
            SetTopLeft(popup, Padding, popupTop, popupWidth, popupHeight);
            ApplyPopupFrame(popup, popupWidth, popupHeight);
            enumPopupRoot = popup;
            enumPopup.Add(popup.gameObject);

            // 视口与内容分开：视口是那层深色底，带 RectMask2D 把滚动时
            // 溢出的一行裁掉；内容是真正摆放选项行的节点，靠 anchoredPosition
            // 在视口里上下移动。滚动移动的必须是内容 —— 用 ScrollRect 去移动
            // 视口的话，项数少的时候列表会整片空白，项数多的时候
            // 又只有滚到某一边才看得见内容。
            RectTransform viewportRect = NewChildRect("Viewport", popup);
            SetTopLeft(viewportRect, 1f, 1f, popupWidth - 2f, listHeight);
            Image viewportBackground = viewportRect.gameObject.AddComponent<Image>();
            viewportBackground.sprite = GetRoundedSprite(24, PopupRadius - 1f, false);
            viewportBackground.type = Image.Type.Sliced;
            viewportBackground.fillCenter = true;
            viewportBackground.color = PopupSurface;
            viewportBackground.raycastTarget = true;
            viewportRect.gameObject.AddComponent<RectMask2D>();

            RectTransform contentRect = NewChildRect("Content", viewportRect);
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, names.Length * optionHeight);

            ScrollRect popupScroll = popup.gameObject.AddComponent<ScrollRect>();
            popupScroll.horizontal = false;
            popupScroll.vertical = true;
            popupScroll.movementType = ScrollRect.MovementType.Clamped;
            popupScroll.scrollSensitivity = optionHeight;
            popupScroll.viewport = viewportRect;
            popupScroll.content = contentRect;

            string current = ReadEnumName(levelEvent, info);
            bool hasCurrent = false;
            int currentIndex = -1;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], current, StringComparison.Ordinal))
                {
                    hasCurrent = true;
                    currentIndex = i;
                    break;
                }
            }

            for (int i = 0; i < names.Length; i++)
            {
                string optionName = names[i];
                string display = DisplayEnumName(levelEvent, info, optionName);
                bool isCurrent = hasCurrent && i == currentIndex;

                RectTransform optionRect = NewChildRect("Option" + i, contentRect);
                SetTopLeft(optionRect, 0f, i * optionHeight, popupWidth - 2f, optionHeight);
                Image optionBackground = optionRect.gameObject.AddComponent<Image>();
                optionBackground.color = isCurrent ? PopupSelected : PopupSurface;
                optionBackground.raycastTarget = true;

                DG.Tweening.Ease optionEase = DG.Tweening.Ease.Linear;
                bool optionIsEase = IsEaseEnum(info)
                    && TryParseEase(optionName, out optionEase);
                // 缓速列表里每一项左边都配一条自己的曲线，这样不用逐项点开
                // 就能看出 InSine 和 OutSine 差在哪。勾画在最左边那一列，
                // 曲线在它右边，两者不重叠。
                float labelX = PopupCheckColumn;
                if (optionIsEase)
                {
                    TryAddEaseGraph(optionRect, optionEase);
                    labelX = OptionGraphX + OptionGraphWidth + 8f;
                }

                // 缓速项的文字要跟曲线**垂直居中对齐**：曲线自己按行高居中，
                // 所以文字也从行顶往下留同样多的空白，而不是贴顶。
                float labelTop = optionIsEase
                    ? (PopupOptionHeight - PopupLabelHeight) * 0.5f
                    : 0f;

                // 所有标签左边缘对齐在同一列上，不因为有没有勾而缩进 ——
                // 一列文字参差不齐比多占几像素难看得多。勾画在标签左边
                // 那段留白的正中，不会压到字上。
                CreateChildText("Label", optionRect, labelX, labelTop,
                    popupWidth - 2f - labelX - 6f, PopupLabelHeight, display, 12.5f);
                if (isCurrent)
                {
                    // 画出来的勾，理由和 V 形箭头一样：工作台字体没有这个字形，
                    // 直接打 "✓" 会得到豆腐块。
                    Image check = CreateChildImage("Check", optionRect,
                        (PopupCheckColumn - 12f) * 0.5f, (optionHeight - 10f) * 0.5f, 12f, 10f);
                    check.sprite = GetCheckSprite();
                    check.color = Accent;
                }

                // hover 覆盖底色。这里是手写的，没用 Button 的 ColorTint ——
                // 那套会给当前选中项也叠上一层高亮，把"选中的是哪一项"
                // 从颜色上抹掉，只能靠左边的勾去认。
                EnumOptionHover hover = optionRect.gameObject.AddComponent<EnumOptionHover>();
                hover.Background = optionBackground;
                hover.Idle = isCurrent ? PopupSelected : PopupSurface;
                hover.Hovered = isCurrent ? PopupSelectedHover : PopupHover;

                Button optionButton = optionRect.gameObject.AddComponent<Button>();
                optionButton.targetGraphic = optionBackground;
                optionButton.transition = Selectable.Transition.None;
                optionButton.navigation = new Navigation { mode = Navigation.Mode.None };

                LevelEvent bound = levelEvent;
                string key = info.name;
                string chosen = optionName;
                optionButton.onClick.AddListener(delegate
                {
                    CloseEnumPopup();
                    Commit(bound, key, ParseEnumValue(info, chosen));
                });
            }

            // 当前项在可视范围外时，列表滚到它那一行，而不是从第一项开始。
            // 分母是「最多能滚过的行数」，名字太少时它是 0，除法会炸，
            // 所以先挡掉；居中的 0.5 是让那一行落在列表中间而不是贴顶/贴底。
            if (currentIndex >= visibleRows && names.Length > visibleRows)
            {
                popupScroll.verticalNormalizedPosition = Mathf.Clamp01(
                    1f - (currentIndex - (visibleRows - 1) * 0.5f) / (names.Length - visibleRows));
            }

            // 锚点按钮跟着变形态，让"这个控件开着"这件事在控件本身上看得出来，
            // 而不是只靠屏幕另一处的那块浮层。
            enumAnchorButton = anchor.GetComponent<Button>();
            Transform caretTransform = anchor.Find("Caret");
            enumAnchorCaret = caretTransform == null ? null : caretTransform.GetComponent<Image>();
            LoadoutAnchor(anchor, currentDisplay);
        }

        private void CloseEnumPopup()
        {
            for (int i = 0; i < enumPopup.Count; i++)
            {
                if (enumPopup[i] != null)
                {
                    Destroy(enumPopup[i]);
                }
            }
            enumPopup.Clear();
            enumPopupRoot = null;
            enumTarget = null;
            enumAnchorButton = null;
            enumAnchorCaret = null;
        }

        private void HandleEnumPopupOutsideClick()
        {
            // 只在"按下的那一帧"判一次，而且只看 GetMouseButtonDown ——
            // 它就是"这次按下的第一帧"，天然只有一帧为真。
            //
            // 不要用 GetMouseButton(0) 做跨帧守卫：按下之后它**每一帧都是真**，
            // 而第一帧同时满足 GetMouseButtonDown，所以那种写法会先把守卫
            // 置真、再让 GetMouseButtonDown 那一句被跳过，判定永远不执行。
            //
            // 也不需要防"打开时立刻把自己关掉"：弹出层是按钮的 onClick
            // 打开的，而 onClick 在**抬起**时触发，那时 GetMouseButtonDown
            // 早已为假。
            if (enumTarget == null || !Input.GetMouseButtonDown(0))
            {
                return;
            }
            if (PointerOverEnumAnchor() || PointerOverEnumPopup())
            {
                return;
            }

            CloseEnumPopup();
        }

        private bool PointerOverEnumAnchor()
        {
            if (enumAnchorButton == null)
            {
                return false;
            }
            Vector2 point = CurrentPointerPosition();
            RectTransform rect = enumAnchorButton.transform as RectTransform;
            return rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, point, PopupEventCamera(rect));
        }

        private bool PointerOverEnumPopup()
        {
            if (enumPopupRoot == null)
            {
                return false;
            }
            Vector2 point = CurrentPointerPosition();
            return RectTransformUtility.RectangleContainsScreenPoint(
                enumPopupRoot, point, PopupEventCamera(enumPopupRoot));
        }

        /// <summary>
        /// 求值时用的相机。面板挂在 Overlay 画布下时必须是 null，
        /// 传了相机反而算不对。
        /// </summary>
        private static Camera PopupEventCamera(RectTransform rect)
        {
            Canvas canvas = rect == null ? null : rect.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }
            return canvas.worldCamera;
        }

        private Vector2 CurrentPointerPosition()
        {
            return new Vector2(Input.mousePosition.x, Input.mousePosition.y);
        }

        // ---------------------------------------------------------- 缓速曲线

        /// <summary>
        /// 这个枚举属性是不是缓速。只认 <c>DG.Tweening.Ease</c>，
        /// 不按属性名匹配 —— 名字可能被本地化或被别的键复用，
        /// 而类型是确定的。
        /// </summary>
        private static bool IsEaseEnum(ADOFAI.PropertyInfo info)
        {
            return info != null && info.enumType == typeof(DG.Tweening.Ease);
        }

        private static bool TryReadEase(LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            out DG.Tweening.Ease ease)
        {
            ease = DG.Tweening.Ease.Linear;
            if (levelEvent == null || info == null)
            {
                return false;
            }
            try
            {
                return TryParseEase(levelEvent[info.name] as string, out ease);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryParseEase(string name, out DG.Tweening.Ease ease)
        {
            ease = DG.Tweening.Ease.Linear;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            try
            {
                ease = (DG.Tweening.Ease)Enum.Parse(typeof(DG.Tweening.Ease), name);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 下拉选项左侧那条缩略曲线。用游戏自己带的第一方折线组件
        /// <c>UnityEngine.UI.Extensions.UILineRenderer</c> 画 ——
        /// 原版的 <c>EaseGraphRenderer</c> 用的就是它（它躺在
        /// Assembly-CSharp-firstpass.dll 里，随游戏一起发布，不用额外带依赖）。
        ///
        /// 采样和归一化照搬原版 DrawGraph：横轴是 <c>i / (pointCount - 1)</c>，
        /// 纵轴是缓速在 0..1 上的取值，再按整条曲线的最大值等比缩放。
        /// 曲线取自 <see cref="Preview.PreviewEase.EasedValue"/>，
        /// 和预览求值器同一套数学，所以图上什么形状、画面里就是什么形状。
        /// </summary>
        private void TryAddEaseGraph(RectTransform option, DG.Tweening.Ease ease)
        {
            RectTransform graphRect = NewChildRect("Graph", option);

            // 轴心必须是左下角 (0,0)。
            //
            // UILineRenderer 把 Points 当作"相对 RectTransform 左下角"的坐标。
            // NewChildRect 给的是 (0,1) 左上轴心，而 RelativeSize=false 时它
            // 只做 `-pivot * 1` 这一步修正（尺寸按 1 算，量纲是错的），
            // 于是整条曲线被推到了锚点上方约一行的高度——表现就是每行的
            // 曲线探进上一行，"图标和文字对不上"且"一条曲线跨了两个缓速"。
            // 把轴心改回左下角，坐标就一一对应，不需要任何修正。
            graphRect.anchorMin = new Vector2(0f, 1f);
            graphRect.anchorMax = new Vector2(0f, 1f);
            graphRect.pivot = new Vector2(0f, 0f);
            float graphTop = (PopupOptionHeight - EaseGraphHeight) * 0.5f;
            graphRect.anchoredPosition = new Vector2(OptionGraphX, -(graphTop + EaseGraphHeight));
            graphRect.sizeDelta = new Vector2(OptionGraphWidth, EaseGraphHeight);

            UnityEngine.UI.Extensions.UILineRenderer line =
                graphRect.gameObject.AddComponent<UnityEngine.UI.Extensions.UILineRenderer>();
            line.color = CurveLine;
            line.LineThickness = 1.6f;
            line.LineCaps = false;
            line.LineJoins = UnityEngine.UI.Extensions.UILineRenderer.JoinType.Bevel;
            line.BezierMode = UnityEngine.UI.Extensions.UILineRenderer.BezierType.None;
            // 这些点本来就是逐点采样出来的，再让它做一次分辨率加密只是白算；
            // Bezier 也必须关掉，否则它会把折线当成控制点重新拟合。
            line.RelativeSize = false;

            line.Points = SampleEaseCurve(ease, EaseGraphPoints, OptionGraphWidth, EaseGraphHeight);
        }

        /// <summary>
        /// 按原版 DrawGraph 的方式采出折线顶点。
        /// 原版把 y 按整条曲线的最大值等比缩放（`max *= size.y`，
        /// 然后 `if (max > size.y) scale = size.y / max`），所以负值部分
        /// 仍然落在 0 以下、会被画到框外——这是原版就有的行为，照搬。
        /// </summary>
        private static Vector2[] SampleEaseCurve(DG.Tweening.Ease ease, int pointCount,
            float width, float height)
        {
            int count = Mathf.Max(2, pointCount);
            var points = new Vector2[count];
            float max = 0f;            for (int i = 0; i < count; i++)
            {
                float ratio = i / (float)(count - 1);
                float value = Preview.PreviewEase.EasedValue(0f, 1f, ratio, ease);
                points[i] = new Vector2(ratio * width, value * height);
                if (value > max)
                {
                    max = value;
                }
            }
            max *= height;
            float scale = max > height ? height / max : 1f;
            for (int i = 0; i < count; i++)
            {
                points[i] = new Vector2(points[i].x, points[i].y * scale);
            }
            return points;
        }

        // ---------------------------------------------------------- 弹出层外观

        private const float PopupOptionHeight = 26f;
        private const float PopupRadius = 6f;
        private const float PopupGap = 3f;
        private const float PopupMargin = 6f;
        private const float PopupMinListHeight = 78f;
        // 标签列起点。它和勾之间留出的那段，就是勾的位置。
        private const float PopupCheckColumn = 22f;
        // 标签的高度。比行高矮，这样缓速项可以让文字在行内垂直居中，
        // 而不是靠"行高 - 字号"这种跟字体度量绑死的算法。
        private const float PopupLabelHeight = 20f;

        private static readonly Color PopupSurface = new Color(0.105f, 0.115f, 0.142f, 1f);
        private static readonly Color PopupHover = new Color(0.165f, 0.180f, 0.215f, 1f);
        private static readonly Color PopupSelected = new Color(0.070f, 0.225f, 0.242f, 1f);
        private static readonly Color PopupSelectedHover = new Color(0.090f, 0.290f, 0.310f, 1f);
        private static readonly Color PopupBorder = new Color(0.270f, 0.300f, 0.340f, 0.90f);
        private static readonly Color PopupShadow = new Color(0f, 0f, 0f, 0.45f);

        /// <summary>
        /// 弹出层的外框：投影 + 圆角面 + 一圈描边。顺序就是子节点的顺序，
        /// 后建的画在上面，所以描边最后建。
        /// </summary>
        private void ApplyPopupFrame(RectTransform popup, float width, float height)
        {
            Image shadow = CreateChildImage("Shadow", popup, -1f, -3f, width + 2f, height + 6f);
            shadow.sprite = GetRoundedSprite(24, PopupRadius + 1f, false);
            shadow.type = Image.Type.Sliced;
            shadow.fillCenter = true;
            shadow.color = PopupShadow;

            Image surface = CreateChildImage("Surface", popup, 0f, 0f, width, height);
            surface.sprite = GetRoundedSprite(24, PopupRadius, false);
            surface.type = Image.Type.Sliced;
            surface.fillCenter = true;
            surface.color = PopupSurface;
            // 这层是"点空白处"的命中目标，所以必须能吃到射线。
            surface.raycastTarget = true;

            Image frame = CreateChildImage("Frame", popup, 0f, 0f, width, height);
            frame.sprite = GetRoundedSprite(24, PopupRadius, true);
            frame.type = Image.Type.Sliced;
            frame.fillCenter = true;
            frame.color = PopupBorder;
        }

        /// <summary>
        /// 锚点按面板坐标给出的实际宽度。直接读 rect.width 拿到的是
        /// 局部值，面板被缩放时和屏幕上看到的宽度对不上。
        /// </summary>
        private float AnchorWidthInPanelUnits(RectTransform anchor)
        {
            float scale = Mathf.Abs(anchor.lossyScale.x);
            if (scale < 0.0001f)
            {
                scale = 1f;
            }
            return anchor.rect.width * scale;
        }

        private void LoadoutCaret(Image caret, RectTransform anchor, float centerY)
        {
            RectTransform rect = caret.transform as RectTransform;
            if (rect == null)
            {
                return;
            }
            float right = AnchorWidthInPanelUnits(anchor) - 4f;
            SetTopLeft(rect, right - 26f, centerY - 5f, 14f, 10f);
        }

        private void LoadoutAnchor(RectTransform anchor, string display)
        {
            if (enumAnchorButton == null)
            {
                return;
            }
            Image background = enumAnchorButton.targetGraphic as Image;
            if (background != null)
            {
                background.color = EnumAnchorOpen;
            }
            if (enumAnchorCaret != null)
            {
                // 展开时箭头翻过来，指向"收起来的方向"。
                enumAnchorCaret.rectTransform.localScale = new Vector3(1f, -1f, 1f);
                enumAnchorCaret.color = Accent;
            }
            TextMeshProUGUI label = null;
            Transform labelTransform = anchor.Find("Value");
            if (labelTransform != null)
            {
                label = labelTransform.GetComponent<TextMeshProUGUI>();
            }
            if (label != null && !string.IsNullOrEmpty(display))
            {
                label.text = display;
            }
        }

        private static readonly Color EnumAnchorOpen = new Color(1.18f, 1.18f, 1.18f, 1f);

        private static object ParseEnumValue(ADOFAI.PropertyInfo info, string name)
        {
            try
            {
                return Enum.Parse(info.enumType, name);
            }
            catch (Exception)
            {
                return name;
            }
        }

        private string ReadEnumName(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
        {
            object raw = null;
            try
            {
                raw = levelEvent[info.name];
            }
            catch (Exception)
            {
                // 落到默认分支。
            }
            if (raw == null)
            {
                return string.Empty;
            }
            string name = raw.ToString();
            if (info.enumType != null)
            {
                try
                {
                    name = Enum.GetName(info.enumType, raw) ?? name;
                }
                catch (Exception)
                {
                    // 保留原始的 ToString。
                }
            }
            return name;
        }

        private static string DisplayEnumName(LevelEvent levelEvent, ADOFAI.PropertyInfo info, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            string localised = null;
            if (info.enumType != null)
            {
                localised = RDString.GetEnumValue(info.enumType.Name, name);
            }
            return string.IsNullOrEmpty(localised) ? name : localised;
        }

        // ---------------------------------------------------------------- 写入路径

        /// <summary>
        /// 唯一的写入路径。SaveState 会给整个关卡拍快照，所以它必须在
        /// 每次编辑手势里调用一次，绝不能每敲一个键调一次：
        /// 输入框在 onEndEdit 提交，开关和枚举选择本身就是单次手势。
        /// </summary>
        private void Commit(LevelEvent levelEvent, string key, object value)
        {
            if (levelEvent == null || !IsStillLive(levelEvent))
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            // 写入前先把被替换的内容抓下来，这样这次编辑才能撤销。
            //
            // 撤销归工作台自己的历史，而不是 scnEditor.SaveState：
            // 那个方法可能已被替换，替换后的撤销只回放它自己 save-scope
            // 窗口记下的改动，所以像这样直接写字段它根本撤不回来。
            object previousValue;
            levelEvent.TryGet<object>(key, out previousValue);
            bool previousDisabled = levelEvent.disabled != null
                && levelEvent.disabled.ContainsKey(key)
                && levelEvent.disabled[key];

            try
            {
                levelEvent[key] = value;
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PropertyPanel] write " + key + " failed: " + exception);
                QueueRefresh();
                return;
            }

            // 动一个值就意味着作者想让它生效；原生面板
            // 在编辑被禁用的属性时也是这么做的。
            bool disabled;
            if (levelEvent.disabled != null
                && levelEvent.disabled.TryGetValue(key, out disabled) && disabled)
            {
                levelEvent.disabled[key] = false;
            }

            // 把新值推到活对象上。
            //
            // 只写 levelEvent[key] 只更新了存档数据。装饰物自己的字段
            // （scaleVec、rotAngle、opacity……）由 scrDecoration.Setup 填充，
            // 它从事件里把这些值读回来；而预览求值器的逐帧
            // 状态就是从这些字段播种的，不是从事件。没有这一步，
            // 数字在文件里变了，画面却原封不动。
            //
            // UpdateDecorationObject 是编辑器自己的单对象刷新；它会为绑定到
            // 这个事件的装饰物重跑 Setup。UpdateDecorationObjects（复数）
            // 是刻意不用的：它会清掉并重建每一个装饰物，
            // 从而销毁求值器持有引用的那些对象。
            if (levelEvent.info != null && levelEvent.info.isDecoration)
            {
                try
                {
                    editor.UpdateDecorationObject(levelEvent);
                }
                catch (Exception exception)
                {
                    Main.Logger.Error("[PropertyPanel] refresh after write to " + key
                        + " failed: " + exception);
                }
            }

            // 预览在 Analyze 期间从那些字段缓了一份逐装饰物基线，
            // 所以这次编辑要等一次重建才会上屏。求值器确实会
            // 靠自己的来源签名察觉到变化，但只在它下一次
            // 周期性检查时；显式请求一次能让编辑在发生的那一帧就生效，
            // 这正是「敲个数字就看见它动」所需要的。
            EditorPlaybackBridge.RequestEvaluatorRebuild("property-commit");

            // 最后才记录，在写入和刷新都成功之后，这样一条撤销
            // 记录不会描述一次并未发生的编辑。
            EditorCommandHistory.RecordPropertyEdit(
                editor,
                levelEvent,
                key,
                previousValue,
                value,
                previousDisabled,
                false);

            QueueRefresh();
        }

        // ---------------------------------------------------------------- 预览

        private void ShowPreview(LevelEvent levelEvent)
        {
            if (previewHost == null)
            {
                return;
            }

            // 只有装饰物事件才有精灵可用来辨认。为
            // 每个 MoveTrack / MoveCamera 事件都显示一个空框，只会白白浪费面板顶部。
            bool isDecoration = levelEvent != null
                && levelEvent.info != null
                && levelEvent.info.isDecoration;
            string filename = isDecoration ? ResolveDecorationImage(levelEvent) : null;
            if (!isDecoration)
            {
                previewHost.gameObject.SetActive(false);
                return;
            }

            previewHost.gameObject.SetActive(true);
            previewCaption.text = string.IsNullOrEmpty(filename) ? "<无图片>" : filename;

            Sprite sprite = null;
            if (!string.IsNullOrEmpty(filename))
            {
                scrDecorationManager manager = scrDecorationManager.instance;
                TextureManager holder = manager == null ? null : manager.imageHolder;
                if (holder != null && holder.customSprites != null)
                {
                    TextureManager.CustomSprite custom;
                    if (holder.customSprites.TryGetValue(filename, out custom) && custom != null)
                    {
                        sprite = custom.sprite;
                    }
                }
            }

            previewImage.texture = sprite == null ? null : sprite.texture;
            previewImage.color = sprite == null ? new Color(1f, 1f, 1f, 0f) : Color.white;
            previewPlaceholder.gameObject.SetActive(sprite == null);

            // 贴合取决于纹理的宽高比，所以这里要和
            // 改变尺寸时一样重算。
            LayoutPreviewSlot();
        }

        /// <summary>
        /// MoveDecorations 本身不带精灵：它持有的是一组目标，
        /// 效果开始时施加到某个装饰物上。要显示「这是哪个装饰物」，
        /// 就把 tag 反查回创建该装饰物的事件，
        /// 用那个事件的图片。如果移动事件覆盖了图片，
        /// 覆盖后的图片才是真正会上屏的，所以它优先。
        /// </summary>
        private string ResolveDecorationImage(LevelEvent levelEvent)
        {
            if (levelEvent == null || levelEvent.info == null || !levelEvent.info.isDecoration)
            {
                return null;
            }

            if (!IsDisabled(levelEvent, "decorationImage"))
            {
                string own = SafeGet(levelEvent, "decorationImage", string.Empty);
                if (!string.IsNullOrEmpty(own))
                {
                    return own;
                }
            }

            string tag = SafeGet(levelEvent, "tag", string.Empty);
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.decorations == null)
            {
                return null;
            }

            for (int i = 0; i < editor.decorations.Count; i++)
            {
                LevelEvent candidate = editor.decorations[i];
                if (candidate == null || candidate.info == null || !candidate.info.isDecoration)
                {
                    continue;
                }
                if (!string.Equals(SafeGet(candidate, "tag", string.Empty), tag, StringComparison.Ordinal))
                {
                    continue;
                }
                string image = SafeGet(candidate, "decorationImage", string.Empty);
                if (!string.IsNullOrEmpty(image))
                {
                    return image;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- 取值辅助

        private static T SafeGet<T>(LevelEvent levelEvent, ADOFAI.PropertyInfo info, T fallback)
        {
            if (levelEvent == null || info == null)
            {
                return fallback;
            }
            try
            {
                return levelEvent.Get<T>(info.name, fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static T SafeGet<T>(LevelEvent levelEvent, string key, T fallback)
        {
            if (levelEvent == null || string.IsNullOrEmpty(key))
            {
                return fallback;
            }
            try
            {
                return levelEvent.Get<T>(key, fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static Color SafeGetColor(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
        {
            if (levelEvent == null || info == null)
            {
                return Color.white;
            }
            try
            {
                return levelEvent.GetColor(info.name);
            }
            catch (Exception)
            {
                return Color.white;
            }
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatInt(float value)
        {
            return Mathf.RoundToInt(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string ColorToHex(Color color)
        {
            return ((int)(Mathf.Clamp01(color.r) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.g) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.b) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.a) * 255f)).ToString("x2");
        }

        internal static bool TryParseHex(string text, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string hex = text.Trim().TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8)
            {
                return false;
            }
            int r, g, b, a = 255;
            if (!int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out r)
                || !int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out g)
                || !int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out b))
            {
                return false;
            }
            if (hex.Length == 8
                && !int.TryParse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out a))
            {
                return false;
            }
            color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }
    }

    /// <summary>
    /// 下拉选项的 hover 底色。
    ///
    /// 没有用 Button 自带的 ColorTint：那套会把高亮色乘到当前项已经标好的
    /// 「选中」底色上，高亮之后反而看不出选的是哪一项。这里让 hover 色
    /// 由每一项自己带着，选中项和普通项各有一套。
    /// </summary>
    internal sealed class EnumOptionHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal Image Background;
        internal Color Idle;
        internal Color Hovered;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (Background != null)
            {
                Background.color = Hovered;
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (Background != null)
            {
                Background.color = Idle;
            }
        }
    }

    /// <summary>
    /// 参数栏里输入框的 hover / focus 边框。
    /// </summary>
    internal sealed class FieldHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal Image Rim;
        internal TMP_InputField Field;
        internal Color Idle;
        internal Color Hovered;
        internal Color Focused;

        private bool pointerInside;

        public void OnPointerEnter(PointerEventData eventData)
        {
            pointerInside = true;
            Refresh();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            pointerInside = false;
            Refresh();
        }

        internal void Refresh()
        {
            if (Rim == null)
            {
                return;
            }
            bool focused = Field != null && Field.isFocused;
            Rim.color = focused ? Focused : (pointerInside ? Hovered : Idle);
        }
    }
}
