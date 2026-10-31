using System;
using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 调色盘。
    ///
    /// 照着原版那个 `RDColorPickerPopup` 做：**饱和/明度方块 + 色相条 +
    /// 透明度条 + HEX + RGBA 四格**。原版用的是另一套资源包里的控件
    /// （`CUIColorPicker`），拿不过来，所以这里一块块自己搭 —— 颜色模型
    /// 和它一致：**以 HSV 为主状态**，RGB/HEX 只是它的几种写法。
    ///
    /// 只负责「一块画布 + 几样输入」以及它们之间的同步。摆在哪儿、
    /// 什么时候收起，由参数栏管（它已经有弹出层那套：点外面收起、
    /// 面板重画时一并销毁）。
    ///
    /// **改色是实时写进谱面的**（这样画面跟着动），但**不实时记撤销** ——
    /// 那要按帧记，拖一次鼠标就是上百条历史。撤销由参数栏在收起时补记一条。
    /// </summary>
    internal sealed class EditorColorPicker : MonoBehaviour
    {
        internal const float SquareSize = 118f;
        private const float BarHeight = 14f;
        private const float FieldHeight = 22f;
        private const float Gap = 6f;
        private const float ChannelLabelWidth = 16f;
        private const float ChannelBoxWidth = 36f;

        /// <summary>盘面占的宽高。参数栏按这个尺寸摆弹出层。</summary>
        internal const float BodyWidth = 180f;
        internal const float BodyHeight = FieldHeight + Gap + SquareSize + Gap + BarHeight + Gap + BarHeight;

        private const float HexY = 0f;
        private const float SquareY = FieldHeight + Gap;
        private const float HueY = SquareY + SquareSize + Gap;
        private const float AlphaY = HueY + BarHeight + Gap;
        private const float ChannelX = SquareSize + 8f;

        private RectTransform body;
        private TMP_FontAsset font;

        private RawImage svImage;
        private RawImage hueImage;
        private RawImage alphaImage;
        private RectTransform svMarker;
        private RectTransform hueMarker;
        private RectTransform alphaMarker;
        private TMP_InputField hexField;
        private Image previewSwatch;
        private TMP_InputField[] channels;

        private Texture2D svTexture;
        /// <summary>方块贴图是按哪个色相画的。NaN = 还没画过。见 RefreshSvTexture。</summary>
        private float svTextureHue = float.NaN;
        private Texture2D alphaTexture;

        /// <summary>当前颜色。它由 hue/sat/val/alpha 推出来，反过来只有输入框会直接写它。</summary>
        private Color color = Color.white;
        private float hue;
        private float sat;
        private float val;

        /// <summary>
        /// **定下来**一个颜色的回调。参数栏接这个去写数据 + 刷画面。
        ///
        /// **只在松手和输入框改完时响，拖动途中一次都不响。**
        /// 每帧响的话，参数栏那边每帧都要重跑一次装饰物 Setup 和求值器 ——
        /// 那才是拖动卡顿的来源。盘子里自己的色块、指示环、色条是每帧更新的，
        /// 拖起来照旧跟手；画面等松手再变。
        /// </summary>
        private Action<Color> onPreview;

        /// <summary>定下来一个颜色的回调（松手 / 输入框改完），见 <see cref="onCommit"/> 的说明。</summary>
        private Action<Color> onCommit;

        /// <summary>自己在回填控件时把它置位：免得「改控件 → 又触发一次回调」绕成环。</summary>
        private bool syncing;

        /// <summary>透明度条和五个输入框的最小刷新间隔（秒）。见 UpdateSecondaryDisplays。</summary>
        private const float SecondaryInterval = 0.06f;
        private float nextSecondaryUpdate;
        private bool secondaryDirty;

        internal void Build(RectTransform host, TMP_FontAsset uiFont, Color initial,
            Action<Color> previewCallback, Action<Color> commitCallback)
        {
            body = host;
            font = uiFont;
            onPreview = previewCallback;
            onCommit = commitCallback;

            BuildSquare();
            BuildBars();
            BuildMarkers();
            BuildFields();

            SetColor(initial, false);
        }

        // ---------------------------------------------------------------- 搭

        private void BuildSquare()
        {
            RectTransform rect = Child("SatVal", body, 0f, SquareY, SquareSize, SquareSize);
            svImage = rect.gameObject.AddComponent<RawImage>();
            svImage.raycastTarget = true;
            AddGrab(rect, delegate(Vector2 uv)
            {
                sat = uv.x;
                val = uv.y;
                Push(false);
            });
        }

        private void BuildBars()
        {
            // **两条色带都是横的，摆在方块下面。**
            //
            // 原版就是这个排法，而且它同时定死了拖动的映射：横条上
            // 「拖动的方向」和「取值的方向」都是 x，`ColorGrab` 交出来的
            // `uv.x` 才成立。竖着摆的话 uv.x 落到条子的**宽度**上（十几像素），
            // 拖长度方向几乎不动 —— 那就是"不跟手"。
            RectTransform hueRect = Child("Hue", body, 0f, HueY, BodyWidth, BarHeight);
            hueImage = hueRect.gameObject.AddComponent<RawImage>();
            hueImage.texture = HueTexture();
            hueImage.raycastTarget = true;
            AddGrab(hueRect, delegate(Vector2 uv)
            {
                hue = uv.x;
                Push(false);
            });

            // 透明度条底下先垫一层棋盘：不垫的话「透明」和「深色」在这条上
            // 分不出来，半透明的颜色看着就是一块脏色。
            RectTransform checker = Child("AlphaChecker", body, 0f, AlphaY, BodyWidth, BarHeight);
            Image checkerImage = checker.gameObject.AddComponent<Image>();
            checkerImage.sprite = GetCheckerSprite(16);
            checkerImage.type = Image.Type.Tiled;
            checkerImage.color = new Color(1f, 1f, 1f, 0.18f);
            checkerImage.raycastTarget = false;

            RectTransform alphaRect = Child("Alpha", body, 0f, AlphaY, BodyWidth, BarHeight);
            // 棋盘要**排在 Alpha 底下**才画得出来。
            checker.SetSiblingIndex(alphaRect.transform.GetSiblingIndex());
            alphaImage = alphaRect.gameObject.AddComponent<RawImage>();
            alphaImage.raycastTarget = true;
            AddGrab(alphaRect, delegate(Vector2 uv)
            {
                color.a = uv.x;
                Push(false);
            });
        }

        private void BuildMarkers()
        {
            svMarker = Child("SatValMarker", body, 0f, 0f, 10f, 10f);
            Image svRing = svMarker.gameObject.AddComponent<Image>();
            svRing.sprite = GetRingSprite(12);
            svRing.type = Image.Type.Simple;
            svRing.color = Color.white;
            svRing.raycastTarget = false;

            hueMarker = MakeBarMarker("HueMarker");
            alphaMarker = MakeBarMarker("AlphaMarker");

        }

        private void BuildFields()
        {
            const float swatchSize = 22f;
            hexField = MakeField(body, "Hex", 0f, HexY, BodyWidth - swatchSize - 6f);

            // 盘里自己也要有一块预览。它是**唯一**每帧跟着拖动的那个可见反馈 ——
            // 画面要等松手才变，没有它拖的时候就没有参照。
            RectTransform swatchRect = Child("Preview", body,
                BodyWidth - swatchSize, HexY, swatchSize, swatchSize);
            Image swatchImage = swatchRect.gameObject.AddComponent<Image>();
            swatchImage.raycastTarget = false;
            previewSwatch = swatchImage;
            hexField.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing)
                {
                    return;
                }
                Color parsed;
                if (EditorPropertyPanel.TryParseHex(text, out parsed))
                {
                    SetColor(parsed, true);
                }
                else
                {
                    // 打错了就把真值写回去，别留一个非法字符串在框里。
                    syncing = true;
                    hexField.SetTextWithoutNotify(EditorPropertyPanel.ColorToHex(color));
                    syncing = false;
                }
            });

            // R/G/B/A 竖排在方块右边，**字母在左、数字框在右** —— 原版就是这个
            // 排法。横排成一溜、没有字母的话，光看四个数字分不清谁是谁。
            string[] names = { "R", "G", "B", "A" };
            channels = new TMP_InputField[names.Length];
            float rowStep = (SquareSize - FieldHeight) / (names.Length - 1);
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                float y = SquareY + i * rowStep;

                TextMeshProUGUI label = Child("Label" + names[i], body,
                    ChannelX, y, ChannelLabelWidth, FieldHeight).gameObject.AddComponent<TextMeshProUGUI>();
                if (font != null)
                {
                    label.font = font;
                }
                label.text = names[i];
                label.fontSize = 11.5f;
                label.color = UiTheme.Current.TextSecondary;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.raycastTarget = false;

                TMP_InputField field = MakeField(body, names[i],
                    ChannelX + ChannelLabelWidth, y, ChannelBoxWidth);
                field.contentType = TMP_InputField.ContentType.IntegerNumber;
                field.onEndEdit.AddListener(delegate(string text)
                {
                    if (syncing)
                    {
                        return;
                    }

                    int parsed;
                    if (!int.TryParse(text, out parsed))
                    {
                        WriteFields();
                        return;
                    }

                    Color next = color;
                    next[index] = Mathf.Clamp01(parsed / 255f);
                    SetColor(next, true);
                });
                channels[i] = field;
            }
        }

        private TMP_InputField MakeField(RectTransform parent, string name, float x, float y, float width)
        {
            RectTransform rect = Child(name, parent, x, y, width, FieldHeight);

            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = GetRoundedSprite(18, 4f);
            background.type = Image.Type.Sliced;
            background.fillCenter = true;
            background.color = InputFill;

            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.richText = false;
            field.restoreOriginalTextOnEscape = false;
            field.caretColor = Color.white;
            field.selectionColor = new Color(1f, 1f, 1f, 0.25f);
            field.textViewport = rect;

            RectTransform textRect = Child("Text", rect, 6f, 0f, width - 12f, FieldHeight);
            TextMeshProUGUI text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.fontSize = 11.5f;
            text.color = UiTheme.Current.TextPrimary;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            field.textComponent = text;
            return field;
        }

        private RectTransform MakeBarMarker(string name)
        {
            // 横条上的指示线是**竖**的：细高一条，横着跟着取值走。
            RectTransform marker = Child(name, body, 0f, 0f, 4f, BarHeight + 4f);
            Image image = marker.gameObject.AddComponent<Image>();
            image.color = Color.white;
            image.raycastTarget = false;
            return marker;
        }

        private void AddGrab(RectTransform area, Action<Vector2> onGrab)
        {
            ColorGrab grab = area.gameObject.AddComponent<ColorGrab>();
            grab.Area = area;
            grab.OnGrab = onGrab;
            grab.OnRelease = ReportCommit;
        }

        private static RectTransform Child(string name, RectTransform parent, float x, float y, float width, float height)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        // ---------------------------------------------------------------- 颜色

        /// <summary>外部塞一个颜色进来（打开时用，或输入框改完回填）。</summary>
        internal void SetColor(Color next, bool notify)
        {
            float h;
            float s;
            float v;
            Color.RGBToHSV(next, out h, out s, out v);

            // 灰阶（含纯黑纯白）时 RGBToHSV 给不出色相，会把色相条打回最左。
            // 这种时候**保留原来的色相**：作者把明度拖到 0 只是想变暗，
            // 不该顺手把色相一起丢掉 —— 再拖回亮处颜色就变了。
            if (s > 0.0001f)
            {
                hue = h;
            }
            sat = s;
            val = v;
            color = next;
            // 清掉节流窗口：外部塞进来的颜色要**立刻**全刷 ——
            // 作者刚打完一个十六进制，色条和那几个框必须马上是对的。
            nextSecondaryUpdate = 0f;
            // 用回推出来的 RGB，和 h/s/v 保持一致（RGBToHSV 有舍入）。
            PushColor(notify);
        }

        private void Push(bool commit)
        {
            // **alpha 要自己护住。** `Color.HSVToRGB` 返回的 Color alpha 恒为 1，
            // 而这里每次都是整个覆盖 —— 不护的话，拖透明度条设进去的值会被
            // 这一行当场抹回 1（条子和指示线都不动，看着就是"拉不动"），
            // 而且设好透明度再去拖方块或色相条，透明度同样会被冲掉。
            float alpha = color.a;
            color = Color.HSVToRGB(hue, sat, val, false);
            color.a = alpha;
            PushColor(commit);
        }

        /// <summary>松手：把定下来的颜色报给参数栏。拖动途中不报。</summary>
        private void ReportCommit()
        {
            if (onCommit != null)
            {
                onCommit(color);
            }
        }

        private void PushColor(bool commit)
        {
            RefreshSvTexture();
            UpdateSecondaryDisplays();
            LayoutMarkers();

            // 每帧都要：参数栏那边只是把**色块**染一下色，一个 Image 的颜色，
            // 便宜；而盘子弹层可能把那一行盖住，不给这个反馈就没处看颜色了。
            if (onPreview != null)
            {
                onPreview(color);
            }

            if (commit)
            {
                ReportCommit();
            }
        }

        /// <summary>
        /// 透明度条和那五个输入框**按节流刷新**。
        ///
        /// 这两样每帧都要重做一遍：透明度条是一次贴图上传，五个输入框是
        /// 五次 TMP 文本重建（TMP 改一次文本就要重算一次网格）。拖动时每秒
        /// 会来六十次，而肉眼分不出 15 次和 60 次的差别。
        ///
        /// 色块和三个指示标记**不节流** —— 那只是几个矩形的位置和颜色，
        /// 便宜，而且拖起来跟手才像在调色。
        ///
        /// 节流要**带尾**：见 Update。少了那一手，松手后最后那一点变化会
        /// 停在半路（色条对了，数字框还是上一个值）。
        /// </summary>
        private void UpdateSecondaryDisplays()
        {
            secondaryDirty = true;
            FlushSecondaryDisplays();
        }

        private void FlushSecondaryDisplays()
        {
            float now = Time.unscaledTime;
            if (!secondaryDirty || now < nextSecondaryUpdate)
            {
                return;
            }

            secondaryDirty = false;
            nextSecondaryUpdate = now + SecondaryInterval;
            RefreshAlphaTexture();
            WriteFields();
        }

        private void Update()
        {
            // 【临时探针】见 EditorPropertyPanel.Update 里的说明。
            long __watch = System.Diagnostics.Stopwatch.GetTimestamp();
            FlushSecondaryDisplays();
            PreviewDiagnostics.RecordSince("Picker.U", __watch);
        }

        private void RefreshSvTexture()
        {
            // **方块那张贴图只由色相决定。**
            //
            // 拖饱和度/明度时它一个字都不用改 —— 原来每帧无条件重建
            // （118² ≈ 1.4 万个像素、每个像素一次 HSVToRGB），那是拖动卡顿的
            // 主要来源之一。这里比一下色相，没变就直接返回。
            if (svTexture != null && Mathf.Abs(svTextureHue - hue) < 0.0005f)
            {
                return;
            }
            svTextureHue = hue;

            // 64 而不是 118：这是一块**平滑**渐变，双线性放大之后肉眼分不出来，
            // 而像素数只有三分之一不到。上面那条色相判断挡掉了大部分重建，
            // 剩下拖色相条那一路靠这个把单帧开销压下去。
            const int size = 64;
            if (svTexture == null)
            {
                svTexture = NewTexture(size, size);
                svImage.texture = svTexture;
            }

            // 纹理的第 0 行在**下**边，而 RawImage 把 uv(0,0) 放在左下，
            // 两者一致，所以 y 直接就是明度、x 直接就是饱和度，不用翻。
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = y / (float)(size - 1);
                for (int x = 0; x < size; x++)
                {
                    float s = x / (float)(size - 1);
                    pixels[y * size + x] = Color.HSVToRGB(hue, s, v, false);
                }
            }
            svTexture.SetPixels32(pixels);
            svTexture.Apply(false);
        }

        private void RefreshAlphaTexture()
        {
            if (alphaTexture == null)
            {
                alphaTexture = NewTexture(2, 1);
                alphaTexture.wrapMode = TextureWrapMode.Clamp;
                alphaImage.texture = alphaTexture;
            }

            Color low = color;
            low.a = 0f;
            Color high = color;
            high.a = 1f;
            alphaTexture.SetPixels(new[] { low, high });
            alphaTexture.Apply(false);
        }

        private void LayoutMarkers()
        {
            // 方块上的环：位置就是 (饱和度, 明度)，y 从上往下所以要翻一下。
            //
            // **要加上 SquareY。** 环的父节点是 body，而方块在 body 里是从
            // SquareY 处开始的 —— 漏掉它环就永远偏高那一截，看着像"跟鼠标差一点"。
            float x = Mathf.Clamp01(sat) * SquareSize;
            float y = SquareY + (1f - Mathf.Clamp01(val)) * SquareSize;
            SetTopLeft(svMarker, x - 5f, y - 5f, 10f, 10f);

            float hueX = Mathf.Clamp01(hue) * BodyWidth;
            SetTopLeft(hueMarker, hueX - 2f, HueY - 2f, 4f, BarHeight + 4f);

            float alphaX = Mathf.Clamp01(color.a) * BodyWidth;
            SetTopLeft(alphaMarker, alphaX - 2f, AlphaY - 2f, 4f, BarHeight + 4f);
        }

        private void WriteFields()
        {
            syncing = true;
            try
            {
                if (previewSwatch != null)
                {
                    previewSwatch.color = color;
                }
                hexField.SetTextWithoutNotify(EditorPropertyPanel.ColorToHex(color));
                for (int i = 0; i < channels.Length; i++)
                {
                    channels[i].SetTextWithoutNotify(
                        Mathf.RoundToInt(Mathf.Clamp01(color[i]) * 255f).ToString());
                }
            }
            finally
            {
                syncing = false;
            }
        }

        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private void OnDestroy()
        {
            Destroy(svTexture);
            Destroy(alphaTexture);
            // 色相条那张是**静态共享**的，不在这里销毁 —— 销毁它会让
            // 之后开出来的调色盘拿到一张已经没了的贴图。
        }

        // ---------------------------------------------------------------- 资源

        private static Texture2D NewTexture(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        // 色相条那张贴图全场只有一张，不随颜色变，所以按静态缓着。
        // 名字不和实例那个 `hueTexture`（方块用的那张，随色相重建）撞。
        private static Texture2D sharedHueTexture;

        private static Texture2D HueTexture()
        {
            if (sharedHueTexture != null)
            {
                return sharedHueTexture;
            }

            const int steps = 128;
            sharedHueTexture = NewTexture(steps, 1);
            Color[] pixels = new Color[steps];
            for (int i = 0; i < steps; i++)
            {
                pixels[i] = Color.HSVToRGB(i / (float)(steps - 1), 1f, 1f, false);
            }
            sharedHueTexture.SetPixels(pixels);
            sharedHueTexture.Apply(false);
            return sharedHueTexture;
        }

        private static Sprite checkerSprite;

        /// <summary>透明底纹：两格灰白交替，铺满用。</summary>
        private static Sprite GetCheckerSprite(int size)
        {
            if (checkerSprite != null)
            {
                return checkerSprite;
            }

            int cell = Mathf.Max(1, size / 2);
            Texture2D texture = NewTexture(size, size);
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool dark = ((x / cell) + (y / cell)) % 2 == 0;
                    pixels[y * size + x] = dark ? new Color(0f, 0f, 0f, 0.35f) : new Color(0f, 0f, 0f, 0f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false);
            checkerSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f);
            checkerSprite.hideFlags = HideFlags.HideAndDontSave;
            return checkerSprite;
        }

        private static Sprite ringSprite;

        /// <summary>色相/饱和度方块上那个指示环：白环 + 一圈极淡的黑边，</summary>
        private static Sprite GetRingSprite(int size)
        {
            if (ringSprite != null)
            {
                return ringSprite;
            }

            Texture2D texture = NewTexture(size, size);
            float radius = size * 0.5f - 1.5f;
            float center = size * 0.5f - 0.5f;
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.Clamp01(1.5f - Mathf.Abs(distance - radius));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, ring);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false);
            ringSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f);
            ringSprite.hideFlags = HideFlags.HideAndDontSave;
            return ringSprite;
        }

        private static Sprite roundedSprite;

        private static Sprite GetRoundedSprite(int size, float radius)
        {
            if (roundedSprite != null)
            {
                return roundedSprite;
            }

            Texture2D texture = NewTexture(size, size);
            Color[] pixels = new Color[size * size];
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
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false);
            float border = radius + 1f;
            roundedSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            roundedSprite.hideFlags = HideFlags.HideAndDontSave;
            return roundedSprite;
        }

        private static Color InputFill
        {
            get { return UiTheme.Current.SurfaceRaised; }
        }
    }

    /// <summary>
    /// 一块能拖的色区。
    ///
    /// 单独一个组件是因为 uGUI 的指针事件是**按物体**派的：一个
    /// MonoBehaviour 只能收到挂在它自己 GameObject 上的事件，所以
    /// 方块的拖动没法从 <see cref="EditorColorPicker"/> 那边直接收。
    /// </summary>
    internal sealed class ColorGrab : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IEndDragHandler, IPointerUpHandler
    {
        internal RectTransform Area;
        /// <summary>归一化到 0..1 的位置，原点在**左下**。</summary>
        internal Action<Vector2> OnGrab;
        /// <summary>松手。拖动途中一次都不响 —— 参数栏只在这一次去写数据。</summary>
        internal Action OnRelease;

        public void OnPointerDown(PointerEventData eventData)
        {
            Report(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Report(eventData);
        }

        // 两条收尾路都要接：拖过之后走 IEndDragHandler，原地按一下只走
        // IPointerUpHandler。少接一条，"点一下选个颜色"就永远不落数据。
        public void OnPointerUp(PointerEventData eventData)
        {
            Release();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            Release();
        }

        private void Release()
        {
            if (OnRelease != null)
            {
                OnRelease();
            }
        }

        private void Report(PointerEventData eventData)
        {
            if (Area == null || OnGrab == null)
            {
                return;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    Area, eventData.position, eventData.pressEventCamera, out local))
            {
                return;
            }

            Rect rect = Area.rect;
            OnGrab(new Vector2(
                Mathf.Clamp01((local.x - rect.xMin) / Mathf.Max(1f, rect.width)),
                Mathf.Clamp01((local.y - rect.yMin) / Mathf.Max(1f, rect.height))));
            eventData.Use();
        }
    }
}
