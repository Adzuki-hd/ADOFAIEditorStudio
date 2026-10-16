using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 预览面板**页眉那一条**（工具栏底衬、页眉竖条、"预览"标题、分隔线、
    /// 右侧那排车道筛选按钮）的 ImGui 版。
    ///
    /// **只搬页眉，不搬舞台。** 舞台那块是透明的 —— 游戏画面从它下面透上来，
    /// 那块区域里根本没有可画的东西；它那圈描边是四条静态线，留着 uGUI 画
    /// 和重画一遍没区别。所以这块的边界就划在工具栏下沿。
    ///
    /// 车道筛选按钮的状态和图标都归时间轴所有（它才是按车道分组事件的那个）：
    /// - 图标：`EditorTimelineView.GetLaneIcon` —— 一个车道挑一个属于它的事件类型
    /// - 状态：`IsLaneVisible` —— **隐藏的车道把图标压到 28% 白**，和 uGUI 那边
    ///   同一条表现（见 UpdateFilterVisuals），不另立一套"选中"样式
    /// </summary>
    internal sealed class EditorImGuiPreviewPanel
    {
        private Rect _rect;

        /// <summary>页眉竖条的 x / 宽 / 高。</summary>
        private const float HeadingBarX = 16f;
        private const float HeadingBarWidth = 3f;
        private const float HeadingBarHeight = 16f;
        /// <summary>"预览"两个字的左缘：竖条之后。</summary>
        private const float HeadingTextX = HeadingBarX + HeadingBarWidth + 8f;
        /// <summary>
        /// 标题行里"标题之后"那一段的左缘。现在是**状态读数**的起点；
        /// 车道筛选按钮原本也起在这儿，它们已经搬去时间轴第一行了。
        /// </summary>
        private const float TitleRowContentX = 96f;
        private const float LaneButtonSize = 26f;
        private const float LaneButtonGap = 4f;
        private const float LaneIconInset = 4f;
        /// <summary>隐藏的车道，图标压到这个透明度 —— 和 uGUI 那条一致。</summary>
        private const float LaneHiddenIconAlpha = 0.28f;

        /// <summary>
        /// 那排车道按钮占多宽 —— 时间轴第一行要用它算右对齐的位置。
        /// </summary>
        internal static float LaneButtonsWidth
        {
            get
            {
                int count = EditorTimelineView.LaneCount;
                return count * LaneButtonSize + (count - 1) * LaneButtonGap;
            }
        }

        internal Rect Rect { get { return _rect; } }

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || !overlay.PreviewVisible)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            overlay.GetPreviewToolbarRect(out x, out y, out width, out height);
            _rect = new Rect(x, y, width, height);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            // 底色自己画（见下），窗口这层透明 —— 工具栏只有**上面两个角**是圆的，
            // 交给窗口画会连下面两个角一起圆掉，那两处正贴着游戏画面。
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            if (ImGui.Begin("##aes_preview_toolbar", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawContents(overlay, x, y, width, height);
            }
            ImGui.End();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        private static void DrawContents(EditorStudioOverlay overlay,
            float x, float y, float width, float height)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            NVec2 min = new NVec2(x, y);
            NVec2 max = new NVec2(x + width, y + height);

            // 工具栏底衬：圆角只圆上面两个。用 Band（和另外三块面板的标题条
            // 同一档）—— 原来用过"面板色 + 0.97 不透明"，和面板几乎一样，
            // 整条读不出来，预览画面顶上就是一片平的黑。
            draw.AddRectFilled(min, max, ImGui.GetColorU32(ToVec4(Palette.SurfaceBand)),
                EditorStudioOverlay.CornerLarge, ImDrawFlags.RoundCornersTop);

            // 页眉竖条。和别的面板同一条语言：紫＝能点的，金＝要读的。
            float middle = y + height * 0.5f;
            draw.AddRectFilled(
                new NVec2(x + HeadingBarX, middle - HeadingBarHeight * 0.5f),
                new NVec2(x + HeadingBarX + HeadingBarWidth, middle + HeadingBarHeight * 0.5f),
                ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), 1f);

            draw.AddText(new NVec2(x + HeadingTextX, y + (height - ImGui.GetTextLineHeight()) * 0.5f),
                ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), "预览");

            // **状态读数挪到这儿画。**
            //
            // 它原来在顶栏中段，而中段正中被模式切换按钮占着 —— 状态文本一长
            // 就被压掉半截（"只读模式不能修改谱面，先切到编辑"只剩前半句）。
            // 预览标题行这会儿正好空出来：车道筛选按钮刚搬去时间轴第一行。
            DrawStatus(overlay, draw, x, y, height);

            // 分隔线落在工具栏下沿（不是面板通用的标题高度）—— 那条会被画进
            // 工具栏内部，读起来是横穿它的一条杂线。
            draw.AddLine(new NVec2(x, y + height), new NVec2(x + width, y + height),
                ImGui.GetColorU32(ToVec4(Palette.PanelRule)), 1f);
        }

        /// <summary>
        /// 状态读数（滚轮微调、存档提示、报错、只读提示都走它）。
        ///
        /// 底色用的是 `StatusPillColor`，和顶栏那版一模一样 —— 只是换了地方，
        /// 在预览标题行里、标题之后那片空位上。
        /// </summary>
        private static void DrawStatus(EditorStudioOverlay overlay, ImDrawListPtr draw,
            float x, float y, float height)
        {
            string status = overlay.StatusText;
            if (string.IsNullOrEmpty(status))
            {
                return;
            }

            float left = x + TitleRowContentX;
            float middle = y + height * 0.5f;
            NVec2 size = ImGui.CalcTextSize(status);

            draw.AddRectFilled(
                new NVec2(left - 10f, middle - LaneButtonSize * 0.5f),
                new NVec2(left + size.X + 10f, middle + LaneButtonSize * 0.5f),
                ImGui.GetColorU32(ToVec4(overlay.StatusPillColor)),
                EditorStudioOverlay.CornerSmall);
            draw.AddText(new NVec2(left, middle - size.Y * 0.5f),
                ImGui.GetColorU32(ToVec4(Palette.Accent)), status);
        }

        /// <summary>
        /// 那排车道筛选按钮。
        ///
        /// **现在画在时间轴的第一行右边** —— 原来它在预览标题行里，而那个位置
        /// 要让给状态读数（状态文本一长就被顶栏中间的模式切换按钮压住）。
        /// 所以这里不再自己算位置：`startX` 是第一颗按钮的左缘，由调用方按自己
        /// 那行的排布给。
        /// </summary>
        internal static void DrawLaneButtons(EditorStudioOverlay overlay,
            ImDrawListPtr draw, float startX, float y, float height)
        {
            EditorTimelineView lanes = overlay.Timeline;
            if (lanes == null)
            {
                return;
            }

            float buttonY = y + (height - LaneButtonSize) * 0.5f;
            float iconSize = LaneButtonSize - LaneIconInset * 2f;

            for (int lane = 0; lane < EditorTimelineView.LaneCount; lane++)
            {
                float buttonX = startX + lane * (LaneButtonSize + LaneButtonGap);
                ImGui.PushID(lane);
                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));

                // **底衬跟着开/关走。**
                //
                // 原来两种状态的按钮底衬是一模一样的（都是默认样式那块），
                // 只有图标透明度 1.0 / 0.28 的差别 —— 在工具栏这条深色带上
                // 太弱，一眼看不出哪几个是开着的（用户原话："很难辨认开了哪些"）。
                // 现在开着的给一块升起的底衬，关掉的整个底衬去掉 —— 两个维度
                // 一起变，扫一眼就分得出来。
                bool laneVisible = lanes.IsLaneVisible(lane);
                ImGui.PushStyleColor(ImGuiCol.Button,
                    laneVisible ? ToVec4(Palette.Raised) : new System.Numerics.Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToVec4(Palette.ButtonHover));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ToVec4(Palette.Selected));
                if (ImGui.Button("##lane", new NVec2(LaneButtonSize, LaneButtonSize)))
                {
                    lanes.ToggleLane(lane);
                }
                ImGui.PopStyleColor(3);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("点一下：显示 / 隐藏这一类");
                }
                ImGui.PopID();

                EditorImGuiIcons.Icon icon;
                if (!EditorImGuiIcons.TryFromSprite(lanes.GetLaneIcon(lane), out icon))
                {
                    continue;
                }

                float drawWidth, drawHeight;
                FitBox(iconSize, iconSize, icon.Aspect, out drawWidth, out drawHeight);
                float left = buttonX + (LaneButtonSize - drawWidth) * 0.5f;
                float top = buttonY + (LaneButtonSize - drawHeight) * 0.5f;
                float alpha = lanes.IsLaneVisible(lane) ? 1f : LaneHiddenIconAlpha;
                draw.AddImage(icon.TextureId, new NVec2(left, top),
                    new NVec2(left + drawWidth, top + drawHeight),
                    icon.UvMin, icon.UvMax,
                    ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, alpha)));
            }
        }
    }
}
