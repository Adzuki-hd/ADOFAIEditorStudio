using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 顶栏下面那一排菜单：`文件 / 工程 / 偏好 / 设置`。
    ///
    /// **「文件」整条是转发**：打开、保存、另存都直接调 `scnEditor` 自己的方法，
    /// AES 一个字节都不碰 `.adofai`（那正是我们定的第一条硬规矩——谱面用游戏
    /// 自己的解析器写，标准库往返会改坏数字格式和尾逗号）。
    /// 撤销/重做多一层：**先走工作台自己的栈**，栈空了才轮到原生 ——
    /// 装饰物、批量改参、拖块这些改动原生那边没有记录。
    ///
    /// 「偏好」「默认设置」这一轮只占位（置灰），按计划后面做。
    /// </summary>
    internal sealed class EditorImGuiMenuBar
    {
        private Rect _rect;

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            overlay.GetMenuBarRect(out x, out y, out width, out height);
            if (width <= 1f || height <= 1f)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }
            _rect = new Rect(x, y, width, height);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);

            // **套项目自己的调色板。** 菜单栏用的是 ImGui 的标准控件
            // （`BeginMenu` / `MenuItem`），它们默认的配色是灰蓝带边框，
            // 跟工作台的雾蓝扁平主题摆在一起很突兀。
            // 自绘一套菜单（下拉、键盘导航、层级）代价太大，
            // 所以走"标准控件 + 换色"这条路。
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0f, 0f, 0f, 0f));
            // 用**面板色**，不是页面底色 —— 页面底色和顶栏贴在一起分不出来，
            // 菜单栏看着就像"文字直接印在背景上"、不成一条。
            ImGui.PushStyleColor(ImGuiCol.MenuBarBg, ToVec4(Palette.Panel));
            ImGui.PushStyleColor(ImGuiCol.PopupBg, ToVec4(Palette.PanelAlt));
            ImGui.PushStyleColor(ImGuiCol.Text, ToVec4(Palette.TextPrimary));
            ImGui.PushStyleColor(ImGuiCol.TextDisabled, ToVec4(Palette.TextMuted));
            ImGui.PushStyleColor(ImGuiCol.Header, ToVec4(Palette.ButtonNormal));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ToVec4(Palette.ButtonHover));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, ToVec4(Palette.Selected));
            ImGui.PushStyleColor(ImGuiCol.Separator, ToVec4(Palette.PanelRule));
            ImGui.PushStyleColor(ImGuiCol.Border, ToVec4(Palette.PanelRule));

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new NVec2(12f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new NVec2(8f, 6f));
            // 扁平：圆角一律不给，和面板的直角语言一致。
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);

            if (ImGui.Begin("##aes_menubar", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                // **必须有这个标志**：`BeginMenuBar()` 开头就查它，
                // 没有就直接返回 false —— 表现是"位置空着、菜单一条都没画"。
                | ImGuiWindowFlags.MenuBar))
            {
                DrawItems(overlay);
            }
            ImGui.End();
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor(10);
        }

        private static void DrawItems(EditorStudioOverlay overlay)
        {
            if (!ImGui.BeginMenuBar())
            {
                return;
            }

            // 只读模式下「文件」整条置灰：那几项都会改谱面。
            // 不能点开比点开再发现都灰掉更清楚。
            bool edit = overlay.IsEditMode;
            if (!edit)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.BeginMenu("文件"))
            {
                if (ImGui.MenuItem("打开", "Ctrl+O"))
                {
                    overlay.MenuOpenLevel();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("撤销", "Ctrl+Z"))
                {
                    overlay.MenuUndo();
                }
                if (ImGui.MenuItem("重做", "Ctrl+Y"))
                {
                    overlay.MenuRedo();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("保存", "Ctrl+S"))
                {
                    overlay.MenuSaveLevel();
                }
                if (ImGui.MenuItem("另存为", "Ctrl+Shift+S"))
                {
                    overlay.MenuSaveLevelAs();
                }
                ImGui.EndMenu();
            }
            if (!edit)
            {
                ImGui.EndDisabled();
            }

            // 「工程」管的是 `.aesprj`，同样只在编辑模式下可用。
            if (!edit)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.BeginMenu("工程"))
            {
                if (ImGui.MenuItem("重置时间轴排版"))
                {
                    overlay.MenuResetTimelineLayout();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("保存工程"))
                {
                    overlay.MenuSaveProject();
                }
                // 另存要一个输入框（新工程名），还没做 —— 留成占位项，
                // 让用户知道这里将来有它。
                ImGui.MenuItem("另存工程（开发中）", null, false, false);
                ImGui.EndMenu();
            }
            if (!edit)
            {
                ImGui.EndDisabled();
            }

            // 偏好：这一轮只占位。**留成菜单项而不是干脆不画** ——
            // 用户能看到"这里将来有这些东西"，比一片空白清楚。
            ImGui.BeginDisabled();
            if (ImGui.BeginMenu("偏好"))
            {
                ImGui.MenuItem("（开发中）", null, false, false);
                ImGui.EndMenu();
            }
            ImGui.EndDisabled();

            // 设置下面只有「开发者设置」是活的，所以**不能整条置灰** ——
            // 那会把它一起关掉。只把「默认设置」那一项单独禁用。
            if (ImGui.BeginMenu("设置"))
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("默认设置（开发中）", null, false, false);
                ImGui.EndDisabled();
                ImGui.Separator();
                DrawDeveloperSettings();
                ImGui.EndMenu();
            }

            ImGui.EndMenuBar();
        }

        /// <summary>
        /// 开发者设置：调试开关。这些不该给普通玩家看见，但目前没有
        /// "是不是开发者"的判据，先收在二级菜单里。
        /// </summary>
        private static void DrawDeveloperSettings()
        {
            Settings settings = Main.Settings;
            if (settings == null)
            {
                ImGui.MenuItem("（读不到设置）", null, false, false);
                return;
            }

            bool verbose = settings.VerboseDiagnostics;
            if (ImGui.MenuItem("探针总闸", null, verbose))
            {
                settings.VerboseDiagnostics = !verbose;
                Main.SaveSettings();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "打开后输出 [TickCost] / [时间轴] / [ImGui桥] / [ImGui帧] 四条性能探针，每 60 帧一行");
            }

            // 媒体缩略图的缓存（磁盘 + 内存）。**只删缓存目录里的文件**，
            // 不碰谱面、不碰工程、不碰设置。
            if (ImGui.MenuItem("清空媒体缓存", null, false, true))
            {
                EditorStudioOverlay overlay = Main.Overlay;
                if (overlay != null)
                {
                    overlay.ClearMediaCacheEverywhere();
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "删掉媒体缩略图的磁盘缓存和内存缓存。\n"
                    + "下次打开谱面媒体会重新解码（加载页会重新出现）——\n"
                    + "要验证「首次打开」那条路径时用它，省得手动去 mod 目录删文件。");
            }
        }
    }
}
