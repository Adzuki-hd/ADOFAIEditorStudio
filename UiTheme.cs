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

        // ── 品牌渐变（2026-11-06）──────────────────────────────────────────
        /// <summary>
        /// 顶栏 logo（「ADOFAI EDITOR STUDIO」）的左→右三色渐变。
        /// 用户给的：E60013 → BF442E → 78141B，朱红到暗红。
        /// </summary>
        internal Color[] BrandGradient;

        /// <summary>
        /// 面板标题（预览 / 参数 / 媒体 / 时间轴 …）的颜色。
        /// 用户指定纯色 FB1F07（2026-11-06）。原来是双色渐变，改回单色。
        /// </summary>
        internal Color HeadingColor;

        // ── 阴影 ──────────────────────────────────────────────────────────
        internal Color Shadow;

        /// <summary>完全透明。用于「这一层不再自己画底色」的表面。</summary>
        private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);

        /// <summary>
        /// 黑白 —— **默认主题**（用户 2026-11-06 定的）。
        ///
        /// 就是加暖色之前那一版：纯灰阶，不带任何色相。
        ///
        /// **logo 和面板标题也必须是灰的**：它们是两套主题差别最明显的地方，
        /// 留着红色的话"切到黑白"就只换了个底，看着像没生效。
        /// </summary>
        internal static readonly UiTheme Mono = new UiTheme
        {
            Id = "mono",
            DisplayName = "默认",

            SurfacePage = UiPrimitives.Hex("111111"),
            SurfacePanel = UiPrimitives.Hex("151515"),
            SurfaceRaised = Transparent,
            SurfaceRaisedHover = new Color(1f, 1f, 1f, 0.08f),
            SurfaceOverlay = UiPrimitives.Hex("131313"),
            SurfaceWell = Transparent,
            SurfaceThumb = Transparent,
            SurfaceBand = Transparent,
            SurfaceTopBand = UiPrimitives.Hex("151515"),

            // 加暖色之前那一版：正文近白、标题也近白。
            TextPrimary = UiPrimitives.Hex("F2F2F2"),
            TextSecondary = UiPrimitives.Hex("B8B8B8"),
            TextMuted = UiPrimitives.Hex("8A8A8A"),
            TextDisabled = UiPrimitives.Hex("5C5C5C"),
            TextEmphasis = UiPrimitives.Hex("FFFFFF"),
            TextOnAccent = UiPrimitives.Hex("151515"),

            BorderHairline = new Color(1f, 1f, 1f, 0.08f),
            BorderStrong = UiPrimitives.Hex("8A8A8A"),
            BorderFocus = UiPrimitives.Hex("FFFFFF"),

            ActionPrimary = UiPrimitives.Hex("E8E8E8"),
            ActionPrimaryHover = UiPrimitives.Hex("FFFFFF"),
            ActionPrimaryPressed = UiPrimitives.Hex("C4C4C4"),

            AccentSecondary = UiPrimitives.Hex("E8E8E8"),

            SelectedFill = new Color(1f, 1f, 1f, 0.14f),
            SelectedHoverFill = new Color(1f, 1f, 1f, 0.22f),

            FeedbackDanger = UiPrimitives.Hex("E8E8E8"),
            FeedbackWarning = UiPrimitives.Hex("C4C4C4"),
            FeedbackSuccess = UiPrimitives.Hex("FFFFFF"),

            // 品牌渐变收敛成灰阶：三档白 → 浅灰 → 中灰。
            // 仍然保留**三档**而不是合并成纯白，是为了让渐变那条路
            // 在两套主题下走同一段代码，不用为黑白再分支。
            BrandGradient = new[]
            {
                UiPrimitives.Hex("F2F2F2"),
                UiPrimitives.Hex("C4C4C4"),
                UiPrimitives.Hex("8A8A8A")
            },
            HeadingColor = UiPrimitives.Hex("F2F2F2"),

            Shadow = Transparent
        };

        /// <summary>
        /// 暖色 —— 2026-11-06 那一套：橙红正文、红色标题、朱红 logo 渐变。
        /// 用户在设置里手动切过来才用；默认是 <see cref="Mono"/>。
        /// </summary>
        internal static readonly UiTheme Ember = new UiTheme
        {
            Id = "ember",
            DisplayName = "烈炎",

            SurfacePage = UiPrimitives.Hex("111111"),
            SurfacePanel = UiPrimitives.Hex("151515"),
            SurfaceRaised = Transparent,
            SurfaceRaisedHover = new Color(1f, 1f, 1f, 0.08f),
            SurfaceOverlay = UiPrimitives.Hex("131313"),
            SurfaceWell = Transparent,
            SurfaceThumb = Transparent,
            SurfaceBand = Transparent,
            SurfaceTopBand = UiPrimitives.Hex("151515"),

            TextPrimary = UiPrimitives.Hex("F48862"),
            TextSecondary = UiPrimitives.Hex("B8B8B8"),
            TextMuted = UiPrimitives.Hex("8A8A8A"),
            TextDisabled = UiPrimitives.Hex("5C5C5C"),
            TextEmphasis = UiPrimitives.Hex("FFFFFF"),
            TextOnAccent = UiPrimitives.Hex("151515"),

            BorderHairline = new Color(1f, 1f, 1f, 0.08f),
            BorderStrong = UiPrimitives.Hex("8A8A8A"),
            BorderFocus = UiPrimitives.Hex("FFFFFF"),

            ActionPrimary = UiPrimitives.Hex("E8E8E8"),
            ActionPrimaryHover = UiPrimitives.Hex("FFFFFF"),
            ActionPrimaryPressed = UiPrimitives.Hex("C4C4C4"),

            AccentSecondary = UiPrimitives.Hex("E8E8E8"),

            SelectedFill = new Color(1f, 1f, 1f, 0.14f),
            SelectedHoverFill = new Color(1f, 1f, 1f, 0.22f),

            FeedbackDanger = UiPrimitives.Hex("E8E8E8"),
            FeedbackWarning = UiPrimitives.Hex("C4C4C4"),
            FeedbackSuccess = UiPrimitives.Hex("FFFFFF"),

            BrandGradient = new[]
            {
                UiPrimitives.Hex("E60013"),
                UiPrimitives.Hex("BF442E"),
                UiPrimitives.Hex("78141B")
            },
            HeadingColor = UiPrimitives.Hex("FB1F07"),

            Shadow = Transparent
        };

        /// <summary>按 Id 找主题；找不到返回 null（调用方负责回落到默认）。</summary>
        internal static UiTheme ById(string id)
        {
            if (string.Equals(id, Mono.Id, System.StringComparison.OrdinalIgnoreCase))
            {
                return Mono;
            }
            if (string.Equals(id, Ember.Id, System.StringComparison.OrdinalIgnoreCase))
            {
                return Ember;
            }
            return null;
        }

        private static UiTheme current;

        /// <summary>当前主题。**默认黑白**（用户 2026-11-06 定的）。</summary>
        internal static UiTheme Current
        {
            get { return current ?? (current = Mono); }
        }

        /// <summary>
        /// 切换主题。**调用方必须重建整个工作台** —— 颜色是在建控件那一刻
        /// 读进去的，改 `Current` 不会让已经建好的控件变色。
        /// 见 `EditorStudioOverlay.RebuildWorkbench()`。
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
