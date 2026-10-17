using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 工作台顶栏的 ImGui 版：品牌竖线、标题、版本链接、副标题、状态读数、
    /// 快捷键提示、联机、帮助。
    ///
    /// **字号只有一档。** 原来的顶栏用了三档（20 / 13 / 26），而 ImGui 那边的
    /// 字体图集是按"正文那一档"光栅化的 —— 拿它缩小画是糊的，再加一档字号又要
    /// 让图集翻倍。所以这里统一按正文那一档排，和 ImGui 面板里的字一致；
    /// 槽位按这个字号重算，**没有照抄原来那串按 13px 算出来的 x**。
    ///
    /// 三个能点的地方都转发到桥已有的入口，不另开一套：
    /// 版本号 → `SetVersionVisible`，帮助 → `ToggleHelp`，联机 → `SetTransientStatus`。
    ///
    /// `Rect` 要交给桥去挡住点击 —— uGUI 那条件被藏了，没人接的话点顶栏会
    /// 穿到后面的工作台上去。
    /// </summary>
    internal sealed class EditorImGuiHeader
    {
        private Rect _rect;

        /// <summary>左右两端的留白。</summary>
        private const float SidePadding = 24f;
        /// <summary>相邻两块文字之间至少留这么多。</summary>
        private const float TextGap = 16f;
        /// <summary>品牌竖线：x、宽、高。</summary>
        private const float RuleX = 24f;
        private const float RuleWidth = 3f;
        private const float RuleHeight = 12f;
        /// <summary>右侧两颗按钮的尺寸。</summary>
        private const float MultiplayerWidth = 96f;
        private const float ActionHeight = 32f;
        private const float HelpSize = 34f;
        /// <summary>
        /// 版本号那颗隐形按钮的高度。
        ///
        /// **不占满整条顶栏** —— 占满的话，在顶栏上下任意高度点到版本号那一列
        /// 都会弹出更新窗口，包括离那行字很远的地方。
        /// </summary>
        private const float VersionHitHeight = 36f;

        internal Rect Rect { get { return _rect; } }

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            overlay.GetHeaderRect(out x, out y, out width, out height);
            _rect = new Rect(x, y, width, height);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            // **底色直接换成顶栏色**，不另外铺一块矩形 —— 窗口本来就会填自己那一块，
            // 再铺一层是多余的一遍，而且会把后面画的东西压住。
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToVec4(overlay.HeaderColor));
            // 内边距清零：底下的摆位全是绝对坐标，留了内边距反而要到处补。
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            if (ImGui.Begin("##aes_header", ImGuiWindowFlags.NoTitleBar
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
            float middle = y + height * 0.5f;
            // 按**行高**居中，不是按字号 —— 字面本身比行盒矮，按字号会整体偏上。
            float textY = y + (height - ImGui.GetTextLineHeight()) * 0.5f;

            // 品牌竖线。沿用面板标题那条竖条的语言：紫＝能点的，金＝要读的。
            draw.AddRectFilled(
                new NVec2(x + RuleX, middle - RuleHeight * 0.5f),
                new NVec2(x + RuleX + RuleWidth, middle + RuleHeight * 0.5f),
                ImGui.GetColorU32(ToVec4(Palette.AccentSecondary)), 1f);

            float cursor = x + RuleX + RuleWidth + 8f;

            string title = "ADOFAI  EDITOR STUDIO";
            NVec2 titleSize = ImGui.CalcTextSize(title);
            draw.AddText(new NVec2(cursor, textY),
                ImGui.GetColorU32(ToVec4(Palette.TextPrimary)), title);
            cursor += titleSize.X + TextGap;

            cursor = DrawVersionLink(overlay, draw, cursor, y, height, textY, titleSize.Y);
            cursor += TextGap;

            string subtitle = "剪辑器工作台";
            draw.AddText(new NVec2(cursor, textY),
                ImGui.GetColorU32(ToVec4(Palette.TextMuted)), subtitle);

            // 状态读数**不再画在这儿**。它原先钉在顶栏中段（StatusX），而中段
            // 正中被模式切换按钮占着 —— 状态文本一长就被压掉半截。现在挪到
            // 预览标题行里画，见 `EditorImGuiPreviewPanel`。

            // 正中：只读 / 编辑。
            DrawModeSwitch(overlay, x, width, y, height);

            // 右侧从右往左摆：帮助 → 联机 → 快捷键提示。
            float helpX = x + width - SidePadding - HelpSize;
            float multiplayerX = helpX - 12f - MultiplayerWidth;

            // 快捷键已经取消：工作台改由编辑器里那颗悬浮球打开。
            string shortcut = "悬浮球  打开工作台  ·  Esc  关闭";
            NVec2 shortcutSize = ImGui.CalcTextSize(shortcut);
            draw.AddText(new NVec2(multiplayerX - 24f - shortcutSize.X, textY),
                ImGui.GetColorU32(ToVec4(Palette.TextMuted)), shortcut);

            ImGui.SetCursorScreenPos(new NVec2(multiplayerX, y + (height - ActionHeight) * 0.5f));
            if (ImGui.Button("联机", new NVec2(MultiplayerWidth, ActionHeight)))
            {
                // 面板和协议都还在代码里，只是先不给玩家开这个口子
                // （和 uGUI 那颗按钮同一条话）。
                overlay.SetTransientStatus("联机功能开发中，敬请期待");
            }

            ImGui.SetCursorScreenPos(new NVec2(helpX, y + (height - HelpSize) * 0.5f));
            if (ImGui.Button("?", new NVec2(HelpSize, HelpSize)))
            {
                EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
                if (bridge != null)
                {
                    bridge.ToggleHelp();
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("打开说明窗口");
            }
        }

        /// <summary>
        /// 版本号：能点，点开更新窗口。
        ///
        /// **下划线是「能点」的唯一提示** —— 它没有边框也没有底色，加了下划线
        /// 才读得成一行链接，而不是一串普通的字。悬停时提亮一档。
        /// </summary>
        private static float DrawVersionLink(EditorStudioOverlay overlay,
            ImDrawListPtr draw, float x, float y, float height, float textY, float lineHeight)
        {
            string version = "v" + EditorStudioOverlay.ModVersion;
            NVec2 size = ImGui.CalcTextSize(version);

            // 点击区：压在文字上，**但按钮自己那三层底色全压成透明** ——
            // 留了底色它就不是"一行字"了。
            ImGui.PushStyleColor(ImGuiCol.Button, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new NVec4(1f, 1f, 1f, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new NVec4(1f, 1f, 1f, 0.10f));
            ImGui.SetCursorScreenPos(new NVec2(x - 4f, y + (height - VersionHitHeight) * 0.5f));
            bool clicked = ImGui.Button("##version", new NVec2(size.X + 8f, VersionHitHeight));
            bool hovered = ImGui.IsItemHovered();
            ImGui.PopStyleColor(3);

            if (clicked)
            {
                EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
                if (bridge != null)
                {
                    bridge.SetVersionVisible(true);
                }
            }

            Color color = hovered ? Palette.TextPrimary : overlay.VersionLinkColor;
            draw.AddText(new NVec2(x, textY), ImGui.GetColorU32(ToVec4(color)), version);
            float underlineY = textY + lineHeight - 2f;
            draw.AddLine(new NVec2(x, underlineY), new NVec2(x + size.X, underlineY),
                ImGui.GetColorU32(ToVec4(color)), 1f);

            return x + size.X;
        }

        /// <summary>
        /// 中段那条状态读数（滚轮微调、存档提示、报错都走它）。
        ///
        /// **位置钉死，不跟着前面的字流。** 它每次滚轮都会变长变短，跟着流
        /// 就是每滚一格整条左右跳一下。底衬按**文字实际宽度**贴 ——
        /// 用固定宽度会得到一条横贯顶栏的长条。
        /// </summary>
        /// <summary>模式按钮的尺寸。</summary>
        private const float ModeSwitchHeight = 32f;
        private const float ModeSwitchPadding = 20f;
        /// <summary>下边每侧内缩多少 —— 做出上宽下窄的倒梯形。</summary>
        private const float ModeSwitchSlant = 9f;
        private const float ModeArrowWidth = 11f;
        private const float ModeArrowHeight = 6f;

        /// <summary>
        /// 顶栏正中的模式切换：**一颗倒梯形按钮，只显示当前状态**。
        ///
        /// 点一下在两档之间来回切，所以文字写的是「现在是什么」，旁边那对箭头
        /// 才是「这里能点」的提示 —— 状态和入口合成一个东西，比并排两颗按钮
        /// 省一半横向空间，也不会让人猜「哪颗才是当前的」。
        ///
        /// 形状是自绘的：ImGui 的 `Button` 只能画矩形，而这里要的是倒梯形。
        /// 点击用一颗透明的 `InvisibleButton` 盖在上面 —— 形状归形状、命中归命中。
        /// </summary>
        private static void DrawModeSwitch(EditorStudioOverlay overlay,
            float x, float width, float y, float height)
        {
            bool edit = overlay.IsEditMode;
            string label = edit ? "编辑模式" : "只读模式";

            NVec2 textSize = ImGui.CalcTextSize(label);
            float buttonWidth = textSize.X + ModeSwitchPadding * 2f + ModeArrowWidth + 6f;
            float left = x + (width - buttonWidth) * 0.5f;
            float top = y + (height - ModeSwitchHeight) * 0.5f;
            float bottom = top + ModeSwitchHeight;
            float right = left + buttonWidth;
            float slant = ModeSwitchSlant;

            ImGui.SetCursorScreenPos(new NVec2(left, top));
            bool clicked = ImGui.InvisibleButton("##modeSwitch",
                new NVec2(buttonWidth, ModeSwitchHeight));
            bool hovered = ImGui.IsItemHovered();
            if (clicked)
            {
                overlay.SetEditMode(!edit);
            }

            ImDrawListPtr draw = ImGui.GetWindowDrawList();

            // 倒梯形：上宽下窄，四角顺时针。
            NVec4 fill = ToVec4(hovered ? Palette.ButtonHover : Palette.ButtonNormal);
            if (edit)
            {
                // 编辑模式底下换成强调色 —— 扫一眼就知道现在处在「能改」的状态。
                fill = ToVec4(hovered ? Palette.SelectedHover : Palette.Selected);
            }
            draw.AddQuadFilled(
                new NVec2(left, top),
                new NVec2(right, top),
                new NVec2(right - slant, bottom),
                new NVec2(left + slant, bottom),
                ImGui.GetColorU32(fill));

            // **两档各给一个鲜艳的颜色**，一眼分得开：编辑用次色、只读用主色。
            // 悬停时**不换成白色** —— 那反而把颜色洗掉了，只把底衬提亮（见上）。
            NVec4 textColor = ToVec4(edit ? Palette.AccentSecondary : Palette.Accent);
            draw.AddText(
                new NVec2(left + ModeSwitchPadding,
                    top + (ModeSwitchHeight - textSize.Y) * 0.5f),
                ImGui.GetColorU32(textColor), label);

            // 右侧那对箭头：上下各一个三角，表示「这里能切」。
            float arrowX = right - slant - ModeSwitchPadding + 2f;
            float midY = (top + bottom) * 0.5f;
            float half = ModeArrowHeight * 0.5f;
            const float gap = 1.5f;
            // 箭头跟文字同色；悬停时提亮一档，表示"这里能点"。
            NVec4 arrowVec = hovered ? ToVec4(Palette.TextPrimary) : textColor;
            uint arrowColor = ImGui.GetColorU32(arrowVec);
            draw.AddTriangleFilled(
                new NVec2(arrowX - ModeArrowWidth * 0.5f, midY - gap),
                new NVec2(arrowX + ModeArrowWidth * 0.5f, midY - gap),
                new NVec2(arrowX, midY - gap - half), arrowColor);
            draw.AddTriangleFilled(
                new NVec2(arrowX - ModeArrowWidth * 0.5f, midY + gap),
                new NVec2(arrowX + ModeArrowWidth * 0.5f, midY + gap),
                new NVec2(arrowX, midY + gap + half), arrowColor);

            if (hovered)
            {
                ImGui.SetTooltip(edit
                    ? "现在是编辑模式 —— 点一下切回只读"
                    : "现在是只读 —— 点一下切到编辑");
            }
        }

    }
}
