using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 时间轴第二行那排事件按钮的 ImGui 版（原版是 uGUI 画的）。
    ///
    /// **几何和行为仍然是时间轴那套 uGUI 的**，这边只负责画和转发：
    ///
    ///   · 位置 —— 来自 `LayoutPaletteRows` 摆完记下的快照（含换行后的行数，
    ///     那一排能排成好几行、工具区高度跟着变）；
    ///   · 图标 —— `GCS.levelEventIcons`，和 uGUI 那排同一张表；
    ///   · 点击 —— 转 `EditorTimelineView.ActivatePaletteEvent`，只读判断也在那边。
    ///
    /// **不在这里另算一份布局**：换行规则一改两边就会错位，而今天已经栽过一次
    /// 同类的事（时间读数和车道按钮各自硬算位置、互相压）。
    /// </summary>
    internal sealed class EditorImGuiEventPalette
    {
        private Rect _rect;

        internal Rect Rect { get { return _rect; } }

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                return;
            }

            EditorTimelineView timeline = overlay.TimelineView;
            if (timeline == null)
            {
                return;
            }

            float x, y, width, height;
            overlay.GetTimelinePaletteRect(out x, out y, out width, out height);
            _rect = new Rect(x, y, width, height);

            int slots = timeline.PaletteSlotCount;
            if (slots <= 0 || height <= 1f)
            {
                return;
            }

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            if (ImGui.Begin("##aes_event_palette", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawSlots(timeline, x, y, slots);
            }
            ImGui.End();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        private static void DrawSlots(EditorTimelineView timeline, float x, float y, int slots)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float size = timeline.PaletteSlotSize;
            // 快照里的位置是"相对面板原点"的（和 uGUI 的 SetTopLeft 同一套坐标），
            // 而这个窗口放在面板原点 + PaletteRowY 处 —— 所以本地 y 要减掉这一段。
            float rowOffset = EditorTimelineView.PaletteRowYOffset;

            for (int i = 0; i < slots; i++)
            {
                Vector2 local = timeline.PaletteSlotPosition(i);
                float buttonX = x + local.x;
                float buttonY = y + (local.y - rowOffset);

                ImGui.PushID(i);
                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));

                // 底色跟着鼠标走就行：这排按钮没有"选中"状态（点了是新建事件，
                // 不是切模式），所以不需要常驻的底衬。
                ImGui.PushStyleColor(ImGuiCol.Button, new NVec4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToVec4(Palette.ButtonHover));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ToVec4(Palette.Selected));
                if (ImGui.Button("##slot", new NVec2(size, size)))
                {
                    timeline.ActivatePaletteSlot(i);
                }
                ImGui.PopStyleColor(3);

                if (ImGui.IsItemHovered())
                {
                    string name = timeline.PaletteSlotName(i);
                    if (!string.IsNullOrEmpty(name))
                    {
                        ImGui.SetTooltip(name);
                    }
                }
                ImGui.PopID();

                EditorImGuiIcons.Icon icon;
                if (!EditorImGuiIcons.TryFromSprite(
                    timeline.GetPaletteIcon(timeline.PaletteSlotType(i)), out icon))
                {
                    continue;
                }

                // 和 uGUI 那边同一个内缩比例（它按边长的 16% 算）。
                float inset = Mathf.Max(2f, size * 0.16f);
                draw.AddImage(icon.TextureId,
                    new NVec2(buttonX + inset, buttonY + inset),
                    new NVec2(buttonX + size - inset, buttonY + size - inset),
                    icon.UvMin, icon.UvMax,
                    ImGui.GetColorU32(new NVec4(1f, 1f, 1f, 1f)));
            }
        }

        private static NVec4 ToVec4(Color color)
        {
            return new NVec4(color.r, color.g, color.b, color.a);
        }
    }
}
