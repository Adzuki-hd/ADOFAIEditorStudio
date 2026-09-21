using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ADOFAI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    public sealed class EditorStudioOverlay : MonoBehaviour
    {
        // 圆角半径。外层大面板取大的那个值，这样工作台读起来才像一组卡片；
        // 内层凹槽和卡片取小的那个值，这样它们读起来像是凹在面板里，
        // 而不是自己又成了一块面板。
        // 面板标题条。每个面板都用同一套度量，四个标题才能在工作台上对齐；
        // 以前它们分别是 14pt 和 17pt，缩进还各不相同——这正是
        // 面板不像一套东西的主要原因。
        private const float PanelHeadingInset = 12f;
        private const float PanelHeadingHeight = 26f;
        private const float PanelHeadingRule = 27f;

        private const float CornerLarge = 9f;
        private const float CornerSmall = 6f;

        private const float DesignWidth = 1920f;
        private const float DesignHeight = 1080f;
        private const float HeaderHeight = 54f;
        private const float OuterMargin = 12f;
        private const float PanelGap = 12f;
        // 舞台在 1080p 下受高度限制，几乎没有富余可以分：真正决定舞台靠左到哪、
        // 参数栏能分到多少空间的是左面板的最小宽度。
        // 从 440 调低，让舞台左移，右列因此多出明确的约 68px，
        // 够再放一列属性。
        private const float MinimumLeftPanelWidth = 360f;
        private const float MinimumRightPanelWidth = 290f;

        // 舞台之外剩余的宽度如何在左右两个侧面板之间分配。媒体列
        // 只需要显示一个缩略图和一个文件名，而参数栏是以后新增属性列的地方，
        // 所以富余主要给右边。
        private const float LeftPanelExtraShare = 0.15f;
        private const float DefaultTimelineHeight = 292f;
        private const float MinimumTimelineHeight = 220f;
        private const float MaximumTimelineHeight = 520f;
        private const float TimelinePanelWidth = DesignWidth - 24f;
        private const float PreviewPanelY = HeaderHeight + 12f;
        private const float PreviewStageInsetY = 58f;
        private const float BrowserRowHeight = 32f;
        private const float BrowserDataRefreshInterval = 0.5f;
        private const float BrowserSelectionRefreshInterval = 0.12f;
        private const float ProjectTreeWidthRatio = 0.38f;

        // 项目树只显示文件夹名，所以面板变窄时可以让出宽度给
        // 媒体列。这是按典型谱面文件夹里的名字量的，
        // 那些名字都很短。
        private const float MinimumProjectTreeWidth = 118f;
        private const float MaximumProjectTreeWidth = 190f;
        private const float ProjectTreeRowHeight = 30f;
        private const float MediaCardHeight = 126f;
        private const float MediaCardGap = 8f;
        // 缓存的核心就是每个媒体文件一条记录：列表是要滚动的，被淘汰的条目
        // 下一趟就得从几 MB 的源图重新解码。Digital Warrior 文件夹里有 178 张图，
        // 其中好几张 5000+ px、4-5MB，而 64 条的上限意味着滚动时会不停地
        // 重新解码。缩略图最大也就 256px，全部留着
        // 只占几 MB 纹理内存。
        private const int MediaThumbnailCacheCapacity = 512;
        private const float MediaThumbnailLoadDelay = 0.12f;
        private const string UiGlyphProbe = "编辑器工作台工程媒体文件目录未保存浏览全部装饰物轨道预览参数时间轴锁定缩放镜头视效音频游戏其他第格图片文本粒子对象";

        private enum BrowserFilter
        {
            All,
            Decorations,
            Tracks
        }

        private enum BrowserItemKind
        {
            Track,
            Decoration
        }

        private struct PreviewLayout
        {
            public float LeftPanelWidth;
            public float RightPanelWidth;
            public float StageX;
            public float StageY;
            public float StageWidth;
            public float StageHeight;
            public float InspectorX;
        }

        private sealed class BrowserItem
        {
            public BrowserItemKind Kind;
            public int FloorId;
            public int SourceOrder;
            public string Label;
            public scrFloor Floor;
            public scrDecoration Decoration;
        }

        private sealed class BrowserRow
        {
            public GameObject GameObject;
            public RectTransform Rect;
            public Button Button;
            public TextMeshProUGUI Label;
            public int BoundIndex = -1;
        }

        private sealed class ProjectTreeRow
        {
            public GameObject GameObject;
            public RectTransform Rect;
            public Button Button;
            public TextMeshProUGUI Label;
            public string Directory;
            public bool Selectable;
        }

        private sealed class MediaCard
        {
            public GameObject GameObject;
            public RectTransform Rect;
            public RawImage Image;
            public TextMeshProUGUI Label;
            public Button Button;
            public Texture2D Texture;
            public int BoundIndex = -1;
            public string BoundPath;
            public string TexturePath;
            public float CardWidth;
            public float ThumbnailHeight;
            public float ThumbnailWidth;
        }

        private sealed class MediaThumbnailCacheEntry
        {
            public string Key;
            public Texture2D Texture;
            public bool Failed;
            public int LastUse;
        }

        private sealed class MediaThumbnailRequest
        {
            public string Key;
            public string AbsolutePath;
            public string Extension;
        }

        // 从原型稿移植过来，那就是选定的观感：深色表面、亮色文字、
        // 一种饱和的强调色。对比才是重点——早先一版把面板调亮、往背景色上靠，
        // 结果整个工作台变得扁平，因为这里的纵深
        // 来自面板保持深色去衬亮色文字，而不是
        // 面板变亮去衬深色桌面。
        //
        // 面板带一点 alpha。画布后面没有任何不透明的东西——Backdrop 是
        // Color.clear——所以半透明的面板会透出游戏画面，这就是零成本的
        // 磨砂玻璃观感。背后没有模糊：真正的毛玻璃效果需要每帧
        // 抓屏再做一遍模糊，而预览本身已经渲染了游戏相机，
        // 所以这个廉价版本反而更值得要。alpha 保持较高，
        // 好让移植过来的配色和它们的对比度在明亮的谱面上依然成立。
        // 以 #151515 为基准。它是 (0.082, 0.082, 0.082)——一种不带色偏的
        // 中性近黑——也是整个色阶围绕它搭建的那个面板值。这里的一切
        // 都是不同明度的中性灰；工作台上唯一的颜色就是强调色，
        // 所以带色偏的表面读起来是错误，而不是风格。
        //
        // ---------------------------------------------------------------- 调色板
        //
        // 这里是工作台唯一的颜色来源。时间轴和参数栏都从这里取，
        // 不再各自维护一份镜像——以前三份灰阶各自漂移，
        // 拼在一起就露出「贴了另一款程序的面板」的破绽。
        //
        // 颜色刻意少：三个层级 + 一个强调色 + 三档文字。
        // 层级只在明度上拉开，色相全部锁在同一个冷调上
        // （约 218°，饱和度 12%），所以它们读起来是同一个表面的深浅，
        // 而不是三种不同的灰。
        //
        // 配色是「冷石墨 + 青瓷」：底子是不带情绪的深冷灰，
        // 饱和色只留一个青瓷绿，且只用在三处——选中态、
        // 主按钮、以及需要指路的高亮。这是整个界面安静的
        // 唯一原因：不是元素少，是颜色少。
        //
        // 层级只有两层半，不是八层。以前有 0.038/0.055/0.065/0.082/
        // 0.118/0.135/0.170/0.240 八个近似灰，彼此差得太近，
        // 眼睛分不出来，读起来是「糊」而不是「层次」。
        // 现在只有三个明度档，每一档都拉得开：
        //
        //   Background  0.049  桌面（面板之外的一切）
        //   Surface     0.093  浮在上面的面板
        //   Raised      0.132  面板里的卡片、输入框、按钮
        //
        // 面板内部的凹槽（PanelAlt、FieldWell）直接落回 Background
        // 那一档，于是「板子凸起、里面的内容凹下去」这件事
        // 用两个值就说清了，不需要第四档。
        private static readonly Color Background = new Color(0.049f, 0.053f, 0.062f, 0.97f);
        private static readonly Color Header = new Color(0.093f, 0.099f, 0.113f, 0.96f);
        private static readonly Color Panel = new Color(0.093f, 0.099f, 0.113f, 0.96f);
        private static readonly Color PanelAlt = new Color(0.049f, 0.053f, 0.062f, 0.94f);
        private static readonly Color ButtonNormal = new Color(0.132f, 0.140f, 0.158f, 0.96f);
        private static readonly Color ButtonHover = new Color(0.176f, 0.186f, 0.208f, 0.97f);
        // 强调色是这里唯一的饱和色，选中态都从它派生，
        // 而不是从灰色阶派生，这样选中的行一眼就能认出来。
        private static readonly Color Accent = new Color(0.286f, 0.827f, 0.796f, 1f);
        private static readonly Color Selected = new Color(0.074f, 0.223f, 0.224f, 0.95f);
        private static readonly Color SelectedHover = new Color(0.100f, 0.298f, 0.300f, 0.97f);
        // 面板描边：一条一像素的冷灰发丝线。
        //
        // 以前是 1.5px 的纯白，外面还套两圈白色辉光。那在实时游戏画面上
        // 太响了——一圈白边会把视线从它框住的内容上拽走，
        // 也正是这套界面看起来「廉价」的主要原因。
        //
        // 面板浮在游戏画面上时靠什么和背景分开，答案不是把边画亮，
        // 而是**在它背后投一层阴影**（见 AddPanelShadow）。
        // 发丝线只是兜底：背景本身就是深色时，阴影看不出来，
        // 那时这条线负责给出边界。
        private static readonly Color Border = new Color(1f, 1f, 1f, 0.085f);
        private static readonly Color TextPrimary = new Color(0.925f, 0.937f, 0.957f, 1f);
        private static readonly Color TextSecondary = new Color(0.757f, 0.780f, 0.827f, 1f);
        private static readonly Color TextMuted = new Color(0.545f, 0.573f, 0.639f, 1f);
        // 凸起内容：卡片和色块。比面板高一档，
        // 这样卡片读起来像放在表面上的一件物体，而不是表面上的一块区域。
        private static readonly Color Raised = new Color(0.132f, 0.140f, 0.158f, 1f);
        // 比 Raised 再高一档，给凸起卡片内部的内容用（缩略图、色块）。
        // 这是全套里最亮的一档填充色，仍然远低于纯白。
        private static readonly Color ThumbWell = new Color(0.180f, 0.190f, 0.212f, 1f);
        // 面板标题下方的接缝。比描边弱，因为它分割的是同一个表面，
        // 而不是隔开两个物体——但要亮到能在面板上看见。
        private static readonly Color PanelRule = new Color(1f, 1f, 1f, 0.075f);
        // 输入凹槽。它是"凹"的，所以要比面板暗，
        // 才能读成一个输入框，而不是面板上的一个凸起。
        private static readonly Color FieldWell = new Color(0.038f, 0.041f, 0.049f, 0.95f);

        // ------------------------------------------------------------ 描边与阴影
        //
        // 面板的立体感由两样东西合力给出，它们是分开的两层：
        //
        //   描边（Border）  一像素的发丝线，贴住面板边缘，给出确切边界
        //   阴影（Shadow）  垫在面板背后、往右下推开的一个更大的圆角矩形
        //
        // 阴影用的是**纯黑低透明度**，而不是"面板的深色版本"：
        // 面板会浮在实时游戏画面之上，那块画面什么颜色都有可能，
        // 半透明的深蓝叠上去只会变成一块脏色；黑色叠在任何颜色上
        // 都是把那个颜色压暗，这才读得像影子。
        private const float PanelBorderWidth = 1f;
        // 阴影偏移与扩散。偏移往下（-y）多一点、往右少一点，
        // 因为光源在习惯上被理解在左上方；这样四边的阴影是
        // 不对称的，物体才"立"得起来。
        private const float PanelShadowSpread = 5f;
        private static readonly Vector2 PanelShadowOffset = new Vector2(2f, -4f);
        private static readonly Color PanelShadow = new Color(0f, 0f, 0f, 0.42f);

        // Palette 是平级类，读不到 private static，所以这里开一组
        // internal 只读入口。用属性而不是把字段改成 internal：
        // 字段仍然只有本文件能写，其它类只能读。
        internal static Color PanelBackgroundColor { get { return Background; } }
        internal static Color PanelColor { get { return Panel; } }
        internal static Color PanelAltColor { get { return PanelAlt; } }
        internal static Color ButtonNormalColor { get { return ButtonNormal; } }
        internal static Color ButtonHoverColor { get { return ButtonHover; } }
        internal static Color AccentColor { get { return Accent; } }
        internal static Color SelectedColor { get { return Selected; } }
        internal static Color SelectedHoverColor { get { return SelectedHover; } }
        internal static Color RaisedColor { get { return Raised; } }
        internal static Color ThumbWellColor { get { return ThumbWell; } }
        internal static Color FieldWellColor { get { return FieldWell; } }
        internal static Color PanelRuleColor { get { return PanelRule; } }
        internal static Color TextPrimaryColor { get { return TextPrimary; } }
        internal static Color TextSecondaryColor { get { return TextSecondary; } }
        internal static Color TextMutedColor { get { return TextMuted; } }

        private readonly List<Button> browserFilterButtons = new List<Button>();
        private readonly List<TextMeshProUGUI> browserFilterLabels = new List<TextMeshProUGUI>();
        private readonly List<BrowserItem> allBrowserItems = new List<BrowserItem>();
        private readonly List<BrowserItem> filteredBrowserItems = new List<BrowserItem>();
        private readonly List<BrowserRow> browserRows = new List<BrowserRow>();
        private readonly List<ProjectTreeRow> projectTreeRows = new List<ProjectTreeRow>();
        private readonly List<MediaCard> mediaCards = new List<MediaCard>();
        private readonly Dictionary<string, MediaThumbnailCacheEntry> mediaThumbnailCache =
            new Dictionary<string, MediaThumbnailCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<MediaThumbnailRequest> mediaThumbnailQueue =
            new Queue<MediaThumbnailRequest>();
        private readonly HashSet<string> mediaThumbnailQueued =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Vector3[] previewViewportCorners = new Vector3[4];

        private Settings settings;

        // 参数栏读它取显示缩放。参数栏和工作台是两个 MonoBehaviour，
        // 对外暴露一个只读入口比让它自己去翻 Main.Settings 干净。
        internal Settings Settings
        {
            get { return settings; }
        }

        private GameObject uiRoot;
        private Canvas canvas;
        private TMP_FontAsset uiFont;
        private TMP_FontAsset generatedTmpFont;
        private GameObject ownedEventSystem;
        private bool built;
        private bool reopenAfterReload;
        private bool wasVisibleBeforeReload;
        private bool buildFailed;
        private bool visible;
        private int layoutScreenWidth;
        private int layoutScreenHeight;
        private float timelineHeight = DefaultTimelineHeight;
        private PreviewLayout previewLayout;
        private RectTransform designRoot;
        private RectTransform browserPanel;
        private RectTransform projectTreePanel;
        private RectTransform projectTreeViewport;
        private RectTransform projectTreeContent;
        private RectTransform mediaPanel;
        private RectTransform mediaViewport;
        private RectTransform mediaContent;
        private TextMeshProUGUI projectTitle;
        private TextMeshProUGUI mediaTitle;
        private ScrollRect projectTreeScroll;
        private ScrollRect mediaScroll;
        private RectTransform previewPanel;
        private RectTransform previewToolbar;
        // 预览工具栏上的车道筛选按钮，谱面加载后要靠它刷新图标。
        private readonly List<RectTransform> laneFilterButtonRects = new List<RectTransform>();
        private RectTransform previewHeading;
        private RectTransform previewViewport;
        private RawImage previewHandoffImage;
        private Texture2D previewHandoffTexture;
        private Coroutine previewHandoffCoroutine;
        private Coroutine previewHandoffReleaseCoroutine;
        private int previewHandoffGeneration;
        private RectTransform inspectorPanel;
        private RectTransform inspectorHeading;
        private EditorPropertyPanel propertyPanel;
        private RectTransform timelinePanel;
        private RectTransform matteTop;
        private RectTransform matteBottom;
        private RectTransform matteLeft;
        private RectTransform matteRight;
        private EditorTimelineView timelineView;
        private Camera previewCamera;
        private Rect previewCameraOriginalRect;
        private bool previewCameraModified;
        private bool nonPreviewPointerCapture;
        private static MethodInfo loadImageMethod;
        private static bool imageLoaderSearched;

        private BrowserFilter browserFilter;
        private ScrollRect browserScroll;
        private RectTransform browserContent;
        private int browserFingerprint;
        private bool browserFingerprintValid;
        private bool browserDataDirty = true;
        private bool browserSelectionDirty = true;
        private float nextBrowserDataRefresh;
        private float nextBrowserSelectionRefresh;
        private EditorProjectSession projectSession;
        private string selectedProjectDirectory = ".";
        private string selectedMediaPath;
        private readonly List<EditorProjectMediaItem> visibleProjectMedia = new List<EditorProjectMediaItem>();
        private bool projectDataDirty = true;
        private float nextProjectRefresh;
        private bool mediaCardsDirty = true;
        private int mediaCardsBoundFirstRow = -1;
        private int mediaCardsBoundVisibleCount = -1;
        private int mediaCardsBoundPoolCount = -1;
        private float mediaCardsBoundWidth = -1f;
        private string mediaCardsBoundSelectionPath;
        private float mediaThumbnailLoadNotBefore;
        private int mediaThumbnailUseCounter;

        internal bool IsVisible
        {
            get { return visible; }
        }

        /// <summary>
        /// 时间轴当前选中项，按需解析。参数面板不能持有时间轴引用：
        /// BuildInspector 在 BuildTimeline 之前运行，所以面板创建时该视图
        /// 还不存在，而且它每次
        /// 分辨率重建都会被换掉。
        /// </summary>
        internal LevelEvent TimelineSelection
        {
            get
            {
                // 游戏编辑器自己的选中优先，所以在预览里点击一个装饰物
                // 就能调出它的参数，不必经过时间轴。原生编辑器本来就是
                // 点击经过的那条路（预览会转发给 scnEditor.SelectDecoration），
                // 它也是用户点击空白处时
                // 被清掉的那个选中。
                scnEditor editor = scnEditor.instance;
                if (editor != null
                    && editor.selectedDecorations != null && editor.selectedDecorations.Count > 0
                    && editor.selectedDecorations[0] != null)
                {
                    return editor.selectedDecorations[0];
                }

                // 否则退回时间轴自己的事件块选中，面板跟随时间轴点击
                // 靠的就是这个。
                return timelineView == null ? null : timelineView.SelectedEvent;
            }
        }

        internal bool IsPreviewZoomLocked
        {
            get { return settings == null || settings.LockPreviewZoom; }
        }

        internal bool IsPointerInsidePreview
        {
            get { return IsScreenPointInsidePreview(Input.mousePosition); }
        }

        internal bool IsPointerOverNonPreviewUi
        {
            get { return visible && !IsPointerInsidePreview; }
        }

        internal bool IsCapturingNonPreviewPointer
        {
            get { return visible && nonPreviewPointerCapture; }
        }

        internal bool IsPointerInsideTimeline(Vector2 screenPosition)
        {
            return visible && timelineView != null && timelineView.ContainsScreenPoint(screenPosition);
        }

        internal float CurrentTimelinePanelHeight
        {
            get { return timelineHeight - 12f; }
        }

        internal void SetTimelinePanelHeight(float panelHeight, bool persist)
        {
            float requested = Mathf.Clamp(
                panelHeight + 12f,
                MinimumTimelineHeight,
                MaximumTimelineHeight
            );
            if (!Mathf.Approximately(requested, timelineHeight))
            {
                timelineHeight = requested;
                if (settings != null)
                {
                    settings.TimelineHeight = timelineHeight;
                }
                ApplyWorkspaceLayout();
            }

            if (persist)
            {
                Main.SaveSettings();
            }
        }

        private float PreviewPanelHeight
        {
            get { return DesignHeight - HeaderHeight - timelineHeight - 28f; }
        }

        private float PreviewStageAvailableHeight
        {
            get { return PreviewPanelHeight - 64f; }
        }

        public void Configure(Settings value)
        {
            settings = value;
            if (built)
            {
            }
        }

        internal bool ShouldBlockPreviewZoom(Vector2 screenPosition)
        {
            if (!visible)
            {
                return false;
            }

            return !IsScreenPointInsidePreview(screenPosition) || IsPreviewZoomLocked;
        }

        private bool IsScreenPointInsidePreview(Vector2 screenPosition)
        {
            return visible
                && previewViewport != null
                && RectTransformUtility.RectangleContainsScreenPoint(previewViewport, screenPosition, null);
        }

        private void UpdateNonPreviewPointerCapture()
        {
            bool pointerHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (!pointerHeld)
            {
                nonPreviewPointerCapture = false;
                return;
            }

            bool pointerPressed = Input.GetMouseButtonDown(0)
                || Input.GetMouseButtonDown(1)
                || Input.GetMouseButtonDown(2);
            if (pointerPressed)
            {
                nonPreviewPointerCapture = !IsScreenPointInsidePreview(Input.mousePosition);
            }
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            // 场景重载会摧毁这个工作台正在驱动的 runtime，并让每一份
            // 缓存引用全都指向旧场景。这不是假设：别的 mod 也可以在
            // 自己的 OnEnable/OnDisable 里重置编辑器场景，
            // 走的是 ADOBase.RestartScene 那条路。
            //
            // 危险的是这类重置**不由我们掌控时机**：加载是异步的，
            // 落点取决于对方的初始化何时完成，在机器繁忙时可能是作者
            // 开始播放几十秒之后。看得见的结果就是播放控制跑到一半死掉、
            // 编辑器又冒出来，而且每次尝试出现的位置都不一样。
            //
            // 宿主本身能挺过重载，所以这里是唯一能察觉并停下来的地方。
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// 任何场景加载之后把工作台停掉。另一条路是试图
        /// 在已被替换的场景上恢复，那会让每个标志位都指向
        /// 已销毁的对象。
        /// </summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            wasVisibleBeforeReload = visible;
            EditorPlaybackBridge.HandleSceneReloaded(scene == null ? "?" : scene.name);
            // 每一份 UI 引用都属于刚被替换掉的那个场景——Unity 会把它们
            // 报成已销毁，而构建标志位仍然说 UI 还在。两个都丢掉
            // 才能让 Update 对着新场景重建；留着它们会让工作台
            // 继续绘制到一条已销毁的层级里。这里不用
            // RebuildRuntimeUiForResolution，因为它会销毁 uiRoot，而重载已经把它销毁了。
            ClearRuntimeUiReferences();
            built = false;
            // 别处从不清理，而重载后最初几帧里尝试重建
            // 可能在新场景还没起来时抛异常。没有这一句，
            // 工作台会在本次会话剩余时间里一直是死的。
            buildFailed = false;
            // 如果工作台原本是打开的，就重新打开：这次运行要在被中断的
            // 播放头处恢复，而那只通过工作台的视口才看得见。
            reopenAfterReload = wasVisibleBeforeReload;
            wasVisibleBeforeReload = false;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            HidePreviewHandoffImmediate();
            ReleaseMediaTextures();
            RestorePreviewViewport();
            EditorPlaybackBridge.TryStopPlayback();

            if (ownedEventSystem != null)
            {
                Destroy(ownedEventSystem);
            }

            if (generatedTmpFont != null)
            {
                Destroy(generatedTmpFont);
            }
        }

        private void LateUpdate()
        {
            if (visible && built)
            {
                ApplyPreviewViewport();
                EditorPlaybackBridge.EnforcePreviewZoomLock();
            }
            else
            {
                RestorePreviewViewport();
            }
        }

        private void Update()
        {
            if (settings == null)
            {
                return;
            }

            if (!EditorBridge.IsEditorActive() && !EditorPlaybackBridge.IsRestartingPreview)
            {
                if (visible)
                {
                    SetVisible(false);
                }
                return;
            }

            KeyCode shortcut = (KeyCode)settings.Shortcut;
            if (shortcut != KeyCode.None && Input.GetKeyDown(shortcut) && (!built || !IsTextInputFocused()))
            {
                if (!built && !TryBuildRuntimeUi())
                {
                    return;
                }
                SetVisible(!visible);
            }

            // 场景重载销毁了 UI；重建它并重新打开工作台，
            // 好让恢复后的运行可见。
            if (reopenAfterReload)
            {
                if (TryBuildRuntimeUi())
                {
                    reopenAfterReload = false;
                    SetVisible(true);
                }
            }

            // 只要重载恢复还挂起，就每帧 tick 一次，与工作台是否
            // 正在重开无关：在工作台关闭时落地的重载
            // 同样摧毁了播放控制，让恢复处于已挂载却没有任何东西
            // 驱赶它的状态只会把它搁浅。这也必须跑在 `built` 门之前，
            // 因为重载刚过什么都还没建。它故意不 return：
            // 它可能启动的播放控制需要同一帧再往下的 TickPreview 调用，
            // 之后每一帧也都需要——见下面的说明。
            EditorPlaybackBridge.TickSceneReloadResume();

            if (!built)
            {
                return;
            }

            if (Screen.width > 0 && Screen.height > 0
                && (Screen.width != layoutScreenWidth || Screen.height != layoutScreenHeight))
            {
                RebuildRuntimeUiForResolution();
                if (!built)
                {
                    return;
                }
            }

            if (visible && Input.GetKeyDown(KeyCode.Escape))
            {
                SetVisible(false);
            }


            if (!visible)
            {
                nonPreviewPointerCapture = false;
                if (EditorPlaybackBridge.IsRestartingPreview)
                {
                    EditorPlaybackBridge.TickPreview(null);
                }
                return;
            }

            UpdateNonPreviewPointerCapture();
            long frameStart = Main.Settings != null && Main.Settings.VerboseDiagnostics
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            EditorPlaybackBridge.TickPreview(previewViewport);
            long afterPreview = System.Diagnostics.Stopwatch.GetTimestamp();
            RefreshPlaybackState();
            long afterState = System.Diagnostics.Stopwatch.GetTimestamp();
            TickProjectWorkspace();
            long afterWorkspace = System.Diagnostics.Stopwatch.GetTimestamp();

            if (frameStart != 0L && Time.frameCount - lastOverlayCostFrame >= 60)
            {
                lastOverlayCostFrame = Time.frameCount;
                double frequency = System.Diagnostics.Stopwatch.Frequency / 1000.0;
                Main.Logger.Log("[OverlayCost] preview="
                    + ((afterPreview - frameStart) / frequency).ToString("0.00")
                    + "ms state=" + ((afterState - afterPreview) / frequency).ToString("0.00")
                    + "ms workspace=" + ((afterWorkspace - afterState) / frequency).ToString("0.00")
                    + "ms total=" + ((afterWorkspace - frameStart) / frequency).ToString("0.00")
                    + "ms fps=" + (1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime)).ToString("0") + ".");
            }
        }

        private int lastOverlayCostFrame = -10000;

        private void BuildRuntimeUi()
        {
            if (uiFont == null)
            {
                uiFont = ResolveFont();
            }

            timelineHeight = Mathf.Clamp(
                settings == null ? DefaultTimelineHeight : settings.TimelineHeight,
                MinimumTimelineHeight,
                MaximumTimelineHeight
            );
            if (settings != null)
            {
                settings.TimelineHeight = timelineHeight;
            }
            previewLayout = CalculatePreviewLayout();
            layoutScreenWidth = Screen.width;
            layoutScreenHeight = Screen.height;

            uiRoot = new GameObject(
                "ADOFAIEditorStudio.Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster)
            );
            uiRoot.transform.SetParent(transform, false);

            RectTransform rootRect = uiRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            canvas = uiRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 30000;
            canvas.pixelPerfect = false;

            CanvasScaler scaler = uiRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(DesignWidth, DesignHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;

            Image backdrop = CreateStretchImage("Backdrop", rootRect, Color.clear);
            backdrop.raycastTarget = false;

            GameObject designObject = new GameObject("DesignRoot", typeof(RectTransform));
            designObject.transform.SetParent(rootRect, false);
            designRoot = designObject.GetComponent<RectTransform>();
            designRoot.anchorMin = new Vector2(0.5f, 0.5f);
            designRoot.anchorMax = new Vector2(0.5f, 0.5f);
            designRoot.pivot = new Vector2(0.5f, 0.5f);
            designRoot.anchoredPosition = Vector2.zero;
            designRoot.sizeDelta = new Vector2(DesignWidth, DesignHeight);

            BuildViewportMatte(designRoot);
            BuildHeader(designRoot);
            BuildBrowser(designRoot);
            BuildPreview(designRoot);
            BuildInspector(designRoot);
            BuildTimeline(designRoot);

            built = true;
            uiRoot.SetActive(false);
            projectDataDirty = true;
            RefreshProjectWorkspace(true);
        }

        private bool TryBuildRuntimeUi()
        {
            if (built)
            {
                return true;
            }
            if (buildFailed)
            {
                return false;
            }

            try
            {
                BuildRuntimeUi();
                return true;
            }
            catch (Exception exception)
            {
                buildFailed = true;
                Main.Logger.Log("Failed to build the runtime uGUI: " + exception);
                if (uiRoot != null)
                {
                    uiRoot.SetActive(false);
                    Destroy(uiRoot);
                    uiRoot = null;
                }
                return false;
            }
        }

        private void BuildHeader(RectTransform parent)
        {
            Image header = CreateImage("Header", parent, 0f, 0f, DesignWidth, HeaderHeight, Header);
            header.raycastTarget = true;
            CreateText("Title", header.rectTransform, 24f, 9f, 360f, 36f, "ADOFAI  EDITOR STUDIO", 20f, TextPrimary, TextAlignmentOptions.MidlineLeft, true);
            CreateText("Subtitle", header.rectTransform, 390f, 13f, 220f, 28f, "剪辑器工作台", 13f, TextMuted, TextAlignmentOptions.MidlineLeft, false);

            // 实时状态读数。夹在副标题和快捷键提示之间，这样它有
            // 喘息的空间、也不撞上任何一边，宽度也够
            // longest adjustment line ("深度 +120" style plus a label).
            statusLabel = CreateText(
                "Status", header.rectTransform, 620f, 13f, 900f, 28f,
                string.Empty, 13f, Accent, TextAlignmentOptions.MidlineLeft, false);
            statusLabel.color = Accent;

            CreateText("Shortcut", header.rectTransform, DesignWidth - 230f, 13f, 206f, 28f, "F8  工作台  ·  Esc  关闭", 12f, TextMuted, TextAlignmentOptions.MidlineRight, false);
        }

        private void BuildBrowser(RectTransform parent)
        {
            float height = PreviewPanelHeight;
            Image panel = CreateRoundedPanel("ProjectWorkspace", parent, OuterMargin, PreviewPanelY, previewLayout.LeftPanelWidth, height, Panel, CornerLarge);
            panel.raycastTarget = true;
            AddPanelBorder(panel, CornerLarge);
            RectTransform root = panel.rectTransform;
            browserPanel = root;
            float panelWidth = previewLayout.LeftPanelWidth;
            float innerWidth = panelWidth - 24f;
            float treeWidth = Mathf.Clamp(innerWidth * ProjectTreeWidthRatio, MinimumProjectTreeWidth, MaximumProjectTreeWidth);
            float mediaWidth = innerWidth - treeWidth - 8f;
            float listY = 46f;
            float listHeight = height - listY - 12f;
            float mediaListY = listY + MediaSearchRowHeight;
            float mediaListHeight = height - mediaListY - 12f;

            Image treeImage = CreateRoundedPanel("ProjectTree", root, 12f, 10f, treeWidth, height - 20f, Panel, CornerSmall);
            Image mediaImage = CreateRoundedPanel("Media", root, 12f + treeWidth + 8f, 10f, mediaWidth, height - 20f, Panel, CornerSmall);
            AddPanelBorder(treeImage, CornerSmall);
            AddPanelBorder(mediaImage, CornerSmall);
            projectTreePanel = treeImage.rectTransform;
            mediaPanel = mediaImage.rectTransform;
            projectTitle = CreateText("ProjectTitle", projectTreePanel, PanelHeadingInset, 0f,
                treeWidth - PanelHeadingInset * 2f, PanelHeadingHeight, "工程", 13f,
                TextPrimary, TextAlignmentOptions.MidlineLeft, true);
            mediaTitle = CreateText("MediaTitle", mediaPanel, PanelHeadingInset, 0f,
                mediaWidth - PanelHeadingInset * 2f, PanelHeadingHeight, "媒体", 13f,
                TextPrimary, TextAlignmentOptions.MidlineLeft, true);
            // 这条细线是标题变成页眉的关键：没有它，标题会浮在
            // 下方列表所在的同一个表面上，面板读起来就是一整块没有分割的东西。
            //
            // 它横跨面板的全宽而不是内缩后的宽度。一条不到边的分隔线
            // 读起来是标题的下划线；一条两头都到边的
            // 读起来是两个区域之间的分界，这才是重点。
            //
            // 它也在下面那些滚动视图之后创建，因为后加的同级节点
            // 画在上面：先做的话，列表自己的背景会盖住它，线就永远不出现。
            Image treeRule = null;
            Image mediaRule = null;

            CreateMediaSearchControls(mediaPanel, mediaWidth, mediaListY - 10f);

            projectTreeScroll = CreateProjectScroll("ProjectTreeScroll", projectTreePanel, 8f, listY - 10f, treeWidth - 16f, listHeight, out projectTreeViewport, out projectTreeContent);
            mediaScroll = CreateProjectScroll("MediaScroll", mediaPanel, 8f, mediaListY - 10f, mediaWidth - 16f, mediaListHeight, out mediaViewport, out mediaContent);
            treeRule = CreateHeadingRule("TreeRule", projectTreePanel, PanelHeadingRule);
            mediaRule = CreateHeadingRule("MediaRule", mediaPanel, PanelHeadingRule);
            projectTreeScroll.onValueChanged.AddListener(delegate { UpdateProjectTreeRows(); });
            mediaScroll.onValueChanged.AddListener(OnMediaScrollChanged);
            EnsureProjectTreeRows(32);
            EnsureMediaCards(20);
            RefreshProjectWorkspace(true);
        }

        private ScrollRect CreateProjectScroll(
            string name,
            RectTransform parent,
            float x,
            float y,
            float width,
            float height,
            out RectTransform viewport,
            out RectTransform content)
        {
            RectTransform scrollRect = CreateTopLeftRect(name, parent, x, y, width, height);
            Image background = scrollRect.gameObject.AddComponent<Image>();
            background.color = new Color(PanelAlt.r, PanelAlt.g, PanelAlt.b, 0.88f);
            background.raycastTarget = true;
            ScrollRect scroll = scrollRect.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            scroll.scrollSensitivity = 28f;

            GameObject viewportObject = new GameObject(name + ".Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollRect, false);
            viewport = viewportObject.GetComponent<RectTransform>();
            SetStretch(viewport, 1f, 1f, 1f, 1f);
            Image viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(PanelAlt.r, PanelAlt.g, PanelAlt.b, 0.01f);
            viewportImage.raycastTarget = true;

            GameObject contentObject = new GameObject(name + ".Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);
            content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, height);

            scroll.viewport = viewport;
            scroll.content = content;
            return scroll;
        }

        private void EnsureProjectTreeRows(int count)
        {
            while (projectTreeRows.Count < count)
            {
                ProjectTreeRow row = null;
                TextMeshProUGUI label;
                Button button = CreateButton(
                    "ProjectTreeRow" + projectTreeRows.Count,
                    projectTreeContent,
                    0f,
                    0f,
                    100f,
                    ProjectTreeRowHeight - 2f,
                    string.Empty,
                    false,
                    delegate { ActivateProjectTreeRow(row); },
                    out label
                );
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.margin = new Vector4(8f, 0f, 6f, 0f);
                label.fontSize = 10.5f;
                row = new ProjectTreeRow
                {
                    GameObject = button.gameObject,
                    Rect = button.GetComponent<RectTransform>(),
                    Button = button,
                    Label = label
                };
                row.GameObject.SetActive(false);
                projectTreeRows.Add(row);
            }
        }

        private void EnsureMediaCards(int count)
        {
            bool created = false;
            while (mediaCards.Count < count)
            {
                MediaCard card = null;
                GameObject cardObject = new GameObject(
                    "MediaCard" + mediaCards.Count,
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(Button)
                );
                cardObject.transform.SetParent(mediaContent, false);
                RectTransform rect = cardObject.GetComponent<RectTransform>();
                SetTopLeftRect(rect, 0f, 0f, 100f, MediaCardHeight - MediaCardGap);
                Image background = cardObject.GetComponent<Image>();
                // 卡片是凸起内容，所以远高于它们所落的面板
                // (0.137 对 0.071)，而不是只高一线。还用老的 ButtonNormal
                // 值时，卡片和它背后的凹槽颜色几乎一样，媒体列
                // 读起来就是一整块扁平的东西——眼睛找不到
                // 可抓的独立物体。
                ApplyRounded(background, 6f, false, 0f);
                background.color = Raised;
                Button button = cardObject.GetComponent<Button>();
                button.targetGraphic = background;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                // 比它所落的卡片高一档，这样缩略图读起来是卡片
                // 里的内容，而不是卡片上的一个洞。
                RawImage image = CreateRawImage("Thumbnail", rect, 6f, 6f, 90f, 76f, ThumbWell);
                image.raycastTarget = false;
                TextMeshProUGUI label = CreateText("Label", rect, 7f, 86f, 90f, 28f, string.Empty, 9.5f, TextSecondary, TextAlignmentOptions.MidlineLeft, false);
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.raycastTarget = false;
                card = new MediaCard
                {
                    GameObject = cardObject,
                    Rect = rect,
                    Image = image,
                    Label = label,
                    Button = button
                };
                button.onClick.AddListener(delegate { ActivateMediaCard(card); });
                card.GameObject.SetActive(false);
                mediaCards.Add(card);
                created = true;
            }

            if (created)
            {
                mediaCardsDirty = true;
            }
        }

        private void RefreshProjectWorkspace(bool force)
        {
            string levelPath = EditorProjectSession.GetCurrentLevelPath();
            if (projectSession == null
                || !string.Equals(projectSession.LevelPath, levelPath, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseMediaTextures();
                projectSession = EditorProjectSession.LoadCurrent();
                selectedProjectDirectory = ".";
                selectedMediaPath = null;
                projectDataDirty = false;
                RebuildProjectTree();
                RebuildVisibleMedia();
                return;
            }

            if (projectSession != null && projectSession.RefreshMediaIndex())
            {
                ReleaseMediaTextures();
                RebuildProjectTree();
                RebuildVisibleMedia();
            }
            projectDataDirty = false;
        }

        private void TickProjectWorkspace()
        {
            if (!visible || projectTreeContent == null)
            {
                return;
            }

            TickMediaCardDrag();

            float now = Time.unscaledTime;
            if (projectDataDirty || now >= nextProjectRefresh)
            {
                nextProjectRefresh = now + 1.2f;
                RefreshProjectWorkspace(projectDataDirty);
            }
            UpdateProjectTreeRows();
            if (mediaCardsDirty)
            {
                UpdateMediaCards();
            }
            ProcessMediaThumbnailQueue();
        }

        private void LayoutMediaSearchControls(float mediaWidth)
        {
            if (mediaSearchField == null)
            {
                return;
            }

            float searchHeight = 24f;
            float refreshWidth = 30f;
            float searchWidth = Mathf.Max(40f, mediaWidth - 24f - refreshWidth - 6f);
            SetTopLeftRect(mediaSearchField.GetComponent<RectTransform>(), 12f, MediaSearchY, searchWidth, searchHeight);
            if (mediaRefreshButton != null)
            {
                SetTopLeftRect(
                    mediaRefreshButton.GetComponent<RectTransform>(),
                    mediaWidth - 12f - refreshWidth,
                    MediaSearchY,
                    refreshWidth,
                    searchHeight);
            }
        }

        /// <summary>
        /// 落在缩略图上的按下动作会挂上一个拖放；随后场景交互会在
        /// 指针一进入预览视口的瞬间就创建装饰物。
        /// </summary>
        private void TickMediaCardDrag()
        {
            // 安全网：漏掉一次 mouse-up（失焦、alt-tab）时，
            // 列表绝不能就这样变得滚不动。在这里恢复，而不是只在按钮抬起分支里恢复。
            if (mediaScroll != null && !mediaScroll.enabled
                && !Input.GetMouseButton(0)
                && !EditorSceneInteraction.IsMediaDropActive)
            {
                mediaScroll.enabled = true;
            }

            // 落在缩略图上的按下动作会挂上一个拖放；随后场景交互会在
            // 指针一进入预览视口的瞬间就创建装饰物。
            if (Input.GetMouseButtonDown(0))
            {
                mediaDragCandidate = FindMediaCardAt(Input.mousePosition);
                return;
            }

            if (Input.GetMouseButton(0) && mediaDragCandidate != null && !EditorSceneInteraction.IsMediaDropActive)
            {
                float distance = Vector2.Distance(mediaDragPressScreen, Input.mousePosition);
                if (distance < 6f)
                {
                    return;
                }

                EditorProjectMediaItem item = mediaDragCandidate;
                mediaDragCandidate = null;

                // 否则 ScrollRect 会把同一次拖动当成列表平移，
                // 在装饰物跟着光标走的同时把整列从指针底下滚走。
                // 手势一结束就恢复。
                if (mediaScroll != null)
                {
                    mediaScroll.enabled = false;
                }

                EditorSceneInteraction.ArmMediaDrop(item.FileName, item.AbsolutePath);
                return;
            }

            if (Input.GetMouseButtonUp(0))
            {
                mediaDragCandidate = null;
                if (mediaScroll != null && !mediaScroll.enabled)
                {
                    mediaScroll.enabled = true;
                }
            }
        }

        private EditorProjectMediaItem FindMediaCardAt(Vector2 screenPoint)
        {
            for (int i = 0; i < mediaCards.Count; i++)
            {
                MediaCard card = mediaCards[i];
                if (card == null || !card.GameObject.activeInHierarchy || card.Rect == null)
                {
                    continue;
                }
                if (card.BoundIndex < 0 || card.BoundIndex >= visibleProjectMedia.Count)
                {
                    continue;
                }
                if (RectTransformUtility.RectangleContainsScreenPoint(card.Rect, screenPoint, null))
                {
                    mediaDragPressScreen = screenPoint;
                    return visibleProjectMedia[card.BoundIndex];
                }
            }

            return null;
        }

        private EditorProjectMediaItem mediaDragCandidate;
        private Vector2 mediaDragPressScreen;

        private void OnMediaScrollChanged(Vector2 ignored)
        {
            mediaThumbnailLoadNotBefore = Time.unscaledTime + MediaThumbnailLoadDelay;
            if (mediaContent == null)
            {
                return;
            }

            int rowCount = Mathf.CeilToInt(visibleProjectMedia.Count / 2f);
            int firstRow = Mathf.Clamp(
                Mathf.FloorToInt(Mathf.Max(0f, mediaContent.anchoredPosition.y) / MediaCardHeight),
                0,
                Mathf.Max(0, rowCount - 1)
            );
            if (firstRow != mediaCardsBoundFirstRow)
            {
                ClearMediaThumbnailQueue();
                mediaCardsDirty = true;
                UpdateMediaCards();
            }
        }

        private void RebuildProjectTree()
        {
            if (projectTreeContent == null)
            {
                return;
            }

            var labels = new List<string>();
            var directories = new List<string>();
            var selectable = new List<bool>();
            labels.Add(projectSession == null ? "未保存谱面" : Path.GetFileNameWithoutExtension(projectSession.ProjectPath));
            directories.Add(".");
            selectable.Add(false);
            if (projectSession != null)
            {
                labels.Add("  " + Path.GetFileName(projectSession.LevelPath));
                directories.Add(".");
                selectable.Add(false);
                labels.Add("媒体  ·  " + projectSession.Media.Count);
                directories.Add(".");
                selectable.Add(true);
                for (int i = 0; i < projectSession.MediaDirectories.Count; i++)
                {
                    string directory = projectSession.MediaDirectories[i];
                    int depth = directory.Split('/').Length;
                    int slash = directory.LastIndexOf('/');
                    string displayName = slash < 0 ? directory : directory.Substring(slash + 1);
                    labels.Add(new string(' ', Mathf.Clamp(depth, 1, 4) * 2) + displayName);
                    directories.Add(directory);
                    selectable.Add(true);
                }
            }

            EnsureProjectTreeRows(Mathf.Max(1, labels.Count));
            projectTreeContent.sizeDelta = new Vector2(0f, Mathf.Max(projectTreeViewport.rect.height, labels.Count * ProjectTreeRowHeight));
            for (int i = 0; i < projectTreeRows.Count; i++)
            {
                ProjectTreeRow row = projectTreeRows[i];
                if (i >= labels.Count)
                {
                    row.Label.text = string.Empty;
                    row.Directory = null;
                    row.Selectable = false;
                    row.GameObject.SetActive(false);
                    continue;
                }
                row.Directory = directories[i];
                row.Selectable = selectable[i];
                row.Rect.anchoredPosition = new Vector2(0f, -i * ProjectTreeRowHeight);
                row.Rect.sizeDelta = new Vector2(projectTreeViewport.rect.width, ProjectTreeRowHeight - 2f);
                row.Label.text = labels[i];
                row.Button.interactable = row.Selectable;
                SetButtonPalette(row.Button, row.Selectable && string.Equals(row.Directory, selectedProjectDirectory, StringComparison.OrdinalIgnoreCase));
                row.GameObject.SetActive(true);
            }
        }

        private void UpdateProjectTreeRows()
        {
            if (projectTreeScroll == null || projectTreeContent == null)
            {
                return;
            }
            float offset = Mathf.Max(0f, projectTreeContent.anchoredPosition.y);
            int first = Mathf.Clamp(Mathf.FloorToInt(offset / ProjectTreeRowHeight), 0, Mathf.Max(0, projectTreeRows.Count - 1));
            int visible = Mathf.CeilToInt(projectTreeViewport.rect.height / ProjectTreeRowHeight) + 2;
            for (int i = 0; i < projectTreeRows.Count; i++)
            {
                projectTreeRows[i].GameObject.SetActive(i >= first && i < first + visible && projectTreeRows[i].Label.text.Length > 0);
            }
        }

        private void ActivateProjectTreeRow(ProjectTreeRow row)
        {
            if (row == null || !row.Selectable)
            {
                return;
            }
            selectedProjectDirectory = string.IsNullOrWhiteSpace(row.Directory) ? "." : row.Directory;
            if (mediaContent != null)
            {
                mediaContent.anchoredPosition = Vector2.zero;
            }
            RebuildProjectTree();
            RebuildVisibleMedia();
        }

        private void RebuildVisibleMedia(bool resetScroll = true)
        {
            ClearMediaThumbnailQueue();
            visibleProjectMedia.Clear();
            if (projectSession != null)
            {
                for (int i = 0; i < projectSession.Media.Count; i++)
                {
                    EditorProjectMediaItem item = projectSession.Media[i];
                    if (selectedProjectDirectory != "."
                        && !string.Equals(item.DirectoryPath, selectedProjectDirectory, StringComparison.OrdinalIgnoreCase)
                        && !item.DirectoryPath.StartsWith(selectedProjectDirectory + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!MediaMatchesSearch(item))
                    {
                        continue;
                    }
                    visibleProjectMedia.Add(item);
                }
            }

            // 最新的排最前：刚编辑或刚导出的那张图就是要找的那张，而
            // 磁盘上的顺序是按字母排的，恰好把它埋掉。
            visibleProjectMedia.Sort((left, right) =>
            {
                int byTime = right.LastWriteUtcTicks.CompareTo(left.LastWriteUtcTicks);
                return byTime != 0
                    ? byTime
                    : string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase);
            });

            if (mediaTitle != null)
            {
                mediaTitle.text = string.IsNullOrEmpty(mediaSearchQuery)
                    ? "媒体  ·  " + visibleProjectMedia.Count
                    : "媒体  ·  " + visibleProjectMedia.Count + " / 搜索";
            }
            if (mediaContent != null)
            {
                int rows = visibleProjectMedia.Count;
                mediaContent.sizeDelta = new Vector2(0f, Mathf.Max(mediaViewport.rect.height, rows * MediaCardHeight));
                if (resetScroll)
                {
                    mediaContent.anchoredPosition = Vector2.zero;
                }
            }
            mediaCardsBoundVisibleCount = -1;
            mediaCardsDirty = true;
            UpdateMediaCards();
        }

        private bool MediaMatchesSearch(EditorProjectMediaItem item)
        {
            if (string.IsNullOrEmpty(mediaSearchQuery))
            {
                return true;
            }

            // 匹配整个相对路径，这样文件夹名也能缩小列表范围。
            return item.RelativePath.IndexOf(mediaSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnMediaSearchChanged(string value)
        {
            string next = value ?? string.Empty;
            if (string.Equals(next, mediaSearchQuery, StringComparison.Ordinal))
            {
                return;
            }
            mediaSearchQuery = next;
            RebuildVisibleMedia();
        }

        private string mediaSearchQuery = string.Empty;

        private void UpdateMediaCards()
        {
            if (mediaScroll == null || mediaContent == null || mediaViewport == null)
            {
                return;
            }
            // 一行一张卡片。两列会把缩略图砍半、把名字挤成
            // 省略号，而媒体列表是装饰物的拖放来源，
            // 所以更宽的行值得多滚几下。
            float cardWidth = Mathf.Max(40f, mediaViewport.rect.width);
            int requiredRows = Mathf.CeilToInt(mediaViewport.rect.height / MediaCardHeight) + 2;
            EnsureMediaCards(Mathf.Max(4, requiredRows));
            int rowCount = visibleProjectMedia.Count;
            int firstRow = Mathf.Clamp(
                Mathf.FloorToInt(Mathf.Max(0f, mediaContent.anchoredPosition.y) / MediaCardHeight),
                0,
                Mathf.Max(0, rowCount - 1)
            );
            bool bindingChanged = firstRow != mediaCardsBoundFirstRow
                || visibleProjectMedia.Count != mediaCardsBoundVisibleCount
                || mediaCards.Count != mediaCardsBoundPoolCount
                || !Mathf.Approximately(cardWidth, mediaCardsBoundWidth)
                || !string.Equals(selectedMediaPath, mediaCardsBoundSelectionPath, StringComparison.OrdinalIgnoreCase);
            mediaCardsDirty = false;
            if (!bindingChanged)
            {
                return;
            }

            mediaCardsBoundFirstRow = firstRow;
            mediaCardsBoundVisibleCount = visibleProjectMedia.Count;
            mediaCardsBoundPoolCount = mediaCards.Count;
            mediaCardsBoundWidth = cardWidth;
            mediaCardsBoundSelectionPath = selectedMediaPath;
            int firstIndex = firstRow;
            for (int i = 0; i < mediaCards.Count; i++)
            {
                int itemIndex = firstIndex + i;
                MediaCard card = mediaCards[i];
                if (itemIndex >= visibleProjectMedia.Count)
                {
                    UnbindMediaCard(card);
                    continue;
                }
                EditorProjectMediaItem item = visibleProjectMedia[itemIndex];
                card.BoundIndex = itemIndex;
                card.BoundPath = item.RelativePath;
                card.Rect.anchoredPosition = new Vector2(0f, -itemIndex * MediaCardHeight);
                card.Rect.sizeDelta = new Vector2(cardWidth, MediaCardHeight - MediaCardGap);

                // 缩略图在左，名字占满这一行剩下的部分。方形，但从不超过
                // 整行的约 45%，这样窄的媒体列在省略号开始之前
                // 仍然给文件名留得下位置。
                float thumbnailHeight = MediaCardHeight - MediaCardGap - 12f;
                float thumbnailWidth = Mathf.Min(thumbnailHeight, Mathf.Max(40f, cardWidth * 0.45f));
                card.CardWidth = cardWidth;
                card.ThumbnailHeight = thumbnailHeight;
                card.ThumbnailWidth = thumbnailWidth;

                // 把纹理按比例装进新框里，而不是直接把框赋给它。
                // 裸用 SetTopLeftRect 会把贴图拉伸填满，所以每次重新布局——
                // 拖时间轴会改变面板宽度，也就改变了这个框——都会
                // 让图片被压成新框碰巧具有的那个宽高比。这里
                // 也是缩略图首次加载时应用等比适配的地方。
                LayoutMediaThumbnail(card, thumbnailWidth, thumbnailHeight);
                SetTopLeftRect(
                    card.Label.rectTransform,
                    thumbnailWidth + 12f,
                    0f,
                    Mathf.Max(20f, cardWidth - thumbnailWidth - 20f),
                    MediaCardHeight - MediaCardGap);
                card.Label.text = item.FileName;
                card.Label.color = string.Equals(selectedMediaPath, item.RelativePath, StringComparison.OrdinalIgnoreCase) ? TextPrimary : TextSecondary;
                SetButtonPalette(card.Button, string.Equals(selectedMediaPath, item.RelativePath, StringComparison.OrdinalIgnoreCase));
                BindMediaThumbnail(card, item);
                card.GameObject.SetActive(true);
            }

            TrimMediaThumbnailCache();
        }

        private void ActivateMediaCard(MediaCard card)
        {
            if (card == null || card.BoundIndex < 0 || card.BoundIndex >= visibleProjectMedia.Count)
            {
                return;
            }
            selectedMediaPath = visibleProjectMedia[card.BoundIndex].RelativePath;
            mediaCardsDirty = true;
            UpdateMediaCards();
        }

        private void BindMediaThumbnail(MediaCard card, EditorProjectMediaItem item)
        {
            string key = GetMediaThumbnailKey(item);
            if (!string.Equals(card.TexturePath, key, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseCardTexture(card);
                card.TexturePath = key;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                SetMediaThumbnailPlaceholder(card, true);
                return;
            }

            if (string.Equals(item.Extension, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                SetMediaThumbnailPlaceholder(card, true);
                return;
            }

            MediaThumbnailCacheEntry entry;
            if (mediaThumbnailCache.TryGetValue(key, out entry))
            {
                TouchMediaThumbnail(entry);
                ApplyMediaThumbnailToCard(card, entry);
                return;
            }

            SetMediaThumbnailPlaceholder(card, false);
            QueueMediaThumbnail(item, key);
        }

        private static string GetMediaThumbnailKey(EditorProjectMediaItem item)
        {
            if (item == null)
            {
                return null;
            }
            return !string.IsNullOrWhiteSpace(item.AbsolutePath)
                ? item.AbsolutePath
                : item.RelativePath;
        }

        private void QueueMediaThumbnail(EditorProjectMediaItem item, string key)
        {
            if (item == null
                || string.IsNullOrWhiteSpace(key)
                || mediaThumbnailCache.ContainsKey(key)
                || !mediaThumbnailQueued.Add(key))
            {
                return;
            }

            mediaThumbnailQueue.Enqueue(new MediaThumbnailRequest
            {
                Key = key,
                AbsolutePath = item.AbsolutePath,
                Extension = item.Extension
            });
        }

        private void ProcessMediaThumbnailQueue()
        {
            if (!visible
                || mediaThumbnailQueue.Count == 0
                || Time.unscaledTime < mediaThumbnailLoadNotBefore)
            {
                return;
            }

            // 解码一张缩略图是对源图的同步读取，源图可能有
            // 好几 MB、宽过 5000px，耗时几十毫秒。每帧解一张
            // 在列表滚动时仍然会卡住每一帧，所以改成按墙上时钟
            // 限流：一直解码到预算用光，
            // 然后让这一帧渲染出去，下一帧继续。
            float budgetEnd = Time.realtimeSinceStartup + MediaThumbnailBudgetSeconds;
            int decodeBudget = MediaThumbnailMaxPerFrame;
            while (mediaThumbnailQueue.Count > 0 && decodeBudget-- > 0)
            {
                MediaThumbnailRequest request = mediaThumbnailQueue.Dequeue();
                mediaThumbnailQueued.Remove(request.Key);
                MediaThumbnailCacheEntry existing;
                if (mediaThumbnailCache.TryGetValue(request.Key, out existing))
                {
                    ApplyMediaThumbnailToBoundCards(request.Key, existing);
                }
                else
                {
                    DecodeMediaThumbnail(request);
                }

                if (Time.realtimeSinceStartup >= budgetEnd)
                {
                    break;
                }
            }
        }

        private void DecodeMediaThumbnail(MediaThumbnailRequest request)
        {
            Texture2D texture = null;
            bool failed = true;
            try
            {
                if (!string.Equals(request.Extension, ".gif", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(request.AbsolutePath)
                    && File.Exists(request.AbsolutePath))
                {
                    byte[] bytes = File.ReadAllBytes(request.AbsolutePath);
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    if (TryLoadTextureImage(texture, bytes))
                    {
                        texture = CreateThumbnailTexture(texture, 256);
                        texture.filterMode = FilterMode.Bilinear;
                        texture.wrapMode = TextureWrapMode.Clamp;
                        failed = false;
                    }
                    else
                    {
                        Destroy(texture);
                        texture = null;
                    }
                }
            }
            catch (Exception exception)
            {
                if (texture != null)
                {
                    Destroy(texture);
                    texture = null;
                }
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("Media thumbnail failed: " + exception.Message);
                }
            }

            var entry = new MediaThumbnailCacheEntry
            {
                Key = request.Key,
                Texture = texture,
                Failed = failed
            };
            TouchMediaThumbnail(entry);
            mediaThumbnailCache[request.Key] = entry;
            ApplyMediaThumbnailToBoundCards(request.Key, entry);
            TrimMediaThumbnailCache();
        }

        private const float MediaThumbnailBudgetSeconds = 0.006f;
        private const int MediaThumbnailMaxPerFrame = 2;

        private void ApplyMediaThumbnailToBoundCards(string key, MediaThumbnailCacheEntry entry)
        {
            for (int i = 0; i < mediaCards.Count; i++)
            {
                MediaCard card = mediaCards[i];
                if (card.GameObject.activeSelf
                    && string.Equals(card.TexturePath, key, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMediaThumbnailToCard(card, entry);
                    LayoutMediaThumbnail(card, card.ThumbnailWidth, card.ThumbnailHeight);
                }
            }
        }

        private static void ApplyMediaThumbnailToCard(MediaCard card, MediaThumbnailCacheEntry entry)
        {
            card.Texture = entry == null ? null : entry.Texture;
            card.Image.texture = card.Texture;
            card.Image.color = entry == null || entry.Failed || card.Texture == null
                ? new Color(0.16f, 0.19f, 0.24f, 1f)
                : Color.white;
        }

        private static void SetMediaThumbnailPlaceholder(MediaCard card, bool failed)
        {
            card.Texture = null;
            card.Image.texture = null;
            card.Image.color = failed
                ? new Color(0.16f, 0.19f, 0.24f, 1f)
                : new Color(PanelAlt.r, PanelAlt.g, PanelAlt.b, 1f);
        }

        private void TouchMediaThumbnail(MediaThumbnailCacheEntry entry)
        {
            if (entry != null)
            {
                entry.LastUse = ++mediaThumbnailUseCounter;
            }
        }

        private void TrimMediaThumbnailCache()
        {
            while (mediaThumbnailCache.Count > MediaThumbnailCacheCapacity)
            {
                MediaThumbnailCacheEntry oldest = null;
                foreach (MediaThumbnailCacheEntry entry in mediaThumbnailCache.Values)
                {
                    if (!IsMediaThumbnailBound(entry.Key)
                        && (oldest == null || entry.LastUse < oldest.LastUse))
                    {
                        oldest = entry;
                    }
                }

                if (oldest == null)
                {
                    return;
                }

                mediaThumbnailCache.Remove(oldest.Key);
                if (oldest.Texture != null)
                {
                    Destroy(oldest.Texture);
                }
            }
        }

        private bool IsMediaThumbnailBound(string key)
        {
            for (int i = 0; i < mediaCards.Count; i++)
            {
                MediaCard card = mediaCards[i];
                if (card.GameObject.activeSelf
                    && string.Equals(card.TexturePath, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void UnbindMediaCard(MediaCard card)
        {
            card.BoundIndex = -1;
            card.BoundPath = null;
            ReleaseCardTexture(card);
            card.GameObject.SetActive(false);
        }

        private void ReleaseCardTexture(MediaCard card)
        {
            card.Texture = null;
            card.Image.texture = null;
            card.TexturePath = null;
        }

        private static bool TryLoadTextureImage(Texture2D texture, byte[] bytes)
        {
            if (!imageLoaderSearched)
            {
                imageLoaderSearched = true;
                Type imageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false);
                if (imageConversionType == null)
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length && imageConversionType == null; i++)
                    {
                        imageConversionType = assemblies[i].GetType("UnityEngine.ImageConversion", false);
                    }
                }
                if (imageConversionType != null)
                {
                    loadImageMethod = imageConversionType.GetMethod(
                        "LoadImage",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
                        null
                    );
                }
            }

            if (loadImageMethod == null)
            {
                return false;
            }
            object result = loadImageMethod.Invoke(null, new object[] { texture, bytes, false });
            return result is bool && (bool)result;
        }

        private static Texture2D CreateThumbnailTexture(Texture2D source, int maximumDimension)
        {
            if (source == null || Mathf.Max(source.width, source.height) <= maximumDimension)
            {
                return source;
            }

            float scale = maximumDimension / (float)Mathf.Max(source.width, source.height);
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture previous = RenderTexture.active;
            Texture2D thumbnail = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                thumbnail = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                thumbnail.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                thumbnail.Apply(false, false);
                return thumbnail;
            }
            catch
            {
                if (thumbnail != null)
                {
                    Destroy(thumbnail);
                    thumbnail = null;
                }
                return source;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (thumbnail != null && thumbnail != source)
                {
                    Destroy(source);
                }
            }
        }

        /// <summary>
        /// 把解码后的缩略图按原宽高比装进一个框里。
        ///
        /// 这个框必须是卡片布局真正把缩略图放进去的那个框。以前
        /// 是用卡片宽度减一个固定内缩量推出来的，那只在
        /// 缩略图占满整行时成立；行改成「缩略图在左、名字在右」之后，
        /// 适配跑在一个比缩略图实际占位宽得多的框上，把图压扁了。
        /// </summary>
        private static void LayoutMediaThumbnail(MediaCard card, float availableWidth, float availableHeight)
        {
            availableWidth = Mathf.Max(1f, availableWidth);
            availableHeight = Mathf.Max(1f, availableHeight);
            float width = availableWidth;
            float height = availableHeight;
            if (card.Texture != null && card.Texture.width > 0 && card.Texture.height > 0)
            {
                float imageAspect = card.Texture.width / (float)card.Texture.height;
                float boxAspect = availableWidth / availableHeight;
                if (imageAspect > boxAspect)
                {
                    height = availableWidth / imageAspect;
                }
                else
                {
                    width = availableHeight * imageAspect;
                }
            }
            SetTopLeftRect(
                card.Image.rectTransform,
                6f + (availableWidth - width) * 0.5f,
                6f + (availableHeight - height) * 0.5f,
                width,
                height
            );
        }

        private void ReleaseMediaTextures()
        {
            for (int i = 0; i < mediaCards.Count; i++)
            {
                ReleaseCardTexture(mediaCards[i]);
                mediaCards[i].GameObject.SetActive(false);
            }
            ClearMediaThumbnailCache();
            mediaCardsDirty = true;
            mediaCardsBoundFirstRow = -1;
            mediaCardsBoundVisibleCount = -1;
            mediaCardsBoundPoolCount = -1;
            mediaCardsBoundWidth = -1f;
            mediaCardsBoundSelectionPath = null;
        }

        private void ClearMediaThumbnailQueue()
        {
            mediaThumbnailQueue.Clear();
            mediaThumbnailQueued.Clear();
        }

        private void ClearMediaThumbnailCache()
        {
            ClearMediaThumbnailQueue();
            foreach (MediaThumbnailCacheEntry entry in mediaThumbnailCache.Values)
            {
                if (entry.Texture != null)
                {
                    Destroy(entry.Texture);
                }
            }
            mediaThumbnailCache.Clear();
            mediaThumbnailUseCounter = 0;
            mediaThumbnailLoadNotBefore = 0f;
        }

        private void CreateBrowserRow(float width)
        {
            BrowserRow row = null;
            TextMeshProUGUI label;
            Button button = CreateButton(
                "ObjectRow" + browserRows.Count,
                browserContent,
                0f,
                0f,
                width,
                BrowserRowHeight - 2f,
                string.Empty,
                false,
                delegate { ActivateBrowserRow(row); },
                out label
            );
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.margin = new Vector4(10f, 0f, 8f, 0f);
            label.fontSize = 11.5f;
            row = new BrowserRow
            {
                GameObject = button.gameObject,
                Rect = button.GetComponent<RectTransform>(),
                Button = button,
                Label = label
            };
            row.GameObject.SetActive(false);
            browserRows.Add(row);
        }

        private void BuildPreview(RectTransform parent)
        {
            float width = previewLayout.StageWidth;
            // 透明根节点，描边与其他面板相同。它必须保持不填充：
            // 这个面板的 rect 横跨实时预览，在这里填不透明色会盖住
            // 游戏画面，而那正是这个面板存在的唯一理由。AddPanelBorder 画的是
            // 圆环而不是实心块，所以它只描出边缘、不遮挡任何东西。
            Image panel = CreateImage("Preview", parent, previewLayout.StageX, PreviewPanelY, width, PreviewPanelHeight, Color.clear);
            RectTransform root = panel.rectTransform;
            previewPanel = root;
            // 用的是圆环变体，不是 AddPanelBorder：后者画实心圆角矩形，靠
            // 面板挡住它们的中间，而透明的预览做不到这一点。
            AddPanelOutline(panel, CornerLarge);

            Image toolbar = CreateRoundedPanel("Toolbar", root, 0f, 0f, width, 52f, new Color(Header.r, Header.g, Header.b, 0.97f), CornerLarge);
            toolbar.raycastTarget = true;
            previewToolbar = toolbar.rectTransform;
            previewHeading = CreateText("Heading", root, PanelHeadingInset, 0f,
                220f, PanelHeadingHeight, "预览", 13f, TextPrimary,
                TextAlignmentOptions.MidlineLeft, true).rectTransform;
            // 预览面板顶部有一条 52px 的工具栏条，所以这个面板的分隔线该落在
            // 工具栏的下沿，而不是共用的标题高度——放在 27 会画进
            // 工具栏里，读起来是横穿它的一条杂线。全宽的理由
            // 与其他面板相同。
            CreateHeadingRule("PreviewRule", root, 52f);

            BuildLaneFilterButtons(root);

            // 缩放锁定以前放在这里。现在它是时间轴
            // 播放控制带上的一把锁按钮，同时也覆盖相机位置；
            // 两边都留等于同一个设置有两个控件。

            float stageY = previewLayout.StageY - PreviewPanelY;
            Image stage = CreateImage("Viewport", root, 0f, stageY, previewLayout.StageWidth, previewLayout.StageHeight, Color.clear);
            stage.raycastTarget = false;
            previewViewport = stage.rectTransform;
            previewHandoffImage = CreateStretchRawImage("NativeHandoffFrame", previewViewport, Color.white);
            previewHandoffImage.gameObject.SetActive(false);
            // 比面板描边低一档：舞台坐在面板里面，所以它的边框不能
            // 跟面板自己的边缘抢眼。用四条线而不是一个九宫格框，因为
            // 舞台是不透明的，而框不是。
            Color viewportBorder = new Color(0.280f, 0.280f, 0.280f, 1f);
            CreateAnchoredLine("Top", stage.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), viewportBorder);
            CreateAnchoredLine("Bottom", stage.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), viewportBorder);
            CreateAnchoredLine("Left", stage.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), viewportBorder);
            CreateAnchoredLine("Right", stage.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0f), viewportBorder);
        }

        /// <summary>
        /// 预览工具栏右侧那一排车道筛选按钮。
        ///
        /// 它们原本是时间轴顶部的一排文字按钮（全部/镜头/视效/…），
        /// 现在改成游戏原生的事件图标、不带文字、可多选，并挪到预览画面上方——
        /// 离它们控制的东西近，也给时间轴的工具行腾出位置。
        ///
        /// 状态归时间轴所有（它才是按车道分组事件的那个），这里只建按钮、
        /// 转发点击，并把图标注册回去以便同步选中态。
        /// </summary>
        private void BuildLaneFilterButtons(RectTransform toolbarRoot)
        {
            if (timelineView == null)
            {
                // BuildPreview 在 BuildTimeline 之前跑，所以第一次走到这里时
                // 时间轴还没建出来。真正的按钮在构建完成后由 RefreshLaneFilterButtons 补上。
                return;
            }

            const int laneCount = 6;
            const float size = 26f;
            const float gap = 4f;
            float x = 96f;
            for (int lane = 0; lane < laneCount; lane++)
            {
                int index = lane;
                Image background = CreateRoundedPanel(
                    "LaneFilter" + lane, toolbarRoot, x, 13f, size, size, ButtonNormal, CornerSmall);
                background.raycastTarget = true;
                Button button = background.gameObject.AddComponent<Button>();
                button.targetGraphic = background;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                SetButtonPalette(button, false);
                button.onClick.AddListener(delegate { timelineView.ToggleLane(index); });

                Sprite icon = timelineView.GetLaneIcon(lane);
                if (icon != null)
                {
                    Image iconImage = CreateImage("Icon", background.rectTransform, 4f, 4f, size - 8f, size - 8f, Color.white);
                    iconImage.sprite = icon;
                    iconImage.preserveAspect = true;
                    timelineView.RegisterLaneFilterButton(lane, iconImage);
                }

                laneFilterButtonRects.Add(background.rectTransform);
                x += size + gap;
            }
        }

        /// <summary>
        /// 谱面加载完成后再刷一次车道图标。
        /// 按钮是在预览面板里建的，那时谱面数据还没进来；上面 GetLaneIcon 的
        /// 回退分支要等事件就绪才找得到图标。
        /// </summary>
        internal void RefreshLaneFilterIcons()
        {
            if (timelineView == null)
            {
                return;
            }
            for (int lane = 0; lane < laneFilterButtonRects.Count; lane++)
            {
                RectTransform button = laneFilterButtonRects[lane];
                if (button == null)
                {
                    continue;
                }
                Transform existing = button.Find("Icon");
                Image iconImage = existing == null ? null : existing.GetComponent<Image>();
                Sprite icon = timelineView.GetLaneIcon(lane);
                if (icon == null)
                {
                    continue;
                }
                if (iconImage == null)
                {
                    iconImage = CreateImage("Icon", button, 4f, 4f, button.sizeDelta.x - 8f, button.sizeDelta.x - 8f, Color.white);
                    iconImage.preserveAspect = true;
                }
                iconImage.sprite = icon;
                timelineView.RegisterLaneFilterButton(lane, iconImage);
            }
        }

        private void BuildViewportMatte(RectTransform parent)
        {
            Image top = CreateImage("ViewportMatteTop", parent, 0f, 0f, DesignWidth, previewLayout.StageY, Background);
            Image bottom = CreateImage(
                "ViewportMatteBottom",
                parent,
                0f,
                previewLayout.StageY + previewLayout.StageHeight,
                DesignWidth,
                DesignHeight - previewLayout.StageY - previewLayout.StageHeight,
                Background
            );
            Image left = CreateImage("ViewportMatteLeft", parent, 0f, previewLayout.StageY, previewLayout.StageX, previewLayout.StageHeight, Background);
            Image right = CreateImage(
                "ViewportMatteRight",
                parent,
                previewLayout.StageX + previewLayout.StageWidth,
                previewLayout.StageY,
                DesignWidth - previewLayout.StageX - previewLayout.StageWidth,
                previewLayout.StageHeight,
                Background
            );

            top.raycastTarget = true;
            bottom.raycastTarget = true;
            left.raycastTarget = true;
            right.raycastTarget = true;
            matteTop = top.rectTransform;
            matteBottom = bottom.rectTransform;
            matteLeft = left.rectTransform;
            matteRight = right.rectTransform;
        }

        private void BuildInspector(RectTransform parent)
        {
            Image panel = CreateRoundedPanel(
                "Inspector",
                parent,
                previewLayout.InspectorX,
                PreviewPanelY,
                previewLayout.RightPanelWidth,
                PreviewPanelHeight,
                Panel,
                CornerLarge
            );
            panel.raycastTarget = true;
            inspectorPanel = panel.rectTransform;
            AddPanelBorder(panel, CornerLarge);
            inspectorHeading = CreateText("Heading", panel.rectTransform, PanelHeadingInset, 0f,
                previewLayout.RightPanelWidth - PanelHeadingInset * 2f, PanelHeadingHeight,
                "参数", 13f, TextPrimary, TextAlignmentOptions.MidlineLeft, true).rectTransform;
            CreateHeadingRule("InspectorRule", panel.rectTransform, PanelHeadingRule);
            propertyPanel = panel.gameObject.AddComponent<EditorPropertyPanel>();
            propertyPanel.Initialize(panel.rectTransform, uiFont, this);
            LogPropertyMetadataProbe();
        }

        private static bool propertyMetadataProbed;

        /// <summary>
        /// 只读、一次性。参数面板是从游戏自己的
        /// PropertyInfo 元数据生成的，不是手写的一张键表，所以
        /// 在写控件分派之前必须先看清那份元数据的真实形状。
        /// 只打印枚举本身只能说明枚举里有什么，说不清哪些键用
        /// 哪个控件、它们带什么范围、标签是否本地化。
        /// </summary>
        private static void LogPropertyMetadataProbe()
        {
            if (propertyMetadataProbed || Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            LevelEvent sample = null;
            if (editor != null && editor.events != null)
            {
                for (int i = 0; i < editor.events.Count; i++)
                {
                    LevelEvent candidate = editor.events[i];
                    if (candidate != null && candidate.eventType == LevelEventType.MoveDecorations)
                    {
                        sample = candidate;
                        break;
                    }
                }
            }
            if (sample == null || sample.info == null || sample.info.propertiesInfo == null)
            {
                return;
            }

            propertyMetadataProbed = true;
            Main.Logger.Log("[PropProbe] event=" + sample.eventType
                + " props=" + sample.info.propertiesInfo.Count);

            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in sample.info.propertiesInfo)
            {
                ADOFAI.PropertyInfo info = pair.Value;

                bool exists;
                string label = RDString.GetWithCheck(
                    "editor." + info.levelEventInfo.name + "." + info.name, out exists);
                if (!exists)
                {
                    label = RDString.GetWithCheck("editor." + info.name, out exists);
                    if (!exists)
                    {
                        label = info.customLabel;
                    }
                }

                object current = null;
                try
                {
                    current = sample[info.name];
                }
                catch (Exception)
                {
                    // 读不出来的值也值得按键和类型报出来。
                }

                string enumOptions = string.Empty;
                if (info.type == PropertyType.Enum && info.enumType != null)
                {
                    try
                    {
                        string[] names = Enum.GetNames(info.enumType);
                        List<string> rendered = new List<string>();
                        for (int n = 0; n < names.Length; n++)
                        {
                            rendered.Add(names[n] + "="
                                + RDString.GetEnumValue(info.enumType.Name, names[n]));
                        }
                        enumOptions = " opts[" + string.Join("|", rendered.ToArray()) + "]";
                    }
                    catch (Exception exception)
                    {
                        enumOptions = " opts<" + exception.GetType().Name + ">";
                    }
                }

                Main.Logger.Log("[PropProbe] " + info.order.ToString("000") + " " + info.name
                    + " type=" + info.type
                    + " ctrl=" + info.controlType
                    + " canDisable=" + info.canBeDisabled
                    + " slider=" + info.slider
                    + " invisible=" + info.invisible
                    + " unit=" + (info.unit ?? "-")
                    + " group=" + (info.groupName ?? "-")
                    + " range=[" + info.int_min + ".." + info.int_max
                    + " / " + info.float_min + ".." + info.float_max + "]"
                    + " def=" + (info.value_default == null ? "null" : info.value_default.GetType().Name)
                    + " cur=" + (current == null ? "null" : current.GetType().Name)
                    + " label=\"" + label + "\""
                    + enumOptions);
            }
        }

        private void BuildTimeline(RectTransform parent)
        {
            float panelY = DesignHeight - timelineHeight + 4f;
            float panelHeight = timelineHeight - 12f;
            Image panel = CreateRoundedPanel("Timeline", parent, 12f, panelY, TimelinePanelWidth, panelHeight, Panel, CornerLarge);
            panel.raycastTarget = true;
            AddPanelBorder(panel, CornerLarge);
            timelinePanel = panel.rectTransform;

            timelineView = panel.gameObject.AddComponent<EditorTimelineView>();
            timelineView.Initialize(panel.rectTransform, uiFont, this);

            // 预览工具栏上的车道按钮要读时间轴，而 BuildPreview 早于本方法执行，
            // 那一趟拿不到 timelineView 就直接返回了。这里补建。
            if (previewToolbar != null)
            {
                BuildLaneFilterButtons(previewToolbar);
            }
        }

        private void SetVisible(bool value)
        {
            visible = value;
            if (!visible)
            {
                HidePreviewHandoffImmediate();
                nonPreviewPointerCapture = false;
                ReleaseMediaTextures();
                RestorePreviewViewport();
                EditorPlaybackBridge.TryStopPlayback();
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                    && uiRoot != null
                    && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(uiRoot.transform))
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            }
            else
            {
                EnsureEventSystem();
                uiRoot.transform.SetAsLastSibling();
                projectDataDirty = true;
                RefreshProjectWorkspace(true);
                string ignored;
                // 打开工作台是启动一次会话，不是一次拖动。这里用 scrub
                // 那条入口会置上拖动标志，而空格处理器把它读成
                // 「指针在播放头上」，于是拒绝切换，直到一次真正的拖动
                // 恰好把它清掉。
                EditorPlaybackBridge.TryBeginPreviewSession(out ignored);
            }

            if (uiRoot != null)
            {
                uiRoot.SetActive(visible);
            }
        }


        private void SetBrowserFilter(BrowserFilter value)
        {
            if (browserFilter == value && filteredBrowserItems.Count > 0)
            {
                return;
            }

            browserFilter = value;
            ApplyBrowserFilter(true);
        }

        private void RefreshBrowserIfNeeded()
        {
            float now = Time.unscaledTime;
            if (browserDataDirty || now >= nextBrowserDataRefresh)
            {
                RefreshBrowserData(browserDataDirty);
                nextBrowserDataRefresh = now + BrowserDataRefreshInterval;
            }

            if (browserSelectionDirty || now >= nextBrowserSelectionRefresh)
            {
                browserSelectionDirty = false;
                nextBrowserSelectionRefresh = now + BrowserSelectionRefreshInterval;
                UpdateBrowserRows();
            }
        }

        private void RefreshBrowserData(bool force)
        {
            browserDataDirty = false;
            scnEditor editor = scnEditor.instance;
            scrDecorationManager manager = scrDecorationManager.instance;
            int fingerprint = ComputeBrowserFingerprint(editor, manager);
            if (!force && browserFingerprintValid && fingerprint == browserFingerprint)
            {
                return;
            }

            browserFingerprint = fingerprint;
            browserFingerprintValid = true;
            allBrowserItems.Clear();

            if (editor != null && editor.floors != null)
            {
                for (int i = 0; i < editor.floors.Count; i++)
                {
                    scrFloor floor = editor.floors[i];
                    if (floor == null)
                    {
                        continue;
                    }

                    allBrowserItems.Add(new BrowserItem
                    {
                        Kind = BrowserItemKind.Track,
                        FloorId = floor.seqID,
                        SourceOrder = i,
                        Floor = floor,
                        Label = "轨道  ·  第 " + floor.seqID + " 格"
                    });
                }
            }

            if (manager != null && manager.allDecorations != null)
            {
                for (int i = 0; i < manager.allDecorations.Count; i++)
                {
                    scrDecoration decoration = manager.allDecorations[i];
                    if (decoration == null || decoration.sourceLevelEvent == null)
                    {
                        continue;
                    }

                    int floorId = decoration.sourceLevelEvent.floor;
                    allBrowserItems.Add(new BrowserItem
                    {
                        Kind = BrowserItemKind.Decoration,
                        FloorId = floorId,
                        SourceOrder = i,
                        Decoration = decoration,
                        Label = "装饰物  ·  " + GetDecorationDisplayName(decoration) + "  ·  第 " + floorId + " 格"
                    });
                }
            }

            allBrowserItems.Sort(CompareBrowserItems);
            ApplyBrowserFilter(false);
        }

        private static int ComputeBrowserFingerprint(scnEditor editor, scrDecorationManager manager)
        {
            unchecked
            {
                int hash = 17;
                if (editor != null)
                {
                    hash = hash * 31 + editor.GetInstanceID();
                    if (editor.floors != null)
                    {
                        hash = hash * 31 + editor.floors.Count;
                        for (int i = 0; i < editor.floors.Count; i++)
                        {
                            scrFloor floor = editor.floors[i];
                            hash = hash * 31 + (floor == null ? 0 : floor.GetInstanceID());
                            hash = hash * 31 + (floor == null ? -1 : floor.seqID);
                        }
                    }
                }

                if (manager != null && manager.allDecorations != null)
                {
                    hash = hash * 31 + manager.allDecorations.Count;
                    for (int i = 0; i < manager.allDecorations.Count; i++)
                    {
                        scrDecoration decoration = manager.allDecorations[i];
                        hash = hash * 31 + (decoration == null ? 0 : decoration.GetInstanceID());
                        LevelEvent source = decoration == null ? null : decoration.sourceLevelEvent;
                        hash = hash * 31 + (source == null ? -1 : source.floor);
                    }
                }
                return hash;
            }
        }

        private static int CompareBrowserItems(BrowserItem left, BrowserItem right)
        {
            int result = left.FloorId.CompareTo(right.FloorId);
            if (result != 0)
            {
                return result;
            }

            result = left.Kind.CompareTo(right.Kind);
            return result != 0 ? result : left.SourceOrder.CompareTo(right.SourceOrder);
        }

        private void ApplyBrowserFilter(bool resetScroll)
        {
            filteredBrowserItems.Clear();
            int decorationCount = 0;
            int trackCount = 0;
            for (int i = 0; i < allBrowserItems.Count; i++)
            {
                BrowserItem item = allBrowserItems[i];
                if (item.Kind == BrowserItemKind.Track)
                {
                    trackCount++;
                }
                else
                {
                    decorationCount++;
                }

                if (browserFilter == BrowserFilter.All
                    || (browserFilter == BrowserFilter.Decorations && item.Kind == BrowserItemKind.Decoration)
                    || (browserFilter == BrowserFilter.Tracks && item.Kind == BrowserItemKind.Track))
                {
                    filteredBrowserItems.Add(item);
                }
            }

            UpdateFilterVisuals(decorationCount, trackCount);
            if (browserContent != null)
            {
                float viewportHeight = browserScroll == null || browserScroll.viewport == null
                    ? 1f
                    : browserScroll.viewport.rect.height;
                browserContent.sizeDelta = new Vector2(0f, Mathf.Max(viewportHeight, filteredBrowserItems.Count * BrowserRowHeight));
                if (resetScroll)
                {
                    browserContent.anchoredPosition = Vector2.zero;
                }
            }

            browserSelectionDirty = true;
            UpdateBrowserRows();
        }

        private void UpdateFilterVisuals(int decorationCount, int trackCount)
        {
            string[] labels =
            {
                "全部 " + (decorationCount + trackCount),
                "装饰物 " + decorationCount,
                "轨道 " + trackCount
            };
            for (int i = 0; i < browserFilterButtons.Count; i++)
            {
                SetButtonPalette(browserFilterButtons[i], i == (int)browserFilter);
                if (i < browserFilterLabels.Count)
                {
                    browserFilterLabels[i].text = labels[i];
                }
            }
        }

        private void UpdateBrowserRows()
        {
            if (browserContent == null || browserRows.Count == 0)
            {
                return;
            }

            float offset = Mathf.Max(0f, browserContent.anchoredPosition.y);
            int maximumFirst = Mathf.Max(0, filteredBrowserItems.Count - browserRows.Count);
            int first = Mathf.Clamp(Mathf.FloorToInt(offset / BrowserRowHeight), 0, maximumFirst);
            for (int i = 0; i < browserRows.Count; i++)
            {
                BrowserRow row = browserRows[i];
                int itemIndex = first + i;
                if (itemIndex >= filteredBrowserItems.Count)
                {
                    row.BoundIndex = -1;
                    row.GameObject.SetActive(false);
                    continue;
                }

                BrowserItem item = filteredBrowserItems[itemIndex];
                row.BoundIndex = itemIndex;
                row.Rect.anchoredPosition = new Vector2(0f, -itemIndex * BrowserRowHeight);
                row.Label.text = item.Label;
                SetButtonPalette(row.Button, IsBrowserItemSelected(item));
                row.GameObject.SetActive(true);
            }
        }

        private static bool IsBrowserItemSelected(BrowserItem item)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return false;
            }

            if (item.Kind == BrowserItemKind.Track)
            {
                return item.Floor != null
                    && editor.selectedFloors != null
                    && editor.selectedFloors.Contains(item.Floor);
            }

            LevelEvent source = item.Decoration == null ? null : item.Decoration.sourceLevelEvent;
            return source != null
                && editor.selectedDecorations != null
                && editor.selectedDecorations.Contains(source);
        }

        private void ActivateBrowserRow(BrowserRow row)
        {
            if (row == null || row.BoundIndex < 0 || row.BoundIndex >= filteredBrowserItems.Count)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            BrowserItem item = filteredBrowserItems[row.BoundIndex];
            if (item.Kind == BrowserItemKind.Track)
            {
                if (item.Floor != null)
                {
                    editor.SelectFloor(item.Floor, cameraJump: false);
                }
            }
            else if (item.Decoration != null && item.Decoration.sourceLevelEvent != null)
            {
                editor.SelectDecoration(
                    item.Decoration.sourceLevelEvent,
                    jumpToDecoration: false,
                    showPanel: false,
                    ignoreDeselection: false,
                    ignoreAdjustRect: true
                );
            }

            browserSelectionDirty = true;
            UpdateBrowserRows();
        }

        private static string GetDecorationDisplayName(scrDecoration decoration)
        {
            LevelEvent source = decoration.sourceLevelEvent;
            string value;
            switch (source.eventType)
            {
                case LevelEventType.AddDecoration:
                    if (source.TryGet<string>("decorationImage", out value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return NormalizeBrowserLabel(value);
                    }
                    return "图片";
                case LevelEventType.AddText:
                    if (source.TryGet<string>("decText", out value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return NormalizeBrowserLabel(value);
                    }
                    return "文本";
                case LevelEventType.AddParticle:
                    if (source.TryGet<string>("decorationImage", out value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return NormalizeBrowserLabel(value);
                    }
                    return "粒子";
                case LevelEventType.AddObject:
                    return "对象";
                default:
                    if (source.TryGet<string>("tag", out value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return NormalizeBrowserLabel(value);
                    }
                    return source.eventType.ToString();
            }
        }

        private static string NormalizeBrowserLabel(string value)
        {
            return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        internal void SetSceneSelectionStatus(string message)
        {
            browserSelectionDirty = true;

            // 这条消息以前被丢在地上没人管——只置了脏标志，所以
            // 场景交互报上来的任何东西都从来没有显示过。现在它驱动
            // 页眉读数，滚轮调整的实时值也显示在那里。
            if (statusLabel != null)
            {
                statusLabel.text = message ?? string.Empty;
            }
        }

        /// <summary>
        /// 拖动装饰物时滚轮调整的临时读数。数值每滚一格
        /// 就变，所以这是用户唯一能看见滚轮
        /// 实际在做什么的地方。
        /// </summary>
        internal void SetAdjustmentStatus(string message)
        {
            if (statusLabel != null)
            {
                statusLabel.text = message ?? string.Empty;
            }
        }

        private TextMeshProUGUI statusLabel;

        internal bool IsTextInputFocused()
        {
            if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null)
            {
                return false;
            }

            TMP_InputField input = EventSystem.current.currentSelectedGameObject.GetComponentInParent<TMP_InputField>();
            return input != null && input.isFocused;
        }

        internal void TogglePlaybackFromShortcut()
        {
            string ignored;
            EditorPlaybackBridge.TryTogglePlayback(out ignored);
            RefreshPlaybackState();
        }

        internal bool TryCapturePreviewHandoff(Action onCaptured)
        {
            if (!visible || !built || previewViewport == null || previewHandoffImage == null)
            {
                return false;
            }

            previewHandoffGeneration++;
            int generation = previewHandoffGeneration;
            if (previewHandoffCoroutine != null)
            {
                StopCoroutine(previewHandoffCoroutine);
            }
            if (previewHandoffReleaseCoroutine != null)
            {
                StopCoroutine(previewHandoffReleaseCoroutine);
                previewHandoffReleaseCoroutine = null;
            }
            previewHandoffCoroutine = StartCoroutine(CapturePreviewHandoff(generation, onCaptured));
            return true;
        }

        internal void ReleasePreviewHandoffAfterNativeFrame()
        {
            if (previewHandoffImage == null || !previewHandoffImage.gameObject.activeSelf)
            {
                return;
            }
            if (previewHandoffReleaseCoroutine != null)
            {
                StopCoroutine(previewHandoffReleaseCoroutine);
            }
            previewHandoffReleaseCoroutine = StartCoroutine(ReleasePreviewHandoffAfterFrame());
        }

        internal void HidePreviewHandoffImmediate()
        {
            previewHandoffGeneration++;
            if (previewHandoffCoroutine != null)
            {
                StopCoroutine(previewHandoffCoroutine);
                previewHandoffCoroutine = null;
            }
            if (previewHandoffReleaseCoroutine != null)
            {
                StopCoroutine(previewHandoffReleaseCoroutine);
                previewHandoffReleaseCoroutine = null;
            }
            if (previewHandoffImage != null)
            {
                previewHandoffImage.texture = null;
                previewHandoffImage.gameObject.SetActive(false);
            }
            if (previewHandoffTexture != null)
            {
                Destroy(previewHandoffTexture);
                previewHandoffTexture = null;
            }
        }

        private IEnumerator CapturePreviewHandoff(int generation, Action onCaptured)
        {
            yield return new WaitForEndOfFrame();
            previewHandoffCoroutine = null;
            if (generation != previewHandoffGeneration || previewViewport == null
                || previewHandoffImage == null || !visible)
            {
                yield break;
            }

            bool captured = false;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                previewViewport.GetWorldCorners(previewViewportCorners);
                int left = Mathf.Clamp(Mathf.RoundToInt(previewViewportCorners[0].x), 0, Screen.width - 1);
                int bottom = Mathf.Clamp(Mathf.RoundToInt(previewViewportCorners[0].y), 0, Screen.height - 1);
                int right = Mathf.Clamp(Mathf.RoundToInt(previewViewportCorners[2].x), left + 1, Screen.width);
                int top = Mathf.Clamp(Mathf.RoundToInt(previewViewportCorners[2].y), bottom + 1, Screen.height);
                int width = right - left;
                int height = top - bottom;
                Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                RenderTexture.active = null;
                texture.ReadPixels(new Rect(left, bottom, width, height), 0, 0, false);
                texture.Apply(false, false);

                if (previewHandoffTexture != null)
                {
                    Destroy(previewHandoffTexture);
                }
                previewHandoffTexture = texture;
                previewHandoffImage.texture = texture;
                previewHandoffImage.transform.SetAsLastSibling();
                previewHandoffImage.gameObject.SetActive(true);
                captured = true;
                Main.Logger.Log("[PreviewHandoff] Captured static frame " + width + "x" + height + ".");
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PreviewHandoff] Failed to capture static frame: " + exception);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }

            if (generation == previewHandoffGeneration && onCaptured != null)
            {
                onCaptured();
            }
            if (!captured)
            {
                HidePreviewHandoffImmediate();
            }
        }

        private IEnumerator ReleasePreviewHandoffAfterFrame()
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            previewHandoffReleaseCoroutine = null;
            if (previewHandoffImage != null)
            {
                previewHandoffImage.texture = null;
                previewHandoffImage.gameObject.SetActive(false);
            }
            if (previewHandoffTexture != null)
            {
                Destroy(previewHandoffTexture);
                previewHandoffTexture = null;
            }
            Main.Logger.Log("[PreviewHandoff] Native frame is visible.");
        }

        private static void RefreshPlaybackState()
        {
            EditorPlaybackSnapshot snapshot = EditorPlaybackBridge.ReadSnapshot();
            if (snapshot.Available && snapshot.State != EditorPlaybackState.Editing)
            {
                Cursor.visible = true;
            }
        }

        private void ApplyPreviewViewport()
        {
            if (previewViewport == null || Screen.width <= 0 || Screen.height <= 0)
            {
                return;
            }

            Camera camera = EditorBridge.GetPreviewOutputCamera();
            if (camera == null)
            {
                LogViewportState("no-output-camera", null, 0f, 0f, 0f, 0f);
                return;
            }

            if (previewCamera != camera)
            {
                RestorePreviewViewport();
                previewCamera = camera;
                previewCameraOriginalRect = camera.rect;
                previewCameraModified = true;
            }

            previewViewport.GetWorldCorners(previewViewportCorners);
            float left = Mathf.Clamp(previewViewportCorners[0].x, 0f, Screen.width);
            float bottom = Mathf.Clamp(previewViewportCorners[0].y, 0f, Screen.height);
            float right = Mathf.Clamp(previewViewportCorners[2].x, 0f, Screen.width);
            float top = Mathf.Clamp(previewViewportCorners[2].y, 0f, Screen.height);
            float width = right - left;
            float height = top - bottom;
            if (width < 2f || height < 2f)
            {
                LogViewportState("stage-too-small", camera, left, bottom, width, height);
                return;
            }

            camera.rect = new Rect(
                left / Screen.width,
                bottom / Screen.height,
                width / Screen.width,
                height / Screen.height
            );

            LogViewportState("applied", camera, left, bottom, width, height);
        }

        private int viewportLogCount;
        private string lastViewportLogKey;

        /// <summary>
        /// 工作台打开时，追踪预览视口解析出来的结果。预览全黑
        /// 但工作台 UI 还活着，意味着游戏相机要么 rect 退化了、
        /// 要么根本没在渲染，而计算这个值的只有这一处。
        /// 完全相同的状态会被抑制，所以日志
        /// 显示的是状态切换，而不是每帧一行。
        /// </summary>
        private void LogViewportState(
            string reason,
            Camera camera,
            float left,
            float bottom,
            float width,
            float height)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            string key = reason
                + "|" + Mathf.RoundToInt(width) + "x" + Mathf.RoundToInt(height)
                + "|" + (camera == null ? "-" : camera.name)
                + "|" + (camera == null ? "-" : camera.enabled.ToString())
                + "|" + (camera == null || camera.targetTexture == null ? "-" : "rt");
            if (key == lastViewportLogKey || viewportLogCount >= 24)
            {
                return;
            }

            lastViewportLogKey = key;
            viewportLogCount++;
            Main.Logger.Log("[PreviewViewport] " + reason
                + " screen=" + Screen.width + "x" + Screen.height
                + " stage=" + width.ToString("0.0") + "x" + height.ToString("0.0")
                + " left=" + left.ToString("0.0") + " bottom=" + bottom.ToString("0.0")
                + " camera=" + (camera == null ? "null" : camera.name)
                + " enabled=" + (camera == null ? "-" : camera.enabled.ToString())
                + " rect=" + (camera == null ? "-" : camera.rect.ToString())
                + " targetTexture=" + (camera == null || camera.targetTexture == null ? "none" : camera.targetTexture.name)
                + " handoff=" + (previewHandoffImage == null || !previewHandoffImage.gameObject.activeSelf ? "off" : "ON")
                + ".");
        }

        private PreviewLayout CalculatePreviewLayout()
        {
            float screenAspect = DesignWidth / DesignHeight;
            if (Screen.width > 0 && Screen.height > 0)
            {
                screenAspect = Mathf.Max(0.1f, Screen.width / (float)Screen.height);
            }

            float maxStageWidth = DesignWidth
                - OuterMargin * 2f
                - PanelGap * 2f
                - MinimumLeftPanelWidth
                - MinimumRightPanelWidth;
            float stageHeight = Mathf.Min(PreviewStageAvailableHeight, maxStageWidth / screenAspect);
            float stageWidth = stageHeight * screenAspect;
            float remainingWidth = DesignWidth - OuterMargin * 2f - PanelGap * 2f - stageWidth;
            float extraPanelWidth = Mathf.Max(0f, remainingWidth - MinimumLeftPanelWidth - MinimumRightPanelWidth);
            float leftPanelWidth = MinimumLeftPanelWidth + extraPanelWidth * LeftPanelExtraShare;
            float rightPanelWidth = MinimumRightPanelWidth + extraPanelWidth * (1f - LeftPanelExtraShare);
            float stageX = OuterMargin + leftPanelWidth + PanelGap;

            return new PreviewLayout
            {
                LeftPanelWidth = leftPanelWidth,
                RightPanelWidth = rightPanelWidth,
                StageX = stageX,
                StageY = PreviewPanelY + PreviewStageInsetY + (PreviewStageAvailableHeight - stageHeight) * 0.5f,
                StageWidth = stageWidth,
                StageHeight = stageHeight,
                InspectorX = stageX + stageWidth + PanelGap
            };
        }

        private void ApplyWorkspaceLayout()
        {
            if (!built || designRoot == null)
            {
                return;
            }

            previewLayout = CalculatePreviewLayout();
            float previewHeight = PreviewPanelHeight;

            SetTopLeftRect(browserPanel, OuterMargin, PreviewPanelY, previewLayout.LeftPanelWidth, previewHeight);
            RelayoutProjectWorkspace(previewHeight);

            SetTopLeftRect(previewPanel, previewLayout.StageX, PreviewPanelY, previewLayout.StageWidth, previewHeight);
            SetTopLeftRect(previewToolbar, 0f, 0f, previewLayout.StageWidth, 52f);
            SetTopLeftRect(previewHeading, 18f, 11f, 160f, 30f);
            SetTopLeftRect(
                previewViewport,
                0f,
                previewLayout.StageY - PreviewPanelY,
                previewLayout.StageWidth,
                previewLayout.StageHeight
            );

            SetTopLeftRect(
                inspectorPanel,
                previewLayout.InspectorX,
                PreviewPanelY,
                previewLayout.RightPanelWidth,
                previewHeight
            );
            SetTopLeftRect(inspectorHeading, 18f, 14f, previewLayout.RightPanelWidth - 36f, 30f);
            if (propertyPanel != null)
            {
                propertyPanel.Relayout(previewLayout.RightPanelWidth, previewHeight);
            }

            SetTopLeftRect(matteTop, 0f, 0f, DesignWidth, previewLayout.StageY);
            SetTopLeftRect(
                matteBottom,
                0f,
                previewLayout.StageY + previewLayout.StageHeight,
                DesignWidth,
                DesignHeight - previewLayout.StageY - previewLayout.StageHeight
            );
            SetTopLeftRect(matteLeft, 0f, previewLayout.StageY, previewLayout.StageX, previewLayout.StageHeight);
            SetTopLeftRect(
                matteRight,
                previewLayout.StageX + previewLayout.StageWidth,
                previewLayout.StageY,
                DesignWidth - previewLayout.StageX - previewLayout.StageWidth,
                previewLayout.StageHeight
            );

            SetTopLeftRect(
                timelinePanel,
                12f,
                DesignHeight - timelineHeight + 4f,
                TimelinePanelWidth,
                timelineHeight - 12f
            );
            if (timelineView != null)
            {
                timelineView.Relayout();
            }

        }

        private void RelayoutProjectWorkspace(float previewHeight)
        {
            if (browserPanel == null || projectTreePanel == null || mediaPanel == null)
            {
                return;
            }

            float panelWidth = previewLayout.LeftPanelWidth;
            float innerWidth = panelWidth - 24f;
            float treeWidth = Mathf.Clamp(innerWidth * ProjectTreeWidthRatio, MinimumProjectTreeWidth, MaximumProjectTreeWidth);
            float mediaWidth = innerWidth - treeWidth - 8f;
            float innerHeight = Mathf.Max(64f, previewHeight - 20f);
            float listY = 36f;
            // 媒体列在列表上方有一条搜索行；项目树没有，
            // 所以两者不能共用偏移。
            float mediaListY = listY + MediaSearchRowHeight;
            float listHeight = Mathf.Max(32f, innerHeight - listY - 10f);
            float mediaListHeight = Mathf.Max(32f, innerHeight - mediaListY - 10f);
            SetTopLeftRect(projectTreePanel, 12f, 10f, treeWidth, innerHeight);
            SetTopLeftRect(mediaPanel, 12f + treeWidth + 8f, 10f, mediaWidth, innerHeight);
            SetTopLeftRect(projectTitle.rectTransform, 12f, 8f, treeWidth - 24f, 28f);
            SetTopLeftRect(mediaTitle.rectTransform, 12f, 8f, mediaWidth - 24f, 28f);
            SetTopLeftRect(projectTreeScroll.GetComponent<RectTransform>(), 8f, listY, treeWidth - 16f, listHeight);
            SetTopLeftRect(mediaScroll.GetComponent<RectTransform>(), 8f, mediaListY, mediaWidth - 16f, mediaListHeight);
            LayoutMediaSearchControls(mediaWidth);
            int requiredTreeRows = Mathf.CeilToInt(listHeight / ProjectTreeRowHeight) + 2;
            EnsureProjectTreeRows(requiredTreeRows);
            int requiredMediaCards = Mathf.CeilToInt(mediaListHeight / MediaCardHeight) + 2;
            EnsureMediaCards(requiredMediaCards);
            projectTreeContent.sizeDelta = new Vector2(0f, Mathf.Max(listHeight, projectTreeContent.sizeDelta.y));
            mediaContent.sizeDelta = new Vector2(0f, Mathf.Max(mediaListHeight, mediaContent.sizeDelta.y));
            RebuildProjectTree();
            RebuildVisibleMedia(false);
        }

        private void RebuildRuntimeUiForResolution()
        {
            bool wasVisible = visible;
            HidePreviewHandoffImmediate();
            EditorCommandHistory.Clear();
            RestorePreviewViewport();

            if (uiRoot != null)
            {
                uiRoot.SetActive(false);
                Destroy(uiRoot);
            }

            ClearRuntimeUiReferences();
            built = false;

            try
            {
                BuildRuntimeUi();
                if (uiRoot != null)
                {
                    uiRoot.SetActive(wasVisible);
                    if (wasVisible)
                    {
                        uiRoot.transform.SetAsLastSibling();
                    }
                }
                Main.Logger.Log(
                    "Rebuilt editor layout for "
                    + layoutScreenWidth
                    + "x"
                    + layoutScreenHeight
                    + " (preview "
                    + previewLayout.StageWidth.ToString("0.0")
                    + "x"
                    + previewLayout.StageHeight.ToString("0.0")
                    + ")."
                );
            }
            catch (Exception exception)
            {
                buildFailed = true;
                Main.Logger.Log("Failed to rebuild the runtime uGUI after a resolution change: " + exception);
            }
        }

        private void ClearRuntimeUiReferences()
        {
            ReleaseMediaTextures();
            browserFilterButtons.Clear();
            browserFilterLabels.Clear();
            browserRows.Clear();
            allBrowserItems.Clear();
            filteredBrowserItems.Clear();
            uiRoot = null;
            canvas = null;
            designRoot = null;
            browserPanel = null;
            projectTreePanel = null;
            projectTreeViewport = null;
            projectTreeContent = null;
            mediaPanel = null;
            mediaViewport = null;
            mediaContent = null;
            projectTitle = null;
            mediaTitle = null;
            projectTreeScroll = null;
            mediaScroll = null;
            projectTreeRows.Clear();
            mediaCards.Clear();
            visibleProjectMedia.Clear();
            projectSession = null;
            selectedProjectDirectory = ".";
            selectedMediaPath = null;
            projectDataDirty = true;
            previewPanel = null;
            previewToolbar = null;
            laneFilterButtonRects.Clear();
            previewHeading = null;
            previewViewport = null;
            previewHandoffImage = null;
            inspectorPanel = null;
            inspectorHeading = null;
            propertyPanel = null;
            timelinePanel = null;
            matteTop = null;
            matteBottom = null;
            matteLeft = null;
            matteRight = null;
            timelineView = null;
            browserScroll = null;
            browserContent = null;
            browserFingerprintValid = false;
            browserDataDirty = true;
            browserSelectionDirty = true;
        }

        private void RestorePreviewViewport()
        {
            if (previewCameraModified && previewCamera != null)
            {
                previewCamera.rect = previewCameraOriginalRect;
            }

            previewCameraModified = false;
            previewCamera = null;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null && EventSystem.current.gameObject.activeInHierarchy)
            {
                return;
            }

            EventSystem editorEventSystem = FindEditorEventSystem();
            if (editorEventSystem != null && editorEventSystem.gameObject.activeInHierarchy)
            {
                EventSystem.current = editorEventSystem;
                return;
            }

            EventSystem[] systems = Resources.FindObjectsOfTypeAll<EventSystem>();
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null && systems[i].gameObject.activeInHierarchy)
                {
                    EventSystem.current = systems[i];
                    return;
                }
            }

            if (ownedEventSystem != null)
            {
                EventSystem.current = ownedEventSystem.GetComponent<EventSystem>();
                return;
            }

            ownedEventSystem = new GameObject(
                "ADOFAIEditorStudio.EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule)
            );
            ownedEventSystem.transform.SetParent(transform, false);
            EventSystem.current = ownedEventSystem.GetComponent<EventSystem>();
            Main.Logger.Log("No active EventSystem was found; created a legacy-input fallback.");
        }

        private TMP_FontAsset ResolveFont()
        {
            // 全程用游戏自己的字体。这里试过系统 UI 字体（走
            // TMP_FontAsset.CreateFontAsset 的微软雅黑），确实能解析出来，但它让
            // 工作台读起来像一扇压在游戏上的外来窗口，而不是游戏
            // 的一部分，所以下面的字体链仍然留在游戏自己的字体里。密集
            // 时间轴文字的可读性改用阴影解决——见 ApplyTextShadow。
            TMP_FontAsset editorFont = FindEditorFont();
            if (editorFont != null)
            {
                Main.Logger.Log("Using editor TMP font: " + editorFont.name);
                return editorFont;
            }

            try
            {
                List<TMP_FontAsset> fallbackFonts = TMP_Settings.fallbackFontAssets;
                TMP_FontAsset fallback = FindCjkFont(fallbackFonts);
                if (fallback != null)
                {
                    Main.Logger.Log("Using TMP fallback CJK font: " + fallback.name);
                    return fallback;
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Log("TMP fallback font lookup failed: " + exception.Message);
            }

            TMP_FontAsset[] loadedFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            TMP_FontAsset loadedFont = FindCjkFont(loadedFonts);
            if (loadedFont != null)
            {
                Main.Logger.Log("Using loaded CJK TMP font: " + loadedFont.name);
                return loadedFont;
            }

            if (TMP_Settings.defaultFontAsset != null)
            {
                Main.Logger.Log("Using TMP default font; Chinese glyph coverage may be incomplete.");
                return TMP_Settings.defaultFontAsset;
            }

            return loadedFonts.Length > 0 ? loadedFonts[0] : null;
        }

        /// <summary>
        /// 用一套已安装的 Windows 字体构建 TMP 资源；候选字体一个都
        /// 解析不出来时返回 null。
        ///
        /// 这里走的是 TMP 自己的系统字体路径，而不是老的 UnityEngine.Font 那条。
        /// 在这个 Unity 版本上，Font.CreateDynamicFontFromOSFont 对每个字体家族都返回 null——
        /// 包括确实装了的那些——所以面板以前才会一路掉到
        /// TMP_Settings.defaultFontAsset，一直停在游戏的 godoMaum 上。
        /// </summary>
        private static EventSystem FindEditorEventSystem()
        {
            object editor = EditorBridge.GetEditorInstance();
            if (editor == null)
            {
                return null;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo field = editor.GetType().GetField("eventSystem", flags);
                return field == null ? null : field.GetValue(editor) as EventSystem;
            }
            catch
            {
                return null;
            }
        }

        private static TMP_FontAsset FindEditorFont()
        {
            object editor = EditorBridge.GetEditorInstance();
            if (editor == null)
            {
                return null;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo[] fields = editor.GetType().GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!typeof(TMP_Text).IsAssignableFrom(fields[i].FieldType))
                    {
                        continue;
                    }

                    TMP_Text text = fields[i].GetValue(editor) as TMP_Text;
                    if (text != null && SupportsUiGlyphs(text.font))
                    {
                        return text.font;
                    }
                }
            }
            catch
            {
                // 初始化到一半的编辑器可能在还没赋值之前就把文本字段暴露出来。
            }

            return null;
        }

        private static TMP_FontAsset FindCjkFont(IList<TMP_FontAsset> fonts)
        {
            if (fonts == null)
            {
                return null;
            }

            TMP_FontAsset firstCompatible = null;
            for (int i = 0; i < fonts.Count; i++)
            {
                TMP_FontAsset font = fonts[i];
                if (!SupportsUiGlyphs(font))
                {
                    continue;
                }

                string name = font.name == null ? string.Empty : font.name.ToLowerInvariant();
                if (name.Contains("sourcehan") || name.Contains("chinesesimplified") || name.Contains("sourcehansanssc"))
                {
                    return font;
                }

                if (firstCompatible == null)
                {
                    firstCompatible = font;
                }
            }

            return firstCompatible;
        }

        private static bool SupportsUiGlyphs(TMP_FontAsset font)
        {
            if (font == null)
            {
                return false;
            }

            try
            {
                return font.HasCharacter('编', true, false)
                    && font.HasCharacter('舞', true, false)
                    && font.HasCharacter('器', true, false);
            }
            catch
            {
                return false;
            }
        }

        // ---- 圆角贴图，运行时生成 ----
        //
        // Unity 的 Image 只会画一个普通矩形，所以用一片 Image 拼出来的「面板」就是四个硬角，
        // 读起来像调试用的表面而不是卡片。解法是让贴图的 alpha
        // 携带圆角：一张小方形纹理，把圆角形状画进去，
        // 再当九宫格贴图用，这样圆角在任何面板尺寸下都保持弧度。
        //
        // 选择生成而不是当图片发出去，是为了没有资源需要同步，也为了让
        // 半径可以做成参数。遮罩是像素到内缩矩形最近点的距离：
        // 半径以内是圆，半径以外整个像素就是实心，
        // 这正是直线边精确、没有锯齿的原因。

        private static readonly Dictionary<string, Sprite> roundedSpriteCache =
            new Dictionary<string, Sprite>();

        /// <summary>
        /// 一张带圆角的白色贴图，用作九宫格 Image。
        /// <paramref name="size"/> 是纹理边长；可见的圆角半径是
        /// 纹理像素里的 <paramref name="radius"/>，并且在任何绘制尺寸下都保持这个形状，
        /// 因为贴图是按该半径作边框切九宫的。
        /// </summary>
        private static Sprite GetRoundedSprite(int size, float radius, bool frame, float frameWidth)
        {
            string key = size + "/" + radius + "/" + (frame ? "f" + frameWidth : "s");
            Sprite cached;
            if (roundedSpriteCache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color32[] pixels = new Color32[size * size];
            float inner = frame ? frameWidth : 0f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    // 像素到圆角矩形边界的距离。把采样点夹取进
                    // 「核心」矩形之后，剩下的偏移就是到最近
                    // 圆角中心的距离，再拿它的长度去和半径比。
                    float cx = Mathf.Clamp(px, radius, size - radius);
                    float cy = Mathf.Clamp(py, radius, size - radius);
                    float dx = px - cx;
                    float dy = py - cy;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    float alpha = Mathf.Clamp01(radius - distance + 0.5f);

                    if (frame && alpha > 0f)
                    {
                        // 边框是外缘与同一形状内缩一份之间的那一圈，
                        // 所以同一套距离判定要对着内缩后的半径再跑一遍。
                        float insetRadius = Mathf.Max(0.001f, radius - frameWidth);
                        float icx = Mathf.Clamp(px, insetRadius + inner, size - insetRadius - inner);
                        float icy = Mathf.Clamp(py, insetRadius + inner, size - insetRadius - inner);
                        float idx = px - icx;
                        float idy = py - icy;
                        float innerDistance = Mathf.Sqrt(idx * idx + idy * idy);
                        float innerAlpha = Mathf.Clamp01(insetRadius - innerDistance + 0.5f);
                        alpha = Mathf.Clamp01(alpha - innerAlpha);
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            float border = radius + 1f;
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            roundedSpriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// 一块面板：一个圆角矩形，会被拉伸到给它的任何尺寸。贴图
        /// 是九宫格切好的，所以圆角半径保持不变，而不是
        /// 跟着面板一起被拉伸。
        /// </summary>
        /// <summary>
        /// 在面板周围加一圈白色描边，以及它背后一层微弱辉光。
        ///
        /// 不是 Outline 组件。Outline 画的是*整张图形的一份偏移副本*，所以用在
        /// 填充的圆角矩形上，产物是一份平移了几个像素的重复图形，而不是一圈——
        /// 叠三层来做辉光，只是把三块面板叠起来。描边真正需要的
        /// 是垫在这一块背后的一圈更大的形状，这就是这里搭的东西：一个
        /// 大几个像素的白色圆角矩形，再加两个更大也更淡的，全部插在
        /// 面板背后，只露出边缘那一圈。
        ///
        /// 这些层是放在背后的同级节点，而不是子节点，因为子节点画在
        /// 父节点之上，会盖住它本该框住的那块面板。
        /// </summary>
        private void AddPanelBorder(Image panel, float radius)
        {
            if (panel == null || panel.rectTransform.parent == null)
            {
                return;
            }
            // 先阴影、后描边：后建的画在上面，所以描边压在阴影之上，
            // 阴影只在面板边缘之外露出来那一圈。
            AddPanelShadow(panel, radius);
            AddBorderLayer(panel, radius, Border, PanelBorderWidth);
        }

        /// <summary>
        /// 垫在面板背后、往右下推开的黑色圆角矩形，也就是投影。
        ///
        /// 这一层是面板"浮在游戏画面上"的全部依据。以前这里做的是两圈
        /// 白色辉光加一条纯白硬边，那样在实时画面上太响：一圈亮边会把
        /// 视线从它框住的内容上拽走。投影则相反——它只是在面板
        /// 和背景之间制造一段暗区，人被边界说服，却注意不到这条边界。
        ///
        /// 用黑色而不是"面板的深色版本"：面板下面可能是任何颜色，
        /// 半透明的深蓝叠上去只会变成一块脏色；黑色叠在任何颜色上
        /// 都只是把那个颜色压暗，这才读得像影子。
        ///
        /// **只能用在有填充的面板上。** 这一层是实心的，靠面板本身
        /// 盖住它的中间；给透明面板加投影，它就会透出来变成一块
        /// 黑幕（预览面板就是这么被压暗过一次）。
        /// </summary>
        private void AddPanelShadow(Image panel, float radius)
        {
            RectTransform target = panel.rectTransform;
            GameObject layerObject = new GameObject("Shadow", typeof(RectTransform), typeof(Image));
            layerObject.transform.SetParent(target.parent, false);

            RectTransform rect = layerObject.GetComponent<RectTransform>();
            rect.anchorMin = target.anchorMin;
            rect.anchorMax = target.anchorMax;
            rect.pivot = target.pivot;
            rect.anchoredPosition = target.anchoredPosition
                - new Vector2(PanelShadowSpread, -PanelShadowSpread)
                + new Vector2(PanelShadowOffset.x, -PanelShadowOffset.y);
            rect.sizeDelta = target.sizeDelta
                + new Vector2(PanelShadowSpread * 2f, PanelShadowSpread * 2f);

            BorderFollower follower = layerObject.AddComponent<BorderFollower>();
            follower.Target = target;
            follower.Margin = PanelShadowSpread;
            follower.Offset = PanelShadowOffset;

            Image image = layerObject.GetComponent<Image>();
            image.color = PanelShadow;
            image.raycastTarget = false;
            ApplyRounded(image, radius + PanelShadowSpread, false, 0f);

            // 插到和面板同一个位置，于是它恰好画在面板背后：
            // 比面板先画，就被面板盖住中间、只露出边缘那一圈。
            layerObject.transform.SetSiblingIndex(target.GetSiblingIndex());
        }

        /// <summary>
        /// 同样的描边，但用圆环搭，而不是实心块。
        ///
        /// <see cref="AddPanelBorder"/> 画实心圆角矩形，靠面板挡住
        /// 它们的中间——除预览之外每个面板都能这么做。预览那个必须保持
        /// 透明好让实时游戏透出来，而在那里同样的层读起来就是一块填满
        /// 整个舞台的白色实心板。这些层只携带自己的边缘，所以它们
        /// 框住透明面板的方式和框住不透明面板是一样的。
        ///
        /// **这里没有投影，是有意的，也不要补上。** 投影的技术前提是
        /// 面板不透明：它是一整块实心圆角矩形，靠面板盖住中间、
        /// 只露出边缘那一圈。透明面板盖不住任何东西，同一层会从面板里
        /// 透出来，变成一块压在整个预览上的半透明黑幕——实时画面
        /// 整体暗掉四成。透明面板没有投影可画。
        /// </summary>
        private void AddPanelOutline(Image panel, float radius)
        {
            if (panel == null || panel.rectTransform.parent == null)
            {
                return;
            }
            AddOutlineLayer(panel, radius, Border, PanelBorderWidth);
        }

        private void AddOutlineLayer(Image panel, float radius, Color colour, float band)
        {
            RectTransform target = panel.rectTransform;
            GameObject layerObject = new GameObject("Outline", typeof(RectTransform), typeof(Image));
            layerObject.transform.SetParent(target.parent, false);

            RectTransform rect = layerObject.GetComponent<RectTransform>();
            rect.anchorMin = target.anchorMin;
            rect.anchorMax = target.anchorMax;
            rect.pivot = target.pivot;
            // 一条这么宽的环带，贴着比面板大这么多的盒子外缘，
            // 正好落在面板自己的边缘上——几何与 AddBorderLayer 相同，
            // 只是填充用的是 frame，中间留空。
            rect.anchoredPosition = target.anchoredPosition + new Vector2(-band, band);
            rect.sizeDelta = target.sizeDelta + new Vector2(band * 2f, band * 2f);

            BorderFollower follower = layerObject.AddComponent<BorderFollower>();
            follower.Target = target;
            follower.Margin = band;

            Image image = layerObject.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            ApplyRounded(image, radius + band, true, band);

            layerObject.transform.SetSiblingIndex(target.GetSiblingIndex());
        }

        /// <summary>描边的一层：一个比面板大这么多像素的圆角矩形。</summary>
        private void AddBorderLayer(Image panel, float radius, Color colour, float margin)
        {
            RectTransform target = panel.rectTransform;
            GameObject layerObject = new GameObject("Border", typeof(RectTransform), typeof(Image));
            layerObject.transform.SetParent(target.parent, false);

            RectTransform rect = layerObject.GetComponent<RectTransform>();
            rect.anchorMin = target.anchorMin;
            rect.anchorMax = target.anchorMax;
            rect.pivot = target.pivot;
            // 只增大 sizeDelta 只会把一个左上轴心的 rect 往右下方
            // 扩——轴心钉在原地——于是多出来的边距出现在两条边上，
            // 另外两条被塞进面板里面，读起来就是一条被裁掉的
            // 描边。把位置按同样的边距回推，就能让放大后的 rect 以面板为中心，
            // 四边的描边也就均匀了。
            //
            // 在左上轴心的坐标系里，anchoredPosition 是 (右, 下)：往左是
            // 更小的 x，往上是更大的 y。
            rect.anchoredPosition = target.anchoredPosition + new Vector2(-margin, margin);
            rect.sizeDelta = target.sizeDelta + new Vector2(margin * 2f, margin * 2f);
            // 而且要一直保持这样：稍后被改过尺寸的面板（拖动把手下的
            // 时间轴、窗口缩放时的任何面板）否则会把它的圆环留在旧
            // 尺寸上，表现成面板背后一块白板或者一条被裁掉的边。
            BorderFollower follower = layerObject.AddComponent<BorderFollower>();
            follower.Target = target;
            follower.Margin = margin;

            Image image = layerObject.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            // 圆角跟着形状一起变大，这样描边绕过弯的时候宽度保持均匀，
            // 而不是在角上被掐细。
            ApplyRounded(image, radius + margin, false, 0f);

            // 直接插在面板*之前*。Unity 按顺序画同级节点，所以索引
            // 等于面板自己的话，这一层会排在它之后——盖在面板上而不是
            // 垫在下面。插在面板自己的索引上会把面板往下推，这正是
            // 想要的顺序：最宽的辉光、更紧的辉光、描边，最后面板在最上。
            layerObject.transform.SetSiblingIndex(target.GetSiblingIndex());
        }

        /// <summary>
        /// 一条横跨自己面板、并且持续横跨的标题分隔线。
        ///
        /// 和这里其他所有元素一样按固定宽度搭，直到面板改变尺寸之前都没问题——
        /// 时间轴会在拖动把手下拉大，而一条在构建时就定死宽度的线
        /// 会比新宽度短一截、或者探出去。把这条线锚定
        /// 到两条水平边，布局就能拉伸它，于是它跟着面板走，
        /// 不需要任何逐次缩放的处理。
        /// </summary>
        private Image CreateHeadingRule(string name, RectTransform parent, float y)
        {
            RectTransform rect = CreateTopLeftRect(name, parent, 0f, y, 0f, 1f);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(0f, rect.offsetMin.y);
            rect.offsetMax = new Vector2(0f, rect.offsetMax.y);
            rect.sizeDelta = new Vector2(0f, 1f);

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = PanelRule;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// 给文本元素加一层深色投影，让标签落在哪里都读得清——
        /// 面板上、浅色时间块上、透过缝隙露出的游戏画面上。工作台字体是
        /// 细笔画亮色，所以没有这一层，落在浅色块上的标签会被冲淡。
        ///
        /// 偏移故意很小：这是可读性辅助，不是设计母题，而
        /// 12pt 文字上的宽投影读起来就是糊。
        /// </summary>
        internal static void ApplyTextShadow(TextMeshProUGUI text)
        {
            if (text == null)
            {
                return;
            }
            text.fontMaterial.EnableKeyword(ShaderUtilities.Keyword_Underlay);
            text.fontMaterial.SetColor(ShaderUtilities.ID_UnderlayColor, TextShadowColor);
            text.fontMaterial.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0.55f);
            text.fontMaterial.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -0.55f);
            text.fontMaterial.SetFloat(ShaderUtilities.ID_UnderlayDilate, 0.12f);
            text.fontMaterial.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.30f);
        }

        // 用近黑而不是纯黑，并且只给部分 alpha：纯黑投影落在深色
        // 面板上是看不见的，同时还要为每个字形付出填充率。
        private static readonly Color TextShadowColor = new Color(0f, 0f, 0f, 0.72f);

        private Image CreateRoundedPanel(
            string name, RectTransform parent, float x, float y,
            float width, float height, Color color, float radius)
        {
            Image image = CreateImage(name, parent, x, y, width, height, color);
            ApplyRounded(image, radius, false, 0f);
            return image;
        }

        /// <summary>一个圆角轮廓，用于卡片或输入凹槽的边框。</summary>
        private Image CreateRoundedFrame(
            string name, RectTransform parent, float x, float y,
            float width, float height, Color color, float radius, float frameWidth)
        {
            Image image = CreateImage(name, parent, x, y, width, height, color);
            ApplyRounded(image, radius, true, frameWidth);
            return image;
        }

        /// <summary>
        /// 给已有的 Image 换上圆角贴图，并把它切成九宫格绘制。九宫格
        /// 就是面板比高更宽时圆角仍保持圆形的原因。
        /// </summary>
        private static void ApplyRounded(Image image, float radius, bool frame, float frameWidth)
        {
            if (image == null)
            {
                return;
            }
            int size = Mathf.Max(8, Mathf.CeilToInt(radius * 2f) + 4);
            image.sprite = GetRoundedSprite(size, radius, frame, frameWidth);
            image.type = Image.Type.Sliced;
            // frame 贴图只有它自己的那圈——中间是透明的。填充它的中心
            // 会把九宫格的中心列和中心行画到中间去，把圆环变成
            // 一整块实心，某层边框就是这么把预览舞台刷白的。
            image.fillCenter = !frame;
        }

        private Image CreateImage(string name, RectTransform parent, float x, float y, float width, float height, Color color)
        {
            RectTransform rect = CreateTopLeftRect(name, parent, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private RawImage CreateRawImage(string name, RectTransform parent, float x, float y, float width, float height, Color color)
        {
            RectTransform rect = CreateTopLeftRect(name, parent, x, y, width, height);
            RawImage image = rect.gameObject.AddComponent<RawImage>();
            image.color = color;
            image.raycastTarget = false;
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
            return image;
        }

        private RawImage CreateStretchRawImage(string name, RectTransform parent, Color color)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            RawImage image = gameObject.GetComponent<RawImage>();
            image.color = color;
            image.raycastTarget = false;
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
            return image;
        }

        private Image CreateStretchImage(string name, RectTransform parent, Color color)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Image image = gameObject.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private TextMeshProUGUI CreateText(
            string name,
            RectTransform parent,
            float x,
            float y,
            float width,
            float height,
            string value,
            float size,
            Color color,
            TextAlignmentOptions alignment,
            bool bold
        )
        {
            RectTransform rect = CreateTopLeftRect(name, parent, x, y, width, height);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (uiFont != null)
            {
                text.font = uiFont;
            }
            text.text = value;
            text.fontSize = size;
            text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            text.color = color;
            text.alignment = alignment;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            ApplyTextShadow(text);
            return text;
        }

        private Button CreateButton(
            string name,
            RectTransform parent,
            float x,
            float y,
            float width,
            float height,
            string text,
            bool accent,
            Action action,
            out TextMeshProUGUI label
        )
        {
            RectTransform rect = CreateTopLeftRect(name, parent, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            SetButtonPalette(button, accent);
            if (action != null)
            {
                button.onClick.AddListener(delegate { action(); });
            }

            label = CreateText("Label", rect, 0f, 0f, width, height, text, 12f, TextSecondary, TextAlignmentOptions.Center, false);
            return button;
        }

        private Toggle CreateToggle(string name, RectTransform parent, float x, float y, float width, float height, string labelValue)
        {
            RectTransform rect = CreateTopLeftRect(name, parent, x, y, width, height);
            Toggle toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.navigation = new Navigation { mode = Navigation.Mode.None };

            Image box = CreateRoundedPanel("Box", rect, 0f, 4f, 22f, 22f, FieldWell, 5f);
            box.raycastTarget = true;
            Image check = CreateImage("Check", box.rectTransform, 5f, 5f, 12f, 12f, Accent);
            toggle.targetGraphic = box;
            toggle.graphic = check;

            CreateText("Label", rect, 34f, 0f, width - 34f, height, labelValue, 13f, TextSecondary, TextAlignmentOptions.MidlineLeft, false);
            return toggle;
        }

        private void CreateMediaSearchControls(RectTransform parent, float mediaWidth, float maxY)
        {
            float searchHeight = 24f;
            float refreshWidth = 30f;
            float searchWidth = Mathf.Max(40f, mediaWidth - 24f - refreshWidth - 6f);

            Button refresh = CreateButton(
                "MediaRefresh",
                parent,
                mediaWidth - 12f - refreshWidth,
                MediaSearchY,
                refreshWidth,
                searchHeight,
                "↻",
                false,
                RefreshMediaList,
                out mediaRefreshLabel);
            mediaRefreshButton = refresh;
            mediaRefreshLabel.fontSize = 15f;

            RectTransform rect = CreateTopLeftRect("MediaSearch", parent, 12f, MediaSearchY, searchWidth, searchHeight);
            rect.gameObject.SetActive(false);
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = FieldWell;
            background.raycastTarget = true;

            // 一圈描边能让输入框在面板上读得清，也和参数栏
            // 框住自己输入框的方式一致。
            Outline rim = rect.gameObject.AddComponent<Outline>();
            rim.effectColor = Border;
            rim.effectDistance = new Vector2(1f, -1f);
            rim.useGraphicAlpha = false;

            // 先在对象非激活时接好线，最后再打开。TMP_InputField
            // 在 OnEnable 里创建光标，那时会去读 textComponent 和 textViewport；
            // 对活着的对象 AddComponent 会立刻跑 OnEnable，而这时两者都还没赋值，
            // 于是光标根本不会建，字段再点也不会闪。
            mediaSearchField = rect.gameObject.AddComponent<TMP_InputField>();
            mediaSearchField.targetGraphic = background;
            mediaSearchField.lineType = TMP_InputField.LineType.SingleLine;
            mediaSearchField.navigation = new Navigation { mode = Navigation.Mode.None };
            mediaSearchField.customCaretColor = true;
            mediaSearchField.caretColor = new Color(0.960f, 0.960f, 0.960f, 1f);
            mediaSearchField.caretWidth = 2;
            mediaSearchField.caretBlinkRate = 0.85f;

            RectTransform textRect = CreateTopLeftRect("Text", rect, 7f, 0f, searchWidth - 14f, searchHeight);
            TextMeshProUGUI text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (uiFont != null)
            {
                text.font = uiFont;
            }
            text.fontSize = 11.5f;
            text.color = TextPrimary;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            ApplyTextShadow(text);

            TextMeshProUGUI placeholder = CreateText(
                "Placeholder", rect, 7f, 0f, searchWidth - 14f, searchHeight,
                "搜索图片…", 11.5f, TextSecondary, TextAlignmentOptions.MidlineLeft, false);
            placeholder.raycastTarget = false;

            mediaSearchField.textViewport = rect;
            mediaSearchField.textComponent = text;
            mediaSearchField.placeholder = placeholder;
            mediaSearchField.text = mediaSearchQuery ?? string.Empty;
            mediaSearchField.onValueChanged.AddListener(OnMediaSearchChanged);
            rect.gameObject.SetActive(true);
        }

        /// <summary>
        /// 重新枚举项目文件夹，好让游戏运行期间加进去的文件出现。
        /// </summary>
        private void RefreshMediaList()
        {
            projectDataDirty = true;
            nextProjectRefresh = 0f;
            mediaThumbnailCache.Clear();
            ClearMediaThumbnailQueue();
            ReleaseMediaTextures();
            RefreshProjectWorkspace(true);
            EditorSceneInteraction.ReportMediaRefresh();
        }

        private const float MediaSearchY = 40f;
        private const float MediaSearchRowHeight = 32f;
        private TMP_InputField mediaSearchField;
        private Button mediaRefreshButton;
        private TextMeshProUGUI mediaRefreshLabel;

        private static RectTransform CreateTopLeftRect(string name, RectTransform parent, float x, float y, float width, float height)
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

        private static void SetTopLeftRect(RectTransform rect, float x, float y, float width, float height)
        {
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void SetStretch(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        private static void CreateAnchoredLine(string name, RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Color color)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = sizeDelta;
            Image image = gameObject.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static void SetButtonPalette(Button button, bool selected)
        {
            Color normal = selected ? Selected : ButtonNormal;
            Color highlighted = selected ? SelectedHover : ButtonHover;
            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = highlighted;
            colors.pressedColor = highlighted * 0.85f;
            colors.selectedColor = highlighted;
            colors.disabledColor = new Color(normal.r, normal.g, normal.b, 0.45f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            if (button.targetGraphic != null)
            {
                button.targetGraphic.color = normal;
            }
        }
    }

    /// <summary>
    /// 工作台调色板。时间轴和参数栏从这里取色，保证三个面板
    /// 永远是同一套颜色——它们各自维护一份镜像时，任何一处微调
    /// 都会让屏幕上的读数变差，而这类偏差正是「几块面板看起来
    /// 像来自不同程序」的根源。
    ///
    /// 取值全部转发到 <see cref="EditorStudioOverlay"/> 上那一组常量，
    /// 这里只给其它类一个稳定的入口，不再复制数值。
    /// </summary>
    internal static class Palette
    {
        internal static Color Background { get { return EditorStudioOverlay.PanelBackgroundColor; } }
        internal static Color Panel { get { return EditorStudioOverlay.PanelColor; } }
        internal static Color PanelAlt { get { return EditorStudioOverlay.PanelAltColor; } }
        internal static Color ButtonNormal { get { return EditorStudioOverlay.ButtonNormalColor; } }
        internal static Color ButtonHover { get { return EditorStudioOverlay.ButtonHoverColor; } }
        internal static Color Accent { get { return EditorStudioOverlay.AccentColor; } }
        internal static Color Selected { get { return EditorStudioOverlay.SelectedColor; } }
        internal static Color SelectedHover { get { return EditorStudioOverlay.SelectedHoverColor; } }
        internal static Color Raised { get { return EditorStudioOverlay.RaisedColor; } }
        internal static Color ThumbWell { get { return EditorStudioOverlay.ThumbWellColor; } }
        internal static Color FieldWell { get { return EditorStudioOverlay.FieldWellColor; } }
        internal static Color PanelRule { get { return EditorStudioOverlay.PanelRuleColor; } }
        internal static Color TextPrimary { get { return EditorStudioOverlay.TextPrimaryColor; } }
        internal static Color TextSecondary { get { return EditorStudioOverlay.TextSecondaryColor; } }
        internal static Color TextMuted { get { return EditorStudioOverlay.TextMutedColor; } }
    }
}
