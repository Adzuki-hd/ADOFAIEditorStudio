using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// ImGui 那两个面板（装饰物列表、媒体页）共用的几个小工具。
    ///
    /// 抽出来是因为都是**几何逻辑**，复制第二份迟早两边不一样 —— 尤其是
    /// "按宽度裁字"那种，一处改了另一处没改，表现出来只是某一边偶尔难看，
    /// 很难注意到。
    ///
    /// 用它的文件顶部加一句 `using static EditorImGuiDraw;` 就能按原样调
    /// `ToVec4(...)`、`FitText(...)`，调用处一行都不用动。
    /// </summary>
    internal static class EditorImGuiDraw
    {
        internal static NVec4 ToVec4(Color color)
        {
            return new NVec4(color.r, color.g, color.b, color.a);
        }

        /// <summary>
        /// 按像素宽度裁文字，放不下就在结尾加省略号。
        ///
        /// 比硬切掉半个字干净：面板窄的时候文件名经常超宽，"STAR_Source.p…"
        /// 一眼知道是截断，而切到一半看着像画错了。用二分找位置，别一个字一个字试。
        /// </summary>
        internal static string FitText(string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
            {
                return text;
            }
            int lo = 0;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (ImGui.CalcTextSize(text.Substring(0, mid) + "…").X <= maxWidth)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return lo > 0 ? text.Substring(0, lo) + "…" : string.Empty;
        }

        /// <summary>
        /// 和上面那个一样，但按**指定字号**量。正文那一档以外的字号必须走这个 ——
        /// `ImGui.CalcTextSize` 只认当前字号，量出来的宽度是错的。
        /// </summary>
        internal static string FitText(ImFontPtr font, float size, string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)
                || font.CalcTextSizeA(size, float.MaxValue, 0f, text).X <= maxWidth)
            {
                return text;
            }
            int lo = 0;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (font.CalcTextSizeA(size, float.MaxValue, 0f, text.Substring(0, mid) + "…").X
                    <= maxWidth)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return lo > 0 ? text.Substring(0, lo) + "…" : string.Empty;
        }

        /// <summary>
        /// 把 <paramref name="aspect"/>（宽/高）的矩形**等比**装进 boxW×boxH 里，
        /// 返回实际该画多大。
        ///
        /// **不能直接铺满方框** —— 非正方的图（垃圾桶、竖构图的截图）会被拉变形，
        /// 看起来"瘦"或者"胖"，像是素材本身画得不好。
        /// </summary>
        internal static void FitBox(float boxW, float boxH, float aspect,
            out float width, out float height)
        {
            if (aspect <= 0f)
            {
                aspect = 1f;
            }
            float heightIfFullWidth = boxW / aspect;
            if (heightIfFullWidth <= boxH)
            {
                width = boxW;
                height = heightIfFullWidth;
            }
            else
            {
                width = boxH * aspect;
                height = boxH;
            }
        }
    }
}
