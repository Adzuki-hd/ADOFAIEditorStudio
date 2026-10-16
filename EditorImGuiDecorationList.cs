using System.Collections.Generic;
using ADOFAIEditorStudio.Ui;
using ImGuiNET;
using static ADOFAIEditorStudio.EditorImGuiDraw;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 装饰物列表的 ImGui 版第一段：先搬显示，不搬按钮副作用。
    ///
    /// 数据来自 EditorDecorationList.FillImGuiEntries，保证两边的筛选、名字、tag、
    /// 格数和选中状态一致。下一段再接点击选中、显隐、锁定、删除和搜索。
    /// </summary>
    internal sealed class EditorImGuiDecorationList
    {
        private readonly List<ImGuiDecorationEntry> _entries
            = new List<ImGuiDecorationEntry>();
        private Rect _rect;
        /// <summary>搜索框的缓冲。ImGui 每次按键都回传文本，得跨帧留住。</summary>
        private string _search = string.Empty;

        // ------------------------------------------------------------ 图标
        //
        // **两级兜底：游戏原版图标 → 我们自己画的那张。**
        //
        // 每级都是"拿不到就往下退"，两级都没有才写文字。所以缺哪一级都只影响观感，
        // 绝不会画出豆腐块或者一片空白 —— 这一条比图标好看重要得多。
        //
        // 原版图标只借引用：不拷像素、不落盘、不进包（见 EditorImGuiIcons）。

        /// <summary>原版图标拿到过没有。拿到一次就不再问了。</summary>
        private bool _gameIconsResolved;
        // 每个图标配一个"有没有"，不合并成一个总开关 —— 游戏那边可能只给到一半
        // （比如眼睛有、锁没有），合并了就会把有的一起丢掉。
        private EditorImGuiIcons.Icon _iconEyeOpen;
        private bool _hasEyeOpen;
        private EditorImGuiIcons.Icon _iconEyeClosed;
        private bool _hasEyeClosed;
        private EditorImGuiIcons.Icon _iconLockOpen;
        private bool _hasLockOpen;
        private EditorImGuiIcons.Icon _iconLockClosed;
        private bool _hasLockClosed;
        private EditorImGuiIcons.Icon _iconDelete;
        private bool _hasDelete;
        /// <summary>四个类型图标，下标和 EditorDecorationList.TypeAt 对齐。</summary>
        private readonly EditorImGuiIcons.Icon[] _iconType
            = new EditorImGuiIcons.Icon[EditorDecorationList.TypeCount];
        private readonly bool[] _hasTypeIcon
            = new bool[EditorDecorationList.TypeCount];

        /// <summary>
        /// 去游戏那边取原版图标。**每帧调一次，取到为止。**
        ///
        /// 为什么不是只试一次：工作台可能在编辑器原生 UI 就绪之前就被打开了，
        /// 那时 `scnEditor.propertyControlDecorationsList` 还是空的。uGUI 那版
        /// 也是这么轮询的（RetryIconsIfNeeded），两边行为保持一路。
        /// </summary>
        private void ResolveGameIcons()
        {
            if (_gameIconsResolved)
            {
                return;
            }

            bool any = false;
            _hasEyeOpen = EditorImGuiIcons.TryGetEye(true, out _iconEyeOpen);
            _hasEyeClosed = EditorImGuiIcons.TryGetEye(false, out _iconEyeClosed);
            _hasLockOpen = EditorImGuiIcons.TryGetLock(false, out _iconLockOpen);
            _hasLockClosed = EditorImGuiIcons.TryGetLock(true, out _iconLockClosed);
            _hasDelete = EditorImGuiIcons.TryGetDelete(out _iconDelete);
            any |= _hasEyeOpen | _hasEyeClosed | _hasLockOpen | _hasLockClosed | _hasDelete;
            for (int i = 0; i < EditorDecorationList.TypeCount; i++)
            {
                _hasTypeIcon[i] = EditorImGuiIcons.TryGetType(
                    EditorDecorationList.TypeAt(i), out _iconType[i]);
                any |= _hasTypeIcon[i];
            }
            _gameIconsResolved = any;

            // **打一行日志说清到底走的哪一级。** 屏幕上"原版图标"和"自绘那张"
            // 只差一点风格，肉眼不容易判定，但排查的时候这个答案是关键。
            if (any)
            {
                int types = 0;
                for (int i = 0; i < _hasTypeIcon.Length; i++)
                {
                    if (_hasTypeIcon[i])
                    {
                        types++;
                    }
                }
                // 用"有/无"不用 ✓/✗ —— 源字体里没有 ✗（有 ✓），
                // 混着用会有一个渲染成方框。
                Main.Logger.Log("[ImGui] 装饰物图标走原版："
                    + "眼睛 开/闭 " + (_hasEyeOpen ? "有" : "无") + "/" + (_hasEyeClosed ? "有" : "无")
                    + "  锁 开/闭 " + (_hasLockOpen ? "有" : "无") + "/" + (_hasLockClosed ? "有" : "无")
                    + "  删除 " + (_hasDelete ? "有" : "无")
                    + "  类型 " + types + "/" + _hasTypeIcon.Length);
            }
        }

        /// <summary>
        /// 本列表行尾那三个按钮的图标画法。
        ///
        /// **实现搬到了 `EditorImGuiIcons.DrawIcon`** —— 参数栏那个「事件生效」
        /// 开关也要画同一种图标按钮，两条路径各写一份的话，大小、居中方式、
        /// 兜底文字迟早走散（而两边在同一个界面上挨着放）。
        /// 这里只把本列表的图标尺寸钉进去。
        /// </summary>
        private static void DrawIcon(ImDrawListPtr draw, NVec2 min, NVec2 max,
            bool hasGame, EditorImGuiIcons.Icon game,
            EditorImGuiIcons.Drawn drawn, string text, Color tint)
        {
            EditorImGuiIcons.DrawIcon(draw, min, max, IconSize,
                hasGame, game, drawn, text, tint);
        }

        // 尺寸全部取项目自己的刻度（UiSpacing：4/8/12/16/24/32/48），不临场编数。
        //
        // **卡片的"内边距"必须小于卡片之间的"间距"。** 组内小于组外，眼睛才分得清
        // 哪些内容属于同一张卡。原来内边距 8、卡间距 6 是反的 —— 这正是"挤"的来源。
        private const float CardPad = UiSpacing.Md;      // 12
        private const float CardGap = UiSpacing.Lg;      // 16
        /// <summary>
        /// 一行文字的排版高度。正文字号 23，取整到刻度上的 24。
        ///
        /// 卡片里只剩名字这一行了（下面那行"第 N 格 · 已隐藏"已经去掉，
        /// 隐藏/锁定交给行尾那两个图标表示），所以这里只要放得下一行字就够。
        /// </summary>
        private const float TextLine = UiSpacing.Xl;   // 24
        /// <summary>
        /// 行尾三个按钮。**正方形** —— 原来 30x22 是长条，图标塞进去上下顶满，
        /// 就是"扁"。窄档降一级，免得把文件名挤没了。
        /// </summary>
        private const float ButtonSizeWide = UiSpacing.Xxl;   // 32
        private const float ButtonSizeNarrow = UiSpacing.Xl;  // 24
        private const float ButtonGapWide = UiSpacing.Sm;     // 8
        private const float ButtonGapNarrow = UiSpacing.Xs;   // 4
        /// <summary>够得着宽档所需的内容宽度。低于它就把按钮降一级。</summary>
        private const float RoomyWidth = 240f;
        /// <summary>头部四个"新建"方块。正方形，理由同上。</summary>
        private const float CreateTileSize = UiSpacing.Xxl;   // 32
        private const float CreateTileGap = UiSpacing.Sm;     // 8
        /// <summary>
        /// 图标本身的绘制尺寸。**比按钮小一圈，不撑满。**
        ///
        /// 撑满有两个后果：原版图标是照小尺寸画的，拉到 32 之后笔画显得又细又飘；
        /// 我们自绘那张是 12x12 的位图，拉到 32 就糊了。小一圈两个毛病一起治。
        /// 嫌大嫌小改这一个数（按钮尺寸不用动）。
        /// </summary>
        private const float IconSize = UiSpacing.Lg;   // 16

        internal Rect Rect { get { return _rect; } }

        internal void Draw()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            EditorDecorationList source = overlay == null ? null : overlay.DecorationList;
            if (overlay == null || source == null || !overlay.IsDecorationPane)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            overlay.GetDecorationListRect(out x, out y, out width, out height);
            _rect = new Rect(x, y, width, height);
            source.FillImGuiEntries(_entries);
            // 每帧试一次，取到就缓存住（见 ResolveGameIcons）。
            ResolveGameIcons();

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            if (ImGui.Begin("##aes_decorations", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus))
            {
                ImGui.SeparatorText("装饰物");
                DrawHeader(source);
                ImGui.BeginChild("##decoration_scroll", new NVec2(0f, 0f),
                    ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);
                DrawRows();
                ImGui.EndChild();
            }
            ImGui.End();
        }

        /// <summary>
        /// 头部三行：新建四个方块 + 计数、搜索框、批量显隐/锁定。
        ///
        /// 全部转发到 uGUI 那版的同一套实现（见 EditorDecorationList 里那组
        /// internal 入口）—— 建装饰物、搜索、批量开关都带副作用，复制一份出来
        /// 迟早两边不一致。
        ///
        /// 方块和批量按钮**统一 32 高**，三行控件不出现高矮不一。
        /// </summary>
        private void DrawHeader(EditorDecorationList source)
        {
            if (source == null)
            {
                return;
            }

            // 第一行：新建方块 + 计数。
            //
            // 方块上画的是**游戏原版的事件类型图标**，和事件面板上那排同一个来源 ——
            // 做谱的人一眼认得出。名字走悬停提示：32 见方的方块塞不下"图片/文本/
            // 粒子/对象"。
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            for (int i = 0; i < EditorDecorationList.TypeCount; i++)
            {
                if (i > 0)
                {
                    ImGui.SameLine(0f, CreateTileGap);
                }
                string name = EditorDecorationList.TypeNameAt(i);
                // 标签用 `##` 隐藏 —— 内容改成画上去的图标了，ImGui 自己的文字会
                // 和图标叠在一起。ID 靠 `##` 后面那截区分，所以要带上 i。
                if (ImGui.Button("##create" + i, new NVec2(CreateTileSize, CreateTileSize)))
                {
                    source.CreateDecorationFromImGui(EditorDecorationList.TypeAt(i));
                }
                // 类型图标没有"我们自己画的"那份（uGUI 那版也是纯取原版），
                // 所以传 None：原版图标 → 文字。
                //
                // **兜底文字只取首字**（图/文/粒/对）：32 宽的方块放不下两个汉字，
                // 会被硬切掉半个 —— 全名走悬停提示。
                DrawIcon(draw, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    _hasTypeIcon[i], _iconType[i], EditorImGuiIcons.Drawn.None,
                    string.IsNullOrEmpty(name) ? "" : name.Substring(0, 1),
                    EditorDecorationList.TypeColorAt(i));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("新建" + name);
                }
            }
            ImGui.SameLine();
            PushText(ImGuiCol.Text, Palette.AccentSecondary);
            ImGui.TextUnformatted(source.FilteredCount == source.TotalCount
                ? source.TotalCount.ToString()
                : source.FilteredCount + "/" + source.TotalCount);
            ImGui.PopStyleColor();

            // 第二行：搜索框。前缀语义（`type:` / `tag:` / `floor:`）和 uGUI 那边
            // 完全一致 —— 解析在 EditorDecorationList.ParseSearch 里，只有一份。
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("##deco_search", "搜索（type: / tag: / floor:）",
                ref _search, 128))
            {
                source.SetSearchText(_search);
            }

            // 第三行：批量开关。**只改运行时状态、不写进谱面**，和 uGUI 那边一致。
            bool hidden = source.BatchHidden;
            bool locked = source.BatchLocked;
            if (ImGui.Button(hidden ? "全部隐藏 ✓" : "全部隐藏",
                new NVec2(0f, CreateTileSize)))
            {
                source.ToggleBatchHiddenFromImGui();
            }
            ImGui.SameLine();
            if (ImGui.Button(locked ? "全部锁定 ✓" : "全部锁定",
                new NVec2(0f, CreateTileSize)))
            {
                source.ToggleBatchLockedFromImGui();
            }
        }

        private void DrawRows()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            EditorDecorationList source = overlay == null ? null : overlay.DecorationList;
            ImDrawListPtr draw = ImGui.GetWindowDrawList();

            // 光标自己管，不靠 ImGui 的行间距往上叠 —— 那样每张卡外面会多出
            // 一个 ItemSpacing，卡间距就不是我们写的那个数了。
            float contentLeft = ImGui.GetCursorScreenPos().X;
            float rowY = ImGui.GetCursorScreenPos().Y;

            // **按可用宽度分两档，不按比例一路缩。** 面板最窄能到 200，
            // 那时三个 32 的按钮会把文件名挤到只剩五个字。大件缩得比小件快。
            float avail = ImGui.GetContentRegionAvail().X;
            bool roomy = avail - CardPad * 2f >= RoomyWidth;
            float buttonSize = roomy ? ButtonSizeWide : ButtonSizeNarrow;
            float buttonGap = roomy ? ButtonGapWide : ButtonGapNarrow;
            float contentH = Mathf.Max(buttonSize, TextLine);
            float cardH = CardPad * 2f + contentH;
            float buttonArea = 3f * buttonSize + 2f * buttonGap;

            for (int i = 0; i < _entries.Count; i++)
            {
                ImGuiDecorationEntry entry = _entries[i];
                ImGui.PushID(i);
                NVec2 card = new NVec2(contentLeft, rowY);
                NVec2 cardMax = new NVec2(card.X + avail, card.Y + cardH);
                // 内容区：卡片四边各让开 CardPad。
                NVec2 cMin = new NVec2(card.X + CardPad, card.Y + CardPad);
                float cW = avail - CardPad * 2f;
                float textW = Mathf.Max(40f, cW - buttonArea - buttonGap);

                draw.ChannelsSplit(2);
                draw.ChannelsSetCurrent(1);

                bool selected = entry.Selected;
                // **Selectable 自带的底色压成透明。** 不压的话选中的行会出现
                // 两块高亮：它自己那块（只盖住文字区）+ 我们画的卡片底衬。
                ImGui.PushStyleColor(ImGuiCol.Header, new NVec4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new NVec4(1f, 1f, 1f, 0.04f));
                ImGui.PushStyleColor(ImGuiCol.HeaderActive, new NVec4(0f, 0f, 0f, 0f));
                ImGui.SetCursorScreenPos(cMin);
                bool rowClicked = ImGui.Selectable("##row", selected,
                    ImGuiSelectableFlags.None, new NVec2(textW, contentH));
                ImGui.PopStyleColor(3);
                if (rowClicked && source != null)
                {
                    source.ActivateEntry(source.EntryFor(entry.Source));
                }

                // 名字自己画 —— Selectable 的标签放不下。裁切是安全网：
                // FitText 正常已经处理了溢出。
                //
                // **卡片里只剩这一行了。** 原来下面还有一行"第 N 格 · 已隐藏 · tag"，
                // 去掉的原因：隐藏/锁定现在由行尾那两个图标直接表示（闭眼/闭锁），
                // 文字再写一遍是重复；而装饰物大多在第 0 格，格号也没多少信息量。
                // 垂直居中，和右边那三个按钮对齐。
                string title = FitText(entry.DisplayName ?? entry.Type.ToString(), textW);
                NVec2 titleSize = ImGui.CalcTextSize(title);
                draw.PushClipRect(cMin, new NVec2(cMin.X + textW, cMin.Y + contentH), true);
                draw.AddText(new NVec2(cMin.X, cMin.Y + (contentH - titleSize.Y) * 0.5f),
                    ImGui.GetColorU32(ToVec4(selected ? Palette.Accent : Palette.TextPrimary)),
                    title);
                draw.PopClipRect();

                // **三个按钮用绝对定位，不用 SameLine。**
                //
                // `SameLine` 之后再 `SetCursorPosY` 会破坏 ImGui 的行逻辑 ——
                // 它会认为这一项落在下一行，于是按钮被挤到卡片外面去。
                //
                // 按钮画的是**原版图标**（拿不到就用我们自己画的，见 DrawIcon）。
                //
                // **图标跟着状态变**：已隐藏显示闭眼、已锁定显示闭锁。
                // 两套图（睁/闭、开/闭）游戏那边和自绘那边都有，不用另画；
                // 状态取的是这一次点击之前的值，所以点完下一帧才翻过来，正常。
                float buttonY = cMin.Y + (contentH - buttonSize) * 0.5f;
                float buttonX = cMin.X + cW - buttonArea;

                bool eyeOpen = entry.Visible;
                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));
                if (ImGui.Button("##deco_visible", new NVec2(buttonSize, buttonSize)))
                {
                    if (source != null)
                    {
                        source.ToggleVisibleEntry(source.EntryFor(entry.Source));
                    }
                }
                DrawIcon(draw, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    eyeOpen ? _hasEyeOpen : _hasEyeClosed,
                    eyeOpen ? _iconEyeOpen : _iconEyeClosed,
                    eyeOpen ? EditorImGuiIcons.Drawn.EyeOpen : EditorImGuiIcons.Drawn.EyeClosed,
                    "隐", Palette.TextPrimary);
                buttonX += buttonSize + buttonGap;

                bool lockOpen = !entry.Locked;
                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));
                if (ImGui.Button("##deco_locked", new NVec2(buttonSize, buttonSize)))
                {
                    if (source != null)
                    {
                        source.ToggleLockedEntry(source.EntryFor(entry.Source));
                    }
                }
                DrawIcon(draw, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    lockOpen ? _hasLockOpen : _hasLockClosed,
                    lockOpen ? _iconLockOpen : _iconLockClosed,
                    lockOpen ? EditorImGuiIcons.Drawn.LockOpen : EditorImGuiIcons.Drawn.LockClosed,
                    "锁", Palette.TextPrimary);
                buttonX += buttonSize + buttonGap;

                ImGui.SetCursorScreenPos(new NVec2(buttonX, buttonY));
                if (ImGui.Button("##deco_delete", new NVec2(buttonSize, buttonSize)))
                {
                    if (source != null)
                    {
                        source.DeleteEntry(source.EntryFor(entry.Source));
                    }
                }
                DrawIcon(draw, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    _hasDelete, _iconDelete, EditorImGuiIcons.Drawn.Delete, "删",
                    Palette.TextPrimary);

                draw.ChannelsSetCurrent(0);
                draw.AddRectFilled(card, cardMax,
                    ImGui.GetColorU32(ToVec4(selected ? Palette.Selected : Palette.Raised)),
                    EditorStudioOverlay.CornerSmall);
                draw.ChannelsMerge();

                // 下一张卡的顶边：本张卡底 + 卡间距。
                rowY = cardMax.Y + CardGap;
                ImGui.SetCursorScreenPos(new NVec2(contentLeft, rowY));
                ImGui.Dummy(new NVec2(0f, 0f));
                ImGui.PopID();
            }
        }

        private static void PushText(ImGuiCol slot, Color color)
        {
            ImGui.PushStyleColor(slot, ToVec4(color));
        }

    }
}
