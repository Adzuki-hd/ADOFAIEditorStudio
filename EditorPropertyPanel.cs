using System;
using System.Collections.Generic;
using ADOFAI;
using ADOFAIEditorStudio.Multiplayer;
using ADOFAIEditorStudio.Ui;
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
        /// <summary>批量模式下「多个值」那块标签占的宽度（在控件右边）。</summary>
        private const float MixedChipWidth = 56f;
        private const float ControlHeight = 30f;
        private const float PreviewHeight = 116f;

        /// <summary>
        /// 预览块在面板里的位置与高度，以及**滚动区上沿**。
        ///
        /// 这几个数是绑在一起的，不能各写各的 —— 原来就是这么错开的：
        /// 块摆在 34 起、高 `PreviewHeight - 10`（=34..140），块内的说明文字
        /// 用 `PreviewHeight - 30` 定位、一直落到 142，而滚动区从
        /// `PreviewHeight + 8`（=124）开始 —— **说明文字被滚动区压住 18px**，
        /// 面板里第一行属性（标签）正好摞在文件名上。
        ///
        /// 现在统一：块内元素按块高排，滚动区从"块底 + 间距"起。
        /// </summary>
        private const float PreviewHostTop = 34f;
        private const float PreviewHostHeight = PreviewHeight - 10f;
        /// <summary>有预览块时，滚动区上沿。</summary>
        private const float ScrollTopWithPreview = PreviewHostTop + PreviewHostHeight + 6f;
        /// <summary>
        /// 没有预览块时，滚动区上沿。
        ///
        /// **仍然要让开顶上那条标题栏**（"参数"两个字那一带，占 0..PreviewHostTop）。
        /// 这里原来图省事写了 8 —— 而 8 落在标题栏里，滚动区的半透明底
        /// 就把"参数"盖掉一半。原来那条 `PreviewHeight + 8` 之所以没暴露
        /// 这个问题，只是因为它顺带躲开了标题栏。
        /// </summary>
        private const float ScrollTopWithoutPreview = PreviewHostTop + 8f;

        // 工作台调色板的镜像，而不是共享：本面板和工作台是两个不同的
        // MonoBehaviour，为了取颜色向对方伸手，只会把两者绑死，没有任何好处。
        // 这里用的是各面板和时间轴共用的同一组中性色，
        // 好让参数栏读起来是同一块界面的一部分，而不是一套旧皮肤。
        private static readonly Color RowStripe = new Color(1f, 1f, 1f, 0.030f);
        private static readonly Color FieldFill = Palette.FieldWell;
        private static readonly Color FieldBorder = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color FieldBorderHover = new Color(1f, 1f, 1f, 0.20f);
        private static Color FieldBorderFocus { get { return UiTheme.Current.BorderFocus; } }
        private static readonly Color LabelColor = Palette.TextSecondary;
        private static Color LabelDisabled { get { return UiTheme.Current.TextDisabled; } }
        private static readonly Color TextPrimary = Palette.TextPrimary;
        private static Color Placeholder { get { return UiTheme.Current.TextMuted; } }
        private static readonly Color Accent = Palette.Accent;
        // caret 用白色，理由和面板边框一样：在近黑底色上，
        // 有颜色的 caret 会和自己要指示的文字抢注意力。
        private static readonly Color CaretColor = new Color(0.960f, 0.960f, 0.960f, 1f);
        private static Color SelectionColor
        {
            get
            {
                Color color = UiTheme.Current.ActionPrimary;
                color.a = 0.30f;
                return color;
            }
        }
        private const float FieldFontSize = 13f;


        // 控件的可用宽度低于这个值后，成对并排的行（Vector2 对、颜色通道）
        // 就排不下了：每一半都会窄到连一个数字都读不清，
        // 而输入框自身的最小宽度让它们互相重叠，而不是收缩。这时该行
        // 改为纵向堆叠控件，高度翻倍。
        private const float MinimumTwoAcrossWidth = 190f;

        /// <summary>
        /// 「标签 + 控件并排」所需的最小控件宽度。低于它就把标签挪到单独一行。
        ///
        /// **没有这一条时的表现**：控件宽度卡在 `Mathf.Max(60f, ...)` 那个下限上
        /// 不再缩，整块**溢出到面板右边被裁掉** —— 看着像"自适应坏了"，其实是
        /// 根本没换行。把时间轴拖矮、面板跟着变窄的时候就会走到这一步。
        /// </summary>
        private const float MinimumInlineControlWidth = 100f;

        /// <summary>堆叠行里「标签」独占的那一行的高度。</summary>
        private const float StackedLabelHeight = 22f;

        // 堆叠行里两个字段的纵向位置。行高 76，
        // 每个字段 30，所以 3 + 30 + 10 + 30 在底部还剩 3。
        private const float StackedFieldTop = 3f;
        private const float StackedFieldGap = 10f;

        // 缓速曲线图。**照原版 EaseGraphRenderer 的 pointCount = 100。**
        //
        // 原来取 24，理由是"缩略图这么小用不到那么多点"。但这条曲线是拿来
        // 当"图片标志"看的，点数一少，陡峭段（Back / Elastic 那类）就能看出
        // 折线。其余设置本来就已经和原版一致：原版只赋 Points，其它全吃
        // UILineRenderer 的默认值，而默认值是 Bevel 接头、None 贝塞尔、
        // LineCaps=false —— 本类原先显式写的那几项和默认值相同，不是差异所在。
        internal const int EaseGraphPoints = 100;
        // 曲线框的高度。
        //
        // **原来取 16，太扁了** —— 和原版并排比，原版的曲线占行高的比重
        // 明显更大（大约 0.54 对 0.37）。曲线的"高矮"直接决定它读不读得出
        // 是哪条缓动：扁下去之后 In/Out/InOut 三者的差别也跟着糊了。
        // 21 是按那个比值反推的，同时给上下各留 2.5px 不至于顶到行边。
        internal const float EaseGraphHeight = 21f;
        // 下拉行左侧那条缩略曲线占的宽度，以及它相对行左边缘的起点。
        // 勾画在 PopupCheckColumn 那一段里，曲线跟在它后面，
        // 标签再跟在曲线后面——三者错开。
        //
        // **宽度收到 22（原来 44）。** 用户对照原版后指出：高度对，但横向
        // 拉得太长，整条曲线读起来是"扁的"，In / Out / InOut 的差别被摊平。
        // 22×21 基本是方框，和原版那个小方图同比例；缩窄之后左边距也收一档，
        // 不然曲线和右侧文字之间会空出一块。
        internal const float OptionGraphX = 12f;
        internal const float OptionGraphWidth = 22f;

        private static Color CurveLine { get { return WithAlpha(UiTheme.Current.TextPrimary, 0.90f); } }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        private sealed class Row
        {
            internal GameObject Root;
            internal RectTransform Rect;
            internal Toggle Enable;
            internal TextMeshProUGUI Label;
            internal RectTransform ControlHost;
            /// <summary>
            /// 批量模式下这个属性各项取值不一致时，挂在**控件右边**的「多个值」。
            ///
            /// 它挂在 `Rect` 上、**不在** `ControlHost` 里 —— 控件会为了给它
            /// 让位而收窄，但两者是并排的兄弟，收窄不能顺手把它也裁掉。
            /// 每行建一次，之后按需开关、按需摆位，不给每次重画都新建一个。
            /// </summary>
            internal TextMeshProUGUI MixedLabel;
            internal readonly List<GameObject> Controls = new List<GameObject>();
            /// <summary>
            /// 这一行是哪个属性。联机时用来在重建之后把焦点还给同一个属性——
            /// 行是按枚举顺序生成的，靠名字找比靠下标稳（属性增删不会错位）。
            /// </summary>
            internal string Key;
        }

        private EditorStudioOverlay owner;
        private TMP_FontAsset font;
        private RectTransform panelRoot;        // 参数栏的外壳。缩放 = 1 时它和 panelRoot 是同一个；
        // 缩放 > 1 时 panelRoot 是内层容器，panelRect 才是外壳。
        private RectTransform panelRect;
        private ScrollRect scroll;
        private RectTransform scrollRectTransform;
        private RectTransform content;
        private RectTransform viewport;
        /// <summary>未选中任何东西时的占位块。见 BuildEmptyState。</summary>
        private RectTransform emptyState;
        private RectTransform heading;
        private RectTransform previewHost;
        private RawImage previewImage;
        private TextMeshProUGUI previewCaption;
        private Image previewPlaceholder;

        /// <summary>
        /// 预览块此刻是不是显示着。
        ///
        /// **它必须参与布局。** 原来只有 `ShowPreview` 把块 `SetActive(false)`，
        /// 而滚动区的位置是写死的 `PreviewHeight + 8`，于是非装饰物事件（MoveTrack、
        /// MoveCamera……）选中的时候，顶部会**空出一条 116px 的空白带** ——
        /// 块没了、位置还占着，看上去就像"这里应该有东西但坏了"。
        /// </summary>
        private bool previewVisible;

        private readonly List<Row> rows = new List<Row>();
        private readonly List<float> rowY = new List<float>();
        private LevelEvent currentEvent;

        /// <summary>
        /// 当前显示的是不是**一页设置**（歌曲设置 / 轨道 / 背景 / 相机……）。
        ///
        /// 非 null 时这个参数栏实例就不再跟时间轴的选中了，写回也要多两步
        /// 副作用 —— 见 <see cref="ApplySettingsSideEffects"/>。
        /// </summary>
        private LevelEvent settingsPage;

        /// <summary>
        /// 这一份实例要不要跟时间轴的选中。
        ///
        /// 设置页那一份**不跟**：它显示的是谱面设置，跟用户在时间轴上点了什么
        /// 是两回事 —— 不挡的话，点一个事件块就会把左边的设置页顶掉。
        /// </summary>
        private bool followTimelineSelection = true;

        /// <summary>
        /// 批量模式选中的那一批；单选时是 null。
        ///
        /// 非 null 就说明参数栏画的是**一批**而不是一个事件：只列公共属性、
        /// 不一致的画成「多个值」、控件全部只读。
        /// </summary>
        private List<LevelEvent> batchEvents;

        /// <summary>批量模式下**每个选中事件都有**的属性键 —— 只有这些能上参数栏。</summary>
        private readonly HashSet<string> batchKeys = new HashSet<string>();

        /// <summary>批量模式下**取值还全等**的键。不在里面的画成「多个值」。</summary>
        private readonly HashSet<string> batchUniformKeys = new HashSet<string>();

        private float nextSelectionPoll;
        private bool built;
        private bool syncing;
        private bool refreshQueued;

        // 焦点快照。重建会销毁所有控件，正在编辑的输入框随之失效，
        // 所以重建之前把「在改哪个属性、光标在第几个字」记下来。
        private string focusSnapshotKey;
        private LevelEvent focusSnapshotTarget;
        private int focusSnapshotCaret;
        private bool focusRestorePending;
        /// <summary>这次重建是不是为了「把焦点还回去」——只有联机改值才需要。</summary>
        private bool captureFocusOnRefresh;

        private readonly List<GameObject> enumPopup = new List<GameObject>();
        private LevelEvent enumTarget;
        // 弹出层自己的状态，不塞进 enumPopup 那个列表里 ——
        // 那个列表是给「一次性销毁全部」用的，多一样东西就多一处要对齐。
        private RectTransform enumPopupRoot;

        // 调色盘的弹出层。和枚举那套是同一个套路：一个根节点 + 一串待销毁的
        // 物体 + 当前绑定的目标；关闭时补记一步撤销（拖动期间是实时写、不记的）。
        private readonly List<GameObject> colorPopup = new List<GameObject>();
        private RectTransform colorPopupRoot;
        private LevelEvent colorTarget;
        private string colorKey;
        private string colorOriginalHex;
        private bool colorOriginalDisabled;
        private Button colorAnchorButton;
        private Image colorSwatch;
        private TMP_InputField colorHexField;
        /// <summary>调色盘最后报上来的颜色。收尾要拿它补写一次。</summary>
        private Color colorLiveValue = Color.white;
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
            EnsureBackdrop();
            built = true;
        }

        /// <summary>
        /// **只接数据与写入，不建任何 UI。**
        ///
        /// 新的 uGUI 参数栏（<see cref="UiInspectorPane"/>）自己画行，但写入必须
        /// 走这一条路：只读拦截、批量合成一条撤销、装饰物活对象刷新、设置页
        /// 副作用，全在 `Commit` 里。另写一条写路径迟早两边不一样。
        ///
        /// `built` 保持 false —— 这扇面板的 Update 轮询与行池就不会跑。
        /// </summary>
        internal void InitializeModel(EditorStudioOverlay panelOwner, TMP_FontAsset panelFont)
        {
            owner = panelOwner;
            font = panelFont;
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
            ApplyScrollLayout(panelRoot.rect.width, panelRoot.rect.height);

            Image background = scrollRect.gameObject.AddComponent<Image>();
            // **直接用模块底色铺满滚动区，不再叠任何半透明层。**
            //
            // 之前的做法是「模块底 #0B0B0B + 一层 35% 黑」，实测出来是 5,5,5，
            // 与顶栏的 11,11,11 对不上（用户看到的就是"参数栏比别处暗一档"）。
            // 现在这一层本身就取模块色，叠多少层都还是同一个值。
            background.color = Color.clear;
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
            // 初始高度按「预览块此刻的显隐」算，和 ApplyScrollLayout 同一套。
            // ShowPreview 之后还会再算一次（选中项进来时会重排）。
            float initialScrollTop = previewVisible ? ScrollTopWithPreview : ScrollTopWithoutPreview;
            content.sizeDelta = new Vector2(0f,
                Mathf.Max(60f, panelRoot.rect.height - initialScrollTop - 16f));

            scroll.viewport = viewport;
            scroll.content = content;

            BuildEmptyState();

            // 首次进来必须主动 Show 一次。轮询那套只在「选中项变了」时刷新，
            // 而初始状态两边都是 null、ReferenceEquals(null, null) 为真，
            // 于是 Show 永远不被调用——空状态也就永远不显示，面板是纯空白。
            Show(null);
        }

        /// <summary>
        /// 未选中任何对象时的占位。
        ///
        /// 这里原本是纯空白——那读起来不像「等着你选」，像「这一页加载失败了」。
        /// 给一句话说明会显示什么、怎么让它有内容，空状态才算被考虑过，
        /// 而不是被忘掉。
        ///
        /// 用锚点 (0.5, 0.5) 居中而不是按坐标摆：Show(null) 时 content 会被
        /// 拉到与 viewport 等高，锚点居中自动就是面板居中；按坐标算的话
        /// 每次布局都得跟着更新一遍。
        /// </summary>
        private const float EmptyCardWidth = 248f;
        private const float EmptyCardHeight = 180f;
        private const float EmptyMarkSize = 56f;

        private void BuildEmptyState()
        {
            RectTransform box = NewChildRect("EmptyState", content);
            emptyState = box;
            box.anchorMin = new Vector2(0.5f, 0.5f);
            box.anchorMax = new Vector2(0.5f, 0.5f);
            box.pivot = new Vector2(0.5f, 0.5f);
            box.anchoredPosition = Vector2.zero;
            box.sizeDelta = new Vector2(EmptyCardWidth, EmptyCardHeight);

            // 一整块实心卡片。
            //
            // 原本这里只有两行字浮在面板上，面板空着的时候就是真的一整块空的——
            // 工作台上将近六分之一的面积是它。铺一块实心底之后，空状态才有
            // "这是一块有边界的区域，里面等着放东西"的意思。
            Image card = CreateChildImage("Card", box, 0f, 0f, EmptyCardWidth, EmptyCardHeight);
            card.sprite = GetRoundedSprite(24, 8f, false);
            card.type = Image.Type.Sliced;
            card.color = UiTheme.Current.SurfaceRaised;

            BuildEmptyStateMark(box, (EmptyCardWidth - EmptyMarkSize) * 0.5f, 24f, EmptyMarkSize);

            TextMeshProUGUI title = CreateChildText(
                "Title", box, 0f, 96f, EmptyCardWidth, 24f, "未选中任何对象", 13f);
            title.alignment = TextAlignmentOptions.Center;
            title.color = UiTheme.Current.TextPrimary;

            TextMeshProUGUI hint = CreateChildText(
                "Hint", box, 16f, 122f, EmptyCardWidth - 32f, 48f,
                "在时间轴上点一个事件块，\n或在左栏的装饰物列表里选一个", 11f);
            hint.alignment = TextAlignmentOptions.Center;
            // 次要色，不是弱化色：弱化色压在卡片上只有 3.0:1，够不着正文的 4.5:1。
            hint.color = UiTheme.Current.TextSecondary;

            box.gameObject.SetActive(false);
        }

        /// <summary>
        /// 空状态上方那组取景框角标——四个 L 形角，围出一个"东西放这里"的框。
        ///
        /// 用角标而不是画一个完整的方框：完整的框读成"一个输入区"，
        /// 会被当成能点的地方；四个角读成取景器，既是这台工具该有的图形语言，
        /// 也不抢下面两行字的视线。
        /// </summary>
        private void BuildEmptyStateMark(RectTransform parent, float x, float y, float size)
        {
            const float arm = 14f;
            const float thickness = 2f;
            // 弱化色而不是禁用色：角标是衬托，不该压过下面两行字，
            // 但要够到"图形≥3:1"这条线——禁用色压在卡片上只有 1.9:1，等于没画。
            Color color = UiTheme.Current.TextMuted;

            for (int i = 0; i < 4; i++)
            {
                bool right = i >= 2;
                bool down = (i & 1) == 1;
                // 横臂贴着上沿或下沿，竖臂贴着左沿或右沿，两条在角上拼成 L。
                CreateChildImage("CornerH" + i, parent,
                    x + (right ? size - arm : 0f),
                    y + (down ? size - thickness : 0f),
                    arm, thickness).color = color;
                CreateChildImage("CornerV" + i, parent,
                    x + (right ? size - thickness : 0f),
                    y + (down ? size - arm : 0f),
                    thickness, arm).color = color;
            }
        }

        private void BuildPreviewSlot()
        {
            RectTransform slot = EnsureChildRect("DecorationPreview");
            SetTopLeft(slot, Padding, PreviewHostTop,
                panelRoot.rect.width - Padding * 2f, PreviewHostHeight);
            previewHost = slot;

            Image frame = slot.gameObject.AddComponent<Image>();
            frame.color = UiTheme.Current.SurfacePage;
            frame.raycastTarget = false;

            previewPlaceholder = CreateChildImage("Placeholder", slot, 1f, 1f, 1f, 1f);
            previewPlaceholder.color = UiTheme.Current.SurfaceRaised;

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
            // 说明文字占底部 24px（22 高 + 2 间距），图片框吃掉剩下的部分。
            // 两者都按**块高**排，不再按 PreviewHeight —— 原来那样会让说明文字
            // 落到块外、进而被滚动区压住。
            const float captionHeight = 22f;
            const float captionGap = 2f;
            float boxHeight = Mathf.Max(1f, PreviewHostHeight - captionHeight - captionGap - 2f);
            const float boxY = 1f;

            SetTopLeft(previewPlaceholder.rectTransform, 1f, boxY, boxWidth, boxHeight);
            SetTopLeft(previewCaption.rectTransform, 6f,
                PreviewHostHeight - captionHeight - captionGap,
                Mathf.Max(1f, slotWidth - 12f), captionHeight);

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
            text.color = UiTheme.Current.TextSecondary;
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

            // **面板尺寸变了就重排滚动区。**
            //
            // 拖时间轴高度会改参数栏的 rect，而滚动区的位置/高度只在建面板时
            // 算过一次 —— 不跟着改，行就会跑到可视区外面（实测：反复拖高度
            // 之后参数栏只剩半截内容，像"乱了"）。
            if (panelRoot != null)
            {
                // **量外壳，不量内层。** 内层的 sizeDelta 是我们自己按外壳算的，
                // 建面板那一刻外壳还没排版完（宽高是 0），内层就被定成了 0 ——
                // 表现是「首次进来排版很小，拖一下高度又突然变大」。
                Vector2 size = panelRect != null ? panelRect.rect.size : panelRoot.rect.size;
                if (size.x > 1f && size.y > 1f)
                {
                    Vector2 wanted = new Vector2(size.x / PanelScale, size.y / PanelScale);
                    if ((panelRoot.sizeDelta - wanted).sqrMagnitude > 0.5f)
                    {
                        panelRoot.sizeDelta = wanted;
                    }
                }
                bool widthChanged = Mathf.Abs(size.x - lastPanelWidth) > 4f;
                bool heightChanged = Mathf.Abs(size.y - lastPanelHeight) > 0.5f;
                if (widthChanged || heightChanged)
                {
                    lastPanelWidth = size.x;
                    lastPanelHeight = size.y;
                    ApplyScrollLayout(size.x, size.y);
                }
                // **宽度变了要重排整行**，不只是滚动区。
                //
                // 每一行的「标签列 / 控件列」宽度是按面板宽度算死的：宽度变了
                // 不重排，标签就会压到勾选框上（实测现象）。高度变化只影响
                // 可视范围，滚动区重算就够。
                if (widthChanged && !refreshQueued && currentEvent != null)
                {
                    refreshQueued = true;
                }
            }

            if (refreshQueued)
            {
                refreshQueued = false;

                // 重建之前先记下焦点。只有联机改值提示的那次重建才需要
                // （captureFocusOnRefresh）——普通的选择变化重建时，焦点
                // 本来就该跟着新事件走，硬拉回去反而会跳。
                if (captureFocusOnRefresh)
                {
                    captureFocusOnRefresh = false;
                    CaptureEditingFocus();
                }

                // 重画要**接着原来那条路**走。这里过去一律调 Show ——
                // 批量模式下那会让面板在一次编辑之后突然掉回「只画一个事件」，
                // 而作者看到的会是：改一下值，半张面板没了。
                if (batchEvents != null && batchEvents.Count > 1)
                {
                    ShowBatch(batchEvents);
                }
                else
                {
                    Show(currentEvent);
                }

                // 重画完了，把焦点和光标位置还给同一个属性。
                if (focusRestorePending && ReferenceEquals(currentEvent, focusSnapshotTarget))
                {
                    focusRestorePending = false;
                    ReapplyEditingFocus();
                }

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

            // 设置页那一份实例不跟时间轴的选中，到这里就够 —— 上面该跑的都
            // 跑完了（枚举弹窗收尾、重建标记）。不挡的话，点一个事件块就会把
            // 左边的设置页顶掉。
            if (!followTimelineSelection)
            {
                return;
            }

            if (now < nextSelectionPoll)
            {
                return;
            }
            nextSelectionPoll = now + 0.12f;

            // **选集一律以时间轴那份为准**（多选优先，否则主选中那一个）。
            //
            // 不能再用 `TimelineSelection`：它只认主选中。Ctrl 只选了一个、
            // 或反选到只剩一个的时候，主选中还是 null —— 面板会整个空着，
            // 看上去像"选中了却没反应"。
            //
            // 见 EditorTimelineView.PanelSelection：它和删除/复制用的
            // CollectSelection 是同一条判断，**只差不排除装饰物**（参数栏
            // 要给装饰物显示图片预览）。
            List<LevelEvent> selection = owner == null || owner.Timeline == null
                ? null
                : owner.Timeline.PanelSelectionWithDecorations();

            // 两个以上才算批量。
            if (selection != null && selection.Count > 1 && CountLive(selection) > 1)
            {
                if (!SameBatch(selection) || !BatchStillLive())
                {
                    ShowBatch(selection);
                }
                return;
            }

            // 一个（或零个）走单选那条路。死引用当成没选中 —— 原生撤销会
            // 整体替换 levelData，读一个失效事件得不到有用信息，
            // 写进去则会弄坏无关数据。
            LevelEvent selected = selection != null && selection.Count == 1
                && IsLive(selection[0])
                ? selection[0]
                : null;

            // 刚刚还在批量、现在退回来了：把占位痕迹撤掉，交回 Show 重画。
            // Show 自己会把每一行的痕迹清干净（见 BuildRow），这里只要清掉
            // 「当前是批量」这个状态。
            if (batchEvents != null)
            {
                ClearBatchState();
                Show(selected);
                return;
            }

            if (!ReferenceEquals(selected, currentEvent))
            {
                Show(selected);
                return;
            }

            // 原生撤销会整体替换 customLevel.levelData，
            // 面板持有的每个 LevelEvent 随之变成悬空引用。出现这种情况时，
            // 时间轴会清掉自己的选中项；在它清掉之前，在这里读一个失效事件
            // 得不到有用信息，写进去则会弄坏无关数据。
            if (currentEvent != null && !IsLive(currentEvent))
            {
                Show(null);
            }

        }

        /// <summary>
        /// 「这个东西还在不在」。
        ///
        /// 设置页要**单独放行**：原生那套存活判断认的是 `editor.events` /
        /// `editor.decorations`，而设置页两边都不在（它挂在 `levelData` 上）——
        /// 照那个判，设置页会被当成死引用，读不出、也写不进。
        /// </summary>
        private bool IsLive(LevelEvent levelEvent)
        {
            if (levelEvent != null && ReferenceEquals(levelEvent, settingsPage))
            {
                return true;
            }
            return IsStillLiveCore(levelEvent);
        }

        private static bool IsStillLiveCore(LevelEvent levelEvent)
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
            SetTopLeft(previewHost, Padding, PreviewHostTop, width - Padding * 2f, PreviewHostHeight);
            LayoutPreviewSlot(width);
            ApplyScrollLayout(width, height);

            if (currentEvent != null)
            {
                // 重画要**接着原来那条路**走：批量模式下调 Show 会让面板
                // 掉回「只画一个事件」。和 refresh 那条路同样的处理。
                if (batchEvents != null && batchEvents.Count > 1)
                {
                    ShowBatch(batchEvents);
                }
                else
                {
                    Show(currentEvent);
                }
            }
        }

        /// <summary>
        /// 滚动区的位置。**预览块不显示时要把那 116px 还回去**，
        /// 不然面板顶部会留一条空白带。见 previewVisible。
        /// </summary>
        /// <summary>
        /// 面板自身的整块底色。
        ///
        /// **不能依赖外层 Inspector 面板的 Image**：那个面板的矩形和这里的
        /// 缩放/内缩不是同一套坐标，一旦时间轴高度变了，就会出现「顶部一条
        /// 没被覆盖的黑带」。这里铺一张跟着 panelRoot 走的整块底，
        /// 颜色取模块色，和顶栏一致。
        /// </summary>
        private void EnsureBackdrop()
        {
            if (panelRoot == null || backdrop != null)
            {
                return;
            }
            GameObject layer = new GameObject("PaneFill", typeof(RectTransform));
            layer.transform.SetParent(panelRoot, false);
            RectTransform rect = layer.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Image image = layer.AddComponent<Image>();
            // 总背景已经由工作台铺了，这里不再叠一层。
            image.color = Color.clear;
            image.raycastTarget = false;
            backdrop = image;
            // 排在最底层，免得盖住行。
            layer.transform.SetAsFirstSibling();
        }

        private Image backdrop;

        private float lastPanelWidth = -1f;
        private float lastPanelHeight = -1f;

        private void ApplyScrollLayout(float width, float height)
        {
            if (scrollRectTransform == null)
            {
                return;
            }

            // 设置页那一份顶部不留预留：`PreviewHostTop` 那一截存在的理由是
            // 「顶部可能有装饰物图片」，设置页没有图片，留着就是一条无来由的空带。
            float scrollTop = settingsPage != null
                ? Padding
                : (previewVisible ? ScrollTopWithPreview : ScrollTopWithoutPreview);
            float scrollHeight = Mathf.Max(60f, height - scrollTop - 16f);
            SetTopLeft(scrollRectTransform, 0f, scrollTop, width, scrollHeight);
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

        /// <summary>
        /// 联机那边改了本机正在看的这个事件，把参数栏重画一遍。
        ///
        /// **不能只改控件里的文字。** 行的内容是按 `PropertyInfo` 元数据
        /// 生成的，改一个键可能要连带改别的东西（启用开关的勾选、缓动曲线
        /// 的形状），只改文字会留下一个半新半旧的面板。
        ///
        /// 重画会销毁正在编辑的输入框，所以先把焦点和光标位置记下来，
        /// 重建之后还回去——见 <see cref="CaptureEditingFocus"/>。
        /// </summary>
        internal void RefreshFromRemoteEdit()
        {
            captureFocusOnRefresh = true;
            QueueRefresh();
        }

        /// <summary>
        /// 记下当前正在编辑的控件和光标位置。
        ///
        /// 两种时候用得上：联机改了本机正在看的这个事件（重画参数栏），
        /// 以及对方增删了事件导致整份谱面重载（所有对象都换新的）。
        /// 不记的话，对面动一下你正在打的字就没了，还不知道去哪了。
        /// </summary>
        internal void CaptureEditingFocus()
        {
            EventSystem system = EventSystem.current;
            if (system == null || rows.Count == 0)
            {
                return;
            }

            GameObject selected = system.currentSelectedGameObject;
            if (selected == null)
            {
                // 没有控件的焦点也可能有事件级的焦点（用户选中了某个事件
                // 在看参数），那种情况下面恢复到事件即可。
                if (currentEvent != null)
                {
                    focusRestorePending = true;
                }
                return;
            }

            // 从光标控件往上找它属于哪一行。
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (row == null || row.ControlHost == null || string.IsNullOrEmpty(row.Key))
                {
                    continue;
                }

                if (selected.transform.IsChildOf(row.ControlHost))
                {
                    focusSnapshotKey = row.Key;
                    focusSnapshotTarget = currentEvent;
                    focusSnapshotTarget = currentEvent;

                    TMP_InputField field = selected.GetComponent<TMP_InputField>();
                    if (field != null)
                    {
                        focusSnapshotCaret = field.caretPosition;
                    }

                    focusRestorePending = true;
                    return;
                }
            }

            // 焦点不在任何一行上（可能停在启用开关之类的地方），
            // 那就只恢复到事件级。
            if (currentEvent != null)
            {
                focusSnapshotTarget = currentEvent;
                focusRestorePending = true;
            }
        }

        /// <summary>
        /// 把重建之前记下的焦点还回去。
        ///
        /// 跟的是**属性键**而不是控件下标：行是按枚举顺序生成的，靠下标
        /// 找在属性增删之后会错位，靠键名找永远对得上。
        /// </summary>
        private void ReapplyEditingFocus()
        {
            LevelEvent target = currentEvent;
            if (focusSnapshotTarget == null || target == null
                || !ReferenceEquals(target, focusSnapshotTarget))
            {
                return;
            }

            ApplyFocusToRows();
        }

        /// <summary>
        /// 按记下的属性键和光标位置，把焦点装回新造出来的那批行上。
        /// 调用方保证 `currentEvent` 已经是重建后的正确事件。
        /// </summary>
        private void ApplyFocusToRows()
        {
            string key = focusSnapshotKey;
            int caret = focusSnapshotCaret;
            ClearFocusSnapshot();

            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (row == null || row.Root == null || !row.Root.activeSelf
                    || !string.Equals(row.Key, key, StringComparison.Ordinal))
                {
                    continue;
                }

                TMP_InputField field = row.ControlHost == null
                    ? null
                    : row.ControlHost.GetComponentInChildren<TMP_InputField>(true);
                if (field != null && field.interactable)
                {
                    field.ActivateInputField();
                    // 光标位置放在激活之后设，否则 TMP 会按自己的规则
                    // 把光标归到开头或结尾。
                    field.caretPosition = Mathf.Clamp(
                        caret, 0, field.text == null ? 0 : field.text.Length);
                }
                else
                {
                    // 这一行的控件不是输入框（开关、枚举、颜色），
                    // 那就把焦点还给行的控件，别让键盘快捷键落空。
                    SelectRowControl(row);
                }
                break;
            }
        }

        private void SelectRowControl(Row row)
        {
            if (row == null || row.ControlHost == null)
            {
                return;
            }

            Selectable selectable = row.ControlHost.GetComponentInChildren<Selectable>(true);
            if (selectable != null && selectable.interactable)
            {
                EventSystem system = EventSystem.current;
                if (system != null)
                {
                    system.SetSelectedGameObject(selectable.gameObject);
                }
            }
        }

        private void ClearFocusSnapshot()
        {
            focusSnapshotKey = null;
            focusSnapshotTarget = null;
            focusSnapshotCaret = 0;
        }        internal void Show(LevelEvent levelEvent)
        {
            currentEvent = levelEvent;
            CloseEnumPopup();
            CloseColorPopup();
            ClearRows();

            if (levelEvent == null || levelEvent.info == null || levelEvent.info.propertiesInfo == null)
            {
                ShowPreview(null);
                if (emptyState != null)
                {
                    emptyState.gameObject.SetActive(true);
                }
                content.sizeDelta = new Vector2(0f, viewport == null ? 0f : viewport.rect.height);
                return;
            }

            if (emptyState != null)
            {
                emptyState.gameObject.SetActive(false);
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
                    row.Key = pair.Key;
                    BuildRow(row, levelEvent, info, y, rowWidth, rowHeight, false);
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

        /// <summary>
        /// 只记下"当前这一页是设置页"，**不建行、不显示**。
        ///
        /// 给 ImGui 版用：行是它自己画的，但设置页特有的那几步副作用
        /// （路径/格位重算、背景重铺）是由这里认人的 —— 靠 <see cref="settingsPage"/>
        /// 判断。不打这个标记，ImGui 那边改设置就会"值变了、画面不跟"。
        /// </summary>
        internal void MarkSettingsPage(LevelEvent page)
        {
            settingsPage = page;
        }

        /// <summary>
        /// 进入批量模式，并把两个键集算好。**给 ImGui 那版参数栏的口子。**
        ///
        /// uGUI 这一扇整块被 `SetActive(false)`（见 `EditorImGuiBridge`），
        /// 它自己的轮询不再跑，`ShowBatch` 就没人调了 —— 于是 ImGui 那边
        /// 多选时在这里把状态喂进来。
        ///
        /// **行仍由 ImGui 自己画，但写入路径和键集规则沿用这同一套**：
        /// `Commit` 会看到 `batchEvents` 自动转 `CommitBatch`，
        /// 一次改值落到整批、只记一步撤销。这正是这个功能最容易做坏的地方，
        /// 所以宁可两边共用，也不在 ImGui 那边另写一条写路径。
        /// </summary>
        /// <returns>活着的够两个才算批量；不够就顺手退出批量并返回 false。</returns>
        internal bool EnterBatch(IList<LevelEvent> events)
        {
            List<LevelEvent> live = new List<LevelEvent>();
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    LevelEvent candidate = events[i];
                    if (candidate != null && candidate.info != null
                        && candidate.info.propertiesInfo != null && IsLive(candidate))
                    {
                        live.Add(candidate);
                    }
                }
            }
            if (live.Count < 2)
            {
                ExitBatch();
                return false;
            }

            batchEvents = live;
            currentEvent = live[0];
            ComputeBatchKeys();
            return true;
        }

        /// <summary>
        /// 退出批量模式。
        ///
        /// **必须有这一条**：退到只剩一个（或零个）之后如果不清，下一次单选改值
        /// 会看到过期的 `batchEvents`，把值写给一批早就不是选中的事件。
        /// </summary>
        internal void ExitBatch()
        {
            ClearBatchState();
        }

        /// <summary>现在是不是批量模式（选了两个及以上）。</summary>
        internal bool InBatch
        {
            get { return batchEvents != null && batchEvents.Count > 1; }
        }

        /// <summary>这一批的事件。顺序不承诺，只用来取"第一个"当画的抓手。</summary>
        internal List<LevelEvent> BatchEvents
        {
            get { return batchEvents; }
        }

        /// <summary>这个键是不是**每个选中事件都有**（没有的不该出现在批量行里）。</summary>
        internal bool BatchHasKey(string key)
        {
            return batchKeys.Contains(key);
        }

        /// <summary>这个键在这一批里取值是不是**全等**。不等就要挂「多个值」。</summary>
        internal bool BatchIsUniform(string key)
        {
            return batchUniformKeys.Contains(key);
        }

        /// <summary>
        /// 批量模式：把选中的**一批**事件画进参数栏。
        ///
        /// 三条规则：
        ///   · 只列**每个选中事件都有**的属性 —— 别人没有的键不出现，
        ///     否则会写到一个它根本不认识的地方去。
        ///   · 取值全等的照常显示；不等的顶上盖一块「多个值」，真的控件藏掉。
        ///   · 这一版**只读**，所有控件置灰不给改。
        ///
        /// 放开编辑时只要把 <see cref="FinishBatchRow"/> 里那道闸去掉，
        /// 再把写入改成遍历 batchEvents —— 版式和信息不用重做。
        /// </summary>
        internal void ShowBatch(IList<LevelEvent> events)
        {
            List<LevelEvent> live = new List<LevelEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent candidate = events[i];
                if (candidate != null && candidate.info != null
                    && candidate.info.propertiesInfo != null && IsLive(candidate))
                {
                    live.Add(candidate);
                }
            }

            // 掉到只剩一个（或零个）了：这一批已经不算数。
            //
            // 这里**不画**，只把批量状态清掉，交回轮询 —— 它下一轮
            // （0.12 秒后）看到活着的不足两个，就会走单选那条路重画。
            // 在这里自己调 Show 会在「单选还是那个主选中项」和
            // 「批量没了」之间来回切，反而更难收敛。
            if (live.Count < 2)
            {
                ClearBatchState();
                return;
            }

            batchEvents = live;
            currentEvent = live[0];
            ComputeBatchKeys();

            CloseEnumPopup();
            CloseColorPopup();
            ClearRows();
            if (emptyState != null)
            {
                emptyState.gameObject.SetActive(false);
            }
            // 批量没有「这一个事件」的图可给，预览块整个收掉。
            ShowPreview(null);

            float rowWidth = viewport == null || viewport.rect.width < 1f
                ? Mathf.Max(120f, panelRoot.rect.width - Padding * 2f)
                : viewport.rect.width;

            float y = 0f;
            syncing = true;
            try
            {
                int rowIndex = 0;
                foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in live[0].info.propertiesInfo)
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
                    // 只有每个选中事件都有的属性才上得来。
                    if (!batchKeys.Contains(pair.Key))
                    {
                        continue;
                    }

                    Row row = ObtainRow(rowIndex, rowWidth);
                    float rowHeight = RowHeightFor(info, rowWidth);
                    row.Key = pair.Key;
                    bool mixed = !batchUniformKeys.Contains(pair.Key);
                    // 值一律取第一个选中事件的：控件总得有个东西可画，
                    // 而且它同时就是作者要改的那个抓手。不一致这件事
                    // 由 FinishBatchRow 在右边那块标签表达。
                    BuildRow(row, live[0], info, y, rowWidth, rowHeight, mixed);
                    FinishBatchRow(row, rowWidth, rowHeight, mixed);
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

        /// <summary>把「当前是批量」这个状态清掉，连同两个键集。</summary>
        private void ClearBatchState()
        {
            batchEvents = null;
            batchKeys.Clear();
            batchUniformKeys.Clear();
        }

        /// <summary>这一批里还活着的有几个。「够不够两个」是算不算批量的门槛。</summary>
        private int CountLive(IList<LevelEvent> events)
        {
            int live = 0;
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent candidate = events[i];
                if (candidate != null && candidate.info != null
                    && candidate.info.propertiesInfo != null && IsLive(candidate))
                {
                    live++;
                }
            }
            return live;
        }

        /// <summary>这一批里还有死引用吗（原生撤销会整体换掉 levelData）。</summary>
        private bool BatchStillLive()
        {
            if (batchEvents == null)
            {
                return true;
            }
            for (int i = 0; i < batchEvents.Count; i++)
            {
                if (!IsLive(batchEvents[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 轮询到的这一批，和正在画的是不是同一批。
        ///
        /// 按**集合**比而不是按顺序比：时间轴那边的多选是个 HashSet，
        /// 枚举顺序不是承诺。按顺序比会在什么都没变的时候每轮都判成
        /// 「换了」，于是每 0.12 秒无谓地重建一次面板。
        /// </summary>
        private bool SameBatch(List<LevelEvent> batch)
        {
            if (batchEvents == null || batchEvents.Count != batch.Count)
            {
                return false;
            }
            HashSet<LevelEvent> incoming = new HashSet<LevelEvent>(batch);
            for (int i = 0; i < batchEvents.Count; i++)
            {
                if (!incoming.Contains(batchEvents[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 算出这一批里**每个事件都有**的属性键，以及其中**取值还全等**的那些。
        ///
        /// 走键的基准是第一个选中事件的属性表，所以顺序稳定、不受别人
        /// 增删属性的影响 —— 多选集合每次枚举顺序不同，块就不会跟着跳。
        /// </summary>
        private void ComputeBatchKeys()
        {
            batchKeys.Clear();
            batchUniformKeys.Clear();
            if (batchEvents == null || batchEvents.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in batchEvents[0].info.propertiesInfo)
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

                bool presentEverywhere = true;
                bool uniform = true;
                string firstValue = null;

                for (int i = 0; i < batchEvents.Count; i++)
                {
                    ADOFAI.PropertyInfo other = FindInfo(batchEvents[i], pair.Key);
                    if (other == null || other.invisible || other.controlType == ControlType.Hidden)
                    {
                        presentEverywhere = false;
                        break;
                    }

                    string value = BatchValueKey(batchEvents[i], other);
                    if (i == 0)
                    {
                        firstValue = value;
                    }
                    else if (value != firstValue)
                    {
                        uniform = false;
                    }
                }

                if (!presentEverywhere)
                {
                    continue;
                }

                batchKeys.Add(pair.Key);
                if (uniform)
                {
                    batchUniformKeys.Add(pair.Key);
                }
            }
        }

        /// <summary>在这个事件的属性表里找一项，没有则 null。</summary>
        private static ADOFAI.PropertyInfo FindInfo(LevelEvent levelEvent, string key)
        {
            ADOFAI.PropertyInfo info;
            if (levelEvent == null || levelEvent.info == null
                || levelEvent.info.propertiesInfo == null
                || !levelEvent.info.propertiesInfo.TryGetValue(key, out info))
            {
                return null;
            }
            return info;
        }

        /// <summary>
        /// 一个属性在某个事件上的取值化成字符串，用来判断「这一批一致吗」。
        ///
        /// 必须按类型走强类型读取。统一读成 object 再比会踩精度的坑：
        /// 作者根本没动过的两个值，装箱之后也可能判成不同。浮点按三位
        /// 小数比 —— 那正是参数栏自己显示用的精度。
        /// </summary>
        private static string BatchValueKey(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
        {
            switch (info.type)
            {
                case PropertyType.Bool:
                    return SafeGet(levelEvent, info, false) ? "1" : "0";
                case PropertyType.Int:
                    return FormatInt(SafeGet(levelEvent, info, 0));
                case PropertyType.Float:
                    return FormatFloat(SafeGet(levelEvent, info, 0f));
                case PropertyType.Vector2:
                {
                    Vector2 value = SafeGet(levelEvent, info, Vector2.zero);
                    return value.x.ToString("0.###") + "," + value.y.ToString("0.###");
                }
                case PropertyType.Color:
                {
                    Color value = SafeGet(levelEvent, info, Color.white);
                    return ((int)(value.r * 1000f)) + "," + ((int)(value.g * 1000f)) + ","
                        + ((int)(value.b * 1000f)) + "," + ((int)(value.a * 1000f));
                }
                default:
                    return SafeGet(levelEvent, info, string.Empty) ?? string.Empty;
            }
        }

        /// <summary>
        /// 批量模式下一行的收尾：值不一致的在控件右边挂一块「多个值」。
        ///
        /// 只做这一件事 —— 控件保持原样、可交互。批量改参最有用的用法
        /// 恰恰是「把选中的这些全设成某个值」，而那种情况下各项几乎必然
        /// 不相同；把这些行锁掉等于把这个功能废掉。
        ///
        /// 标签的位置按**传进来的 rowWidth 算**，不读回 rect：行是从对象池
        /// 复用来的，那一刻它的 rect 还可能是上一次的宽度。
        /// </summary>
        private void FinishBatchRow(Row row, float rowWidth, float rowHeight, bool mixed)
        {
            if (row.MixedLabel == null)
            {
                return;
            }

            row.MixedLabel.gameObject.SetActive(mixed);
            if (!mixed)
            {
                return;
            }

            // **把控件里显示的数值抹掉。**
            //
            // 那一格里现在是**第一个选中事件**的值，而它长得和「整批共同
            // 的值」一模一样。作者点进去又原样点出来，就等于把那个值写给
            // 了整批 —— 一次什么都没改的操作把各事件本来不同的值统统抹平，
            // 还占掉一步撤销。这正是「点一下什么都没改也算修改、标签也消失」
            // 那个反馈的来路。
            //
            // 空着之后：没敲东西就离开什么都不会发生（解析失败 → 不提交），
            // 敲了才落到整批。开关和下拉框不用管 —— 它们只有真的点选才会写值，
            // 不存在「路过」这种情况。
            for (int i = 0; i < row.Controls.Count; i++)
            {
                if (row.Controls[i] == null)
                {
                    continue;
                }
                TMP_InputField field = row.Controls[i].GetComponent<TMP_InputField>();
                if (field != null)
                {
                    field.SetTextWithoutNotify(string.Empty);
                }
            }

            // 「多个值」贴着控件右边。堆叠时控件另起了一行，标签也挪走了，
            // 所以这一块要跟着下到控件那一行去，不然它会悬在两行之间的缝上。
            float chipX = Mathf.Max(CheckWidth + LabelWidth,
                rowWidth - Padding - MixedChipWidth);
            float chipY = RowStacks(rowWidth) ? StackedLabelHeight : 0f;
            SetTopLeft(row.MixedLabel.rectTransform, chipX, chipY, MixedChipWidth,
                Mathf.Max(ControlHeight, rowHeight - chipY));
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

            // **行不铺任何底色。**
            //
            // 原来每隔一行加一条低透明度白色条纹（斑马纹），在纯平深色面板上
            // 读起来是"相间的色块"，和整体三色规则冲突。行的高亮只保留
            // 悬停反馈，由 Row 自身处理。
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0f);
            background.raycastTarget = false;

            row.Enable = CreateEnableToggle(rect);
            row.Label = CreateChildText("Label", rect, CheckWidth + 4f, 0f, LabelWidth - 6f, RowHeight, string.Empty, 12f);

            row.ControlHost = NewChildRect("Control", rect);
            SetTopLeft(row.ControlHost, CheckWidth + LabelWidth, 0f,
                Mathf.Max(40f, rowWidth - CheckWidth - LabelWidth - Padding), RowHeight);

            // 「多个值」标签，摆在控件右边，平时关着。
            // 真正的位置由 FinishBatchRow 按当次行宽算（行是对象池复用的，
            // 那一刻只能信传进来的宽度，不能读回 rect）。
            row.MixedLabel = CreateChildText("Mixed", rect,
                0f, 0f, MixedChipWidth, RowHeight, "多个值", 11.5f);
            row.MixedLabel.gameObject.SetActive(false);
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
        /// 行窄到标签必须独占一行、控件另起一行。
        ///
        /// 和 <see cref="StacksVertically"/> 是两件事：那个管"控件自己要占几行"，
        /// 这个管"标签还挤不挤得下"。两个可以叠加。
        /// </summary>
        private static bool RowStacks(float rowWidth)
        {
            return rowWidth - CheckWidth - LabelWidth - Padding < MinimumInlineControlWidth;
        }

        /// <summary>
        /// 控件区实际能拿到多宽。**行堆不堆是它的输入**，不是反过来求 ——
        /// 判定和排版都走这一个函数，免得两边算出来的宽度不一样
        /// （"判定按 104、排版按 130"那种打架）。
        /// </summary>
        private static float ControlWidthFor(float rowWidth, bool rowStacked)
        {
            float width = rowStacked
                ? rowWidth - CheckWidth - Padding
                : rowWidth - CheckWidth - LabelWidth - Padding;
            return Mathf.Max(40f, width);
        }

        /// <summary>
        /// 一行的高度。两种情况会变高：控件自己并排不下（多值行纵向堆叠），
        /// 以及标签被挪到了单独一行。**两种可以叠加**，所以分开算再相加。
        /// </summary>
        private static float RowHeightFor(ADOFAI.PropertyInfo info, float rowWidth)
        {
            bool rowStacked = RowStacks(rowWidth);
            float controlWidth = ControlWidthFor(rowWidth, rowStacked);

            float height = StacksVertically(info, controlWidth)
                ? RowHeight * 2f - 4f
                : RowHeight;
            return rowStacked ? StackedLabelHeight + height : height;
        }

        /// <summary>
        /// 该行的控件是否必须堆叠，而不是并排。
        ///
        /// 只有 Vector2（以及颜色，它要摆四个通道加一个色块）有不止
        /// 一个控件。**参数是控件区宽度，不是面板宽度**：标签和启用复选框
        /// 先分走自己那份，所以看起来够宽的面板也可能剩下太少。
        /// </summary>
        private static bool StacksVertically(ADOFAI.PropertyInfo info, float controlWidth)
        {
            if (info == null)
            {
                return false;
            }
            // Vector2Range 永远是两行（X 一行、Y 一行），行高固定翻倍。
            if (info.type == PropertyType.Vector2Range)
            {
                return true;
            }
            if (info.type != PropertyType.Vector2)
            {
                return false;
            }
            return controlWidth < MinimumTwoAcrossWidth;
        }

        private void BuildRow(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info, float y,
            float rowWidth, float rowHeight, bool mixed)
        {
            SetTopLeft(row.Rect, 0f, y, rowWidth, rowHeight);

            // 行是从对象池里复用的，先把上一轮「多个值」的痕迹清干净。
            // 批量那一侧会在 BuildRow 之后重新挂上（见 FinishBatchRow）——
            // 痕迹只在**一处**设置、在一处清除，不会出现某次重画漏掉一半。
            row.ControlHost.gameObject.SetActive(true);
            if (row.MixedLabel != null)
            {
                row.MixedLabel.gameObject.SetActive(false);
            }

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
            // 窄到并排不下就把标签挪到单独一行（见 RowStacks）。
            // 行高在 RowHeightFor 里已经按这个多留出来了。
            bool rowStacked = RowStacks(rowWidth);
            float controlWidth = ControlWidthFor(rowWidth, rowStacked);
            // 批量模式下这一行的值不一致：右边让出一块给「多个值」，
            // 控件本身照常显示**第一个选中事件**的值 —— 它是给作者改的
            // 抓手，改一下那一下就会落到整批上。值显示的是谁，
            // 由旁边那块标签说明。
            if (mixed)
            {
                controlWidth = Mathf.Max(60f, controlWidth - MixedChipWidth);
            }

            float labelLineHeight = rowStacked ? StackedLabelHeight : 0f;
            if (rowStacked)
            {
                // 标签独占第一行，右边界跟着行宽走（不再被 LabelWidth 钉死）。
                SetTopLeft(row.Label.rectTransform, CheckWidth, 0f,
                    Mathf.Max(40f, rowWidth - CheckWidth - Padding), StackedLabelHeight);
                // 勾选框跟着标签那一行走，不然它会落在两行之间的缝上。
                SetTopLeft((RectTransform)row.Enable.transform,
                    8f, (StackedLabelHeight - 18f) * 0.5f, 18f, 18f);
            }
            else
            {
                SetTopLeft(row.Label.rectTransform, CheckWidth + 4f, 0f,
                    LabelWidth - 6f, RowHeight);
            }

            SetTopLeft(row.ControlHost,
                CheckWidth + (rowStacked ? 0f : LabelWidth), labelLineHeight,
                controlWidth, Mathf.Max(ControlHeight, rowHeight - labelLineHeight));

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

                // 这一处以前直接改 floor 并只调 editor.SaveState，
                // **没有进工作台自己的历史**。联机要同步所有写谱面的动作，
                // 所以在这里补一次上报（旧值要在写之前取）。
                int oldFloor = bound.floor;

                editor.SaveState();
                bound.floor = parsed;
                QueueRefresh();

                if (oldFloor != parsed)
                {
                    Multiplayer.MultiplayerEditSink.Report(editor, bound,
                        new[] { "floor" }, new object[] { parsed }, new[] { false });
                }
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

        /// <summary>
        /// 这一条属性在当前事件上是不是被「禁用」了（原生那套 `disabled` 字典）。
        /// **static** —— 只用到入参，所以 ImGui 那版参数栏能直接复用。
        /// </summary>
        internal static bool IsDisabled(LevelEvent levelEvent, string key)
        {
            bool disabled;
            return levelEvent.disabled != null
                && levelEvent.disabled.TryGetValue(key, out disabled)
                && disabled;
        }

        /// <summary>
        /// 事件级的「生效 / 停用」（`LevelEvent.active`）。
        ///
        /// **和上面那个 `SetDisabled` 不是一回事**：那是属性级的，只让某一条
        /// 属性不参与求值；这个让整个事件失效。原版编辑器把这件事放在自己的
        /// 事件清单上，工作台一直没有入口。
        ///
        /// 走游戏自己的 `scnEditor.EnableEvent` —— 它顺手调
        /// `ApplyEventsToFloors()`，少了那一步值改了、画面和播放都不跟。
        /// 它自己不记撤销，所以这里补一条（含联机上报）。
        /// </summary>
        internal void SetEventActive(LevelEvent levelEvent, bool active)
        {
            if (levelEvent == null)
            {
                return;
            }

            // **只读模式：什么都不能改。**
            //
            // 这条是漏的：`Commit` 那条路一早就有这道闸，可顶部那个「生效」开关
            // 走的是这里、不走 `Commit`，于是只读下照样能开关 —— 用户实测就是
            // 「只读居然能开关」。守卫统一走 `AllowEdit`，提示语也只有一句。
            if (owner != null && !owner.AllowEdit())
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            // 批量模式下整批一起改 —— 那个勾代表的是"这一批"，
            // 只改掉一个等于说了句假话（和 SetDisabled 同一条理由）。
            // 判成员的理由也一样：设置页不属于任何一批。
            if (batchEvents != null && batchEvents.Count > 1 && BatchContains(levelEvent))
            {
                EditorCommandHistory.BeginBatch("批量改生效状态");
                try
                {
                    for (int i = 0; i < batchEvents.Count; i++)
                    {
                        ApplyEventActive(editor, batchEvents[i], active);
                    }
                }
                finally
                {
                    EditorCommandHistory.EndBatch();
                }
                QueueRefresh();
                return;
            }

            ApplyEventActive(editor, levelEvent, active);
            QueueRefresh();
        }

        /// <summary>一个事件上落一次生效/停用，并按需记一条撤销。</summary>
        private void ApplyEventActive(scnEditor editor, LevelEvent levelEvent, bool active)
        {
            if (levelEvent == null || !IsLive(levelEvent) || levelEvent.active == active)
            {
                return;
            }
            bool from = levelEvent.active;
            editor.EnableEvent(levelEvent, active);
            EditorCommandHistory.RecordEventActive(editor, levelEvent, from, active);
        }

        internal void SetDisabled(LevelEvent levelEvent, string key, bool disabled)
        {
            // **只读模式：什么都不写。** 同 `Commit` 那条 —— 这里也是改谱面
            // （切某个属性的「启用 / 禁用」）。粒子浮窗里的那个勾选框也走这一条。
            if (owner != null && !owner.AllowEdit())
            {
                return;
            }

            // 批量模式下「启用 / 禁用」也是整批一起改 —— 行的那个勾选开关
            // 在批量里代表的是「这一批」，只改掉一个等于说了句假话。
            // 判成员的理由和 Commit 那条一样：设置页不属于任何一批。
            if (batchEvents != null && batchEvents.Count > 1 && BatchContains(levelEvent))
            {
                for (int i = 0; i < batchEvents.Count; i++)
                {
                    SetDisabledOne(batchEvents[i], key, disabled);
                }
                return;
            }

            SetDisabledOne(levelEvent, key, disabled);
        }

        private void SetDisabledOne(LevelEvent levelEvent, string key, bool disabled)
        {
            if (levelEvent == null || !IsLive(levelEvent))
            {
                return;
            }
            // **不能要求键已经存在。** 事件的 `disabled` 字典只装「谱面里显式禁过」
            // 的那几条，多数事件上根本没有这个键 —— 早先这里一判 `ContainsKey`
            // 就直接返回，于是参数栏那个勾选框点了等于没点（写不进去，重建后又
            // 回到勾选态）。缺键就现建一个，和原生面板一样。
            if (levelEvent.disabled == null)
            {
                levelEvent.disabled = new Dictionary<string, bool>();
            }
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.SaveState();
            }

            bool changed = levelEvent.disabled[key] != disabled;
            levelEvent.disabled[key] = disabled;

            // 同上：这一处原先只有 SaveState，没进工作台历史。
            if (changed && editor != null)
            {
                Multiplayer.MultiplayerEditSink.Report(editor, levelEvent,
                    new[] { key }, new object[] { levelEvent[key] }, new[] { disabled });
            }

            if (changed)
            {
                RefreshAfterDisabledChange(levelEvent, key, disabled);
            }
        }

        /// <summary>
        /// 勾掉 / 勾回一条属性之后，把改动推到画面上。两条路完全不一样：
        ///
        /// - **装饰物**：不去碰组件。预览求值器按作者数据算起点（见
        ///   `DecorationCorePreviewDomain.AuthorPosition`），只要让它重新分析
        ///   一次就够 —— 找组件这条路不可靠（检查器拿到的是 fake 事件，
        ///   `UpdateDecorationObject` 遇到它直接 return，一次 Setup 都不跑）。
        /// - **轨道 / 格子**：位移是在 `scnGame.ApplyEventsToFloors` 里烘进
        ///   `floor.offsetPos` 的（那里认 `disabled`，第 514 行那个判断），所以
        ///   重烘一次它自己就回到该在的位置；预览的基准值也是在 Analyze 时从
        ///   `startPos + offsetPos` 抓的，得让它重新分析一次才吃到新值。
        /// </summary>
        private void RefreshAfterDisabledChange(LevelEvent levelEvent, string key, bool disabled)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }
            if (levelEvent != null && levelEvent.info != null && levelEvent.info.isDecoration)
            {
                // 求值器平时「时间没变就整帧早退」，置脏位才会拿新状态重算一次。
                Preview.Domains.DecorationCorePreviewDomain.PositionSeedDirty = true;
                return;
            }
            try
            {
                editor.ApplyEventsToFloors();
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PropertyPanel] rebake after disabled change failed: " + exception);
            }
            EditorPlaybackBridge.RequestEvaluatorRebuild("property-disabled");
        }

        /// <summary>
        /// 解析行标签。先试原生那三步查表，因为它
        /// 跟随游戏自身的语言；在真实事件上实测，26 个属性里漏掉 11 个，
        /// 所以最后兜底渲染键名本身，而不是让这一行
        /// 没有标签。
        /// </summary>
        /// <summary>
        /// 解析行标签 —— **完全照原生的 `Property.info` 那三段**（`Property.cs:47-71`）。
        ///
        /// **顺序不能换。** `customLabel` 和 `customLocalizationKey` 在原生里
        /// 是**优先于**那两张本地化表的；反过来的话，凡是指定了这两个的字段都会
        /// 落到兜底那一步、显示成英文键名 —— 表现就是"一部分参数有翻译、
        /// 一部分没有"。
        ///
        /// 原来的写法正是反的，而且**整个漏掉了 `customLocalizationKey`**：
        /// 相机设置那页的 `Relative To`、`Start Cam Low VFX` 就是这么漏的。
        /// </summary>
        internal static string ResolveLabel(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
        {
            if (info == null)
            {
                return string.Empty;
            }

            if (info.customLabel != null)
            {
                return info.customLabel;
            }

            if (info.customLocalizationKey != null)
            {
                return string.IsNullOrEmpty(info.customLocalizationKey)
                    ? string.Empty
                    : RDString.Get(info.customLocalizationKey);
            }

            bool exists;
            string typeName = info.levelEventInfo == null ? null : info.levelEventInfo.name;
            if (!string.IsNullOrEmpty(typeName))
            {
                string label = RDString.GetWithCheck(
                    "editor." + typeName + "." + info.name, out exists);
                if (exists && !string.IsNullOrEmpty(label))
                {
                    return label;
                }
            }

            string byKey = RDString.GetWithCheck("editor." + info.name, out exists);
            if (exists && !string.IsNullOrEmpty(byKey))
            {
                return byKey;
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
                    case PropertyType.Tile:
                        BuildTile(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.FloatPair:
                        BuildFloatPair(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.Vector2Range:
                        BuildVector2Range(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.MinMaxGradient:
                        BuildMinMaxGradient(row, levelEvent, info, x, width);
                        break;
                    case PropertyType.ParticlePlayback:
                        BuildParticlePlayback(row, levelEvent, info, x, width);
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

        /// <summary>
        /// Tile 类型：一个整数字段 + 一个「本格 / 起点 / 终点」的模式按钮。
        ///
        /// ImGui 版早就是这个形态了，uGUI 这一份还停在「暂不支持 Tile」——
        /// 那是 ImGui 改造前的旧实现。这里按 ImGui 版的语义补齐：
        /// 数字与模式分开提交，提交时取另一半的**当前值**。
        /// </summary>
        private void BuildTile(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            float numberWidth = Mathf.Clamp(width * 0.42f, 48f, width);
            const float gap = 6f;
            float modeWidth = Mathf.Max(40f, width - numberWidth - gap);

            Tuple<int, TileRelativeTo> stored = SafeGet<Tuple<int, TileRelativeTo>>(
                levelEvent, info, null);
            if (stored == null)
            {
                // 老谱面没写这一项：默认「本格 + 0」，与 ResolveTile 对 null 的处理一致。
                stored = new Tuple<int, TileRelativeTo>(0, TileRelativeTo.ThisTile);
            }

            LevelEvent bound = levelEvent;
            string key = info.name;
            int offset = stored.Item1;
            TileRelativeTo mode = stored.Item2;

            TMP_InputField field = CreateInputField(row, "Tile", x, numberWidth,
                "Standard", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            row.Controls.Add(field.gameObject);
            field.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                int parsed;
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return;
                }
                Commit(bound, key, new Tuple<int, TileRelativeTo>(parsed, mode));
            });

            // 模式：点击循环三档，不做下拉浮层（这一格只有三个取值，
            // 弹层反而多一次点击、还要处理浮层层级）。
            float modeX = x + numberWidth + gap;
            RectTransform modeRect = NewChildRect("TileMode", row.ControlHost);
            SetTopLeft(modeRect, modeX, (RowHeight - ControlHeight) * 0.5f,
                modeWidth, ControlHeight);
            BuildFieldChrome(modeRect, out Image modeBackground, out Image modeRim);
            modeBackground.raycastTarget = true;
            Button modeButton = modeRect.gameObject.AddComponent<Button>();
            modeButton.targetGraphic = modeBackground;
            modeButton.navigation = new Navigation { mode = Navigation.Mode.None };

            TextMeshProUGUI modeLabel = CreateChildText("TileModeLabel", modeRect,
                6f, 0f, modeWidth - 12f, ControlHeight, TileModeLabel(mode), 12f);
            modeLabel.alignment = TextAlignmentOptions.MidlineLeft;
            modeLabel.color = UiTheme.Current.TextPrimary;
            row.Controls.Add(modeRect.gameObject);

            TileRelativeTo capturedMode = mode;
            modeButton.onClick.AddListener(delegate
            {
                if (bound == null)
                {
                    return;
                }
                TileRelativeTo next =
                    capturedMode == TileRelativeTo.ThisTile ? TileRelativeTo.Start
                    : capturedMode == TileRelativeTo.Start ? TileRelativeTo.End
                    : TileRelativeTo.ThisTile;
                capturedMode = next;
                modeLabel.text = TileModeLabel(next);
                Commit(bound, key, new Tuple<int, TileRelativeTo>(offset, next));
            });
        }

        private static string TileModeLabel(TileRelativeTo mode)
        {
            switch (mode)
            {
                case TileRelativeTo.Start:
                    return "起点";
                case TileRelativeTo.End:
                    return "终点";
                default:
                    return "本格";
            }
        }

        /// <summary>一对浮点（`Tuple<float,float>`），最小 ~ 最大。</summary>
        private void BuildFloatPair(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            Tuple<float, float> pair = SafeGet<Tuple<float, float>>(levelEvent, info, null);
            if (pair == null)
            {
                pair = new Tuple<float, float>(0f, 0f);
            }
            float gap = 6f;
            float suffixWidth = string.IsNullOrEmpty(info.unit) ? 0f : 22f;
            float usable = Mathf.Max(40f, width - suffixWidth);
            float half = Mathf.Max(1f, (usable - gap) * 0.5f);

            LevelEvent bound = levelEvent;
            string key = info.name;
            float low = pair.Item1;
            float high = pair.Item2;

            TMP_InputField lowField = CreateInputField(row, "PairLow", x, half,
                "0.###", FormatFloat(low));
            TMP_InputField highField = CreateInputField(row, "PairHigh", x + half + gap, half,
                "0.###", FormatFloat(high));
            row.Controls.Add(lowField.gameObject);
            row.Controls.Add(highField.gameObject);

            lowField.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                float parsed;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    Commit(bound, key, new Tuple<float, float>(parsed, high));
                }
            });
            highField.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                float parsed;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    Commit(bound, key, new Tuple<float, float>(low, parsed));
                }
            });
        }

        /// <summary>
        /// 两对 Vector2（`Tuple<Vector2,Vector2>`）。排成两行：X 一行、Y 一行。
        /// 行高已在 `StacksVertically` 里按双倍预留。
        /// </summary>
        private void BuildVector2Range(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            Tuple<Vector2, Vector2> range =
                SafeGet<Tuple<Vector2, Vector2>>(levelEvent, info, null);
            if (range == null)
            {
                range = new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
            }
            Vector2 a = range.Item1;
            Vector2 b = range.Item2;

            const float axisWidth = 16f;
            const float gap = 6f;
            float suffixWidth = string.IsNullOrEmpty(info.unit) ? 0f : 22f;
            float usable = Mathf.Max(60f, width - axisWidth - suffixWidth);
            float fieldWidth = Mathf.Max(24f, (usable - gap) * 0.5f);

            LevelEvent bound = levelEvent;
            string key = info.name;

            BuildRangeLine(row, bound, key, x, StackedFieldTop, axisWidth, fieldWidth, gap,
                "X", a.x, b.x,
                delegate(float low, float high)
                {
                    Commit(bound, key, new Tuple<Vector2, Vector2>(
                        new Vector2(low, a.y), new Vector2(high, b.y)));
                });
            BuildRangeLine(row, bound, key, x,
                StackedFieldTop + ControlHeight + StackedFieldGap,
                axisWidth, fieldWidth, gap, "Y", a.y, b.y,
                delegate(float low, float high)
                {
                    Commit(bound, key, new Tuple<Vector2, Vector2>(
                        new Vector2(a.x, low), new Vector2(b.x, high)));
                });
        }

        /// <summary>「X [最小] [最大]」这一行。</summary>
        private void BuildRangeLine(Row row, LevelEvent bound, string key,
            float x, float y, float axisWidth, float fieldWidth, float gap,
            string axis, float low, float high, System.Action<float, float> apply)
        {
            CreateChildText("Axis" + axis, row.ControlHost, x, y, axisWidth,
                ControlHeight, axis, 11f).color = UiTheme.Current.TextMuted;

            TMP_InputField lowField = CreateInputFieldAt(row, "Min" + axis,
                x + axisWidth, fieldWidth, y, "0.###", FormatFloat(low));
            TMP_InputField highField = CreateInputFieldAt(row, "Max" + axis,
                x + axisWidth + fieldWidth + gap, fieldWidth, y, "0.###", FormatFloat(high));
            row.Controls.Add(lowField.gameObject);
            row.Controls.Add(highField.gameObject);

            float capturedHigh = high;
            float capturedLow = low;
            lowField.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                float parsed;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    apply(parsed, capturedHigh);
                }
            });
            highField.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing || bound == null)
                {
                    return;
                }
                float parsed;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    apply(capturedLow, parsed);
                }
            });
        }

        /// <summary>
        /// 双色渐变：两个 HEX 字段 + 两个色块（只读显示）。
        /// 提交类型必须是 `SerializedMinMaxGradient`，游戏读的是它。
        /// </summary>
        private void BuildMinMaxGradient(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            Color low = Color.white;
            Color high = Color.white;
            try
            {
                ParticleSystem.MinMaxGradient gradient = levelEvent.GetMinMaxGradient(info.name);
                bool twoColors = gradient.mode == ParticleSystemGradientMode.TwoColors;
                low = twoColors ? gradient.colorMin : gradient.color;
                high = twoColors ? gradient.colorMax : gradient.color;
            }
            catch (Exception)
            {
                // 读不到就用白色，用户改一次就覆盖了。
            }

            const float gap = 6f;
            float half = Mathf.Max(60f, (width - gap) * 0.5f);

            LevelEvent bound = levelEvent;
            string key = info.name;
            Color capturedLow = low;
            Color capturedHigh = high;

            BuildGradientHalf(row, x, 0f, half, "Low", low,
                delegate(Color picked)
                {
                    capturedLow = picked;
                    CommitGradient(bound, key, picked, capturedHigh);
                });
            BuildGradientHalf(row, x + half + gap, 0f, half, "High", high,
                delegate(Color picked)
                {
                    capturedHigh = picked;
                    CommitGradient(bound, key, capturedLow, picked);
                });
        }

        private void BuildGradientHalf(Row row, float x, float y, float width, string caption,
            Color color, System.Action<Color> apply)
        {
            const float swatch = 18f;
            Image chip = CreateChildImage("Swatch" + caption, row.ControlHost, x,
                y + (ControlHeight - swatch) * 0.5f, swatch, swatch);
            chip.color = color;
            chip.raycastTarget = false;
            row.Controls.Add(chip.gameObject);

            TMP_InputField field = CreateInputFieldAt(row, "Hex" + caption,
                x + swatch + 4f, Mathf.Max(24f, width - swatch - 4f), y,
                null, ColorToHex(color));
            row.Controls.Add(field.gameObject);
            field.onEndEdit.AddListener(delegate(string text)
            {
                if (syncing)
                {
                    return;
                }
                Color parsed;
                if (TryParseHex(text, out parsed))
                {
                    chip.color = parsed;
                    apply(parsed);
                }
            });
        }

        private void CommitGradient(LevelEvent bound, string key, Color low, Color high)
        {
            if (bound == null)
            {
                return;
            }
            Commit(bound, key, new ADOFAI.Editor.Models.SerializedMinMaxGradient
            {
                mode = ParticleSystemGradientMode.TwoColors,
                color1 = ColorToHex(low),
                color2 = ColorToHex(high),
            });
        }

        /// <summary>
        /// 粒子发射器：播放/暂停、停止、重置。原版还有「编辑」，
        /// 那扇窗口目前仍是 ImGui 的，这里暂时不给入口。
        /// </summary>
        private void BuildParticlePlayback(Row row, LevelEvent levelEvent, ADOFAI.PropertyInfo info,
            float x, float width)
        {
            scrParticleDecoration decoration = null;
            try
            {
                if (levelEvent != null && scrDecorationManager.instance != null)
                {
                    decoration = scrDecorationManager.GetDecoration(levelEvent) as scrParticleDecoration;
                }
            }
            catch (Exception)
            {
                decoration = null;
            }

            ParticleSystem system = decoration == null ? null : decoration.particleSystem;
            if (system == null)
            {
                row.Controls.Add(CreateChildText("NoParticle", row.ControlHost, x, 0f,
                    width, RowHeight, "（还没有对应的粒子对象）", 12f).gameObject);
                return;
            }

            const float gap = 6f;
            float buttonWidth = Mathf.Max(1f, (width - gap * 2f) / 3f);
            string[] labels = { system.isPlaying ? "暂停" : "播放", "停止", "重置" };
            for (int i = 0; i < labels.Length; i++)
            {
                int captured = i;
                RectTransform buttonRect = NewChildRect("Particle" + i, row.ControlHost);
                SetTopLeft(buttonRect, x + (buttonWidth + gap) * i,
                    (RowHeight - ControlHeight) * 0.5f, buttonWidth, ControlHeight);
                BuildFieldChrome(buttonRect, out Image buttonFace, out Image buttonRim);
                buttonFace.raycastTarget = true;
                Button button = buttonRect.gameObject.AddComponent<Button>();
                button.targetGraphic = buttonFace;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                TextMeshProUGUI label = CreateChildText("Label", buttonRect,
                    0f, 0f, buttonWidth, ControlHeight, labels[i], 12f);
                label.alignment = TextAlignmentOptions.Center;
                button.onClick.AddListener(delegate
                {
                    if (system == null)
                    {
                        return;
                    }
                    if (captured == 0)
                    {
                        ParticleSystem.MainModule main = system.main;
                        main.useUnscaledTime = true;
                        if (system.isPlaying)
                        {
                            system.Pause();
                        }
                        else
                        {
                            system.Play();
                        }
                    }
                    else if (captured == 1)
                    {
                        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                    else
                    {
                        system.Clear();
                    }
                    label.text = system.isPlaying ? "暂停" : "播放";
                });
                row.Controls.Add(button.gameObject);
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
            background.sprite = GetRoundedSprite(24, 0f, false);
            background.type = Image.Type.Sliced;
            background.fillCenter = true;
            background.color = Color.black;
            background.raycastTarget = true;
            Image rim = CreateChildImage("Rim", rect, 0f, 0f, width - 4f, ControlHeight);
            rim.sprite = GetRoundedSprite(24, 0f, true);
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
            // **成对字段也要给单位留出同样的槽位。**
            //
            // 单值行会把右侧 22px 留给单位文字，成对行原来没有留 —— 于是
            // 同一栏里「两个框并排」的那几行右边缘比别的行更靠右，
            // 整列读起来间距不齐（用户指出的是这个）。
            float suffixWidth = string.IsNullOrEmpty(info.unit) ? 0f : 22f;
            float usable = Mathf.Max(40f, width - suffixWidth);
            bool stacked = usable < MinimumTwoAcrossWidth;
            float half = stacked ? usable : (usable - gap) * 0.5f;
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

            // 色块本身就是**开调色盘的按钮**。它原来 `raycastTarget = false`
            // ——纯给眼睛看的，于是这个属性只能靠手打 HEX，而原版是有调色盘的。
            RectTransform swatchRect = NewChildRect("Swatch", row.ControlHost);
            SetTopLeft(swatchRect, x, (RowHeight - swatch) * 0.5f, swatch, swatch);
            Image preview = swatchRect.gameObject.AddComponent<Image>();
            // **实心**圆角，不是空心环：`frame: true` 那张是给描边用的，
            // 指望 `fillCenter` 把中间补上并不成立 —— 色块会变成一副空心描边，
            // 看着像这个属性没有颜色。
            preview.sprite = GetRoundedSprite(Mathf.CeilToInt(swatch * 2f) + 4, 5f, false);
            preview.type = Image.Type.Sliced;
            preview.color = value;
            row.Controls.Add(swatchRect.gameObject);

            TMP_InputField field = CreateInputField(
                row, "Hex", x + swatch + 6f, width - swatch - 6f, null, ColorToHex(value));
            row.Controls.Add(field.gameObject);

            string key = info.name;
            LevelEvent bound = levelEvent;

            // 绑的是**这一行自己的**色块和 HEX 框：调色盘拖动时实时更新它们，
            // 但不走重画 —— 重画会把这个弹出层一起销毁，拖到一半盘就没了。
            Button opener = swatchRect.gameObject.AddComponent<Button>();
            opener.targetGraphic = preview;
            opener.navigation = new Navigation { mode = Navigation.Mode.None };
            opener.onClick.AddListener(delegate
            {
                OpenColorPopup(opener, bound, key, preview, field);
            });
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
            // 直角 + 纯黑底：形状只靠外圈描边给出来，不用圆角也不用填色块。
            background.sprite = GetRoundedSprite(24, 0f, false);
            background.type = Image.Type.Sliced;
            background.fillCenter = true;
            background.color = Color.black;
            background.raycastTarget = true;

            rim = CreateChildImage("Rim", rect, 0f, 0f, rect.sizeDelta.x, rect.sizeDelta.y);
            rim.sprite = GetRoundedSprite(24, 0f, true);
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
            CloseColorPopup();
                return;
            }
            CloseEnumPopup();
            CloseColorPopup();
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
            // **下拉列表：直角、纯黑底、只靠描边成形。**
            viewportBackground.sprite = GetRoundedSprite(24, 0f, false);
            viewportBackground.type = Image.Type.Sliced;
            viewportBackground.fillCenter = true;
            viewportBackground.color = Color.black;
            viewportBackground.raycastTarget = true;
            viewportRect.gameObject.AddComponent<RectMask2D>();

            // 描边单独一层，画在底之上；没有它这张浮层在深色面板上会"没有边界"。
            Image popupRim = CreateChildImage("PopupRim", popup,
                0f, 0f, popup.rect.width, popup.rect.height);
            popupRim.sprite = GetRoundedSprite(24, 0f, true);
            popupRim.type = Image.Type.Sliced;
            popupRim.fillCenter = false;
            popupRim.color = FieldBorder;
            popupRim.raycastTarget = false;

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
            CloseColorPopup();
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

        /// <summary>
        /// 打开调色盘。
        ///
        /// 摆位和枚举弹层同一套（贴锚点，下面放不下就往上弹再夹回面板里），
        /// 这样两个弹层的脾气一致，不用记两套。
        /// </summary>
        private void OpenColorPopup(Button anchor, LevelEvent target, string key, Image swatch,
            TMP_InputField hexField)
        {
            CloseColorPopup();
            CloseEnumPopup();

            RectTransform anchorRect = anchor == null ? null : anchor.transform as RectTransform;
            if (anchorRect == null || target == null || string.IsNullOrEmpty(key) || panelRoot == null)
            {
                return;
            }

            colorTarget = target;
            colorKey = key;
            colorAnchorButton = anchor;
            colorSwatch = swatch;
            colorHexField = hexField;

            ADOFAI.PropertyInfo info = FindInfo(target, key);
            colorOriginalHex = ColorToHex(SafeGetColor(target, info));
            colorOriginalDisabled = target.disabled != null
                && target.disabled.ContainsKey(key)
                && target.disabled[key];
            colorLiveValue = SafeGetColor(target, info);

            const float padding = 8f;
            float popupWidth = EditorColorPicker.BodyWidth + padding * 2f;
            float popupHeight = EditorColorPicker.BodyHeight + padding * 2f;

            float anchorLocalY = panelRoot.InverseTransformPoint(
                anchorRect.TransformPoint(Vector3.zero)).y;
            float panelHeight = panelRoot.rect.height;

            float spaceBelow = panelHeight + anchorLocalY - PopupMargin;
            float spaceAbove = -anchorLocalY - ControlHeight - PopupMargin * 2f - PopupGap;
            bool dropUp = spaceBelow < popupHeight && spaceAbove > spaceBelow;

            float anchorTop = -anchorLocalY + ControlHeight;
            float popupTop = dropUp ? anchorTop - popupHeight - PopupGap : anchorTop + PopupGap;
            popupTop = Mathf.Clamp(popupTop, PopupMargin,
                Mathf.Max(PopupMargin, panelHeight - popupHeight - PopupMargin));

            RectTransform popup = NewChildRect("ColorPopup", panelRoot);
            SetTopLeft(popup, Padding, popupTop, popupWidth, popupHeight);
            ApplyPopupFrame(popup, popupWidth, popupHeight);
            colorPopupRoot = popup;
            colorPopup.Add(popup.gameObject);

            RectTransform body = NewChildRect("Body", popup);
            SetTopLeft(body, padding, padding, EditorColorPicker.BodyWidth, EditorColorPicker.BodyHeight);

            EditorColorPicker picker = popup.gameObject.AddComponent<EditorColorPicker>();
            picker.Build(body, font, SafeGetColor(target, info),
                delegate(Color picked) { ApplyColorPreview(picked); },
                delegate(Color picked) { CommitColorFromPicker(picked); });
        }

        /// <summary>
        /// 收起调色盘，并**补记一步撤销**。
        ///
        /// 拖动期间是**实时写进谱面**的（这样画面跟着动），但没记历史 ——
        /// 每帧记一条的话，拖一次鼠标就是上百条，Ctrl+Z 要点到手酸。
        /// 所以这里是这次调色**唯一**的一条撤销，从进来时那个值记到现在这个值。
        /// </summary>
        private void CloseColorPopup()
        {
            if (colorTarget != null && !string.IsNullOrEmpty(colorKey) && IsLive(colorTarget))
            {
                // 先把最后一次落点补写进去，再读回来记账 —— 顺序反了会记下
                // 一个"屏幕上是这个颜色、谱面里是上一个值"的偏差。
                WriteColorNow();

                // **这一次求值器重建放在这里，而不是拖动途中。**
                // 拖动期间那份基线用不上（逐帧状态是自己播种的），
                // 只有收尾时才需要它对齐；一次重建和每秒六十次是两个量级。
                EditorPlaybackBridge.RequestEvaluatorRebuild("color-picker-commit");

                string now = ColorToHex(SafeGetColor(colorTarget, FindInfo(colorTarget, colorKey)));
                if (!string.Equals(now, colorOriginalHex, StringComparison.OrdinalIgnoreCase))
                {
                    EditorCommandHistory.RecordPropertyEdit(
                        scnEditor.instance, colorTarget, colorKey,
                        colorOriginalHex, now, colorOriginalDisabled, false);
                }
            }

            for (int i = 0; i < colorPopup.Count; i++)
            {
                if (colorPopup[i] != null)
                {
                    Destroy(colorPopup[i]);
                }
            }
            colorPopup.Clear();
            colorPopupRoot = null;
            colorTarget = null;
            colorKey = null;
            colorAnchorButton = null;
            colorSwatch = null;
            colorHexField = null;
        }

        /// <summary>
        /// 调色盘拖动时的实时写入：改数据 + 刷画面，**不记撤销**。
        ///
        /// 界面这边只动这一行自己的色块和 HEX 框 —— 不能走 QueueRefresh，
        /// 一重画这个弹出层就被销毁了。
        /// </summary>
        /// <summary>
        /// 拖动途中的**显示**回调：只把这一行的色块染一下色，**一个字都不写进谱面**。
        ///
        /// 每帧都写的话，参数栏每帧都要重跑一次装饰物 Setup —— 那才是拖动
        /// 卡顿的来源。盘子里自己的色块、色条、指示环是每帧更新的，
        /// 拖起来照旧跟手；画面等**松手**再变。
        /// </summary>
        private void ApplyColorPreview(Color picked)
        {
            if (colorSwatch != null)
            {
                colorSwatch.color = picked;
            }
            colorLiveValue = picked;
        }

        /// <summary>
        /// 松手（或输入框改完）：把定下来的颜色**落进谱面**。
        ///
        /// 拖动全程只走这一条 —— 一次拖动 = 一次写入，而不是六十次。
        /// </summary>
        private void CommitColorFromPicker(Color picked)
        {
            colorLiveValue = picked;
            if (colorHexField != null)
            {
                colorHexField.SetTextWithoutNotify(ColorToHex(picked));
            }
            WriteColorNow();
        }

        /// <summary>
        /// 把 <see cref="colorLiveValue"/> 落进谱面并刷画面。
        ///
        /// **只在松手、输入框改完、以及收起调色盘时走这里**，拖动途中不走。
        /// 一次拖动就是一次写入。
        /// </summary>
        private void WriteColorNow()
        {
            if (colorTarget == null || string.IsNullOrEmpty(colorKey) || !IsLive(colorTarget))
            {
                return;
            }

            try
            {
                colorTarget[colorKey] = ColorToHex(colorLiveValue);
                if (colorTarget.disabled != null && colorTarget.disabled.ContainsKey(colorKey))
                {
                    // 动一个值就意味着作者想让它生效 —— 和别处一致。
                    colorTarget.disabled[colorKey] = false;
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PropertyPanel] 调色盘写入 " + colorKey + " 失败：" + exception);
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor != null && colorTarget.info != null && colorTarget.info.isDecoration)
            {
                try
                {
                    editor.UpdateDecorationObject(colorTarget);
                }
                catch (Exception exception)
                {
                    Main.Logger.Error("[PropertyPanel] 调色盘刷新装饰物失败：" + exception);
                }
            }

            if (ReferenceEquals(colorTarget, settingsPage))
            {
                ApplySettingsSideEffects(FindInfo(colorTarget, colorKey));

                // 改的是背景设置里那个颜色，原生会顺手重铺一次背景
                // （PropertyControl_Color.OnChange 里就有这一支）。
                // 不补的话值变了、背景没跟。
                if (colorTarget.eventType == LevelEventType.BackgroundSettings)
                {
                    SetBackgroundSafe();
                }
            }

            // **这里没有 RequestEvaluatorRebuild，是照着原版删的。**
            //
            // 那个函数第一件事是 evaluatorBackend.End()，把求值器**整个拆掉**、
            // 下次求值时重来。按帧调就是每秒六十次完全重建 —— 那正是
            // "卡得要命"的来源，而原生从不做这件事。
            //
            // 画面对得上靠的是上面那次 UpdateDecorationObject：求值器的逐帧
            // 状态本来就是从装饰物那些字段播种的，字段一更新画面就跟上。
            // 只有 Analyze 期缓的那份基线要等一次重建 —— 那一次放在收起时做
            // （见 CloseColorPopup）。
        }

        /// <summary>重铺一次背景。原生改背景设置里的颜色时会顺手做这一步。</summary>
        private static void SetBackgroundSafe()
        {
            try
            {
                if (ADOBase.customLevel != null)
                {
                    ADOBase.customLevel.SetBackground();
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PropertyPanel] 重铺背景失败：" + exception);
            }
        }

        private bool PointerOverColorAnchor()
        {
            return colorAnchorButton != null
                && RectContains(colorAnchorButton.transform as RectTransform);
        }

        private bool PointerOverColorPopup()
        {
            return RectContains(colorPopupRoot);
        }

        private bool RectContains(RectTransform rect)
        {
            if (rect == null)
            {
                return false;
            }
            Vector2 point = CurrentPointerPosition();
            return RectTransformUtility.RectangleContainsScreenPoint(rect, point, PopupEventCamera(rect));
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
            if ((enumTarget == null && colorTarget == null) || !Input.GetMouseButtonDown(0))
            {
                return;
            }
            // 两个弹层各查各的锚点和面板。拖色拖到一半**绝不能**把盘收掉：
            // 拖动是从盘里按下去的，这一帧的按下点就落在 colorPopupRoot 里，
            // 下面那一句会把它放过去。
            if (PointerOverEnumAnchor() || PointerOverEnumPopup()
                || PointerOverColorAnchor() || PointerOverColorPopup())
            {
                return;
            }

            CloseEnumPopup();
            CloseColorPopup();
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
        internal static bool IsEaseEnum(ADOFAI.PropertyInfo info)
        {
            return info != null && info.enumType == typeof(DG.Tweening.Ease);
        }

        internal static bool TryParseEase(string name, out DG.Tweening.Ease ease)
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
            // **细一点。** 原版那条 `lineRenderer.Points = ...;` 之外什么都不设，
            // 但 `UILineRenderer.lineThickness = 2f` 只是**字段初值** ——
            // prefab 上的序列化值会把它覆盖掉，而原版看起来明显比 2 细。
            // 从 prefab 读不到那个值，所以按观感取 1：粗一档就会显得笨重，
            // 而这条曲线是拿来当"标志"看的，细才像原版。
            line.LineThickness = 1f;
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
        internal static Vector2[] SampleEaseCurve(DG.Tweening.Ease ease, int pointCount,
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

        // 弹窗浮在面板之上，所以整组取「浮层」那一档而不是面板自己的色——
        // 比面板亮一点，读起来才像盖在上面。选中态沿用全局的选中填充，
        // 不另配一套，这样弹窗里的选中和列表里的选中是同一个视觉语言。
        private static Color PopupSurface { get { return UiTheme.Current.SurfaceOverlay; } }
        private static Color PopupHover { get { return UiTheme.Current.SurfaceRaisedHover; } }
        private static Color PopupSelected { get { return UiTheme.Current.SelectedFill; } }
        private static Color PopupSelectedHover { get { return UiTheme.Current.SelectedHoverFill; } }
        private static Color PopupBorder { get { return UiTheme.Current.BorderStrong; } }
        private static Color PopupShadow { get { return UiTheme.Current.Shadow; } }

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

        internal static object ParseEnumValue(ADOFAI.PropertyInfo info, string name)
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

        /// <summary>
        /// 读一个枚举属性当前的值（枚举成员名）。**static** —— 它只用到
        /// `levelEvent` 和 `info`，不碰实例状态，所以 ImGui 那版参数栏能直接复用。
        /// </summary>
        internal static string ReadEnumName(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
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

        internal static string DisplayEnumName(LevelEvent levelEvent, ADOFAI.PropertyInfo info, string name)
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
        internal void Commit(LevelEvent levelEvent, string key, object value)
        {
            // **只读模式：什么都不写。**
            //
            // 拦这里就够 —— 事件参数的写入只有这一条路（技术架构里那条硬规矩），
            // 不必去给参数栏每个控件单独挂开关。
            if (owner != null && !owner.AllowEdit())
            {
                return;
            }

            // **批量模式下，一次改值要落到这一批全部事件上。**
            //
            // 控件绑的都还是第一个选中事件（ShowBatch / EnterBatch 那里定的），
            // 所以在这里把目标换成整批 —— 各个 Build* 的写入路径一个字都不用动。
            //
            // **但只在写的那一个确实在这一批里的时候才换。**
            //
            // 判成员而不是判 "batchEvents 非空"：左边开着设置页时，设置页自己也
            // 是一个 `LevelEvent`（`levelData.songSettings`），而它永远不属于时间轴
            // 那一批。按非空判，改一个设置就会把值写给一批正被选中的事件 —— 而且是
            // 静默的。设置页和参数栏是两块同时在屏上的面板，这条岔路真的走得到。
            if (batchEvents != null && batchEvents.Count > 1 && BatchContains(levelEvent))
            {
                CommitBatch(key, value);
                return;
            }

            CommitOne(levelEvent, key, value);
        }

        /// <summary>这个事件在不在这批里。</summary>
        private bool BatchContains(LevelEvent levelEvent)
        {
            if (levelEvent == null || batchEvents == null)
            {
                return false;
            }
            for (int i = 0; i < batchEvents.Count; i++)
            {
                if (ReferenceEquals(batchEvents[i], levelEvent))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把同一个值写进这一批全部事件，并且**只记一步撤销**。
        ///
        /// 「一步」靠 BeginBatch/EndBatch 攒起来、收口时合成一条复合命令。
        /// 逐个各自入栈会留下 N 条，作者按一次 Ctrl+Z 只退一个 —— 那正是
        /// 这个功能最容易做坏的地方。
        ///
        /// 死引用（原生撤销会整体换掉 levelData）跳过：写进去会弄坏无关数据，
        /// 记一步则会让历史里多一条描述「并未发生的编辑」的记录。
        /// </summary>
        private void CommitBatch(string key, object value)
        {
            EditorCommandHistory.BeginBatch("批量改参 · " + key);
            try
            {
                for (int i = 0; i < batchEvents.Count; i++)
                {
                    LevelEvent target = batchEvents[i];
                    if (target == null || !IsLive(target))
                    {
                        continue;
                    }
                    CommitOne(target, key, value);
                }
            }
            finally
            {
                // 中途抛出去也必须收口：攒下的命令留在 batch 里没人接，
                // 之后就再也进不了撤销栈了（见 BeginBatch 的说明）。
                EditorCommandHistory.EndBatch();
            }
        }

        /// <summary>把一次改值写进**一个**事件，并按需记一步撤销。</summary>
        private void CommitOne(LevelEvent levelEvent, string key, object value)
        {
            if (levelEvent == null || !IsLive(levelEvent))
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

            // 这个事件上的值本来就是这个 —— 什么都不用做。
            //
            // 必须挡在**写入之前**。点进输入框又原样点出去就会走到这里，
            // 而批量模式下这种「没改的提交」要是照写，会把各个事件本来
            // 不同的值统统抹成第一个事件的那个：一次什么都没改的操作，
            // 却把整批的差异抹平了，还占掉一步撤销。
            //
            // 唯独「本来被禁用」不能走这条路 —— 那种情况写同一个值是有
            // 意义的，它顺带把属性解禁（见下面）。
            if (Equals(previousValue, value) && !previousDisabled)
            {
                return;
            }

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

            // 设置页写回之后要补上原生的那两步副作用，否则值是变了、画面不跟。
            // 事件那条路不需要它 —— 事件的 `UpdateDecorationObject` 已经覆盖了。
            if (ReferenceEquals(levelEvent, settingsPage))
            {
                ADOFAI.PropertyInfo settingsInfo = FindInfo(levelEvent, key);
                ApplySettingsSideEffects(settingsInfo);

                // 改的是背景设置里那个颜色，原生还会顺手重铺一次背景
                // （PropertyControl_Color.OnChange 里就有这一支）。
                //
                // **两条写入路径都要这一步**：uGUI 那条写在调色盘的提交里
                // （CommitColorFromPicker），ImGui 那条是直接走 Commit 的 ——
                // 收在这里两边就都盖到了。
                if (settingsInfo != null
                    && settingsInfo.type == PropertyType.Color
                    && levelEvent.eventType == LevelEventType.BackgroundSettings)
                {
                    SetBackgroundSafe();
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

        /// <summary>
        /// 设置页写回之后的副作用。
        ///
        /// 复刻的是原生 `PropertyControl.ApplyTileChanges()` —— 那是原生控件
        /// 在写值之外**唯一**的副作用入口，只有两个分支：
        ///
        ///   · `affectsPath`   → 重算轨道 + 取消装饰物选中 + 重建装饰物对象
        ///   · `affectsFloors` → 把事件重新摊到地板上
        ///
        /// 不补这一步，表现就是「值在存档里变了、画面/时间轴没跟」。
        ///
        /// 原生还有个 `OnValueChange`，那只管粒子编辑器的预览（而且粒子编辑器
        /// 没开时它第一行就返回了），跟设置页无关，所以不搬。
        /// </summary>
        private static void ApplySettingsSideEffects(ADOFAI.PropertyInfo info)
        {
            if (info == null)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            try
            {
                if (info.affectsPath)
                {
                    editor.RemakePath();
                    editor.DeselectAllDecorations();
                    editor.UpdateDecorationObjects();
                }
                else if (info.affectsFloors)
                {
                    editor.ApplyEventsToFloors();
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[PropertyPanel] 设置页副作用失败：" + exception);
            }
        }

        // ---------------------------------------------------------------- 预览

        /// <summary>
        /// 装饰物事件对应的那张图。**给 ImGui 那版参数栏用的口子。**
        ///
        /// 逻辑和 uGUI 那扇的 <see cref="ShowPreview"/> 同源：先看事件自己有没有
        /// `decorationImage`，没有就拿 `tag` 反查创建该装饰物的事件（见
        /// <see cref="ResolveDecorationImage"/>）。图的取法也一样 ——
        /// 从装饰物管理器的 `imageHolder.customSprites` 里按文件名拿。
        ///
        /// <returns>
        /// **是不是装饰物事件。** 不是就整个不画预览块 —— 给 MoveTrack / MoveCamera
        /// 这类事件顶上留一条空带，只会让人以为"这里应该有东西但坏了"。
        /// 是装饰物但没图时返回 true、`sprite` 为 null，那边画占位和「<无图片>」。
        /// </returns>
        /// </summary>
        internal bool TryGetDecorationPreview(LevelEvent levelEvent,
            out Sprite sprite, out string filename)
        {
            sprite = null;
            filename = null;

            // 设置页一律不算装饰物：它的元数据未必把 `isDecoration` 标成 false
            // （那一位本来是给事件用的）。理由见 ShowPreview。
            if (settingsPage != null || levelEvent == null
                || levelEvent.info == null || !levelEvent.info.isDecoration)
            {
                return false;
            }

            filename = ResolveDecorationImage(levelEvent);
            if (string.IsNullOrEmpty(filename))
            {
                return true;
            }

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
            return true;
        }

        private void ShowPreview(LevelEvent levelEvent)
        {
            if (previewHost == null)
            {
                return;
            }

            // 只有装饰物事件才有精灵可用来辨认。为
            // 每个 MoveTrack / MoveCamera 事件都显示一个空框，只会白白浪费面板顶部。
            //
            // **设置页一律不算装饰物。** 它的元数据未必把 `isDecoration`
            // 标成 false（那一位本来是给事件用的），照它走会给设置页在顶上
            // 留出一条 146px 的空带 —— 正是"顶上总空着一块、不知道干嘛用"。
            bool isDecoration = settingsPage == null
                && levelEvent != null
                && levelEvent.info != null
                && levelEvent.info.isDecoration;
            string filename = isDecoration ? ResolveDecorationImage(levelEvent) : null;

            // **显隐一变就要把空间还给滚动区（或收回来）。** 只 SetActive
            // 的话块是没了、位置还占着，面板顶部会空出一条 116px 的空白带，
            // 看上去像"这里应该有东西但坏了"。见 previewVisible。
            previewVisible = isDecoration;
            previewHost.gameObject.SetActive(isDecoration);
            if (panelRoot != null)
            {
                ApplyScrollLayout(panelRoot.rect.width, panelRoot.rect.height);
            }

            if (!isDecoration)
            {
                return;
            }
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

        internal static T SafeGet<T>(LevelEvent levelEvent, ADOFAI.PropertyInfo info, T fallback)
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

        internal static T SafeGet<T>(LevelEvent levelEvent, string key, T fallback)
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

        internal static Color SafeGetColor(LevelEvent levelEvent, ADOFAI.PropertyInfo info)
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

        /// <summary>
        /// 把一个浮点写成界面上显示的文本。
        ///
        /// **NaN / 无穷一律写成空字符串。** 谱面里那些没设过的 Vector2
        /// （位置偏移、大小、视差偏移……）在数据里是 `[null, null]`，读出来就是
        /// NaN —— 直接写 "NaN" 会让人以为 mod 坏了。空着反而更对：那是"没设过"
        /// 的诚实表达，而且框里本来就能直接输入。
        ///
        /// 放在这里而不是各自的显示代码里，是因为**两扇参数栏都用它** ——
        /// 改一处两边一起变，不会一个显示 NaN、一个显示空。
        /// </summary>
        internal static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return string.Empty;
            }
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string FormatInt(float value)
        {
            // 同理：NaN 取整会变成 0，而那是在说一个假值。空着。
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return string.Empty;
            }
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
