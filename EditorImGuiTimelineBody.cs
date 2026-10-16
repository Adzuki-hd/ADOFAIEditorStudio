using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 时间轴内容区（那条卡片）的 ImGui 版 —— **目前只有标尺**。
    ///
    /// 分类表头和行条纹不画：那两样是分段卷帘模式的东西，而那个模式已经删掉了
    /// （见 `EditorTimelineView` 里 `layoutMode` 的说明）。层堆叠下事件直接坐在
    /// 卡片上，没有分类框，也就没有框内条纹。
    ///
    /// **事件块那层还没做** —— 等堆叠方案定下来。所以这一版只画标尺，
    /// 事件块仍旧由 uGUI 画在同一块区域下面。这样正好拿真块来对：标尺的格线
    /// 要和事件块的左右边缘对得上，对不上就是我的坐标换算错了。
    ///
    /// 正因为如此，这个窗口带 `NoInputs` —— 现在鼠标还得留给下面那些 uGUI
    /// 事件块。等事件块也搬过来，这个标记连同挡点击层一起加。
    /// </summary>
    internal sealed class EditorImGuiTimelineBody
    {
        private Rect _rect;

        /// <summary>
        /// 时间轴这边用的字号。**比正文小一档。**
        ///
        /// 23 像素塞进 26 高的块里上下只剩一个多像素，标尺那 28 高的条也一样顶 ——
        /// 所以图集里另带了一档小位图（ImGui 缩小画是糊的，只能多带一档）。
        /// 万一那档没建出来就退回正文，宁可大点也不要空着。
        /// </summary>
        private static ImFontPtr SmallFont
        {
            get
            {
                EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
                if (bridge == null)
                {
                    return default(ImFontPtr);
                }
                return bridge.HasSmallFont ? bridge.SmallFont : bridge.UiFont;
            }
        }

        private static float SmallFontSize
        {
            get
            {
                EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
                return bridge != null && bridge.HasSmallFont
                    ? EditorImGuiBridge.SmallFontSize : ImGui.GetFontSize();
            }
        }

        // 拖动状态。**跨帧留着** —— 按下那一帧只知道起手位置，
        // 之后每帧拿当前鼠标减它才得到位移。
        private int _dragId = -1;
        /// <summary>正在拖播放头 / 正在拉框。两者互斥，且都要跨帧记着。</summary>
        private bool _scrubbing;
        private bool _marqueeActive;
        private NVec2 _marqueeStart;
        private NVec2 _dragStartMouse;

        /// <summary>块窄到这个数以下就不写名字了 —— 几个像素里塞两个字比不写还难看。</summary>
        private const float LabelMinBlockWidth = 46f;

        /// <summary>起框时打一行 —— 框选出问题时，先分清"没起来"和"起来但没选到"。</summary>
        private static void LogMarquee(string where)
        {
            if (!VerboseMarquee)
            {
                return;
            }
            ImGuiIOPtr io = ImGui.GetIO();
            Main.Logger.Log("[Timeline] 起框（" + where + "）Ctrl=" + CtrlHeld()
                + " Alt=" + io.KeyAlt + " Shift=" + io.KeyShift);
        }

        /// <summary>
        /// Ctrl 按住没有。**直接问 Unity，不问 `io.KeyCtrl`。**
        ///
        /// 实测 `io.KeyCtrl` 一直是 false —— 探针日志里"按住 Ctrl 点左键"
        /// 打出来的还是 `Ctrl=False`（桥那边 LeftCtrl/RightCtrl 都喂了，
        /// 也确认调到了）。而项目里其余地方一律读
        /// `Input.GetKey(KeyCode.LeftControl)`，所以这里也照那个来。
        /// </summary>
        private static bool CtrlHeld()
        {
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        /// <summary>框小于这个尺寸就不算框选（Ctrl 点一下不该把多选清掉）。</summary>
        private const float MarqueeMinSize = 4f;

        /// <summary>排查框选用：打开后会打日志说明框有多小。</summary>
        /// <summary>排查框选用。**已经定位完了，关掉** —— 要看再改回 true。</summary>
        private const bool VerboseMarquee = false;

        /// <summary>块窄到这个数以下就不画缓速曲线了 —— 画出来也是一团。</summary>
        private const float CurveMinBlockWidth = 34f;

        /// <summary>
        /// 缓速曲线每段至少占多少像素。
        ///
        /// 曲线数组固定 128 个采样，但**画多少段要按块的像素宽度给** ——
        /// 一屏能摊开几百个块，每个都画满 127 条线段就是一帧五万多条，直接掉帧。
        /// 3 像素一段肉眼已经看不出折角（旧模型是拿"1800 像素宽的块"当上限算的，
        /// 那个上限对大多数块都过密）。
        /// </summary>
        private const float CurvePixelsPerSegment = 3f;

        /// <summary>
        /// 停用的事件（`active=false`）压到多透。
        ///
        /// **和旧版 `BindMarker` 同一个数**：填充 0.46、文字 0.55。这两个数不是
        /// 随手取的 —— 只压填充的话，深色字在暗块上还是满亮度、整块读起来仍然像
        /// "生效中"；只压文字的话，块的色相还是饱和的，一样看不出来。
        ///
        /// 新模型里没有分类框，块本身就是"这个事件长什么样"的唯一线索，
        /// 少这一档就等于"哪些事件被停用了"在画布上完全看不出来。
        /// </summary>
        private const float InactiveFillAlpha = 0.46f;
        private const float InactiveTextAlpha = 0.55f;

        /// <summary>选中时往白提亮多少。旧版 6% —— 很轻，和白色描边一起读成"被圈出来了"。</summary>
        private const float SelectedBrighten = 0.06f;

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
            overlay.GetTimelineBodyRect(out x, out y, out width, out height);
            if (width <= 1f || height <= 1f)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }
            _rect = new Rect(x, y, width, height);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            if (ImGui.Begin("##aes_timeline_body", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                // 滚轮：**这一块现在归 ImGui 了**，所以得替 uGUI 把它转给时间轴，
                // 否则鼠标一进时间轴就滚不动（挡点击层挡住了 EventSystem 那条路）。
                //
                // **修饰键一律问 Unity，不问 `io.KeyXxx`。** 桥那边虽然都喂了，
                // 实测 `io.KeyCtrl` 一直是 false —— Alt 同一个机制、同一个下场，
                // Alt+滚轮调行高就是这么哑掉的（Ctrl 早就改成问 Unity 了，
                // Alt/Shift 漏在外面）。见 `EditorTimelineView.IsAltHeld`。
                ImGuiIOPtr io = ImGui.GetIO();
                if (ImGui.IsWindowHovered() && !Mathf.Approximately(io.MouseWheel, 0f))
                {
                    timeline.ScrollBy(io.MouseWheel, CtrlHeld(),
                        EditorTimelineView.IsAltHeld(), EditorTimelineView.IsShiftHeld());
                }

                DrawBlocks(timeline, x, y, width, height);
                DrawRuler(timeline, x, y, width, height);
            }
            ImGui.End();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// 画布上的事件块。**按行升序画** —— 块比行距高，相邻两块叠着，
        /// 后画的那块（行号大的）盖住前一块的下沿，这就是"一层层往下盖"。
        /// 颜色来自每个事件类型自己的色，所以叠起来是层层分明的。
        /// </summary>
        private void DrawBlocks(EditorTimelineView timeline,
            float x, float y, float width, float height)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            // **先把上一帧起的拖动结算掉，再取位置。** 反过来的话块会慢一帧，
            // 拖起来是橡皮筋的手感。
            if (_dragId >= 0)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    float dy = io.MousePos.Y - _dragStartMouse.Y;
                    // **纵向按行吸附，而且整批一起走。**
                    //
                    // 行号是优先级（写回谱面时按它排顺序），连续的小数位置没有意义
                    // —— 也对不上"一块一行"这个模型。横向不吸：时间本来就是连续的。
                    // **纵向 = 布局**（只改行号），**横向 = 谱面**（交给时间轴去提交）。
                    // 两条分开走：横向是真实属性、要写回要上报；纵向现在只活在会话里。
                    //
                    // 位移交给时间轴去分摊到整批 —— 这边原来只对被拖的那一个
                    // `SetBlockRow`，于是"能整批横向拖、纵向只有它自己动"。
                    timeline.DragBlockRowBy(Mathf.RoundToInt(dy / timeline.BlockStridePixels));
                    timeline.DragBlockTo(PointerTime(timeline, x, io));
                }
                else
                {
                    // 松手：**两条包成一步撤销**（横向写谱面 + 纵向改摆放），
                    // 不然按一次 Ctrl+Z 只退一半，看着像"没撤干净"。
                    EditorCommandHistory.BeginBatch("移动事件块");
                    try
                    {
                        // **纵向的撤销要在 EndBlockDrag 之前记**：那一步的结尾会
                        // 清空拖动起点表，之后就问不到"原来在第几行"了。
                        // 整批一起记（横向由各自的谱面命令负责）。
                        timeline.RecordDragRowMove();
                        // 这一步写回谱面（横向），并按事件各记一条撤销。
                        timeline.EndBlockDrag();
                    }
                    finally
                    {
                        EditorCommandHistory.EndBatch();
                    }
                    _dragId = -1;
                }
            }

            // 中键拖动也是横向平移 —— 原来 uGUI 那条路被挡点击层挡住了，
            // 不补上的话习惯用中键的人会以为它坏了。
            if (ImGui.IsWindowHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f))
            {
                // **两个方向都要**：只做横向的话纵向拖不动（报过）。
                timeline.PanBy(-io.MouseDelta.X);
                timeline.PanVerticalBy(-io.MouseDelta.Y);
            }

            timeline.CollectBlocks();
            List<EditorTimelineView.BlockView> blocks = timeline.BlockViews;
            if (blocks.Count == 0)
            {
                return;
            }

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float pixelsPerSecond = timeline.PixelsPerSecond;
            float viewStart = timeline.ViewStartSeconds;
            float rulerHeight = EditorTimelineView.RulerHeight;
            float contentTop = y + rulerHeight - timeline.ScrollOffsetY;
            float right = x + width;
            float bottom = y + height;
            float stride = timeline.BlockStridePixels;
            float blockHeight = timeline.BlockHeightPixels;

            bool hoverLeftEdge = false;
            bool hoverRightEdge = false;

            for (int i = 0; i < blocks.Count; i++)
            {
                EditorTimelineView.BlockView block = blocks[i];
                float blockTop = contentTop + block.Row * stride;

                // 按行升序，所以顶已经落到底下之后，后面的都在更下面 —— 直接停。
                if (blockTop >= bottom)
                {
                    break;
                }
                if (blockTop + blockHeight <= y + rulerHeight)
                {
                    continue;
                }

                float left = x + (block.Time - viewStart) * pixelsPerSecond;
                // 右边留一道缝：连着两个事件本来会画成一整条，分不开。
                float blockWidth = Mathf.Max(EditorTimelineView.BlockMinWidth,
                    block.Duration * pixelsPerSecond - EditorTimelineView.BlockGap);
                if (left >= right || left + blockWidth <= x)
                {
                    continue;
                }

                NVec2 min = new NVec2(left, blockTop);
                NVec2 max = new NVec2(left + blockWidth, blockTop + blockHeight);
                float rounding = EditorStudioOverlay.CornerSmall;

                // 填充：选中时往白提亮一点，停用的压到 46%。
                // **透明度是最后定的**，不跟事件色自己的 alpha 相乘 ——
                // 旧版也是直接赋值（`blockColor.a = Active ? 1 : 0.46`）。
                NVec4 blockColor = ToVec4(block.BlockColor);
                if (block.Selected)
                {
                    blockColor.X = Mathf.Lerp(blockColor.X, 1f, SelectedBrighten);
                    blockColor.Y = Mathf.Lerp(blockColor.Y, 1f, SelectedBrighten);
                    blockColor.Z = Mathf.Lerp(blockColor.Z, 1f, SelectedBrighten);
                }
                blockColor.W = block.Active ? 1f : InactiveFillAlpha;

                draw.AddRectFilled(min, max, ImGui.GetColorU32(blockColor), rounding);

                // **每块一个裁剪矩形，去掉了。**
                //
                // 它本来是防"长名字糊到隔壁块上"的安全网，可名字和图标本来就是
                // 按块宽量好、放不下就裁字的（FitText），量出来放得下才会画到这里
                // —— 网已经用不上了。
                //
                // 而它的代价很大：每 Push/Pop 一次裁剪就是一次绘制命令切换，
                // 而桥那边**每条绘制命令建一个 Unity Mesh**。一屏三百多个块，
                // 光这一项就是几百条多余的网格。窗口本身已经裁在视口里，
                // 不需要再套一层。

                // 缓速曲线：块里那条线，一眼看出缓速怎么走。
                // 旧版是自绘网格，这边直接画折线。
                if (block.Curve != null && block.Curve.Length >= 2
                    && blockWidth >= CurveMinBlockWidth
                    && blockHeight > EditorTimelineView.EaseCurveMarginY * 2f + 4f)
                {
                    NVec4 curveColor = ToVec4(block.LabelColor);
                    curveColor.W = block.Active ? 1f : 0.42f;
                    uint curveU32 = ImGui.GetColorU32(curveColor);

                    float boxLeft = min.X + EditorTimelineView.EaseCurveMarginX;
                    float boxWidth = blockWidth - EditorTimelineView.EaseCurveMarginX * 2f;
                    float boxTop = min.Y + EditorTimelineView.EaseCurveMarginY;
                    float boxHeight = blockHeight - EditorTimelineView.EaseCurveMarginY * 2f;

                    // **采样点数跟着块的像素宽度走。**
                    //
                    // 曲线数组固定 128 个采样，旧模型的理由是"1800 像素宽的块也要
                    // 平滑"。可现在一屏能同时摊开几百个块，每个都画 127 条线段 ——
                    // 一帧五万多条线段，掉帧就是这么来的。
                    //
                    // 按宽度给点数：每段不小于 3 像素。64 像素的块只要 21 段，
                    // 肉眼分辨不出差别，而线段数掉到六分之一。
                    int wanted = Mathf.Clamp(
                        Mathf.CeilToInt(boxWidth / CurvePixelsPerSegment),
                        2, block.Curve.Length);
                    int sampleStep = Mathf.Max(1,
                        Mathf.CeilToInt(block.Curve.Length / (float)wanted));

                    NVec2 first = default(NVec2);
                    NVec2 previous = default(NVec2);
                    for (int k = 0; k < wanted; k++)
                    {
                        // **末点一定落在最后一个采样上** —— 不然曲线右端会缺一截，
                        // 而两端的圆点正是用来标起止的。
                        int sample = k == wanted - 1
                            ? block.Curve.Length - 1
                            : Mathf.Min(k * sampleStep, block.Curve.Length - 1);
                        float t = (float)sample / (block.Curve.Length - 1);
                        NVec2 point = new NVec2(
                            boxLeft + t * boxWidth,
                            // 值 0 在底、1 在顶 —— 和参数栏那个缓速图一个朝向。
                            boxTop + (1f - Mathf.Clamp01(block.Curve[sample])) * boxHeight);
                        if (k == 0)
                        {
                            first = point;
                        }
                        else
                        {
                            draw.AddLine(previous, point, curveU32,
                                EditorTimelineView.EaseCurveThickness);
                        }
                        previous = point;
                    }
                    // 两端的圆点：和旧版一致，标出起止。
                    draw.AddCircleFilled(first, EditorTimelineView.EaseCurveDotRadius, curveU32);
                    draw.AddCircleFilled(previous, EditorTimelineView.EaseCurveDotRadius, curveU32);
                }

                // 事件图标。**白色剪影要自己上色** —— 不上色就是白贴彩，
                // 这个小尺寸下糊成一团。默认用块自己的深色（和文字一致），
                // 选中时换白（那时填充被提亮、描边变深，需要图标跳出来）。
                float textLeft = EditorTimelineView.MarkerIconLeft;
                float iconSize = EditorTimelineView.GetMarkerIconSize(blockHeight);
                if (block.Icon != null && iconSize > 0f
                    && blockWidth >= iconSize + EditorTimelineView.MarkerIconLeft
                        + EditorTimelineView.MarkerIconGap + 12f)
                {
                    EditorImGuiIcons.Icon icon;
                    if (EditorImGuiIcons.TryFromSprite(block.Icon, out icon))
                    {
                        float iconTop = min.Y + (blockHeight - iconSize) * 0.5f;
                        NVec4 iconColor = ToVec4(block.Selected ? Color.white : block.LabelColor);
                        // 停用的块，图标也跟着压暗 —— 只压填充的话，深色图标在
                        // 暗块上仍是满亮度，整块读起来还是像"生效中"。
                        if (!block.Active && !block.Selected)
                        {
                            iconColor.W = InactiveTextAlpha;
                        }
                        draw.AddImage(icon.TextureId,
                            new NVec2(min.X + EditorTimelineView.MarkerIconLeft, iconTop),
                            new NVec2(min.X + EditorTimelineView.MarkerIconLeft + iconSize,
                                iconTop + iconSize),
                            icon.UvMin, icon.UvMax, ImGui.GetColorU32(iconColor));
                    }
                    textLeft = EditorTimelineView.MarkerIconLeft + iconSize
                        + EditorTimelineView.MarkerIconGap;
                }

                // 名字排在图标之后；没图标就退回原来的左边距，不留空档。
                float labelRoom = blockWidth - textLeft - EditorTimelineView.MarkerIconLeft;
                if (labelRoom >= 24f && !string.IsNullOrEmpty(block.Name))
                {
                    float labelSize = SmallFontSize;
                    string label = FitText(SmallFont, labelSize, block.Name, labelRoom);
                    if (label.Length > 0)
                    {
                        float labelHeight = SmallFont.CalcTextSizeA(
                            labelSize, float.MaxValue, 0f, label).Y;
                        NVec4 labelColor = ToVec4(block.LabelColor);
                        // 停用的块文字压到 55%（旧版同一个数）—— 填充和文字两样一起压，
                        // 才读得出"这一条不生效"。
                        if (!block.Active)
                        {
                            labelColor.W = InactiveTextAlpha;
                        }
                        draw.AddText(SmallFont, labelSize,
                            new NVec2(min.X + textLeft, min.Y + (blockHeight - labelHeight) * 0.5f),
                            ImGui.GetColorU32(labelColor), label);
                    }
                }

                if (block.Selected)
                {
                    draw.AddRect(min, max, ImGui.GetColorU32(ToVec4(Color.white)),
                        rounding, ImDrawFlags.None, 2f);
                }

                // 交互层：整块当点击/拖动目标。**画在前、控件在后**，
                // 这样拖起来的那一块就是这一帧刚摆好的位置，不慢一帧。
                ImGui.PushID(block.Id);
                ImGui.SetCursorScreenPos(min);
                ImGui.InvisibleButton("##block",
                    new NVec2(blockWidth, blockHeight));
                bool hovered = ImGui.IsItemHovered();
                // 落在左右那 6 像素里就是"拉时长"，其余是"整体移动"。
                //
                // **两道前提**：
                //   · 块不能太窄 —— 否则整块都落进边缘区，永远挪不动；
                //   · 这个事件得**真的能拉时长**（声明了 `duration`）。
                //     点事件（没有 duration）给了边缘区就是纯死区：按在边上走
                //     "改时长"，而没东西可改，结果什么都不发生 —— 用户看到的就是
                //     "这块拖不动"。实测日志里那块 64 像素宽、4 次按下的落点全在
                //     右边 6 像素里，于是 4 次都原地不动。
                bool hasEdgeZone = block.Resizable
                    && blockWidth > EditorTimelineView.BlockEdgeZone * 2f;
                bool atLeftEdge = hovered && hasEdgeZone
                    && io.MousePos.X - min.X <= EditorTimelineView.BlockEdgeZone;
                bool atRightEdge = hovered && hasEdgeZone
                    && io.MousePos.X - min.X >= blockWidth - EditorTimelineView.BlockEdgeZone;
                if (atLeftEdge)
                {
                    hoverLeftEdge = true;
                }
                if (atRightEdge)
                {
                    hoverRightEdge = true;
                }

                if (ImGui.IsItemClicked())
                {
                    timeline.SelectBlock(block.Id, CtrlHeld());
                }
                if (ImGui.IsItemActivated())
                {
                    // **Ctrl 从块上按下 = 拉框**（旧版专门做了转交，这里直接分叉）。
                    // 块身上从此也能起框，不用先挪到空白处。
                    if (CtrlHeld())
                    {
                        _marqueeActive = true;
                        _marqueeStart = io.MousePos;
                        LogMarquee("块上");
                        ImGui.PopID();
                        continue;
                    }

                    EditorTimelineView.BlockDragMode mode = EditorTimelineView.BlockDragMode.Move;
                    if (atLeftEdge)
                    {
                        mode = EditorTimelineView.BlockDragMode.ResizeLeft;
                    }
                    else if (atRightEdge)
                    {
                        mode = EditorTimelineView.BlockDragMode.ResizeRight;
                    }

                    _dragId = block.Id;
                    _dragStartMouse = io.MousePos;
                    // 横向那条交给时间轴：它自己抓原值、自己吸附、自己写回。
                    if (block.Event != null
                        && !timeline.BeginBlockDrag(block.Event, mode,
                            PointerTime(timeline, x, io)))
                    {
                        // 拿不到（事件已经不在了）就不要进拖动状态，
                        // 否则松手时会对一个空目标提交。
                        _dragId = -1;
                    }
                }
                ImGui.PopID();

                if (hovered && !string.IsNullOrEmpty(block.Name))
                {
                    ImGui.SetTooltip(block.Name);
                }
            }

            // 悬停在块边缘 → 左右箭头指针（**复用旧版那份实现**，
            // 连"纹理必须 CPU 可读"那个坑都在里面）。
            timeline.ApplyBlockDragCursor(hoverLeftEdge, hoverRightEdge);

            DrawPlayhead(timeline, x, y, width, height);
            HandlePointer(timeline, io, x, y, width, height);
        }

        /// <summary>鼠标当前指在哪个时间点（秒）。</summary>
        private static float PointerTime(EditorTimelineView timeline, float viewportX, ImGuiIOPtr io)
        {
            return timeline.ViewStartSeconds
                + (io.MousePos.X - viewportX) / timeline.PixelsPerSecond;
        }

        /// <summary>
        /// 播放头：一条竖线加顶端一个小手柄。
        ///
        /// 没有它就没法知道现在播到哪儿 —— uGUI 那条被挡点击层掐了，
        /// 这一条是补回来的。
        /// </summary>
        private static void DrawPlayhead(EditorTimelineView timeline,
            float x, float y, float width, float height)
        {
            float playheadX = x + (timeline.PlayheadSeconds - timeline.ViewStartSeconds)
                * timeline.PixelsPerSecond;
            if (playheadX < x || playheadX > x + width)
            {
                return;
            }

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            // **从视口最顶端起，盖住尺标那一条。** 旧版就是 `SetTopLeft(playhead, x, 0, ...)`
            // —— 我从尺标下沿起，看起来像"播放头从尺标下面才开始"。
            float top = y;
            float bottom = y + height;
            NVec4 core = ToVec4(Palette.Accent);

            // 辉光 + 芯：两层，宽的压得很淡，读起来像"发光的线"而不是一根粗条。
            draw.AddLine(new NVec2(playheadX, top), new NVec2(playheadX, bottom),
                ImGui.GetColorU32(new NVec4(core.X, core.Y, core.Z, 0.18f)), 8f);
            draw.AddLine(new NVec2(playheadX, top), new NVec2(playheadX, bottom),
                ImGui.GetColorU32(core), 2f);

            // 顶端那个小手柄，一眼看出"这条能拖"。
            draw.AddRectFilled(
                new NVec2(playheadX - 5.5f, top), new NVec2(playheadX + 5.5f, top + 9f),
                ImGui.GetColorU32(core), 2f);
        }

        /// <summary>
        /// 指针交互，全在这一个地方：
        ///
        /// - **空白处按下 + 左键拖** = 拖播放头（和原版一致）
        /// - **Ctrl + 拖** = 框选
        /// - **中键** = 两个方向平移（见上）
        ///
        /// 原来左键拖空白是横向平移 —— 那是挡点击层之后的临时补法，
        /// 和"点空白定位播放头"抢了同一个手势，改回原版的语义。
        /// </summary>
        private void HandlePointer(EditorTimelineView timeline, ImGuiIOPtr io,
            float x, float y, float width, float height)
        {
            bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);

            // 左键按下时把**每一个闸**的状态打出来 —— 框选进不去到底是哪一个挡的，
            // 一次就看清楚，不用一轮一轮试。
            if (VerboseMarquee && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ImGuiIOPtr probe = ImGui.GetIO();
                Main.Logger.Log("[Timeline] 左键按下：窗口悬停=" + ImGui.IsWindowHovered()
                    + " 有控件悬停=" + ImGui.IsAnyItemHovered()
                    + " dragId=" + _dragId
                    + " Ctrl=" + probe.KeyCtrl + " Alt=" + probe.KeyAlt
                    + " Shift=" + probe.KeyShift);
            }

            if (_marqueeActive)
            {
                if (!down)
                {
                    float left = Mathf.Min(_marqueeStart.X, io.MousePos.X) - x;
                    float top = Mathf.Min(_marqueeStart.Y, io.MousePos.Y) - y;
                    float right = Mathf.Max(_marqueeStart.X, io.MousePos.X) - x;
                    float bottom = Mathf.Max(_marqueeStart.Y, io.MousePos.Y) - y;
                    _marqueeActive = false;

                    // **几乎没动就不提交。**
                    //
                    // Ctrl 在块上点一下也会起框 —— 要是照提交，那个"零面积的框"
                    // 会把多选整个清掉、只剩点中的那一个。表现就是
                    // "Ctrl 能单选不能多选"。点就归点，框就归框。
                    if (right - left >= MarqueeMinSize && bottom - top >= MarqueeMinSize)
                    {
                        timeline.SelectBlocksInRect(left, top, right, bottom);
                        if (VerboseMarquee)
                        {
                            Main.Logger.Log("[Timeline] 框选 "
                                + left.ToString("0") + "," + top.ToString("0") + " → "
                                + right.ToString("0") + "," + bottom.ToString("0")
                                + " → 选中 " + timeline.SelectedBlockCount + " 个");
                        }
                    }
                    else if (VerboseMarquee)
                    {
                        Main.Logger.Log("[Timeline] 框选没提交（太小）："
                            + (right - left).ToString("0.#") + "x"
                            + (bottom - top).ToString("0.#"));
                    }
                }
            }
            else if (_scrubbing)
            {
                if (down)
                {
                    timeline.ScrubToX(io.MousePos.X - x);
                }
                else
                {
                    timeline.EndScrub();
                    _scrubbing = false;
                }
            }
            else if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && _dragId < 0 && !ImGui.IsAnyItemHovered())
            {
                if (CtrlHeld())
                {
                    _marqueeActive = true;
                    _marqueeStart = io.MousePos;
                    LogMarquee("空地");
                }
                else
                {
                    // **只在尺标那一条里能拖播放头。**
                    //
                    // 连播放头自己那条线也不算 —— 点它按"点空白"处理（清除选中）。
                    // 我一开始把整块视口都当成拖动区，随手一点就把播放头带跑了；
                    // 后来改成"尺标 + 播放头±7像素"还是多给了地方。
                    float localY = io.MousePos.Y - y;
                    bool rulerHit = localY >= 0f && localY <= EditorTimelineView.RulerHeight;

                    if (rulerHit)
                    {
                        if (timeline.BeginScrub())
                        {
                            _scrubbing = true;
                            timeline.ScrubToX(io.MousePos.X - x);
                        }
                    }
                    else
                    {
                        timeline.ClearBlockSelection();
                    }
                }
            }

            if (!_marqueeActive)
            {
                return;
            }
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            NVec2 a = new NVec2(Mathf.Min(_marqueeStart.X, io.MousePos.X),
                Mathf.Min(_marqueeStart.Y, io.MousePos.Y));
            NVec2 b = new NVec2(Mathf.Max(_marqueeStart.X, io.MousePos.X),
                Mathf.Max(_marqueeStart.Y, io.MousePos.Y));
            NVec4 accent = ToVec4(Palette.Accent);
            draw.AddRectFilled(a, b,
                ImGui.GetColorU32(new NVec4(accent.X, accent.Y, accent.Z, 0.12f)));
            draw.AddRect(a, b, ImGui.GetColorU32(accent), 0f, ImDrawFlags.None, 1f);
        }

        private static void DrawRuler(EditorTimelineView timeline,
            float x, float y, float width, float height)
        {
            // 数学全在时间轴那边（CollectRuler），这里只负责画 ——
            // 刻度按格走、格宽按可视格数均摊那些坑，别在这儿重推一遍。
            timeline.CollectRuler();

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float rulerHeight = EditorTimelineView.RulerHeight;

            // 格线：从标尺下沿一直落到视口底部。挤在一起的由 CollectRuler 滤掉了。
            uint lineColor = ImGui.GetColorU32(new NVec4(1f, 1f, 1f, 0.10f));
            List<EditorTimelineView.RulerLine> lines = timeline.RulerLines;
            for (int i = 0; i < lines.Count; i++)
            {
                float lineX = x + lines[i].X;
                draw.AddLine(new NVec2(lineX, y + rulerHeight),
                    new NVec2(lineX, y + height), lineColor, 1f);
            }

            // 刻度：格号。和 uGUI 那边一样贴着格线右移 4 像素排。
            uint textColor = ImGui.GetColorU32(ToVec4(Palette.TextMuted));
            float tickSize = SmallFontSize;
            float textY = y + (rulerHeight
                - SmallFont.CalcTextSizeA(tickSize, float.MaxValue, 0f, "0").Y) * 0.5f;
            List<EditorTimelineView.RulerTick> ticks = timeline.RulerTicks;
            for (int i = 0; i < ticks.Count; i++)
            {
                draw.AddText(SmallFont, tickSize,
                    new NVec2(x + ticks[i].X + 4f, textY), textColor, ticks[i].Label);
            }
        }
    }
}
