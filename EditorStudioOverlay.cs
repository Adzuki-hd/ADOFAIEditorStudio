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

        internal const float CornerLarge = 0f;
        internal const float CornerSmall = 0f;

        private const float DesignWidth = 1920f;
        private const float DesignHeight = 1080f;
        /// <summary>品牌那一行的高度。</summary>
        private const float HeaderHeight = 54f;
        /// <summary>
        /// 菜单栏（`文件 / 工程 / 偏好 / 设置`）那一行的高度，紧贴在品牌行下面。
        ///
        /// 单独一个常量，**不要并进 `HeaderHeight`** —— 品牌行的绘制按
        /// `HeaderHeight` 居中，合起来会让标题掉到半空。下游凡是"从顶栏往下算"
        /// 的位置改用 `LayoutTop`。
        /// </summary>
        private const float MenuBarHeight = 30f;
        /// <summary>顶栏 + 菜单栏的总高。下面所有面板的起点从这里算。</summary>
        private const float LayoutTop = HeaderHeight + MenuBarHeight;
        // 外边距与面板间距。原来都是 12，整个界面读起来是「紧」的——
        // 面板之间、面板与屏幕边、标题与内容全是同一个数，没有呼吸。
        // 提到 16 之后同样的内容从容很多，这是最容易见效、
        // 又不改任何结构的一档调整。
        private const float OuterMargin = 4f;
        private const float PanelGap = 4f;

        /// <summary>
        /// 模块背景块之间的净间距，同时也是块与顶栏、块与屏幕边缘的间距。
        ///
        /// 它不是 PanelGap 的替代：面板内容仍按 OuterMargin / PanelGap 排，
        /// 块是把面板矩形往外"吃"掉一部分间距换来的 —— 这样内容一行都不用挪，
        /// 块之间恰好剩 6。取 6 是"细而不糊"的一档（4 在 1080p 下只有 4 像素，
        /// 缩放后容易糊成一条；8 又比现在的分组间距粗）。
        /// </summary>
        private const float BlockGap = 4f;

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
        private const float PreviewPanelY = LayoutTop + 12f;

        /// <summary>时间轴面板（也是标题行）的上沿。原来这算式在四处各写一遍，容易改漏。</summary>
        internal float TimelinePanelTop { get { return DesignHeight - timelineHeight + 4f; } }

        /// <summary>上排（左栏 / 舞台工具条 / 参数栏）背景块的顶。</summary>
        private float BlockRowTop { get { return TopBandHeight + BlockGap; } }

        /// <summary>上排背景块的底 —— 让它与时间轴块之间也只剩 BlockGap。</summary>
        private float BlockRowBottom { get { return TimelinePanelTop - BlockGap; } }
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

        /// <summary>
        /// **所有模块的唯一底色。** 直接取顶栏那条（SurfaceTopBand），
        /// 于是顶栏、左栏、右栏、预览、时间轴是同一种不纯黑 —— 用户要求
        /// 「每个模块一个大色块，颜色和顶上一致」。
        /// </summary>
        private static Color ModuleFill { get { return Color.clear; } }
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

        /// <summary>工作台 uGUI 那层的排序（ImGui 那层是 32000，见桥接）。</summary>
        private const int CanvasSortingOrder = 30000;

        private TMP_FontAsset uiFont;
        private TMP_FontAsset generatedTmpFont;
        private GameObject ownedEventSystem;
        private bool built;
        private bool reopenAfterReload;
        private bool wasVisibleBeforeReload;
        private bool buildFailed;
        private bool visible;

        /// <summary>
        /// 当前是只读还是编辑模式。
        ///
        /// **不持久化** —— 每次 F8 打开都从只读开始。只读是「看」的姿态、
        /// 编辑是「改」的姿态，每次进来重新选一次，不容易误改谱面。
        ///
        /// 只读下块拖不动、参数改不了。拦截点只有一处：**写入路径本来就只有一条**
        /// （技术架构里那条硬规矩），只要在那条路上问一句就够，
        /// 不必给每个按钮单独挂开关。
        /// </summary>
        private bool editMode;

        /// <summary>
        /// 别的面板拿它判断「现在能不能改东西」。
        ///
        /// **「创建工程」那一屏还开着的时候不算编辑** —— 用户还没答应建，
        /// 这时候放行写入会让他以为已经能改了，结果一取消全白摆。
        /// </summary>
        internal bool IsEditMode
        {
            get
            {
                if (ProjectCreateVisible)
                {
                    return false;
                }
                return editMode;
            }
        }

        /// <summary>
        /// 只读模式下，**统一**在"改谱面"的入口拦一次，并**说明为什么**。
        ///
        /// 用法：`if (!(owner?.AllowEdit() ?? true)) return;` —— 返回 true 表示可以动手，
        /// false 表示已经在只读下拦住了、提示也发了。
        ///
        /// **为什么要有它**：每个入口各写一句 `if (!IsEditMode) return;` 的话，
        /// 漏一处就变成"点了没反应"，而从现象上看那和"坏了"一模一样 ——
        /// 今天这个坑连着骗了我们两回（事件面板、装饰物列表的新建），
        /// 每次都从"为什么加不进时间轴"一路查回来。走同一个入口就不会再漏。
        ///
        /// 提示用的是那条瞬态状态（几秒后自己消失），文案统一成一句。
        /// </summary>
        internal bool AllowEdit()
        {
            if (IsEditMode)
            {
                return true;
            }
            SetTransientStatus(ReadOnlyEditMessage, 3f);
            return false;
        }

        /// <summary>
        /// 只读挡下时那句统一的话。
        ///
        /// `AllowEdit` 用它弹提示；**需要把自己拦下的原因回传给调用方的入口**
        /// （时间轴的删除/粘贴：热键处理要靠"有没有原因"决定要不要吃掉这个键）
        /// 也从这儿取，别各写一份 —— 文案走散之后，读日志的人会以为遇到了两种问题。
        /// </summary>
        internal const string ReadOnlyEditMessage = "只读模式不能修改谱面，先切到编辑模式";

        /// <summary>
        /// 「关工作台 / 切回只读」这两个动作要等用户答完保存提示才能做。
        /// 弹窗是异步的，所以先把意图挂在这儿。
        /// </summary>
        private enum PendingExit { None, HideOverlay, ToReadOnly }

        private PendingExit pendingExit;

        /// <summary>
        /// 关工作台或切只读之前，先问一句布局存了没有。
        ///
        /// 返回 true 表示**已经拦下**（弹窗开了、动作挂起来了），
        /// 调用方这一下什么都别做，等 `UpdateConfirmSave` 那边收答案。
        /// 返回 false 表示可以照常往下走。
        /// </summary>
        private bool InterceptForUnsavedLayout(PendingExit action)
        {
            if (Timeline == null || !Timeline.ProjectDirty)
            {
                return false;
            }
            if (confirmSaveWindow == null)
            {
                // UI 还没建起来（没开过工作台）：没有弹窗可问，按"不拦"处理。
                return false;
            }
            pendingExit = action;
            OpenConfirmSave();
            return true;
        }

        /// <summary>
        /// 收「布局还没存」那扇窗的答复。每帧问一次，没答就什么都不做。
        ///
        /// 1 = 保存（存完再执行那个动作）、2 = 不保存（直接执行）、3 = 取消（什么都不做）。
        /// </summary>
        private void UpdateConfirmSave()
        {
            int answer;
            if (!TakeConfirmSaveAnswer(out answer))
            {
                return;
            }
            PendingExit action = pendingExit;
            pendingExit = PendingExit.None;

            if (answer == 3)
            {
                // 取消：什么都不做，用户还在原地。
                return;
            }
            if (answer == 1)
            {
                MenuSaveProject();
                // 存失败（比如还没有工程）就别硬往下走了 —— 那会变成
                // 「点了保存、结果布局还是丢了」。
                if (Timeline != null && Timeline.ProjectDirty)
                {
                    return;
                }
            }

            if (action == PendingExit.HideOverlay)
            {
                SetVisible(false);
            }
            else if (action == PendingExit.ToReadOnly)
            {
                ApplyEditMode(false);
            }
        }

        /// <summary>切模式。只读 → 编辑时确保这张谱有工程。</summary>
        internal void SetEditMode(bool value)
        {
            if (editMode == value)
            {
                return;
            }
            // 切回只读之前先问一句布局存了没有 —— 只读之后就不能再改了，
            // 没存的话那次的改动就白摆。
            if (!value && InterceptForUnsavedLayout(PendingExit.ToReadOnly))
            {
                return;
            }
            ApplyEditMode(value);
        }

        /// <summary>真正切模式，不拦截。给答复保存提示那条路用。</summary>
        private void ApplyEditMode(bool value)
        {
            if (editMode == value)
            {
                return;
            }
            editMode = value;
            Main.Logger.Log("[模式] 切到 " + (value ? "编辑" : "只读"));
            if (value)
            {
                EnsureProjectForEdit();
            }
        }

        /// <summary>
        /// 切到编辑模式时确保这张谱有工程。
        ///
        /// 有工程就直接进去；没有就推开「创建工程」那一屏让用户起名 ——
        /// **这时候还不算真的切过去了**，等他答完（见 `UpdateProjectCreate`）。
        /// </summary>
        private void EnsureProjectForEdit()
        {
            string chartPath = EditorProjectSession.GetCurrentLevelPath();
            if (string.IsNullOrEmpty(chartPath))
            {
                // 拿不到路径就建不了工，退回只读，别让用户卡在「编辑」却存不了。
                editMode = false;
                SetTransientStatus("拿不到谱面路径，建不了工程");
                return;
            }
            if (EditorProjectFile.FindProjectFile(chartPath) != null)
            {
                return;
            }
            OpenProjectCreate(
                Path.GetFileNameWithoutExtension(chartPath),
                EditorProjectFile.GetProjectDirectory(chartPath));
        }

        /// <summary>
        /// 收「创建工程」那一屏的答复。每帧问一次，没有答复就什么都不做。
        ///
        /// 建工程只是**落盘**，不需要等 —— 自动排列的结果在 F8 那趟加载里算好了。
        /// </summary>
        private void UpdateProjectCreate()
        {
            string name;
            bool confirmed;
            if (!TakeProjectCreateResult(out name, out confirmed))
            {
                return;
            }
            if (!confirmed)
            {
                editMode = false;   // 取消 = 退回只读
                return;
            }
            string chartPath = EditorProjectSession.GetCurrentLevelPath();
            if (string.IsNullOrEmpty(chartPath) || Timeline == null)
            {
                editMode = false;
                SetTransientStatus("建工程失败：拿不到谱面路径");
                return;
            }
            string fileName = SanitizeProjectName(name) + EditorProjectFile.FileExtension;
            string error = Timeline.SaveProjectRows(
                Path.Combine(EditorProjectFile.GetProjectDirectory(chartPath), fileName),
                Path.GetFileName(chartPath));
            if (string.IsNullOrEmpty(error))
            {
                SetTransientStatus("工程已新建，时间轴排列会保存到工程里");
            }
            else
            {
                editMode = false;
                SetTransientStatus("新建工程失败：" + error);
            }
        }

        /// <summary>
        /// 工程名里不能有的字符换成下划线 —— 它会变成一个真实文件名，
        /// 带 `\ / : * ? " &lt; &gt; |` 的路径在 Windows 上根本建不出来。
        /// </summary>
        private static string SanitizeProjectName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "project";
            }
            char[] bad = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            string cleaned = name.Trim();
            for (int i = 0; i < bad.Length; i++)
            {
                cleaned = cleaned.Replace(bad[i], '_');
            }
            return cleaned.Length == 0 ? "project" : cleaned;
        }
        private int layoutScreenWidth;
        private int layoutScreenHeight;
        private float timelineHeight = DefaultTimelineHeight;
        private PreviewLayout previewLayout;
        private RectTransform designRoot;
        private RectTransform browserPanel;
        /// <summary>左栏模块底色（uGUI 自己铺，见 BuildBrowser）。跟着块行一起重算。</summary>
        private RectTransform browserBlockFill;
        /// <summary>左栏的装饰物列表数据层。建好后交给时间轴去通知刷新。</summary>
        private EditorDecorationList decoList;

        /// <summary>左栏当前显示哪一页。</summary>
        // internal：新的 uGUI 左栏（UiBrowserPane）要拿它当切页参数。
        internal enum BrowserPane
        {
            /// <summary>装饰物列表。</summary>
            Decorations,
            /// <summary>媒体库。</summary>
            Media,
            /// <summary>
            /// 游戏原生的某一个设置页。
            ///
            /// 具体是哪一页由 <see cref="selectedSettingsPage"/> 决定。正文目前仍由
            /// ImGui 参数栏渲染器画（它和右栏共用同一条写入路径），等右栏迁完
            /// uGUI 一起换掉；左栏这一侧只负责标签与切页。
            /// </summary>
            Settings
        }

        private BrowserPane browserPane = BrowserPane.Decorations;
        /// <summary>设置页在前台时，正显示的是哪一页。</summary>
        private LevelEventType selectedSettingsPage = LevelEventType.SongSettings;

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

        /// <summary>
        /// 「创建工程」那一屏。用户答完（建 / 不建）才把结果交给
        /// <see cref="UpdateProjectCreate"/> —— 它是**要答复的问题**，不是普通窗口。
        /// </summary>
        internal sealed class ProjectCreateWindow
        {
            internal RectTransform Layer;
            internal TMP_InputField NameField;
            internal TextMeshProUGUI PathLabel;
            internal bool Confirmed;
            internal bool Cancelled;

            internal bool IsOpen
            {
                get { return Layer != null && Layer.gameObject.activeSelf; }
            }
        }

        /// <summary>「布局还没存」那一屏。答复记在 <see cref="Answer"/>：1 存 / 2 不存 / 3 取消。</summary>
        internal sealed class ConfirmSaveWindow
        {
            internal RectTransform Layer;
            internal int Answer;

            internal bool IsOpen
            {
                get { return Layer != null && Layer.gameObject.activeSelf; }
            }
        }

        private ProjectCreateWindow projectCreateWindow;
        private ConfirmSaveWindow confirmSaveWindow;

        /// <summary>新的左栏（标签条 + 装饰物页 + 媒体页），见 UiBrowserPane。</summary>
        private UiBrowserPane browserPaneView;
        /// <summary>左栏设置页正文（和右栏共用一个写入层）。</summary>
        private UiInspectorPane settingsPaneView;
        /// <summary>右栏新的 uGUI 参数栏。</summary>
        private UiInspectorPane inspectorPane;
        /// <summary>右栏模块底色。</summary>
        private RectTransform inspectorBlockFill;
        /// <summary>时间轴模块底色（随拖拽改高度）。</summary>
        private RectTransform timelineBlockFill;
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
        /// 加载遮罩（媒体预载 / 联机加入）正盖在屏幕上。
        ///
        /// 键盘输入要靠这个判断让路：uGUI 的遮罩只挡鼠标（GraphicRaycaster 挡射线），
        /// **挡不住键盘** —— 加载期间按空格，求值器预览就会开始播，用户听到
        /// "黑屏底下有音乐"。所有快捷键都必须在遮罩期间吃掉。
        /// </summary>
        internal bool IsLoadingScreenShowing
        {
            get { return mediaPreloading && loadingScreen != null && loadingScreen.IsShowing; }
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
        /// 参数栏在屏幕上的矩形（1920x1080 设计坐标）。
        ///
        /// ImGui 那版参数栏要占**同一个坑**。坐标不用换算，是因为两边已经统一到
        /// 同一个设计空间了（见 EditorImGuiBridge 里对 `_scale` 的说明）。
        /// </summary>
        /// <summary>设计画布尺寸（1920×1080）。给背景块日志反算边距用。</summary>
        internal float CanvasWidth { get { return DesignWidth; } }
        internal float CanvasHeight { get { return DesignHeight; } }

        /// <summary>顶栏带的高度：标题行 + 菜单行（到「工程」那一行下沿为止，不含标签条）。</summary>
        internal float TopBandHeight { get { return HeaderHeight + MenuBarHeight; } }

        // -------------------------------------------------- 模块背景块矩形
        //
        // 四块（左栏 / 预览工具条 / 参数栏 / 时间轴）的底色区。它们比面板矩形
        // 往外多吃掉一部分间距，于是块与块、块与顶栏/屏幕边之间恒为 BlockGap。
        // 全部由 previewLayout 与 timelineHeight 现算，切分辨率、拖时间轴高度都不漂。

        /// <summary>左栏背景块。装饰物 / 媒体 / 设置三页共用同一块。</summary>
        internal void GetBrowserBlockRect(out float x, out float y, out float width, out float height)
        {
            x = BlockGap;
            y = BlockRowTop;
            width = previewLayout.StageX - BlockGap * 2f;   // 右缘 = 舞台左缘 - BlockGap
            height = BlockRowBottom - y;
        }

        /// <summary>参数栏背景块。</summary>
        internal void GetInspectorBlockRect(out float x, out float y, out float width, out float height)
        {
            x = previewLayout.StageX + previewLayout.StageWidth + BlockGap;
            y = BlockRowTop;
            width = DesignWidth - BlockGap - x;
            height = BlockRowBottom - y;
        }

        /// <summary>时间轴背景块：从时间轴顶一直到屏幕底上方 BlockGap。</summary>
        internal void GetTimelineBlockRect(out float x, out float y, out float width, out float height)
        {
            x = BlockGap;
            y = TimelinePanelTop;
            width = DesignWidth - BlockGap * 2f;
            height = DesignHeight - BlockGap - y;
        }

        /// <summary>当前左栏是不是媒体页。</summary>
        internal bool IsMediaPane
        {
            get { return browserPane == BrowserPane.Media; }
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

        // -------------------------------------------------- 设置页
        //
        // 左边那几页设置（歌曲/关卡/轨道/……）**本身就是一个 `LevelEvent`**
        // （原生把八页挂在 levelData 上，见 FindSettingsPage），正文由参数栏
        // 渲染器画；uGUI 旧的那份设置面板已经删掉，不再有两套窗口叠在一起。

        /// <summary>当前左栏是不是某个设置页。</summary>
        internal bool IsSettingsPane
        {
            get { return browserPane == BrowserPane.Settings; }
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
        /// 顶栏那个 Image。ImGui 版接管时整个藏掉（见 SuppressHeader）。
        /// </summary>
        private Image headerRoot;

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

        /// <summary>预览面板页眉那一行有多高 —— 工具栏那一条。</summary>
        internal const float PreviewToolbarHeight = 52f;

        // -------------------------------------------------- ImGui 版时间轴的口子
        //
        // 时间轴面板 = 12 起、宽度 `TimelinePanelWidth`、高度跟着拖拽走（见
        // RelayoutProjectWorkspace 里那次 SetTopLeftRect）。**标题行是它最上面
        // 那 42 像素**：事件面板从 y=44 起，紧贴在它下面，所以这一条能单独接管，
        // 下面照旧。

        /// <summary>时间轴视图（标题行上的按钮都转发到它）。</summary>
        internal EditorTimelineView TimelineView
        {
            get { return timelineView; }
        }

        /// <summary>
        /// 交给菜单栏的活儿：都是转发给游戏原生编辑器，AES 自己不碰 `.adofai`。
        ///
        /// **只有"打开/保存/另存"是完全转发**；撤销和重做要先走 AES 自己的栈
        /// （工作台里改过的东西在原生那边没有记录），栈空了才轮到原生。
        /// </summary>
        internal void MenuOpenLevel()
        {
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.OpenLevel();
            }
        }

        internal void MenuSaveLevel()
        {
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.SaveLevel();
            }
        }

        internal void MenuSaveLevelAs()
        {
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.SaveLevelAs();
            }
        }

        internal void MenuUndo()
        {
            // 撤销会改谱面 —— 只读模式下和改参数一样要拦。
            if (!AllowEdit())
            {
                return;
            }
            // **先走工作台自己的撤销栈。**
            //
            // 拖块换行、装饰物、批量改参这些都只记在 `EditorCommandHistory` 里，
            // 游戏原生那边根本没有 —— 拖块换行甚至不改谱面，原生无从知道。
            // 直接转发原生的话，在时间轴上拖完再撤销会「什么都没发生」。
            string status;
            if (EditorCommandHistory.TryUndo(out status))
            {
                SetTransientStatus(string.IsNullOrEmpty(status) ? "已撤销" : status);
                return;
            }
            // 工作台这边没得撤了，才轮到原生（比如原生面板里做的改动）。
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.Undo();
            }
        }

        internal void MenuRedo()
        {
            if (!AllowEdit())
            {
                return;
            }
            string status;
            if (EditorCommandHistory.TryRedo(out status))
            {
                SetTransientStatus(string.IsNullOrEmpty(status) ? "已重做" : status);
                return;
            }
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.Redo();
            }
        }

        /// <summary>
        /// 「重置时间轴排版」：丢掉工程里存的位置，让全部事件重新自动装箱。
        ///
        /// **这是破坏性的**（玩家摆好的位置会没），所以做之前重新载一次工程表
        /// 是反的 —— 这里直接清空表再强制重排。
        /// </summary>
        internal void MenuResetTimelineLayout()
        {
            if (Timeline != null)
            {
                Timeline.ResetPlacementsToAuto();
            }
            SetTransientStatus("时间轴排版已重置为自动排列");
        }

        /// <summary>「保存工程」：按约定路径写 `.aesprj`。</summary>
        /// <summary>
        /// 关游戏（或卸载 mod）前自动存一份布局。**没有未保存改动时什么都不做。**
        ///
        /// 退出流程里弹不出 UI（没人看得到窗口），只能静默写 —— 总比
        /// "在时间轴上摆了一晚上、关游戏全丢"好。
        /// </summary>
        internal void SaveProjectBeforeQuit()
        {
            if (Timeline == null || !Timeline.ProjectDirty)
            {
                return;
            }
            MenuSaveProject();
            if (Timeline.ProjectDirty)
            {
                // 存完还脏（多半是这张谱还没有工程）—— 记一条，别假装成功。
                Main.Logger.Warning("[工程] 关游戏前想自动保存，但没能写进去"
                    + "（多半是这张谱还没有工程）");
            }
            else
            {
                Main.Logger.Log("[工程] 关游戏前自动保存了一份（之前有未存的布局改动）");
            }
        }

        /// <summary>
        /// **Ctrl+S 的第二半**：把排版（行号 / 事件块 Y 位置）写进 `.aesprj`。
        ///
        /// 之前这条路只挂在 `Ctrl+Alt+S` 上，于是用户按完 Ctrl+S（只存了谱面）、
        /// 关工作台时还会被问一句"布局还没保存" —— 因为排版那次根本没写。
        /// 现在并进 Ctrl+S：一次按键同时存谱面和排版。
        ///
        /// 没有工程（还是只读状态）、或者排版没改过时，什么都不做。
        /// </summary>
        internal void SaveLayoutIfDirty()
        {
            if (Timeline == null || !Timeline.ProjectDirty)
            {
                return;
            }
            string chartPath = EditorProjectSession.GetCurrentLevelPath();
            if (string.IsNullOrEmpty(chartPath))
            {
                return;
            }
            if (EditorProjectFile.FindProjectFile(chartPath) == null)
            {
                return;
            }
            string directory = EditorProjectFile.GetProjectDirectory(chartPath);
            string fileName = Path.GetFileNameWithoutExtension(chartPath)
                + EditorProjectFile.FileExtension;
            string error = Timeline.SaveProjectRows(
                Path.Combine(directory, fileName), Path.GetFileName(chartPath));
            if (!string.IsNullOrEmpty(error))
            {
                Main.Logger.Error("[Save] 工程排版保存失败：" + error);
            }
        }

        internal void MenuSaveProject()
        {
            string chartPath = EditorProjectSession.GetCurrentLevelPath();
            if (string.IsNullOrEmpty(chartPath) || Timeline == null)
            {
                SetTransientStatus("拿不到谱面路径，存不了工程");
                return;
            }
            if (EditorProjectFile.FindProjectFile(chartPath) == null)
            {
                SetTransientStatus("这张谱还没有工程，先切到编辑模式建一个");
                return;
            }
            string directory = EditorProjectFile.GetProjectDirectory(chartPath);
            string fileName = Path.GetFileNameWithoutExtension(chartPath)
                + EditorProjectFile.FileExtension;
            string error = Timeline.SaveProjectRows(
                Path.Combine(directory, fileName), Path.GetFileName(chartPath));
            SetTransientStatus(string.IsNullOrEmpty(error)
                ? "工程已保存"
                : "保存工程失败：" + error);
        }

        // -------------------------------------------------- 预览区

        /// <summary>预览区现在有没有地方可画（窗口太小的时候舞台会被收掉）。</summary>
        internal bool PreviewVisible
        {
            get { return previewLayout.StageWidth > 1f && previewLayout.StageHeight > 1f; }
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
            // 从「顶栏 + 菜单栏」下面算，不是从顶栏算 —— 菜单栏占的那 30 像素
            // 要从预览区里让出来，否则它会被菜单栏压住一截。
            get { return DesignHeight - LayoutTop - timelineHeight - 28f; }
        }

        /// <summary>
        /// 舞台可用高度：**只扣顶部的工具栏（58），底部不预留**。
        ///
        /// 原来扣 64（顶 58 + 底 6），窗口拉满时底下会留一条 6px 的面板色带 ——
        /// 看起来就像描边下面多了一条粗横线、框没包紧。现在底部留 0，
        /// 窗口最大时底边正好贴住面板底；窗口因长宽比变小时由居中算法
        /// 上下平分剩余空间，那时才会有垫边。
        /// </summary>
        private float PreviewStageAvailableHeight
        {
            get { return PreviewPanelHeight - PreviewStageInsetY; }
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
            long __watch = System.Diagnostics.Stopwatch.GetTimestamp();
            LateUpdateCore();
            PreviewDiagnostics.RecordPart("Overlay.L",
                (System.Diagnostics.Stopwatch.GetTimestamp() - __watch)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private void LateUpdateCore()
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

        /// <summary>
        /// 只**记录**、不修改：把画布上面积较大的 uGUI Image 名字/尺寸/颜色写进日志，
        /// 用来确认那几块底色到底是哪几个对象画的。
        ///
        /// 之前这里会直接把颜色清成透明，结果把整个工作台的底板也清掉了 ——
        /// 画面变成半透明、背后游戏一动就跟着闪。这里改成纯读取。
        /// </summary>
        private bool loggedSurfaces;

        private void LogLargeSurfacesOnce()
        {
            if (loggedSurfaces)
            {
                return;
            }
            loggedSurfaces = true;
            Image[] images = GetComponentsInChildren<Image>(true);
            int found = 0;
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null || image.rectTransform == null)
                {
                    continue;
                }
                Rect rect = image.rectTransform.rect;
                if (Mathf.Abs(rect.width * rect.height) < 40000f)
                {
                    continue;
                }
                found++;
                Main.Logger.Log("[底色清单] " + image.gameObject.name
                    + " " + rect.width.ToString("0") + "x" + rect.height.ToString("0")
                    + " 色=" + image.color.ToString("0.00")
                    + " 父=" + (image.transform.parent == null ? "-" : image.transform.parent.name));
            }
            Main.Logger.Log("[底色清单] 共 " + found + " 块大面积 Image。");
        }

        private void Update()
        {
            long __watch = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateCore();
            PreviewDiagnostics.RecordPart("Overlay.U",
                (System.Diagnostics.Stopwatch.GetTimestamp() - __watch)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private void UpdateCore()
        {
            if (settings == null)
            {
                return;
            }
            // 帧耗时诊断：开关关着时它内部第一行就返回。
            PreviewDiagnostics.Tick();
            if (visible)
            {
                LogLargeSurfacesOnce();
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

            // **快捷键已经取消**：工作台改由编辑器里那颗悬浮球打开
            // （见 `EditorImGuiBridge.DrawFloatingBall`）。
            //
            // 原因：实测有用户的 F8 根本进不了 Unity（笔记本 F 键默认走媒体键、
            // 或者被录屏 / 输入法 / 显卡 overlay 抢了全局热键），而键盘方案
            // 没有任何可视入口 —— 用户连"这个功能存在"都不知道。
            // 悬浮球看得见、点得到，也不跟任何按键冲突。

            // （原来这里还有快捷键的"游玩中不给开"拦截和开关分支。
            //  改成悬浮球之后，打开入口在 `EditorImGuiBridge.DrawFloatingBall`，
            //  那边自己也判了游玩模式 —— 见那个方法。）

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

            // 说明窗口 / 版本说明现在都是 uGUI：关掉时由 `ShowWindow` 自己把
            // 「这个版本的更新内容看过了」记下来，不必再轮询。

            // 「创建工程」那一屏的答复由它自己收。
            UpdateProjectCreate();

            // 「布局还没存」那扇窗同理 —— 答复决定要不要真的关掉/切模式。
            UpdateConfirmSave();

            // 加载页：画预载进度，铺完自己收掉。
            UpdateMediaPreload();

            // 打开工作台时"落到原生选中格"的待办，落地一次就撤。
            if (layoutRefreshPending > 0)
            {
                layoutRefreshPending--;
                if (layoutRefreshPending == 0)
                {
                    ApplyWorkspaceLayout();
                }
            }

            UiPreviewChrome.Refresh(previewChrome, StatusText);
            RefreshModeSwitch();

            // 面板状态取证（临时）：每 2 秒一次，看左右两栏到底有没有内容。
            if (Time.frameCount - lastPanelProbeFrame >= 120)
            {
                lastPanelProbeFrame = Time.frameCount;
                try
                {
                    RectTransform decoRect = browserPanel;
                    RectTransform mediaRect = browserPanel;
                    RectTransform inspRect = inspectorPanel;
                    Main.Logger.Log("[面板] 左栏=" + DescribePane(browserPanel, decoRect)
                        + " 媒体同栏=" + DescribePane(browserPanel, mediaRect)
                        + " inspector=" + DescribePane(inspectorPanel, inspRect)
                        + " 列表子物体=" + (browserPanel != null ? browserPanel.childCount : -1)
                        + " 探针:左=" + ProbeDrawn(120f, 300f)
                        + " 右=" + ProbeDrawn(DesignWidth - 120f, 300f)
                        + " 轴=" + ProbeDrawn(600f, DesignHeight - 60f)
                        + " 色:左=" + PaneColor(browserPanel)
                        + " 右上=" + PaneColor(inspectorPanel)
                        + " 时间轴=" + PaneColor(timelinePanel)
                        + " 预览=" + PaneColor(previewPanel));
                }
                catch (System.Exception exception)
                {
                    Main.Logger.Warning("[面板] 取证失败：" + exception.Message);
                }

            }

            // 点击取证（临时）：可见期间每次左键按下，打一行命中链。
            if (Input.GetMouseButtonDown(0))
            {
                StartCoroutine(LogRaycastPeek());
            }

            TickPendingSelectionJump();

            // 跳转的走位：定位点轨道、打开落格都靠它"加速过去"而不是硬切。
            EditorPlaybackBridge.TickPlayheadTravel();

            if (visible && Input.GetKeyDown(KeyCode.Escape))
            {
                // 有窗口开着时 Esc **先关它**：否则开着一层窗口按 Esc，整个
                // 工作台一起消失，正看着的东西也没了 —— 那是两件事，退出得逐层来。
                //
                // 顺序按「谁在最上面」：粒子浮窗 → 创建工程 → 保存提示 →
                // 说明 / 版本说明 → 关工作台。
                //
                // 前三个都不在 uGUI 那棵窗口树上（`OpenWindow` 只认说明与版本
                // 两扇），所以得单独问一句。
                if (UiParticleWindow.CloseIfOpen())
                {
                    // 关掉了，这一下 Esc 到此为止。
                }
                else if (ProjectCreateVisible)
                {
                    // 要答复的问题：关掉 = 回答「不建」（退回只读）。
                    CancelProjectCreate();
                }
                else if (ConfirmSaveVisible)
                {
                    // 同理：Esc 等于「取消」，那个动作就不做了。
                    AnswerConfirmSave(3);
                }
                else
                {
                    TextWindow openWindow = OpenWindow();
                    if (openWindow != null)
                    {
                        ToggleWindow(openWindow);
                    }
                    else if (!InterceptForUnsavedLayout(PendingExit.HideOverlay))
                    {
                        // 布局没存的话上面那一句会把弹窗挂起来、这里就不关了。
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
            canvas.sortingOrder = CanvasSortingOrder;
            canvas.pixelPerfect = false;

            CanvasScaler scaler = uiRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(DesignWidth, DesignHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;

            // **总背景保持透明。**
            //
            // 试过铺一层不透明的 #0B0B0B：预览那块立刻被整片盖住（画面直接没了）。
            // 原因是预览不是纹理，而是"透着看游戏"的一扇窗——底不透，窗就没了。
            // 所以「一层」靠的是**各模块都不画底**，而不是再铺一张更大的底。
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
            BuildLayer("菜单栏", delegate { BuildMenuBar(designRoot); });
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
            BuildLayer("创建工程", delegate { BuildProjectCreateWindow(designRoot); });
            BuildLayer("保存提示", delegate { BuildConfirmSaveWindow(designRoot); });

            built = true;
            // **按当前可见性决定激活，不要无条件关掉。**
            //
            // 原来这里写死 `SetActive(false)`：如果工作台是先「打开」再「建 UI」
            // （悬浮球那条路就是这样），`SetVisible(true)` 里那次激活因为
            // uiRoot 还不存在而被跳过，随后这里又把它关掉 —— 结果 uGUI 整块
            // 画布一直是关的，看起来就是「界面画出来了但点不动」。
            uiRoot.SetActive(visible);
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

        // ── 顶栏菜单（uGUI 版）──────────────────────────────────────────────
        //
        // ImGui 那版菜单栏（EditorImGuiMenuBar）现在是唯一入口，迁移后顶栏
        // 必须自带菜单。结构刻意做得简单：一条文字按钮 + 一个懒建的浮层，
        // 浮层里的条目都是普通 Button，不引入任何布局组件（全场禁 LayoutGroup）。

        /// <summary>一个菜单条目。`SeparatorBefore` 用来分段，`Placeholder` 是「开发中」占位项。</summary>
        private sealed class MenuEntry
        {
            internal string Label;
            internal string Shortcut;
            internal System.Action Action;
            internal System.Func<bool> EnabledWhen;
            internal System.Func<bool> CheckedWhen;
            internal bool SeparatorBefore;
            internal bool Placeholder;
        }

        private Image menuBarRoot;
        private RectTransform menuBlocker;
        private readonly System.Collections.Generic.Dictionary<string, RectTransform> menuDropdowns
            = new System.Collections.Generic.Dictionary<string, RectTransform>();
        private string openMenuName;

        /// <summary>菜单行底色：和顶栏同一条带子（ImGui 那层已经不画了）。</summary>
        private static Color MenuStripFill { get { return UiTheme.Current.SurfaceTopBand; } }
        private static Color MenuDropdownFill { get { return UiTheme.Current.SurfacePanel; } }

        /// <summary>建菜单条本身。位置与 ImGui 版一致：紧贴品牌行下沿，横跨整个设计宽度。</summary>
        private void BuildMenuBar(RectTransform parent)
        {
            float y = HeaderHeight;
            menuBarRoot = CreateImage("MenuBar", parent, 0f, y, DesignWidth, MenuBarHeight, MenuStripFill);
            menuBarRoot.raycastTarget = true;

            // **菜单条下面不再画分隔线。**
            //
            // 那条线是 1px 的 Image（`MenuRule`），不是块 —— 用户看到的就是它。
            // 现在整块工作台只有一层底色、没有模块边界，这条线反而成了唯一的
            // 一条"多余横线"，去掉。

            float x = 18f;
            x = AddMenuButton(parent, "文件", x, y, FileMenuEntries());
            x = AddMenuButton(parent, "工程", x, y, ProjectMenuEntries());
            x = AddMenuButton(parent, "偏好", x, y, PreferenceMenuEntries());
            AddMenuButton(parent, "设置", x, y, SettingsMenuEntries());
        }

        private float AddMenuButton(RectTransform parent, string name, float x, float y,
            System.Collections.Generic.List<MenuEntry> entries)
        {
            const float width = 68f;
            TextMeshProUGUI label;
            Button button = CreateButton(name, parent, x, y + 3f, width, MenuBarHeight - 6f,
                name, false, delegate { ToggleMenu(name, buttonRectOf[name], entries); }, out label);
            label.fontSize = 13f;
            label.color = TextPrimary;
            buttonRectOf[name] = button.GetComponent<RectTransform>();
            menuEntries[name] = entries;
            return x + width + 2f;
        }

        private readonly System.Collections.Generic.Dictionary<string, RectTransform> buttonRectOf
            = new System.Collections.Generic.Dictionary<string, RectTransform>();
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MenuEntry>> menuEntries
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MenuEntry>>();

        private void ToggleMenu(string name, RectTransform anchor,
            System.Collections.Generic.List<MenuEntry> entries)
        {
            Main.Logger.Log("[菜单] 进入 ToggleMenu name=" + name
                + " openMenuName=" + (openMenuName ?? "null")
                + " this=" + GetInstanceID());
            if (openMenuName == name)
            {
                CloseMenu();
                return;
            }
            CloseMenu();

            RectTransform dropdown;
            if (!menuDropdowns.TryGetValue(name, out dropdown) || dropdown == null)
            {
                dropdown = BuildDropdown(name, entries);
                menuDropdowns[name] = dropdown;
            }
            RefreshDropdown(dropdown, entries);
            dropdown.gameObject.SetActive(true);
            dropdown.SetAsLastSibling();
            if (menuBlocker != null)
            {
                menuBlocker.gameObject.SetActive(true);
                menuBlocker.SetSiblingIndex(dropdown.GetSiblingIndex() - 1);
            }
            openMenuName = name;
            Vector3[] corners = new Vector3[4];
            dropdown.GetWorldCorners(corners);
            Canvas layer = dropdown.GetComponentInParent<Canvas>();
            Main.Logger.Log("[菜单] 打开 " + name + "，浮层=" + dropdown.name
                + " rect=" + dropdown.anchoredPosition + " size=" + dropdown.sizeDelta
                + " active=" + dropdown.gameObject.activeInHierarchy
                + " 世界左下=" + corners[0] + " 右上=" + corners[2]
                + " 屏幕=" + Screen.width + "x" + Screen.height
                + " 画布=" + (layer != null ? layer.name + "#" + layer.sortingOrder : "无")
                + " 兄弟序=" + dropdown.GetSiblingIndex() + "/" + dropdown.parent.childCount
                + " 媒体层=" + MediaLayerOrder()
                + " 面板色=" + (dropdown.GetComponent<Image>() != null
                    ? dropdown.GetComponent<Image>().color.ToString() : "无图"));
        }

        private void CloseMenu()
        {
            Main.Logger.Log("[菜单] CloseMenu 之前 open=" + (openMenuName ?? "null"));
            if (!string.IsNullOrEmpty(openMenuName))
            {
                RectTransform open;
                if (menuDropdowns.TryGetValue(openMenuName, out open) && open != null)
                {
                    open.gameObject.SetActive(false);
                }
            }
            openMenuName = null;
            if (menuBlocker != null)
            {
                menuBlocker.gameObject.SetActive(false);
            }
        }

        /// <summary>懒建浮层：一块圆角底 + 每条目一个按钮。条目内容每次打开时刷新。</summary>
        /// <summary>
        /// 浮层专用画布（sortingOrder = 工作台 + 90）。
        ///
        /// **不能靠兄弟序抢层级**：面板在布局刷新时会被重新 SetAsLastSibling，
        /// 浮层很容易被装饰物列表重新盖住（实测就是这个现象）。
        /// 独立的 overrideSorting 画布按 sortingOrder 排序，跟层级顺序无关。
        /// </summary>
        /// <summary>
        /// 浮层画布（sortingOrder 32000）：菜单下拉、参数栏的枚举/调色盘共用。
        /// 它比 uGUI 工作台（30000）和 ImGui 层（31000）都高，弹层才不会被盖。
        /// </summary>
        internal RectTransform PopupLayer { get { return EnsurePopupCanvas(); } }

        /// <summary>工作台统一字体 —— 临时建的提示/弹层要用它，否则会退回默认字体。</summary>
        internal TMP_FontAsset UiFont { get { return uiFont; } }

        private RectTransform EnsurePopupCanvas()
        {
            if (popupCanvasRoot != null)
            {
                return popupCanvasRoot;
            }
            GameObject popupObject = new GameObject(
                "AES.Popup",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster));
            popupObject.transform.SetParent(uiRoot.transform, false);
            Canvas popupLayer = popupObject.GetComponent<Canvas>();
            popupLayer.overrideSorting = true;
            // 32000：工作台 30000 → ImGui 31000 → 浮层 32000 → 加载页 32767。
            // 浮层必须高于 ImGui 那层，否则 ImGui 的全屏铺底会把下拉菜单盖住
            // （这就是「文件下拉被盖住」的真正原因）。
            popupLayer.sortingOrder = 32000;
            RectTransform rect = popupObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(DesignWidth, DesignHeight);
            popupCanvasRoot = rect;
            return rect;
        }

        private RectTransform popupCanvasRoot;

        private RectTransform BuildDropdown(string name,
            System.Collections.Generic.List<MenuEntry> entries)
        {
            // 全屏点击拦截层：点空白处关菜单，同时挡住下面板子的点击。
            if (menuBlocker == null)
            {
                // **让开菜单条本身。** 全屏拦截面如果盖住菜单条，第二次点
                // 别的菜单会先被它接住（表现为「点了没反应、要再点一下」）。
                float blockTop = HeaderHeight + MenuBarHeight;
                Image blocker = CreateImage("MenuBlocker", EnsurePopupCanvas(),
                    0f, blockTop, DesignWidth, DesignHeight - blockTop,
                    new Color(0f, 0f, 0f, 0f));
                blocker.raycastTarget = true;
                Button blockerButton = blocker.gameObject.AddComponent<Button>();
                blockerButton.transition = Selectable.Transition.None;
                blockerButton.onClick.AddListener(CloseMenu);
                menuBlocker = blocker.rectTransform;
                menuBlocker.gameObject.SetActive(false);
            }

            float width = 240f;
            float rowHeight = 28f;
            float pad = 6f;
            float height = pad * 2f;
            for (int i = 0; i < entries.Count; i++)
            {
                height += entries[i].SeparatorBefore ? rowHeight + 9f : rowHeight;
            }

            Image panel = CreateImage("Menu_" + name, EnsurePopupCanvas(),
                0f, 0f, width, height, MenuDropdownFill);
            panel.raycastTarget = true;
            panel.sprite = GetRoundedSprite(20, 8f, false, 0f);
            panel.type = Image.Type.Sliced;
            RectTransform rect = panel.rectTransform;
            rect.SetAsLastSibling();
            rect.gameObject.SetActive(false);

            float rowY = pad;
            for (int i = 0; i < entries.Count; i++)
            {
                MenuEntry entry = entries[i];
                if (entry.SeparatorBefore)
                {
                    CreateImage("MenuSep_" + i, rect, 8f, rowY + 4f, width - 16f, 1f, PanelRule)
                        .raycastTarget = false;
                    rowY += 9f;
                }

                TextMeshProUGUI label;
                MenuEntry captured = entry;
                Button row = CreateButton("MenuRow_" + i, rect, 4f, rowY, width - 8f,
                    rowHeight, entry.Label, false, delegate
                    {
                        Main.Logger.Log("[菜单] 点击条目 " + captured.Label
                            + " action=" + (captured.Action != null));
                        if (captured.Action != null)
                        {
                            CloseMenu();
                            captured.Action();
                        }
                    }, out label);
                label.fontSize = 13f;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.color = entry.Placeholder ? TextMuted : TextPrimary;
                row.gameObject.name = "MenuRow_" + i;

                if (!string.IsNullOrEmpty(entry.Shortcut))
                {
                    CreateText("MenuKey_" + i, rect, width - 96f, rowY, 88f, rowHeight,
                        entry.Shortcut, 12f, TextMuted, TextAlignmentOptions.MidlineRight, false);
                }
                rowY += rowHeight;
            }

            return rect;
        }

        private void RefreshDropdown(RectTransform dropdown,
            System.Collections.Generic.List<MenuEntry> entries)
        {
            if (dropdown == null || entries == null)
            {
                return;
            }
            // 位置跟着按钮走：按钮的矩形在设计空间里就是它自己的位置。
            RectTransform anchor = null;
            string menuName = dropdown.name.StartsWith("Menu_")
                ? dropdown.name.Substring(5) : null;
            if (menuName != null)
            {
                buttonRectOf.TryGetValue(menuName, out anchor);
            }
            if (anchor != null)
            {
                dropdown.anchoredPosition = new Vector2(
                    anchor.anchoredPosition.x,
                    -(HeaderHeight + MenuBarHeight));
            }

            // 可用性与勾选状态每次打开时重算：只读模式、开关值都会变。
            for (int i = 0; i < entries.Count; i++)
            {
                Transform row = dropdown.Find("MenuRow_" + i);
                if (row == null)
                {
                    continue;
                }
                Button button = row.GetComponent<Button>();
                if (button == null)
                {
                    continue;
                }
                MenuEntry entry = entries[i];
                bool enabled = !entry.Placeholder
                    && (entry.EnabledWhen == null || entry.EnabledWhen());
                button.interactable = enabled;

                TextMeshProUGUI label = row.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    label.color = enabled ? TextPrimary : TextMuted;
                    bool on = entry.CheckedWhen != null && entry.CheckedWhen();
                    label.text = on ? "✓ " + entry.Label : entry.Label;
                }
            }
        }

        private System.Collections.Generic.List<MenuEntry> FileMenuEntries()
        {
            System.Func<bool> edit = delegate { return IsEditMode; };
            return new System.Collections.Generic.List<MenuEntry>
            {
                new MenuEntry { Label = "打开", Shortcut = "Ctrl+O", EnabledWhen = edit,
                    Action = delegate { MenuOpenLevel(); } },
                new MenuEntry { Label = "撤销", Shortcut = "Ctrl+Z", SeparatorBefore = true,
                    EnabledWhen = edit, Action = delegate { MenuUndo(); } },
                new MenuEntry { Label = "重做", Shortcut = "Ctrl+Y", EnabledWhen = edit,
                    Action = delegate { MenuRedo(); } },
                new MenuEntry { Label = "保存", Shortcut = "Ctrl+S", SeparatorBefore = true,
                    EnabledWhen = edit, Action = delegate { MenuSaveLevel(); } },
                new MenuEntry { Label = "另存为", Shortcut = "Ctrl+Shift+S", EnabledWhen = edit,
                    Action = delegate { MenuSaveLevelAs(); } },
            };
        }

        private System.Collections.Generic.List<MenuEntry> ProjectMenuEntries()
        {
            System.Func<bool> edit = delegate { return IsEditMode; };
            return new System.Collections.Generic.List<MenuEntry>
            {
                new MenuEntry { Label = "重置时间轴排版", EnabledWhen = edit,
                    Action = delegate { MenuResetTimelineLayout(); } },
                new MenuEntry { Label = "保存工程", SeparatorBefore = true, EnabledWhen = edit,
                    Action = delegate { MenuSaveProject(); } },
                new MenuEntry { Label = "另存工程（开发中）", Placeholder = true },
            };
        }

        private System.Collections.Generic.List<MenuEntry> PreferenceMenuEntries()
        {
            return new System.Collections.Generic.List<MenuEntry>
            {
                new MenuEntry { Label = "（开发中）", Placeholder = true },
            };
        }

        private System.Collections.Generic.List<MenuEntry> SettingsMenuEntries()
        {
            var list = new System.Collections.Generic.List<MenuEntry>
            {
                new MenuEntry { Label = "默认设置（开发中）", Placeholder = true },
                new MenuEntry { Label = "开发者：探针总闸", SeparatorBefore = true,
                    CheckedWhen = delegate { return SettingOn("VerboseDiagnostics"); },
                    Action = delegate { ToggleSetting("VerboseDiagnostics"); } },
                new MenuEntry { Label = "开发者：简化装饰物遮罩",
                    CheckedWhen = delegate { return SettingOn("SimplifyDecorationMasks"); },
                    Action = delegate { ToggleSetting("SimplifyDecorationMasks"); } },
                new MenuEntry { Label = "清空媒体缓存", SeparatorBefore = true,
                    Action = delegate { ClearMediaCacheEverywhere(); } },
            };
            return list;
        }

        private static bool SettingOn(string key)
        {
            Settings settings = Main.Settings;
            if (settings == null)
            {
                return false;
            }
            switch (key)
            {
                case "VerboseDiagnostics": return settings.VerboseDiagnostics;
                case "SimplifyDecorationMasks": return settings.SimplifyDecorationMasks;
            }
            return false;
        }

        private void ToggleSetting(string key)
        {
            Settings settings = Main.Settings;
            if (settings == null)
            {
                return;
            }
            switch (key)
            {
                case "VerboseDiagnostics":
                    settings.VerboseDiagnostics = !settings.VerboseDiagnostics;
                    break;
                case "SimplifyDecorationMasks":
                    settings.SimplifyDecorationMasks = !settings.SimplifyDecorationMasks;
                    break;
            }
            Main.SaveSettings();
        }

        private void BuildHeader(RectTransform parent)
        {
            // 顶栏现在**只有 uGUI 这一份**（ImGui 那条带子已经删掉），
            // 所以底色也必须自己铺：取和模块块同一档的 SurfaceTopBand，
            // 否则只剩文字浮在游戏画面上。
            Image header = CreateImage("Header", parent, 0f, 0f, DesignWidth, HeaderHeight,
                UiTheme.Current.SurfaceTopBand);
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

            // 状态读数**不在顶栏画了**。
            //
            // ImGui 版早就把它挪进预览标题行（顶栏中段被「编辑/只读」占着，
            // 长文本会被压掉半截），uGUI 这边由 `UiPreviewChrome` 显示同一份
            // `StatusText`。这里只保留这颗文本当**数据源**，节点本身不显示 ——
            // 留着会变成两份状态、位置还不对。
            statusLabel = CreateText(
                "Status", header.rectTransform, StatusLabelX, 13f, 900f, 28f,
                string.Empty, 13f, Accent, TextAlignmentOptions.MidlineLeft, false);
            statusLabel.color = Accent;
            statusLabel.gameObject.SetActive(false);

            // 提示改成"点左下角按钮" —— 快捷键已经取消（见 AesOpenButton）。
            CreateText("Shortcut", header.rectTransform, DesignWidth - 230f, 13f, 206f, 28f,
                "左下角按钮  打开工作台  ·  Esc  关闭",
                13f, TextMuted, TextAlignmentOptions.MidlineRight, false);

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

            BuildModeSwitch(header.rectTransform);
        }

        /// <summary>
        /// 顶栏正中的模式切换（编辑 / 只读）。
        ///
        /// 这一颗是 ImGui 顶栏里唯一一处「状态本身就是入口」的控件：
        /// 文字写当前模式，点一下切另一档。迁移时照搬这个语义 ——
        /// 编辑模式下底色换成强调色，扫一眼就知道现在能改。
        /// </summary>
        private void BuildModeSwitch(RectTransform parent)
        {
            // **贴住顶边、去掉顶边那条描边。**
            // 于是它读起来是「从顶栏上沿垂下来的一枚梯形标签」，
            // 而不是一颗浮在带子中间的按钮。
            const float height = 40f;
            const float width = 150f;
            float x = (DesignWidth - width) * 0.5f;
            float y = 0f;

            // **倒梯形、纯黑填充、纯黑描边、没有别的颜色。**
            //
            // 形状用运行时生成的贴图（四边形 + 一圈描边）而不是矩形按钮：
            // 用户明确要梯形，而 uGUI 的 Image 只能画矩形，除非自带网格。
            // 一格 4 像素的贴图按原尺寸绘制，不会因为缩放糊掉。
            RectTransform rect = CreateTopLeftRect("ModeSwitch", parent, x, y, width, height);
            Image face = rect.gameObject.AddComponent<Image>();
            // **只描边、不填充**：梯形内部透空，只有一圈纯黑描边。
            face.sprite = GetTrapezoidSprite(
                Mathf.RoundToInt(width), Mathf.RoundToInt(height), 11, 3,
                Color.clear, Color.black);
            face.type = Image.Type.Simple;
            face.raycastTarget = true;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { SetEditMode(!IsEditMode); });
            modeSwitchButton = button;

            TextMeshProUGUI label = CreateText("Label", rect, 0f, 0f, width, height,
                "编辑模式", UiType.Body, TextPrimary, TextAlignmentOptions.Center, false);
            modeSwitchLabel = label;
            modeSwitchState = null;
            RefreshModeSwitch();
        }

        /// <summary>倒梯形贴图（填充 + 一圈描边），按参数缓存，只生成一次。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> trapezoidSprites
            = new System.Collections.Generic.Dictionary<string, Sprite>();

        private static Sprite GetTrapezoidSprite(int width, int height, int slant,
            int thickness, Color fill, Color border)
        {
            string key = width + "x" + height + "/" + slant + "/" + thickness
                + "/" + fill + "/" + border;
            Sprite cached;
            if (trapezoidSprites.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 fillColor = fill;
            Color32 borderColor = border;
            Color32[] pixels = new Color32[width * height];

            for (int py = 0; py < height; py++)
            {
                // py 从下往上：底边窄、顶边宽（倒梯形）。
                float t = height <= 1 ? 0f : py / (float)(height - 1);
                float inset = slant * (1f - t);
                int left = Mathf.RoundToInt(inset);
                int right = width - 1 - Mathf.RoundToInt(inset);
                for (int px = 0; px < width; px++)
                {
                    bool inside = px >= left && px <= right;
                    if (!inside)
                    {
                        pixels[py * width + px] = clear;
                        continue;
                    }
                    // 描边按 thickness 向内长，外侧不额外偏移 ——
                    // 这样「梯形外边到顶栏边缘的留白」与描边粗细一致。
                    // **顶边不画**：这条边贴着顶栏上沿，画出来就是一条
                    // 横穿画面的黑线。只留左、右、下三条。
                    bool onEdge = py < thickness
                        || (px - left) < thickness || (right - px) < thickness;
                    pixels[py * width + px] = onEdge ? borderColor : fillColor;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            Sprite sprite = Sprite.Create(texture,
                new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
            trapezoidSprites[key] = sprite;
            return sprite;
        }

        private UiPreviewChrome.Parts previewChrome;

        private Button modeSwitchButton;
        private TextMeshProUGUI modeSwitchLabel;
        private string modeSwitchState;

        /// <summary>模式按钮的显示：文字写「当前模式」，编辑态用强调色底。</summary>
        private void RefreshModeSwitch()
        {
            if (modeSwitchButton == null || modeSwitchLabel == null)
            {
                return;
            }
            bool edit = IsEditMode;
            string state = edit ? "编辑模式" : "只读模式";
            if (state == modeSwitchState)
            {
                return;
            }
            modeSwitchState = state;
            modeSwitchLabel.text = state;
            // **编辑模式也不给黑字。** 只读态用灰字区分，编辑态用白字。
            modeSwitchLabel.color = edit ? TextPrimary : TextMuted;
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
        /// 给它最高的 sortingOrder（32767），所以永远在最上面 —— 工作台那层由它自己压。
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
            // **32767 就是上限，不能再往上加。**
            //
            // **试过 32768，结果是黑屏整个掉到工作台底下** —— Unity 排画布用的是
            // short，超了上限会翻成负数，越"高"越沉底。
            //
            // 但光站住 32767 还不够：工作台那层 ImGui 也在这个数上，谁压谁没准。
            // 真正让它盖满全屏的是**加载期间把 ImGui 压到 0**
            // （见 `SetImGuiSuppressedForLoading`），这里只是自己站到最高位。
            loading.sortingOrder = 32767;
            loading.pixelPerfect = false;

            CanvasScaler scaler = loadingCanvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(DesignWidth, DesignHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            // 遮罩本体是 canvas 的子物体，这样「隐藏」只关它、不关 canvas。
            RectTransform screenRoot = CreateTopLeftRect("Screen", canvasRect, 0f, 0f,
                DesignWidth, DesignHeight);
            // **铺满整块画布，别用固定尺寸。**
            //
            // 上面那个 `DesignWidth × DesignHeight` 是"设计尺寸"，屏幕比它宽或
            // 高的时候（比如 2560 宽的屏），遮罩只盖住左上角一块，
            // 右边和下边会露出来 —— 用户看到的就是"黑屏没覆盖完全"。
            // 拉伸锚点之后它跟着画布走，任何分辨率都盖满。
            screenRoot.anchorMin = Vector2.zero;
            screenRoot.anchorMax = Vector2.one;
            screenRoot.offsetMin = Vector2.zero;
            screenRoot.offsetMax = Vector2.zero;
            // pivot 也要回正：`CreateTopLeftRect` 把它定在左上角 (0,1)，
            // 而铺满的算法按"锚点即四角"推 rect，pivot 停在角上会整体偏出去。
            screenRoot.pivot = new Vector2(0.5f, 0.5f);

            loadingScreen = screenRoot.gameObject.AddComponent<Multiplayer.MultiplayerLoadingScreen>();
            loadingScreen.Initialize(screenRoot, uiFont, GetRoundedSprite(24, CornerLarge, false, 0f));
            // 加载页自己站在 32767 这一档（见上面那段分层说明），压层那套
            // 是给已经删掉的 ImGui 画布用的，现在不需要了。
        }


        /// <summary>
        /// 加入房间期间的加载遮罩。会话层只描述状态，显示由这里做。
        ///
        /// **预载媒体时不要收它。** 这块遮罩现在两头共用：联机加入、以及
        /// F8 打开工作台时的媒体预载（见 `UpdateMediaPreload`）。
        /// 原来这里只要「不是联机加载中」就无条件 Hide，于是预载刚 Show 出来
        /// 就被它抹掉 —— 一帧都看不到，表现就是「加载页根本没出现」。
        /// </summary>
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

            if (!mediaPreloading && loadingScreen != null && loadingScreen.IsShowing)
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
        /// <summary>
        /// mod 的版本号 —— 顶栏和更新说明都显示它。
        ///
        /// **从 UMM 读，不在这里写第二份。** 原来这里是个 `const "0.29.2"`，
        /// 而真实版本在 `Info.json` 里 —— 两处各写一份，升版本时漏改一处
        /// 就会出现「顶栏写着 0.29.2、实际装的是 0.31.0」。
        /// （`pack-release.ps1` 当初为了避免同一个坑，程序集名也是从 `Info.json` 读的。）
        /// </summary>
        internal static string ModVersion
        {
            get
            {
                var entry = Main.Mod;
                return entry == null ? "0.0.0" : entry.Version.ToString();
            }
        }

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
            "<b>新增</b>",
            "<b>· 打开落格</b>：打开工作台时，播放头直接落到原生编辑器当前选中的那一格。",
            "<b>· 点轨道跟随</b>（标题行准星按钮，Ctrl+J）：打开后在预览里点轨道，画面 1 秒内加速走过去；"
                + "关着就只改选中、不动画面。",
            "<b>· 点事件跟随</b>（标题行「指针指块」那颗）：打开后在时间轴上点事件块，画面 1 秒内加速走过去。"
                + "和上面那颗是两件事：一个管轨道、一个管事件块，手感一致。",
            "<b>· 定位到播放头</b>：把时间轴横向滚到播放头位置（标题行那颗按钮，Ctrl+K）。",
            "   自动翻页已取消 —— 视图不再跟着播放头跑，横向滚动完全归你。",
            "",
            "<b>修好的</b>",
            "<b>· 行星相对装饰物</b>：relativeTo 红 / 蓝 / 绿球的装饰物在工作台里塌到世界原点，已修正。",
            "<b>· 粒子 EmitParticle</b>：会在事件之后重复炸开，已修正 —— 只在播放头跨过事件时炸一次。",
            "<b>· 媒体加载页</b>：媒体多于 512 个的谱面会卡在「正在加载媒体 512 / N」永不关闭，已修正。",
            "<b>· 组合运镜</b>：MoveCamera 的 rotation 在 relativeTo: LastPosition 下按相对量处理，",
            "   连续组合运镜不再每拍回甩到 0。",
            "<b>· 粒子透明剔除</b>：发射器不再被「透明度 0」剔除 —— 原生里 opacity 0 的发射器照样在喷，",
            "   727 格之后发射器的角度和位置不再错。",
            "<b>· 粒子 autoPlay</b>：写了显式 Start 的粒子不再替原生补 autoPlay，",
            "   992 格附近不会多出不该出现的发射器。",
            "<b>· 起播剪音乐</b>：不再剪掉开头 0.1 秒 —— 片段从目标内容开始，画面定帧等音频，",
            "   音画仍然同步。",
            "<b>· 加载遮罩</b>：遮罩期间按空格会在黑屏底下播歌，已修正。",
            "<b>· 镜头偏移</b>：relativeTo: Player + position: [null,null] 的运镜没有归零，已修正。",
            "<b>· 镜厅段</b>：相机甩远的那一段出现重影 / 抽搐 / 黑闪 / 节奏不均，已修正。",
            "<b>· 功能快捷键</b>：点轨道跟随 / 定位到播放头，工作台里改用 Ctrl+J / Ctrl+K。",
            "<b>· 自动翻页开关</b>：关不掉、以及开着时抢手动横向滚动，已修正。",
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

        // ---------------------------------------------------------- 两个要答复的弹窗

        /// <summary>
        /// 遮罩 + 居中卡片的外壳。两个弹窗共用 —— 它们的差别只有正文和按钮，
        /// 而遮罩必须吃点击（否则点下去会穿到后面的工作台上偷偷改东西）。
        /// </summary>
        private RectTransform BuildDialogShell(string name, RectTransform parent,
            float cardWidth, float cardHeight, out RectTransform card)
        {
            RectTransform layer = CreateTopLeftRect(name + "Layer", parent,
                0f, 0f, DesignWidth, DesignHeight);
            layer.gameObject.SetActive(false);

            Image shade = CreateTopLeftRect(name + "Shade", layer,
                0f, 0f, DesignWidth, DesignHeight).gameObject.AddComponent<Image>();
            shade.color = new Color(0f, 0f, 0f, 0.55f);
            shade.raycastTarget = true;

            card = CreateRoundedPanel(name + "Card", layer,
                (DesignWidth - cardWidth) * 0.5f, (DesignHeight - cardHeight) * 0.5f,
                cardWidth, cardHeight, Panel, CornerLarge).rectTransform;
            Image cardImage = card.GetComponent<Image>();
            AddPanelBorder(cardImage, CornerLarge);
            cardImage.raycastTarget = true;
            return layer;
        }

        /// <summary>
        /// 「创建工程」：切到编辑模式但这张谱还没有工程时推开。
        ///
        /// 名字给一个默认值（谱面文件名），用户可以改；路径只读展示，
        /// 让人知道东西会落在哪。**Esc / × 都算「不建」**。
        /// </summary>
        private void BuildProjectCreateWindow(RectTransform parent)
        {
            const float cardWidth = 620f;
            const float cardHeight = 300f;
            RectTransform card;
            projectCreateWindow = new ProjectCreateWindow();
            projectCreateWindow.Layer = BuildDialogShell("ProjectCreate", parent,
                cardWidth, cardHeight, out card);

            CreateText("Title", card, 28f, 18f, cardWidth - 90f, 30f, "创建工程",
                UiType.Title, AccentSecondary, TextAlignmentOptions.MidlineLeft, true);
            CreateHeadingRule("Rule", card, PanelHeadingRule);

            CreateButton("Close", card, cardWidth - 52f, 18f, 30f, 30f, "×", false,
                delegate { CancelProjectCreate(); }, out TextMeshProUGUI closeLabel);
            closeLabel.fontSize = UiType.Body;

            CreateText("Intro", card, 28f, 66f, cardWidth - 56f, 44f,
                "这张谱还没有工作台工程。起个名字就行 —— 布局、时间轴摆放都会存在工程目录里。",
                14f, TextSecondary, TextAlignmentOptions.TopLeft, false);

            // 输入框：和媒体搜索框同一套搭法（对象先关掉再接 TMP_InputField，
            // 否则光标建不出来）。
            RectTransform fieldRect = CreateTopLeftRect("NameField", card, 28f, 118f,
                cardWidth - 56f, 44f);
            fieldRect.gameObject.SetActive(false);
            Image fieldBackground = fieldRect.gameObject.AddComponent<Image>();
            fieldBackground.color = FieldWell;
            fieldBackground.raycastTarget = true;
            CreateImage("FieldRule", fieldRect, 0f, 43f, cardWidth - 56f, 1f, PanelRule)
                .raycastTarget = false;

            TMP_InputField field = fieldRect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = fieldBackground;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.customCaretColor = true;
            field.caretColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.textViewport = fieldRect;
            field.textComponent = CreateStretchedFieldText("Text", fieldRect, 10f,
                string.Empty, 16f, TextPrimary, uiFont);
            field.placeholder = CreateStretchedFieldText("Placeholder", fieldRect, 10f,
                "工程名", 16f, TextSecondary, uiFont);
            field.characterLimit = 60;
            fieldRect.gameObject.SetActive(true);
            projectCreateWindow.NameField = field;

            TextMeshProUGUI pathLabel = CreateText("PathLabel", card, 28f, 168f,
                cardWidth - 56f, 40f, string.Empty, 12.5f, TextMuted,
                TextAlignmentOptions.TopLeft, false);
            pathLabel.overflowMode = TextOverflowModes.Ellipsis;
            projectCreateWindow.PathLabel = pathLabel;

            CreateButton("Create", card, cardWidth - 28f - 100f - 12f - 100f, cardHeight - 68f,
                100f, 40f, "创建", true, ConfirmProjectCreate, out _);
            CreateButton("Cancel", card, cardWidth - 28f - 100f, cardHeight - 68f,
                100f, 40f, "取消", false, CancelProjectCreate, out _);
        }

        /// <summary>
        /// 「布局还没存」：关工作台 / 切只读之前拦一下。
        ///
        /// 三个回答：保存（存完继续那个动作）、不保存（直接继续）、取消（什么都不做）。
        /// </summary>
        private void BuildConfirmSaveWindow(RectTransform parent)
        {
            const float cardWidth = 640f;
            const float cardHeight = 260f;
            RectTransform card;
            confirmSaveWindow = new ConfirmSaveWindow();
            confirmSaveWindow.Layer = BuildDialogShell("ConfirmSave", parent,
                cardWidth, cardHeight, out card);

            CreateText("Title", card, 28f, 18f, cardWidth - 90f, 30f,
                "布局还没保存", UiType.Title, AccentSecondary,
                TextAlignmentOptions.MidlineLeft, true);
            CreateHeadingRule("Rule", card, PanelHeadingRule);

            CreateText("Body", card, 28f, 68f, cardWidth - 56f, 60f,
                "时间轴的摆放改过、还没存进工程。现在离开，这次的调整会丢。",
                15f, TextSecondary, TextAlignmentOptions.TopLeft, false);

            float y = cardHeight - 68f;
            CreateButton("Cancel", card, cardWidth - 28f - 96f, y, 96f, 40f, "取消", false,
                delegate { AnswerConfirmSave(3); }, out _);
            CreateButton("Discard", card, cardWidth - 28f - 96f - 12f - 96f, y, 96f, 40f,
                "不保存", false, delegate { AnswerConfirmSave(2); }, out _);
            CreateButton("Save", card, cardWidth - 28f - 96f - 12f - 96f - 12f - 96f, y,
                96f, 40f, "保存", true, delegate { AnswerConfirmSave(1); }, out _);
        }

        /// <summary>
        /// 推开「创建工程」。默认名和路径由工作台算好传进来 ——
        /// 只有它知道谱面在哪、目录按哪套约定拼。
        /// </summary>
        private void OpenProjectCreate(string defaultName, string path)
        {
            if (projectCreateWindow == null || projectCreateWindow.Layer == null)
            {
                return;
            }
            projectCreateWindow.Confirmed = false;
            projectCreateWindow.Cancelled = false;
            string name = string.IsNullOrEmpty(defaultName) ? "新建工程" : defaultName;
            if (projectCreateWindow.NameField != null)
            {
                projectCreateWindow.NameField.SetTextWithoutNotify(name);
            }
            if (projectCreateWindow.PathLabel != null)
            {
                projectCreateWindow.PathLabel.text = "会建到：" + (path ?? string.Empty);
            }
            projectCreateWindow.Layer.SetAsLastSibling();
            projectCreateWindow.Layer.gameObject.SetActive(true);
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
                if (projectCreateWindow.NameField != null)
                {
                    projectCreateWindow.NameField.ActivateInputField();
                }
            }
        }

        /// <summary>把这一屏当「取消」关掉 —— Esc、×、取消按钮都走这里。</summary>
        private void CancelProjectCreate()
        {
            if (projectCreateWindow == null || !projectCreateWindow.IsOpen)
            {
                return;
            }
            projectCreateWindow.Cancelled = true;
            projectCreateWindow.Layer.gameObject.SetActive(false);
        }

        private void ConfirmProjectCreate()
        {
            if (projectCreateWindow == null || !projectCreateWindow.IsOpen)
            {
                return;
            }
            projectCreateWindow.Confirmed = true;
            projectCreateWindow.Layer.gameObject.SetActive(false);
        }

        private bool ProjectCreateVisible
        {
            get { return projectCreateWindow != null && projectCreateWindow.IsOpen; }
        }

        /// <summary>
        /// 取一次用户在「创建工程」上的答复（取完就清）。返回 true 表示确实有答复：
        /// `confirmed` 为真 = 建（`name` 是他填的名字），为假 = 取消。
        /// </summary>
        private bool TakeProjectCreateResult(out string name, out bool confirmed)
        {
            name = projectCreateWindow != null && projectCreateWindow.NameField != null
                ? projectCreateWindow.NameField.text
                : string.Empty;
            confirmed = projectCreateWindow != null && projectCreateWindow.Confirmed;
            if (projectCreateWindow == null
                || (!projectCreateWindow.Confirmed && !projectCreateWindow.Cancelled))
            {
                return false;
            }
            projectCreateWindow.Confirmed = false;
            projectCreateWindow.Cancelled = false;
            return true;
        }

        /// <summary>推开「布局还没存」。</summary>
        private void OpenConfirmSave()
        {
            if (confirmSaveWindow == null || confirmSaveWindow.Layer == null)
            {
                return;
            }
            confirmSaveWindow.Answer = 0;
            confirmSaveWindow.Layer.SetAsLastSibling();
            confirmSaveWindow.Layer.gameObject.SetActive(true);
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }

        private bool ConfirmSaveVisible
        {
            get { return confirmSaveWindow != null && confirmSaveWindow.IsOpen; }
        }

        private void AnswerConfirmSave(int answer)
        {
            if (confirmSaveWindow == null || !confirmSaveWindow.IsOpen)
            {
                return;
            }
            confirmSaveWindow.Answer = answer;
            confirmSaveWindow.Layer.gameObject.SetActive(false);
        }

        /// <summary>取一次那扇窗的答复（取完就清）。返回 true 表示确实答了。</summary>
        private bool TakeConfirmSaveAnswer(out int answer)
        {
            answer = confirmSaveWindow != null ? confirmSaveWindow.Answer : 0;
            if (confirmSaveWindow == null || confirmSaveWindow.Answer == 0)
            {
                return false;
            }
            confirmSaveWindow.Answer = 0;
            return true;
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

        /// <summary>「?」那颗按钮：开 / 关说明窗口。</summary>
        private void ToggleHelpPanel()
        {
            if (helpWindow != null)
            {
                ToggleWindow(helpWindow);
            }
        }

        /// <summary>
        /// 打开版本说明窗口。两个入口：左上角那串版本号，以及换了版本后第一次开工作台。
        ///
        /// 关掉时由 <see cref="ShowWindow"/> 记「这个版本看过了」。
        /// </summary>
        private void ShowVersionWindow()
        {
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
            float height = BlockRowBottom - BlockRowTop;

            // **左栏这块模块底色自己铺。**
            //
            // 交给 uGUI 之后 `DrawModuleBlocks` 就不再给浏览器块垫底（见那边对
            // UguiBrowser 的判断），不铺的话整块面板是透明的 —— 表现就是
            // 「文字浮在游戏画面上」。
            float blockX, blockY, blockWidth, blockHeight;
            GetBrowserBlockRect(out blockX, out blockY, out blockWidth, out blockHeight);
            Image blockFill = CreateImage("BrowserBlockFill", parent,
                blockX, blockY, blockWidth, blockHeight, UiTheme.Current.SurfaceTopBand);
            blockFill.raycastTarget = false;
            browserBlockFill = blockFill.rectTransform;

            // **顶底与预览块对齐**（用户要求：以预览窗口到顶栏的间距为准）。
            // 原来这里用 PreviewPanelY（LayoutTop + 12），比块行低 8px，
            // 于是左栏看着比预览"更贴顶"，三块的上沿不在一条线上。
            Image panel = CreateRoundedPanel("ProjectWorkspace", parent, OuterMargin,
                BlockRowTop, previewLayout.LeftPanelWidth, BlockRowBottom - BlockRowTop,
                ModuleFill, CornerLarge);
            panel.raycastTarget = true;
            AddPanelBorder(panel, CornerLarge);
            RectTransform root = panel.rectTransform;
            browserPanel = root;

            float innerWidth = previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset;
            float viewHeight = Mathf.Max(64f, height - BrowserViewY - PanelInnerInset);

            // 数据层：装饰物列表只保留数据与副作用入口，UI 全部由新的左栏画
            // （见 UiBrowserPane）。**不要再调 Initialize**，那会把旧骨架建出来。
            RectTransform modelHost = CreateTopLeftRect("DecorationModel", root,
                BrowserContentX, BrowserViewY, innerWidth, viewHeight);
            decoList = modelHost.gameObject.AddComponent<EditorDecorationList>();
            decoList.InitializeModel();

            // 新左栏：标签条 + 装饰物页 + 媒体页，三件都在 UiBrowserPane 里。
            browserPaneView = UiBrowserPane.Build(root, this, decoList, uiFont,
                BrowserContentX, BrowserViewY, innerWidth, viewHeight);

            RefreshProjectWorkspace(true);
            SetBrowserTab(BrowserPane.Decorations, LevelEventType.None);
        }

        /// <summary>
        /// 左栏内容的左缘。和别的面板同一条规矩：竖条之后才是内容，
        /// 所以标签和下面两页都从这个 x 起，而不是面板内边距那个 x。
        /// </summary>
        // **左栏内容左移贴边。** 原来等于 HeadingTextInset（四十来像素），
        // 于是装饰物行、标签、搜索框都从面板内侧很远的地方开始，
        // 左边空出一条看着很别扭。统一改成一个内缩 12px，和面板自身的
        // 内边距一致。
        private const float BrowserContentX = PanelInnerInset;
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

        /// <summary>标签清单：装饰物 / 媒体 / 六个设置页。</summary>
        internal static string[] BrowserTabTitles()
        {
            string[] titles = new string[2 + SettingsPageNames.Length];
            titles[0] = "装饰物";
            titles[1] = "媒体";
            for (int i = 0; i < SettingsPageNames.Length; i++)
            {
                titles[2 + i] = SettingsPageNames[i];
            }
            return titles;
        }

        /// <summary>当前选中的标签序号（与 BrowserTabTitles / SwitchBrowserTabByIndex 同序）。</summary>
        internal int CurrentBrowserTabIndex()
        {
            if (browserPane == BrowserPane.Media)
            {
                return 1;
            }
            if (browserPane == BrowserPane.Settings)
            {
                for (int i = 0; i < SettingsPages.Length; i++)
                {
                    if (SettingsPages[i] == selectedSettingsPage)
                    {
                        return 2 + i;
                    }
                }
                return 2;
            }
            return 0;
        }

        /// <summary>第 index 个标签对应的页与事件类型（与 BrowserTabTitles 同序）。</summary>
        internal void SwitchBrowserTabByIndex(int index)
        {
            if (index <= 0)
            {
                SetBrowserTab(BrowserPane.Decorations, LevelEventType.None);
            }
            else if (index == 1)
            {
                SetBrowserTab(BrowserPane.Media, LevelEventType.None);
            }
            else
            {
                int page = index - 2;
                if (page >= 0 && page < SettingsPages.Length)
                {
                    SetBrowserTab(BrowserPane.Settings, SettingsPages[page]);
                }
            }
        }

        private void SetBrowserTab(BrowserPane pane, LevelEventType page)
        {
            browserPane = pane;
            if (pane == BrowserPane.Settings)
            {
                selectedSettingsPage = page;
            }

            bool showMedia = pane == BrowserPane.Media;
            bool showSettings = pane == BrowserPane.Settings;

            // 装饰物页 / 媒体页都由新的左栏自己管显隐（见 UiBrowserPane）。
            // 数据层那个 MonoBehaviour 一直活着：批量隐藏/锁定是运行时状态，
            // 切页不该把它收掉，只有工作台整个关掉才轮到 ReleaseBatch。
            if (browserPaneView != null)
            {
                browserPaneView.ShowPage(pane);
            }

            // 设置页正文：和右栏共用同一个 uGUI 渲染器（单独的实例），
            // 只在停在这一页时显示。
            if (showSettings)
            {
                EnsureSettingsPaneView();
                if (settingsPaneView != null)
                {
                    settingsPaneView.gameObject.SetActive(true);
                    settingsPaneView.SetSettingsMode(true);
                    settingsPaneView.SetPaneRect(BrowserContentX, BrowserViewY,
                        previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset,
                        Mathf.Max(64f, browserPanel.rect.height - BrowserViewY - PanelInnerInset));
                }
            }
            else if (settingsPaneView != null)
            {
                settingsPaneView.SetSettingsMode(false);
                settingsPaneView.gameObject.SetActive(false);
            }

            // 媒体页刚从隐藏里回来：它在隐藏期间不推进缩略图队列，卡片位置也
            // 是按隐藏时的视口算的。这里重排一次，把位置和要加载的图都补上。
            if (showMedia)
            {
                RebuildVisibleMedia(false);
            }
        }

        /// <summary>
        /// 左栏设置页的渲染器。**懒建**：它要和右栏共用同一个写入层
        /// （`propertyPanel`），而右栏是在 `BuildInspector` 里才建出来的。
        /// </summary>
        private void EnsureSettingsPaneView()
        {
            if (settingsPaneView != null || browserPanel == null || propertyPanel == null)
            {
                return;
            }
            settingsPaneView = UiInspectorPane.Build(browserPanel, this, propertyPanel,
                uiFont, BrowserContentX, BrowserViewY,
                previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset,
                Mathf.Max(64f, browserPanel.rect.height - BrowserViewY - PanelInnerInset),
                true);
            settingsPaneView.gameObject.SetActive(false);
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
            // 左栏换成 UiBrowserPane 之后，媒体页不再有自己的 ScrollRect 尺寸对象；
            // 这里只按「工作台开着」判断，不再拿 mediaContent 当开关，
            // 否则缩略图队列永远不消费（表现是媒体页一直空着）。
            if (!visible)
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
            //
            // **预载期间是例外**：那时队列里压着全部媒体，而用户可能停在
            // 「装饰物」页 —— 按原来的判据队列就永远不消费，加载页卡在 0/N
            // 不动，看起来像死机。探针实测就是这么卡住的。
            if (browserPane != BrowserPane.Media && !mediaPreloading)
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

                // **先试着武装，成了才停滚动。** 只读模式下 `ArmMediaDrop` 会拒绝
                // （它最终会在预览视口里建一个装饰物，那是改谱面的动作）—— 那时不该
                // 把手势吞掉，列表照常滚动就行。
                if (!EditorSceneInteraction.ArmMediaDrop(item.FileName, item.AbsolutePath))
                {
                    return;
                }

                // 否则 ScrollRect 会把同一次拖动当成列表平移，
                // 在装饰物跟着光标走的同时把整列从指针底下滚走。
                // 手势一结束就恢复。
                if (mediaScroll != null)
                {
                    mediaScroll.enabled = false;
                }

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
            //
            // **预载期间换一套宽预算**（见那两个常量的说明）：那几帧加载页盖着
            // 屏幕，掉帧看不见，把等待从十几秒压到一秒上下才是要紧的。
            float budgetSeconds = mediaPreloading
                ? MediaThumbnailPreloadBudgetSeconds
                : MediaThumbnailBudgetSeconds;
            int decodeBudget = mediaPreloading
                ? MediaThumbnailPreloadMaxPerFrame
                : MediaThumbnailMaxPerFrame;
            float budgetEnd = Time.realtimeSinceStartup + budgetSeconds;
            while (mediaThumbnailQueue.Count > 0 && decodeBudget-- > 0)
            {
                MediaThumbnailRequest request = mediaThumbnailQueue.Dequeue();
                mediaThumbnailQueued.Remove(request.Key);
                if (mediaPreloading)
                {
                    // 预载的进度就是"出队了多少条"，和解码成功与否、和缓存有没有
                    // 被削都无关（见 mediaPreloadDrained 的说明）。
                    mediaPreloadDrained++;
                }
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

        /// <summary>
        /// 缩略图的**磁盘缓存目录**。
        ///
        /// 优先用设置里指定的（`Settings.MediaCacheDirectory`）；留空则用默认的
        /// **mod 目录下的 `MediaCache/`**。
        ///
        /// **刻意不放在谱面目录里** —— 那是玩家的地盘，往那儿写东西是越界；
        /// 而且放在谱面目录就意味着「只读模式打开一张谱」也会产生文件，
        /// 违背我们定的那条约定。缓存放 mod 目录之后，只读模式真的是零写入。
        ///
        /// 多张谱共用一份也没问题：缓存文件名里含源图的绝对路径，不会串。
        /// </summary>
        private string GetMediaCacheDirectory()
        {
            Settings settings = Main.Settings;
            if (settings != null && !string.IsNullOrWhiteSpace(settings.MediaCacheDirectory))
            {
                return settings.MediaCacheDirectory;
            }
            var mod = Main.Mod;
            if (mod == null || string.IsNullOrEmpty(mod.Path))
            {
                return null;
            }
            return Path.Combine(mod.Path, "MediaCache");
        }

        /// <summary>
        /// 缓存文件名：源路径 + 修改时间 的哈希（FNV-1a，不要求加密强度）。
        ///
        /// **必须带上修改时间**：只按路径算的话，用户换了同名贴图之后
        /// 看到的还是旧缩略图 —— 缓存看着像"坏了"，其实只是没失效。
        /// </summary>
        private static string GetMediaCacheFileName(MediaThumbnailRequest request)
        {
            return GetMediaCacheFileName(request.AbsolutePath, request.Key);
        }

        /// <summary>同上，但不依赖请求对象 —— 预载时要拿它探"磁盘上有没有"。</summary>
        private static string GetMediaCacheFileName(string absolutePath, string key)
        {
            long stamp = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(absolutePath)
                    && File.Exists(absolutePath))
                {
                    stamp = File.GetLastWriteTimeUtc(absolutePath).Ticks;
                }
            }
            catch
            {
                // 取不到时间就当 0 —— 缓存退化成"只按路径"，不影响正确性。
            }
            string raw = (absolutePath ?? key ?? "") + "|" + stamp;
            uint hash = 2166136261u;
            for (int i = 0; i < raw.Length; i++)
            {
                hash = (hash ^ raw[i]) * 16777619u;
            }
            return hash.ToString("x8") + ".png";
        }

        /// <summary>
        /// 从磁盘缓存读一张缩略图。没有、或者读坏了，都返回 false ——
        /// 调用方会退回"解原图"那条路，顺手把坏的覆盖掉。
        /// </summary>
        private bool TryLoadThumbnailFromDisk(MediaThumbnailRequest request, out Texture2D texture)
        {
            texture = null;
            string directory = GetMediaCacheDirectory();
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }
            try
            {
                string path = Path.Combine(directory, GetMediaCacheFileName(request));
                if (!File.Exists(path))
                {
                    return false;
                }
                byte[] bytes = File.ReadAllBytes(path);
                var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!TryLoadTextureImage(loaded, bytes))
                {
                    Destroy(loaded);
                    return false;
                }
                loaded.filterMode = FilterMode.Bilinear;
                loaded.wrapMode = TextureWrapMode.Clamp;
                texture = loaded;
                return true;
            }
            catch
            {
                // 缓存读坏了就当没有 —— 下一句会解原图并把它覆盖掉。
                return false;
            }
        }

        /// <summary>
        /// 把缩略图写到磁盘缓存。失败只记日志，不影响这一帧的显示 ——
        /// 缓存是"下次更快"，不是"这次能不能看见"。
        /// </summary>
        private void SaveThumbnailToDisk(MediaThumbnailRequest request, Texture2D texture)
        {
            string directory = GetMediaCacheDirectory();
            if (string.IsNullOrEmpty(directory) || texture == null)
            {
                return;
            }
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                byte[] png = EncodeTextureToPng(texture);
                if (png == null)
                {
                    return;   // 编不出来就当没缓存，不影响这一帧的显示
                }
                string path = Path.Combine(directory, GetMediaCacheFileName(request));
                File.WriteAllBytes(path, png);
            }
            catch (Exception exception)
            {
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("[缩略图缓存] 写盘失败：" + exception.Message);
                }
            }
        }

        private void DecodeMediaThumbnail(MediaThumbnailRequest request)
        {
            Texture2D texture = null;
            bool failed = true;

            // **先看磁盘缓存。** 有工程时缩略图会落盘，第二次打开直接读小图
            // （几十 KB），不用再解几 MB 的原图 —— 落盘缓存的意义全在这。
            if (TryLoadThumbnailFromDisk(request, out texture))
            {
                failed = false;
            }
            else
            {
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
                            // 解出来顺手存一份，下次就不用再解了。
                            SaveThumbnailToDisk(request, texture);
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

        /// <summary>
        /// **预载期间**的解码预算（比平时宽得多）。
        ///
        /// 平时那 6ms / 2 张是为了「列表滚动时别卡住每一帧」—— 解码是同步读源图，
        /// 一张就可能几十毫秒。但预载（F8 打开工作台）时屏幕被加载页盖着，
        /// **掉帧用户看不见**，而等待是实打实的：178 张按 6ms 限流要十几秒
        /// （实测「结束：10.17 秒」）。
        ///
        /// 放到 150ms / 6 张，同样的活半秒到一秒就完了 —— 用户少等十几秒，
        /// 代价只是那几帧帧率低，而那几帧本来就遮着。
        /// </summary>
        private const float MediaThumbnailPreloadBudgetSeconds = 0.15f;
        private const int MediaThumbnailPreloadMaxPerFrame = 6;

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

        /// <summary>反射找出来的 `ImageConversion.EncodeToPNG`。</summary>
        private static bool imageEncoderSearched;
        private static MethodInfo encodeToPngMethod;

        /// <summary>
        /// 把贴图编成 PNG 字节。
        ///
        /// **必须反射调 `ImageConversion.EncodeToPNG`** —— 和 `TryLoadTextureImage`
        /// 同一个原因：那个模块编译期引用不到，直接写 `texture.EncodeToPNG()`
        /// 编译器会报「Texture2D 未包含 EncodeToPNG 的定义」（已经踩过）。
        /// </summary>
        private static byte[] EncodeTextureToPng(Texture2D texture)
        {
            if (texture == null)
            {
                return null;
            }
            if (!imageEncoderSearched)
            {
                imageEncoderSearched = true;
                Type imageConversionType = Type.GetType(
                    "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false);
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
                    encodeToPngMethod = imageConversionType.GetMethod(
                        "EncodeToPNG",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(Texture2D) },
                        null);
                }
            }
            if (encodeToPngMethod == null)
            {
                return null;
            }
            return encodeToPngMethod.Invoke(null, new object[] { texture }) as byte[];
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

        /// <summary>
        /// 清空媒体缩略图缓存 —— **内存和磁盘都清**（开发者设置里那个菜单项）。
        ///
        /// 要验证「首次打开谱面」那条路径（没有缓存时媒体得现解码、加载页会显示）
        /// 点一下就行，不用跑到 mod 目录里手动删文件。
        ///
        /// **只删缓存目录里的文件** —— 不删目录本身，也不碰谱面、工程、设置。
        /// </summary>
        internal void ClearMediaCacheEverywhere()
        {
            int memoryBefore = mediaThumbnailCache.Count;
            ClearMediaThumbnailCache();

            int removedFiles = 0;
            string directory = GetMediaCacheDirectory();
            if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
            {
                try
                {
                    string[] files = System.IO.Directory.GetFiles(directory);
                    for (int i = 0; i < files.Length; i++)
                    {
                        try
                        {
                            System.IO.File.Delete(files[i]);
                            removedFiles++;
                        }
                        catch
                        {
                            // 被占用的跳过 —— 少删一两个不影响验证。
                        }
                    }
                }
                catch (Exception exception)
                {
                    Main.Logger.Warning("[媒体] 清磁盘缓存失败：" + exception.Message);
                }
            }

            Main.Logger.Log("[媒体] 缓存已清空：内存 " + memoryBefore + " 张、磁盘 "
                + removedFiles + " 个文件");
        }

        private void BuildPreview(RectTransform parent)
        {
            float width = previewLayout.StageWidth;
            // 透明根节点，描边与其他面板相同。它必须保持不填充：
            // 这个面板的 rect 横跨实时预览，在这里填不透明色会盖住
            // 游戏画面，而那正是这个面板存在的唯一理由。AddPanelBorder 画的是
            // 圆环而不是实心块，所以它只描出边缘、不遮挡任何东西。
            // **预览必须保持不填充。**
            //
            // 这个面板的 rect 正好盖在实时画面上，填任何不透明色都会把游戏
            // 画面挡住（实测：填完之后预览整块变黑）。它的「色块」由
            // 工具栏与四周留白给出，不是靠填底。
            Image panel = CreateImage("Preview", parent, previewLayout.StageX, PreviewPanelY, width, PreviewPanelHeight, Color.clear);
            RectTransform root = panel.rectTransform;
            previewPanel = root;
            // 用的是圆环变体，不是 AddPanelBorder：后者画实心圆角矩形，靠
            // 面板挡住它们的中间，而透明的预览做不到这一点。
            AddPanelOutline(panel, CornerLarge);

            // 工具栏底色用 Band，和另外三块面板的标题条同一档。
            // 原来用的是面板色加 0.97 不透明度——和面板几乎一样，整条读不出来，
            // 预览画面顶上就是一片平的黑。
            // 工具栏底衬保持透明：ImGui 版的底色由模块块统一给，这里不再自绘。
            Image toolbar = CreateRoundedPanel("Toolbar", root, 0f, 0f, width, 52f, Color.clear, CornerLarge);
            toolbar.raycastTarget = true;
            previewToolbar = toolbar.rectTransform;
            // 外壳全部交给新实现（见 UiPreviewChrome）：竖条 + 标题 + 状态读数。
            // 旧骨架里那套 TitleBar/Heading/HeadingRule/车道按钮不再使用。
            previewChrome = UiPreviewChrome.Build(previewToolbar, uiFont, width, 52f);
            previewHeading = previewChrome != null ? previewChrome.Title : null;

            // **车道筛选按钮不在这里建。**
            //
            // ImGui 版早就把它们搬到时间轴第一行的右边（预览标题行要让给状态
            // 读数）。这里再建一份就是两套按钮、位置还不对 —— 旧 uGUI 版正是
            // 这个状态。等时间轴迁过来时再接。
            // BuildLaneFilterButtons(root);

            // 缩放锁定以前放在这里。现在它是时间轴
            // 播放控制带上的一把锁按钮，同时也覆盖相机位置；
            // 两边都留等于同一个设置有两个控件。

            float stageY = previewLayout.StageY - PreviewPanelY;
            Image stage = CreateImage("Viewport", root, 0f, stageY, previewLayout.StageWidth, previewLayout.StageHeight, Color.clear);
            stage.raycastTarget = false;
            previewViewport = stage.rectTransform;
            previewHandoffImage = CreateStretchRawImage("NativeHandoffFrame", previewViewport, Color.white);
            previewHandoffImage.gameObject.SetActive(false);
            // **不给预览面板垫底色**：舞台以外保持纯黑。
            //
            // 试过铺 #0B0B0B 与其它面板齐平，结果窗口上下多出两条同色带，
            // 反而显得"框外面还有一圈"；纯黑把画面衬出来更干净（用户选择）。

            // 框细一点：1px、18% 白。黑底上仍看得出边界，又不抢画面。
            Color viewportBorder = new Color(1f, 1f, 1f, 0.18f);
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
            // **遮沿必须不透明。**
            //
            // 它们的作用是挡住"预览窗以外"的游戏原生编辑器界面。刚才把它们
            // 改透明，native UI 立刻从工作台底下透出来 —— 表现就是满屏重影
            // （两套面板叠在一起）。它们的矩形在 ApplyWorkspaceLayout 里会跟着
            // 布局重算，所以「拖时间轴高度」也不会再盖住画面。
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
            // 右栏的模块底色自己铺（ImGui 那层不再给参数栏垫底）。
            float blockX, blockY, blockWidth, blockHeight;
            GetInspectorBlockRect(out blockX, out blockY, out blockWidth, out blockHeight);
            Image blockFill = CreateImage("InspectorBlockFill", parent,
                blockX, blockY, blockWidth, blockHeight, UiTheme.Current.SurfaceTopBand);
            blockFill.raycastTarget = false;
            inspectorBlockFill = blockFill.rectTransform;

            Image panel = CreateRoundedPanel(
                "Inspector",
                parent,
                previewLayout.InspectorX,
                BlockRowTop,
                previewLayout.RightPanelWidth,
                BlockRowBottom - BlockRowTop,
                ModuleFill,
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

            // 数据/写入层：只接数据，不建旧 UI（见 InitializeModel）。
            propertyPanel = panel.gameObject.AddComponent<EditorPropertyPanel>();
            propertyPanel.InitializeModel(this, uiFont);

            // 新的 uGUI 参数栏。
            float paneTop = PanelHeadingRule + 8f;
            inspectorPane = UiInspectorPane.Build(panel.rectTransform, this, propertyPanel,
                uiFont, PanelInnerInset, paneTop,
                previewLayout.RightPanelWidth - PanelInnerInset * 2f,
                previewLayout.RightPanelWidth > 0f
                    ? BlockRowBottom - BlockRowTop - paneTop - PanelInnerInset
                    : 400f,
                false);
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
            // 时间轴这块模块底色自己铺（ImGui 那层不再给任何模块垫底）。
            float blockX, blockY, blockWidth, blockHeight;
            GetTimelineBlockRect(out blockX, out blockY, out blockWidth, out blockHeight);
            Image blockFill = CreateImage("TimelineBlockFill", parent,
                blockX, blockY, blockWidth, blockHeight, UiTheme.Current.SurfaceTopBand);
            blockFill.raycastTarget = false;
            timelineBlockFill = blockFill.rectTransform;

            float panelY = TimelinePanelTop;
            float panelHeight = timelineHeight - 12f;
            Image panel = CreateRoundedPanel("Timeline", parent, OuterMargin, panelY, TimelinePanelWidth, panelHeight, ModuleFill, CornerLarge);
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

            // 车道筛选按钮已经搬进时间轴第一行（见 EditorTimelineView.BuildUi），
            // 预览工具栏上不再建第二份。
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

        /// <summary>打开工作台时预载媒体缩略图的进度状态。</summary>
        private bool mediaPreloading;

        /// <summary>
        /// 打开工作台时"把播放头落到原生选中格"这个待办还挂着没有。
        /// 会话是异步起来的，所以不能只在打开那一帧做一次。
        /// </summary>
        private bool pendingSelectionJump;
        /// <summary>上面那个待办的截止时刻（unscaledTime）。超时就放弃。</summary>
        private float pendingSelectionJumpDeadline;
        private int mediaPreloadTotal;

        /// <summary>
        /// 这次预载里**需要解码**的条数，以及开跑时缓存里已有的条数。
        ///
        /// 进度不能直接拿 `mediaThumbnailCache.Count` 当"已完成" —— 那个缓存是**按
        /// 源图路径**存的、多张谱共用一份，别的谱留下的条目会把进度顶高，甚至超过
        /// 总数（标签会显示成「320 / 300」）。所以要减掉开跑时的基线，而且"本来就
        /// 有缓存、不用解码"的那些要单独算进去，否则进度永远到不了头。
        /// </summary>
        private int mediaPreloadPending;
        /// <summary>
        /// 预载开始时，这张谱有多少个媒体的缩略图**已经在磁盘缓存里**。
        ///
        /// 用来判断"是不是真的第一次加载"：内存缓存每次关工作台都会清
        /// （`ReleaseMediaTextures`），所以它区分不出首次与再次；磁盘缓存留得住。
        /// 只有一张都没有的时候才提示「首次加载较慢」。
        /// </summary>
        private int mediaPreloadCachedOnDisk;

        /// <summary>
        /// 预载开始时**已经有缩略图、不用再解码**的条数（= 总数 - 排进队列的数）。
        /// </summary>
        private int mediaPreloadAlreadyReady;

        /// <summary>
        /// 这次预载里**已经从队列里处理掉**的条数，每出队一条加一。
        ///
        /// 进度必须按它算，**不能**按 `mediaThumbnailCache.Count` 的增长算：
        /// 那个缓存有 512 条的上限（`MediaThumbnailCacheCapacity`），媒体一多，
        /// 解码还在继续、缓存却已经被 `TrimMediaThumbnailCache` 削回 512，
        /// 「缓存增长量」就永远停在 512。实测一张 943 个媒体的谱面就是这么
        /// 卡死在「正在加载媒体 512 / 943」上的——活其实干完了，计数器不动，
        /// `progress` 到不了 1，加载页永远不关。
        /// </summary>
        private int mediaPreloadDrained;

        private float mediaPreloadStartedAt;

        /// <summary>探针：只打前几帧，不刷屏。定位完就删。</summary>
        private int mediaPreloadLogCount;

        /// <summary>加载页至少显示这么久，免得一闪而过像故障。</summary>
        private const float MinimumLoadingScreenSeconds = 0.35f;

        /// <summary>
        /// 把所有媒体缩略图塞进解码队列，并撑起加载页。
        ///
        /// **这里只是「把活派下去」** —— 真正的解码在已有的缩略图队列里分帧做
        /// （`ProcessMediaThumbnailQueue` 有时间和数量双重预算），所以这一下
        /// 不会卡住任何一帧，加载页也就真的在动。
        ///
        /// 目的是「免得快速滚轮浏览图片卡顿」：不预载的话每张图都要等滚到
        /// 眼前才现解，一路滚下去就是一路顿。
        /// </summary>
        private void BeginMediaPreload()
        {
            // **先把遮罩建出来。** 它是懒加载的 —— 只在联机加入那条路上被
            // `EnsureLoadingScreen()` 创建过，平时是 null。不建就用，
            // 下面那一句永远走不通（探针实测：「没开始：loadingScreen 是 null」）。
            EnsureLoadingScreen();
            if (loadingScreen == null)
            {
                Main.Logger.Log("[加载页] 没开始：loadingScreen 建不出来");
                return;
            }
            // **缓速曲线也在这里预热。**
            //
            // 每种缓速第一次被画到时要现算 128 个采样点，一张用了好几种缓速的谱，
            // 这些计算全挤在进时间轴的头几帧 —— 用户看到的"曲线加载很慢"。
            // 加载页盖着的这段时间正好用来算完，等它收掉时曲线就是现成的。
            EditorTimelineView.PrecomputeEaseCurves();

            List<EditorProjectMediaItem> items = VisibleMedia;
            mediaPreloadTotal = items == null ? 0 : items.Count;
            mediaPreloadStartedAt = Time.unscaledTime;
            mediaPreloadLogCount = 0;
            mediaPreloading = true;

            // **先清队列的旧账。** 队列里可能还压着上一张谱入队、还没解码的请求
            // （实测过「总数 7、队列 48」这种）。它们跟这次预载没关系，留着只会
            // 让日志和进度都读不准；反正下面会把当前这张谱的要解码项重新入队。
            ClearMediaThumbnailQueue();

            // 顺便数一遍磁盘缓存里已经有多少张 —— 提示语要靠它判断"是不是真的第一次"。
            string cacheDirectory = GetMediaCacheDirectory();
            int queued = 0;
            int cachedOnDisk = 0;
            for (int i = 0; i < mediaPreloadTotal; i++)
            {
                EditorProjectMediaItem item = items[i];
                // 它自己会判断"有没有缓存"，没有就入队 —— 所以直接调就行。
                Texture2D texture;
                if (!TryGetMediaThumbnail(item, out texture))
                {
                    queued++;
                    string key = GetMediaThumbnailKey(item);
                    if (!string.IsNullOrEmpty(cacheDirectory)
                        && !string.IsNullOrWhiteSpace(key))
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(cacheDirectory,
                                GetMediaCacheFileName(item.AbsolutePath, key))))
                            {
                                cachedOnDisk++;
                            }
                        }
                        catch
                        {
                            // 路径不合法之类的，当成"磁盘上没有"就行。
                        }
                    }
                }
            }
            mediaPreloadPending = queued;
            mediaPreloadAlreadyReady = Mathf.Max(0, mediaPreloadTotal - queued);
            mediaPreloadDrained = 0;
            mediaPreloadCachedOnDisk = cachedOnDisk;
            Main.Logger.Log("[加载页] 开始：媒体 " + mediaPreloadTotal
                + " 个，其中要解码 " + queued + " 个（磁盘缓存已有 " + cachedOnDisk
                + " 个）；队列现有 " + mediaThumbnailQueue.Count);
        }

        /// <summary>
        /// 打开工作台时「播放头落到原生选中格」的待办，每帧重试一次。
        ///
        /// 打开是异步的：媒体预载那扇加载页可能还盖着，求值器与会话未必已经就绪，
        /// 那一帧直接跳会被挡掉。所以挂一个有时限的重试，落地一次就撤；
        /// 超时（3 秒）也要撤 —— 卡住不该让工作台一直重试。
        /// </summary>
        private void TickPendingSelectionJump()
        {
            if (!pendingSelectionJump)
            {
                return;
            }
            if (!visible || Time.unscaledTime > pendingSelectionJumpDeadline)
            {
                pendingSelectionJump = false;
                return;
            }
            if (EditorPlaybackBridge.JumpToNativeSelection())
            {
                pendingSelectionJump = false;
            }
        }

        /// <summary>
        /// 推进加载页。进度按**从队列里处理掉的条数**算，不数缓存 ——
        /// 那个缓存有 512 条上限，媒体一多就会被削回上限，增长量永远停在 512。
        /// </summary>
        private void UpdateMediaPreload()
        {
            if (!mediaPreloading)
            {
                return;
            }
            if (loadingScreen == null)
            {
                mediaPreloading = false;
                return;
            }

            int total = mediaPreloadTotal;
            // 「已经到位多少」= 开跑时就有的 + 这次从队列里处理掉的。
            //
            // **不能再用缓存增长量**：那个缓存有 512 条上限，媒体多于 512 时
            // 缓存会被 Trim 削回上限，增长量就永久停在 512 —— 943 个媒体的谱面
            // 会卡在「512 / 943」再也不动，加载页关不掉。出队计数不受这个影响。
            int done = Mathf.Clamp(mediaPreloadAlreadyReady + mediaPreloadDrained, 0, total);

            // 兜底：队列空了就说明活儿真的干完了。计数万一哪里算漏，也不能
            // 把用户永久锁在加载页上——那是最难自救的一种卡死。
            if (mediaThumbnailQueue.Count == 0)
            {
                done = total;
            }

            float progress = 1f;
            if (total > 0)
            {
                progress = Mathf.Clamp01((float)done / total);
            }

            if (mediaPreloadLogCount < 3)
            {
                mediaPreloadLogCount++;
                Main.Logger.Log("[加载页] 帧" + mediaPreloadLogCount
                    + "：已完成=" + done + "/" + total
                    + "（本次处理 " + mediaPreloadDrained + "/" + mediaPreloadPending
                    + "，开跑前已有 " + mediaPreloadAlreadyReady + "）"
                    + " 队列=" + mediaThumbnailQueue.Count
                    + " progress=" + progress.ToString("0.00")
                    + " showing=" + loadingScreen.IsShowing);
            }

            // 图少、或者全都命中缓存时，它会在同一帧里就到 100% ——
            // 闪一下反而像哪里坏了，所以压一个最短显示时间。
            if (progress >= 1f
                && Time.unscaledTime - mediaPreloadStartedAt >= MinimumLoadingScreenSeconds)
            {
                Main.Logger.Log("[加载页] 结束："
                    + (Time.unscaledTime - mediaPreloadStartedAt).ToString("0.00") + " 秒");
                mediaPreloading = false;
                loadingScreen.Hide();
                return;
            }

            // **只在「磁盘缓存里一张都没有」时提示。**
            //
            // 判据必须是磁盘，不是内存：内存缓存每次关工作台都会被
            // `ReleaseMediaTextures` 清掉，所以"再次打开"和"第一次打开"在内存里
            // 长得一模一样（实测连开三次都是「要解码 474 个」）。
            // 磁盘缓存留得住 —— 它为空才是真正的第一次加载。
            bool firstLoadOfThisChart = total > 0 && mediaPreloadCachedOnDisk == 0;
            loadingScreen.Show(
                total > 0
                    ? "正在加载媒体 " + done + " / " + total
                    : "正在准备时间轴",
                progress,
                firstLoadOfThisChart ? "首次加载较慢，请耐心等待" : null);
        }

        /// <summary>
        /// 编辑器左下角那颗按钮点开的入口。
        ///
        /// **必须先保证运行期 UI 建起来**：快捷键那条路一直有
        /// `if (!built && !TryBuildRuntimeUi()) return;` 这道门，而按钮这边最初
        /// 直接调了 `SetVisible(true)` —— `uiRoot` 还是 null，一进来就
        /// NullReferenceException，结果只画出顶栏和菜单栏、其余面板全缺，
        /// 原生面板还漏了出来（实测就是这么崩的）。
        /// </summary>
        internal void OpenFromEditorButton()
        {
            if (visible)
            {
                return;
            }
            if (!built && !TryBuildRuntimeUi())
            {
                Main.Logger.Log("[工作台] 左下角按钮打开失败：运行期 UI 没建起来");
                return;
            }
            SetVisible(true);
        }

        /// <summary>临时诊断：把指针下方最上面的几个 uGUI 元素打出来。</summary>
        private System.Collections.IEnumerator LogRaycastPeek()
        {
            yield return null;   // 等这一帧的布局/射线都就绪
            UnityEngine.EventSystems.EventSystem es =
                UnityEngine.EventSystems.EventSystem.current;
            if (es == null)
            {
                Main.Logger.Log("[点击取证] EventSystem 为空");
                yield break;
            }
            UnityEngine.EventSystems.PointerEventData eventData =
                new UnityEngine.EventSystems.PointerEventData(es)
                {
                    position = Input.mousePosition
                };
            System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult> hits
                = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(eventData, hits);
            string text = "[点击取证] 命中 " + hits.Count + " 个：";
            for (int i = 0; i < hits.Count && i < 4; i++)
            {
                text += " [" + i + "]" + hits[i].gameObject.name
                    + "(canvas=" + (hits[i].module != null ? hits[i].module.name : "?")
                    + ")";
            }
            Main.Logger.Log(text);
        }

        /// <summary>临时：媒体页所在画布的 sortingOrder，用来确认谁在上面。</summary>
        private int MediaLayerOrder()
        {
            if (mediaPanel == null)
            {
                return -1;
            }
            Canvas layer = mediaPanel.GetComponentInParent<Canvas>();
            return layer != null ? layer.sortingOrder : -2;
        }

        /// <summary>临时：把一个面板上的 Image 颜色写成 RGBA 简写。</summary>
        private static string PaneColor(RectTransform pane)
        {
            if (pane == null)
            {
                return "无";
            }
            Image image = pane.GetComponent<Image>();
            if (image == null)
            {
                return "无图";
            }
            Color c = image.color;
            return ((int)(c.r * 255f)) + "/" + ((int)(c.g * 255f)) + "/"
                + ((int)(c.b * 255f)) + "/" + c.a.ToString("0.00");
        }

        /// <summary>
        /// 临时：找出「设计坐标处、真正在画不透明深色」的 Image。
        ///
        /// 和射线探针的区别很关键：射线只看 `raycastTarget = true` 的 Graphic，
        /// 而盖住面板的往往是纯装饰层（raycast 全关）——所以必须遍历整棵树、
        /// 按矩形包含关系自己判断。
        /// </summary>
        private static string ProbeDrawn(float designX, float designY)
        {
            if (Main.Overlay == null || Main.Overlay.uiRoot == null)
            {
                return "无画布";
            }
            Image[] images = Main.Overlay.uiRoot.GetComponentsInChildren<Image>(true);
            string text = string.Empty;
            int found = 0;
            for (int i = 0; i < images.Length && found < 4; i++)
            {
                Image image = images[i];
                if (image == null || !image.gameObject.activeInHierarchy)
                {
                    continue;
                }
                Color c = image.color;
                if (c.a < 0.3f || c.r > 0.10f || c.g > 0.10f || c.b > 0.10f)
                {
                    continue;
                }
                RectTransform rect = image.rectTransform;
                Vector3[] corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                float scaleX = Screen.width / DesignWidth;
                float scaleY = Screen.height / DesignHeight;
                float px = designX * scaleX;
                float py = Screen.height - designY * scaleY;
                if (px < corners[0].x || px > corners[2].x
                    || py < corners[0].y || py > corners[2].y)
                {
                    continue;
                }
                text += (found == 0 ? "" : ">") + image.gameObject.name
                    + "(" + ((int)(c.r * 255f)) + "/" + c.a.ToString("0.00")
                    + "," + Mathf.RoundToInt(rect.rect.width) + "x"
                    + Mathf.RoundToInt(rect.rect.height) + ")";
                found++;
            }
            return text.Length == 0 ? "无非透明深色" : text;
        }

        private int lastPanelProbeFrame = -1000;
        private int layoutRefreshPending;

        /// <summary>临时：把一个面板的激活状态与矩形写成一行。</summary>
        private static string DescribePane(Component component, RectTransform rect)
        {
            if (component == null)
            {
                return "无";
            }
            if (rect == null)
            {
                return (component.gameObject.activeSelf ? "显示" : "隐藏") + "/无rect";
            }
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (component.gameObject.activeSelf ? "显示" : "隐藏")
                + "/" + Mathf.RoundToInt(rect.sizeDelta.x) + "x" + Mathf.RoundToInt(rect.sizeDelta.y)
                + "@" + Mathf.RoundToInt(corners[0].x) + "," + Mathf.RoundToInt(corners[0].y);
        }

        internal void SetVisible(bool value)
        {
            visible = value;

            // **打开后下一帧重排一次。**
            //
            // 建 UI 那一刻面板尺寸还没定（Screen/timelineHeight 都可能读到旧值），
            // 于是首次进去右侧参数栏是一小块，拖一次时间轴才变正常。
            // 这里只标一个标志，真正的重排放到 Update 里做 —— 那时布局已经稳了。
            layoutRefreshPending = 2;

            // 一次性诊断：打开工作台时把「谁在管 UI、谁可能在吃点击」打出来。
            if (value)
            {
                try
                {
                    Settings st = Main.Settings;
                    UnityEngine.EventSystems.EventSystem es =
                        UnityEngine.EventSystems.EventSystem.current;
                    int activeBlockers = 0;
                    GameObject imguiRoot = GameObject.Find("ADOFAIEditorStudio.ImGui");
                    if (imguiRoot != null)
                    {
                        Image[] images = imguiRoot.GetComponentsInChildren<Image>(true);
                        for (int i = 0; i < images.Length; i++)
                        {
                            if (images[i].gameObject.activeInHierarchy && images[i].raycastTarget)
                            {
                                activeBlockers++;
                            }
                        }
                    }
                    Main.Logger.Log("[UI模式] uiRoot=" + (uiRoot != null && uiRoot.activeInHierarchy)
                        + " canvasEnabled=" + (canvas != null && canvas.enabled)
                        + " order=" + (canvas != null ? canvas.sortingOrder : -1)
                        + " eventSystem=" + (es != null)
                        + " imGuiCanvas=" + (imguiRoot != null)
                        + " imGuiRaycastFaces=" + activeBlockers);
                }
                catch (System.Exception exception)
                {
                    Main.Logger.Warning("[UI模式] 诊断失败：" + exception.Message);
                }
            }

            // **关掉工作台就把整块画布停掉。**
            //
            // 原来只是把可见性标志置 false，画布本身仍然激活 —— 三万多个
            // uGUI 对象每帧照样参与合批与渲染。实测关闭状态下整帧 17ms
            // （原版 6ms），其中一大块就是这块「看不见但还在活」的画布。
            // 停掉根节点只影响渲染，各子物体的 activeSelf 原样保留，
            // 重新打开时完全恢复到关闭前的状态。
            if (uiRoot != null && uiRoot.activeSelf != value)
            {
                uiRoot.SetActive(value);
            }

            // 探针：打开工作台时把「点轨道跟随」开关的运行时值打出来。
            if (value && Main.VerboseLog)
            {
                Main.Logger.Log("[定位] 打开工作台：点轨道跟随="
                    + (Main.Settings.LocateOnTrackClick ? "开" : "关") + "。");
            }

            // 换了版本之后第一次打开才弹更新内容（见 ShowVersionNotesIfNew）。
            // 放这里而不是 F8 那一处：重开工作台、场景重载后重开，
            // 走的都是这条。
            if (visible)
            {
                try
                {
                    ShowVersionNotesIfNew();
                    // **先把媒体清单纯净了再预载。**
                    //
                    // 清单的刷新挂在每帧的 `TickProjectWorkspace` 里，而这里是打开工作台
                    // 的那一帧、跑在它前面 —— 直接预载的话读到的还是**上一张谱**的列表：
                    // 换到一张有上百个媒体的谱，加载页却按上一张的数量显示（比如「0/70」，
                    // 而那张谱刚好 70 个）。先把工作区刷新一遍，清单才是当前这张谱的。
                    RefreshProjectWorkspace(true);
                    // 打开工作台就把媒体铺开 —— 加载页同时是"预热"和"别让用户
                    // 看着空界面等"这两个作用。
                    BeginMediaPreload();
                }
                catch (Exception exception)
                {
                    // 打开过程中任何一步出错都不该让整个入口失效 —— 记下来继续。
                    Main.Logger.Log("[工作台] 打开过程异常：" + exception);
                }
            }

            if (!visible)
            {
                HidePreviewHandoffImmediate();
                nonPreviewPointerCapture = false;
                // 加载页要一起收 —— 否则关掉工作台它还在那儿盖着。
                mediaPreloading = false;
                if (loadingScreen != null && loadingScreen.IsShowing)
                {
                    loadingScreen.Hide();
                }
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

                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                    && uiRoot != null
                    && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(uiRoot.transform))
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            }
            else
            {
                try
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

                    // 打开工作台之后，播放头要落在原生编辑器**当前选中**的那一格。
                    //
                    // 不能在这里直接跳：打开是异步的（媒体预载那扇加载页还盖着，
                    // 求值器可能还没接管），直接调会被 "会话还没起来" 挡掉。
                    // 挂一个最多 3 秒的待办，由 TickPendingSelectionJump 每帧重试。
                    pendingSelectionJump = true;
                    pendingSelectionJumpDeadline = Time.unscaledTime + 3f;
                }
                catch (Exception exception)
                {
                    Main.Logger.Log("[工作台] 打开分支异常：" + exception);
                }
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
        // 纯图标：状态胶囊也不再垫圆角底，只留文字。
        private static readonly Color StatusPillFill = new Color(1f, 1f, 1f, 0f);

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
        internal static bool IsNativePlayModeRunning()
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
            // 固定播放头下 Shift+滚轮会开一次 scrub 会话来扫时间；会话挂着的时候
            // 播放起不来（求值器仍在"拖动定位"状态）。按空格先把它收掉。
            if (Timeline != null)
            {
                Timeline.CancelScrollScrub();
            }
            string ignored;
            EditorPlaybackBridge.TryTogglePlayback(out ignored);
            RefreshPlaybackState();
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
            // **统一用「块行」的上下沿。**
            //
            // 原来这里写的是 PreviewPanelY / PreviewPanelHeight，于是建 UI 时
            // 设的 BlockRowTop 会在下一帧被这行覆盖回去 —— 间距常量怎么调都
            // 看不出变化，这就是原因。
            float blockTop = BlockRowTop;
            float previewHeight = BlockRowBottom - blockTop;

            SetTopLeftRect(browserPanel, OuterMargin, blockTop, previewLayout.LeftPanelWidth, previewHeight);
            // 模块底色跟着块行的下沿走：拖时间轴高度会改 BlockRowBottom，
            // 不跟着改的话这块底要么留一条缝、要么压到时间轴上。
            if (browserBlockFill != null)
            {
                float blockX, blockY, blockWidth, blockHeight;
                GetBrowserBlockRect(out blockX, out blockY, out blockWidth, out blockHeight);
                SetTopLeftRect(browserBlockFill, blockX, blockY, blockWidth, blockHeight);
            }
            RelayoutProjectWorkspace(previewHeight);

            SetTopLeftRect(previewPanel, previewLayout.StageX, blockTop, previewLayout.StageWidth, previewHeight);
            SetTopLeftRect(previewToolbar, 0f, 0f, previewLayout.StageWidth, 52f);
            // 两个标题用**同一个 y**，否则它们不在一条水平线上。
            // 位置由这里唯一负责，创建时给的值会被覆盖，见 HeadingRowY 的说明。
            SetTopLeftRect(previewHeading, HeadingTextInset, HeadingRowY,
                160f, HeadingRowBoxHeight);
            // 视口相对预览面板定位：面板顶现在是 blockTop，偏移要跟着换。
            // 四条遮沿跟着舞台矩形重算：它们是按舞台算的，舞台一变（拖时间轴）
            // 不重算就会盖住画面或露出游戏原生界面。
            float stageY = previewLayout.StageY;
            float stageH = previewLayout.StageHeight;
            float stageX = previewLayout.StageX;
            float stageW = previewLayout.StageWidth;
            SetTopLeftRect(matteTop, 0f, 0f, DesignWidth, stageY);
            SetTopLeftRect(matteBottom, 0f, stageY + stageH,
                DesignWidth, DesignHeight - stageY - stageH);
            SetTopLeftRect(matteLeft, 0f, stageY, stageX, stageH);
            SetTopLeftRect(matteRight, stageX + stageW, stageY,
                DesignWidth - stageX - stageW, stageH);

            SetTopLeftRect(
                previewViewport,
                0f,
                previewLayout.StageY - blockTop,
                previewLayout.StageWidth,
                previewLayout.StageHeight
            );

            SetTopLeftRect(
                inspectorPanel,
                previewLayout.InspectorX,
                blockTop,
                previewLayout.RightPanelWidth,
                previewHeight
            );
            SetTopLeftRect(inspectorHeading, HeadingTextInset, HeadingRowY,
                previewLayout.RightPanelWidth - HeadingTextInset - PanelHeadingInset, HeadingRowBoxHeight);

            float inspectorBlockX, inspectorBlockY, inspectorBlockW, inspectorBlockH;
            GetInspectorBlockRect(out inspectorBlockX, out inspectorBlockY,
                out inspectorBlockW, out inspectorBlockH);
            if (inspectorBlockFill != null)
            {
                SetTopLeftRect(inspectorBlockFill, inspectorBlockX, inspectorBlockY,
                    inspectorBlockW, inspectorBlockH);
            }
            if (inspectorPane != null)
            {
                float paneTop = PanelHeadingRule + 8f;
                inspectorPane.SetPaneRect(PanelInnerInset, paneTop,
                    previewLayout.RightPanelWidth - PanelInnerInset * 2f,
                    previewHeight - paneTop - PanelInnerInset);
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
                OuterMargin,
                TimelinePanelTop,
                TimelinePanelWidth,
                timelineHeight - 12f
            );
            if (timelineBlockFill != null)
            {
                float blockFillX, blockFillY, blockFillW, blockFillH;
                GetTimelineBlockRect(out blockFillX, out blockFillY,
                    out blockFillW, out blockFillH);
                SetTopLeftRect(timelineBlockFill, blockFillX, blockFillY,
                    blockFillW, blockFillH);
            }
            if (timelineView != null)
            {
                timelineView.Relayout();
            }

        }

        private void RelayoutProjectWorkspace(float previewHeight)
        {
            if (browserPanel == null)
            {
                return;
            }

            float innerWidth = previewLayout.LeftPanelWidth - BrowserContentX - PanelHeadingInset;
            float viewHeight = Mathf.Max(64f, previewHeight - BrowserViewY - PanelInnerInset);

            // 左栏的尺寸入口只有这一个：标签条、装饰物页、媒体页都由它自己重排
            // （见 UiBrowserPane.SetPaneRect）。**不要再单独摆一次某一件** ——
            // 两份坐标迟早会不一致。
            if (browserPaneView != null)
            {
                browserPaneView.SetPaneRect(BrowserContentX, BrowserViewY, innerWidth, viewHeight);
            }
            if (settingsPaneView != null)
            {
                settingsPaneView.SetPaneRect(BrowserContentX, BrowserViewY,
                    innerWidth, viewHeight);
            }

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
            browserBlockFill = null;
            browserPaneView = null;
            settingsPaneView = null;
            decoList = null;
            helpWindow = null;
            versionWindow = null;
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
            inspectorPane = null;
            inspectorBlockFill = null;
            timelineBlockFill = null;
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

        private static TMP_FontAsset cachedSystemFont;

        /// <summary>
        /// 用系统里装的中文字体建一套 TMP 资源，优先级：微软雅黑 UI → 微软雅黑 → 思源黑体 → 黑体。
        ///
        /// 为什么不用 `Font.CreateDynamicFontFromOSFont` 的结果直接给 TMP：那是
        /// 老的 UnityEngine.Font，TMP 要的是自己的 FontAsset，中间得走
        /// `TMP_FontAsset.CreateFontAsset`。这个版本上后者支持动态图集，
        /// 缺字形时按需光栅化，中英文都能覆盖。
        /// </summary>
        private static TMP_FontAsset TryCreateSystemFont()
        {
            if (cachedSystemFont != null)
            {
                return cachedSystemFont;
            }
            try
            {
                string[] installed = Font.GetOSInstalledFontNames();
                if (installed == null || installed.Length == 0)
                {
                    return null;
                }
                string[] preferred =
                {
                    "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
                    "Source Han Sans SC", "Noto Sans CJK SC", "SimHei", "SimSun"
                };
                string picked = null;
                for (int i = 0; i < preferred.Length && picked == null; i++)
                {
                    for (int j = 0; j < installed.Length; j++)
                    {
                        if (string.Equals(installed[j], preferred[i],
                            System.StringComparison.OrdinalIgnoreCase))
                        {
                            picked = installed[j];
                            break;
                        }
                    }
                }
                if (picked == null)
                {
                    Main.Logger.Log("[字体] 系统里没找到可用中文字体，回退游戏字体");
                    return null;
                }

                // **走 TMP 自己的系统字体入口。**
                //
                // `Font.CreateDynamicFontFromOSFont` 建出来的 Font 没有字体文件
                // 引用，`CreateFontAsset(Font, …)` 在这个版本上直接返回 null
                // （实测日志就是这一条）。TMP 另外有一个「按家族名 + 字形名」
                // 的入口，它内部用 FontEngine 从系统字体库加载，才能建出
                // 可用的动态图集资源。用反射调用是为了不依赖具体 TMP 版本
                // 是否把这个重载编进程序集。
                TMP_FontAsset asset = null;
                try
                {
                    System.Reflection.MethodInfo byFamily = typeof(TMP_FontAsset).GetMethod(
                        "CreateFontAsset",
                        new[] { typeof(string), typeof(string), typeof(int) });
                    if (byFamily != null)
                    {
                        asset = byFamily.Invoke(null, new object[] { picked, "Regular", 32 })
                            as TMP_FontAsset;
                    }
                }
                catch (System.Exception exception)
                {
                    Main.Logger.Log("[字体] 家族名入口失败：" + exception.Message);
                }

                if (asset == null)
                {
                    Font osFont = Font.CreateDynamicFontFromOSFont(picked, 32);
                    if (osFont != null)
                    {
                        asset = TMP_FontAsset.CreateFontAsset(
                            osFont, 32, 4,
                            UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                            1024, 1024,
                            AtlasPopulationMode.Dynamic, true);
                    }
                }
                if (asset == null)
                {
                    Main.Logger.Log("[字体] 两个入口都失败：" + picked);
                    return null;
                }
                asset.name = "AES_System_" + picked;
                cachedSystemFont = asset;
                Main.Logger.Log("[字体] 使用系统字体：" + picked);
                return asset;
            }
            catch (System.Exception exception)
            {
                Main.Logger.Warning("[字体] 系统字体不可用：" + exception.Message);
                return null;
            }
        }

        private TMP_FontAsset ResolveFont()
        {
            // 全程用游戏自己的字体。这里试过系统 UI 字体（走
            // TMP_FontAsset.CreateFontAsset 的微软雅黑），确实能解析出来，但它让
            // 工作台读起来像一扇压在游戏上的外来窗口，而不是游戏
            // 的一部分，所以下面的字体链仍然留在游戏自己的字体里。密集
            // 时间轴文字的可读性改用阴影解决——见 ApplyTextShadow。
            // **优先用系统字体**（用户要求）。失败时再退回游戏字体。
            TMP_FontAsset systemFont = TryCreateSystemFont();
            if (systemFont != null)
            {
                return systemFont;
            }

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
            // 纯平背景：面板不描边、不投影。
            //「参数」那一栏本来就是纯文字窗口，没有边框，观感最干净；
            // 装饰物 / 预览 / 时间轴三块都走这条 AddPanelBorder，于是只有它们
            // 外面挂着一条线和一层影子。整体停掉后四块就统一了。
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
            // 纯平背景：面板不再投影。阴影是 uGUI 画的，不走 ImGui 样式，
            // 所以前几轮清样式时它一直留着 —— 画面上那些"比背景深一块"的方块就是它。
            if (PanelShadow.a <= 0.001f)
            {
                return;
            }
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
            // 纯平背景：预览面板同样不描边。
            // 圆环只画边缘、不挡画面，所以它一直留着；这次按要求一并去掉，
            // 四块面板与预览窗口都不再有边框。
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
            // 纯平背景：面板标题下的那条分隔线也不再画。
            image.color = new Color(0f, 0f, 0f, 0f);
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

            // **输入框不要整圈边框**：无底色 + 底部一条 1px 细线，
            // 和 ImGui 那版的输入框观感一致（整圈框在纯黑上像一块灰板）。
            CreateImage("SearchRule", rect, 0f, searchHeight - 1f,
                searchWidth, 1f, PanelRule).raycastTarget = false;

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
