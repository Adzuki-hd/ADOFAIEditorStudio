using TMPro;
using UnityEngine;

namespace ADOFAIEditorStudio.Ui
{
    /// <summary>
    /// 给**整段文字**铺一条左→右的颜色渐变（不是每个字各自渐变）。
    ///
    /// 两条实现路径，按色标数量自动选：
    ///
    ///   · **两个色标 → 用 TMP 自带的顶点渐变**（`enableVertexGradient`）。
    ///     它在字形之间**连续插值**，是一条真正平滑的渐变，没有台阶。
    ///
    ///   · **三个及以上 → 逐字符上色**。TMP 的顶点渐变只有四个角
    ///     （左上/右上/左下/右下），只能表达两色；三色硬塞进去，中间那色
    ///     会只出现在一条对角线上。所以退回逐字符：每个字按它在**整段文字**
    ///     里的水平位置取一个纯色，字够多时读起来仍然是连续渐变。
    ///     代价是字与字之间是硬切、没有插值，那是多色方案下唯一的选择。
    /// </summary>
    internal static class UiTextGradient
    {
        /// <summary>
        /// 给 `label` 铺一条横跨**整段文字**的渐变。
        ///
        /// 渐变范围永远是整段文字的左端到右端 —— 不是每字一份。
        /// </summary>
        internal static void Apply(TextMeshProUGUI label, string text, params Color[] stops)
        {
            if (label == null)
            {
                return;
            }
            if (string.IsNullOrEmpty(text) || stops == null || stops.Length == 0)
            {
                label.text = text;
                return;
            }

            if (stops.Length == 1)
            {
                label.enableVertexGradient = false;
                label.color = stops[0];
                label.text = text;
                return;
            }

            if (stops.Length == 2)
            {
                // 两色：顶点渐变，连续插值，最平滑。
                label.richText = true;
                label.enableVertexGradient = true;
                // 四个角：左上 / 右上 / 左下 / 右下。
                // 左列起点色、右列终点色 —— 上下无变化、左右是一条完整渐变，
                // 正是"整段字横向渐变"。
                label.colorGradient = new VertexGradient(
                    stops[0], stops[1], stops[0], stops[1]);
                // **顶点渐变和 `color` 相乘**，所以这里必须是白色，
                // 否则会把渐变整体压暗。
                label.color = Color.white;
                label.text = text;
                return;
            }

            // 三色及以上：逐字符，按字符在整段文字里的位置取色。
            label.enableVertexGradient = false;
            label.color = Color.white;
            label.richText = true;
            label.text = BuildPerCharacter(text, stops);
        }

        /// <summary>
        /// 逐字符上色：`t = 字符位置 / 整段长度`，在色标之间线性插值。
        ///
        /// **分母是整段长度**，所以这是"一组字的渐变"——第一个字拿起点色、
        /// 最后一个字拿终点色，中间按比例过渡。
        /// </summary>
        private static string BuildPerCharacter(string text, Color[] stops)
        {
            int length = text.Length;
            if (length == 0)
            {
                return text;
            }
            System.Text.StringBuilder builder = new System.Text.StringBuilder(length * 24);
            for (int i = 0; i < length; i++)
            {
                // 用字符中心取样，首尾各占半格 —— 第一个字就是起点色、
                // 最后一个字就是终点色，不会被挤掉半档。
                float t = length == 1 ? 0f : (i + 0.5f) / length;
                builder.Append(Wrap(text[i], Sample(stops, t)));
            }
            return builder.ToString();
        }

        private static Color Sample(Color[] stops, float t)
        {
            t = Mathf.Clamp01(t);
            float scaled = t * (stops.Length - 1);
            int index = Mathf.Min(Mathf.FloorToInt(scaled), stops.Length - 2);
            float local = scaled - index;
            return Color.Lerp(stops[index], stops[index + 1], local);
        }

        private static string Wrap(char glyph, Color color)
        {
            // 空格套标签没有意义，还会多出标签的宽度误差。
            if (glyph == ' ')
            {
                return " ";
            }
            Color32 c = color;
            return "<color=#" + c.r.ToString("X2") + c.g.ToString("X2") + c.b.ToString("X2") + ">"
                + glyph + "</color>";
        }
    }
}
