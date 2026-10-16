using UnityEngine;

namespace ADOFAIEditorStudio.Ui
{
    /// <summary>
    /// 原始色阶。**控件永远不要直接引用这里**，只用 UiTheme 的语义层。
    ///
    /// 分层的意义只有一个：换主题时不用改控件代码。控件读
    /// `UiTheme.Current.text.primary`，主题负责把它映射到某个原生色阶值；
    /// 加一套主题 = 新增一个 UiTheme 实例，控件那一行照旧。
    ///
    /// 取值来自 Tailwind 色系（slate / sky / rose / amber / emerald）——
    /// 它本身就是按 OKLCH 感知均匀性设计的成熟色阶，比手工调更靠得住，
    /// 也省得为「11 级里第 7 级该多亮」这种问题重新发明一遍。
    /// </summary>
    internal static class UiPrimitives
    {
        /// <summary>中性色。深色 UI 的骨架全在这里。</summary>
        internal static class Slate
        {
            internal static readonly Color C50 = Hex("f8fafc");
            internal static readonly Color C100 = Hex("f1f5f9");
            internal static readonly Color C200 = Hex("e2e8f0");
            internal static readonly Color C300 = Hex("cbd5e1");
            internal static readonly Color C400 = Hex("94a3b8");
            internal static readonly Color C500 = Hex("64748b");
            internal static readonly Color C600 = Hex("475569");
            internal static readonly Color C700 = Hex("334155");
            internal static readonly Color C800 = Hex("1e293b");
            internal static readonly Color C900 = Hex("0f172a");
            internal static readonly Color C950 = Hex("020617");
        }

        /// <summary>主色。雾蓝这一套全系统只有这一个彩色。</summary>
        internal static class Sky
        {
            internal static readonly Color C200 = Hex("bae6fd");
            internal static readonly Color C300 = Hex("7dd3fc");
            internal static readonly Color C400 = Hex("38bdf8");
            internal static readonly Color C500 = Hex("0ea5e9");
            internal static readonly Color C600 = Hex("0284c7");
            internal static readonly Color C700 = Hex("0369a1");
        }

        /// <summary>危险 / 错误。</summary>
        internal static class Rose
        {
            internal static readonly Color C300 = Hex("fda4af");
            internal static readonly Color C400 = Hex("fb7185");
            internal static readonly Color C500 = Hex("f43f5e");
            internal static readonly Color C600 = Hex("e11d48");
        }

        /// <summary>警告。</summary>
        internal static class Amber
        {
            internal static readonly Color C300 = Hex("fcd34d");
            internal static readonly Color C400 = Hex("fbbf24");
            internal static readonly Color C500 = Hex("f59e0b");
        }

        /// <summary>成功。</summary>
        internal static class Emerald
        {
            internal static readonly Color C300 = Hex("6ee7b7");
            internal static readonly Color C400 = Hex("34d399");
            internal static readonly Color C500 = Hex("10b981");
        }

        /// <summary>
        /// 十二位十六进制（RRGGBB 或 RRGGBBAA）转 Color。
        /// 项目里没有资源文件，所有颜色只能这样写死在代码里，
        /// 所以至少让写法统一、可读。
        /// </summary>
        internal static Color Hex(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return Color.magenta;
            }

            // 解析失败时返回洋红而不是黑色：黑色在深色底上和背景分不清，
            // 洋红一眼就能看出是这里出的问题。
            int r = HexByte(hex, 0);
            int g = HexByte(hex, 2);
            int b = HexByte(hex, 4);
            int a = hex.Length >= 8 ? HexByte(hex, 6) : 255;
            if (r < 0 || g < 0 || b < 0 || a < 0)
            {
                return Color.magenta;
            }

            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        private static int HexByte(string hex, int offset)
        {
            if (hex.Length < offset + 2)
            {
                return -1;
            }

            int high = HexDigit(hex[offset]);
            int low = HexDigit(hex[offset + 1]);
            return high < 0 || low < 0 ? -1 : high * 16 + low;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    /// <summary>
    /// 间距刻度。基准 4px —— 界面对齐靠的是统一刻度，不是逐处微调。
    ///
    /// 原来项目里的值（6 / 10 / 26 / 27 / 54 / 58 / 18）有一半不是 4 的倍数，
    /// 结果是同类元素在不同面板里各差一两个像素，看着"哪里不对"却说不上来。
    /// 归到 4 的倍数后这种漂移就绝迹了。
    /// </summary>
    internal static class UiSpacing
    {
        /// <summary>4</summary>
        internal const float Xs = 4f;
        /// <summary>8</summary>
        internal const float Sm = 8f;
        /// <summary>12</summary>
        internal const float Md = 12f;
        /// <summary>16</summary>
        internal const float Lg = 16f;
        /// <summary>24</summary>
        internal const float Xl = 24f;
        /// <summary>32</summary>
        internal const float Xxl = 32f;
        /// <summary>48</summary>
        internal const float Xxxl = 48f;

        /// <summary>发丝描边宽度。不做缩放，1px 就是 1px。</summary>
        internal const float Hairline = 1f;
    }

    /// <summary>
    /// 字号刻度，Major Third（1.25）梯度。
    ///
    /// 原来有 10 档（9 / 9.5 / 10 / 10.5 / 11 / 12 / 13 / 15 / 17 / 20），
    /// 大多数差值在半像素到一像素之间 —— 这种"几乎一样但不一样"是
    /// 界面显得不专业的典型来源。收敛到 6 档后每档都能一眼分辨。
    /// </summary>
    internal static class UiType
    {
        /// <summary>11 — 最小号：刻度、角标</summary>
        internal const float Caption = 11f;
        /// <summary>13 — 字段值、次要信息</summary>
        internal const float Body = 13f;
        /// <summary>15 — 正文、按钮</summary>
        internal const float BodyLg = 15f;
        /// <summary>17 — 面板标题</summary>
        internal const float Title = 17f;
        /// <summary>20 — 顶栏标题</summary>
        internal const float TitleLg = 20f;
        /// <summary>24 — 仅用于加载页那种独占整屏的场合</summary>
        internal const float Display = 24f;
    }

    /// <summary>
    /// 动效时长与缓动。UI 目前零动画，这些值是第二步加过渡时的基准。
    /// </summary>
    internal static class UiMotion
    {
        /// <summary>0.12s — 按钮反馈、悬停变色这种即时响应</summary>
        internal const float Fast = 0.12f;
        /// <summary>0.20s — 弹窗出现、面板展开</summary>
        internal const float Base = 0.20f;
        /// <summary>0.32s — 大块面板的开合</summary>
        internal const float Slow = 0.32f;

        /// <summary>按下的缩放比例。回弹到 1 时用。</summary>
        internal const float PressScale = 0.96f;
    }
}
