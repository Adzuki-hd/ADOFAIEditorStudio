using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ADOFAI;
using ADOFAIEditorStudio.Multiplayer;
using ADOFAIEditorStudio.Ui;
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
        private const float PanelHeadingInset = 16f;
        private const float PanelHeadingHeight = 28f;
        private const float PanelHeadingRule = PanelHeadingHeight + 1f;

        // 面板标题左边那条实心竖条。四个面板共用一条。
        //
        // 它是整个工作台上唯一的纯装饰图形，作用是把"金色"从文字的属性
        // 变成一块看得见的东西——没有它，页面全是细描边和文字，
        // 视线没有落点，看着就虚。
        internal const float HeadingBarWidth = 3f;
        internal const float HeadingBarHeight = 16f;
        internal const float HeadingBarGap = 8f;
        /// <summary>标题文字、以及面板内容的左缘：竖条之后再让开一个间距。</summary>
        internal const float HeadingTextInset = PanelHeadingInset + HeadingBarWidth + HeadingBarGap;

        /// <summary>
        /// 标题文字相对标题行**上提**的像素数。
        ///
        /// TMP 的 Middle 对齐是按字体行框（ascender..descender）居中的，而行框
        /// 是按拉丁字母的度量算的。中文glyph 在拉丁度量的字体里会往下坐
        /// （CJK 字形越过拉丁基线向下伸），于是「预览」「参数」这类中文标题
        /// 看起来比几何中心低一两个像素——和左边那条按几何居中的竖条对不上。
        ///
        /// 上提这一点点，两者才落在同一条水平线上。数字偏小，改一行就能调。
        /// </summary>
        internal const float HeadingTextLift = 2f;

        /// <summary>
        /// 标题文字的框高。比标题行（<see cref="PanelHeadingHeight"/>）略高一点，
        /// 加上下面这条算式，框就是**居中**在标题行上的。
        ///
        /// 标题的位置只有一处负责：ApplyWorkspaceLayout 里那次 SetTopLeftRect。
        /// 创建时给的值只是占位，重排一跑就被它覆盖——所以两边的算式必须一致，
        /// 否则就是"改了半天没反应"。这里踩过：预览和参数两处写死的 y 差了 3px
        /// （11 对 14），两个标题因此不在一条水平线上，和左边的竖条也各偏各的。
        /// </summary>
        private const float HeadingRowBoxHeight = 30f;

        /// <summary>标题框在标题行上居中、再减去中文要上提的那几个像素。</summary>
        private const float HeadingRowY =
            (PanelHeadingHeight - HeadingRowBoxHeight) * 0.5f - HeadingTextLift;

        internal const float CornerLarge = 9f;
        internal const float CornerSmall = 6f;

        private const float DesignWidth = 1920f;
        private const float DesignHeight = 1080f;
        private const float HeaderHeight = 54f;
        // 外边距与面板间距。原来都是 12，整个界面读起来是「紧」的——
        // 面板之间、面板与屏幕边、标题与内容全是同一个数，没有呼吸。
        // 提到 16 之后同样的内容从容很多，这是最容易见效、
        // 又不改任何结构的一档调整。
        private const float OuterMargin = 16f;
        private const float PanelGap = 16f;

        /// <summary>
        /// 面板自己的内边距（内容离面板边缘的距离）。
        ///
        /// 和 OuterMargin 是两回事：那个是面板离屏幕边缘的距离。
        /// 混用过一次——把面板内部某一块的横坐标按 OuterMargin 算，
        /// 结果它整体右移了 4px，和同一栏里的其它块对不齐。
        /// </summary>
        private const float PanelInnerInset = 12f;

        // 两侧面板的宽度。两边**永远等宽**，因为舞台要被夹在正中间——
        // 两栏不一样宽，舞台就会偏，看起来像被谁挤了一下。
        //
        // 宽度是算出来的，不是定的。唯一真正定死的量是舞台：
        // 它只受可用高度限制（高度不够就矮，宽度随之按比例缩），
        // 横向余量则由两栏平分。所以时间轴拉高、舞台变矮时，
        // 两栏自动变宽，媒体网格也就自动多出一列——列数是按面板宽度
        // 现算的，不需要为「什么时候该多一列」写死任何阈值。
        //
        // 这个最小值只在一件事上生效：极端情况下（屏幕特别宽）
        // 防止两侧被舞台挤没。1920 设计宽度下不会碰到它。
        private const float MinimumSidePanelWidth = 200f;
        private const float DefaultTimelineHeight = 292f;
        private const float MinimumTimelineHeight = 220f;
        private const float MaximumTimelineHeight = 520f;
        private const float TimelinePanelWidth = DesignWidth - OuterMargin * 2f;
        private const float PreviewPanelY = HeaderHeight + 12f;
        private const float PreviewStageInsetY = 58f;
        // 目标列数。实际列数由当前面板宽度算出来，不是钉死的——
        // 面板变窄时宁可少一列，也不要把格子压到看不清缩略图。
        internal const int MediaGridTargetColumns = 6;
        internal const float MediaGridColumnGap = 6f;
        internal const float MediaGridRowGap = 8f;
        // 网格整体离视口边缘的留白，和每张卡片自己的内边距。
        // 分开是因为它们作用相反：外面那圈要够宽，网格才不贴着
        // 滚动条；里面那圈要够窄，缩略图才拿得到宽度——格子本来
        // 就只有八十来像素，被内边距吃掉 16px 就明显小了。
        internal const float MediaGridPadding = 10f;
        internal const float MediaCardInset = 4f;
        // 一列窄到这个数就不能再挤了：再窄缩略图就小而糊，
        // 干脆少一列。填不满六列时宁可这样，也不要六格都看不清。
        internal const float MediaGridMinColumnWidth = 56f;
        // 卡片高度和列宽无关，是「缩略图方块 + 下面一行文件名」的高。
        internal const float MediaCardLabelHeight = 18f;
        internal const float MediaCardHeight = 112f;
        // 缓存的核心就是每个媒体文件一条记录：列表是要滚动的，被淘汰的条目
        // 下一趟就得从几 MB 的源图重新解码。Digital Warrior 文件夹里有 178 张图，
        // 其中好几张 5000+ px、4-5MB，而 64 条的上限意味着滚动时会不停地
        // 重新解码。缩略图最大也就 256px，全部留着
        // 只占几 MB 纹理内存。
        private const int MediaThumbnailCacheCapacity = 512;
        private const float MediaThumbnailLoadDelay = 0.12f;
        private const string UiGlyphProbe = "编辑器工作台工程媒体文件目录未保存浏览全部装饰物轨道预览参数时间轴锁定缩放镜头视效音频游戏其他第格图片文本粒子对象";

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
            // 缩略图方框，卡片局部坐标。存下来是因为缩略图是异步解出来的：
            // 它到达时布局可能已经变过，要按「此刻」的方框重新等比适配。
            public float ThumbBoxX;
            public float ThumbBoxY;
            public float ThumbBoxWidth;
            public float ThumbBoxHeight;
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
        // 取值全部来自 UiTheme —— 这里只保留「这个面板用哪一档」的映射，
        // 不再写死任何色值。加主题时改 UiTheme 一处，整个工作台跟着走。
        //
        // 层次的划分本身没有变，仍然是：
        //   Background  最底（桌面）
        //   Panel       浮在上面的面板
        //   Raised      面板里的卡片、输入框、按钮
        // 凹槽（PanelAlt、FieldWell）落回比 Background 更深的一档，
        // 于是「板子凸起、里面的内容凹下去」用两档就说清了。
        private static Color Background { get { return UiTheme.Current.SurfacePage; } }
        private static Color Header { get { return UiTheme.Current.SurfacePanel; } }
        private static Color Panel { get { return UiTheme.Current.SurfacePanel; } }
        private static Color PanelAlt { get { return UiTheme.Current.SurfaceWell; } }
        private static Color ButtonNormal { get { return UiTheme.Current.SurfaceRaised; } }
        private static Color ButtonHover { get { return UiTheme.Current.SurfaceRaisedHover; } }
        // 强调色是这里唯一的饱和色，选中态都从它派生，
        // 而不是从灰色阶派生，这样选中的行一眼就能认出来。
        private static Color Accent { get { return UiTheme.Current.ActionPrimary; } }
        // 第二强调色，专门给"要读的东西"：面板标题、时间读数、计数。
        // 和 Accent 分工，不混用——同一个颜色既当按钮又当标题，
        // 界面就读不出哪里能点了。
        private static Color AccentSecondary { get { return UiTheme.Current.AccentSecondary; } }
        private static Color Selected { get { return UiTheme.Current.SelectedFill; } }
        private static Color SelectedHover { get { return UiTheme.Current.SelectedHoverFill; } }
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
        private static Color Border { get { return UiTheme.Current.BorderHairline; } }
        private static Color TextPrimary { get { return UiTheme.Current.TextPrimary; } }
        private static Color TextSecondary { get { return UiTheme.Current.TextSecondary; } }
        private static Color TextMuted { get { return UiTheme.Current.TextMuted; } }
        /// <summary>压在实心强调色上的文字（分段控件的选中段）。</summary>
        private static Color TextOnAccent { get { return UiTheme.Current.TextOnAccent; } }
        // 凸起内容：卡片和色块。比面板高一档，
        // 这样卡片读起来像放在表面上的一件物体，而不是表面上的一块区域。
        private static Color Raised { get { return UiTheme.Current.SurfaceRaised; } }
        // 比 Raised 再高一档，给凸起卡片内部的内容用（缩略图、色块）。
        // 这是全套里最亮的一档填充色，仍然远低于纯白。
        private static Color ThumbWell { get { return UiTheme.Current.SurfaceThumb; } }
        /// <summary>面板标题条的实心底衬。比 Raised 再亮一档，见 UiTheme.SurfaceBand。</summary>
        private static Color Band { get { return UiTheme.Current.SurfaceBand; } }
        // 面板标题下方的接缝。比描边弱，因为它分割的是同一个表面，
        // 而不是隔开两个物体——但要亮到能在面板上看见。
        private static Color PanelRule { get { return UiTheme.Current.BorderHairline; } }
        // 输入凹槽。它是"凹"的，所以要比面板暗，
        // 才能读成一个输入框，而不是面板上的一个凸起。
        private static Color FieldWell { get { return UiTheme.Current.SurfaceWell; } }

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
        private static Color PanelShadow { get { return UiTheme.Current.Shadow; } }

        // Palette 是平级类，读不到 private static，所以这里开一组
        // internal 只读入口。用属性而不是把字段改成 internal：
        // 字段仍然只有本文件能写，其它类只能读。
        internal static Color PanelBackgroundColor { get { return Background; } }
        internal static Color PanelColor { get { return Panel; } }
        internal static Color PanelAltColor { get { return PanelAlt; } }
        internal static Color ButtonNormalColor { get { return ButtonNormal; } }
        internal static Color ButtonHoverColor { get { return ButtonHover; } }
        internal static Color AccentColor { get { return Accent; } }
        internal static Color AccentSecondaryColor { get { return AccentSecondary; } }
        internal static Color SelectedColor { get { return Selected; } }
        internal static Color SelectedHoverColor { get { return SelectedHover; } }
        internal static Color RaisedColor { get { return Raised; } }
        internal static Color ThumbWellColor { get { return ThumbWell; } }
        internal static Color BandColor { get { return Band; } }
        internal static Color FieldWellColor { get { return FieldWell; } }
        internal static Color PanelRuleColor { get { return PanelRule; } }
        internal static Color TextPrimaryColor { get { return TextPrimary; } }
        internal static Color TextSecondaryColor { get { return TextSecondary; } }
        internal static Color TextEmphasisColor { get { return UiTheme.Current.TextEmphasis; } }
        internal static Color TextMutedColor { get { return TextMuted; } }
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
        /// <summary>左栏的装饰物页。建好后交给时间轴去通知刷新。</summary>
        private EditorDecorationList decoList;
        /// <summary>装饰物页的根。切标签时按它整页开关。</summary>
        private RectTransform decoView;
        // 左栏顶部那排标签：装饰物 / 媒体 / 原生每一个设置页。
        //
        // 它是一条**可横滚的带子**（见 HorizontalButtonStrip）：原生那七个
        // 设置页加上前两项，横着排不下这 357px 的栏宽，所以内容比可视区宽时
        // 滚轮和左键拖动都能滚。
        private RectTransform browserTabStripHost;
        private RectTransform browserTabStripContent;
        private HorizontalButtonStrip browserTabStrip;
        private readonly List<Button> browserTabButtons = new List<Button>();
        private readonly List<TextMeshProUGUI> browserTabLabels = new List<TextMeshProUGUI>();
        /// <summary>每个按钮对应哪个设置页；前两个（装饰物 / 媒体）是 None。</summary>
        private readonly List<LevelEventType> browserTabPages = new List<LevelEventType>();
        // 原生有哪些设置页、各叫什么。布局时从 settingsPanel 现读 ——
        // 页数或名字变了这里会跟着变，不用我们维护一份名单。
        /// <summary>这排按钮当前的宽度。不满 80 是常态，见 LayoutBrowserTabs 的排法。</summary>
        private float browserTabWidth = BrowserTabWidth;

        /// <summary>左栏当前显示哪一页。</summary>
        private enum BrowserPane
        {
            /// <summary>装饰物列表。</summary>
            Decorations,
            /// <summary>媒体库。</summary>
            Media,
            /// <summary>
            /// 游戏原生的某一个设置页。
            ///
            /// 内容**不是我们建的** —— 是把原生那个侧栏借过来摆在左栏这个
            /// 位置，具体是哪一页由 <see cref="selectedSettingsPage"/> 决定，
            /// 内容由另一份 <see cref="EditorPropertyPanel"/> 渲染，
            /// 见 <see cref="ShowSettingsPane"/>。
            /// </summary>
            Settings
        }

        private BrowserPane browserPane = BrowserPane.Decorations;
        /// <summary>设置页在前台时，正显示的是哪一页。</summary>
        private LevelEventType selectedSettingsPage = LevelEventType.SongSettings;

        /// <summary>
        /// 设置页那一份参数栏的外壳、容器和实例，见 <see cref="EnsureSettingsPane"/>。
        ///
        /// **显隐一律动 `settingsPaneRoot`**，不动 `settingsPane`：描边和投影
        /// 是面板的**同级**兄弟（见 AddPanelBorder），只藏面板藏不干净。
        /// </summary>
        /// <summary>
        /// 一扇「文本窗口」的两个把手：整层（显隐看它）和正文。
        ///
        /// 说明窗口和版本更新窗口共用同一套外壳，见 <see cref="BuildTextWindow"/>。
        /// </summary>
        private sealed class TextWindow
        {
            internal RectTransform Layer;
            internal TextMeshProUGUI Body;
        }

        private TextWindow helpWindow;
        private TextWindow versionWindow;

        private RectTransform settingsPaneRoot;
        private Image settingsPane;
        private EditorPropertyPanel settingsPropertyPanel;
        /// <summary>设置页被 ImGui 版接管了（见 SuppressSettings / ShowSettingsPane）。</summary>
        private bool settingsPaneTakenOver;
        private RectTransform mediaPanel;
        private RectTransform mediaViewport;
        private RectTransform mediaContent;
        private TextMeshProUGUI mediaTitle;
        private ScrollRect mediaScroll;
        private RectTransform previewPanel;
        private RectTransform previewToolbar;
        // 预览工具栏上的车道筛选按钮，谱面加载后要靠它刷新图标。
        private readonly List<RectTransform> laneFilterButtonRects = new List<RectTransform>();
        private RectTransform previewHeading;
        private RectTransform previewViewport;
        /// <summary>预览面板页眉的那条彩色竖条和那条分隔线。ImGui 版接管时一起藏。</summary>
        private Image previewHeadingBar;
        private Image previewHeadingRule;
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
        /// <summary>
        /// 联机面板。挂在 designRoot 上而不是某个面板里——它是「模式切换」，
        /// 开着时要盖住整个工作台。
        /// </summary>
        private MultiplayerPanel multiplayerPanel;
        /// <summary>顶部那枚「联机」按钮，用它把面板叫出来。</summary>
        private Button multiplayerButton;
        private Camera previewCamera;
        private Rect previewCameraOriginalRect;
        private bool previewCameraModified;

        // 预览期间被一并收进面板矩形的背景相机，以及它们各自的原始矩形。
        private readonly List<Camera> previewBackgroundCameras = new List<Camera>();
        private readonly List<Rect> previewBackgroundOriginalRects = new List<Rect>();
        private bool nonPreviewPointerCapture;
        private static MethodInfo loadImageMethod;
        private static bool imageLoaderSearched;
        private EditorProjectSession projectSession;
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
        /// uGUI 那扇参数栏实例。
        ///
        /// ImGui 那版**借它来写值**（`Commit` / `SetDisabled`）：撤销、联机上报、
        /// 活对象转发、批量合成一条历史，这些语义全在那一条路径上 ——
        /// 复制一份出来，两边迟早会不一样，而"改了一个值却撤不回来"
        /// 是最难查的那类问题。
        ///
        /// 它被 ImGui 接管时会藏起来，但**实例还活着**（只是不显示），
        /// 所以照样能用。
        /// </summary>
        internal EditorPropertyPanel PropertyPanel
        {
            get { return propertyPanel; }
        }

        /// <summary>装饰物列表给 ImGui 版复用的快照源。</summary>
        internal EditorDecorationList DecorationList
        {
            get { return decoList; }
        }

        /// <summary>当前左栏是不是装饰物页。</summary>
        internal bool IsDecorationPane
        {
            get { return browserPane == BrowserPane.Decorations; }
        }

        /// <summary>
        /// 装饰物列表在屏幕上的矩形（1920x1080 设计坐标）。
        ///
        /// **必须加上左栏面板自己的原点。** `BrowserContentX/ViewY` 是相对
        /// 「ProjectWorkspace」那块面板量的（见 BuildBrowser），不是相对整个
        /// 设计区；直接把它们交给 ImGui 会整体偏到左上角去。
        /// </summary>
        internal void GetDecorationListRect(out float x, out float y, out float width, out float height)
        {
            x = OuterMargin + BrowserContentX;
            y = PreviewPanelY + BrowserViewY;
            width = previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset;
            height = Mathf.Max(64f, PreviewPanelHeight - BrowserViewY - PanelInnerInset);
        }

        /// <summary>
        /// 参数栏在屏幕上的矩形（1920x1080 设计坐标）。
        ///
        /// ImGui 那版参数栏要占**同一个坑**。坐标不用换算，是因为两边已经统一到
        /// 同一个设计空间了（见 EditorImGuiBridge 里对 `_scale` 的说明）。
        /// </summary>
        internal void GetInspectorRect(out float x, out float y, out float width, out float height)
        {
            x = previewLayout.InspectorX;
            y = PreviewPanelY;
            width = previewLayout.RightPanelWidth;
            height = PreviewPanelHeight;
        }

        /// <summary>
        /// 装饰物列表这个坑让不让 ImGui 那版接管。
        ///
        /// 和 <see cref="SuppressInspector"/> 同一个套路：桥接每帧调用，幂等。
        /// 但**只在左栏停在装饰物页时**才压 —— 切到媒体页时 ImGui 什么都不画，
        /// 这里必须把 uGUI 那扇还回去，否则媒体页整个是空的。
        /// </summary>
        internal void SuppressDecorations(bool suppressed)
        {
            if (decoView == null)
            {
                return;
            }
            bool wanted = !suppressed;
            if (decoView.gameObject.activeSelf != wanted)
            {
                decoView.gameObject.SetActive(wanted);
            }
        }

        // -------------------------------------------------- ImGui 版媒体页的口子
        //
        // 和装饰物列表那组同一种做法：**界面逻辑留在 ImGui 那边，这里只开数据、
        // 几何和副作用的口子**。筛选、缩略图缓存、选中都有且只有这一份实现 ——
        // 往 ImGui 那边复制一份，两边迟早就不是一个意思了。

        /// <summary>当前左栏是不是媒体页。</summary>
        internal bool IsMediaPane
        {
            get { return browserPane == BrowserPane.Media; }
        }

        /// <summary>媒体页在屏幕上的矩形（1920x1080 设计坐标）。和装饰物页同一个坑。</summary>
        internal void GetMediaRect(out float x, out float y, out float width, out float height)
        {
            x = OuterMargin + BrowserContentX;
            y = PreviewPanelY + BrowserViewY;
            width = previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset;
            height = Mathf.Max(64f, PreviewPanelHeight - BrowserViewY - PanelInnerInset);
        }

        /// <summary>筛选之后的媒体列表。**只读，别改。**</summary>
        internal List<EditorProjectMediaItem> VisibleMedia
        {
            get { return visibleProjectMedia; }
        }

        /// <summary>
        /// 谱面目录里的媒体**总数**，不受搜索影响。
        ///
        /// 搜索生效时顶栏那个计数要写成「命中 / 总数」—— 只报分子的话，
        /// 搜出 3 张就只看到个"3"，没法判断是"整个目录就 3 张"还是"滤掉了 40 张"。
        /// </summary>
        internal int MediaTotalCount
        {
            get
            {
                return projectSession == null || projectSession.Media == null
                    ? 0 : projectSession.Media.Count;
            }
        }

        /// <summary>选中项的 RelativePath；没选中是 null。</summary>
        internal string SelectedMediaPath
        {
            get { return selectedMediaPath; }
        }

        /// <summary>按 <see cref="VisibleMedia"/> 里的下标选中。越界当没点。</summary>
        internal void ActivateMediaIndex(int index)
        {
            if (index < 0 || index >= visibleProjectMedia.Count)
            {
                return;
            }
            selectedMediaPath = visibleProjectMedia[index].RelativePath;
            mediaCardsDirty = true;
            UpdateMediaCards();
        }

        internal string MediaSearchQuery
        {
            get { return mediaSearchQuery; }
        }

        internal void SetMediaSearch(string query)
        {
            query = query ?? string.Empty;
            if (string.Equals(mediaSearchQuery, query, StringComparison.Ordinal))
            {
                return;
            }
            mediaSearchQuery = query;
            RebuildVisibleMedia();
        }

        /// <summary>媒体页上那个「刷新」——重扫一遍谱面目录。</summary>
        internal void RefreshMedia()
        {
            RefreshProjectWorkspace(true);
            // **报一声"刷完了"。**
            //
            // 这句原来只在 `RefreshMediaList` 里，而那个方法是 uGUI 那个刷新按钮
            // 的回调 —— 那扇面板被 ImGui 接管时整块 `SetActive(false)` 了，
            // 于是点了刷新一声不响，看不出到底刷没刷。挪到这里，两条路都盖得住。
            EditorSceneInteraction.ReportMediaRefresh();
        }

        /// <summary>
        /// 取某个媒体项的缩略图。**没有会顺手排进队列**，下一帧再来就有了
        /// （解码是异步的，每帧限量，见 ProcessMediaThumbnailQueue）。
        ///
        /// 返回 false 有三种情况：还没解出来、解码失败了、本来就不给解
        /// （`.gif` 和取不到路径的）—— 调用方一律画占位，不用区分。
        /// </summary>
        internal bool TryGetMediaThumbnail(EditorProjectMediaItem item, out Texture2D texture)
        {
            texture = null;
            if (item == null)
            {
                return false;
            }
            string key = GetMediaThumbnailKey(item);
            if (string.IsNullOrWhiteSpace(key)
                || string.Equals(item.Extension, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            MediaThumbnailCacheEntry entry;
            if (mediaThumbnailCache.TryGetValue(key, out entry))
            {
                TouchMediaThumbnail(entry);
                if (entry.Failed || entry.Texture == null)
                {
                    return false;
                }
                texture = entry.Texture;
                return true;
            }

            QueueMediaThumbnail(item, key);
            return false;
        }

        /// <summary>把 uGUI 那扇媒体页藏掉（ImGui 版接管时）。切回别的页要还回来。</summary>
        internal void SuppressMedia(bool suppressed)
        {
            if (mediaPanel == null)
            {
                return;
            }
            bool wanted = !suppressed;
            if (mediaPanel.gameObject.activeSelf != wanted)
            {
                mediaPanel.gameObject.SetActive(wanted);
            }
        }

        // -------------------------------------------------- ImGui 版设置页的口子
        //
        // 左边那几页设置（歌曲/关卡/轨道/……）也是 ImGui 版接管的。
        // **它们本身就是一个 `LevelEvent`**（原生把八页挂在 levelData 上，
        // 见 FindSettingsPage），所以 ImGui 参数栏那套"渲染一个 LevelEvent"
        // 直接拿来用 —— 行怎么排、值怎么改、撤销怎么走，全是同一份实现。

        /// <summary>当前左栏是不是某个设置页。</summary>
        internal bool IsSettingsPane
        {
            get { return browserPane == BrowserPane.Settings; }
        }

        /// <summary>
        /// 设置页在屏幕上的矩形。**和媒体页、装饰物页是同一个坑** ——
        /// 三页轮流占满整宽（见 BuildBrowser 里那段说明），所以直接借媒体页的。
        /// </summary>
        internal void GetSettingsRect(out float x, out float y, out float width, out float height)
        {
            GetMediaRect(out x, out y, out width, out height);
        }

        /// <summary>当前设置页对应的那个 <see cref="LevelEvent"/>。不在设置页时是 null。</summary>
        internal LevelEvent SelectedSettingsEvent
        {
            get
            {
                return browserPane == BrowserPane.Settings
                    ? FindSettingsPage(selectedSettingsPage)
                    : null;
            }
        }

        /// <summary>当前设置页的显示名（和那排标签上写的是同一个词）。</summary>
        internal string SelectedSettingsTitle
        {
            get
            {
                for (int i = 0; i < SettingsPages.Length; i++)
                {
                    if (SettingsPages[i] == selectedSettingsPage)
                    {
                        return SettingsPageNames[i];
                    }
                }
                return "设置";
            }
        }

        /// <summary>
        /// 告诉 uGUI 那份参数栏"现在这一页是设置页"。
        ///
        /// ImGui 版自己画行，但**设置页特有的那几步副作用只能由它补**
        /// （路径/格位重算、背景重铺 —— 见 EditorPropertyPanel 里那两处
        /// `ReferenceEquals(..., settingsPage)`）。不打这个标记，ImGui 那边
        /// 改设置就会"值变了、画面不跟"。
        /// </summary>
        internal void MarkSettingsTarget(LevelEvent page)
        {
            if (settingsPropertyPanel != null)
            {
                settingsPropertyPanel.MarkSettingsPage(page);
            }
        }

        /// <summary>
        /// 顶栏那个 Image。ImGui 版接管时整个藏掉（见 SuppressHeader）。
        /// </summary>
        private Image headerRoot;

        // -------------------------------------------------- ImGui 版顶栏的口子

        /// <summary>
        /// 顶栏在屏幕上的矩形（1920x1080 设计坐标）。
        ///
        /// 顶栏是 `designRoot` 的**第一个直接子物体**，就摆在设计原点，
        /// 所以是整整一块 (0,0,1920,54) —— 不像左栏那几页要先加面板自己的原点。
        /// </summary>
        internal void GetHeaderRect(out float x, out float y, out float width, out float height)
        {
            x = 0f;
            y = 0f;
            width = DesignWidth;
            height = HeaderHeight;
        }

        /// <summary>
        /// 顶栏中段那条状态读数的当前文字（滚轮微调、存档提示、报错都走它）。
        ///
        /// 空的就表示这会儿没有要说的 —— ImGui 那边据此决定要不要画那块底衬。
        /// </summary>
        internal string StatusText
        {
            get { return statusLabel == null ? null : statusLabel.text; }
        }

        /// <summary>顶栏底色。</summary>
        internal Color HeaderColor { get { return Header; } }

        /// <summary>版本号那串字（蓝色，和品牌字的米白分开）。</summary>
        internal Color VersionLinkColor { get { return VersionLink; } }

        /// <summary>状态读数底衬的填充色（极淡的白）。</summary>
        internal Color StatusPillColor { get { return StatusPillFill; } }

        /// <summary>
        /// 把 uGUI 那条顶栏藏掉（ImGui 版接管时）。
        ///
        /// 藏的不只是底衬 —— 版本号、联机、帮助那三颗能点的也一起没了，
        /// 它们的点击由 ImGui 那边接管（走桥的 SetVersionVisible / ToggleHelp）。
        /// </summary>
        internal void SuppressHeader(bool suppressed)
        {
            if (headerRoot == null)
            {
                return;
            }
            bool wanted = !suppressed;
            if (headerRoot.gameObject.activeSelf != wanted)
            {
                headerRoot.gameObject.SetActive(wanted);
            }
        }

        /// <summary>预览面板页眉那一行有多高 —— 工具栏那一条。</summary>
        internal const float PreviewToolbarHeight = 52f;

        // -------------------------------------------------- ImGui 版时间轴的口子
        //
        // 时间轴面板 = 12 起、宽度 `TimelinePanelWidth`、高度跟着拖拽走（见
        // RelayoutProjectWorkspace 里那次 SetTopLeftRect）。**标题行是它最上面
        // 那 42 像素**：事件面板从 y=44 起，紧贴在它下面，所以这一条能单独接管，
        // 下面照旧。

        /// <summary>时间轴标题行（最上面 42 像素）在屏幕上的矩形。</summary>
        internal void GetTimelineHeaderRect(out float x, out float y, out float width, out float height)
        {
            x = 12f;
            y = DesignHeight - timelineHeight + 4f;
            width = TimelinePanelWidth;
            height = EditorTimelineView.TitleRowHeight;
        }

        /// <summary>时间轴视图（标题行上的按钮都转发到它）。</summary>
        internal EditorTimelineView TimelineView
        {
            get { return timelineView; }
        }

        /// <summary>
        /// 时间轴内容区（那条卡片）在屏幕上的矩形。
        ///
        /// 面板原点 + 相对量：`root` 就是面板根节点，所以相对量直接加。
        /// 面板高度跟着拖拽走，内容区的位置也跟着变 —— 所以每次都现算。
        /// </summary>
        internal void GetTimelineBodyRect(out float x, out float y, out float width, out float height)
        {
            float localX, localY, localWidth, localHeight;
            if (timelineView == null)
            {
                x = y = width = height = 0f;
                return;
            }
            timelineView.GetViewportLocalRect(out localX, out localY, out localWidth, out localHeight);
            x = 12f + localX;
            y = DesignHeight - timelineHeight + 4f + localY;
            width = localWidth;
            height = localHeight;
        }

        /// <summary>
        /// 把时间轴那排 uGUI 的标题行藏掉（ImGui 版接管时）。
        ///
        /// **只藏标题行**，下面的事件面板和车道区照旧 —— 事件面板从 y=44 起，
        /// 紧贴在标题行下面，是独立的一块。
        /// </summary>
        internal void SuppressTimelineTitleRow(bool suppressed)
        {
            if (timelineView != null)
            {
                timelineView.SuppressTitleRow(suppressed);
            }
        }

        /// <summary>
        /// 把时间轴那排 uGUI 的标尺（刻号和格线）藏掉（ImGui 版接管时）。
        ///
        /// **只藏标尺** —— 事件块还是 uGUI 画的（堆叠方案还没定），
        /// 两者叠在同一块区域上，正好用来对坐标。
        /// </summary>
        internal void SuppressTimelineRuler(bool suppressed)
        {
            if (timelineView != null)
            {
                timelineView.SuppressRuler(suppressed);
            }
        }

        /// <summary>把时间轴那批 uGUI 的事件块藏掉（ImGui 版接管时）。</summary>
        internal void SuppressTimelineBlocks(bool suppressed)
        {
            if (timelineView != null)
            {
                timelineView.SuppressBlocks(suppressed);
            }
        }

        // -------------------------------------------------- ImGui 版预览区的口子

        /// <summary>预览区现在有没有地方可画（窗口太小的时候舞台会被收掉）。</summary>
        internal bool PreviewVisible
        {
            get { return previewLayout.StageWidth > 1f && previewLayout.StageHeight > 1f; }
        }

        /// <summary>预览工具栏那一条在屏幕上的矩形（1920x1080 设计坐标）。</summary>
        internal void GetPreviewToolbarRect(out float x, out float y, out float width, out float height)
        {
            x = previewLayout.StageX;
            y = PreviewPanelY;
            width = previewLayout.StageWidth;
            height = PreviewToolbarHeight;
        }

        /// <summary>
        /// 把预览面板**页眉那几件**藏掉（工具栏底衬、页眉竖条、页眉线、标题）。
        ///
        /// **整块面板不能藏。** 它自己是透明的，游戏画面从它下面透上来 ——
        /// 藏了就把预览整个挡没了；而且舞台里还挂着交接用的那层画面。
        /// 所以只藏页眉这几件；舞台那圈描边暂时仍由 uGUI 画（静态四条线，
        /// ImGui 再画一遍只是重复）。
        /// </summary>
        internal void SuppressPreviewChrome(bool suppressed)
        {
            bool active = !suppressed;
            SetActiveIfChanged(previewToolbar, active);
            SetActiveIfChanged(previewHeading, active);
            SetActiveIfChanged(previewHeadingBar, active);
            SetActiveIfChanged(previewHeadingRule, active);
        }

        private static void SetActiveIfChanged(Component component, bool active)
        {
            if (component == null || component.gameObject.activeSelf == active)
            {
                return;
            }
            component.gameObject.SetActive(active);
        }

        /// <summary>
        /// ImGui 版要不要接管设置页。
        ///
        /// `true` 时把那扇已经借来的原生页**还回去**（不还的话它会留在
        /// 我们隐藏掉的界面里，原生编辑器那边反倒没有了）；`false` 时只清标记，
        /// **不主动显示** —— 该不该显示由 <see cref="SetBrowserTab"/> 决定。
        /// </summary>
        internal void SuppressSettings(bool suppressed)
        {
            if (settingsPaneTakenOver == suppressed)
            {
                return;
            }
            settingsPaneTakenOver = suppressed;
            if (suppressed)
            {
                if (settingsPaneRoot != null)
                {
                    HideSettingsPane();
                }
            }
            else if (settingsPropertyPanel != null)
            {
                // 交还时把标记清掉。留着的话，之后在右边参数栏改**事件**
                // 也会被当成"改设置"，多跑一遍不该跑的副作用。
                settingsPropertyPanel.MarkSettingsPage(null);
            }
        }

        /// <summary>
        /// 参数栏这个坑让不让 ImGui 那版接管。
        ///
        /// 接管时把 uGUI 这扇**整个藏掉**（`inspectorPanel` 是那扇面板的根，
        /// 标题条和描边都是它的子物体，藏它就够），否则两扇会叠在一起。
        /// 桥接每帧调用，所以这里写成幂等的：状态没变就什么都不做。
        /// </summary>
        internal void SuppressInspector(bool suppressed)
        {
            if (inspectorPanel == null)
            {
                return;
            }
            bool wanted = !suppressed;
            if (inspectorPanel.gameObject.activeSelf != wanted)
            {
                inspectorPanel.gameObject.SetActive(wanted);
            }
        }

        /// <summary>
        /// 时间轴当前选中项，按需解析。参数面板不能持有时间轴引用：
        /// BuildInspector 在 BuildTimeline 之前运行，所以面板创建时该视图
        /// 还不存在，而且它每次
        /// 分辨率重建都会被换掉。
        /// </summary>
        /// <summary>
        /// 时间轴视图。选区操作（删除 / 复制 / 剪切 / 粘贴）的入口 ——
        /// 多选存在它那边，所以这几个动作都由它做。
        /// </summary>
        internal EditorTimelineView Timeline
        {
            get { return timelineView; }
        }

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

        /// <summary>
        /// 联机改了本机正在看的这个事件，把参数栏刷成新值，并说明是谁改的。
        ///
        /// 除了即时同步，这还挡下了一类静默的错误：面板上显示的是旧值，
        /// 而谱面里已经是新值，用户照着旧数字继续改就会覆盖对方。
        /// </summary>
        internal void RefreshInspectorFromRemote(string who, bool isDecoration)
        {
            if (propertyPanel != null)
            {
                propertyPanel.RefreshFromRemoteEdit();
            }

            SetSceneSelectionStatus("「" + who + "」改了"
                + (isDecoration ? "这个装饰物" : "这个事件") + "，已同步。");
        }

        /// <summary>
        /// 叫参数栏重画一遍。
        ///
        /// 用在工作台自己的撤回 / 重做之后：那条路是**直接写字段**的，
        /// 不换 levelData，所以面板拿不到任何「选中项变了」的信号，
        /// 会一直显示撤回前的数字 —— 看起来就是「撤了但参数没变」。
        /// </summary>
        internal void RefreshPropertyPanel()
        {
            if (propertyPanel != null)
            {
                propertyPanel.RefreshFromRemoteEdit();
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

        /// <summary>
        /// 清掉工作台自己维护的那几份选中（时间轴事件、媒体卡片）。
        /// 游戏侧的 floor / decoration 选中由调用方负责——两边都得清，
        /// 因为时间轴的高亮有一部分是从游戏的选择集轮询出来的。
        /// </summary>
        internal void ClearSelection()
        {
            if (timelineView != null)
            {
                timelineView.ClearSelection();
            }

            if (string.IsNullOrEmpty(selectedMediaPath))
            {
                return;
            }

            selectedMediaPath = null;
            RebuildVisibleMedia();
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
                // 编辑器专属的装饰（位置轨道的滚动虚线、粒子发射器图标）
                // 只能在这里压：游戏自己的 Update 会在本帧稍后把它们
                // 重新点亮，放在求值阶段压等于白压。
                Preview.PreviewEvaluationEngine.TickEditorScaffoldingLate();
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

            // 临时状态到期就撤掉。见 SetTransientStatus。
            if (statusTransient && Time.unscaledTime >= statusClearAt)
            {
                statusTransient = false;
                SetSceneSelectionStatus(string.Empty);
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
            bool shortcutPressed = shortcut != KeyCode.None && Input.GetKeyDown(shortcut)
                && (!built || !IsTextInputFocused());

            // **游玩中不给开**。关掉始终允许——不然进去了就出不来。
            if (shortcutPressed && !visible && IsNativePlayModeRunning())
            {
                shortcutPressed = false;
                SetTransientStatus("游玩中不能打开工作台，先按 Esc 退出游玩");
            }

            if (shortcutPressed)
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

            // 搬过来的版本说明窗口是用它自己的标题栏 × 关的，uGUI 这边收不到通知，
            // 所以每帧问一句"它是不是刚被用户关掉了"—— 关掉了才把更新内容记成已读。
            EditorImGuiBridge imgui = EditorImGuiBridge.Instance;
            if (imgui != null && imgui.TakeVersionClosed())
            {
                MarkVersionNotesSeen();
            }

            if (visible && Input.GetKeyDown(KeyCode.Escape))
            {
                // 有窗口开着时 Esc **先关它**：否则开着一层窗口按 Esc，整个
                // 工作台一起消失，正看着的东西也没了 —— 那是两件事，退出得逐层来。
                //
                // 已经搬到 ImGui 的那两扇也要算进来：它们不挂在 uGUI 这棵树上
                // （`OpenWindow` 遍历的是 uGUI 的窗口层），所以得单独问一句。
                // 顺序和 `OpenWindow` 一致：先说明窗口，再版本说明。
                if (imgui != null && imgui.HelpVisible)
                {
                    imgui.SetHelpVisible(false);
                }
                else if (imgui != null && imgui.VersionVisible)
                {
                    imgui.SetVersionVisible(false);
                }
                else
                {
                    TextWindow openWindow = OpenWindow();
                    if (openWindow != null)
                    {
                        ToggleWindow(openWindow);
                    }
                    else
                    {
                        SetVisible(false);
                    }
                }
            }

            // 工作台开着的时候，随时把按钮的"选中"状态清掉。
            //
            // Unity 的 StandaloneInputModule 把 Space 映射成 Submit，
            // 而 EventSystem 会记住上一次点过的那个按钮；只要它还处在
            // 选中态，按一次空格播放/暂停就会顺带把那个按钮再点一次。
            // 表现就是「每次按空格，时间轴就切换一次堆叠/卷帘模式」——
            // 因为用户之前点过那个按钮。
            //
            // 之前只在 SetVisible(false)（按 F8 关闭）时清，所以开着
            // 工作台一直用就会一直复发。每帧清一次是标准做法，
            // 代价只是把已经是 null 的引用再写一遍 null。
            ClearUiButtonSelection();


            if (!visible)
            {
                nonPreviewPointerCapture = false;
                if (EditorPlaybackBridge.IsRestartingPreview)
                {
                    EditorPlaybackBridge.TickPreview(null);
                }

                // 工作台关着也要把联机会话推一下。
                //
                // 网络读线程是一直在收的，收到的消息进 ConcurrentQueue 等着
                // 主线程来取。主线程不取的话队列只进不出——而对方推一份谱面
                // 就是一两百 KB，挂着几小时会一直涨。这里驱动一次就够
                // （不刷新界面，关着没人看）。
                if (multiplayerPanel != null)
                {
                    multiplayerPanel.Tick();
                }
                // 加载遮罩挂在独立的 canvas 上，不随工作台重建，
                // 所以工作台关着的时候也要驱动它。
                TickLoadingScreen();
                return;
            }

            UpdateNonPreviewPointerCapture();

            EditorPlaybackBridge.TickPreview(previewViewport);
            RefreshPlaybackState();
            TickProjectWorkspace();

            // 联机面板自己管界面显隐；会话本身要继续跑（后台线程在收发），
            // 所以这个 Tick 是无条件调的。
            if (multiplayerPanel != null)
            {
                multiplayerPanel.Tick();
            }
            TickLoadingScreen();
        }

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
            // 各层各自兜住异常：**一个面板建不出来，不该把整个工作台一起拖下水。**
            // 那会表现成「F8 按了没反应」，而且从外面看不出是哪一层的问题。
            // （这一手是踩出来的：说明窗口里加错一个组件，整个 UI 就建不起来了。）
            BuildLayer("联机面板", delegate { BuildMultiplayer(designRoot); });
            BuildLayer("说明窗口", delegate { BuildHelpPanel(designRoot); });
            BuildLayer("更新窗口", delegate { BuildVersionWindow(designRoot); });

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
            headerRoot = header;

            // 品牌标记。顶栏原本只有一行文字浮在一整条空带上，视线没有落点。
            // 一道金色细线压在最左边，这条带子才有东西镇得住。
            CreateBrandMark(header.rectTransform);
            TextMeshProUGUI brandTitle = CreateText("Title", header.rectTransform, BrandMarkRight, 9f, 360f, 36f,
                "ADOFAI  EDITOR STUDIO", 20f, TextPrimary, TextAlignmentOptions.MidlineLeft, true);

            // 品牌字后面跟一小串**蓝色带下划线**的版本号。
            //
            // 下划线是「能点」的唯一提示 —— 这颗没有边框也没有底色，
            // 不然它就不像一行字了。点开更新窗口。
            //
            // 位置按品牌字的**实际宽度**算，不写死 x：字体或文案一换，
            // 写死的那个数就会和品牌字叠在一起。
            float brandWidth = brandTitle.GetPreferredValues(brandTitle.text, 4000f, 36f).x;
            float versionX = BrandMarkRight + brandWidth + 12f;
            TextMeshProUGUI versionLabel = CreateText("Version", header.rectTransform, versionX, 9f, 220f, 36f,
                "<u>v" + ModVersion + "</u>", 13f, VersionLink, TextAlignmentOptions.MidlineLeft, false);

            // 点击层同上：**单独一个物体**。挂在文字那一格上会因为
            // 「一个物体只能有一个 Graphic」而失败（见 BuildTextWindow 的说明）。
            float versionWidth = versionLabel.GetPreferredValues(versionLabel.text, 4000f, 36f).x;
            Image versionHit = CreateTopLeftRect("VersionHit", header.rectTransform,
                versionX - 6f, 9f, Mathf.Max(44f, versionWidth + 12f), 36f)
                .gameObject.AddComponent<Image>();
            versionHit.color = new Color(0f, 0f, 0f, 0f);
            versionHit.raycastTarget = true;
            Button versionButton = versionHit.gameObject.AddComponent<Button>();
            versionButton.targetGraphic = versionHit;
            versionButton.transition = Selectable.Transition.None;
            versionButton.onClick.AddListener(delegate { ShowVersionWindow(); });
            CreateText("Subtitle", header.rectTransform, 390f + BrandMarkShift, 13f, 220f, 28f, "剪辑器工作台", 13f, TextMuted, TextAlignmentOptions.MidlineLeft, false);

            // 实时状态读数。夹在副标题和快捷键提示之间，这样它有
            // 喘息的空间、也不撞上任何一边，宽度也够
            // longest adjustment line ("深度 +120" style plus a label).
            // 状态读数的底衬。
            //
            // 读数在顶栏中段、小字、又没有边界 —— 有东西临时报上来的时候
            // 等于没显示（实测：Ctrl+S 存完根本没人看得见）。垫一块圆角
            // 底衬，它才读得成"一块状态"，而不是一串飘在顶栏中间的字。
            // 它按**文字实际宽度**贴合，见 RefreshStatusPill。
            statusPill = CreateImage("StatusPill", header.rectTransform,
                StatusLabelX - 10f, StatusPillY, 120f, StatusPillHeight, StatusPillFill);
            statusPill.sprite = GetRoundedSprite(24, StatusPillRadius, false, 0f);
            statusPill.type = Image.Type.Sliced;
            statusPill.raycastTarget = false;
            statusPill.gameObject.SetActive(false);

            statusLabel = CreateText(
                "Status", header.rectTransform, StatusLabelX, 13f, 900f, 28f,
                string.Empty, 13f, Accent, TextAlignmentOptions.MidlineLeft, false);
            statusLabel.color = Accent;

            CreateText("Shortcut", header.rectTransform, DesignWidth - 230f, 13f, 206f, 28f, "F8  工作台  ·  Esc  关闭", 13f, TextMuted, TextAlignmentOptions.MidlineRight, false);

            // 联机入口。放在状态读数和快捷键提示之间那段空档里——
            // 它是个偶尔才点的东西，不该挤到常用控件那一带。
            //
            // **暂时封住**：协议、面板、技能文档全都还在代码里，只是先不给玩家
            // 开这个口子。等多人写谱做完，把回调换回 multiplayerPanel.Toggle()。
            TextMeshProUGUI multiplayerLabel;
            multiplayerButton = CreateButton("Multiplayer", header.rectTransform,
                1540f, 13f, 96f, 28f, "联机", false,
                delegate
                {
                    SetTransientStatus("联机功能开发中，敬请期待");
                }, out multiplayerLabel);

            // 「?」：一颗圆钮，点开说明窗口。就在「联机」右边那一小段空处。
            TextMeshProUGUI helpLabel;
            Button helpButton = CreateButton("Help", header.rectTransform,
                1656f, 10f, 34f, 34f, "?", false,
                delegate { ToggleHelpPanel(); }, out helpLabel);
            // **白色描边圆 + 白色问号。**
            //
            // 贴图要用 `frame: true` 那一张（空心环）：实心那张会画出一颗
            // 深色实心圆，问号糊在上面既看不清也不像"帮助"。
            //
            // 用 `Simple` 而不是 `Sliced`：九宫格的边框是 `半径 + 1`，而圆的
            // 半径是边长的一半 —— 两倍边框必然超过边长，那是**非法**切分
            // （Unity 会报一行错）。这颗按钮就画在贴图的原生尺寸上（28×28），
            // 直接画反而最准。
            //
            // 颜色写死白、并且关掉颜色过渡：这颗是**图标**，不是一颗方块按钮，
            // 跟着调色板变深就失去"描边"的意义了。
            Image helpFace = helpButton.targetGraphic as Image;
            if (helpFace != null)
            {
                helpFace.sprite = GetRoundedSprite(34, 16.5f, true, 2.2f);
                helpFace.type = Image.Type.Simple;
                helpFace.color = Color.white;
            }
            helpButton.transition = Selectable.Transition.None;
            helpLabel.fontSize = 26f;
            helpLabel.color = Color.white;
        }

        // 顶栏品牌标记的度量。金线 + 缝隙，然后才是标题。
        // 位移由这几个数推出来，改一处就够，不要另写一个右移量。
        private const float BrandMarkX = 24f;
        private const float BrandMarkRuleWidth = 3f;
        /// <summary>
        /// 金线比标题那行字矮一截。等高的话它读成"第二个文字块"，
        /// 矮一截才读成"一道分隔"——它是把标记和标题分开的那条缝，
        /// 不是第二个装饰。
        /// </summary>
        private const float BrandMarkRuleHeight = 12f;
        private const float BrandMarkGap = 8f;
        /// <summary>标题的左缘。</summary>
        private const float BrandMarkRight = BrandMarkX + BrandMarkRuleWidth + BrandMarkGap;
        /// <summary>顶栏其余文字保持原相对间距，整体右移这么多。</summary>
        private const float BrandMarkShift = BrandMarkRight - BrandMarkX;

        /// <summary>
        /// 顶栏左侧的品牌标记：一道金色细线。
        ///
        /// 金线沿用面板标题那条竖条的语言（紫＝能点的，金＝要读的），
        /// 顶栏因此和下面四块面板是同一套图形词汇。
        ///
        /// 它左边原本还有一个实心紫菱形（旋转 45° 的圆角方块），
        /// 已按作者要求去掉——标记只剩这条分隔线。
        /// </summary>
        private void CreateBrandMark(RectTransform parent)
        {
            Image rule = CreateImage("BrandRule", parent, BrandMarkX,
                (HeaderHeight - BrandMarkRuleHeight) * 0.5f,
                BrandMarkRuleWidth, BrandMarkRuleHeight, AccentSecondary);
            rule.raycastTarget = false;
        }

        /// <summary>
        /// 建联机面板并挂到 designRoot。
        ///
        /// 挂 designRoot 而不是 uiRoot：designRoot 是 1920×1080 的设计坐标，
        /// 面板的位置和尺寸可以直接按设计尺寸写；挂 uiRoot 会被画布缩放
        /// 再套一层。它最后建，所以默认就在最上层。
        /// </summary>
        /// <summary>
        /// 加载遮罩所在的那层 canvas。
        ///
        /// **刻意不挂在工作台的 `uiRoot` 上**：那个对象在重建时会被销毁，
        /// 而遮罩恰恰要在重建期间一直盖着——挂上去就会跟着一起消失，
        /// 底下那一下闪烁又露出来了。
        ///
        /// 给它更高的 sortingOrder（31000 对工作台的 30000），所以永远在最上面。
        /// </summary>
        private GameObject loadingCanvas;
        private Multiplayer.MultiplayerLoadingScreen loadingScreen;

        private void EnsureLoadingScreen()
        {
            if (loadingScreen != null)
            {
                return;
            }
            if (uiFont == null)
            {
                uiFont = ResolveFont();
            }

            loadingCanvas = new GameObject(
                "ADOFAIEditorStudio.LoadingCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            // 挂在 overlay 自己身上，它和工作台一起存在、不参与重建。
            loadingCanvas.transform.SetParent(transform, false);

            RectTransform canvasRect = loadingCanvas.GetComponent<RectTransform>();
            canvasRect.anchorMin = Vector2.zero;
            canvasRect.anchorMax = Vector2.one;
            canvasRect.offsetMin = Vector2.zero;
            canvasRect.offsetMax = Vector2.zero;

            Canvas loading = loadingCanvas.GetComponent<Canvas>();
            loading.renderMode = RenderMode.ScreenSpaceOverlay;
            loading.overrideSorting = true;
            loading.sortingOrder = 31000;
            loading.pixelPerfect = false;

            CanvasScaler scaler = loadingCanvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(DesignWidth, DesignHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            // 遮罩本体是 canvas 的子物体，这样「隐藏」只关它、不关 canvas。
            RectTransform screenRoot = CreateTopLeftRect("Screen", canvasRect, 0f, 0f,
                DesignWidth, DesignHeight);
            loadingScreen = screenRoot.gameObject.AddComponent<Multiplayer.MultiplayerLoadingScreen>();
            loadingScreen.Initialize(screenRoot, uiFont, GetRoundedSprite(24, CornerLarge, false, 0f));
        }

        /// <summary>加入房间期间的加载遮罩。会话层只描述状态，显示由这里做。</summary>
        private void TickLoadingScreen()
        {
            if (Multiplayer.MultiplayerSession.Loading)
            {
                EnsureLoadingScreen();
                if (loadingScreen != null)
                {
                    loadingScreen.Show(Multiplayer.MultiplayerSession.LoadingText,
                        Multiplayer.MultiplayerSession.LoadingProgress);
                }
                return;
            }

            if (loadingScreen != null && loadingScreen.IsShowing)
            {
                loadingScreen.Hide();
            }
        }

        /// <summary>
        /// 把整个工作台拆掉重建。
        ///
        /// 加入房间结束时调它：那时候谱面、媒体、装饰物都刚换了内容，
        /// 而有些东西（**文字装饰物**是最明显的一个）不会自己刷新，
        /// 表现为「进了房间有几行字不见了」——但重进一次工作台就正常。
        /// 与其去追每一个漏刷的地方，不如在加载结束时整体重建一次；
        /// 反正遮罩盖着，用户看不到这一下。
        /// </summary>
        internal void RebuildWorkbench()
        {
            if (!built)
            {
                return;
            }
            RebuildRuntimeUiForResolution();
        }

        /// <summary>
        /// 「?」那颗按钮的说明正文。
        ///
        /// 快捷键这一节是**照代码核过**的，不是凭印象写的：
        /// 绑定在 `EditorPlaybackBridge.HandleEditorShortcuts` 和
        /// `EditorTimelineView.HandleKeyboardShortcuts` 两处。
        /// 改了那边记得回来改这里。
        /// </summary>
        /// <summary>
        /// 当前版本号。**必须和 Info.json 里的 Version 一致** ——
        /// 那个是 UMM 读的、也是版本闸门比对用的，这里是给人看的。
        /// 发版时两处一起改。
        /// </summary>
        internal const string ModVersion = "0.29.2";

        /// <summary>版本号那串字的颜色：蓝，和品牌字（米白）分开。</summary>
        private static Color VersionLink
        {
            get { return new Color(0.42f, 0.66f, 1f); }
        }

        /// <summary>
        /// 当前版本的更新内容。窗口标题是 `v<版本号> 更新内容`。
        ///
        /// 一行一条、用 Environment.NewLine 拼起来，**不写反斜杠转义** ——
        /// 换行符用转义写在这个文件里出过事（落进去成了真换行，把字符串
        /// 字面量截断）。这样写也不怕以后谁来编辑器里改文案。
        /// </summary>
        private static readonly string VersionNotes = string.Join(Environment.NewLine, new[]
        {
            "<size=20><b>v" + ModVersion + " 更新内容</b></size>",
            "",
            "<b>· 设置页</b>：左栏顶部那排多了「歌曲设置 / 关卡设置 / 轨道设置 / 背景设置 /",
            "   摄像头设置 / 杂项设置」，点开就是原版那几页设置，但用工作台自己的控件画，",
            "   改完立刻生效。",
            "<b>· 调色盘</b>：颜色属性不再只能手打 HEX。点色块开调色盘 ——",
            "   饱和/明度方块 + 色相条 + 透明度条 + HEX + RGBA。",
            "   拖动时画面不跟着变，**松手才写进谱面**（一次拖动只落一次数据，不卡）。",
            "<b>· 多选加强</b>：Ctrl+左键点选加选；Ctrl 从块上按住也能拉框；",
            "   点空白处取消全部选中。",
            "<b>· 批量改参</b>：多选之后参数栏只列公共属性，改一次落到整批，只记一步撤销。",
            "<b>· 说明窗口</b>：右上角那颗「?」—— 这个 MOD 是什么、全部快捷键、全部新功能。",
            "",
            "<b>修好的</b>",
            "<b>· 参数标签</b>：按原版那套三段式本地化，以前一部分参数显示成英文键名。",
            "<b>· 多选拖动</b>：整组按同一个吸附后的位移走，各自的偏移不再被抹平。",
            "<b>· 撤销</b>：撤回之后参数栏会跟着重画（以前数字停在撤回前）。",
            "<b>· 输入框</b>：点进去又原样出来不再算一次修改，也不再抹平批量里不同的值。",
            "<b>· Ctrl+点击</b>：块身上挂着 Button，事件被它吃掉，以前点了没反应。",
            "<b>· 缓速曲线</b>：粗细与高度对齐原版。",
        });

        /// <summary>群号。ImGui 那扇说明窗口的标题也要用，所以是 internal。</summary>
        internal const string QqGroupNumber = "1105868183";

        /// <summary>
        /// 官方加群链接。留空则退到下面的 QQ 客户端协议。
        ///
        /// 注意**不能**自己拼一个只带群号的地址 —— 加群页要靠链接里的
        /// `k` 才能定位到群，硬拼出来的点开就是一页加载不出来的空壳。
        /// 这个值是群主在 QQ 群里「分享群链接」拿到的。
        /// </summary>
        private const string QqGroupUrl = "https://qm.qq.com/q/JP3nBsxYQw";

        /// <summary>
        /// 没配加群链接时的退路：QQ 客户端的群卡片协议。
        ///
        /// 装了 QQ 会直接弹出那个群的群卡片（能加群）；没装则什么都没发生 ——
        /// 所以群号**永远**先复制到剪贴板，那才是保底的那一手。
        /// </summary>
        private const string QqGroupClientUrl =
            "mqqapi://card/show_pslcard?src_type=internal&version=1&uin=" + QqGroupNumber
            + "&card_type=group&source=qrcode";

        /// <summary>
        /// 点标题：**复制群号 + 打开加群页**。
        ///
        /// 复制是保底：链接能不能直接进群不由我们说了算，但群号到了剪贴板
        /// 就一定有用（QQ 里搜群号是最稳的加法）。
        ///
        /// ImGui 那扇说明窗口底部的链接也走到这里，所以是 internal。
        /// </summary>
        internal void OpenQqGroup()
        {
            GUIUtility.systemCopyBuffer = QqGroupNumber;
            SetTransientStatus("群号已复制：" + QqGroupNumber + "　QQ 里搜这个号即可加群", 4f);

            string url = string.IsNullOrEmpty(QqGroupUrl) ? QqGroupClientUrl : QqGroupUrl;
            try
            {
                Application.OpenURL(url);
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[Help] 打开加群页失败：" + exception.Message);
            }
        }

        /// <summary>
        /// 「?」那颗按钮的说明正文。
        ///
        /// 一行一条、用 <see cref="Environment.NewLine"/> 拼起来，**不写反斜杠
        /// 转义** —— 换行符用转义写在这个文件里出过事（落进去成了真换行，
        /// 把字符串字面量截断）。这样写也不怕以后谁来编辑器里改文案。
        ///
        /// 快捷键这一节是**照代码核过**的，不是凭印象写的：绑定在
        /// `EditorPlaybackBridge.HandleEditorShortcuts` 和
        /// `EditorTimelineView.HandleKeyboardShortcuts` 两处。
        /// 改了那边记得回来改这里。
        /// </summary>
        private static readonly string HelpText = string.Join(Environment.NewLine, new[]
        {
            "<size=22><b>ADOFAI Editor Studio</b></size>",
            "<size=14><color=#9aa4b2>给《A Dance of Fire and Ice》编辑器加的一套工作台</color></size>",
            "",
            "<size=17><b>这个 MOD 是什么</b></size>",
            "在原版编辑器旁边加一整套工作台：预览、时间线、参数编辑、媒体库、设置页。",
            "预览由它自己的求值器驱动，所以拖时间轴时画面即时跟着走，",
            "不必真的按播放 —— 这是它和「开原版播放再看」最大的区别。",
            "",
            "<size=17><b>新功能</b></size>",
            "<b>· 独立预览</b>：拖动即出画面，不受原版播放 / 暂停状态影响。",
            "<b>· 时间线</b>：事件按类型分节。拖块改时间与时长；拖动中块跟着光标",
            "   走，松手回到该在的行；默认吸附到格（按住 Alt 临时关掉）。",
            "<b>· 选择</b>：Ctrl+左键点选加选、从空白处拖动框选；点空白处取消全部。",
            "   选中的一批可以一起拖（整组按同一个位移走，各自的偏移不丢）。",
            "<b>· 参数栏</b>：原生全部事件参数，含缓动曲线；标签按原生那套三段式",
            "   本地化，和游戏里一致。",
            "<b>· 调色盘</b>：饱和/明度方块 + 色相条 + 透明度条 + HEX + RGBA。",
            "   拖动跟手，松手才写进谱面。",
            "<b>· 批量改参</b>：多选后只列公共属性，改一次落到整批，只记一步撤销。",
            "<b>· 装饰物</b>：左栏列表管理；媒体库里的图片直接拖进画面即成装饰物。",
            "<b>· 设置页</b>：歌曲 / 关卡 / 轨道 / 背景 / 摄像头 / 杂项，在工作台里直接改。",
            "<b>· 撤销重做</b>：工作台有自己的历史 —— 拖进来的装饰物、批量改动、",
            "   拖块改的时间，都能一次撤回。",
            "<b>· Ctrl+S 保存</b>：走原生 SaveLevel，存出来和原版逐字节一样。",
            "",
            "<size=17><b>快捷键</b></size>",
            "<b>F8</b>                工作台开 / 关",
            "<b>Esc</b>               关闭工作台（游玩中则是退出游玩）",
            "<b>Space</b>             播放 / 暂停",
            "<b>Ctrl+Z</b>            撤销",
            "<b>Ctrl+Y</b>            重做（Ctrl+Shift+Z 同）",
            "<b>Ctrl+S</b>            保存",
            "<b>Ctrl+C / X / V</b>    复制 / 剪切 / 粘贴选中的事件",
            "<b>Delete / Backspace</b>  删除选中",
            "<b>Shift+C</b>           清除全部选中",
            "<b>Ctrl+左键</b>         点选加选；从空白处拖动则框选",
            "<b>点空白处</b>          清除全部选中",
            "",
            "<size=16><b>时间轴</b></size>",
            "<b>滚轮</b>              上下滚；没有纵向可滚时左右滚",
            "<b>Shift+滚轮</b>        左右滚",
            "<b>中键拖动</b>          平移",
            "<b>Ctrl+滚轮</b>         缩放（Ctrl+= / Ctrl+- 同，Ctrl+0 复位）",
            "<b>Alt+滚轮</b>          行高（Alt+= / Alt+- 同）",
            "<b>点标尺 / 播放头</b>   拖动定位",
            "",
            "<size=16><b>调色盘</b></size>",
            "拖方块 / 色相条 / 透明度条选色，松手才写进谱面；",
            "HEX 和 R G B A 也可以直接输入。",
            "",
        });
        /// <summary>
        /// 建一层可选界面，自己兜住异常。
        ///
        /// 工作台的每一层（联机面板、说明窗口……）都是附加的：少一层只是少个
        /// 功能，而**只要有一层抛出去，<see cref="BuildRuntimeUi"/> 整个中止**，
        /// 表现就是「F8 按了没反应」—— 看不出问题在哪，也没法用。
        /// 分头兜住之后，坏掉的那一层在日志里点名，其余照常可用。
        /// </summary>
        private static void BuildLayer(string name, Action build)
        {
            try
            {
                build();
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[Workbench] " + name + " 建不出来：" + exception);
            }
        }

        private void BuildHelpPanel(RectTransform parent)
        {
            helpWindow = BuildTextWindow(parent, "Help",
                "加入 Q 交流群 " + QqGroupNumber, HelpText, OpenQqGroup, null, null);
        }

        private void BuildVersionWindow(RectTransform parent)
        {
            versionWindow = BuildTextWindow(parent, "VersionNotes",
                "v" + ModVersion + " 更新内容", VersionNotes, null,
                "点这里进 Q 群交流：" + QqGroupNumber, OpenQqGroup);
        }

        /// <summary>
        /// 建一扇「文本窗口」：遮罩 + 居中卡片 + 标题 + 可滚正文 + 右侧滚动条 + ×。
        ///
        /// 说明窗口和版本更新窗口共用这一套，差别只有标题、正文、以及
        /// 「点标题干什么」（说明窗口点标题是复制群号，更新窗口没有这一手）。
        /// 抄成两份的话，滚动、射线、补算高度这几处踩过的坑就得各修一遍，
        /// 而且总会漏掉一处。
        /// </summary>
        private TextWindow BuildTextWindow(RectTransform parent, string name, string titleText,
            string bodyText, Action onTitleClick, string footerText, Action onFooterClick)
        {
            TextWindow window = new TextWindow();
            window.Layer = CreateTopLeftRect(name + "Layer", parent, 0f, 0f, DesignWidth, DesignHeight);
            window.Layer.gameObject.SetActive(false);

            // 遮罩：只压暗后面的工作台，**不接点击**。
            //
            // 原来点它任意地方就关，一扇正在读的窗口手一滑点歪就没了。
            // 关它只留两个明确的出口：右上角那颗 ×，以及 Esc。
            // 但它仍然要吃点击 —— 不然点下去的会穿到后面的工作台上，
            // 在窗口底下偷偷改了东西。
            Image shade = CreateTopLeftRect(name + "Shade", window.Layer,
                0f, 0f, DesignWidth, DesignHeight).gameObject.AddComponent<Image>();
            shade.color = new Color(0f, 0f, 0f, 0.55f);
            shade.raycastTarget = true;

            const float cardWidth = 920f;
            const float cardHeight = 780f;
            RectTransform card = CreateRoundedPanel(name + "Card", window.Layer,
                (DesignWidth - cardWidth) * 0.5f, (DesignHeight - cardHeight) * 0.5f,
                cardWidth, cardHeight, Panel, CornerLarge).rectTransform;
            Image cardImage = card.GetComponent<Image>();
            AddPanelBorder(cardImage, CornerLarge);

            // **卡片自己必须吃射线。**
            //
            // `CreateRoundedPanel` 建出来的图默认 `raycastTarget = false`，
            // 于是指针落在窗口内部的空白处时，射线会**穿过去打到遮罩上**，
            // 而遮罩没有 IScrollHandler —— 滚轮就这么没了，挂在卡片上的
            // ScrollRect 永远收不到它。（别处用圆角面板时都要手动开这一句。）
            cardImage.raycastTarget = true;

            CreateText(name + "Title", card, 28f, 16f, cardWidth - 100f, 30f, titleText,
                UiType.Title, AccentSecondary, TextAlignmentOptions.MidlineLeft, true);

            if (onTitleClick != null)
            {
                // 整条标题可点。点击层是**单独一个物体**，绝不能挂在标题那一格上：
                // 一个物体只能有一个 Graphic，而 TMP 已经占着了 ——
                // 往上 AddComponent<Image>() 会失败并返回 null（Unity 只打一行日志、
                // 不抛），下一行就 NRE，整个 UI 构建中止，表现是 F8 按了没反应。
                //
                // 它建在标题**之后**，所以盖在字上收点击；全透明，不挡字。
                Image titleHit = CreateTopLeftRect(name + "TitleHit", card,
                    28f, 16f, cardWidth - 100f, 30f).gameObject.AddComponent<Image>();
                titleHit.color = new Color(0f, 0f, 0f, 0f);
                titleHit.raycastTarget = true;
                Button titleButton = titleHit.gameObject.AddComponent<Button>();
                titleButton.targetGraphic = titleHit;
                titleButton.transition = Selectable.Transition.None;
                titleButton.onClick.AddListener(delegate { onTitleClick(); });
            }

            CreateHeadingRule(name + "Rule", card, PanelHeadingRule);

            Button close = CreateButton(name + "Close", card, cardWidth - 52f, 16f, 30f, 30f, "×",
                false, delegate { ToggleWindow(window); }, out TextMeshProUGUI closeLabel);
            closeLabel.fontSize = UiType.Body;

            const float inset = 24f;
            const float scrollbarWidth = 10f;
            const float scrollbarGap = 10f;
            const float footerHeight = 32f;

            float viewWidth = cardWidth - inset * 2f - scrollbarWidth - scrollbarGap;
            float viewTop = PanelHeadingRule + 12f;

            // 有页脚时，视口要让出底下那一条 —— **页脚在滚动区之外**，
            // 不跟着正文滚走（它是"去哪儿找我们"，得一直在）。
            float footerY = cardHeight - inset - footerHeight;
            float viewBottom = footerText == null ? cardHeight - inset : footerY - 12f;
            float viewHeight = Mathf.Max(80f, viewBottom - viewTop);

            RectTransform viewport = CreateTopLeftRect(name + "Viewport", card,
                inset, viewTop, viewWidth, viewHeight);
            viewport.gameObject.AddComponent<RectMask2D>();

            TextMeshProUGUI body = CreateText(name + "Body", viewport, 0f, 0f, viewWidth, 100f,
                bodyText, 16f, TextPrimary, TextAlignmentOptions.TopLeft, false);
            body.overflowMode = TextOverflowModes.Overflow;

            // 内容高度起手给一个视口高，真正的值由 HelpContentSizer 补算 ——
            // 这一行**量不出东西**：TMP 的排版要等一帧，此刻 preferredHeight
            // 是 0，于是内容恰好和视口一样高、ScrollRect 认为没得滚。
            RectTransform content = CreateTopLeftRect(name + "Content", viewport,
                0f, 0f, viewWidth, viewHeight);
            body.rectTransform.SetParent(content, false);
            SetTopLeftRect(body.rectTransform, 0f, 0f, viewWidth, viewHeight);

            ScrollRect scroll = card.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.viewport = viewport;
            scroll.content = content;

            // 右边那条滚动条：滚轮归 ScrollRect，这一条管「抓着手柄拖」。
            RectTransform track = CreateTopLeftRect(name + "ScrollTrack", card,
                inset + viewWidth + scrollbarGap, viewTop, scrollbarWidth, viewHeight);
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = GetRoundedSprite(20, 5f, false, 0f);
            trackImage.type = Image.Type.Sliced;
            trackImage.color = new Color(1f, 1f, 1f, 0.06f);
            trackImage.raycastTarget = true;

            RectTransform handle = CreateTopLeftRect("Handle", track, 0f, 0f, scrollbarWidth, 60f);
            Image handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.sprite = GetRoundedSprite(20, 5f, false, 0f);
            handleImage.type = Image.Type.Sliced;
            handleImage.color = new Color(1f, 1f, 1f, 0.30f);
            handleImage.raycastTarget = true;

            EditorHelpScroll bar = track.gameObject.AddComponent<EditorHelpScroll>();
            bar.Scroll = scroll;
            bar.Track = track;
            bar.Handle = handle;

            HelpContentSizer sizer = content.gameObject.AddComponent<HelpContentSizer>();
            sizer.Body = body;
            sizer.Viewport = viewport;
            sizer.Content = content;

            if (footerText != null)
            {
                // 页脚那行群号：**蓝色带下划线**，和左上角版本号同一套写法 ——
                // 下划线是"能点"的唯一提示（这行没有边框也没有底色）。
                CreateHeadingRule(name + "FooterRule", card, footerY - 8f);

                TextMeshProUGUI footer = CreateText(name + "Footer", card,
                    inset, footerY, cardWidth - inset * 2f, footerHeight,
                    "<u>" + footerText + "</u>", 15f, VersionLink,
                    TextAlignmentOptions.MidlineLeft, false);

                if (onFooterClick != null)
                {
                    // 点击层是**单独一个物体**（见上）。
                    //
                    // 宽度**铺满整行**，不去量文字：
                    //   · 量出来的值不可靠（富文本标签、字体还没就绪都会让它短），
                    //     短了就是"点前半句能跳、点后面的群号不能跳"。
                    //   · 这一行本来就是"去哪找我们"，整条都算它的地盘并不别扭。
                    Image footerHit = CreateTopLeftRect(name + "FooterHit", card,
                        inset - 8f, footerY, cardWidth - inset * 2f + 16f, footerHeight)
                        .gameObject.AddComponent<Image>();
                    footerHit.color = new Color(0f, 0f, 0f, 0f);
                    footerHit.raycastTarget = true;
                    Button footerButton = footerHit.gameObject.AddComponent<Button>();
                    footerButton.targetGraphic = footerHit;
                    footerButton.transition = Selectable.Transition.None;
                    footerButton.onClick.AddListener(delegate { onFooterClick(); });
                }
            }

            window.Body = body;
            return window;
        }

        /// <summary>
        /// 「?」那颗按钮：开 / 关说明窗口。
        ///
        /// **说明窗口是第一个搬到 ImGui 的面板。** 桥接起来了就走它；
        /// 桥接没起来（cimgui 加载失败之类）时退回 uGUI 那扇 —— 老的留着
        /// 就是为了这条路断了也有得用，正常不会再走到。
        /// </summary>
        private void ToggleHelpPanel()
        {
            EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
            if (bridge != null && bridge.IsReady)
            {
                bridge.ToggleHelp();
                return;
            }
            if (helpWindow != null)
            {
                ToggleWindow(helpWindow);
            }
        }

        /// <summary>
        /// 打开版本说明窗口。两个入口：左上角那串版本号，以及换了版本后第一次开工作台。
        ///
        /// 它也已经搬到 ImGui（和说明窗口共用同一套文本窗口）。桥接起不来时
        /// 退回 uGUI 那扇，理由同 <see cref="ToggleHelpPanel"/>。
        /// </summary>
        private void ShowVersionWindow()
        {
            EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
            if (bridge != null && bridge.IsReady)
            {
                bridge.SetVersionVisible(true);
                return;
            }
            if (versionWindow != null && !versionWindow.Layer.gameObject.activeSelf)
            {
                ShowWindow(versionWindow, true);
            }
        }

        private void ToggleWindow(TextWindow window)
        {
            if (window == null || window.Layer == null)
            {
                return;
            }
            ShowWindow(window, !window.Layer.gameObject.activeSelf);
        }

        private void ShowWindow(TextWindow window, bool show)
        {
            window.Layer.gameObject.SetActive(show);
            if (show)
            {
                window.Layer.SetAsLastSibling();
                if (EventSystem.current != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
                return;
            }

            // 更新窗口关掉就算「看过了」—— 下次进来不再自动弹。
            if (ReferenceEquals(window, versionWindow))
            {
                MarkVersionNotesSeen();
            }
        }

        /// <summary>当前开着的那扇文本窗口，没有则 null。</summary>
        private TextWindow OpenWindow()
        {
            if (helpWindow != null && helpWindow.Layer != null && helpWindow.Layer.gameObject.activeSelf)
            {
                return helpWindow;
            }
            if (versionWindow != null && versionWindow.Layer != null && versionWindow.Layer.gameObject.activeSelf)
            {
                return versionWindow;
            }
            return null;
        }

        /// <summary>
        /// 记下「这个版本的更新内容看过了」。
        ///
        /// 存进 Settings.xml，所以换版本才会再弹一次 —— 判据是「上次看过的是不是
        /// 当前版本」，而不是一个「弹过了」的布尔。
        /// </summary>
        private void MarkVersionNotesSeen()
        {
            if (settings == null || string.Equals(settings.LastSeenVersion, ModVersion, StringComparison.Ordinal))
            {
                return;
            }
            settings.LastSeenVersion = ModVersion;
            Main.SaveSettings();
        }

        /// <summary>
        /// 更新后**第一次打开工作台**弹一次更新内容。
        ///
        /// 只在真的换了版本时弹（见 <see cref="MarkVersionNotesSeen"/>），
        /// 关掉就不再弹；之后想看只能点左上角那串版本号。
        /// </summary>
        private void ShowVersionNotesIfNew()
        {
            if (settings == null || versionWindow == null)
            {
                return;
            }
            if (string.Equals(settings.LastSeenVersion, ModVersion, StringComparison.Ordinal))
            {
                return;
            }
            ShowVersionWindow();
        }

        private void BuildMultiplayer(RectTransform parent)
        {
            RectTransform host = CreateTopLeftRect("MultiplayerLayer", parent, 0f, 0f,
                DesignWidth, DesignHeight);

            multiplayerPanel = host.gameObject.AddComponent<MultiplayerPanel>();
            multiplayerPanel.Initialize(host, uiFont, GetRoundedSprite(24, CornerLarge, false, 0f));
        }

        private void BuildBrowser(RectTransform parent)
        {
            float height = PreviewPanelHeight;
            Image panel = CreateRoundedPanel("ProjectWorkspace", parent, OuterMargin, PreviewPanelY, previewLayout.LeftPanelWidth, height, Panel, CornerLarge);
            panel.raycastTarget = true;
            AddPanelBorder(panel, CornerLarge);
            RectTransform root = panel.rectTransform;
            browserPanel = root;
            float innerWidth = previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset;
            float viewHeight = height - BrowserViewY - PanelInnerInset;

            // 左栏只留一列，顶部两个标签在「装饰物」和「媒体」之间切换。
            //
            // 原来是并排两列：装饰物占四成，媒体占剩下的。左栏本来就只有三百来
            // 像素，劈成两半之后两边都太窄——装饰物一行塞不下名字加三个按钮，
            // 媒体格只看得见两小格。而这两样东西不会同时用：要么在摆装饰物，
            // 要么在挑图。让它们轮流占满整宽，比同时挤着强。
            BuildBrowserTabs(root);

            // 媒体页
            Image mediaImage = CreateImage("MediaView", root, BrowserContentX, BrowserViewY, innerWidth, viewHeight, Color.clear);
            mediaImage.raycastTarget = true;
            mediaPanel = mediaImage.rectTransform;
            CreateMediaSearchControls(mediaPanel, innerWidth, BrowserSearchY);

            float mediaListY = BrowserListY;
            float mediaListHeight = Mathf.Max(32f, viewHeight - mediaListY - 8f);
            mediaScroll = CreateProjectScroll("MediaScroll", mediaPanel, 8f, mediaListY, innerWidth - 16f, mediaListHeight, out mediaViewport, out mediaContent);
            mediaScroll.onValueChanged.AddListener(OnMediaScrollChanged);
            // 张数不再占一整行标题——标签已经写明这里是媒体，标题只剩一个计数，
            // 放在搜索行右端就够，还省下一行高度给网格。
            mediaTitle = CreateText("MediaCount", mediaPanel, 0f, BrowserSearchY + 2f, MediaCountWidth, 20f,
                string.Empty, UiType.Caption, AccentSecondary, TextAlignmentOptions.MidlineRight, false);

            EnsureMediaCards(24);
            RefreshProjectWorkspace(true);

            // 装饰物页 —— 和媒体页同一块位置、同一个尺寸，同一时刻只显示一个。
            // 依然只是一块透明可点击区域，不是「面板里的面板」：层次靠标签和
            // 那条页眉线给出，不靠再套一层背景和边框。
            Image decoImage = CreateImage("DecorationView", root, BrowserContentX, BrowserViewY, innerWidth, viewHeight, Color.clear);
            decoImage.raycastTarget = true;
            decoView = decoImage.rectTransform;
            decoList = decoImage.gameObject.AddComponent<EditorDecorationList>();
            decoList.Initialize(decoImage.rectTransform, uiFont, GetRoundedSprite(24, CornerSmall, false, 0f), innerWidth);
            // 这里必须传**它相对面板的绝对坐标**，不能传 (0,0)：SetPaneRect 会把
            // 面板直接摆到给定位，传 (0,0) 等于把装饰物页顶到面板左上角，
            // 正好压在标签行上。坐标只在这里给一次，CreateImage 那个位置随即被覆盖。
            decoList.SetPaneRect(BrowserContentX, BrowserViewY, innerWidth, viewHeight);

            SetBrowserTab(BrowserPane.Decorations, LevelEventType.None);
        }

        private const float BrowserTabWidth = 80f;
        /// <summary>
        /// 右缘那条「还有东西」的提示留多宽。
        ///
        /// 要**细**：留着是为了让人看出这条能横滚，宽了就变成一大块半拉按钮，
        /// 比不留还难看。见 LayoutBrowserTabs 的排法。
        /// </summary>
        private const float TabSliverWidth = 12f;
        private const float BrowserTabGap = 4f;
        /// <summary>
        /// 左栏内容的左缘。和别的面板同一条规矩：竖条之后才是内容，
        /// 所以标签和下面两页都从这个 x 起，而不是面板内边距那个 x。
        /// </summary>
        private const float BrowserContentX = HeadingTextInset;
        /// <summary>
        /// 标签行之下、内容起点的纵坐标。
        ///
        /// 标签行本身就占着别的面板页眉那一行（y=0 起，高 <see cref="PanelHeadingHeight"/>），
        /// 页眉线也落在同一个 <see cref="PanelHeadingRule"/> 上，于是左栏、预览、参数
        /// 三条页眉线正好在同一条水平线上。
        /// </summary>
        private const float BrowserViewY = PanelHeadingRule + 8f;
        private const float BrowserSearchY = 6f;
        private const float BrowserListY = BrowserSearchY + MediaSearchRowHeight + 6f;
        private const float MediaSearchInset = 12f;
        private const float MediaRefreshWidth = 30f;
        private const float MediaCountWidth = 48f;

        private void BuildBrowserTabs(RectTransform root)
        {
            CreateHeadingBand("HeadingBand", root, PanelHeadingHeight);
            CreateHeadingBar("HeadingBar", root, PanelHeadingInset);

            // 这排标签是一条**可横滚的带子**：装饰物 / 媒体 / 原生那七个设置页，
            // 一共九项，横着排不下这 357px 的栏宽。
            //
            // 裁剪靠宿主上的 RectMask2D —— 没有它就不叫"滚"，内容会直接
            // 画到面板外面去。
            browserTabStripHost = CreateTopLeftRect("BrowserTabStrip", root,
                BrowserContentX, 0f, BrowserTabWidth, PanelHeadingHeight);
            browserTabStripHost.gameObject.AddComponent<RectMask2D>();

            browserTabStripContent = CreateTopLeftRect("Content", browserTabStripHost,
                0f, 0f, BrowserTabWidth, PanelHeadingHeight);

            browserTabStrip = browserTabStripHost.gameObject.AddComponent<HorizontalButtonStrip>();
            browserTabStrip.Configure(browserTabStripContent);

            CreateHeadingRule("BrowserRule", root, PanelHeadingRule);
            LayoutBrowserTabs();
        }

        /// <summary>
        /// 重建那排标签按钮：装饰物 / 媒体 / <see cref="SettingsPages"/> 那七页。
        ///
        /// 名字是**写死的中文常量**，不去原生那排页签上抠 —— 抠过，抠出来
        /// 全是「1」：那排页签里的第一个文本子物体是**计数徽标**（这类事件有几个），
        /// 不是标题。
        /// </summary>
        private void RebuildBrowserTabs()
        {
            if (browserTabButtons.Count == 2 + SettingsPages.Length)
            {
                return;
            }

            for (int i = 0; i < browserTabButtons.Count; i++)
            {
                if (browserTabButtons[i] != null)
                {
                    Destroy(browserTabButtons[i].gameObject);
                }
            }
            browserTabButtons.Clear();
            browserTabLabels.Clear();
            browserTabPages.Clear();

            AddBrowserTab("装饰物", BrowserPane.Decorations, LevelEventType.None);
            AddBrowserTab("媒体", BrowserPane.Media, LevelEventType.None);
            for (int i = 0; i < SettingsPages.Length; i++)
            {
                AddBrowserTab(SettingsPageNames[i], BrowserPane.Settings, SettingsPages[i]);
            }
        }

        private void AddBrowserTab(string title, BrowserPane pane, LevelEventType page)
        {
            BrowserPane capturedPane = pane;
            LevelEventType capturedPage = page;

            TextMeshProUGUI label;
            Button button = CreateButton("BrowserTab" + browserTabButtons.Count, browserTabStripContent,
                0f, 0f, BrowserTabWidth, PanelHeadingHeight, title, false,
                delegate { SetBrowserTab(capturedPane, capturedPage); }, out label);
            label.fontSize = UiType.Body;
            // 和别的面板标题一样上提：中文标题在拉丁度量的字体里会往下坐，
            // 不提的话它和左边那条竖条的几何中心差一两个像素。
            SetTopLeftRect(label.rectTransform, 0f, -HeadingTextLift, BrowserTabWidth, PanelHeadingHeight);

            // **圆角矩形。** `CreateButton` 默认给的是方角贴图，而工作台里
            // 别的控件（输入框、开关、面板）全是圆角 —— 这排方角是唯一一处，
            // 看着像没画完。九宫格切好的贴图，圆角半径不会跟着按钮一起拉伸。
            Image face = button.targetGraphic as Image;
            if (face != null)
            {
                face.sprite = GetRoundedSprite(24, CornerSmall, false, 0f);
                face.type = Image.Type.Sliced;
            }

            browserTabButtons.Add(button);
            browserTabLabels.Add(label);
            browserTabPages.Add(page);
        }

        private void LayoutBrowserTabs()
        {
            if (browserTabStripHost == null || browserTabStripContent == null)
            {
                return;
            }

            // 按钮按原生那几页现建。放在这里而不是只在构建时建一次：工作台是
            // F8 时建的，那一刻原生设置面板未必已经 Init 完 —— 只在构建时建的话
            // 会把"只有装饰物、媒体两个按钮"这个状态定死，之后再也补不上。
            // 数量没变时这一步只做一次比较。
            RebuildBrowserTabs();

            // 构建那一刻 browserPanel 还是零尺寸，量不到宽度。这时现算一次 ——
            // CalculatePreviewLayout 是纯函数，只依赖屏幕尺寸和那几个常量，
            // 任何时候调都安全。不这么兜的话，首次进来这条带子会停在
            // 一个 40px 宽的壳里，要等用户拖一次时间轴高度才恢复。
            float panelWidth = browserPanel != null && browserPanel.rect.width > 1f
                ? browserPanel.rect.width
                : CalculatePreviewLayout().LeftPanelWidth;
            if (panelWidth < 1f)
            {
                return;
            }

            // **让「N 颗完整的 + 一条很细的」正好铺满可用宽度。**
            //
            // 两个要求合起来只能这么做：
            //   · 右边要留一条**细边**，用户才知道这条能横着滚 —— 整好排满
            //     就是"排到头了"，看不出来还有东西。
            //   · 而那条边**不能宽**：切半颗会留下一大块半拉按钮，比不留还难看。
            //
            // 所以不是把按钮钉死在 80，而是**反过来解按钮宽度**：先按当前宽度
            // 定下能有几颗完整的，再把宽度摊平到正好铺满，只留一条
            // TabSliverWidth 的缝给下一颗。
            //
            // 内容是左对齐的，所以停在最左时第一颗永远完整，切开的那颗只出现在右边。
            float available = Mathf.Max(48f + BrowserTabGap,
                panelWidth - BrowserContentX - PanelHeadingInset);

            // **数「放得下几颗」用的是常量 80，不是上一次算出来的宽度。**
            //
            // 原来拿 `browserTabWidth` 当步长，而它又是从这里反解出来的 ——
            // 成了个反馈环，存在好几个稳定不动点。其中一个是"只放得下一颗"：
            // 一旦落进去，宽度变成 (可用宽-12)/1-6，而那个宽度又正好只容得下
            // 一颗，于是自己把自己锁死在那儿（表现就是第一颗按钮长得离谱）。
            // 步长取常量之后没有反馈，也就没有第二个不动点。
            const float nominalStep = BrowserTabWidth + BrowserTabGap;
            int wholeButtons = Mathf.Max(1,
                Mathf.FloorToInt((available - TabSliverWidth + BrowserTabGap) / nominalStep));
            browserTabWidth = Mathf.Max(48f,
                (available - TabSliverWidth) / wholeButtons - BrowserTabGap);

            SetTopLeftRect(browserTabStripHost, BrowserContentX, 0f, available, PanelHeadingHeight);

            float x = 0f;
            for (int i = 0; i < browserTabButtons.Count; i++)
            {
                if (browserTabButtons[i] != null)
                {
                    SetTopLeftRect(browserTabButtons[i].GetComponent<RectTransform>(),
                        x, 0f, browserTabWidth, PanelHeadingHeight);
                    if (i < browserTabLabels.Count && browserTabLabels[i] != null)
                    {
                        // 标签宽度要跟着按钮一起变，不然文字会在窄按钮里被挤掉。
                        SetTopLeftRect(browserTabLabels[i].rectTransform, 0f, -HeadingTextLift,
                            browserTabWidth, PanelHeadingHeight);
                    }
                }
                x += browserTabWidth + BrowserTabGap;
            }

            RefreshBrowserTabPalette();

            if (browserTabStrip != null)
            {
                browserTabStrip.SetBounds(available, Mathf.Max(0f, x - BrowserTabGap));
            }
        }

        /// <summary>
        /// 左栏那排按钮要列哪几页，**按用户要的顺序**。
        ///
        /// 顺序和原生那排不一样：原生是先「关卡设置」再「歌曲设置」，
        /// 这里按需求换过来。
        ///
        /// 这张表是**白名单**，不是"把原生那排抄一遍" —— 原生面板的
        /// `panelsList` 里可能还混着别的页（编辑器备注之类），照单全收会多出按钮。
        /// </summary>
        private static readonly LevelEventType[] SettingsPages =
        {
            LevelEventType.SongSettings,
            LevelEventType.LevelSettings,
            LevelEventType.TrackSettings,
            LevelEventType.BackgroundSettings,
            LevelEventType.CameraSettings,
            LevelEventType.MiscSettings
        };

        /// <summary>和 <see cref="SettingsPages"/> 一一对应的显示名。</summary>
        private static readonly string[] SettingsPageNames =
        {
            "歌曲设置", "关卡设置", "轨道设置", "背景设置",
            "摄像头设置", "杂项设置"
        };

        /// <summary>
        /// 切换左栏显示哪一页。
        ///
        /// 两页各自整块开关，而不是挪到看不见的地方：面板没有遮罩，挪出去的
        /// 内容照样会画到面板外面去。
        /// </summary>
        private void SetBrowserTab(BrowserPane pane, LevelEventType page)
        {
            BrowserPane previous = browserPane;
            browserPane = pane;
            if (pane == BrowserPane.Settings)
            {
                selectedSettingsPage = page;
            }

            bool showMedia = pane == BrowserPane.Media;
            bool showSettings = pane == BrowserPane.Settings;

            if (mediaPanel != null)
            {
                mediaPanel.gameObject.SetActive(showMedia);
            }
            if (decoView != null)
            {
                if (decoList != null)
                {
                    // 被标签切走只是暂时隐藏——用户是去看一眼媒体，切回来时
                    // 「全部隐藏/锁定」该还在。工作台整个关掉才轮到正常收尾。
                    decoList.KeepBatchOnDisable = pane != BrowserPane.Decorations;
                }
                decoView.gameObject.SetActive(pane == BrowserPane.Decorations);
            }

            // **设置页是把原生面板借进来摆着**，不是我们自己画的一页，所以进出
            // 这一页要成对地借 / 还。离开时不还的话，工作台一关那个面板还留在
            // 我们（已经隐藏的）界面里 —— 原生编辑器那边它就没了。
            if (showSettings)
            {
                ShowSettingsPane();
            }
            else if (previous == BrowserPane.Settings)
            {
                HideSettingsPane();
            }

            RefreshBrowserTabPalette();

            // 媒体页刚从隐藏里回来：它在隐藏期间不推进缩略图队列，卡片位置也
            // 是按隐藏时的视口算的。这里重排一次，把位置和要加载的图都补上。
            if (showMedia)
            {
                RebuildVisibleMedia(false);
            }
        }

        /// <summary>
        /// 把那排按钮的高亮刷一遍。
        ///
        /// 选中的那一个是实心紫、文字反过来用深色。金色留给标题和竖条——
        /// 分段控件是"能点的"，按分工该穿紫。
        ///
        /// 设置页那一排是**互斥**的：同时只有一个亮着，亮的就是当前显示的那一页。
        /// </summary>
        private void RefreshBrowserTabPalette()
        {
            for (int i = 0; i < browserTabButtons.Count && i < browserTabPages.Count; i++)
            {
                bool active = IsBrowserTabActive(i);
                SetSegmentPalette(browserTabButtons[i], active, Accent);
                if (browserTabLabels[i] != null)
                {
                    browserTabLabels[i].color = active ? TextOnAccent : TextMuted;
                }
            }
        }

        private bool IsBrowserTabActive(int index)
        {
            if (index == 0)
            {
                return browserPane == BrowserPane.Decorations;
            }
            if (index == 1)
            {
                return browserPane == BrowserPane.Media;
            }
            return browserPane == BrowserPane.Settings
                && browserTabPages[index] == selectedSettingsPage;
        }

        /// <summary>
        /// 设置页那一份参数栏：**同一个 `EditorPropertyPanel`，另开一个实例**，
        /// 摆在左栏、喂的是一页设置而不是一个事件。
        ///
        /// 为什么不用另写一套设置控件：设置页在游戏里**本来就是 `LevelEvent`**
        /// —— `LevelData.songSettings` 那些字段的类型就是它，元数据取自
        /// `GCS.settingsInfo`。所以那套行 / 控件 / 撤销一个字都不用改。
        ///
        /// 也不去搬原生那个设置面板：它是整块排好的 uGUI，搬进来要跟它的
        /// 滑出补间、锚点、布局打架，而且搬完还得原样还回去。自己渲染没有这些账。
        /// </summary>
        private void EnsureSettingsPane()
        {
            if (settingsPropertyPanel != null || browserPanel == null)
            {
                return;
            }

            // **外壳一层容器。**
            //
            // AddPanelBorder 那族（投影 + 描边）不是在面板上挂组件，而是在
            // **同级**垫几个跟着面板走的兄弟物体（BorderFollower）。所以只把
            // 面板 SetActive(false) 是藏不干净的 —— 那几层会留在原地，糊在
            // 装饰物/媒体页上，看起来就是"整页多了一层灰"。套一层容器，
            // 藏容器就等于全藏。
            settingsPaneRoot = CreateTopLeftRect("SettingsPaneRoot", browserPanel,
                BrowserContentX, BrowserViewY, 100f, 100f);

            settingsPane = CreateRoundedPanel("SettingsPane", settingsPaneRoot,
                0f, 0f, 100f, 100f, Panel, CornerLarge);
            settingsPane.raycastTarget = true;
            AddPanelBorder(settingsPane, CornerLarge);

            settingsPropertyPanel = settingsPane.gameObject.AddComponent<EditorPropertyPanel>();
            settingsPropertyPanel.Initialize(settingsPane.rectTransform, uiFont, this);
            settingsPaneRoot.gameObject.SetActive(false);
        }

        private void ShowSettingsPane()
        {
            // **被 ImGui 版接管时整个不摆。**
            //
            // 拦在这里而不是只拦 SetBrowserTab：借/还的配对有好几个入口，
            // 漏一个就会把原生设置页留在我们（已经隐藏的）界面里，
            // 而原生编辑器那边它反倒没了。
            if (settingsPaneTakenOver)
            {
                return;
            }

            EnsureSettingsPane();
            if (settingsPropertyPanel == null)
            {
                return;
            }

            settingsPaneRoot.gameObject.SetActive(true);
            LayoutSettingsPane();
            settingsPropertyPanel.ShowSettingsPage(FindSettingsPage(selectedSettingsPage));
        }

        private void HideSettingsPane()
        {
            if (settingsPropertyPanel == null || settingsPaneRoot == null)
            {
                return;
            }

            settingsPropertyPanel.ClearSettingsPage();
            settingsPaneRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// 把设置页那一步重摆一次。
        ///
        /// 尺寸一律从 `browserPanel` 现读，和 <see cref="RelayoutProjectWorkspace"/>
        /// 给另外两页用的是同一份来源 —— 在这里再抄一遍那串常量，
        /// 改一处漏一处的时候两个页的宽度就会不一致。
        /// </summary>
        private void LayoutSettingsPane()
        {
            if (settingsPane == null || browserPanel == null)
            {
                return;
            }

            float innerWidth = Mathf.Max(64f, browserPanel.rect.width - BrowserContentX - PanelHeadingInset);
            float viewHeight = Mathf.Max(64f, browserPanel.rect.height - BrowserViewY - PanelInnerInset);
            SetTopLeftRect(settingsPaneRoot, BrowserContentX, BrowserViewY, innerWidth, viewHeight);
            SetTopLeftRect(settingsPane.rectTransform, 0f, 0f, innerWidth, viewHeight);

            if (settingsPropertyPanel != null)
            {
                settingsPropertyPanel.Relayout(innerWidth, viewHeight);
            }
        }

        /// <summary>
        /// 取某一类设置页的 `LevelEvent`。
        ///
        /// 原生把八页设置**就挂在 levelData 上**（`songSettings` / `levelSettings`
        /// / ……），类型本来就是 `LevelEvent` —— 不用自己造，取出来直接喂参数栏。
        /// </summary>
        private static LevelEvent FindSettingsPage(LevelEventType type)
        {
            scnEditor editor = scnEditor.instance;
            LevelData levelData = editor == null ? null : editor.levelData;
            if (levelData == null)
            {
                return null;
            }

            switch (type)
            {
                case LevelEventType.LevelSettings:
                    return levelData.levelSettings;
                case LevelEventType.SongSettings:
                    return levelData.songSettings;
                case LevelEventType.TrackSettings:
                    return levelData.trackSettings;
                case LevelEventType.BackgroundSettings:
                    return levelData.backgroundSettings;
                case LevelEventType.CameraSettings:
                    return levelData.cameraSettings;
                case LevelEventType.MiscSettings:
                    return levelData.miscSettings;
                case LevelEventType.EventSettings:
                    return levelData.eventSettings;
                case LevelEventType.DecorationSettings:
                    return levelData.decorationSettings;
                default:
                    return null;
            }
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
                SetTopLeftRect(rect, 0f, 0f, 100f, MediaCardHeight - MediaGridRowGap);
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
                // 图在卡片内居中、字在下方——两者都是定尺寸的对齐，
                // 所以位置和大小都写死，跟着卡片走。尺寸每帧由
                // UpdateMediaCards 重算，卡片池是复用的，布局随时会变。
                //
                // 缩略图框是正方形：它比卡片矮一截，宽度留出 LabelHeight，
                // 而卡片的宽高比不固定（面板能拉能缩），所以正方形的做法是
                // 「取宽高中较小的那个」，而不是钉死一个边长。
                RawImage image = CreateRawImage("Thumbnail", rect, 6f, 5f, 90f, 80f, ThumbWell);
                image.raycastTarget = false;
                // 文件名在缩略图下面、单行居中。9.5 号字配 Ellipsis，
                // 90px 宽的格子里放不下完整文件名就截断——六列格子本来
                // 就没打算放下全名，悬停会有完整名字的提示留给以后加。
                TextMeshProUGUI label = CreateText("Label", rect, 4f, MediaCardHeight - MediaGridRowGap - MediaCardLabelHeight - 2f, 90f, MediaCardLabelHeight, string.Empty, 11f, TextSecondary, TextAlignmentOptions.Center, false);
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
                selectedMediaPath = null;
                projectDataDirty = false;
                RebuildVisibleMedia();
                return;
            }

            if (projectSession != null && projectSession.RefreshMediaIndex())
            {
                ReleaseMediaTextures();
                RebuildVisibleMedia();
            }
            projectDataDirty = false;
        }

        private void TickProjectWorkspace()
        {
            if (!visible || mediaContent == null)
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
            // 媒体页不在前台时不推进缩略图队列。那一步是在解码磁盘上的图片，
            // 一帧好几张；页面看不见的时候没必要付这个代价。数据索引照常刷新，
            // 切回来的那一帧再把图补上（RebuildVisibleMedia 会置脏标志）。
            if (browserPane != BrowserPane.Media)
            {
                return;
            }

            if (mediaCardsDirty)
            {
                UpdateMediaCards();
            }
            ProcessMediaThumbnailQueue();
        }

        /// <summary>
        /// 搜索行（输入框 + 计数 + 刷新）的位置。宽度全部由媒体页宽推出来，
        /// 而媒体页宽跟着左栏走、左栏又跟着舞台走，所以每次重排都要重跑一遍。
        /// </summary>
        private void LayoutMediaSearchControls(float viewWidth, float viewY)
        {
            if (mediaSearchField == null)
            {
                return;
            }

            const float searchHeight = 24f;
            float searchWidth = Mathf.Max(40f,
                viewWidth - MediaSearchInset * 2f - MediaRefreshWidth - MediaCountWidth - 12f);
            SetTopLeftRect(mediaSearchField.GetComponent<RectTransform>(), MediaSearchInset, viewY, searchWidth, searchHeight);
            if (mediaTitle != null)
            {
                SetTopLeftRect(mediaTitle.rectTransform,
                    viewWidth - MediaSearchInset - MediaRefreshWidth - 6f - MediaCountWidth,
                    viewY + 2f,
                    MediaCountWidth,
                    20f);
            }
            if (mediaRefreshButton != null)
            {
                SetTopLeftRect(
                    mediaRefreshButton.GetComponent<RectTransform>(),
                    viewWidth - MediaSearchInset - MediaRefreshWidth,
                    viewY,
                    MediaRefreshWidth,
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

            int rowCount = Mathf.Max(1, Mathf.CeilToInt(visibleProjectMedia.Count / (float)GetMediaColumnCount()));
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

        private void RebuildVisibleMedia(bool resetScroll = true)
        {
            ClearMediaThumbnailQueue();
            visibleProjectMedia.Clear();
            int totalMedia = 0;
            if (projectSession != null)
            {
                totalMedia = projectSession.Media.Count;
                for (int i = 0; i < projectSession.Media.Count; i++)
                {
                    EditorProjectMediaItem item = projectSession.Media[i];
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
                // 只报张数。这里原先是「媒体 · N」的整行标题，可标签已经写明
                // 这页是媒体，剩下的信息就只有个数——给它一行位置太浪费，
                // 挤在搜索行右端刚好。搜索生效时分子是命中的张数。
                mediaTitle.text = string.IsNullOrEmpty(mediaSearchQuery)
                    ? visibleProjectMedia.Count.ToString()
                    : visibleProjectMedia.Count + " / " + totalMedia;
            }
            if (mediaContent != null)
            {
                int columns = GetMediaColumnCount();
                int rows = Mathf.CeilToInt(visibleProjectMedia.Count / (float)columns);
                mediaContent.sizeDelta = new Vector2(
                    0f,
                    Mathf.Max(mediaViewport.rect.height, MediaGridPadding * 2f + rows * MediaCardHeight)
                );
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

        /// <summary>当前视口宽度下能排几列。视口还没建好时按目标列数算。</summary>
        private int GetMediaColumnCount()
        {
            if (mediaViewport == null)
            {
                return MediaGridTargetColumns;
            }
            return MediaColumnsFor(mediaViewport.rect.width);
        }

        /// <summary>
        /// 给定宽度能排几列。
        ///
        /// **抽成静态的是给 ImGui 那版用的** —— 它用自己那块的宽度算，不经过
        /// uGUI 的视口，但列数公式只能有一份，不然两边排出来的列数对不上。
        /// </summary>
        internal static int MediaColumnsFor(float viewportWidth)
        {
            int columns = Mathf.FloorToInt(
                (Mathf.Max(1f, viewportWidth) - MediaGridPadding * 2f + MediaGridColumnGap)
                / (MediaGridMinColumnWidth + MediaGridColumnGap));
            return Mathf.Clamp(columns, 1, MediaGridTargetColumns);
        }

        private void UpdateMediaCards()
        {
            if (mediaScroll == null || mediaContent == null || mediaViewport == null)
            {
                return;
            }

            float viewportWidth = Mathf.Max(1f, mediaViewport.rect.width);
            int columns = GetMediaColumnCount();
            float cardWidth = (viewportWidth - MediaGridPadding * 2f - MediaGridColumnGap * (columns - 1)) / columns;
            int totalItems = visibleProjectMedia.Count;
            int totalRows = Mathf.Max(1, Mathf.CeilToInt(totalItems / (float)columns));

            int firstRow = Mathf.Clamp(
                Mathf.FloorToInt(Mathf.Max(0f, mediaContent.anchoredPosition.y) / MediaCardHeight),
                0,
                Mathf.Max(0, totalRows - 1)
            );
            int visibleRows = Mathf.CeilToInt(mediaViewport.rect.height / MediaCardHeight) + 2;
            EnsureMediaCards(Mathf.Max(columns, (visibleRows + 1) * columns));

            bool bindingChanged = firstRow != mediaCardsBoundFirstRow
                || totalItems != mediaCardsBoundVisibleCount
                || mediaCards.Count != mediaCardsBoundPoolCount
                || !Mathf.Approximately(cardWidth, mediaCardsBoundWidth)
                || !string.Equals(selectedMediaPath, mediaCardsBoundSelectionPath, StringComparison.OrdinalIgnoreCase);
            mediaCardsDirty = false;
            if (!bindingChanged)
            {
                return;
            }

            mediaCardsBoundFirstRow = firstRow;
            mediaCardsBoundVisibleCount = totalItems;
            mediaCardsBoundPoolCount = mediaCards.Count;
            mediaCardsBoundWidth = cardWidth;
            mediaCardsBoundSelectionPath = selectedMediaPath;
            // 只绑当前可见的那几行。池子按行循环使用，滚动时
            // 每张卡片重新指向新的下标——所以手上有 card.BoundIndex
            // 的地方（拖放、选中）必须当它是「此刻」的值，不能缓存。
            int firstIndex = firstRow * columns;
            float labelTop = MediaCardHeight - MediaGridRowGap - MediaCardLabelHeight;
            for (int i = 0; i < mediaCards.Count; i++)
            {
                int itemIndex = firstIndex + i;
                MediaCard card = mediaCards[i];
                if (itemIndex >= totalItems)
                {
                    UnbindMediaCard(card);
                    continue;
                }
                EditorProjectMediaItem item = visibleProjectMedia[itemIndex];
                int cellRow = itemIndex / columns;
                int cellColumn = itemIndex % columns;
                card.BoundIndex = itemIndex;
                card.BoundPath = item.RelativePath;
                card.Rect.anchoredPosition = new Vector2(
                    MediaGridPadding + cellColumn * (cardWidth + MediaGridColumnGap),
                    -(MediaGridPadding + cellRow * MediaCardHeight));
                card.Rect.sizeDelta = new Vector2(cardWidth, MediaCardHeight - MediaGridRowGap);

                // 缩略图取正方形：宽度是格子宽减两侧内边距，高度是
                // 它上方到文件名之间的距离；两者取小，保证图不压到字上。
                // 只有在格子又窄又高的时候高度才是瓶颈，而那时
                // 图片更应该靠宽度定。
                float thumbnailSize = Mathf.Max(
                    16f,
                    Mathf.Min(cardWidth - MediaCardInset * 2f, labelTop - MediaCardInset)
                );
                card.ThumbBoxX = (cardWidth - thumbnailSize) * 0.5f;
                card.ThumbBoxY = MediaCardInset;
                card.ThumbBoxWidth = thumbnailSize;
                card.ThumbBoxHeight = thumbnailSize;

                // 把纹理按比例装进新框里，而不是直接把框赋给它。
                // 裸用 SetTopLeftRect 会把贴图拉伸填满，所以每次重新布局
                // 都会让图片被压成新框碰巧具有的那个宽高比。这里
                // 也是缩略图首次加载时应用等比适配的地方。
                LayoutMediaThumbnail(card);
                SetTopLeftRect(
                    card.Label.rectTransform,
                    MediaCardInset,
                    labelTop,
                    Mathf.Max(16f, cardWidth - MediaCardInset * 2f),
                    MediaCardLabelHeight);
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
                    LayoutMediaThumbnail(card);
                }
            }
        }

        private static void ApplyMediaThumbnailToCard(MediaCard card, MediaThumbnailCacheEntry entry)
        {
            card.Texture = entry == null ? null : entry.Texture;
            card.Image.texture = card.Texture;
            card.Image.color = entry == null || entry.Failed || card.Texture == null
                ? ThumbWell
                : Color.white;
        }

        private static void SetMediaThumbnailPlaceholder(MediaCard card, bool failed)
        {
            card.Texture = null;
            card.Image.texture = null;
            card.Image.color = failed
                ? ThumbWell
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
        /// <summary>
        /// 把纹理等比装进卡片上的缩略图方框，居中。
        ///
        /// 方框本身由调用方写进 card.ThumbBox*，这里只按它算图片的
        /// 实际位置和大小——图中图外都留一圈底色，看起来才像
        /// 「一张图放在一个槽里」，而不是被框裁掉一块。
        /// </summary>
        private static void LayoutMediaThumbnail(MediaCard card)
        {
            float availableWidth = Mathf.Max(1f, card.ThumbBoxWidth);
            float availableHeight = Mathf.Max(1f, card.ThumbBoxHeight);
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
                card.ThumbBoxX + (availableWidth - width) * 0.5f,
                card.ThumbBoxY + (availableHeight - height) * 0.5f,
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

            // 工具栏底色用 Band，和另外三块面板的标题条同一档。
            // 原来用的是面板色加 0.97 不透明度——和面板几乎一样，整条读不出来，
            // 预览画面顶上就是一片平的黑。
            Image toolbar = CreateRoundedPanel("Toolbar", root, 0f, 0f, width, 52f, Band, CornerLarge);
            toolbar.raycastTarget = true;
            previewToolbar = toolbar.rectTransform;
            previewHeadingBar = CreateHeadingBar("HeadingBar", root, PanelHeadingInset);
            previewHeading = CreateText("Heading", root, HeadingTextInset, HeadingRowY,
                220f, HeadingRowBoxHeight, "预览", UiType.Title, AccentSecondary,
                TextAlignmentOptions.MidlineLeft, true).rectTransform;
            // 预览面板顶部有一条 52px 的工具栏条，所以这个面板的分隔线该落在
            // 工具栏的下沿，而不是共用的标题高度——放在 27 会画进
            // 工具栏里，读起来是横穿它的一条杂线。全宽的理由
            // 与其他面板相同。
            previewHeadingRule = CreateHeadingRule("PreviewRule", root, 52f);

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
            Color viewportBorder = ThumbWell;
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

            // 和时间轴的车道数保持一致。这里踩过：写死的数字在加车道之后
            // 会让最后一个按钮永远出不来。
            const int laneCount = EditorTimelineView.LaneCount;
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
            CreateHeadingBand("HeadingBand", panel.rectTransform, PanelHeadingHeight);
            CreateHeadingBar("HeadingBar", panel.rectTransform, PanelHeadingInset);
            inspectorHeading = CreateText("Heading", panel.rectTransform, HeadingTextInset, HeadingRowY,
                previewLayout.RightPanelWidth - HeadingTextInset - PanelHeadingInset, HeadingRowBoxHeight,
                "参数", UiType.Title, AccentSecondary, TextAlignmentOptions.MidlineLeft, true).rectTransform;
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
            Image panel = CreateRoundedPanel("Timeline", parent, OuterMargin, panelY, TimelinePanelWidth, panelHeight, Panel, CornerLarge);
            panel.raycastTarget = true;
            AddPanelBorder(panel, CornerLarge);
            timelinePanel = panel.rectTransform;

            timelineView = panel.gameObject.AddComponent<EditorTimelineView>();
            timelineView.Initialize(panel.rectTransform, uiFont, this);

            // 把左栏那个装饰物列表接上。时间轴持有事件表，数据/选中的变化
            // 只有它知道，所以竖条搬走了仍要由它来通知刷新。
            if (decoList != null)
            {
                timelineView.AttachDecorationPane(decoList);
            }

            // 预览工具栏上的车道按钮要读时间轴，而 BuildPreview 早于本方法执行，
            // 那一趟拿不到 timelineView 就直接返回了。这里补建。
            if (previewToolbar != null)
            {
                BuildLaneFilterButtons(previewToolbar);
            }
        }

        /// <summary>
        /// 清掉 EventSystem 里记住的那个**按钮**。
        ///
        /// 用途只有一个：让 Space 之类的 Submit 键不会再点一次用户上次
        /// 点过的按钮。工作台里按钮从来不需要"键盘焦点"这个概念，
        /// 所以可以无条件清。
        ///
        /// **输入框必须放过。** 用户正在参数栏里打字时，
        /// currentSelectedGameObject 就是那个输入框，而
        /// <see cref="IsTextInputFocused"/> 也正是靠它判断的——
        /// 在这里清掉会让输入框当场失焦，表现是"打字打到一半光标没了"。
        /// </summary>
        private void ClearUiButtonSelection()
        {
            EventSystem system = EventSystem.current;
            if (system == null || uiRoot == null)
            {
                return;
            }
            GameObject selected = system.currentSelectedGameObject;
            if (selected == null)
            {
                return;
            }
            // 只清工作台自己建的：游戏那边可能正当使用着选中态。
            if (!selected.transform.IsChildOf(uiRoot.transform))
            {
                return;
            }
            // 输入框保留焦点。
            if (selected.GetComponentInParent<TMP_InputField>() != null)
            {
                return;
            }

            system.SetSelectedGameObject(null);
        }

        private void SetVisible(bool value)
        {
            visible = value;

            // 换了版本之后第一次打开才弹更新内容（见 ShowVersionNotesIfNew）。
            // 放这里而不是 F8 那一处：重开工作台、场景重载后重开，
            // 走的都是这条。
            if (visible)
            {
                ShowVersionNotesIfNew();
            }

            if (!visible)
            {
                HidePreviewHandoffImmediate();
                nonPreviewPointerCapture = false;
                ReleaseMediaTextures();
                RestorePreviewViewport();
                EditorPlaybackBridge.TryStopPlayback();
                // 收工台时要把「全部隐藏/锁定」解除掉，而这个豁免标记可能还
                // 停在「刚才是被标签切走的」状态上——不撤掉它，关掉工作台
                // 就会把隐藏的装饰物留在隐藏状态里。
                if (decoList != null)
                {
                    decoList.ReleaseBatch();
                }

                // 借来的原生设置面板**必须还回去**。它是原生编辑器的东西，
                // 我们只是把它摆到这儿显示；工作台一关（界面整棵隐藏），
                // 不还的话它在原生那边也跟着不见了。
                HideSettingsPane();
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

        internal void SetSceneSelectionStatus(string message)
        {

            // 这条消息以前被丢在地上没人管——只置了脏标志，所以
            // 场景交互报上来的任何东西都从来没有显示过。现在它驱动
            // 页眉读数，滚轮调整的实时值也显示在那里。
            if (statusLabel != null)
            {
                statusLabel.text = message ?? string.Empty;
            }
            RefreshStatusPill();
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
            // 底衬也要跟着刷新 —— 少了这一步，滚轮微调的读数会写进标签，
            // 但那块底衬还停在上一句的长度上（或者根本没显示）。
            RefreshStatusPill();
        }

        private TextMeshProUGUI statusLabel;
        private Image statusPill;

        // 状态读数的位置与底衬度量。
        private const float StatusLabelX = 620f;
        private const float StatusPillY = 14f;
        private const float StatusPillHeight = 26f;
        private const float StatusPillRadius = 8f;
        private static readonly Color StatusPillFill = new Color(1f, 1f, 1f, 0.06f);

        /// <summary>
        /// 把状态底衬贴到文字上。
        ///
        /// 读数是**左对齐**的、装在一个 900 宽的框里，所以底衬必须按
        /// **文字实际宽度**算 —— 直接用框宽会得到一条横贯顶栏的长条。
        /// </summary>
        private void RefreshStatusPill()
        {
            if (statusLabel == null || statusPill == null)
            {
                return;
            }

            string text = statusLabel.text;
            if (string.IsNullOrEmpty(text))
            {
                statusPill.gameObject.SetActive(false);
                return;
            }

            Vector2 preferred = statusLabel.GetPreferredValues(text, 0f, 0f);
            float width = Mathf.Clamp(preferred.x + 20f, 40f, 880f);
            SetTopLeftRect(statusPill.rectTransform, StatusLabelX - 10f, StatusPillY,
                width, StatusPillHeight);
            statusPill.gameObject.SetActive(true);
        }

        /// <summary>
        /// 报一条**会自己消失**的状态。
        ///
        /// 给 Ctrl+S 这类"动作有没有生效"的反馈用。普通的状态读数由场景
        /// 交互持续覆写，而这类消息报完就没人再写了 —— 不给个到期时间，
        /// 它会一直挂在顶栏上，反而让人以为卡住了。
        /// </summary>
        internal void SetTransientStatus(string message, float seconds = 3f)
        {
            SetSceneSelectionStatus(message);
            statusTransient = !string.IsNullOrEmpty(message);
            statusClearAt = Time.unscaledTime + Mathf.Max(0.5f, seconds);
        }

        private bool statusTransient;
        private float statusClearAt;


        internal bool IsTextInputFocused()
        {
            if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null)
            {
                return false;
            }

            TMP_InputField input = EventSystem.current.currentSelectedGameObject.GetComponentInParent<TMP_InputField>();
            // **还要这个输入框是活的。**
            //
            // 面板被 ImGui 版接管时是整个人 `SetActive(false)`，而 Unity 不会因此
            // 给输入框调 `OnDeselect` —— `isFocused` 就永远停在 true 上。于是这里
            // 一直报"正在打字"，把**撤销、重做、保存、删除、以及时间轴那一整族
            // 快捷键全挡在外面**（EditorPlaybackBridge 里那句 `return true`）。
            // 表现就是"按什么都没反应"，而且看起来和 ImGui 毫无关系。
            return input != null && input.isFocused && input.gameObject.activeInHierarchy;
        }

        /// <summary>
        /// 原生 transport 是不是正在放这一关（也就是"游玩中"）。
        ///
        /// 判据是 <c>scnEditor.playMode</c> 这个计算属性。它在<b>求值器驱动的预览</b>
        /// 下报 false —— 求值器不碰原生 transport 的字段，所以拖时间轴取景、
        /// 静态预览这些编辑动作都不会被误判成游玩（见
        /// <c>EditorPlaybackBridge.ReadSnapshot</c> 顶部那条注释）。
        /// 只有原生 transport 真的在跑时它才是 true。
        /// </summary>
        private static bool IsNativePlayModeRunning()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return false;
            }

            try
            {
                return editor.playMode;
            }
            catch (Exception)
            {
                // 读不到就当成"不在游玩"。判错的两个方向里，误拦会让 F8
                // 彻底失灵，误放最多多开一次面板 —— 所以往放行的方向倒。
                return false;
            }
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
            SyncBackgroundCameraRects(camera.rect);

            LogViewportState("applied", camera, left, bottom, width, height);
        }

        /// <summary>
        /// 预览期间，把**背景相机**也收进同一个矩形里。
        ///
        /// 自定义背景精灵（排序层 `Bg`）不是主相机画的，是
        /// **BGMovingCam**（`scrCamera.BGcam`）画的。原版正常播放时两台相机的
        /// rect 都是整窗口，取景自然一致；预览只把 rect 设给了主相机，
        /// 背景相机仍是整窗口——于是它把整份取景铺满整个窗口，而预览面板
        /// 只占窗口的一块（实测 `0.17,0.23,0.65x0.65`），面板里看到的就是
        /// 那份取景的**一个偏心裁切**：面板占窗口多大，就裁掉多少。
        /// 表现就是"背景没按预览窗口来"，一改窗口大小它就变。
        ///
        /// 注意精灵自己的尺寸**是对的**（实测 `size` 恒等于
        /// `2 * ortho * aspect`），所以别去动 `SetCustomBG`——错的是
        /// "哪个相机、铺在多大一块上"。
        ///
        /// `Bgcamstatic` 一并处理：它画的是背景底色，也是同一套取景逻辑。
        /// 不动 `Overlaycam`——那是渲染纹理那条路要用的。
        /// </summary>
        private void SyncBackgroundCameraRects(Rect rect)
        {
            scrCamera rig = scrCamera.instance;
            if (rig == null)
            {
                return;
            }

            Camera[] backgrounds = { rig.BGcam, rig.Bgcamstatic };
            for (int i = 0; i < backgrounds.Length; i++)
            {
                Camera background = backgrounds[i];
                if (background == null || background == previewCamera)
                {
                    continue;
                }

                if (!previewBackgroundCameras.Contains(background))
                {
                    previewBackgroundCameras.Add(background);
                    previewBackgroundOriginalRects.Add(background.rect);
                }
                background.rect = rect;
            }
        }

        private void RestoreBackgroundCameraRects()
        {
            for (int i = 0; i < previewBackgroundCameras.Count; i++)
            {
                Camera background = previewBackgroundCameras[i];
                if (background != null)
                {
                    background.rect = previewBackgroundOriginalRects[i];
                }
            }

            previewBackgroundCameras.Clear();
            previewBackgroundOriginalRects.Clear();
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

            float chrome = OuterMargin * 2f + PanelGap * 2f;

            // 舞台大小只由可用高度决定。它**不**跟着两侧面板走：
            // 反过来的话，两栏一变舞台就要重算，而舞台一变两栏又要重算，
            // 是个绕不出去的关系。高度是这条链上唯一的外生量，从它出发
            // 算一次就完了。
            //
            // 老写法还有个实际后果：右栏用 Mathf.Max(290, 余量) 兜底，
            // 余量不够也硬占 290，于是参数栏有一截伸到屏幕外面——
            // 舞台上方的视口又是按 previewLayout 定位的，整个工作台
            // 会跟着往右偏。两栏等宽之后这个兜底就不存在了。
            float stageHeight = PreviewStageAvailableHeight;
            float stageWidth = stageHeight * screenAspect;

            // 横向余量由两栏平分。这里不再给某一边「多分一点」——
            // 分了舞台就偏心了。
            float panelWidth = (DesignWidth - chrome - stageWidth) * 0.5f;

            // 兜底：屏幕特别宽时舞台会把两侧挤到没有。这时把舞台收回来，
            // 两栏一起保到最小宽度。1920 设计宽度下不会走到这里。
            float minimumStageWidth = DesignWidth - chrome - MinimumSidePanelWidth * 2f;
            if (minimumStageWidth > 1f && stageWidth > minimumStageWidth)
            {
                stageWidth = minimumStageWidth;
                stageHeight = stageWidth / screenAspect;
                panelWidth = MinimumSidePanelWidth;
            }

            panelWidth = Mathf.Max(MinimumSidePanelWidth, panelWidth);
            float stageX = OuterMargin + panelWidth + PanelGap;

            return new PreviewLayout
            {
                LeftPanelWidth = panelWidth,
                RightPanelWidth = panelWidth,
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
            // 两个标题用**同一个 y**，否则它们不在一条水平线上。
            // 位置由这里唯一负责，创建时给的值会被覆盖，见 HeadingRowY 的说明。
            SetTopLeftRect(previewHeading, HeadingTextInset, HeadingRowY,
                160f, HeadingRowBoxHeight);
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
            SetTopLeftRect(inspectorHeading, HeadingTextInset, HeadingRowY,
                previewLayout.RightPanelWidth - HeadingTextInset - PanelHeadingInset, HeadingRowBoxHeight);
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
            if (browserPanel == null || mediaPanel == null)
            {
                return;
            }

            float innerWidth = previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset;
            float viewHeight = Mathf.Max(64f, previewHeight - BrowserViewY - PanelInnerInset);

            LayoutBrowserTabs();

            SetTopLeftRect(mediaPanel, BrowserContentX, BrowserViewY, innerWidth, viewHeight);
            LayoutMediaSearchControls(innerWidth, BrowserSearchY);
            SetTopLeftRect(mediaScroll.GetComponent<RectTransform>(), 8f, BrowserListY,
                innerWidth - 16f, Mathf.Max(32f, viewHeight - BrowserListY - 8f));

            // 两页共用同一块位置，谁显示由标签决定；尺寸要一起跟着走，
            // 否则压扁时间轴之后另一页会停在旧高度上。
            // 这里也是踩过坑的地方：初始构建按一套尺寸排、拖动重排走另一套时，
            // 表现就是「首次进来正常，一拖时间轴两页错位」。
            //
            // 装饰物页的位置由 SetPaneRect 一并给出（它就是那块页面的尺寸入口），
            // 不要再单独摆一次 decoView——两份坐标迟早会不一致。
            if (decoList != null)
            {
                decoList.SetPaneRect(BrowserContentX, BrowserViewY, innerWidth, viewHeight);
            }

            // 设置页那一份也要跟着重摆：它不走我们这套 SetPaneRect，
            // 左栏一改尺寸它就会停在旧大小上。
            if (browserPane == BrowserPane.Settings)
            {
                LayoutSettingsPane();
            }

            // 卡片池按整个可见区域来备：面板一拉高，行数就变，
            // 池子要够覆盖当前能看见的那些行。
            int requiredMediaCards = Mathf.CeilToInt(viewHeight / MediaCardHeight) + 2;
            EnsureMediaCards(requiredMediaCards);
            mediaContent.sizeDelta = new Vector2(0f, Mathf.Max(viewHeight, mediaContent.sizeDelta.y));
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
            uiRoot = null;
            canvas = null;
            designRoot = null;
            browserPanel = null;
            decoList = null;
            decoView = null;
            helpWindow = null;
            versionWindow = null;
            settingsPaneRoot = null;
            settingsPane = null;
            settingsPropertyPanel = null;
            browserTabStripHost = null;
            browserTabStripContent = null;
            browserTabStrip = null;
            browserTabButtons.Clear();
            browserTabLabels.Clear();
            browserTabPages.Clear();
            // 拆界面之前先把借来的原生面板还回去 —— 再晚一步，我们这边的
            // 宿主就没了，它会落在一个正在被销毁的层级里。
            HideSettingsPane();
            browserPane = BrowserPane.Decorations;
            mediaPanel = null;
            mediaViewport = null;
            mediaContent = null;
            mediaTitle = null;
            mediaScroll = null;
            mediaCards.Clear();
            visibleProjectMedia.Clear();
            projectSession = null;
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
            multiplayerPanel = null;
            multiplayerButton = null;
        }

        private void RestorePreviewViewport()
        {
            if (previewCameraModified && previewCamera != null)
            {
                previewCamera.rect = previewCameraOriginalRect;
            }

            RestoreBackgroundCameraRects();
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
        /// 面板标题条的实心底衬。
        ///
        /// **横向锚成拉伸（anchorMin.x=0 / anchorMax.x=1），不给固定宽度。**
        /// 面板宽度会随时间轴高度变——时间轴拉高，舞台就变矮，两侧面板跟着变宽。
        /// 固定宽度的话，拖时间轴时这条底衬会停在旧宽度上，比面板窄一截，
        /// 右上角露出来，看起来就是"没自适应"。
        ///
        /// 和标题下面那条分隔线用的是同一个办法（见 CreateHeadingRule）：
        /// 锚到两条水平边，布局自动拉伸它。于是两者都不需要任何
        /// 逐次重排的代码，也就不会再漏。
        /// </summary>
        private Image CreateHeadingBand(string name, RectTransform parent, float height)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            // 拉伸轴用 offset 而不是 sizeDelta 摆位：左右各贴边 0，
            // 上沿贴面板顶，下沿往下 height。
            rect.offsetMin = new Vector2(0f, -height);
            rect.offsetMax = new Vector2(0f, 0f);

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = Band;
            ApplyRounded(image, CornerLarge, false, 0f);
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// 标题左边那条实心金色竖条。四个面板各一条，位置和尺寸都从这里出。
        ///
        /// 垂直居中在标题行里（行高 28、条高 16，所以上边距 6），
        /// 不是顶对齐——顶对齐会让它读成标题的下划线，而不是一个独立的标。
        /// </summary>
        private Image CreateHeadingBar(string name, RectTransform parent, float x)
        {
            Image bar = CreateImage(name, parent, x,
                (PanelHeadingHeight - HeadingBarHeight) * 0.5f,
                HeadingBarWidth, HeadingBarHeight, AccentSecondary);
            bar.raycastTarget = false;
            return bar;
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

            label = CreateText("Label", rect, 0f, 0f, width, height, text, 13f, TextSecondary, TextAlignmentOptions.Center, false);
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

        private void CreateMediaSearchControls(RectTransform parent, float viewWidth, float viewY)
        {
            const float searchHeight = 24f;
            float searchWidth = Mathf.Max(40f,
                viewWidth - MediaSearchInset * 2f - MediaRefreshWidth - MediaCountWidth - 12f);

            Button refresh = CreateButton(
                "MediaRefresh",
                parent,
                viewWidth - MediaSearchInset - MediaRefreshWidth,
                viewY,
                MediaRefreshWidth,
                searchHeight,
                "↻",
                false,
                RefreshMediaList,
                out mediaRefreshLabel);
            mediaRefreshButton = refresh;
            mediaRefreshLabel.fontSize = 16f;

            RectTransform rect = CreateTopLeftRect("MediaSearch", parent, MediaSearchInset, viewY, searchWidth, searchHeight);
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

            // 文本与占位符都铺满输入框。这里是照装饰物搜索框那套写的——
            // 那一个是可用的，所以结构上跟它保持一致，不要自己另发明一套。
            //
            // 两个要点：
            //   · 全新 GameObject，不用 CreateTopLeftRect。那个辅助按左上枢轴
            //     摆位，虽然随后会改锚点，但枢轴会留在 (0,1)。而
            //     TMP_InputField 的光标是挂在文本父节点下的**兄弟**，
            //     它的锚点/枢轴/位置/尺寸由 AssignPositioningIfNeeded()
            //     整套从文本抄过去，于是文本枢轴一偏，光标跟着偏。
            //   · 锚点改成拉伸，左右各留 7 像素。
            TextMeshProUGUI text = CreateStretchedFieldText("Text", rect, 7f,
                string.Empty, 11.5f, TextPrimary, uiFont);
            TextMeshProUGUI placeholder = CreateStretchedFieldText("Placeholder", rect, 7f,
                "搜索图片…", 11.5f, TextSecondary, uiFont);

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

        private const float MediaSearchRowHeight = 32f;
        private TMP_InputField mediaSearchField;
        private Button mediaRefreshButton;
        private TextMeshProUGUI mediaRefreshLabel;

        /// <summary>
        /// 输入框内部的文本：铺满父节点、左右各留 inset。
        ///
        /// 用全新 GameObject 而不是 CreateTopLeftRect：后者会先把枢轴设成
        /// 左上，改锚点之后枢轴仍留在 (0,1)。输入框的光标是文本的兄弟节点，
        /// TMP_InputField 会把文本的锚点/枢轴/位置/尺寸整套抄给光标，
        /// 所以文本枢轴一偏，光标就跟着偏。新建对象的枢轴是默认正中，不会出这问题。
        /// </summary>
        private static TextMeshProUGUI CreateStretchedFieldText(string name, RectTransform parent,
            float inset, string value, float size, Color color, TMP_FontAsset uiFont)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, 0f);
            rect.offsetMax = new Vector2(-inset, 0f);

            TextMeshProUGUI text = gameObject.AddComponent<TextMeshProUGUI>();
            if (uiFont != null)
            {
                text.font = uiFont;
            }
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            ApplyTextShadow(text);
            return text;
        }

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

        /// <summary>
        /// 分段控件的分段外观：选中的那一段整块铺满实心强调色，文字反过来用深色。
        ///
        /// 和 <see cref="SetButtonPalette"/> 的 selected 分支不是一回事——那一支
        /// 只叠一层两成多的淡色，在近黑底上几乎看不出来，选中和没选中长得一样。
        /// 分段控件必须一眼看出当前在哪一段，所以这里用实心。
        /// </summary>
        private static void SetSegmentPalette(Button button, bool active, Color activeFill)
        {
            if (button == null)
            {
                return;
            }

            Color normal = active ? activeFill : ButtonNormal;
            Color highlighted = active ? activeFill : ButtonHover;
            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = highlighted;
            colors.pressedColor = active
                ? new Color(activeFill.r * 0.88f, activeFill.g * 0.88f, activeFill.b * 0.88f, activeFill.a)
                : ButtonHover;
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
        /// <summary>第二强调色：面板标题、时间读数、计数这类"要读的东西"。</summary>
        internal static Color AccentSecondary { get { return EditorStudioOverlay.AccentSecondaryColor; } }
        internal static Color Selected { get { return EditorStudioOverlay.SelectedColor; } }
        internal static Color SelectedHover { get { return EditorStudioOverlay.SelectedHoverColor; } }
        internal static Color Raised { get { return EditorStudioOverlay.RaisedColor; } }
        internal static Color ThumbWell { get { return EditorStudioOverlay.ThumbWellColor; } }
        /// <summary>面板标题条的实心底衬。</summary>
        internal static Color SurfaceBand { get { return EditorStudioOverlay.BandColor; } }
        internal static Color FieldWell { get { return EditorStudioOverlay.FieldWellColor; } }
        internal static Color PanelRule { get { return EditorStudioOverlay.PanelRuleColor; } }
        internal static Color TextPrimary { get { return EditorStudioOverlay.TextPrimaryColor; } }
        internal static Color TextSecondary { get { return EditorStudioOverlay.TextSecondaryColor; } }
        /// <summary>强调文字：把关键词从正文里拎出来用（比主色暗一档，不刺眼）。</summary>
        internal static Color TextEmphasis { get { return EditorStudioOverlay.TextEmphasisColor; } }
        internal static Color TextMuted { get { return EditorStudioOverlay.TextMutedColor; } }
    }
}
