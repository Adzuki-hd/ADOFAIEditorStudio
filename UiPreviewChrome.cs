using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 预览模块的「页面外壳」——**按 ImGui 版重写的一份新实现**。
    ///
    /// 为什么不复用 `EditorStudioOverlay.BuildPreview` 里那几行：
    /// 那是 ImGui 改造之前的旧 uGUI 骨架，结构已经和现在的真值不一致
    /// （车道按钮早就搬去时间轴第一行、状态读数搬进了标题行、
    /// 工具栏底衬与标题下分隔线都已取消）。直接打开旧代码只会把过时的
    /// 结构一起带回来 —— 这一轮反复踩的就是这个坑。
    ///
    /// 本文件只负责「工具栏那一行里有什么」，窗口本体（视口、四条遮沿）
    /// 仍由 overlay 负责，那是"窗"，不是"外壳"。
    /// </summary>
    internal static class UiPreviewChrome
    {
        /// <summary>
        /// 标题左侧的强调竖条尺寸与位置 —— **和左右两栏的标题逐像素对齐**：
        /// 面板标题的基准是「左边距 16 + 竖条 3 + 间距 8」= 文字 27，
        /// 竖条在标题行（28 高）里垂直居中。
        /// </summary>
        private const float BarWidth = 3f;
        private const float BarHeight = 16f;
        private const float BarX = 16f;
        private const float TextX = 27f;
        private const float StatusOffsetX = 118f;
        /// <summary>标题行高度：和左右栏的标题条一致（不是整个工具栏的高度）。</summary>
        private const float HeadingHeight = 28f;

        internal sealed class Parts
        {
            internal RectTransform Title;
            internal TextMeshProUGUI Status;
            internal string StatusCache;
        }

        /// <summary>在给定的工具栏矩形里建出竖条、标题与状态读数。</summary>
        internal static Parts Build(RectTransform toolbar, TMP_FontAsset font,
            float width, float height)
        {
            Parts parts = new Parts();
            if (toolbar == null)
            {
                return parts;
            }

            // 竖条：强调色，和左右两栏的标题语言一致；垂直居中在**标题行**里
            // （不是整个工具栏），否则它会比左边那两个面板的竖条低一截。
            Image bar = NewImage("HeadingBar", toolbar, BarX,
                (HeadingHeight - BarHeight) * 0.5f, BarWidth, BarHeight);
            bar.color = UiTheme.Current.AccentSecondary;
            bar.raycastTarget = false;

            TextMeshProUGUI title = NewText("Heading", toolbar, font,
                TextX, 0f, 80f, HeadingHeight, "预览", 15f);
            // 白色：它和「装饰物 / 参数」一样是面板标题，不再是可点的链接。
            title.color = UiTheme.Current.TextPrimary;
            title.alignment = TextAlignmentOptions.MidlineLeft;
            parts.Title = title.rectTransform;

            parts.Status = NewText("Status", toolbar, font,
                StatusOffsetX, 0f, Mathf.Max(80f, width - StatusOffsetX - 24f), HeadingHeight,
                string.Empty, 12.5f);
            parts.Status.color = UiTheme.Current.ActionPrimary;
            parts.Status.alignment = TextAlignmentOptions.MidlineLeft;
            parts.Status.overflowMode = TextOverflowModes.Ellipsis;
            parts.StatusCache = null;
            return parts;
        }

        /// <summary>状态文本变了才写（每帧调用也安全）。</summary>
        internal static void Refresh(Parts parts, string status)
        {
            if (parts == null || parts.Status == null)
            {
                return;
            }
            string text = status ?? string.Empty;
            if (string.Equals(text, parts.StatusCache, System.StringComparison.Ordinal))
            {
                return;
            }
            parts.StatusCache = text;
            parts.Status.text = text;
        }

        private static Image NewImage(string name, RectTransform parent,
            float x, float y, float width, float height)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return go.AddComponent<Image>();
        }

        private static TextMeshProUGUI NewText(string name, RectTransform parent,
            TMP_FontAsset font, float x, float y, float width, float height,
            string value, float size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.text = value;
            text.fontSize = size;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }
    }
}
