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
        internal static readonly UiTheme Slate = new UiTheme
        {
            Id = "slate",
            DisplayName = "雾蓝",

            // 明度阶梯。相邻两档的差距要够大，否则面板和背景糊成一片，
            // 界面会显得「平」——层次是靠明度差读出来的，不是靠描边。
            // 实测梯度：page 最暗 → panel 约两倍 → raised 再上一档 → thumb 最亮。
            SurfacePage = UiPrimitives.Hex("0A0C0F"),
            SurfacePanel = UiPrimitives.Hex("1A1F27"),
            SurfaceRaised = UiPrimitives.Hex("242A33"),
            SurfaceRaisedHover = UiPrimitives.Hex("2A313B"),
            SurfaceOverlay = UiPrimitives.Hex("2A313B"),
            SurfaceWell = UiPrimitives.Hex("07080B"),
            SurfaceThumb = UiPrimitives.Hex("2E353F"),
            // 对 SurfacePanel(#1A1F27) 的明度比约 1.26:1。
            // 标题条只有 28px 高、又贴着面板边缘，再低就看不出是条横带了。
            SurfaceBand = UiPrimitives.Hex("2A313B"),

            TextPrimary = UiPrimitives.Slate.C200,
            TextSecondary = UiPrimitives.Slate.C400,
            TextMuted = UiPrimitives.Slate.C500,
            TextDisabled = UiPrimitives.Slate.C600,
            // 强调文字：天蓝的中间那一档（#0EA5E9）。
            // 对 panel #1A1F27 的对比度约 6.0:1，是**还过得了正文 4.5:1 的、最不刺眼
            // 的一档** —— 再暗一档（C600）只有约 4.1:1，达不到正文要求；
            // 再用主色那一档（C400）就比按钮还亮了，撒在正文里太吵。
            TextEmphasis = UiPrimitives.Sky.C500,
            TextOnAccent = UiPrimitives.Hex("0F1115"),

            BorderHairline = new Color(1f, 1f, 1f, 0.08f),
            BorderStrong = UiPrimitives.Slate.C500,
            BorderFocus = UiPrimitives.Sky.C400,

            ActionPrimary = UiPrimitives.Sky.C400,
            ActionPrimaryHover = UiPrimitives.Sky.C300,
            ActionPrimaryPressed = UiPrimitives.Sky.C500,

            AccentSecondary = UiPrimitives.Sky.C400,

            // 选中态用主色的半透明叠加，而不是另配一个颜色——
            // 这样换主题时选中态自动跟着主色走，不会漏改。
            SelectedFill = new Color(
                UiPrimitives.Sky.C400.r, UiPrimitives.Sky.C400.g,
                UiPrimitives.Sky.C400.b, 0.15f),
            SelectedHoverFill = new Color(
                UiPrimitives.Sky.C400.r, UiPrimitives.Sky.C400.g,
                UiPrimitives.Sky.C400.b, 0.22f),

            FeedbackDanger = UiPrimitives.Rose.C400,
            FeedbackWarning = UiPrimitives.Amber.C400,
            FeedbackSuccess = UiPrimitives.Emerald.C400,

            Shadow = new Color(0f, 0f, 0f, 0.45f)
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
