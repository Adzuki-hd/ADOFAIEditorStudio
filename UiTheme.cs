using UnityEngine;

namespace ADOFAIEditorStudio.Ui
{
    /// <summary>
    /// 一套主题的全部语义取值。控件只读 <see cref="Current"/>，绝不直接引用 UiPrimitives。
    ///
    /// 加一套新主题 = 再写一个 UiTheme 实例 + 调一次 <see cref="Select"/>，
    /// **控件代码一行都不用改**。这是整个 token 分层存在的唯一理由。
    /// </summary>
    internal sealed class UiTheme
    {
        internal string Id;
        internal string DisplayName;

        // ── 表面 ──────────────────────────────────────────────────────────
        // 从最底到最上是 page → panel → raised → overlay，四档足够拉开层次；
        // well 是唯一往下沉的一档（输入框凹槽），比 page 还深。
        internal Color SurfacePage;
        internal Color SurfacePanel;
        internal Color SurfaceRaised;
        /// <summary>悬停态的凸起面。比 SurfaceRaised 再亮一档。</summary>
        internal Color SurfaceRaisedHover;
        internal Color SurfaceOverlay;
        internal Color SurfaceWell;
        /// <summary>比 Raised 更高一档，给凸起卡片内部的内容用（缩略图、色块）。</summary>
        internal Color SurfaceThumb;
        /// <summary>
        /// 面板标题条的实心底衬。比面板亮一档，四条横带才立得起来。
        /// </summary>
        internal Color SurfaceBand;
        /// <summary>
        /// 顶栏带（标题 / 菜单 / 标签条那三行）的底色。
        ///
        /// 全局背景已改纯黑，顶栏再纯黑就和内容糊成一片，所以这里单独留一档
        /// 极浅的灰（原来的 #0B0B0B）。**无边框、无圆角**，只是一块满宽矩形。
        /// </summary>
        internal Color SurfaceTopBand;



        // ── 文字 ──────────────────────────────────────────────────────────
        // 三级足够：正文 / 次要 / 弱化。等级再多就分不出主次了。
        internal Color TextPrimary;
        internal Color TextSecondary;
        internal Color TextMuted;
        internal Color TextDisabled;
        /// <summary>
        /// 强调文字：把关键词从正文里拎出来（快捷键的键名、条目的功能名、小节标题）。
        ///
        /// 它**比主色暗一档**。主色是给"能点的东西"用的，文字上再用一次整屏就吵了 ——
        /// 而且强调色是撒在正文里的，用量比按钮大得多。取的是恰好还过得了对比度的
        /// 那一档（见 Slate 里的取值说明），再暗就不该用来印正文了。
        /// </summary>
        internal Color TextEmphasis;
        /// <summary>压在亮色填充上的文字（比如主按钮）。</summary>
        internal Color TextOnAccent;

        // ── 边框 ──────────────────────────────────────────────────────────
        /// <summary>发丝描边。装饰用，不承担「必须看得见」的职责，对比度可以很低。</summary>
        internal Color BorderHairline;
        /// <summary>需要被辨识的控件边框（复选框勾选态、输入框聚焦态）。对比度需 ≥ 3:1。</summary>
        internal Color BorderStrong;
        /// <summary>聚焦环。</summary>
        internal Color BorderFocus;

        // ── 操作 ──────────────────────────────────────────────────────────
        internal Color ActionPrimary;
        internal Color ActionPrimaryHover;
        internal Color ActionPrimaryPressed;

        /// <summary>
        /// 第二强调色。这一套里它和主色**是同一个值**。
        ///
        /// 存在的理由只是让"标题/读数"和"按钮/选中"这两类位置各有一个名字，
        /// 将来想在某一类上分开调色时不必再去改控件。雾蓝这一版刻意只留
        /// 一个彩色——全屏只有一种蓝，界面才不会花。
        /// </summary>
        internal Color AccentSecondary;

        // ── 选中 ──────────────────────────────────────────────────────────
        internal Color SelectedFill;
        internal Color SelectedHoverFill;

        // ── 反馈 ──────────────────────────────────────────────────────────
        internal Color FeedbackDanger;
        internal Color FeedbackWarning;
        internal Color FeedbackSuccess;

        // ── 阴影 ──────────────────────────────────────────────────────────
        internal Color Shadow;

        /// <summary>
        /// 雾蓝 Slate —— 当前唯一主题。
        ///
        /// 整套只有**一个彩色**：天蓝。它同时出现在能点的地方（按钮、播放头、
        /// 选中块、聚焦环）和要读的地方（面板标题、时间读数、计数）——
        /// 这一版不靠颜色区分这两类，靠的是字号和位置。全屏只有一种蓝，
        /// 界面因此是安静的。
        ///
        /// 对比度（WCAG 2.2，相对 surface.page #0A0C0F）：
        ///   text.primary   #E2E8F0 → 高，远超 4.5:1
        ///   text.secondary #94A3B8 → 约 7:1
        ///   text.muted     #64748B → 约 4.2:1（仅用于大字号或非关键信息）
        ///   border.strong  #64748B → 约 4.2:1，满足控件边框的 3:1
        /// 注意 border.hairline 是纯装饰（白 8%），不用于需要辨识的控件轮廓。
        /// </summary>
        /// <summary>完全透明。用于「这一层不再自己画底色」的表面。</summary>
        private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);

        internal static readonly UiTheme Slate = new UiTheme
        {
            Id = "slate",
            DisplayName = "雾蓝",

            // 全灰阶配色（2026-10-22）：只用黑/灰/白，不带任何色相。
            // 明度阶梯仍然拉开，保证文字与控件的可读性；原来那套雾蓝/天蓝
            // 语义色全部收敛成中性灰阶。
            // 明度阶梯。相邻两档的差距要够大，否则面板和背景糊成一片，
            // 实测梯度：page 最暗 → panel 约两倍 → raised 再上一档 → thumb 最亮。
            // 纯黑底：原来 #0B0B0B 在暗处仍能看出一点点灰，观感“不是纯黑”。
            SurfacePage = UiPrimitives.Hex("000000"),
            // ── 纯平背景（2026-10-21）──────────────────────────────────────
            // 只保留**一层**总背景：SurfacePage。其余所有"面上再叠面"的填充
            // 一律透明 —— 面板底、标题条、输入凹槽、缩略图底都不再自己画底色，
            // 露出来的就是那一层背景。
            //
            // 控件本身不在此列：SurfaceRaised / RaisedHover 仍然有填充，
            // 否则按钮、卡片会和背景糊在一起。这一版只清背景，不动控件。
            // **uGUI 迁移后必须把面画回来。**
            //
            // 「纯平背景」那一版的前提是：所有面板底色都由 ImGui 的
            // DrawModuleBlocks 统一铺。面板改回 uGUI 之后没有那一层了，
            // 于是整块工作台只剩文字浮在游戏画面上 —— 表现就是
            // 「界面像是空的 / 下拉菜单点了没反应」（其实浮层在，只是全透明）。
            // 与 ImGui 那套一致：面板/顶栏就是模块底色 #0B0B0B，
            // 页面纯黑，浮层只比面板亮一档。uGUI 要还原的是**同一套灰阶**，
            // 不是另起一套蓝灰。
            SurfacePanel = UiPrimitives.Hex("0B0B0B"),
            // 控件也去底：按钮、开关、图标一律"纯图标"，不再带圆角底色。
            // 唯一保留的是**悬停/按下**的一层极淡白（8% / 14%），否则鼠标移上去
            // 完全没有反馈，看不出哪里能点。
            SurfaceRaised = Transparent,
            SurfaceRaisedHover = new Color(1f, 1f, 1f, 0.08f),
            SurfaceOverlay = UiPrimitives.Hex("131313"),
            SurfaceWell = Transparent,
            SurfaceThumb = Transparent,
            // 对 SurfacePanel(#1A1F27) 的明度比约 1.26:1。
            // 标题条只有 28px 高、又贴着面板边缘，再低就看不出是条横带了。
            SurfaceBand = Transparent,
            SurfaceTopBand = UiPrimitives.Hex("0B0B0B"),

            TextPrimary = UiPrimitives.Hex("F2F2F2"),
            TextSecondary = UiPrimitives.Hex("B8B8B8"),
            TextMuted = UiPrimitives.Hex("8A8A8A"),
            TextDisabled = UiPrimitives.Hex("5C5C5C"),
            // 强调文字：天蓝的中间那一档（#0EA5E9）。
            // 对 panel #1A1F27 的对比度约 6.0:1，是**还过得了正文 4.5:1 的、最不刺眼
            // 的一档** —— 再暗一档（C600）只有约 4.1:1，达不到正文要求；
            // 再用主色那一档（C400）就比按钮还亮了，撒在正文里太吵。
            TextEmphasis = UiPrimitives.Hex("FFFFFF"),
            TextOnAccent = UiPrimitives.Hex("0B0B0B"),

            BorderHairline = new Color(1f, 1f, 1f, 0.08f),
            BorderStrong = UiPrimitives.Hex("8A8A8A"),
            BorderFocus = UiPrimitives.Hex("FFFFFF"),

            ActionPrimary = UiPrimitives.Hex("E8E8E8"),
            ActionPrimaryHover = UiPrimitives.Hex("FFFFFF"),
            ActionPrimaryPressed = UiPrimitives.Hex("C4C4C4"),

            AccentSecondary = UiPrimitives.Hex("E8E8E8"),

            // 选中态用主色的半透明叠加，而不是另配一个颜色——
            // 这样换主题时选中态自动跟着主色走，不会漏改。
            // 选中态：中性白叠加，不再带色相。
            SelectedFill = new Color(1f, 1f, 1f, 0.14f),
            SelectedHoverFill = new Color(1f, 1f, 1f, 0.22f),

            FeedbackDanger = UiPrimitives.Hex("E8E8E8"),
            FeedbackWarning = UiPrimitives.Hex("C4C4C4"),
            FeedbackSuccess = UiPrimitives.Hex("FFFFFF"),

            // 纯平背景：不留投影（面板本身也透明了，阴影只会变成一块孤立深色）。
            Shadow = Transparent
        };

        private static UiTheme current;

        /// <summary>当前主题。没显式选过就是雾蓝。</summary>
        internal static UiTheme Current
        {
            get { return current ?? (current = Slate); }
        }

        /// <summary>
        /// 切换主题。目前只有一套，所以没有调用方——它在这里是为了让
        /// 「以后加主题」变成一行调用，而不是一次重构。
        /// </summary>
        internal static void Select(UiTheme theme)
        {
            if (theme != null)
            {
                current = theme;
            }
        }
    }
}
