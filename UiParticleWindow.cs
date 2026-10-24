using ADOFAI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 粒子编辑器浮窗（uGUI 版）。
    ///
    /// **挂在工作台那块已经验证能显示的浮层画布上**（`overlay.PopupLayer`，
    /// sortingOrder 32000）—— 自己另建根 Canvas 试过两次：根画布的矩形由
    /// Unity 在 Canvas.Update 里才撑开，建完立刻摆子物体坐标全是 0，
    /// 窗口被摆到屏幕外（探针实测世界左下 (225,-240)）。复用现成画布最稳。
    ///
    /// 只做壳 + 属性行；中间那块实时预览仍由游戏自己的粒子对象驱动。
    /// </summary>
    internal sealed class UiParticleWindow : MonoBehaviour
    {
        // 左参数 / 右预览，窗口整体加宽加高。
        private const float WindowWidth = 840f;
        private const float WindowHeight = 620f;
        private const float TitleHeight = 34f;
        private const float LeftWidth = 412f;
        private const float PreviewInset = 8f;

        private static UiParticleWindow instance;

        private RectTransform window;
        private UiParticlePane particlePane;
        private int lastParticleVersion;

        // 预览：画面由共用宿主出（RenderTexture + 相机 + 装饰物副本都在它里面），
        // 这边只负责把它的 texture 贴到 RawImage 上。
        private RawImage previewImage;
        private readonly ParticlePreviewHost previewHost = new ParticlePreviewHost();
        private TextMeshProUGUI previewHint;

        internal static void Open(LevelEvent target)
        {
            Main.Logger.Log("[粒子分组] Open 进入 target="
                + (target == null ? "null" : target.eventType.ToString())
                + " instance=" + (instance != null));
            if (target == null)
            {
                return;
            }
            if (instance != null)
            {
                // 粒子面板在 Build 时绑定了具体装饰物，换事件只能重建窗口。
                UnityEngine.Object.Destroy(instance.gameObject);
                instance = null;
            }

            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                return;
            }
            RectTransform layer = overlay.PopupLayer;
            if (layer == null)
            {
                return;
            }

            GameObject go = new GameObject("UiParticleWindow", typeof(RectTransform));
            go.transform.SetParent(layer, false);
            UiParticleWindow window = go.AddComponent<UiParticleWindow>();
            window.Build(target, layer);
            instance = window;
        }

        private void Build(LevelEvent target, RectTransform layer)
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                return;
            }

            RectTransform root = (RectTransform)transform;
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(WindowWidth, WindowHeight);
            root.anchoredPosition = new Vector2(380f, 40f);
            window = root;

            Image background = gameObject.AddComponent<Image>();
            background.color = new Color(0.043f, 0.043f, 0.043f, 1f);
            background.raycastTarget = true;

            RectTransform title = CreateRect("Title", root, 0f, 0f, WindowWidth, TitleHeight);
            Image titleHit = title.gameObject.AddComponent<Image>();
            titleHit.color = new Color(1f, 1f, 1f, 0.04f);
            titleHit.raycastTarget = true;
            TextMeshProUGUI titleText = CreateText("TitleText", title, 12f, 0f,
                WindowWidth - 48f, TitleHeight, "粒子编辑器", 13f,
                overlay.VersionLinkColor, TextAlignmentOptions.MidlineLeft);
            titleText.raycastTarget = false;

            CreateTextButton(title, WindowWidth - 34f, 4f, 28f, 26f, "×", Close);

            WindowDrag drag = title.gameObject.AddComponent<WindowDrag>();
            drag.Configure(root);

            // 左栏：参数行
            RectTransform body = CreateRect("Body", root, 0f, TitleHeight,
                LeftWidth, WindowHeight - TitleHeight);
            // **左栏是粒子专用面板**，数据源是 previewDec 的 ParticleSystem，
            // 不是 AddParticle 的事件属性（那个是"粒子摆在哪"）。
            // **面板绑的是"预览副本"本身。**
            //
            // 绑原版 `previewDec` 会把它的位置挪到隔离点，而主预览可能也用
            // 那个对象 —— 一拖时间轴（触发重新 Setup）主预览的粒子就没了。
            // 现在：host 先建出自己的副本，面板改副本；主预览由 MirrorToLive
            // 写到关卡装饰物上，互不干扰。
            float previewWidth = WindowWidth - LeftWidth - PreviewInset;
            float previewHeight = WindowHeight - TitleHeight - PreviewInset * 2f;
            previewHost.Ensure(target, Mathf.RoundToInt(previewWidth),
                Mathf.RoundToInt(previewHeight));
            scrParticleDecoration decoration = previewHost.Decoration;
            particlePane = UiParticlePane.Build(body, overlay, decoration, target, 6f, 6f,
                LeftWidth - 12f, WindowHeight - TitleHeight - 12f);
            previewTarget = target;

            // 右栏：预览载体。现在是一块占位深槽，下一步把 RenderTexture 贴进来
            // （RawImage 已经就位，换掉 texture 就行）。
            float previewX = LeftWidth;
            float previewW = WindowWidth - LeftWidth - PreviewInset;
            float previewH = WindowHeight - TitleHeight - PreviewInset * 2f;
            RectTransform preview = CreateRect("Preview", root, previewX,
                TitleHeight + PreviewInset, previewW, previewH);
            GameObject rawObject = new GameObject("PreviewImage", typeof(RectTransform));
            rawObject.transform.SetParent(preview, false);
            RectTransform rawRect = rawObject.GetComponent<RectTransform>();
            rawRect.anchorMin = Vector2.zero;
            rawRect.anchorMax = Vector2.one;
            rawRect.offsetMin = Vector2.zero;
            rawRect.offsetMax = Vector2.zero;
            previewImage = rawObject.AddComponent<RawImage>();
            previewImage.raycastTarget = false;

            // 预览走共用宿主（原版 previewDec 那一套），不再自己拼相机/副本。
            previewHost.Ensure(target, Mathf.RoundToInt(previewW), Mathf.RoundToInt(previewH));
            previewImage.texture = previewHost.Texture;

            // 有画面就把「预览区」那行占位字收掉；没有才留着提示。
            bool hasPreview = previewHost.Texture != null;
            if (previewHint != null)
            {
                previewHint.gameObject.SetActive(!hasPreview);
                previewHint.text = hasPreview ? string.Empty : "还没有粒子对象";
            }

            previewHint = CreateText("Hint", preview, 0f, 0f,
                previewW, previewH, "预览区", 12f,
                new Color(0.55f, 0.55f, 0.55f, 1f), TextAlignmentOptions.Center);
            previewHint.raycastTarget = false;
            // 已经有 RT 就不要再显示占位字（它是在 RT 之后才建的）。
            previewHint.gameObject.SetActive(previewHost.Texture == null);
        }

        private LevelEvent previewTarget;

        /// <summary>
        /// 参数改过就重建预览。判据是参数栏的 `CommitVersion` ——
        /// 它只在真正写入成功后自增，所以拖滑块、点下拉都能跟上，
        /// 而把光标移进移出不会白重建（重建一次要重跑粒子 Setup，很贵）。
        /// </summary>
        private void Update()
        {
            if (particlePane == null || previewTarget == null)
            {
                return;
            }
            if (particlePane.Version == lastParticleVersion)
            {
                return;
            }
            lastParticleVersion = particlePane.Version;
            previewHost.Rebuild(previewTarget);
            if (previewImage != null)
            {
                previewImage.texture = previewHost.Texture;
            }
            // 预览改成「原地重建」后不再换对象，面板的绑定一直有效，
            // 这里不需要（也不能）换绑 —— 换绑会把正在调的色打断。
        }

        /// <summary>
        /// 开着的话关掉，返回「关过」——Esc 那条链的第一顺位。
        ///
        /// 浮窗不在工作台的窗口栈里，Esc 链遍历不到它；不给它一个入口的话，
        /// 开着粒子面板按 Esc 会把整个工作台一起关掉。
        /// </summary>
        internal static bool CloseIfOpen()
        {
            if (instance == null)
            {
                return false;
            }
            instance.Close();
            return true;
        }

        private void Close()
        {
            instance = null;
            // 关窗前把面板改过的值写回事件（落盘）。
            if (previewTarget != null && previewHost.Decoration != null)
            {
                ParticlePersistence.Apply(Main.Overlay, previewTarget,
                    previewHost.Decoration.particleSystem);
            }
            // 相机 / RenderTexture / 副本都在宿主里，连它一起收。
            previewHost.Release();
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            // 换事件时是直接 Destroy 掉旧窗口（不走 Close），这里也得收一遍 ——
            // `Release` 自带空判，和 Close 那条重复调用没关系。
            previewHost.Release();
            if (instance == this)
            {
                instance = null;
            }
        }

        private Button CreateTextButton(RectTransform parent, float x, float y,
            float width, float height, string label, System.Action action)
        {
            RectTransform rect = CreateRect("Button", parent, x, y, width, height);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            TextMeshProUGUI text = CreateText("Label", rect, 0f, 0f, width, height,
                label, 16f, new Color(0.72f, 0.72f, 0.72f, 1f),
                TextAlignmentOptions.Center);
            text.raycastTarget = false;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { action(); });
            return button;
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent,
            float x, float y, float width, float height, string value, float size,
            Color color, TextAlignmentOptions alignment)
        {
            RectTransform rect = CreateRect(name, parent, x, y, width, height);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay != null && overlay.UiFont != null)
            {
                text.font = overlay.UiFont;
            }
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        internal static RectTransform CreateRect(string name, Transform parent,
            float x, float y, float width, float height)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        /// <summary>标题条拖动整窗（浮层画布缩放恒为 1）。</summary>
        private sealed class WindowDrag : MonoBehaviour, IDragHandler
        {
            private RectTransform target;

            internal void Configure(RectTransform rect)
            {
                target = rect;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (target != null)
                {
                    target.anchoredPosition += eventData.delta;
                }
            }
        }
    }
}
