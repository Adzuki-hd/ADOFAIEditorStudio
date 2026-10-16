using System;
using System.Collections.Generic;
using ADOFAIEditorStudio.Ui;
using System.Globalization;
using ADOFAI;
using ImGuiNET;
using UnityEngine;
// ImGui 那套 Vector2/Vector4 和 Unity 的同名，起别名区分（同 EditorImGuiBridge）。
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 参数栏（ImGui 版）。
    ///
    /// **已经做完的：只读显示 + 可改（带撤销）。还没做的：颜色那一列的调色盘、
    /// 多选批量。**
    ///
    /// 它和 uGUI 那扇 `EditorPropertyPanel` 读的是**同一份数据**
    /// （`levelEvent.info.propertiesInfo` + 同样三条过滤），**写也走同一条路**
    /// （`panel.Commit` / `panel.SetDisabled`）：
    ///
    ///   · 撤销   工作台自己的历史（不是 `scnEditor.SaveState`）
    ///   · 联机   一次编辑上报一次
    ///   · 活对象 写进 levelEvent 之后还要推到正在渲染的装饰物 / 预览上
    ///
    /// 这些语义全在那一条路径上。复制一份出来，两边迟早不一样，而
    /// "改了一个值却撤不回来"是最难查的那类问题。
    /// </summary>
    internal sealed class EditorImGuiInspector
    {
        /// <summary>
        /// 这一份画的是左栏那个设置页，不是右边的参数栏。
        ///
        /// **设置页和普通事件走的是同一条路** —— 它本身就是 levelData 上的一个
        /// `LevelEvent`（见 EditorStudioOverlay.FindSettingsPage），uGUI 那版
        /// 的差别也只是"不跟时间轴选中"（EditorPropertyPanel.ShowSettingsPage）。
        /// 所以整个类直接复用，只换三样：目标从哪来、画在哪、标题写什么。
        /// </summary>
        private readonly bool _settingsPane;

        /// <summary>窗口标题那一行。设置页写那一页的名字，参数栏写"参数"。</summary>
        private string _title = "参数";

        /// <summary>当前画的那个事件。和 uGUI 一样每 0.12 秒去看一眼选中变了没有。</summary>
        private LevelEvent _target;
        private float _nextPoll;

        internal EditorImGuiInspector(bool settingsPane)
        {
            _settingsPane = settingsPane;
        }

        /// <summary>
        /// 正在编辑的**那一行**的标识，以及它的文本缓冲。
        ///
        /// 不用每帧从值重新生成缓冲：那会把用户正在打的字冲掉。
        /// 只在"换了一行"的时候重新生成。
        /// </summary>
        private string _editRow;
        private string _editBuffer;

        /// <summary>
        /// 调色盘的颜色缓冲，以及"这一次开关已经写进去的值"。
        ///
        /// 缓冲必须跨帧留住：拖动期间每帧从事件重新读值的话，
        /// 调色盘每帧弹回原色，根本拖不动。
        ///
        /// `_colorCommitted` 用来判断"值真的变了没有" —— 松手时值没变就不写，
        /// 免得往撤销栈里塞空记录。
        /// </summary>
        private NVec4 _colorBuffer;
        private NVec4 _colorCommitted;
        /// <summary>
        /// **哪一行**的调色盘开着（没开就是 null）。
        ///
        /// 不能用一个 bool：一帧里要画好几行颜色。用 bool 的话，点 A 行的色块
        /// 打开弹层、置上标志，**同一帧**轮到 B 行时看到标志是开的，就会去
        /// B 行的作用域里找弹层 —— 找不到，于是走 else 把标志清掉，
        /// 弹层活不过一帧。日志里「开着 → 没开」只隔两行，就是同一帧两行的距离。
        /// </summary>
        private string _colorRow;

        /// <summary>这一帧要画的属性。复用同一个 List，免得每帧产生垃圾。</summary>
        private readonly List<KeyValuePair<string, ADOFAI.PropertyInfo>> _visible
            = new List<KeyValuePair<string, ADOFAI.PropertyInfo>>();

        /// <summary>
        /// 标签和它右边控件之间那道缝。
        ///
        /// **下面这几个间距一律用项目自己的刻度 `UiSpacing`（4 的倍数）**，
        /// 不要各写各的数字。这条规矩项目里早就定过了（版本说明里写过：
        /// "归到 4 的倍数后这种漂移就绝迹了"）—— 我这几处又各拍了一个数
        /// （7 / 6 / 10 / 12），同类元素差一两个像素，看着就是"到处都差一点"。
        /// </summary>
        ///
        /// 列宽本身是**量出来的**（见 DrawWideRows），不是常量 —— 标签是游戏给的
        /// 本地化文字，长度随语言变。照抄过 uGUI 的 `LabelWidth = 104`，
        /// 但那只配得上它那边的字号，这边中文字更大，直接撞到控件上。
        /// </summary>
        private const float LabelGap = 8f;
        /// <summary>
        /// 「启用」那一列要占的宽度。
        ///
        /// **必须按 ImGui 勾选框的实际宽度算。** 这里照抄过 uGUI 的
        /// `CheckWidth = 22` —— 漏了一件事：uGUI 那边的勾选框是 20 像素的方块，
        /// 而 **ImGui 的 `Checkbox` 占的是 `GetFrameHeight()`（约 35）**。
        /// 于是标签按 22 摆，正好压在勾选框上（用户看到的"字体和勾选框重叠"）。
        ///
        /// 窄排法不受影响，是因为那边用 `SameLine` 让 ImGui 自己排在勾选框后面。
        /// </summary>
        private static float EnableColumnWidth
        {
            get { return ImGui.GetFrameHeight() + LabelLeftGap; }
        }

        /// <summary>
        /// 标签相对"启用列"再往右让一点。
        ///
        /// 够了：勾选框自己占满那 22 像素，紧接着就是文字的话看着贴在一起。
        /// </summary>
        private const float LabelLeftGap = UiSpacing.Sm;
        private const float PollInterval = 0.12f;

        /// <summary>
        /// 值那一栏窄于这个就不排三列了。取 118 是因为一个数值框至少要放得下
        /// "1234.567"，再窄就只剩几个点的宽度 —— 看得见框、看不见内容，
        /// 那比换一种排法更糟。
        /// </summary>
        private const float MinimumValueWidth = 118f;

        /// <summary>
        /// 卡片内边距：控件离卡片边缘这么远。
        ///
        /// 一开始给的 4 —— 太挤了，输入框看着就要碰到卡片边。7 是"看得出是一张卡"
        /// 的下限。
        /// </summary>
        private const float CardPad = UiSpacing.Md;

        /// <summary>卡片之间的缝。</summary>
        private const float CardGap = UiSpacing.Sm;

        /// <summary>
        /// 行距。**写成"内边距×2 + 缝"**，这样以后想调缝直接调 `CardGap` 就行，
        /// 不用反推行距该给多少。
        /// </summary>
        private const float RowSpacing = CardPad * 2f + CardGap;

        /// <summary>下拉文字两侧的留白（箭头已经算在宽度里了，这里只是缩进的余量）。</summary>
        private const float EnumChromeWidth = 12f;

        /// <summary>值和单位之间那道缝。宽度计算里也要算它，不然单位会被挤出面板。</summary>
        private const float UnitGap = UiSpacing.Sm;

        /// <summary>并排的两个框（Vector2）每个窄于这个就上下叠。</summary>
        private const float MinimumPairFieldWidth = 74f;

        /// <summary>
        /// 批量模式下「多个值」那块标签的宽度。
        ///
        /// **和 uGUI 版的 `MixedChipWidth` 是同一个数（56）** —— 两边是同一个东西的
        /// 两种画法，宽度不一样会导致窄面板下换排法的时机差开。
        /// </summary>
        private const float MixedChipWidth = 56f;

        /// <summary>
        /// 顶上那个「生效」图标按钮的边长。取项目自己的刻度。
        ///
        /// 和装饰物列表行尾那三个同档（32 的按钮），但**图标画得比那边满** ——
        /// 那边一行挤三个、图标留白多一点才不糊；这个一行只有一个，
        /// 按那边的留白画出来就"太小了，像误画的"。
        /// </summary>
        private const float EventActiveButtonSize = UiSpacing.Xxl;  // 32
        /// <summary>图标占按钮边长的比例。留一点边，让图标不顶到按钮框上。</summary>
        private const float IconInButton = 0.82f;

        /// <summary>它和下面第一张卡片之间真正要留出的间距。</summary>
        private const float EventActiveGap = UiSpacing.Md;          // 12

        /// <summary>
        /// 顶上那块装饰物图的高度。和 uGUI 那扇的 `PreviewHeight` 同一个数 ——
        /// 两边是同一个东西的两种画法，高度不一样会导致换页时面板顶部跳一下。
        /// </summary>
        private const float PreviewHeight = 116f;

        /// <summary>并排两个框之间的缝。</summary>
        private const float PairGap = 6f;

        private const ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings
            // 别因为被点一下就跳到最前面 —— 模态的文字窗口必须一直压在它上面。
            | ImGuiWindowFlags.NoBringToFrontOnFocus;

        /// <summary>参数栏占据的画面矩形（设计坐标），桥接拿它去摆挡点击的那块遮罩。</summary>
        internal Rect Rect { get; private set; }

        /// <summary>
        /// 展开中的弹层占的矩形（没有弹层时是零矩形）。
        ///
        /// **弹层是画在面板外面的**（ImGui 把它摆在鼠标附近），而挡点击的遮罩必须
        /// 连它一起罩住 —— 不然点弹层超出面板的那部分会穿到后面的工作台上
        /// （症状：点最后一个缓速，时间轴的选中被取消）。
        ///
        /// 怎么量：**弹层本身就是一个 ImGui 窗口**，画它的时候
        /// `GetWindowPos()/GetWindowSize()` 就是它的矩形，直接读，不用猜。
        /// 试过"把所有绘制命令的裁剪矩形并起来"，结果并出个整屏（日志实测
        /// `实际画到=(0,0 1920x1080)`），整个界面都点不动了。
        /// </summary>
        internal Rect PopupRect { get; private set; }

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || overlay.Timeline == null)
            {
                Rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            string windowName;
            if (_settingsPane)
            {
                // 左栏没停在设置页就整个不画（Rect 清零，挡点击那层也跟着收）。
                if (!overlay.IsSettingsPane)
                {
                    Rect = new Rect(0f, 0f, 0f, 0f);
                    PopupRect = new Rect(0f, 0f, 0f, 0f);
                    return;
                }

                // **设置页不跟时间轴选中**：它显示的是谱面设置，用户在时间轴上
                // 点什么是另一回事。不挡的话点一个事件块就会把左边的设置顶掉
                // ——uGUI 那版用 followTimelineSelection = false 表达同一件事。
                LevelEvent page = overlay.SelectedSettingsEvent;
                if (!ReferenceEquals(page, _target))
                {
                    _editRow = null;
                    _colorRow = null;
                    _target = page;
                    // **告诉 uGUI 那份参数栏"现在这一页是设置页"。**
                    //
                    // 行是这里画的，但设置页特有的副作用（路径/格位重算、
                    // 背景重铺）是由 EditorPropertyPanel 靠 settingsPage 这个标记
                    // 认人的 —— 不打的话改设置会"值变了、画面不跟"。
                    overlay.MarkSettingsTarget(page);
                }
                overlay.GetSettingsRect(out x, out y, out width, out height);
                windowName = "##aes_settings";
                _title = overlay.SelectedSettingsTitle;
            }
            else
            {
                Poll(overlay);
                overlay.GetInspectorRect(out x, out y, out width, out height);
                windowName = "##aes_inspector";
                _title = "参数";
            }

            Rect = new Rect(x, y, width, height);
            PopupRect = new Rect(0f, 0f, 0f, 0f);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            // `Begin` 返回 false 时**仍然必须调 `End`**，所以这里不能提前 return。
            if (ImGui.Begin(windowName, PanelFlags))
            {
                DrawBody();
            }
            ImGui.End();
        }

        /// <summary>
        /// 看选中变了没有。
        ///
        /// 选集一律以**时间轴那份**为准（`PanelSelection`），和 uGUI 那边同一处 ——
        /// 它和删除/复制用的是同一条判断，不会出现"时间轴看着选中了、参数栏却是空的"。
        /// </summary>
        private void Poll(EditorStudioOverlay overlay)
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll)
            {
                return;
            }
            _nextPoll = now + PollInterval;

            List<LevelEvent> selection = overlay.Timeline.PanelSelectionWithDecorations();
            EditorPropertyPanel panel = Panel;

            // **两个及以上 = 批量模式。**
            //
            // 状态喂给 uGUI 那份参数栏 —— 写入路径在它那儿：`Commit` 看到
            // `batchEvents` 会自动转 `CommitBatch`，于是**一次改值落到整批、
            // 只记一步撤销**。这里只负责把"现在是一批"告诉它，并拿回两个键集。
            // 行仍由这一侧画（见 CollectVisible 的过滤和两个 Draw*Rows 里的
            // 「多个值」），键集规则两边共用同一套，不会走散。
            bool batch = panel != null && selection != null && selection.Count > 1
                && panel.EnterBatch(selection);
            if (panel != null && !batch)
            {
                // 退到零个／一个：**必须退出批量。**
                //
                // 不清的话，下一次单选改值会看到过期的 `batchEvents`，
                // 把值写给一批早就不是选中的事件 —— 而且是静默的。
                panel.ExitBatch();
            }

            // 画的抓手：批量时用这一批的第一个 —— 它同时就是"改了落到整批"的抓手。
            LevelEvent single = selection != null && selection.Count == 1 ? selection[0] : null;
            LevelEvent anchor = batch && panel.BatchEvents != null && panel.BatchEvents.Count > 0
                ? panel.BatchEvents[0]
                : single;

            if (anchor != null)
            {
                LogRowKeysOnce(anchor);
            }
            if (!ReferenceEquals(anchor, _target))
            {
                // 换了目标就别让上一行的半截输入跟过来。
                _editRow = null;
                _colorRow = null;
                _target = anchor;
            }
        }

        /// <summary>
        /// 收集这一帧要画的属性。
        ///
        /// **只有这一处有过滤规则**：两条排法（宽 / 窄）都从这里取，
        /// 所以加规则时不会只加到其中一条上。
        /// </summary>
        private List<KeyValuePair<string, ADOFAI.PropertyInfo>> CollectVisible()
        {
            _visible.Clear();
            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in _target.info.propertiesInfo)
            {
                ADOFAI.PropertyInfo info = pair.Value;
                if (info == null || string.IsNullOrEmpty(info.name))
                {
                    continue;
                }
                // **`ToggleGroup` 这一类原版不显示。**
                //
                // 实测 MoveCamera 的 `dontDisable` / `minVfxOnly` 两项，
                // 它们和别的属性**唯一的差别**就是 `controlType = ToggleGroup`
                // （别的都是 InputField / Dropdown / NotAssigned），
                // 而原版面板里这两行确实不存在（截图核对过：10 个键只显示 8 行）。
                //
                // 这两条证据都指向同一个判据，所以按它过滤 ——
                // 不是给这两个名字写死例外。
                if (info.invisible || info.controlType == ControlType.Hidden
                    || info.controlType == ControlType.ToggleGroup)
                {
                    continue;
                }
                // **批量只列"每个选中事件都有"的属性。**
                //
                // 别人没有的键上去就是坑：控件的抓手是第一个选中事件，
                // 改一下会写到一个别的成员根本不认识的地方去。
                // 键集由 EditorPropertyPanel 那边算（见 BatchHasKey），
                // 和 uGUI 版的 ShowBatch 同一套规则。
                if (InBatchMode && !BatchHasKey(pair.Key))
                {
                    continue;
                }
                _visible.Add(pair);
            }
            return _visible;
        }

        /// <summary>
        /// 现在是不是批量模式。**设置页永远不是** —— 那一页的选中来源不同，
        /// 走的是 `SelectedSettingsEvent` 那条路，不经过 `Poll` 的批量入口。
        /// </summary>
        private bool InBatchMode
        {
            get
            {
                if (_settingsPane)
                {
                    return false;
                }
                EditorPropertyPanel panel = Panel;
                return panel != null && panel.InBatch;
            }
        }

        /// <summary>这个键是不是每个选中事件都有。</summary>
        private bool BatchHasKey(string key)
        {
            EditorPropertyPanel panel = Panel;
            return panel != null && panel.BatchHasKey(key);
        }

        /// <summary>这个键在这一批里取值是不是全等。取不到面板时按"全等"算。</summary>
        private bool BatchIsUniform(string key)
        {
            EditorPropertyPanel panel = Panel;
            return panel == null || panel.BatchIsUniform(key);
        }

        /// <summary>批量下这一行取值不一致（要挂「多个值」，控件也要让窄）。</summary>
        private bool IsMixedKey(string key)
        {
            return InBatchMode && !BatchIsUniform(key);
        }

        /// <summary>
        /// 批量模式下"这一批取值不一致"的标记。
        ///
        /// 挂在控件的右边，**调用方要先把这块宽度从控件的可用宽度里扣掉** ——
        /// 不扣的话输入框会顶到标签底下。用弱化色：它是说明，不是内容。
        /// </summary>
        private static void DrawMixedChip(float rightEdge, float top)
        {
            const string text = "多个值";
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            NVec2 size = ImGui.CalcTextSize(text);
            draw.AddText(new NVec2(rightEdge - size.X, top),
                ImGui.GetColorU32(ToVec4(Palette.TextMuted)), text);
        }

        private void DrawBody()
        {
            ImGui.SeparatorText(_title);

            if (_target == null || _target.info == null || _target.info.propertiesInfo == null)
            {
                PushText(ImGuiCol.Text, Palette.TextMuted);
                // 两种情况话不一样：右边是"没选中事件"，左边设置页是"谱面还没读进来"。
                ImGui.TextUnformatted(_settingsPane ? "这一页暂时读不到" : "没有选中事件");
                ImGui.PopStyleColor();
                return;
            }

            // 事件级的生效/停用开关。属性级那排勾选管的是单条属性，
            // 这个是"整个事件生不生效"。
            DrawEventActiveToggle();

            // 顶上的装饰物图。**只有装饰物事件才有**，别的事件上边一个字都不占。
            DrawDecorationPreview();

            // 列宽先量出来 —— 窄/宽两种排法的判定和排版都用同一个数，
            // 免得"判定按 104、排版按 130"这种两边打架。
            List<KeyValuePair<string, ADOFAI.PropertyInfo>> rows = CollectVisible();
            float available = ImGui.GetContentRegionAvail().X;
            float labelNeed = MeasureLongestLabel(rows);
            float labelColumn = Mathf.Min(labelNeed + LabelGap, available * 0.5f);

            // 这一份里**最占地方的那个值**要多宽（下拉按最长的一项算）。
            float valueNeed = MinimumValueWidth;
            for (int i = 0; i < rows.Count; i++)
            {
                valueNeed = Mathf.Max(valueNeed, MeasureValueNeed(rows[i].Value));
            }

            // **两种"放不下"都换排法，而不是硬挤或裁掉。**
            //
            //   1. 标签放不下（最长标签 + 缝超过了给它的地方）
            //   2. 值放不下（三列排完，值那一栏不够宽 —— 含下拉里最长的那一项）
            //
            // 换成"标签独占一行"那种排法之后，两样都放得下 —— 这就是"自适应"。
            // 只按"值放不下"判过一次，结果长标签被裁成半截；
            // 值那一侧只按"当前显示的那个值"算过一次，结果下拉里的长选项被截。
            if (labelNeed + LabelGap > available * 0.5f
                || available - EnableColumnWidth - LabelLeftGap - labelColumn < valueNeed)
            {
                DrawStackedRows(rows);
            }
            else
            {
                DrawWideRows(rows, labelColumn);
            }
        }

        /// <summary>
        /// 标签列该多宽 —— 按**最长的那个标签**量出来。
        ///
        /// 不写死：标签是游戏给的本地化文字，长度随语言变。照抄过 uGUI 的
        /// `LabelWidth = 104`，但那只配得上它那边的字号，这边中文字更大，
        /// "色彩动画间隔"直接撞到右边的控件上。上限取可用宽度的一半，
        /// 免得长标签把控件挤没（撞上上限时才需要裁切）。
        /// </summary>
        /// <summary>打过键名的事件类型（每种只打一次，免得刷屏）。</summary>
        private static readonly HashSet<string> LoggedEventTypes = new HashSet<string>();

        /// <summary>
        /// 把这一种事件的**属性键名**打一次日志。
        ///
        /// 用来回答"面板上这两行是什么、对应原版哪个字段"这类问题 ——
        /// 界面显示的是本地化之后的中文标签，而键名才是它在游戏里的身份。
        /// 每种事件类型只打一次。
        /// </summary>
        private static void LogRowKeysOnce(LevelEvent levelEvent)
        {
            if (levelEvent.info == null || string.IsNullOrEmpty(levelEvent.info.name))
            {
                return;
            }
            if (!LoggedEventTypes.Add(levelEvent.info.name))
            {
                return;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in levelEvent.info.propertiesInfo)
            {
                ADOFAI.PropertyInfo info = pair.Value;
                if (info == null || string.IsNullOrEmpty(info.name)) continue;
                if (info.invisible || info.controlType == ControlType.Hidden) continue;
                if (builder.Length > 0) builder.Append(" | ");
                // 旗标也打出来 —— 原版隐藏了 MoveCamera 的 dontDisable / minVfxOnly
                // 两项，而我们的过滤（invisible / controlType）没挡住，
                // 所以要看它们身上到底哪个旗标和别的属性不一样。
                builder.Append(info.name)
                    .Append("(type=").Append(info.type)
                    .Append(",ctl=").Append(info.controlType)
                    .Append(",inv=").Append(info.invisible ? 1 : 0)
                    .Append(",canDis=").Append(info.canBeDisabled ? 1 : 0)
                    .Append(")");
            }
            Main.Logger.Log("[ImGui] " + levelEvent.info.name + " 的参数键名："
                + builder.ToString());
        }

        /// <summary>
        /// 一行"值"至少要多宽 —— 取它**可能显示的文字里最宽的那个**。
        ///
        /// **下拉不能只看它当前显示的那个值。** 下拉里最长的一项放不下，就会被
        /// 截成 "Out Cu"（用户看到的那个），而且展开之后那些长选项在弹层里也一样
        /// 被截。缓速那族里有 `INTERNAL_XXX` 这种长名字，所以要按最长的一项算。
        /// </summary>
        private float MeasureValueNeed(ADOFAI.PropertyInfo info)
        {
            if (info == null || info.type != ADOFAI.PropertyType.Enum || info.enumType == null)
            {
                return MinimumValueWidth;
            }
            float need = MinimumValueWidth;
            string[] names = System.Enum.GetNames(info.enumType);
            for (int i = 0; i < names.Length; i++)
            {
                float optionWidth = ImGui.CalcTextSize(
                    EditorPropertyPanel.DisplayEnumName(_target, info, names[i])).X;
                need = Mathf.Max(need, optionWidth + EnumChromeWidth);
            }
            return need;
        }

        private float MeasureLongestLabel(List<KeyValuePair<string, ADOFAI.PropertyInfo>> rows)
        {
            float width = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                width = Mathf.Max(width, ImGui.CalcTextSize(
                    EditorPropertyPanel.ResolveLabel(_target, rows[i].Value)).X);
            }
            return width;
        }

        /// <summary>宽面板：三列（启用 / 参数 / 值），行行对齐。</summary>
        /// <summary>
        /// 事件级的「生效 / 停用」开关。**图标按钮，两个状态。**
        ///
        /// 画法和装饰物列表行尾那三个按钮是同一套（见
        /// `EditorImGuiIcons.DrawIcon`）：睁眼 = 生效，闭眼 = 停用，点一下翻过来。
        ///
        /// 原本画成一个勾选框，在参数栏里太抢眼（那一排卡片才是主体），
        /// 而且和每行左边那排属性级的勾选长得一样、容易混。换成图标按钮之后
        /// 和列表那边连成一套语言，也不跟卡片抢视线。
        ///
        /// **语义上和装饰物列表那个眼睛不是一回事**：那边是 `visible`（写进谱面，
        /// 管的是画不画），这边是 `active`（管的是求不求值）。工具提示里写明。
        /// </summary>
        private void DrawEventActiveToggle()
        {
            if (_settingsPane || _target == null)
            {
                return;
            }

            bool active = _target.active;

            EditorImGuiIcons.Icon eyeIcon;
            bool hasEye = EditorImGuiIcons.TryGetEye(active, out eyeIcon);

            // **文字在前，图标在后。** 图标本身只表达"哪一态"，说不清这一项是什么；
            // 写成"生效 / 不生效"才一眼看懂。（装饰物列表那三个按钮是反过来的 ——
            // 那边是行操作，图标就是全部信息。）
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + CardPad);
            ImGui.AlignTextToFramePadding();
            PushText(ImGuiCol.Text, active ? Palette.TextSecondary : Palette.TextMuted);
            ImGui.TextUnformatted(active ? "生效" : "不生效");
            ImGui.PopStyleColor();

            // **按钮底色压成透明。**
            //
            // 默认样式那块亮底在这块深色面板上太跳 —— 它是"这个事件生不生效"，
            // 不该比下面那一排参数卡片还抢眼（用户原话："看着太突出了"）。
            // 留一个悬停/按下时的淡淡底色就够了：既说明"这里能点"，
            // 平时又不占视觉重量。图标和文字本身照旧。
            ImGui.SameLine(0f, LabelLeftGap);
            ImGui.PushStyleColor(ImGuiCol.Button, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToVec4(Palette.ButtonHover));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ToVec4(Palette.Selected));
            bool toggled = ImGui.Button("##event_active",
                new NVec2(EventActiveButtonSize, EventActiveButtonSize));
            ImGui.PopStyleColor(3);
            if (toggled)
            {
                EditorPropertyPanel panel = Panel;
                if (panel != null)
                {
                    panel.SetEventActive(_target, !active);
                }
            }
            EditorImGuiIcons.DrawIcon(ImGui.GetWindowDrawList(),
                ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), EventActiveButtonSize * IconInButton,
                hasEye, eyeIcon,
                active ? EditorImGuiIcons.Drawn.EyeOpen : EditorImGuiIcons.Drawn.EyeClosed,
                active ? "生" : "停",
                active ? Palette.TextPrimary : Palette.TextMuted);

            if (ImGui.IsItemHovered())
            {
                string scope = InBatchMode ? "这一批事件" : "这个事件";
                ImGui.SetTooltip(
                    (active ? scope + "正在生效 —— 点一下停用" : scope + "已停用 —— 点一下启用")
                    // 批量下图标只能画一个状态（第一个选中事件的），
                    // 不一致就说明白，免得那个"睁眼"看起来像"整批都生效"。
                    + (InBatchMode && !BatchActiveUniform() ? "\n（选中的事件里取值不一致，图标按第一个画）" : string.Empty)
                    + "\n和每行左边那排勾选不是一回事：那些只管单条属性。");
            }

            // **和下面第一张卡片拉开。**
            //
            // 卡片的下缘是量出来的，可它的**上缘是 `光标 - CardPad`**（见
            // DrawWideRows / DrawStackedRows）—— 所以就留一个间距的话，
            // 卡片会往上吃掉这整段，看起来是贴着的。多留一个 CardPad 才真的是间距。
            ImGui.Dummy(new NVec2(0f, CardPad + EventActiveGap));
        }

        /// <summary>这一批的 `active` 是不是全等。</summary>
        private bool BatchActiveUniform()
        {
            EditorPropertyPanel panel = Panel;
            if (panel == null || panel.BatchEvents == null || panel.BatchEvents.Count == 0)
            {
                return true;
            }
            bool first = panel.BatchEvents[0] != null && panel.BatchEvents[0].active;
            for (int i = 1; i < panel.BatchEvents.Count; i++)
            {
                LevelEvent other = panel.BatchEvents[i];
                if (other != null && other.active != first)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 顶上的装饰物图片预览。
        ///
        /// **只有装饰物事件才画。** 给每个 MoveTrack / MoveCamera 事件都留一块空框，
        /// 只会白占面板顶部 —— uGUI 那扇也是这个判断（结论同样是踩出来的：
        /// "顶上总空着一块、不知道干嘛用"）。所以不是装饰物时这里一个字都不占。
        ///
        /// 图片按纹理宽高比**贴合、居中**，不拉伸填满 —— 拉伸会把图压扁，
        /// 而"这块图长什么样"正是这一块存在的唯一理由。
        /// </summary>
        private void DrawDecorationPreview()
        {
            // 设置页不做预览（它显示的是谱面设置，不是某个装饰物）。
            if (_settingsPane)
            {
                return;
            }

            EditorPropertyPanel panel = Panel;
            if (panel == null)
            {
                return;
            }

            Sprite sprite;
            string filename;
            if (!panel.TryGetDecorationPreview(_target, out sprite, out filename))
            {
                return;
            }

            float slotWidth = ImGui.GetContentRegionAvail().X;
            if (slotWidth <= 2f)
            {
                return;
            }

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            NVec2 slot = ImGui.GetCursorScreenPos();
            draw.AddRectFilled(slot, new NVec2(slot.X + slotWidth, slot.Y + PreviewHeight),
                ImGui.GetColorU32(ToVec4(Palette.Raised)), EditorStudioOverlay.CornerSmall);

            // 和 uGUI 那扇同一套排法：说明文字占底部那一条，
            // 图片框吃掉剩下的部分。
            const float captionHeight = 22f;
            const float captionGap = 2f;
            float boxWidth = Mathf.Max(1f, slotWidth - 2f);
            float boxHeight = Mathf.Max(1f, PreviewHeight - captionHeight - captionGap - 4f);
            float boxX = slot.X + 1f;
            float boxY = slot.Y + 2f;

            EditorImGuiIcons.Icon icon;
            if (sprite != null && EditorImGuiIcons.TryFromSprite(sprite, out icon))
            {
                float width = boxWidth;
                float height = boxHeight;
                float aspect = icon.Aspect > 0.0001f ? icon.Aspect : 1f;
                if (aspect > boxWidth / boxHeight)
                {
                    height = boxWidth / aspect;
                }
                else
                {
                    width = boxHeight * aspect;
                }

                NVec2 imageMin = new NVec2(boxX + (boxWidth - width) * 0.5f,
                    boxY + (boxHeight - height) * 0.5f);
                draw.AddImage(icon.TextureId, imageMin,
                    new NVec2(imageMin.X + width, imageMin.Y + height),
                    icon.UvMin, icon.UvMax, ImGui.GetColorU32(new NVec4(1f, 1f, 1f, 1f)));
            }
            else
            {
                // 装饰物但没图（或者图还没加载出来）：画个凹槽占位。
                // 空着的话分不清"没图"和"这里本来就不该有东西"。
                draw.AddRectFilled(new NVec2(boxX, boxY),
                    new NVec2(boxX + boxWidth, boxY + boxHeight),
                    ImGui.GetColorU32(ToVec4(Palette.FieldWell)), 2f);
            }

            // 说明文字用文件名 —— 图一样但名字不同时，这是唯一的区分点。
            // 裁到框里：文件名可以很长，溢出去会盖到面板外面。
            float captionY = slot.Y + PreviewHeight - captionHeight - captionGap + 4f;
            draw.PushClipRect(new NVec2(boxX, captionY),
                new NVec2(boxX + boxWidth, captionY + captionHeight), true);
            draw.AddText(new NVec2(slot.X + 6f, captionY),
                ImGui.GetColorU32(ToVec4(Palette.TextMuted)),
                string.IsNullOrEmpty(filename) ? "<无图片>" : filename);
            draw.PopClipRect();

            // **必须真的把高度占掉。** 只画不占的话，下面第一行卡片会压在图上
            // —— ImGui 的布局是按光标走的，画进 draw list 的东西不参与排版。
            // 间距里还要多留一个 CardPad：卡片的**上缘是 `光标 - CardPad`**
            // （见 DrawWideRows / DrawStackedRows），不然图会被它吃掉一条边。
            ImGui.SetCursorScreenPos(slot);
            ImGui.Dummy(new NVec2(slotWidth, PreviewHeight + CardPad + CardGap));
        }

        /// <summary>
        /// 宽面板：三列（启用 / 参数 / 值），每行一张圆角卡片。
        ///
        /// **这里故意不用 ImGui 的表格。** 表格自己要用"通道"画行背景，
        /// 我们在行内再插一层通道会把它的状态搅坏 —— 上一版就是这么把标签列和
        /// 启用列整个弄没的（屏幕上只剩一列值）。三列手动排反而更直白。
        ///
        /// 纵向交给 ImGui 的流（行高、行距、内容高度都由它自己算），
        /// 只在每个控件前用 `SameLine` 推横向位置。
        /// </summary>
        private void DrawWideRows(List<KeyValuePair<string, ADOFAI.PropertyInfo>> rows,
            float labelColumn)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            // 行距只在画卡片这段时间改，别影响面板其它地方。
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
                new NVec2(ImGui.GetStyle().ItemSpacing.X, RowSpacing));
            for (int i = 0; i < rows.Count; i++)
            {
                ADOFAI.PropertyInfo info = rows[i].Value;
                ImGui.PushID(rows[i].Key);

                // 卡片 = 整个内容区；**行内容在卡片里左右各让出 CardPad**。
                // 左右都要让：只让左边的话，输入框会顶出卡片右边。
                float contentLeft = ImGui.GetCursorPosX();
                float cardWidth = ImGui.GetContentRegionAvail().X;
                float left = contentLeft + CardPad;
                float rowRight = contentLeft + cardWidth - CardPad;
                NVec2 cardMin = ImGui.GetCursorScreenPos();
                cardMin.Y -= CardPad;

                // 底衬要画在内容**下面**，可它得等内容画完才量得出高度 ——
                // 所以借通道：内容进上层，量完再回下层补底色，最后合并。
                draw.ChannelsSplit(2);
                draw.ChannelsSetCurrent(1);

                // **先把光标摆到卡片的内边距里，再画勾选框。**
                //
                // 这里漏过一次 `SetCursorPosX(left)`：`left` 算出来了却没用，
                // 勾选框还画在卡片的左边缘上 —— 表现是"宽的时候勾选框贴边、
                // 最窄的时候反而没事"（最窄走的是窄排法，那边这句有）。
                ImGui.SetCursorPosX(left);
                if (info.canBeDisabled)
                {
                    DrawEnableToggle(info);
                    ImGui.SameLine(left + EnableColumnWidth + LabelLeftGap);
                }
                else
                {
                    ImGui.SetCursorPosX(left + EnableColumnWidth + LabelLeftGap);
                }

                // 标签裁到列内：量出来的列一般刚好放得下，撞上上限那一档才需要裁。
                //
                // **竖向给足余量。** 这个裁剪框本来只为了"别越到值那一列"，
                // 竖向却按整整一行文字高卡的 —— 而文字实际画出来的高
                // （Ascent + Descent）比 `GetTextLineHeight()` 略大，
                // 带勾选的那几行还会因为勾选框是带框控件而整体下移一点，
                // 于是字底被切掉一块（"到""转""放"这些带底横的字最明显）。
                // 上下各放宽一截就够罩住，又不会糊到上下相邻的卡片上
                // （卡间距是 32）。
                NVec2 labelMin = ImGui.GetCursorScreenPos();
                draw.PushClipRect(
                    new NVec2(labelMin.X, labelMin.Y - 8f),
                    new NVec2(labelMin.X + labelColumn,
                              labelMin.Y + ImGui.GetTextLineHeight() + 12f),
                    true);
                DrawLabel(info);
                draw.PopClipRect();

                // 批量下这一批取值不一致：控件让出右边那块给「多个值」标签，
                // 值本身照常显示第一个选中事件的 —— 它是给作者改的抓手，
                // 改一下那一下就会落到整批上（见 EditorPropertyPanel.CommitBatch）。
                bool mixed = IsMixedKey(rows[i].Key);
                ImGui.SameLine(left + EnableColumnWidth + LabelLeftGap + labelColumn);
                DrawValue(info, mixed ? rowRight - MixedChipWidth - LabelLeftGap : rowRight);
                if (mixed)
                {
                    DrawMixedChip(rowRight, labelMin.Y);
                }

                // 卡片下缘取**这一行最高的那个控件**，所以高矮不一的行
                // （比如窄排法那种两行式的）不用单独算高度。
                // **卡片高度按"标准内容高"取齐，不按这一行实际内容量。**
                //
                // 量出来的话，值是勾选框那种矮内容的行卡片就矮一截 ——
                // 于是行距忽大忽小（用户一眼就看出来了）。取齐之后每张卡一样高，
                // 缝自然也就一样。
                float contentTop = cardMin.Y + CardPad;
                float contentHeight = Mathf.Max(
                    ImGui.GetItemRectMax().Y - contentTop, ImGui.GetFrameHeight());
                NVec2 cardMax = new NVec2(
                    cardMin.X + cardWidth, contentTop + contentHeight + CardPad);

                draw.ChannelsSetCurrent(0);
                draw.AddRectFilled(cardMin, cardMax,
                    ImGui.GetColorU32(ToVec4(
                        ImGui.IsMouseHoveringRect(cardMin, cardMax)
                            ? Palette.ButtonHover
                            : Palette.Raised)),
                    EditorStudioOverlay.CornerSmall);
                draw.ChannelsMerge();

                ImGui.PopID();
            }
            ImGui.PopStyleVar();
        }

        /// <summary>
        /// 窄面板：**标签一行、控件下一行**。
        ///
        /// 三列排法在窄面板里会把输入框挤成一条缝 —— 看得见框、看不见内容，
        /// 点也点不着。与其让它"自适应"到不可用，不如直接换一种排法。
        /// uGUI 那扇在行窄时也有一条同样的判断（`MinimumTwoAcrossWidth`）。
        /// </summary>
        private void DrawStackedRows(List<KeyValuePair<string, ADOFAI.PropertyInfo>> rows)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            // 卡间空隙和宽排法一样用 RowSpacing 撑开 —— 上一版这里只放了个
            // 3 像素的 Dummy，卡片几乎贴在一起（用户看到的就是"输入框贴到上面
            // 那张卡片上了"）。两种排法必须共用同一套间距数。
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
                new NVec2(ImGui.GetStyle().ItemSpacing.X, RowSpacing));
            for (int i = 0; i < rows.Count; i++)
            {
                ADOFAI.PropertyInfo info = rows[i].Value;
                ImGui.PushID(rows[i].Key);

                // 窄排法一行本来就占两行，**卡片高度量出来、不定死**。
                NVec2 cardMin = ImGui.GetCursorScreenPos();
                cardMin.Y -= CardPad;
                float cardLeft = ImGui.GetCursorPosX();
                float cardWidth = ImGui.GetContentRegionAvail().X;
                float blockRight = cardLeft + cardWidth - CardPad;
                draw.ChannelsSplit(2);
                draw.ChannelsSetCurrent(1);

                // **卡片的左内边距也要管住勾选框和标签。**
                //
                // 上一版只给"值"那一栏做了缩进，勾选框和标签还画在卡片的左边缘上 ——
                // 用户看到的就是"又贴边了"。预览工具当时没抓到，因为**那边我照抄时
                // 顺手加了缩进**，两边不一致（镜像和本体不一致，镜子就白照了）。
                ImGui.SetCursorPosX(cardLeft + CardPad);
                DrawEnableToggle(info);
                if (info.canBeDisabled)
                {
                    // 勾选框和文字之间留一道缝，不然贴在一起。
                    ImGui.SameLine(0f, LabelLeftGap);
                }
                DrawLabel(info);

                // **控件和标签用同一个左间距。**
                //
                // 上一版把控件又缩进了"启用"那一列（22 + 6），结果一张卡里
                // 两行的左间距不一样（标签 27px、控件 55px），看着就是没对齐、
                // 像贴边。那一列缩进是宽排法为了对齐值那一栏才需要的，
                // 窄排法里控件独占一行、不需要。
                // 批量下取值不一致时同样让出右边那块给「多个值」——
                // 标签挂在这一行右上角，控件在下面那一行，两者不会撞。
                bool mixed = IsMixedKey(rows[i].Key);
                ImGui.SetCursorPosX(cardLeft + CardPad);
                DrawValue(info, mixed ? blockRight - MixedChipWidth - LabelLeftGap : blockRight);
                if (mixed)
                {
                    DrawMixedChip(blockRight, cardMin.Y + CardPad);
                }

                // 下缘也要 CardPad（原来是 FramePadding ≈ 6，比别的边窄）。
                NVec2 cardMax = new NVec2(cardMin.X + cardWidth,
                    ImGui.GetItemRectMax().Y + CardPad);
                draw.ChannelsSetCurrent(0);
                draw.AddRectFilled(cardMin, cardMax,
                    ImGui.GetColorU32(ToVec4(
                        ImGui.IsMouseHoveringRect(cardMin, cardMax)
                            ? Palette.ButtonHover
                            : Palette.Raised)),
                    EditorStudioOverlay.CornerSmall);
                draw.ChannelsMerge();

                ImGui.PopID();
            }
            ImGui.PopStyleVar();
        }

        /// <summary>
        /// 左边那枚「启用」勾选（只有 `canBeDisabled` 的属性有）。
        ///
        /// 它是**反的**：勾上＝这个属性生效，取消勾选＝写进 `disabled` 字典把它禁掉。
        /// 这条语义在 `EditorPropertyPanel.SetDisabled` 里，这里只负责按一下。
        /// </summary>
        private void DrawEnableToggle(ADOFAI.PropertyInfo info)
        {
            if (!info.canBeDisabled)
            {
                return;
            }

            bool enabled = !EditorPropertyPanel.IsDisabled(_target, info.name);
            if (ImGui.Checkbox("##enable", ref enabled))
            {
                EditorPropertyPanel panel = Panel;
                if (panel != null)
                {
                    panel.SetDisabled(_target, info.name, !enabled);
                }
            }
        }

        private void DrawLabel(ADOFAI.PropertyInfo info)
        {
            bool disabled = EditorPropertyPanel.IsDisabled(_target, info.name);
            PushText(ImGuiCol.Text, disabled ? Palette.TextMuted : Palette.TextSecondary);
            ImGui.TextUnformatted(EditorPropertyPanel.ResolveLabel(_target, info));
            ImGui.PopStyleColor();
        }

        private EditorPropertyPanel Panel
        {
            get
            {
                EditorStudioOverlay overlay = Main.Overlay;
                return overlay == null ? null : overlay.PropertyPanel;
            }
        }

        private void Commit(string key, object value)
        {
            EditorPropertyPanel panel = Panel;
            if (panel != null && _target != null)
            {
                panel.Commit(_target, key, value);
            }
        }

        /// <summary>
        /// 按属性类型画控件。宽度一律用"剩下的地方"，单位（如果有）从里面扣出来。
        ///
        /// 'floor' 是唯一不在事件 data 字典里的属性，它挂在 LevelEvent 字段上，
        /// 通用 Get 什么都读不到 —— uGUI 那边也是单独一条分支。
        /// </summary>
        private void DrawValue(ADOFAI.PropertyInfo info, float rightEdge)
        {
            // 单位宽度按**实际文字量**算，别写死一个数：写死过 26px，而 "beats"
            // 要四十多像素，屏幕上就被裁成 "be"。
            string unit = ResolveUnit(info);
            float unitWidth = 0f;
            if (!string.IsNullOrEmpty(unit))
            {
                // **要把输入框和单位之间那道间距也算进来。** 只减文字宽度的话，
                // 输入框 + 默认间距(8) + 单位 会顶出面板右边，单位被裁掉半截。
                unitWidth = ImGui.CalcTextSize(unit).X + UnitGap;
            }

            // **宽度从"卡片的右缘"量，不是从窗口内容区的右缘量。**
            //
            // 上一版用的是 `GetContentRegionAvail()` —— 那是**窗口内容区**的右缘，
            // 而卡片比它窄（左右各留了 `CardPad`），所以输入框会顶出卡片右边
            // （用户看到的就是"有时候还是贴卡片边"）。这里改成由调用方把
            // 卡片的右边界传进来，自适应就落在卡片里面。
            // **不给"最小宽度"兜底。**
            //
            // 这里以前是 `Mathf.Max(60f, …)` —— 可用宽度不够时它强行给 60 像素，
            // 输入框于是顶出卡片（用户看到的就是"有时候贴边"：面板窄的时候、
            // 拖动时间轴时宽度在变的那一瞬间）。**保障宽度够用是"换排法"的职责**
            // （见 DrawBody 的两条判据），不是给宽度兜一个下限的职责。
            float width = Mathf.Max(1f,
                rightEdge - ImGui.GetCursorPosX() - unitWidth);
            string rowId = info.name;

            if (info.type == ADOFAI.PropertyType.Int && info.name == "floor")
            {
                TextField(rowId, _target.floor.ToString(CultureInfo.InvariantCulture), width,
                    delegate(string text)
                    {
                        int parsed;
                        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            Commit(info.name, parsed);
                        }
                    });
            }
            else
            {
                switch (info.type)
                {
                    case ADOFAI.PropertyType.Bool:
                    {
                        bool value = EditorPropertyPanel.SafeGet(_target, info, false);
                        if (ImGui.Checkbox("##value", ref value))
                        {
                            Commit(info.name, value);
                        }
                        break;
                    }
                    case ADOFAI.PropertyType.Int:
                        TextField(rowId, EditorPropertyPanel.FormatInt(
                            EditorPropertyPanel.SafeGet(_target, info, 0)), width,
                            delegate(string text)
                            {
                                int parsed;
                                if (int.TryParse(text, NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out parsed))
                                {
                                    Commit(info.name, parsed);
                                }
                            });
                        break;
                    case ADOFAI.PropertyType.Float:
                        TextField(rowId, EditorPropertyPanel.FormatFloat(
                            EditorPropertyPanel.SafeGet(_target, info, 0f)), width,
                            delegate(string text)
                            {
                                float parsed;
                                if (float.TryParse(text, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out parsed))
                                {
                                    Commit(info.name, parsed);
                                }
                            });
                        break;
                    case ADOFAI.PropertyType.Vector2:
                        DrawVector2(info, width);
                        break;
                    case ADOFAI.PropertyType.String:
                    case ADOFAI.PropertyType.LongString:
                    case ADOFAI.PropertyType.File:
                        TextField(rowId, EditorPropertyPanel.SafeGet(_target, info, string.Empty), width,
                            delegate(string text) { Commit(info.name, text); });
                        break;
                    case ADOFAI.PropertyType.Enum:
                        DrawEnum(info, width);
                        break;
                    case ADOFAI.PropertyType.Color:
                        DrawColor(info, rowId, width);
                        break;
                    default:
                        ImGui.TextUnformatted("<暂不支持 " + info.type + ">");
                        break;
                }
            }

            if (unitWidth > 0f)
            {
                ImGui.SameLine(0f, UnitGap);
                PushText(ImGuiCol.Text, Palette.TextMuted);
                ImGui.TextUnformatted(unit);
                ImGui.PopStyleColor();
            }
        }

        /// <summary>
        /// 一个「编辑完才提交」的文本框。
        ///
        /// uGUI 那边是 `onEndEdit` —— **回车、或点到别处，才写进谱面**。
        /// 边打字边写会把一堆中间态写进去：打 "12" 会先写 1 再写 12，
        /// 撤销栈里凭空多一条。
        ///
        /// ImGui 的输入框每次按键都把文本还给我们，所以缓冲**必须跨帧留住** ——
        /// 每帧按当前值重置缓冲，会把用户正在打的字冲掉。用一个"哪一行正在编辑"
        /// 的标记决定什么时候重置。
        ///
        /// 另外 `IsItemDeactivatedAfterEdit` 只在**真的改过**的时候为真，
        /// 所以"点进去又原样点出来"不算一次修改 —— 这一条 uGUI 那边专门修过，
        /// 语义保持一致。
        /// </summary>
        private void TextField(string rowId, string initial, float width, System.Action<string> commit)
        {
            if (_editRow != rowId)
            {
                _editBuffer = initial;
            }

            ImGui.SetNextItemWidth(width);
            // **标签必须每一行、每一个框都不同。**
            //
            // ImGui 给控件分配 ID 就是按标签算的，而同一行里 Vector2 有两个框：
            // 都叫 "##text" 就会撞 ID —— ImGui 当场弹一条红字报错，而且两个框
            // 会互相抢输入焦点。行里已经有 `PushID(属性名)` 兜住"不同行"，
            // 但"同一行的两个框"得靠标签自己区分，所以把 rowId 并进标签里。
            ImGui.InputText("##" + rowId, ref _editBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue);

            if (ImGui.IsItemActive())
            {
                // 正在打字：记住是哪一行在用这个缓冲，别让下面几行把它重置掉。
                _editRow = rowId;
                return;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _editRow = null;
                commit(_editBuffer);
            }
        }

        /// <summary>
        /// 两个分量：地方够就并排，不够就上下叠。
        ///
        /// 叠的时候不用 `SameLine` 就行 —— `TextField` 画完光标本来就在下一行。
        /// </summary>
        private void DrawVector2(ADOFAI.PropertyInfo info, float width)
        {
            Vector2 value = EditorPropertyPanel.SafeGet(_target, info, Vector2.zero);
            float half = (width - PairGap) * 0.5f;
            bool sideBySide = half >= MinimumPairFieldWidth;

            TextField(info.name + ".x", EditorPropertyPanel.FormatFloat(value.x),
                sideBySide ? half : width,
                delegate(string text)
                {
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Vector2 current = EditorPropertyPanel.SafeGet(_target, info, Vector2.zero);
                        Commit(info.name, new Vector2(parsed, current.y));
                    }
                });

            if (sideBySide)
            {
                ImGui.SameLine(0f, PairGap);
            }

            TextField(info.name + ".y", EditorPropertyPanel.FormatFloat(value.y),
                sideBySide ? half : width,
                delegate(string text)
                {
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Vector2 current = EditorPropertyPanel.SafeGet(_target, info, Vector2.zero);
                        Commit(info.name, new Vector2(current.x, parsed));
                    }
                });
        }

        /// <summary>
        /// 枚举：一个下拉，选项是 `info.enumType` 的全部成员。
        /// 显示的是**本地化之后**的名字（和 uGUI 那行下拉同一个来源），
        /// 写回去的是枚举值本身。
        /// </summary>
        private void DrawEnum(ADOFAI.PropertyInfo info, float width)
        {
            string current = EditorPropertyPanel.ReadEnumName(_target, info);
            string display = EditorPropertyPanel.DisplayEnumName(_target, info, current);

            // **宽度不用扣箭头 —— 箭头是算在这个宽度里面的。**
            //
            // 这里白减过一次：我从渲染图上看，以为"箭头是加在框外面的"，于是
            // 减掉 39 像素。后来把实际几何打出来才看清 ——
            // `缓速 值=166..283`（117 宽，正好等于我设的 156-39），
            // 也就是**箭头本来就在这段宽度里**，那 39 像素一直空着，下拉被挤窄、
            // 文字被截成 "Out Cu"。
            //
            // 箭头那块的底色压成和输入框一样（它默认用 `ImGuiCol.Button`，
            // 那是"按钮"的颜色，和输入框放一起会读成两块）。
            ImGuiStylePtr style = ImGui.GetStyle();
            ImGui.PushStyleColor(ImGuiCol.Button,
                style.Colors[(int)ImGuiCol.FrameBg]);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToVec4(Palette.ButtonHover));
            ImGui.SetNextItemWidth(Mathf.Max(1f, width));
            if (ImGui.BeginCombo("##enum", display))
            {
                NotePopupRect();
                if (info.enumType != null)
                {
                    string[] names = System.Enum.GetNames(info.enumType);
                    bool easeEnum = EditorPropertyPanel.IsEaseEnum(info);
                    for (int i = 0; i < names.Length; i++)
                    {
                        bool selected = names[i] == current;
                        string optionLabel = EditorPropertyPanel.DisplayEnumName(
                            _target, info, names[i]);

                        DG.Tweening.Ease optionEase;
                        if (easeEnum && EditorPropertyPanel.TryParseEase(names[i], out optionEase))
                        {
                            DrawEaseOption(optionLabel, selected, optionEase,
                                delegate { Commit(info.name,
                                    EditorPropertyPanel.ParseEnumValue(info, names[i])); });
                        }
                        else if (ImGui.Selectable(optionLabel, selected))
                        {
                            Commit(info.name, EditorPropertyPanel.ParseEnumValue(info, names[i]));
                        }

                        if (selected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// 单位那一小段文字（"beats" / "tiles" / "seconds" / "%"）。
        ///
        /// 游戏自己的属性面板里它是**中文**的（拍子 / 方块 / 秒），键名是
        /// `editor.unit.<单位>` —— 这一条是**实测出来的**，不是猜的：日志里
        /// `editor.unit.tilesFrom = 方块来自`、`editor.unit.beats = 拍子`
        /// 一条条都在。而且它对组合单位也通用。
        ///
        /// 问不到就退回原文（英文），并且**报一次警告** —— 那说明游戏加了新单位、
        /// 而本地化表里没有，属于该看一眼的事。
        /// </summary>
        private static string ResolveUnit(ADOFAI.PropertyInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.unit))
            {
                return string.Empty;
            }

            bool exists;
            string localized = RDString.GetWithCheck("editor.unit." + info.unit, out exists);
            if (exists && !string.IsNullOrEmpty(localized))
            {
                return localized;
            }

            // 本地化表里没有：退回原文，但别让它悄悄过去。
            if (ReportedUnits.Add(info.unit))
            {
                Main.Logger.Warning("[ImGui] 单位本地化表里没有 editor.unit." + info.unit
                    + "，先用原文顶上");
            }
            return info.unit;
        }

        /// <summary>每个单位最多报一次，免得刷屏。</summary>
        private static readonly HashSet<string> ReportedUnits = new HashSet<string>();
        // 曲线图在选项里的位置 —— 和 uGUI 那扇用的是同一组数
        // （那边叫 OptionGraphX / OptionGraphWidth / EaseGraphHeight）。
        private const float EaseCurveLeft = 20f;
        private const float EaseCurveWidth = 44f;

        /// <summary>
        /// 缓速下拉里的一个选项：左边一条缩略曲线，右边文字。
        ///
        /// 有曲线时 `Selectable` 只用来占位（标签用 `"##opt"`，不显示），
        /// 曲线和文字都自己画 —— 这样曲线不会压在文字上，文字也不用靠空格挪位置。
        ///
        /// 采样直接复用参数栏那边的 `SampleEaseCurve` —— 它和预览求值器是同一套
        /// 数学（`PreviewEase.EasedValue`），所以图上什么形状、画面里就是什么形状。
        /// </summary>
        private static void DrawEaseOption(string label, bool selected,
            DG.Tweening.Ease ease, System.Action commit)
        {
            // **先把曲线算出来，再碰 ImGui。** 采不到就只画文字（见 TrySampleEaseCurve）。
            Vector2[] sampled = TrySampleEaseCurve(ease, EaseCurveWidth,
                EditorPropertyPanel.EaseGraphHeight);
            float labelOffset = sampled == null
                ? 8f
                : EaseCurveLeft + EaseCurveWidth + 8f;

            float rowHeight = Mathf.Max(ImGui.GetFrameHeight(),
                EditorPropertyPanel.EaseGraphHeight + 6f);

            // **标签要带上选项名。** 一次展开有十几个选项，全叫 "##opt" 就是
            // 十几个同一个 ID —— ImGui 当场弹红框，而且它们会互相抢选中状态。
            // （同一个坑第三次了：循环里画控件，每次的标签必须不同。）
            if (ImGui.Selectable("##opt" + label, selected, ImGuiSelectableFlags.None,
                new NVec2(0f, rowHeight)))
            {
                commit();
            }

            NVec2 min = ImGui.GetItemRectMin();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();

            if (sampled != null)
            {
                NVec2[] points = new NVec2[sampled.Length];
                float curveTop = min.Y + (rowHeight - EditorPropertyPanel.EaseGraphHeight) * 0.5f;
                for (int i = 0; i < sampled.Length; i++)
                {
                    // 采样出来的 y 是"向上为正"（uGUI 那套坐标），ImGui 是向下为正，翻一下。
                    points[i] = new NVec2(
                        min.X + EaseCurveLeft + sampled[i].x,
                        curveTop + EditorPropertyPanel.EaseGraphHeight - sampled[i].y);
                }

                // 原版把 y 按整条曲线的最大值等比缩放，负值部分会落到框外 ——
                // 那是原版就有的行为，但这里得裁一下，不然会画到名字上。
                draw.PushClipRect(
                    new NVec2(min.X, min.Y),
                    new NVec2(min.X + EaseCurveLeft + EaseCurveWidth, min.Y + rowHeight), true);
                draw.AddPolyline(ref points[0], points.Length,
                    ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), ImDrawFlags.None, 1f);
                draw.PopClipRect();
            }

            draw.AddText(
                new NVec2(min.X + labelOffset,
                    min.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f),
                ImGui.GetColorU32(ToVec4(Palette.TextPrimary)), label);
        }

        /// <summary>采不出来的缓速只报一次。</summary>
        private static readonly HashSet<string> ReportedEases = new HashSet<string>();

        /// <summary>
        /// 采一条缓速曲线；**采不出来返回 null，不往外抛**。
        ///
        /// DOTween 对某些枚举值（`Unset`、`INTERNAL_*` 那一族）会直接空引用，
        /// 而参数栏的下拉选项里就有这些值。在 uGUI 那边它只是少画一条线
        /// （所以一直没人发现）；在 ImGui 这边**帧中间抛一次就完蛋** ——
        /// 这一帧 Render 不掉，下一帧 NewFrame 弹原生断言框，每帧弹一次。
        ///
        /// 这里的关键是**只把纯计算包起来**：这一段里不碰任何 ImGui 调用，
        /// 所以异常不会留在 ImGui 的栈上（`Begin`/`End` 之间抛异常是修不回来的）。
        /// </summary>
        private static Vector2[] TrySampleEaseCurve(DG.Tweening.Ease ease, float width, float height)
        {
            try
            {
                return EditorPropertyPanel.SampleEaseCurve(
                    ease, EditorPropertyPanel.EaseGraphPoints, width, height);
            }
            catch (Exception exception)
            {
                if (ReportedEases.Add(ease.ToString()))
                {
                    Main.Logger.Warning("[ImGui] 缓速曲线采不出来（" + ease
                        + "）—— DOTween 求不了这个值，这一项不画曲线："
                        + exception.GetType().Name);
                }
                return null;
            }
        }

        /// <summary>
        /// 颜色：色块点开一个调色盘。
        ///
        /// **提交时机：松手（或输入框改完）才写进谱面。**
        ///
        /// uGUI 那扇的做法不完全一样：它**拖动期间是实时写进谱面的**（这样画面
        /// 跟着动），只是**不记历史**，松手时补记**一条**撤销，覆盖从打开到现在的
        /// 变化 —— 每帧记一条的话，拖一次鼠标就是上百条，Ctrl+Z 要点到手酸。
        ///
        /// 这里用的是简单的那一版：拖动中不写，松手写一次。**差别只有一个：
        /// 拖动时背后的画面不跟着变**（调色盘自己当然是在变的）。要那口实时反馈，
        /// 得把 uGUI 那套"无历史的实时写 + 收尾补记一条"整套搬过来，那是另一段。
        ///
        /// 缓冲必须跨帧留住（`_colorBuffer`）：每帧从事件重新读值的话，
        /// 拖动时调色盘会每帧弹回原色。
        /// </summary>
        private void DrawColor(ADOFAI.PropertyInfo info, string rowId, float width)
        {
            Color color = EditorPropertyPanel.SafeGetColor(_target, info);
            NVec4 shown = new NVec4(color.r, color.g, color.b, color.a);

            // 色块和右边那串 HEX 得落在同一条线上。
            //
            // 两者都从行顶开始画，但文字的**墨**只占行盒偏下的一段，所以 20 像素的
            // 固定色块看上去比文字高（上一版就是）。把边长取成**文字行高**，
            // 两者的垂直中心就落在同一处了。
            float swatchSize = ImGui.GetTextLineHeight();
            if (ImGui.ColorButton("##swatch", shown,
                // **关掉自带的悬停提示。** 色块默认会在鼠标位置弹一个小框显示
                // hex 和 RGBA —— 那是多余的（旁边就有 HEX），而且它正好盖在
                // 调色盘要出现的地方。
                ImGuiColorEditFlags.NoTooltip
                | ImGuiColorEditFlags.AlphaPreviewHalf,
                new NVec2(swatchSize, swatchSize)))
            {
                _colorRow = rowId;
                _colorBuffer = shown;
                _colorCommitted = shown;
                ImGui.OpenPopup("##colorpicker");
            }
            ImGui.SameLine();
            // **HEX 是输入框，不是只读的一串字。**
            //
            // 调色盘用来"看着调"，HEX 用来"知道确切的值、并且能直接贴一个进去" ——
            // 抄色号（从别的工具、别人的谱、截图里取的色）这是唯一的路。
            // uGUI 那版这里本来就是输入框（`BuildColor` 那个 "Hex" 字段），
            // 搬到 ImGui 时被写成了 `TextUnformatted`，于是只能看不能改。
            //
            // 走 `TextField` 那条路：**回车或点到别处才提交**，而且
            // `IsItemDeactivatedAfterEdit` 只在真改过时为真 —— "点进去又原样
            // 点出来"不会往撤销栈里塞一条。
            float hexWidth = Mathf.Max(72f,
                width - swatchSize - ImGui.GetStyle().ItemSpacing.X);
            TextField(rowId + "#hex", EditorPropertyPanel.ColorToHex(color), hexWidth,
                delegate(string text)
                {
                    Color parsed;
                    if (EditorPropertyPanel.TryParseHex(text, out parsed))
                    {
                        Commit(info.name, EditorPropertyPanel.ColorToHex(parsed));
                    }
                });

            // 只有"开着的那一行"才画弹层 —— 别行不能碰它。
            if (_colorRow != rowId)
            {
                return;
            }

            if (ImGui.BeginPopup("##colorpicker"))
            {
                NotePopupRect();
                ImGui.ColorPicker4("##picker", ref _colorBuffer,
                    ImGuiColorEditFlags.AlphaBar
                    | ImGuiColorEditFlags.AlphaPreviewHalf
                    | ImGuiColorEditFlags.DisplayHex
                    | ImGuiColorEditFlags.DisplayRGB);

                // **松手才写，而且只在真的变过时才写** —— 一次拖动因此只落一条撤销。
                //
                // 不能用 `IsItemDeactivatedAfterEdit`：`ColorPicker4` 是个复合控件，
                // 它结束之后"最后一项"指的未必是整个调色盘 —— 实测拿不到那个时机
                // （松手没反应就是这个原因）。直接看鼠标状态更可靠。
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    CommitColorIfChanged(info);
                }
                ImGui.EndPopup();
            }
            else
            {
                // 弹层关了（点到外面 / Esc）：收尾一次 —— 覆盖"只在输入框里打字、
                // 全程没碰过鼠标"那种编辑。
                _colorRow = null;
                CommitColorIfChanged(info);
            }
        }

        /// <summary>
        /// 值真的变了才写。不这么挡的话，在调色盘里随便点一下都会往撤销栈里塞一条 ——
        /// 那正是 uGUI 那边专门防的事（"拖一次鼠标就是上百条，Ctrl+Z 要点到手酸"）。
        /// </summary>
        private void CommitColorIfChanged(ADOFAI.PropertyInfo info)
        {
            if (SameColor(_colorBuffer, _colorCommitted))
            {
                return;
            }
            _colorCommitted = _colorBuffer;
            // **提交的是 hex 字符串，不是 `Color` 对象。**
            //
            // 谱面里颜色就是按 hex 字符串存的（`SafeGetColor` 走 `GetColor` 读的
            // 就是它），uGUI 那扇的调色盘写的也是字符串（`Commit(bound, key,
            // ColorToHex(parsed))`）。写 `Color` 对象是 ImGui 这版自己换的写法，
            // 两边不一定认 —— 先和"走得通的那条路"对齐。
            Commit(info.name, EditorPropertyPanel.ColorToHex(
                new Color(_colorBuffer.X, _colorBuffer.Y, _colorBuffer.Z, _colorBuffer.W)));
        }

        private static bool SameColor(NVec4 a, NVec4 b)
        {
            return Mathf.Abs(a.X - b.X) < 0.0005f
                && Mathf.Abs(a.Y - b.Y) < 0.0005f
                && Mathf.Abs(a.Z - b.Z) < 0.0005f
                && Mathf.Abs(a.W - b.W) < 0.0005f;
        }

        private static NVec4 ToVec4(Color color)
        {
            return new NVec4(color.r, color.g, color.b, color.a);
        }

        /// <summary>记下当前弹层窗口的矩形（在弹层的 Begin 之后调用）。</summary>
        private void NotePopupRect()
        {
            NVec2 pos = ImGui.GetWindowPos();
            NVec2 size = ImGui.GetWindowSize();
            PopupRect = new Rect(pos.X, pos.Y, size.X, size.Y);
        }

        private static void PushText(ImGuiCol slot, Color color)
        {
            ImGui.PushStyleColor(slot, new NVec4(color.r, color.g, color.b, color.a));
        }
    }
}
