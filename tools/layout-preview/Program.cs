// 参数栏布局的离线预览：**不开游戏**，把那一套排法渲染出来存成图，自己看。
//
// 为什么要有它：改布局时我在盲改 —— 把 uGUI 那种"手工摆 x/y"的习惯带过来了，
// 改完只能靠用户在游戏里看，改坏一次就是一个回合。而 ImGui 的输出（顶点缓冲 +
// 字体图集）本来就是纯数据，导出成图看并不难。
//
// **它照抄 EditorImGuiInspector 的排法，不是共用** —— 那边依赖游戏类型
// （LevelEvent / PropertyInfo），跑不起来。所以：
//   **改了 Inspector 的布局，这里要跟着改，否则这面镜子就照歪了。**
//
// 跑法：  cd tools/layout-preview && dotnet run
// 产物：  out/vtx.raw、out/idx.raw、out/atlas.raw（交给 tools/layout-preview/rasterize.py 出图）
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

internal static class Program
{
    // ---- 和 Inspector 保持一致的那几个数（改了那边记得改这里）----
    private static float PanelWidth = 345f;
    /// <summary>扫描模式：不导图，只把每一档宽度下的"溢出"打出来。</summary>
    private static bool Sweep;
    /// <summary>装饰物列表模式：渲染的是左栏那个列表，不是参数栏。</summary>
    private static bool DecoMode;
    private static bool MediaMode;
    private static bool HeaderMode;
    private static bool TimelineMode;
    private static bool BlocksMode;
    private static int OverflowCount;
    private static float PanelHeight = 980f;
    /// <summary>和 Inspector 一样：按 ImGui 勾选框的实际宽度算（约 GetFrameHeight）。</summary>
    private static float EnableColumnWidth
    {
        get { return ImGui.GetFrameHeight() + LabelLeftGap; }
    }
    private const float LabelGap = 8f;
    private const float LabelLeftGap = 8f;
    private const float CardPad = 12f;
    private const float CardGap = 8f;
    private const float RowSpacing = CardPad * 2f + CardGap;
    private const float CornerSmall = 6f;
    private const float BodyFontPixels = 23f;
    private const float Scale = 1.333f;

    private static readonly NVec4 PanelColor = Hex("1A1F27");
    private static readonly NVec4 Raised = Hex("242A33");
    private static readonly NVec4 RaisedHover = Hex("2A313B");
    private static readonly NVec4 TextPrimary = Hex("E2E8F0");
    private static readonly NVec4 TextSecondary = Hex("94A3B8");
    private static readonly NVec4 SelectedHover = Hex("2A313B");
    private static readonly NVec4 TextMuted = Hex("64748B");
    private static readonly NVec4 ThumbWell = Hex("14181E");
    private static readonly NVec4 HeaderBg = Hex("1A1F27");

    private sealed class Row
    {
        public string Label;
        public string Value;
        public string Unit;
        public bool Checkbox;      // 值是勾选框
        public bool Combo;         // 值是下拉
        public bool Swatch;        // 值是色块
        public bool CanDisable;
    }

    private static readonly Row[] Rows =
    {
        new Row { Label = "效果起始方块", Value = "0", Unit = "方块" },
        new Row { Label = "间隙长度", Value = "0" },
        new Row { Label = "时长", Value = "4", Unit = "拍子" },
        new Row { Label = "轨道颜色类型", Value = "单独" },
        new Row { Label = "轨道主色调", Value = "FFFFFF" },
        new Row { Label = "色彩动画间隔", Value = "2", Unit = "秒", CanDisable = true },
        new Row { Label = "颜色脉冲长度", Value = "10", Unit = "方块", CanDisable = true },
        new Row { Label = "可见", Value = "", Checkbox = true, CanDisable = true },
        new Row { Label = "位置偏移", Value = "0", Unit = "方块", CanDisable = true },
        new Row { Label = "缓速", Value = "Out Cubic", Combo = true, CanDisable = true },
        new Row { Label = "轨道颜色类型", Value = "单独", Combo = true },
        new Row { Label = "轨道主色调", Value = "FFFFFF", Swatch = true },
        new Row { Label = "角度偏移", Value = "0", Unit = "°" },
        new Row { Label = "事件标签", Value = "" },
    };

    /// <summary>
    /// 一行画完之后量一下：**最后一个控件的右缘有没有超过卡片右缘（去掉内边距）**。
    /// 这是"输入框贴边"的自动判据 —— 靠眼睛扫各个宽度不可靠。
    /// </summary>
    /// <summary>上一张卡片的下缘 —— 用来量卡间竖向空隙。</summary>
    private static float LastCardBottom = float.MinValue;

    /// <summary>
    /// 卡片之间的**竖向空隙**够不够。
    ///
    /// 横向的溢出有 CheckRowFit 管；竖向这条是补的 —— 窄排法曾经只放了个
    /// 3 像素的 Dummy，卡片几乎贴在一起（"输入框贴到上面那张卡片上了"），
    /// 而当时没有任何闸门能发现它。
    /// </summary>
    /// <summary>
    /// **左边也要查。** 之前只查了"右边溢出"，于是"勾选框贴着卡片左边缘"
    /// 这种（宽排法漏了一句 SetCursorPosX）没有任何闸门能发现。
    /// </summary>
    private static void CheckRowLeft(string label, float firstItemLeft, float cardLeft)
    {
        if (!Sweep) return;
        float over = (cardLeft + CardPad) - firstItemLeft;
        if (over > 1.0f)
        {
            OverflowCount++;
            Console.WriteLine("   左边贴边 " + over.ToString("0.0") + "px   " + label);
        }
    }

    /// <summary>
    /// **标签不能压住勾选框。** 之前所有闸门都在查"离边框多远"，
    /// 而"两个控件自己重叠"没有任何一条能发现 —— 于是标签压着勾选框过了一轮。
    /// </summary>
    private static void CheckLabelClear(string label, float labelLeft, float toggleRight)
    {
        if (!Sweep || toggleRight <= 0f) return;
        if (labelLeft - toggleRight < 1.0f)
        {
            OverflowCount++;
            Console.WriteLine("   标签压住勾选框 " + (toggleRight - labelLeft).ToString("0.0")
                + "px   " + label);
        }
    }

    private static void CheckCardGap(string label, float cardTop)
    {
        if (!Sweep) return;
        if (LastCardBottom > float.MinValue && cardTop - LastCardBottom < CardGap - 1.0f)
        {
            OverflowCount++;
            Console.WriteLine("   卡间空隙只有 " + (cardTop - LastCardBottom).ToString("0.0")
                + "px（要 " + CardGap.ToString("0") + "）   " + label);
        }
    }

    private static void CheckRowFit(string label, NVec2 cardMax)
    {
        if (!Sweep) return;
        float over = ImGui.GetItemRectMax().X - (cardMax.X - CardPad);
        if (over > 0.5f)
        {
            OverflowCount++;
            Console.WriteLine("   溢出 " + over.ToString("0.0") + "px   " + label);
        }
    }

    private static int Main(string[] args)
    {
        // --check <宽>  只扫描不导图：把这一档宽度下"控件顶出卡片"的行打出来。
        // <宽>          正常渲染并导出这一档。
        if (args.Length > 0 && args[0] == "--check")
        {
            Sweep = true;
            PanelWidth = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            PanelHeight = 1500f;
        }
        else if (args.Length > 0 && args[0] == "--deco")
        {
            DecoMode = true;
            PanelWidth = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            PanelHeight = 900f;
        }
        else if (args.Length > 0 && args[0] == "--media")
        {
            MediaMode = true;
            PanelWidth = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            PanelHeight = 900f;
        }
        else if (args.Length > 0 && args[0] == "--blocks")
        {
            BlocksMode = true;
            PanelWidth = args.Length > 1
                ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
                : 1896f;
            PanelHeight = 420f;
        }
        else if (args.Length > 0 && args[0] == "--timeline")
        {
            TimelineMode = true;
            PanelWidth = args.Length > 1
                ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
                : 1896f;
            PanelHeight = 90f;
        }
        else if (args.Length > 0 && args[0] == "--header")
        {
            HeaderMode = true;
            PanelWidth = args.Length > 1
                ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
                : 1920f;
            PanelHeight = 90f;
        }
        else if (args.Length > 0)
        {
            PanelWidth = float.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            PanelHeight = 1500f;
        }

        ImGui.CreateContext();
        ImGuiIOPtr io = ImGui.GetIO();
        unsafe { ((ImGuiIO*)io.NativePtr)->IniFilename = null; }
        // **把 ID 冲突高亮打开。** ImGui 会把撞 ID 的控件画成红框 ——
        // 这样这类问题在渲染图里直接看得见，不用等用户在游戏里撞上。
        unsafe { ((ImGuiIO*)io.NativePtr)->ConfigDebugHighlightIdConflicts = 1; }

        // **DisplaySize 必须在跑帧之前设。** 忘了设的话 ImGui 拿到 0x0，
        // 直接是原生断言（弹 Windows 模态框）—— 第一版扫描就是在这一步崩的。
        io.DisplaySize = new NVec2(PanelWidth, PanelHeight);
        io.DeltaTime = 1f / 60f;

        BuildFont(io);
        ApplyStyle();

        // 鼠标放在第四行上 —— 顺便看悬停那份高亮对不对。
        io.AddMousePosEvent(120f, 150f);

        RunFrames();

        if (Sweep)
        {
            Console.WriteLine("宽度 " + PanelWidth.ToString("0") + "  "
                + (OverflowCount == 0 ? "ok" : ("溢出 " + OverflowCount + " 处")));
            return 0;
        }

        Dump(io);
        return 0;
    }

    /// <summary>跑几帧（第一帧 ImGui 还在量窗口尺寸，导出来是空的）。</summary>
    private static void RunFrames()
    {
        // 跨帧要把"上一张卡片下缘"清掉 —— 不清的话第二帧会拿第一帧的值来比，
        // 报出一堆负的假空隙（-1732px 那种）。
            // **要跑两三帧。** ImGui 第一帧还在量窗口尺寸，导出来是空的 ——
            // 第一次跑就是这个结果（drawList=0）。
            for (int frame = 0; frame < 3; frame++)
            {
                // **每帧都要清**（放循环外面的话，第 2、3 帧会拿上一帧的值来比，
                // 报出 -1732px 那种假空隙）。
                LastCardBottom = float.MinValue;
                ImGui.NewFrame();
                // **必须自己摆窗口。** 不摆的话 ImGui 默认放在 (60,60)，
                // 内容会跑到画布外面去、右边被裁 —— 第一次跑就是这么误报的。
                ImGui.SetNextWindowPos(new NVec2(0f, 0f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new NVec2(PanelWidth, PanelHeight), ImGuiCond.Always);
                if (ImGui.Begin("##panel", ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
                {
                    if (DecoMode)
                    {
                        ImGui.SeparatorText("装饰物");
                        DrawDecoHeader();
                        ImGui.BeginChild("##scroll", new NVec2(0f, 0f),
                            ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);
                        DrawDecoRows();
                        ImGui.EndChild();
                    }
                    else if (HeaderMode)
                    {
                        DrawHeader();
                    }
                    else if (TimelineMode)
                    {
                        DrawTimelineHeader();
                    }
                    else if (BlocksMode)
                    {
                        DrawBlocksCanvas();
                    }
                    else if (MediaMode)
                    {
                        ImGui.SeparatorText("媒体");
                        DrawMediaHeader();
                        ImGui.BeginChild("##scroll", new NVec2(0f, 0f),
                            ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);
                        DrawMediaGrid();
                        ImGui.EndChild();
                    }
                    else
                    {
                    ImGui.SeparatorText("参数");
                    // 和 Inspector 同一条判定：标签放不下、或值放不下，就换"标签独占一行"。
                    float available = ImGui.GetContentRegionAvail().X;
                    float need = 0f;
                    for (int i = 0; i < Rows.Length; i++)
                    {
                        need = MathF.Max(need, ImGui.CalcTextSize(Rows[i].Label).X);
                    }
                    float column = MathF.Min(need + LabelGap, available * 0.5f);
                    if (need + LabelGap > available * 0.5f
                        || available - EnableColumnWidth - LabelLeftGap - column < 118f)
                    {
                        DrawStackedRows();
                    }
                    else
                    {
                        DrawWideRows();
                    }
                    // 缓速下拉展开后长这样 —— 离屏环境里弹层打不开（ImGui 用控件 ID
                    // 当弹层 ID，没法从外面强制），所以平铺出来看几何。
                    ImGui.SeparatorText("缓速选项（展开后的样子）");
                    string[] easeOptions = { "Linear", "In Quad", "Out Cubic", "In Out Quint" };
                    for (int k = 0; k < easeOptions.Length; k++)
                    {
                        DrawEaseOption(easeOptions[k], k == 2);
                    }
                    }
                }
                ImGui.End();
                ImGui.Render();
            }
    }

    private static void DrawWideRows()
    {
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        float labelColumn = 0f;
        for (int i = 0; i < Rows.Length; i++)
        {
            labelColumn = MathF.Max(labelColumn, ImGui.CalcTextSize(Rows[i].Label).X);
        }
        labelColumn = MathF.Min(labelColumn + LabelGap,
            ImGui.GetContentRegionAvail().X * 0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
            new NVec2(ImGui.GetStyle().ItemSpacing.X, RowSpacing));
        for (int i = 0; i < Rows.Length; i++)
        {
            Row row = Rows[i];
            ImGui.PushID(i);

            float contentLeft = ImGui.GetCursorPosX();
            float cardWidth = ImGui.GetContentRegionAvail().X;
            float left = contentLeft + CardPad;
            float rowRight = contentLeft + cardWidth - CardPad;
            NVec2 cardMin = ImGui.GetCursorScreenPos();
            cardMin.Y -= CardPad;
            CheckCardGap(row.Label, cardMin.Y);

            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);

            ImGui.SetCursorPosX(left);
            CheckRowLeft(row.Label, ImGui.GetCursorScreenPos().X, cardMin.X);
            float toggleRight = 0f;
            if (row.CanDisable)
            {
                bool on = true;
                ImGui.Checkbox("##enable", ref on);
                toggleRight = ImGui.GetItemRectMax().X;
                ImGui.SameLine(left + EnableColumnWidth + LabelLeftGap);
            }
            else
            {
                ImGui.SetCursorPosX(left + EnableColumnWidth + LabelLeftGap);
            }
            CheckLabelClear(row.Label, ImGui.GetCursorScreenPos().X, toggleRight);

            NVec2 labelMin = ImGui.GetCursorScreenPos();
            draw.PushClipRect(labelMin,
                new NVec2(labelMin.X + labelColumn, labelMin.Y + ImGui.GetTextLineHeight()), true);
            ImGui.TextUnformatted(row.Label);
            draw.PopClipRect();
            ImGui.SameLine(left + EnableColumnWidth + labelColumn);

            if (row.Checkbox)
            {
                bool value = true;
                ImGui.Checkbox("##value", ref value);
            }
            else if (row.Swatch)
            {
                ImGui.ColorButton("##value", new NVec4(1f, 1f, 1f, 1f),
                    ImGuiColorEditFlags.NoTooltip, new NVec2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight()));
                ImGui.SameLine();
                ImGui.TextUnformatted(row.Value);
            }
            // 下面几个分支的宽度一律按 rowRight 算（卡片右缘），不是窗口内容区右缘。
            else if (row.Combo)
            {
                ImGuiStylePtr st = ImGui.GetStyle();
                st.Colors[(int)ImGuiCol.Button] = st.Colors[(int)ImGuiCol.FrameBg];
                st.Colors[(int)ImGuiCol.ButtonHovered] = RaisedHover;
                ImGui.SetNextItemWidth(MathF.Max(1f, rowRight - ImGui.GetCursorPosX()));
                if (ImGui.BeginCombo("##value", row.Value))
                {
                    // 缓速那种枚举：每一项左边画一条曲线。这里用一条合成的 S 形
                    // 来验几何（生产里用的是 SampleEaseCurve 的真曲线）。
                    string[] options = row.Label == "缓速"
                        ? new[] { "Linear", "In Quad", "Out Cubic", "In Out Quint" }
                        : new[] { "单独", "标准", "霓虹" };
                    bool curved = row.Label == "缓速";
                    for (int k = 0; k < options.Length; k++)
                    {
                        if (curved) DrawEaseOption(options[k], k == 2);
                        else ImGui.Selectable(options[k], k == 1);
                    }
                    ImGui.EndCombo();
                }
            }
            else
            {
                const float UnitWidth = 40f;
                const float UnitGap = 6f;
                ImGui.SetNextItemWidth(MathF.Max(1f, rowRight - ImGui.GetCursorPosX()
                    - (string.IsNullOrEmpty(row.Unit) ? 0f : UnitWidth + UnitGap)));
                string buffer = row.Value;
                ImGui.InputText("##value", ref buffer, 64);
                if (!string.IsNullOrEmpty(row.Unit))
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted(row.Unit);
                }
            }

            float contentTop = cardMin.Y + CardPad;
            float contentHeight = MathF.Max(
                ImGui.GetItemRectMax().Y - contentTop, ImGui.GetFrameHeight());
            NVec2 cardMax = new NVec2(
                cardMin.X + cardWidth, contentTop + contentHeight + CardPad);

            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(cardMin, cardMax,
                ImGui.GetColorU32(ImGui.IsMouseHoveringRect(cardMin, cardMax) ? RaisedHover : Raised),
                CornerSmall);
            draw.ChannelsMerge();

            CheckRowFit(row.Label, cardMax);
            if (Sweep) Console.WriteLine("   " + row.Label + " 值="
                + ImGui.GetItemRectMin().X.ToString("0") + ".."
                + ImGui.GetItemRectMax().X.ToString("0")
                + " 卡片右=" + (cardMax.X - CardPad).ToString("0"));

            ImGui.PopID();
        }
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// 缓速选项：占位 Selectable + 自画的曲线和文字。几何照抄 Inspector 的
    /// DrawEaseOption（曲线起点 20、宽 44、高 21，文字从 72 开始）。
    /// </summary>
    private static void DrawEaseOption(string label, bool selected)
    {
        const float CurveLeft = 20f, CurveWidth = 44f, CurveHeight = 21f;
        float rowHeight = MathF.Max(ImGui.GetFrameHeight(), CurveHeight + 6f);
        if (ImGui.Selectable("##opt" + label, selected, ImGuiSelectableFlags.None, new NVec2(0f, rowHeight)))
        {
        }

        NVec2 min = ImGui.GetItemRectMin();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        NVec2[] points = new NVec2[25];
        float top = min.Y + (rowHeight - CurveHeight) * 0.5f;
        for (int i = 0; i < points.Length; i++)
        {
            float t = i / (float)(points.Length - 1);
            float y = t * t * (3f - 2f * t);          // 合成一条 S 形，只看位置
            points[i] = new NVec2(min.X + CurveLeft + t * CurveWidth, top + CurveHeight - y * CurveHeight);
        }
        draw.PushClipRect(new NVec2(min.X, min.Y),
            new NVec2(min.X + CurveLeft + CurveWidth, min.Y + rowHeight), true);
        draw.AddPolyline(ref points[0], points.Length,
            ImGui.GetColorU32(TextSecondary), ImDrawFlags.None, 1f);
        draw.PopClipRect();
        draw.AddText(new NVec2(min.X + CurveLeft + CurveWidth + 8f,
            min.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(TextPrimary), label);
    }

    /// <summary>
    /// 窄面板：**标签一行、控件下一行**，每块一张卡片。几何照抄 Inspector。
    /// </summary>
    private static void DrawStackedRows()
    {
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
            new NVec2(ImGui.GetStyle().ItemSpacing.X, RowSpacing));
        for (int i = 0; i < Rows.Length; i++)
        {
            Row row = Rows[i];
            ImGui.PushID(i);

            NVec2 cardMin = ImGui.GetCursorScreenPos();
            cardMin.Y -= CardPad;
            CheckCardGap(row.Label, cardMin.Y);
            float cardLeft = ImGui.GetCursorPosX();
            float cardWidth = ImGui.GetContentRegionAvail().X;
            float blockRight = cardLeft + cardWidth - CardPad;

            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);

            // 和 Inspector 的窄排法**逐句对齐** —— 少一句缩进就会"镜子照着好看、
            // 本体贴边"，上一版就是这么漏掉一次。
            ImGui.SetCursorPosX(cardLeft + CardPad);
            CheckRowLeft(row.Label, ImGui.GetCursorScreenPos().X, cardMin.X);
            float toggleRight2 = 0f;
            if (row.CanDisable)
            {
                bool on = true;
                ImGui.Checkbox("##enable", ref on);
                toggleRight2 = ImGui.GetItemRectMax().X;
                ImGui.SameLine(0f, LabelLeftGap);
            }
            CheckLabelClear(row.Label, ImGui.GetCursorScreenPos().X, toggleRight2);
            ImGui.TextUnformatted(row.Label);

            ImGui.SetCursorPosX(cardLeft + CardPad);
            if (row.Checkbox)
            {
                bool value = true;
                ImGui.Checkbox("##value", ref value);
            }
            else
            {
                const float UnitWidth = 40f, UnitGap = 6f;
                ImGui.SetNextItemWidth(MathF.Max(1f, blockRight - ImGui.GetCursorPosX()
                    - (string.IsNullOrEmpty(row.Unit) ? 0f : UnitWidth + UnitGap)));
                string buffer = row.Value;
                ImGui.InputText("##value", ref buffer, 64);
                if (!string.IsNullOrEmpty(row.Unit))
                {
                    ImGui.SameLine(0f, UnitGap);
                    ImGui.TextUnformatted(row.Unit);
                }
            }

            NVec2 cardMax = new NVec2(cardMin.X + cardWidth, ImGui.GetItemRectMax().Y + CardPad);
            LastCardBottom = cardMax.Y;
            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(cardMin, cardMax,
                ImGui.GetColorU32(ImGui.IsMouseHoveringRect(cardMin, cardMax) ? RaisedHover : Raised),
                CornerSmall);
            draw.ChannelsMerge();

            CheckRowFit(row.Label, cardMax);
            ImGui.PopID();
        }
        ImGui.PopStyleVar();
    }

    private static readonly string[,] DecoRows =
    {
        { "bg1.png", "第 2 格" },
        { "png12.png", "第 4 格" },
        { "a_stabe.png", "第 6 格  cycle1" },
        { "STAR_Source.png", "第 9 格  已隐藏" },
        { "1_tag.png", "第 12 格  已锁定" },
        { "1_tag.png", "第 14 格" }
    };

    /// <summary>
    /// 装饰物列表的行 —— 几何照抄 EditorImGuiDecorationList.DrawRows：
    /// 整行一个 Selectable，右边三个 30x22 的小按钮，中间三道 4px 的缝。
    /// </summary>
    /// <summary>
    /// 装饰物列表头部 —— 几何照抄 EditorImGuiDecorationList.DrawHeader：
    /// 四个正方形的新建方块 + 计数、整宽的搜索框、两个批量开关。
    ///
    /// **方块做正方的**（原来 48x24 是个长条，图标塞进去上下顶满，就是"扁"）。
    /// </summary>
    private static void DrawDecoHeader()
    {
        string[] names = { "图片", "文本", "粒子", "对象" };
        NVec4[] colors =
        {
            Hex("38bdf8"), Hex("4ade80"), Hex("fb7185"), Hex("fbbf24")
        };
        const float Tile = 32f, TileGap = 8f;
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        for (int i = 0; i < names.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0f, TileGap);
            // 生产环境这里是空标签 + 把图标画上去（`##create0`）。预览没有游戏那套
            // 图标，就退回**单字**兜底 —— 和生产环境"原版图标拿不到"时的表现一致。
            ImGui.Button("##create" + i, new NVec2(Tile, Tile));
            NVec2 min = ImGui.GetItemRectMin();
            string one = names[i].Substring(0, 1);
            NVec2 ext = ImGui.CalcTextSize(one);
            draw.AddText(new NVec2(min.X + (Tile - ext.X) * 0.5f, min.Y + (Tile - ext.Y) * 0.5f),
                ImGui.GetColorU32(colors[i]), one);
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Hex("fbbf24"));
        ImGui.TextUnformatted("6");
        ImGui.PopStyleColor();

        ImGui.SetNextItemWidth(-1f);
        string search = string.Empty;
        ImGui.InputTextWithHint("##deco_search", "搜索（type: / tag: / floor:）", ref search, 128);

        // 批量开关和方块同高，免得三行控件高矮不一。
        ImGui.Button("全部隐藏", new NVec2(0f, Tile));
        ImGui.SameLine();
        ImGui.Button("全部锁定", new NVec2(0f, Tile));
    }

    /// <summary>按像素宽度裁文字，放不下就结尾加省略号（比硬切掉半个字干净）。</summary>
    private static string FitText(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text.Substring(0, mid) + "…").X <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo > 0 ? text.Substring(0, lo) + "…" : string.Empty;
    }

    private static void DrawDecoRows()
    {
        // 和 EditorImGuiDecorationList 用同一套数：
        //   卡片内边距 12 < 卡片之间 16 —— **组内必须小于组外**，反了就是"挤"。
        //   按钮正方，不扁；右边留出和内边距同宽的空白，不贴卡片边。
        const float CardPad = 12f, CardGap = 16f;
        const float TextLine = 24f;

        // **按可用宽度分两档，不按比例缩。** 大件要缩得比小件快 ——
        // 面板最窄能到 200，那时三个 32 的按钮会把文件名挤到只剩五个字。
        float avail0 = ImGui.GetContentRegionAvail().X;
        bool roomy = avail0 - CardPad * 2f >= 240f;
        float BtnSize = roomy ? 32f : 24f;
        float BtnGap = roomy ? 8f : 4f;
        float contentH = MathF.Max(BtnSize, TextLine);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        for (int i = 0; i < DecoRows.GetLength(0); i++)
        {
            ImGui.PushID(i);
            NVec2 card = ImGui.GetCursorScreenPos();
            float avail = ImGui.GetContentRegionAvail().X;
            NVec2 cardMax = new NVec2(card.X + avail, card.Y + CardPad * 2f + contentH);

            NVec2 cMin = new NVec2(card.X + CardPad, card.Y + CardPad);
            float cW = avail - CardPad * 2f;
            float buttonArea = 3f * BtnSize + 2f * BtnGap;
            float textW = MathF.Max(40f, cW - buttonArea - BtnGap);

            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);

            bool selected = i == 2;
            ImGui.PushStyleColor(ImGuiCol.Header, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new NVec4(1f, 1f, 1f, 0.04f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new NVec4(0f, 0f, 0f, 0f));
            ImGui.SetCursorScreenPos(cMin);
            ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None,
                new NVec2(textW, contentH));
            ImGui.PopStyleColor(3);

            // 只有名字这一行（"第 N 格 · 已隐藏"那行已经去掉），垂直居中。
            string title = FitText(DecoRows[i, 0], textW);
            NVec2 titleSize = ImGui.CalcTextSize(title);
            draw.AddText(new NVec2(cMin.X, cMin.Y + (contentH - titleSize.Y) * 0.5f),
                ImGui.GetColorU32(selected ? Hex("38bdf8") : TextPrimary), title);

            float btnY = cMin.Y + (contentH - BtnSize) * 0.5f;
            float btnX = cMin.X + cW - buttonArea;
            for (int b = 0; b < 3; b++)
            {
                ImGui.SetCursorScreenPos(new NVec2(btnX, btnY));
                ImGui.Button("##b" + b, new NVec2(BtnSize, BtnSize));
                btnX += BtnSize + BtnGap;
            }
            float lastRight = ImGui.GetItemRectMax().X;

            ImGui.SetCursorScreenPos(new NVec2(card.X, cardMax.Y + CardGap));
            ImGui.Dummy(new NVec2(0f, 0f));

            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(card, cardMax,
                ImGui.GetColorU32(selected ? new NVec4(0.22f, 0.74f, 0.97f, 0.15f) : Raised),
                CornerSmall);
            draw.ChannelsMerge();

            CheckRowFit(DecoRows[i, 0], new NVec2(cardMax.X - CardPad, cardMax.Y - CardPad));
            CheckCardGap(DecoRows[i, 0], card.Y);
            LastCardBottom = cardMax.Y;
            if (Sweep) Console.WriteLine("   " + DecoRows[i, 0]
                + "  档=" + (roomy ? "宽" : "窄")
                + "  文字宽=" + textW.ToString("0")
                + "  行尾=" + lastRight.ToString("0")
                + "  卡片右=" + (cardMax.X - CardPad).ToString("0")
                + "  卡高=" + (cardMax.Y - card.Y).ToString("0"));
            ImGui.PopID();
        }
    }

    /// <summary>媒体页的示例文件名。真实数据来自谱面目录，预览里编一份就够看排布。</summary>
    private static readonly string[] MediaNames =
    {
        "bg_sky.png", "bg_night.jpg", "cloud01.png", "star_field.png",
        "STAR_Source.png", "particle_dot.png", "glow_soft.png", "p1_bg1.png",
        "p6_yhsz1.png", "frame_gold.png", "hline_thin.png", "vignette.png",
        "noise_tile.png", "flare_01.png", "ring_soft.png", "arrow_white.png",
        "icon_lock.png", "icon_eye.png", "wave.png", "spark.png"
    };

    /// <summary>
    /// 媒体页头部 —— 几何照抄 EditorImGuiMediaPanel.DrawHeader：
    /// [留白][搜索框][计数][刷新][留白]，计数右对齐在它自己那一格里。
    /// </summary>
    private static void DrawMediaHeader()
    {
        const float RowH = 24f, Inset = 12f, CountW = 48f, RefreshW = 44f, Gap = 12f;
        float avail = ImGui.GetContentRegionAvail().X;
        float searchWidth = MathF.Max(40f, avail - Inset * 2f - RefreshW - CountW - Gap * 2f);

        NVec2 row = ImGui.GetCursorScreenPos();
        float y = row.Y;

        ImGui.SetCursorScreenPos(new NVec2(row.X + Inset, y));
        ImGui.SetNextItemWidth(searchWidth);
        string search = string.Empty;
        ImGui.InputTextWithHint("##media_search", "搜索文件名", ref search, 128);

        string countText = MediaNames.Length.ToString();
        NVec2 countSize = ImGui.CalcTextSize(countText);
        ImGui.SetCursorScreenPos(new NVec2(
            row.X + Inset + searchWidth + Gap + CountW - countSize.X, y));
        ImGui.TextColored(Hex("fbbf24"), countText);

        ImGui.SetCursorScreenPos(new NVec2(row.X + avail - Inset - RefreshW, y));
        ImGui.Button("刷新", new NVec2(RefreshW, RowH));

        ImGui.SetCursorScreenPos(new NVec2(row.X, y + RowH));
        ImGui.Dummy(new NVec2(0f, 0f));
    }

    /// <summary>
    /// 媒体网格 —— 几何照抄 EditorImGuiMediaPanel.DrawGrid。
    /// 缩略图在预览里画不出来（真实数据是磁盘上的图），一律画凹槽占位。
    /// </summary>
    private static void DrawMediaGrid()
    {
        const float GridPad = 10f, ColGap = 6f, RowGap = 8f, CardInset = 4f;
        const float MinColW = 56f, LabelH = 18f, CardH = 112f;
        const int MaxCols = 6;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        NVec2 origin = ImGui.GetCursorScreenPos();
        float viewportWidth = ImGui.GetContentRegionAvail().X;

        int columns = Math.Clamp(
            (int)MathF.Floor((viewportWidth - GridPad * 2f + ColGap) / (MinColW + ColGap)),
            1, MaxCols);
        float cardWidth = (viewportWidth - GridPad * 2f - ColGap * (columns - 1)) / columns;
        float cardHeight = CardH - RowGap;
        float labelTop = CardH - RowGap - LabelH;
        float thumb = MathF.Max(16f, MathF.Min(cardWidth - CardInset * 2f, labelTop - CardInset));

        int selected = 2;
        for (int i = 0; i < MediaNames.Length; i++)
        {
            int rowIndex = i / columns;
            int columnIndex = i % columns;
            NVec2 min = new NVec2(origin.X + GridPad + columnIndex * (cardWidth + ColGap),
                                  origin.Y + GridPad + rowIndex * CardH);
            NVec2 max = new NVec2(min.X + cardWidth, min.Y + cardHeight);
            bool isSelected = i == selected;

            ImGui.PushID(i);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##cell", new NVec2(cardWidth, cardHeight));
            bool hovered = ImGui.IsItemHovered();
            ImGui.PopID();

            draw.AddRectFilled(min, max,
                ImGui.GetColorU32(isSelected ? new NVec4(0.22f, 0.74f, 0.97f, 0.15f)
                                             : (hovered ? SelectedHover : Raised)),
                CornerSmall);

            NVec2 thumbMin = new NVec2(min.X + (cardWidth - thumb) * 0.5f, min.Y + CardInset);
            draw.AddRectFilled(thumbMin, new NVec2(thumbMin.X + thumb, thumbMin.Y + thumb),
                ImGui.GetColorU32(ThumbWell), CornerSmall);

            string label = FitText(MediaNames[i], cardWidth - CardInset * 2f);
            NVec2 labelSize = ImGui.CalcTextSize(label);
            draw.AddText(new NVec2(min.X + (cardWidth - labelSize.X) * 0.5f, min.Y + labelTop),
                ImGui.GetColorU32(isSelected ? TextPrimary : TextSecondary), label);

            if (Sweep)
            {
                Console.WriteLine("   " + MediaNames[i] + "  格宽=" + cardWidth.ToString("0")
                    + " 缩略=" + thumb.ToString("0")
                    + " 左=" + (min.X - origin.X).ToString("0")
                    + " 右=" + (max.X - origin.X).ToString("0"));
            }
        }

        int rows = (MediaNames.Length + columns - 1) / columns;
        if (Sweep) Console.WriteLine("   列数=" + columns + " 视口宽=" + viewportWidth.ToString("0"));
        ImGui.SetCursorScreenPos(new NVec2(origin.X, origin.Y + GridPad * 2f + rows * CardH));
        ImGui.Dummy(new NVec2(0f, 0f));
    }

    /// <summary>
    /// 字形范围：**直接读生成的 UiGlyphTable.cs**，和生产同一个来源。
    ///
    /// 原来用的是 ImGui 自带的 GetGlyphRangesChineseSimplifiedCommon —— 那是另一批
    /// 字，界面文案里的字（比如"媒"）它不一定有，于是预览图里显示成问号，让人分不清
    /// 是"字表真的缺字"还是"预览工具的锅"。这个坑踩过一次。
    /// </summary>
    private static ushort[] BuildGlyphRange()
    {
        SortedSet<ushort> codes = new SortedSet<ushort>();
        for (char c = (char)0x20; c < (char)0x7F; c++)
        {
            codes.Add(c);
        }

        // bin/Debug/net8.0 往上五层就是项目根
        string table = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "UiGlyphTable.cs"));
        if (File.Exists(table))
        {
            string text = File.ReadAllText(table);
            bool inString = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString && c != 0) { codes.Add(c); }
            }
        }
        else
        {
            Console.Error.WriteLine("警告：读不到 UiGlyphTable.cs（" + table
                + "），预览里的汉字可能显示成问号");
        }

        List<ushort> pairs = new List<ushort>();
        bool first = true;
        ushort start = 0, last = 0;
        foreach (ushort code in codes)
        {
            if (first) { start = last = code; first = false; continue; }
            if (code == last + 1) { last = code; continue; }
            pairs.Add(start);
            pairs.Add(last);
            start = last = code;
        }
        if (!first) { pairs.Add(start); pairs.Add(last); }
        pairs.Add(0);
        return pairs.ToArray();
    }

    /// <summary>
    /// 把范围表拷进非托管内存。**不能直接 fixed** —— ImGui 要到建图集时才去读它，
    /// 那时 fixed 的块早退出了，托管数组可能已经被 GC 搬走。
    /// </summary>
    private static IntPtr AllocRange(ushort[] range)
    {
        IntPtr pointer = Marshal.AllocHGlobal(range.Length * sizeof(ushort));
        byte[] bytes = new byte[range.Length * sizeof(ushort)];
        Buffer.BlockCopy(range, 0, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    /// <summary>
    /// 工作台顶栏 —— 几何照抄 EditorImGuiHeader.DrawContents。
    ///
    /// **它验的就是"槽位有没有撞上"**：原顶栏是三档字号，ImGui 统一成一档之后
    /// 那串按 13px 算的坐标（620 / 1540 / 1656 / 1920-230）全得重算，
    /// 撞没撞只有画出来才看得见。
    /// </summary>
    private static void DrawHeader()
    {
        const float SidePadding = 24f, TextGap = 16f;
        const float RuleX = 24f, RuleWidth = 3f, RuleHeight = 12f;
        const float StatusX = 620f;
        const float MultiplayerWidth = 96f, ActionHeight = 32f, HelpSize = 34f;
        const float VersionHitHeight = 36f;

        float width = PanelWidth;
        float height = 54f;

        // **必须自己摆窗口。** 不摆的话 ImGui 按默认级联位置摆，
        // 而内容全是绝对坐标 —— 会被窗口自己的裁剪矩形切掉，只剩几个碰巧落在里面的。
        ImGui.SetNextWindowPos(new NVec2(0f, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, HeaderBg);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
        if (ImGui.Begin("##header", ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float middle = height * 0.5f;
            float textY = (height - ImGui.GetTextLineHeight()) * 0.5f;

            draw.AddRectFilled(new NVec2(RuleX, middle - RuleHeight * 0.5f),
                new NVec2(RuleX + RuleWidth, middle + RuleHeight * 0.5f),
                ImGui.GetColorU32(Hex("fbbf24")), 1f);

            float cursor = RuleX + RuleWidth + 8f;
            string title = "ADOFAI  EDITOR STUDIO";
            NVec2 titleSize = ImGui.CalcTextSize(title);
            draw.AddText(new NVec2(cursor, textY), ImGui.GetColorU32(TextPrimary), title);
            cursor += titleSize.X + TextGap;

            string version = "v0.292";
            NVec2 versionSize = ImGui.CalcTextSize(version);
            ImGui.PushStyleColor(ImGuiCol.Button, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new NVec4(1f, 1f, 1f, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new NVec4(1f, 1f, 1f, 0.10f));
            ImGui.SetCursorScreenPos(new NVec2(cursor - 4f, (height - VersionHitHeight) * 0.5f));
            bool versionHovered = ImGui.Button("##version",
                new NVec2(versionSize.X + 8f, VersionHitHeight));
            versionHovered = ImGui.IsItemHovered();
            ImGui.PopStyleColor(3);
            NVec4 linkColor = versionHovered ? TextPrimary : Hex("6BA8FF");
            draw.AddText(new NVec2(cursor, textY), ImGui.GetColorU32(linkColor), version);
            float underlineY = textY + titleSize.Y - 2f;
            draw.AddLine(new NVec2(cursor, underlineY),
                new NVec2(cursor + versionSize.X, underlineY), ImGui.GetColorU32(linkColor), 1f);
            cursor += versionSize.X + TextGap;

            draw.AddText(new NVec2(cursor, textY), ImGui.GetColorU32(TextMuted), "剪辑器工作台");

            // 状态读数：位置钉死，底衬按文字实际宽度贴
            string status = "深度 +120";
            NVec2 statusSize = ImGui.CalcTextSize(status);
            float statusLeft = StatusX;
            draw.AddRectFilled(new NVec2(statusLeft - 10f, middle - ActionHeight * 0.5f),
                new NVec2(statusLeft + statusSize.X + 10f, middle + ActionHeight * 0.5f),
                ImGui.GetColorU32(new NVec4(1f, 1f, 1f, 0.06f)), CornerSmall);
            draw.AddText(new NVec2(statusLeft, textY), ImGui.GetColorU32(Hex("4ade80")), status);

            float helpX = width - SidePadding - HelpSize;
            float multiplayerX = helpX - 12f - MultiplayerWidth;
            string shortcut = "F8  工作台  ·  Esc  关闭";
            NVec2 shortcutSize = ImGui.CalcTextSize(shortcut);
            draw.AddText(new NVec2(multiplayerX - 24f - shortcutSize.X, textY),
                ImGui.GetColorU32(TextMuted), shortcut);

            ImGui.SetCursorScreenPos(new NVec2(multiplayerX, (height - ActionHeight) * 0.5f));
            ImGui.Button("联机", new NVec2(MultiplayerWidth, ActionHeight));
            ImGui.SetCursorScreenPos(new NVec2(helpX, (height - HelpSize) * 0.5f));
            ImGui.Button("?", new NVec2(HelpSize, HelpSize));

            if (Sweep)
            {
                Console.WriteLine("   标题尾=" + (cursor - TextGap).ToString("0")
                    + "  状态左=" + statusLeft.ToString("0")
                    + "  状态右=" + (statusLeft + statusSize.X + 10f).ToString("0")
                    + "  快捷键左=" + (multiplayerX - 24f - shortcutSize.X).ToString("0")
                    + "  联机左=" + multiplayerX.ToString("0")
                    + "  帮助右=" + (helpX + HelpSize).ToString("0")
                    + "  画布宽=" + width.ToString("0"));
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// 时间轴标题行 —— 几何照抄 EditorImGuiTimelineHeader.DrawContents。
    /// 模式开关的图标是运行期造出来的 Sprite（预览里拿不到），一律画占位方块。
    /// </summary>
    private static void DrawTimelineHeader()
    {
        const float BarX = 18f, BarY = 14f, BarW = 3f, BarH = 16f, TitleGap = 8f;
        const float ToggleX = 130f, ToggleW = 32f, ToggleH = 29f, ToggleGap = 6f;
        const float IconX = 5f, IconY = 6f, IconW = 18f, IconH = 16f;
        const float RightPadding = 16f, StepSize = 28f, RewindW = 32f;

        float width = PanelWidth;
        float height = 42f;
        string[] toggleNames = { "排列", "跟随", "选中跟随", "拖动吸附", "视图锁" };

        ImGui.SetNextWindowPos(new NVec2(0f, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
        if (ImGui.Begin("##timeline", ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float textY = (height - ImGui.GetTextLineHeight()) * 0.5f;

            draw.AddRectFilled(new NVec2(0f, 0f), new NVec2(width, height),
                ImGui.GetColorU32(Hex("1A1F27")), CornerSmall);
            draw.AddRectFilled(new NVec2(BarX, BarY), new NVec2(BarX + BarW, BarY + BarH),
                ImGui.GetColorU32(Hex("fbbf24")), 1f);
            draw.AddText(new NVec2(BarX + BarW + TitleGap, textY),
                ImGui.GetColorU32(Hex("fbbf24")), "时间轴");

            float buttonY = (height - ToggleH) * 0.5f;
            for (int i = 0; i < toggleNames.Length; i++)
            {
                float buttonX = ToggleX + i * (ToggleW + ToggleGap);
                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));
                ImGui.Button("##t" + i, new NVec2(ToggleW, ToggleH));
                // 图标占位（真图是运行期造的 Sprite）
                NVec4 tint = i == 3 ? Hex("fbbf24") : TextPrimary;
                draw.AddRectFilled(new NVec2(buttonX + IconX, buttonY + IconY),
                    new NVec2(buttonX + IconX + IconW, buttonY + IconY + IconH),
                    ImGui.GetColorU32(new NVec4(tint.X, tint.Y, tint.Z, 0.85f)), 2f);
            }

            float toggleRight = ToggleX + toggleNames.Length * (ToggleW + ToggleGap);

            // 右侧那组：回到开头 | 缩放 - 读数 +
            float right = width - RightPadding;
            float zoomInX = right - StepSize;
            float zoomOutX = zoomInX - 4f - StepSize;
            float rewindX = zoomOutX - 16f - RewindW;

            ImGui.SetCursorScreenPos(new NVec2(rewindX, buttonY));
            ImGui.Button("|<", new NVec2(RewindW, ToggleH));
            ImGui.SetCursorScreenPos(new NVec2(zoomOutX, buttonY));
            ImGui.Button("-", new NVec2(StepSize, ToggleH));
            ImGui.SetCursorScreenPos(new NVec2(zoomInX, buttonY));
            ImGui.Button("+", new NVec2(StepSize, ToggleH));

            string zoom = "100%";
            NVec2 zoomSize = ImGui.CalcTextSize(zoom);
            draw.AddText(new NVec2(rewindX - 12f - zoomSize.X, textY),
                ImGui.GetColorU32(TextMuted), zoom);

            string time = "00:12.345 / 01:30.000";
            NVec2 timeSize = ImGui.CalcTextSize(time);
            draw.AddText(new NVec2(width - 200f - timeSize.X, textY),
                ImGui.GetColorU32(Hex("fbbf24")), time);

            string summary = "事件 128 · 格 1/64 · 相机 · 视效 · 装饰 三车道";
            float sLeft = toggleRight + 16f;
            float sRight = width - 200f - 16f;
            draw.AddText(new NVec2(sLeft, textY), ImGui.GetColorU32(TextMuted),
                FitText(summary, MathF.Max(40f, sRight - sLeft)));

            if (Sweep)
            {
                Console.WriteLine("   开关右=" + toggleRight.ToString("0")
                    + "  摘要左=" + sLeft.ToString("0") + "  摘要右=" + sRight.ToString("0")
                    + "  时间左=" + (width - 200f - timeSize.X).ToString("0")
                    + "  |<左=" + rewindX.ToString("0")
                    + "  缩放读数左=" + (zoomOutX - 8f - zoomSize.X).ToString("0")
                    + "  画布宽=" + width.ToString("0"));
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// 自由画布的示意：标尺 + 事件块，块按行叠着（披棉被）。
    /// 数据是编的 —— 真实数据要开着游戏才有，这里验的是**排法和分层效果**。
    /// </summary>
    private static void DrawBlocksCanvas()
    {
        // 和 EditorTimelineView 里同一组数
        const float BlockStride = 22f, BlockHeight = 32f, RulerHeight = 28f;
        const float LabelMinBlockWidth = 46f, MinBlockWidth = 8f;
        const float PixelsPerSecond = 90f;
        const float ViewStart = 1.0f;

        float width = PanelWidth, height = PanelHeight;
        // 编一组块：(行, 时间, 时长, 名字, 颜色索引)
        float[,] raw =
        {
            { 0, 1.02f, 0.35f }, { 1, 1.10f, 0.60f }, { 2, 1.30f, 0.25f },
            { 3, 1.45f, 0.80f }, { 4, 1.55f, 0.40f }, { 5, 1.70f, 0.55f },
            { 6, 1.85f, 0.30f }, { 7, 2.00f, 1.10f }, { 8, 2.15f, 0.45f },
            { 9, 2.30f, 0.35f }, { 10, 2.45f, 0.70f }, { 11, 2.60f, 0.25f },
            { 12, 2.75f, 0.90f }, { 13, 2.90f, 0.40f }, { 14, 3.05f, 0.55f },
            { 15, 3.20f, 0.30f }, { 16, 3.35f, 1.00f }, { 17, 3.50f, 0.45f },
            { 18, 3.65f, 0.35f }, { 19, 3.80f, 0.60f }, { 20, 3.95f, 0.28f },
            { 21, 4.10f, 0.75f }, { 22, 4.25f, 0.50f }, { 23, 4.40f, 0.33f }
        };
        NVec4[] typeColors =
        {
            Hex("38bdf8"), Hex("4ade80"), Hex("fb7185"), Hex("fbbf24"), Hex("a78bfa")
        };
        string[] names =
        {
            "MoveCamera", "SetFilter", "MoveTrack", "AddDecoration", "PlaySound",
            "SetSpeed", "EditorComment", "Twirl", "SetHitsound", "Flash"
        };

        ImGui.SetNextWindowPos(new NVec2(0f, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
        if (ImGui.Begin("##blocks", ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(new NVec2(0f, 0f), new NVec2(width, height),
                ImGui.GetColorU32(Hex("20252E")), CornerSmall);

            // 格线
            for (float t = 1.0f; t < ViewStart + width / PixelsPerSecond; t += 0.25f)
            {
                float gx = (t - ViewStart) * PixelsPerSecond;
                draw.AddLine(new NVec2(gx, RulerHeight), new NVec2(gx, height),
                    ImGui.GetColorU32(new NVec4(1f, 1f, 1f, 0.10f)), 1f);
                if (MathF.Abs(t - MathF.Round(t)) < 0.001f)
                {
                    draw.AddText(new NVec2(gx + 4f, (RulerHeight - ImGui.GetTextLineHeight()) * 0.5f),
                        ImGui.GetColorU32(TextMuted), ((int)MathF.Round(t)).ToString());
                }
            }

            for (int i = 0; i < raw.GetLength(0); i++)
            {
                float row = raw[i, 0], time = raw[i, 1], dur = raw[i, 2];
                float left = (time - ViewStart) * PixelsPerSecond;
                float blockWidth = MathF.Max(MinBlockWidth, dur * PixelsPerSecond);
                float top = RulerHeight + row * BlockStride;
                NVec2 min = new NVec2(left, top);
                NVec2 max = new NVec2(left + blockWidth, top + BlockHeight);

                NVec4 color = typeColors[i % typeColors.Length];
                draw.AddRectFilled(min, max, ImGui.GetColorU32(color), CornerSmall);

                if (blockWidth >= LabelMinBlockWidth)
                {
                    string label = FitText(names[i % names.Length], blockWidth - 8f);
                    draw.PushClipRect(min, max, true);
                    draw.AddText(new NVec2(min.X + 4f,
                        min.Y + (BlockHeight - ImGui.GetTextLineHeight()) * 0.5f),
                        ImGui.GetColorU32(new NVec4(0.05f, 0.08f, 0.12f, 1f)), label);
                    draw.PopClipRect();
                }
            }

            if (Sweep)
            {
                Console.WriteLine("   块数=" + raw.GetLength(0)
                    + "  行距=" + BlockStride + " 块高=" + BlockHeight
                    + " 叠=" + (BlockHeight - BlockStride)
                    + "  内容高=" + (raw.GetLength(0) * BlockStride).ToString("0")
                    + "  视口高=" + height.ToString("0"));
            }
        }
        ImGui.End();
    }

    private static void BuildFont(ImGuiIOPtr io)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "UiFont.ttf");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("找不到 UiFont.ttf：" + path);
            Environment.Exit(1);
        }
        unsafe
        {
            ImFontConfigPtr cfg = ImGuiNative.ImFontConfig_ImFontConfig();
            cfg.SizePixels = BodyFontPixels * Scale;
            cfg.OversampleH = 1;
            cfg.OversampleV = 1;
            cfg.PixelSnapH = true;
            // **字形范围直接读生成的 UiGlyphTable.cs**，和生产同一个来源。
            //
            // 原来用的是 ImGui 自带的 GetGlyphRangesChineseSimplifiedCommon —— 那是
            // 另一批字，界面文案里的字（比如"媒"）它不一定有，于是预览图里显示成
            // 问号，让人分不清是"字表真的缺字"还是"预览工具的锅"。这个坑踩过一次。
            IntPtr range = AllocRange(BuildGlyphRange());
            ImFontPtr font = io.Fonts.AddFontFromFileTTF(
                path, BodyFontPixels * Scale, cfg, range);
            ImGuiNative.ImFontConfig_destroy(cfg);
            ((ImGuiIO*)io.NativePtr)->FontDefault = (ImFont*)font.NativePtr;
        }
        io.FontGlobalScale = 1f / Scale;

        IntPtr pixels;
        int w, h, bpp;
        io.Fonts.GetTexDataAsRGBA32(out pixels, out w, out h, out bpp);
        byte[] atlas = new byte[w * h * bpp];
        Marshal.Copy(pixels, atlas, 0, atlas.Length);
        Directory.CreateDirectory(OutDir);
        File.WriteAllBytes(Path.Combine(OutDir, "atlas.raw"), atlas);
        File.WriteAllText(Path.Combine(OutDir, "atlas.txt"), w + " " + h);
        Console.WriteLine($"字体图集 {w}x{h} bpp={bpp}");
        io.Fonts.SetTexID((IntPtr)1);
        io.Fonts.ClearTexData();
    }

    /// <summary>
    /// 皮肤：和生产那边 ApplyTheme 用的同一批数值（那边从 token 取，这里写死 ——
    /// 因为几何只跟这几个数有关，差异太大的话说明我在照抄时抄漏了）。
    /// </summary>
    private static void ApplyStyle()
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 9f;
        style.FrameRounding = CornerSmall;
        style.WindowPadding = new NVec2(16f, 12f);
        style.FramePadding = new NVec2(10f, 6f);
        style.ItemSpacing = new NVec2(8f, 8f);
        style.Colors[(int)ImGuiCol.WindowBg] = PanelColor;
        style.Colors[(int)ImGuiCol.Text] = TextPrimary;
        style.Colors[(int)ImGuiCol.FrameBg] = Hex("07080B");
        style.Colors[(int)ImGuiCol.CheckMark] = Hex("38bdf8");
        style.Colors[(int)ImGuiCol.Separator] = new NVec4(1f, 1f, 1f, 0.08f);
        style.Colors[(int)ImGuiCol.Border] = new NVec4(1f, 1f, 1f, 0.08f);
    }

    private static string OutDir => Path.Combine(AppContext.BaseDirectory, "out");

    /// <summary>把这一帧的顶点和索引导出来 —— 交给 rasterize.py 出图。</summary>
    private static unsafe void Dump(ImGuiIOPtr io)
    {
        ImDrawDataPtr data = ImGui.GetDrawData();
        Directory.CreateDirectory(OutDir);

        int verts = data.TotalVtxCount;
        int indices = data.TotalIdxCount;
        Console.WriteLine($"窗口 {PanelWidth}x{PanelHeight}  drawList={data.CmdListsCount}"
            + $"  顶点 {verts}  索引 {indices}");

        using (FileStream vtxFile = File.Create(Path.Combine(OutDir, "vtx.raw")))
        using (FileStream idxFile = File.Create(Path.Combine(OutDir, "idx.raw")))
        {
            int vertexBase = 0;
            for (int i = 0; i < data.CmdListsCount; i++)
            {
                ImDrawListPtr list = data.CmdLists[i];
                int vcount = list.VtxBuffer.Size;
                byte[] vtx = new byte[vcount * 20];
                Marshal.Copy(list.VtxBuffer.Data, vtx, 0, vtx.Length);
                vtxFile.Write(vtx, 0, vtx.Length);

                // **每个绘制列表的索引是相对它自己的顶点缓冲的**，拼成一份导出时
                // 必须按已写出的顶点数偏移 —— 不偏移的话第二个列表的三角形全指错
                // 顶点，渲染出来是斜条纹（装饰物那版有 BeginChild，正好是两个列表）。
                int icount = list.IdxBuffer.Size;
                byte[] raw = new byte[icount * 2];
                Marshal.Copy(list.IdxBuffer.Data, raw, 0, raw.Length);
                byte[] idx = new byte[icount * 2];
                for (int k = 0; k < icount; k++)
                {
                    ushort value = (ushort)(raw[k * 2] | (raw[k * 2 + 1] << 8));
                    ushort shifted = (ushort)(value + vertexBase);
                    idx[k * 2] = (byte)(shifted & 0xFF);
                    idx[k * 2 + 1] = (byte)(shifted >> 8);
                }
                idxFile.Write(idx, 0, idx.Length);
                vertexBase += vcount;
            }
        }

        ImGui.DestroyContext();
        Console.WriteLine("已写出 " + OutDir);
    }

    private static NVec4 Hex(string hex)
    {
        int r = Convert.ToInt32(hex.Substring(0, 2), 16);
        int g = Convert.ToInt32(hex.Substring(2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(4, 2), 16);
        return new NVec4(r / 255f, g / 255f, b / 255f, 1f);
    }
}
