using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 时间轴**标题行**（面板最上面那 42 像素）的 ImGui 版。
    ///
    /// 只接管这一条：事件面板从 y=44 起、紧贴它下面，所以下面照旧 —— 车道区、
    /// 事件面板、拖动缩放那些都还是原来的实现。
    ///
    /// **字号统一按正文那一档。** 原来这一行用了三档（标题 / 13 / 11），而 ImGui
    /// 那边的字体图集只按一档光栅化 —— 缩小画是糊的，再加一档要让图集翻倍。
    /// 所以槽位是按这个字号**重排过的**，不是照抄原来那串按小字算出来的 x。
    ///
    /// 五个模式开关的状态、图标、点击全部来自 `CollectHeaderToggles()` ——
    /// 图标本来就是它那边造好的 Sprite，这里只负责画和转发点击。
    /// </summary>
    internal sealed class EditorImGuiTimelineHeader
    {
        private Rect _rect;

        /// <summary>页眉竖条的 x / y，以及它的宽高（和其他面板同一套）。</summary>
        private const float BarX = 18f;
        private const float BarY = 14f;
        /// <summary>竖条之后紧跟标题。</summary>
        private const float TitleGap = 8f;
        /// <summary>模式开关那一排的起点和尺寸。</summary>
        private const float ToggleX = 130f;
        private const float ToggleWidth = 32f;
        private const float ToggleHeight = 29f;
        private const float ToggleGap = 6f;
        /// <summary>开关图标在按钮里的位置和大小 —— 和 uGUI 那边同一组数。</summary>
        private const float IconX = 5f;
        private const float IconY = 6f;
        private const float IconWidth = 18f;
        private const float IconHeight = 16f;
        /// <summary>右侧那一组（从右缘往里量）。</summary>
        private const float RightPadding = 16f;
        private const float StepButtonSize = 28f;
        private const float RewindWidth = 32f;

        /// <summary>
        /// 车道筛选按钮和左右邻居之间的空档。**两侧都要留** —— 左边是时间读数、
        /// 右边是缩放读数，挨上去就会互相压（实测过：读数被按钮盖住、看着断成两截）。
        /// </summary>
        private const float LaneFilterGap = 20f;

        /// <summary>缩放手柄那道握把的宽。和 uGUI 那个 Grip 一致（48）。</summary>
        private const float GripWidth = 48f;

        internal Rect Rect { get { return _rect; } }

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            EditorTimelineView timeline = overlay == null ? null : overlay.TimelineView;
            if (overlay == null || timeline == null)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            overlay.GetTimelineHeaderRect(out x, out y, out width, out height);
            _rect = new Rect(x, y, width, height);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            if (ImGui.Begin("##aes_timeline_header", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawContents(overlay, timeline, x, y, width, height);
            }
            ImGui.End();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        private void DrawContents(EditorStudioOverlay overlay, EditorTimelineView timeline,
            float x, float y, float width, float height)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();

            // 标题条实心底衬。高度取整条标题行 —— 右边还并排放着缩放读数和几个
            // 开关，底衬得把它们一起罩住，才读成"一条横带"而不是"标题后面垫了块布"。
            draw.AddRectFilled(new NVec2(x, y), new NVec2(x + width, y + height),
                ImGui.GetColorU32(ToVec4(Palette.SurfaceBand)),
                EditorStudioOverlay.CornerSmall);

            // **缩放手柄的握把。** uGUI 那个（Grip）就贴在时间轴最上面那几像素，
            // 结果被这一条的底衬盖住了 —— 拖是能拖（挡点击层让开了），但看不出来
            // 那儿有个能拖的东西。所以在这边补画一条。
            draw.AddRectFilled(
                new NVec2(x + width * 0.5f - GripWidth * 0.5f, y + 2f),
                new NVec2(x + width * 0.5f + GripWidth * 0.5f, y + 5f),
                ImGui.GetColorU32(new NVec4(
                    Palette.TextMuted.r, Palette.TextMuted.g, Palette.TextMuted.b, 0.55f)), 1.5f);

            DrawHeading(draw, x, y, height);
            float cursor = DrawToggles(timeline, draw, y, height);
            // 两个功能开关贴在模式开关那排后面，用文字按钮画（它们没有图标，
            // 而且"定位 / 翻页"两个词本身就是最短的说明）。
            cursor = DrawFeatureButtons(timeline, draw, cursor, y, height);

            // **右端那一串的位置在这里算一次。**
            //
            // 从右往左依次是：缩放 +/− 、缩放读数、**车道筛选**。连成一串算，
            // 是为了让它们之间保持固定空档 —— 各自按右边距硬算的话，筛选按钮和
            // 缩放读数会贴到一起（实测过）。
            string zoom = timeline.ZoomReadout;
            float zoomWidth = string.IsNullOrEmpty(zoom) ? 0f : ImGui.CalcTextSize(zoom).X;
            float zoomInX = x + width - RightPadding - StepButtonSize;
            float zoomOutX = zoomInX - 4f - StepButtonSize;
            float rewindX = zoomOutX - 16f - RewindWidth;
            float zoomTextX = rewindX - 12f - zoomWidth;
            float lanesX = zoomTextX - LaneFilterGap - EditorImGuiPreviewPanel.LaneButtonsWidth;

            // 时间读数摆在这一行的**正中间**（见 `DrawReadouts`），所以不用再按
            // 右端那串控件给它算右边界了。
            DrawReadouts(overlay, timeline, draw, x, y, width, height, cursor);
            DrawTransport(overlay, timeline, draw, y, height,
                rewindX, zoomOutX, zoomInX, zoomTextX, zoom, lanesX);
        }

        private static void DrawHeading(ImDrawListPtr draw, float x, float y, float height)
        {
            draw.AddRectFilled(
                new NVec2(x + BarX, y + BarY),
                new NVec2(x + BarX + EditorStudioOverlay.HeadingBarWidth,
                          y + BarY + EditorStudioOverlay.HeadingBarHeight),
                ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), 1f);

            float textX = x + BarX + EditorStudioOverlay.HeadingBarWidth + TitleGap;
            draw.AddText(new NVec2(textX, y + (height - ImGui.GetTextLineHeight()) * 0.5f),
                ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), "时间轴");
        }

        /// <summary>画那五个模式开关，返回它们右端的 x（摘要从这儿往后排）。</summary>
        private float DrawToggles(EditorTimelineView timeline, ImDrawListPtr draw,
            float y, float height)
        {
            // 每次重取：开关的状态和图标都会变（图标是点一下重造一张），
            // 缓存下来就会画成上一次的样子。
            List<EditorTimelineView.HeaderToggle> toggles = timeline.CollectHeaderToggles();

            float buttonY = y + (height - ToggleHeight) * 0.5f;
            for (int i = 0; i < toggles.Count; i++)
            {
                EditorTimelineView.HeaderToggle toggle = toggles[i];
                float buttonX = ToggleX + i * (ToggleWidth + ToggleGap);

                ImGui.PushID(i);
                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));
                if (ImGui.Button("##toggle", new NVec2(ToggleWidth, ToggleHeight)))
                {
                    if (toggle.Flip != null)
                    {
                        toggle.Flip();
                    }
                }
                ImGui.PopID();

                EditorImGuiIcons.Icon icon;
                if (EditorImGuiIcons.TryFromSprite(toggle.Icon, out icon))
                {
                    draw.AddImage(icon.TextureId,
                        new NVec2(buttonX + IconX, buttonY + IconY),
                        new NVec2(buttonX + IconX + IconWidth, buttonY + IconY + IconHeight),
                        icon.UvMin, icon.UvMax, ImGui.GetColorU32(ToVec4(toggle.Tint)));
                }

                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(toggle.Hint))
                {
                    ImGui.SetTooltip(toggle.Name + "\n" + toggle.Hint);
                }
            }

            return ToggleX + toggles.Count * (ToggleWidth + ToggleGap);
        }

        /// <summary>
        /// 两个功能开关：**定位**、**自动翻页**。
        ///
        /// 和左边那排图标开关不是一类东西，所以不塞进 `CollectHeaderToggles()`
        /// （那个接口的图标是 uGUI 那边造好的 Sprite，这里是现算的矢量图形）。
        /// 尺寸和左边那排**完全一致**（32×29、间距 6），读起来还是同一排按钮。
        ///
        /// 图标是自己画的两笔几何形，不带文字：
        ///   定位   = 准星（圆环 + 四向刻线 + 中心点）
        ///   翻页   = 页面左边界 + 两个向右的尖角
        /// 开/关的差别画在颜色上（开 = 强调色，关 = 次要文字色），
        /// 和「拖动吸附」那个开关同一种表达。
        ///
        /// 状态直接读 `Main.Settings`（和图标开关读同一份设置），点一下调
        /// 时间轴那边的 Toggle 方法 —— 设置写盘、状态栏提示都在那里。
        /// </summary>
        private float DrawFeatureButtons(EditorTimelineView timeline, ImDrawListPtr draw,
            float startX, float y, float height)
        {
            bool locate = Main.Settings != null && Main.Settings.LocateOnTrackClick;
            bool autoPage = Main.Settings != null && Main.Settings.AutoPageTimeline;
            float buttonY = y + (height - ToggleHeight) * 0.5f;

            float x = startX;
            DrawFeatureButton(draw, x, buttonY, "locate", locate,
                "定位", "点轨道跳过去（Ctrl+J）",
                timeline.ToggleLocateOnTrackClick, PaintLocateIcon);
            x += ToggleWidth + ToggleGap;
            DrawFeatureButton(draw, x, buttonY, "autopage", autoPage,
                "自动翻页", "播放头跑出右边缘就整页翻过去（Ctrl+K）",
                timeline.ToggleAutoPageTimeline, PaintPageIcon);
            x += ToggleWidth + ToggleGap;
            return x;
        }

        private static void DrawFeatureButton(ImDrawListPtr draw, float x, float y,
            string id, bool on, string title, string hint, System.Action click,
            System.Action<ImDrawListPtr, NVec2, uint> paintIcon)
        {
            NVec2 topLeft = new NVec2(x, y);
            NVec2 size = new NVec2(ToggleWidth, ToggleHeight);

            // 背景 / 悬停 / 按下交给 ImGui 自己的按钮样式，
            // 和左边那排图标开关长得一模一样；图标只是画在它上面。
            ImGui.SetCursorScreenPos(topLeft);
            if (ImGui.Button("##feat_" + id, size))
            {
                if (click != null)
                {
                    click();
                }
            }
            bool hovered = ImGui.IsItemHovered();

            NVec2 center = new NVec2(x + ToggleWidth * 0.5f, y + ToggleHeight * 0.5f);
            uint color = ImGui.GetColorU32(ToVec4(on ? Palette.Accent : Palette.TextMuted));
            paintIcon(draw, center, color);

            if (hovered)
            {
                ImGui.SetTooltip(title + (on ? "：开" : "：关") + "\n" + hint);
            }
        }

        /// <summary>定位图标：准星。圆环 + 四向刻线 + 中心点。</summary>
        private static void PaintLocateIcon(ImDrawListPtr draw, NVec2 center, uint color)
        {
            const float radius = 5.2f;
            const float tick = 3.2f;
            const float thickness = 1.6f;

            draw.AddCircle(center, radius, color, 20, thickness);
            draw.AddLine(new NVec2(center.X - radius - tick, center.Y),
                new NVec2(center.X - radius + 0.6f, center.Y), color, thickness);
            draw.AddLine(new NVec2(center.X + radius - 0.6f, center.Y),
                new NVec2(center.X + radius + tick, center.Y), color, thickness);
            draw.AddLine(new NVec2(center.X, center.Y - radius - tick),
                new NVec2(center.X, center.Y - radius + 0.6f), color, thickness);
            draw.AddLine(new NVec2(center.X, center.Y + radius - 0.6f),
                new NVec2(center.X, center.Y + radius + tick), color, thickness);
            draw.AddCircleFilled(center, 1.5f, color, 8);
        }

        /// <summary>翻页图标：页面左边界 + 两个向右的尖角（"整页往右翻"）。</summary>
        private static void PaintPageIcon(ImDrawListPtr draw, NVec2 center, uint color)
        {
            const float halfHeight = 5.5f;
            const float thickness = 1.6f;

            float left = center.X - 6.5f;
            draw.AddLine(new NVec2(left, center.Y - halfHeight),
                new NVec2(left, center.Y + halfHeight), color, thickness);

            for (int i = 0; i < 2; i++)
            {
                float x = center.X - 2.6f + i * 4.2f;
                draw.AddLine(new NVec2(x, center.Y - halfHeight + 0.5f),
                    new NVec2(x + 3.8f, center.Y), color, thickness);
                draw.AddLine(new NVec2(x + 3.8f, center.Y),
                    new NVec2(x, center.Y + halfHeight - 0.5f), color, thickness);
            }
        }

        /// <summary>
        /// 摘要（左）和时间读数（**正中间**）。
        ///
        /// 时间读数摆在这一行的中点 —— 它是这排里唯一"随时在变、一眼要看"的读数，
        /// 放中间扫视时最省力。摘要则从开关那排之后起、到它之前收住。
        /// </summary>
        private static void DrawReadouts(EditorStudioOverlay overlay, EditorTimelineView timeline,
            ImDrawListPtr draw, float x, float y, float width, float height, float summaryX)
        {
            float textY = y + (height - ImGui.GetTextLineHeight()) * 0.5f;

            string time = timeline.TimeReadout;
            float timeLeft = 0f;
            bool hasTime = !string.IsNullOrEmpty(time);
            if (hasTime)
            {
                NVec2 size = ImGui.CalcTextSize(time);
                timeLeft = x + (width - size.X) * 0.5f;
                draw.AddText(new NVec2(timeLeft, textY),
                    ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), time);
            }

            string summary = timeline.SummaryReadout;
            if (string.IsNullOrEmpty(summary))
            {
                return;
            }
            // 摘要从开关那排之后开始，到**中间那个时间读数**之前收住 —— 两端都得
            // 让开，所以宽度是**算出来的**，不能写死。超宽走省略号。
            float left = summaryX + 16f;
            float right = hasTime ? timeLeft - 20f : x + width * 0.5f;
            draw.AddText(new NVec2(left, textY), ImGui.GetColorU32(ToVec4(Palette.TextMuted)),
                FitText(summary, Mathf.Max(40f, right - left)));
        }

        /// <summary>原版「回到开头」的图标 —— 取到就钉住，没取到每帧重试。</summary>
        private static bool rewindIconResolved;
        private static EditorImGuiIcons.Icon rewindIcon;

        /// <summary>
        /// 从游戏自己那枚「回到开头」按钮上借图标。
        ///
        /// 和 uGUI 那排工具栏是同一个来源（`EditorTimelineView.ApplyNativeRewindIcon`），
        /// 只不过那边是喂给 uGUI 的 `Image`。编辑器没就绪时 `rewind` 可能是空的，
        /// 所以**没取到就每帧重试**，取到才钉住。
        /// </summary>
        private static bool TryGetRewindIcon(out EditorImGuiIcons.Icon icon)
        {
            if (!rewindIconResolved)
            {
                scnEditor editor = scnEditor.instance;
                UnityEngine.Sprite sprite = editor == null || editor.rewind == null
                    || editor.rewind.image == null
                    ? null
                    : editor.rewind.image.sprite;
                rewindIconResolved = EditorImGuiIcons.TryFromSprite(sprite, out rewindIcon);
            }

            icon = rewindIcon;
            return rewindIconResolved;
        }

        /// <summary>
        /// 右侧那组：车道筛选 | 回到开头 | 缩放 − 读数 + 。
        ///
        /// **位置全部由调用方算好传进来**（见 `DrawContents`）—— 中间那段时间读数
        /// 要按这组控件让位，两边必须用同一套几何；各算各的迟早会撞上（撞上时读数
        /// 的笔画会从按钮缝里透出来，看着像飘了一圈青括号）。
        /// </summary>
        private static void DrawTransport(EditorStudioOverlay overlay,
            EditorTimelineView timeline, ImDrawListPtr draw, float y, float height,
            float rewindX, float zoomOutX, float zoomInX, float zoomTextX, string zoom,
            float lanesX)
        {
            float buttonY = y + (height - ToggleHeight) * 0.5f;

            ImGui.SetCursorScreenPos(new NVec2(rewindX, buttonY));
            if (ImGui.Button("##rewind", new NVec2(RewindWidth, ToggleHeight)))
            {
                timeline.GoToStart();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("回到开头");
            }

            // **画原版那枚图标，不自己写 "|<"。**
            //
            // 精灵直接从游戏自己那枚「回到开头」按钮上取（`scnEditor.rewind.image.sprite`）
            // —— 和 uGUI 那排工具栏是同一个来源，见 `EditorTimelineView.ApplyNativeRewindIcon`。
            // 取不到（编辑器还没就绪之类）才退回文字，别开天窗。
            EditorImGuiIcons.Icon rewindIcon;
            if (TryGetRewindIcon(out rewindIcon))
            {
                draw.AddImage(rewindIcon.TextureId,
                    new NVec2(rewindX + (RewindWidth - IconWidth) * 0.5f, buttonY + IconY),
                    new NVec2(rewindX + (RewindWidth + IconWidth) * 0.5f,
                        buttonY + IconY + IconHeight),
                    rewindIcon.UvMin, rewindIcon.UvMax,
                    ImGui.GetColorU32(ToVec4(Palette.TextPrimary)));
            }
            else
            {
                NVec2 textSize = ImGui.CalcTextSize("|<");
                draw.AddText(
                    new NVec2(rewindX + (RewindWidth - textSize.X) * 0.5f,
                        buttonY + (ToggleHeight - textSize.Y) * 0.5f),
                    ImGui.GetColorU32(ToVec4(Palette.TextPrimary)), "|<");
            }

            ImGui.SetCursorScreenPos(new NVec2(zoomOutX, buttonY));
            if (ImGui.Button("-", new NVec2(StepButtonSize, ToggleHeight)))
            {
                timeline.ZoomOutStep();
            }

            ImGui.SetCursorScreenPos(new NVec2(zoomInX, buttonY));
            if (ImGui.Button("+", new NVec2(StepButtonSize, ToggleHeight)))
            {
                timeline.ZoomInStep();
            }

            // 缩放读数。位置由调用方给：它排在「回到开头」左边，不夹在缩小和
            // 回到开头之间 —— 那个缝是按 13px 的字量出来的，换成正文那一档之后
            // 读数宽了近一倍，塞回去就会和回到开头撞上。
            if (!string.IsNullOrEmpty(zoom))
            {
                draw.AddText(
                    new NVec2(zoomTextX, y + (height - ImGui.GetTextLineHeight()) * 0.5f),
                    ImGui.GetColorU32(ToVec4(Palette.TextMuted)), zoom);
            }

            // **车道筛选按钮画在这儿 —— 时间轴第一行的右边。**
            //
            // 它们原来在预览标题行里；那一行现在让给状态读数了（状态文本一长
            // 就被顶栏中间的模式切换按钮压住）。
            EditorImGuiPreviewPanel.DrawLaneButtons(overlay, draw, lanesX, y, height);
        }
    }
}
