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
            DrawReadouts(overlay, timeline, draw, x, y, width, height, cursor);
            DrawTransport(timeline, draw, x, y, width, height);
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

        /// <summary>摘要（左）和时间读数（右）。时间读数右对齐占住右端。</summary>
        private static void DrawReadouts(EditorStudioOverlay overlay, EditorTimelineView timeline,
            ImDrawListPtr draw, float x, float y, float width, float height, float summaryX)
        {
            float textY = y + (height - ImGui.GetTextLineHeight()) * 0.5f;

            string time = timeline.TimeReadout;
            if (!string.IsNullOrEmpty(time))
            {
                NVec2 size = ImGui.CalcTextSize(time);
                draw.AddText(new NVec2(x + width - 200f - size.X, textY),
                    ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), time);
            }

            string summary = timeline.SummaryReadout;
            if (string.IsNullOrEmpty(summary))
            {
                return;
            }
            // 摘要从开关那排之后开始，到时间读数之前收住 —— 两端都得让开，
            // 所以宽度是**算出来的**，不能写死。超宽走省略号。
            float left = summaryX + 16f;
            float right = x + width - 200f - 16f;
            draw.AddText(new NVec2(left, textY), ImGui.GetColorU32(ToVec4(Palette.TextMuted)),
                FitText(summary, Mathf.Max(40f, right - left)));
        }

        /// <summary>右侧那组：回到开头 | 缩放 - 读数 + 。</summary>
        private static void DrawTransport(EditorTimelineView timeline, ImDrawListPtr draw,
            float x, float y, float width, float height)
        {
            float buttonY = y + (height - ToggleHeight) * 0.5f;
            float right = x + width - RightPadding;

            float zoomInX = right - StepButtonSize;
            float zoomOutX = zoomInX - 4f - StepButtonSize;
            float rewindX = zoomOutX - 16f - RewindWidth;

            ImGui.SetCursorScreenPos(new NVec2(rewindX, buttonY));
            if (ImGui.Button("|<", new NVec2(RewindWidth, ToggleHeight)))
            {
                timeline.GoToStart();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("回到开头");
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

            // 读数排在「回到开头」**左边**，不夹在缩小和回到开头之间 ——
            // 那个缝是按 13px 的字量出来的，换成正文那一档之后读数宽了近一倍，
            // 塞回去就会和 |< 撞上（离线预览里一眼可见）。
            string zoom = timeline.ZoomReadout;
            if (!string.IsNullOrEmpty(zoom))
            {
                NVec2 size = ImGui.CalcTextSize(zoom);
                draw.AddText(
                    new NVec2(rewindX - 12f - size.X, y + (height - ImGui.GetTextLineHeight()) * 0.5f),
                    ImGui.GetColorU32(ToVec4(Palette.TextMuted)), zoom);
            }
        }
    }
}
