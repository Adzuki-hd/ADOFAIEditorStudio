using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 编辑器左下角那颗「打开工作台」的按钮（uGUI 版）。
    ///
    /// **为什么不用悬浮球了**：悬浮球飘在画面里，跟原生编辑器自己的按钮不是一套
    /// 视觉，位置还会随分数条 / 判定面板打架。现在改成原生底栏风格的一颗小按钮，
    /// 停在左下角（播放键那一排的右侧空档），和原生按钮同一档尺寸与配色。
    ///
    /// **自己一块画布**：工作台关着时整棵树会被 `SetActive(false)`，挂在那棵树上
    /// 的东西跟着消失；而且它要盖在原生编辑器 UI 上面。
    ///
    /// 只在「编辑器里、工作台关着、没在游玩」时出现 —— 和旧悬浮球同一套规则。
    /// </summary>
    internal sealed class AesOpenButton : MonoBehaviour
    {
        /// <summary>
        /// 相对**原生播放键**的直径比例。
        ///
        /// 原生的「回到开头」那颗大约就是播放键的 0.56 倍，这里取同一个比例，
        /// 并排站着才像同一套控件。
        /// </summary>
        private const float SizeRatioToPlay = 0.56f;
        private const float MinSize = 36f;
        private const float MaxSize = 120f;
        /// <summary>和播放键之间的缝（占自身直径的比例）。贴着但不重叠。</summary>
        private const float GapRatio = 0.06f;
        /// <summary>图标留白（占直径的比例）：0.205 → 图形约占圆的 59%。</summary>
        private const float IconInsetRatio = 0.205f;
        private const string IconFile = "AesIcon.rgba";
        /// <summary>图标边长（和随包那份裸 RGBA 对齐，见 tools/make-aes-icon）。</summary>
        private const int IconSize = 128;

        private RectTransform root;
        private RectTransform iconRect;
        private Image background;
        private Image icon;
        private Text fallback;
        private bool placing = true;
        private bool hovered;
        private bool pressed;

        /// <summary>原生那两颗按钮是**白底 + 灰描边 + 黑图形**，这里照抄。</summary>
        private static readonly Color IdleColor = Color.white;
        private static readonly Color HoverColor = new Color(0.88f, 0.88f, 0.88f, 1f);
        private static readonly Color PressColor = new Color(0.74f, 0.74f, 0.74f, 1f);
        private static readonly Color GlyphColor = new Color(0.07f, 0.07f, 0.07f, 1f);

        internal static void Install(Transform parent)
        {
            GameObject canvasObject = new GameObject("AES.OpenButton", typeof(RectTransform));
            canvasObject.transform.SetParent(parent, false);
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 原生编辑器 UI 在很后面，这颗要压在上面；工作台的浮层是 32000，
            // 但它只在工作台关着时才显示，两者不会同时在场。
            canvas.sortingOrder = 32000;

            // **1 单位 = 1 像素**：位置和大小都直接照原生播放键的屏幕矩形算
            // （见 `Place`），不经缩放，任何分辨率 / 带鱼屏都不会错位。
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            AesOpenButton button = canvasObject.AddComponent<AesOpenButton>();
            button.Build(canvasObject.transform);
        }

        private void Build(Transform canvasTransform)
        {
            GameObject rootObject = new GameObject("Button", typeof(RectTransform));
            rootObject.transform.SetParent(canvasTransform, false);
            root = (RectTransform)rootObject.transform;
            root.anchorMin = new Vector2(0f, 0f);
            root.anchorMax = new Vector2(0f, 0f);
            // 以中心定位：位置是每帧照播放键算出来的（见 `Place`）。
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(MaxSize, MaxSize);

            background = rootObject.AddComponent<Image>();
            background.sprite = CircleSprite();
            background.type = Image.Type.Simple;
            background.color = IdleColor;
            background.raycastTarget = true;

            Sprite glyph = LoadIcon();
            if (glyph != null)
            {
                GameObject iconObject = new GameObject("Icon", typeof(RectTransform));
                iconObject.transform.SetParent(root, false);
                iconRect = (RectTransform)iconObject.transform;
                iconRect.anchorMin = Vector2.zero;
                iconRect.anchorMax = Vector2.one;
                iconRect.offsetMin = Vector2.zero;
                iconRect.offsetMax = Vector2.zero;
                icon = iconObject.AddComponent<Image>();
                icon.sprite = glyph;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                // 图标贴图是白色的；乘上近黑色就得到原生那种黑图形。
                icon.color = GlyphColor;
            }
            else
            {
                // 图标文件缺失时也必须有东西可点 —— 退化成字母。
                GameObject letterObject = new GameObject("Letter", typeof(RectTransform));
                letterObject.transform.SetParent(root, false);
                RectTransform letterRect = (RectTransform)letterObject.transform;
                letterRect.anchorMin = Vector2.zero;
                letterRect.anchorMax = Vector2.one;
                letterRect.offsetMin = Vector2.zero;
                letterRect.offsetMax = Vector2.zero;
                fallback = letterObject.AddComponent<Text>();
                fallback.text = "AES";
                fallback.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                fallback.fontSize = 14;
                fallback.alignment = TextAnchor.MiddleCenter;
                fallback.color = new Color(0.85f, 0.85f, 0.85f, 1f);
                fallback.raycastTarget = false;
            }

            rootObject.SetActive(false);
        }

        private void Update()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            bool show = overlay != null
                && !overlay.IsVisible
                && EditorBridge.IsEditorActive()
                && !EditorStudioOverlay.IsNativePlayModeRunning();

            if (root.gameObject.activeSelf != show)
            {
                root.gameObject.SetActive(show);
                if (!show)
                {
                    hovered = false;
                    pressed = false;
                }
            }
            if (!show)
            {
                return;
            }

            Place();

            Vector2 mouse = Input.mousePosition;
            bool inside = RectTransformUtility.RectangleContainsScreenPoint(root, mouse, null);
            hovered = inside;

            if (inside && Input.GetMouseButtonDown(0))
            {
                pressed = true;
            }
            else if (pressed && Input.GetMouseButtonUp(0))
            {
                pressed = false;
                if (inside && overlay != null)
                {
                    overlay.OpenFromEditorButton();
                }
            }

            // 只改底色：图形始终是黑的（原生那两颗按钮也没有悬停变色，
            // 这里给一点反馈但不改变配色关系）。
            background.color = pressed && inside
                ? PressColor
                : (hovered ? HoverColor : IdleColor);
        }

        /// <summary>
        /// 贴着原生播放键站：**取播放键的屏幕矩形现算**，而不是写死坐标。
        ///
        /// 写死坐标在别的分辨率 / 带鱼屏上必然错位（原生底栏是按锚点摆的），
        /// 而播放键是稳定存在的控件（`scnEditor.playPause`）—— 拿它的实时矩形
        /// 当基准，缩放、窗口化、UI 缩放都跟着走。
        /// </summary>
        private void Place()
        {
            float diameter = MaxSize * 0.6f;
            Vector2 center = new Vector2(330f, 26f);
            scnEditor scene = scnEditor.instance;
            RectTransform play = null;
            if (scene != null && scene.playPause != null)
            {
                play = scene.playPause.transform as RectTransform;
            }
            if (play != null && play.rect.width > 1f)
            {
                Rect rect = ScreenRect(play);
                diameter = Mathf.Clamp(rect.height * SizeRatioToPlay, MinSize, MaxSize);
                // 右边缘 + 一点缝；纵向**底边对齐播放键的底边**，所以看上去是
                // 贴着底栏下沿站着，而不是悬在播放键中间那条线上。
                center = new Vector2(
                    rect.xMax + diameter * (GapRatio + 0.5f),
                    rect.yMin + diameter * 0.5f);
            }

            root.sizeDelta = new Vector2(diameter, diameter);
            root.position = new Vector3(center.x, center.y, 0f);

            float inset = diameter * IconInsetRatio;
            if (iconRect != null)
            {
                iconRect.offsetMin = new Vector2(inset, inset);
                iconRect.offsetMax = new Vector2(-inset, -inset);
            }
            if (fallback != null)
            {
                fallback.fontSize = Mathf.RoundToInt(diameter * 0.28f);
            }

            if (placing)
            {
                placing = false;
                Main.Logger.Log("[AES按钮] 就位 center=" + center + " 直径=" + diameter
                    + " 屏幕=" + Screen.width + "x" + Screen.height
                    + (play == null ? "（没找到播放键，退回固定坐标）" : string.Empty));
            }
        }

        /// <summary>控件在**屏幕像素**里占的矩形（含它所在的画布缩放）。</summary>
        private static Rect ScreenRect(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
        }

        // ------------------------------------------------------------ 资源

        private static Sprite circle;
        private static Sprite glyphSprite;

        /// <summary>
        /// 白底 + 灰描边的圆 —— 原生「播放 / 回到开头」那两颗就是这个长相：
        /// 实心白、外面一圈细灰线，图形是纯黑的。
        ///
        /// 描边直接烘进贴图（而不是用 Outline 组件）：Outline 是复制四个偏移
        /// 副本，在 64px 的圆上会糊成一圈脏边。
        /// </summary>
        private static Sprite CircleSprite()
        {
            if (circle != null)
            {
                return circle;
            }
            const int size = 128;
            // 最终显示 64px，所以这里 4px 环 ≈ 屏幕上的 2px。
            const float ring = 4f;
            float radius = size * 0.5f - 1f;
            Color ringColor = new Color(0.62f, 0.62f, 0.62f, 1f);
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = "ADOFAIEditorStudio.OpenButton";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - size * 0.5f;
                    float dy = y + 0.5f - size * 0.5f;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    // 边缘一像素内线性过渡，避免锯齿。
                    float cover = Mathf.Clamp01(radius - distance);
                    if (cover <= 0f)
                    {
                        texture.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
                        continue;
                    }
                    bool inRing = distance > radius - ring;
                    Color color = inRing ? ringColor : Color.white;
                    color.a = cover;
                    texture.SetPixel(x, y, color);
                }
            }
            texture.Apply();
            circle = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f);
            return circle;
        }

        /// <summary>
        /// 读 Mod 目录里的 `AesIcon.rgba`（128×128 裸 RGBA，透明底白色图形）。
        ///
        /// **不用 PNG**：`Texture2D.LoadImage` 在 ImageConversion 模块里，那个
        /// 程序集是按 netstandard 2.1 编的，和本项目引用的 2.0 冲突（CS1705）。
        /// 裸 RGBA 只需要 `LoadRawTextureData`，半点依赖都不用加。
        /// </summary>
        private static Sprite LoadIcon()
        {
            if (glyphSprite != null)
            {
                return glyphSprite;
            }
            try
            {
                string modPath = Main.Mod == null ? null : Main.Mod.Path;
                if (string.IsNullOrEmpty(modPath))
                {
                    return null;
                }
                string path = Path.Combine(modPath, IconFile);
                if (!File.Exists(path))
                {
                    Main.Logger.Log("[AES按钮] 找不到图标：" + path);
                    return null;
                }
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length != IconSize * IconSize * 4)
                {
                    Main.Logger.Log("[AES按钮] 图标大小不对：" + bytes.Length
                        + "，应为 " + (IconSize * IconSize * 4));
                    return null;
                }
                Texture2D texture = new Texture2D(IconSize, IconSize,
                    TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(bytes);
                texture.Apply();
                texture.name = "ADOFAIEditorStudio.OpenIcon";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                glyphSprite = Sprite.Create(texture,
                    new Rect(0f, 0f, IconSize, IconSize),
                    new Vector2(0.5f, 0.5f), 100f);
                return glyphSprite;
            }
            catch (System.Exception exception)
            {
                Main.Logger.Log("[AES按钮] 读图标失败：" + exception.Message);
                return null;
            }
        }
    }
}
