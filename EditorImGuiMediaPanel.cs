using System.Collections.Generic;
using ADOFAIEditorStudio.Ui;
using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 媒体页的 ImGui 版**第一段：网格显示 + 点选**。
    ///
    /// 数据一份都不复制 —— 筛选后的列表、缩略图缓存、选中状态全部来自
    /// EditorStudioOverlay。缩略图是**异步解码**的（每帧限量、带 LRU 淘汰），
    /// 复制一份队列出来只会两边互相抢。
    ///
    /// 尺寸也共用 overlay 里那组常量（现在都是 internal），免得两边排出来的
    /// 列数和格子大小对不上。
    ///
    /// **这一段没做拖拽**：uGUI 那版能从这里把图拖进画布变成装饰物
    /// （TickMediaCardDrag / FindMediaCardAt），留到下一段。
    /// </summary>
    internal sealed class EditorImGuiMediaPanel
    {
        private Rect _rect;
        /// <summary>搜索框的缓冲。ImGui 每次按键都回传文本，得跨帧留住。</summary>
        private string _search = string.Empty;
        /// <summary>
        /// 这一轮拖动是从哪个媒体项起手的（没有就是 null）。
        ///
        /// 只为"同一轮里别重复武装"用 —— `ArmMediaDrop` 内部会先
        /// `CancelMediaDrop`，每帧调一次等于每帧把拖放重建一遍。
        /// </summary>
        private string _draggingPath;

        /// <summary>
        /// 拖动的判定距离。**和 uGUI 那版同一个数**（TickMediaCardDrag 里的 6f）——
        /// 太小会和"点一下选中"抢，太大拖起来发滞。
        /// </summary>
        private const float MediaDragThreshold = 6f;

        /// <summary>头部行高。和 uGUI 那条搜索行对齐（CreateMediaSearchControls 里的 searchHeight）。</summary>
        private const float SearchRowHeight = 24f;
        /// <summary>搜索框左右各留多少。uGUI 那边也是 12，没跟着面板内容左缘走。</summary>
        private const float SearchInset = 12f;
        /// <summary>计数那一格的宽。</summary>
        private const float CountWidth = 48f;
        /// <summary>
        /// 刷新按钮的宽。**比 uGUI 那个宽**：那边画的是 "↻"（30 就够），
        /// 而这个符号不在我们的字体子集里，只能写"刷新"两个字。
        /// </summary>
        private const float RefreshWidth = 44f;
        private const float RowGap = 12f;

        internal Rect Rect { get { return _rect; } }

        internal void Draw()
        {
            // 松手、或者拖放那边自己取消了，就把这一轮的标记清掉 ——
            // 下一次按下才能重新武装。**每帧都判**，不依赖"收到 mouse-up 事件"，
            // 免得漏一次（失焦、alt-tab）之后拖拽就再也不响应了。
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)
                || !EditorSceneInteraction.IsMediaDropActive)
            {
                _draggingPath = null;
            }

            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || !overlay.IsMediaPane)
            {
                _rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }

            float x, y, width, height;
            overlay.GetMediaRect(out x, out y, out width, out height);
            _rect = new Rect(x, y, width, height);

            ImGui.SetNextWindowPos(new NVec2(x, y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Always);
            if (ImGui.Begin("##aes_media", ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus))
            {
                ImGui.SeparatorText("媒体");
                DrawHeader(overlay);
                ImGui.BeginChild("##media_scroll", new NVec2(0f, 0f),
                    ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);
                DrawGrid(overlay);
                ImGui.EndChild();
            }
            ImGui.End();
        }

        /// <summary>
        /// 一行：[留白][搜索框][计数][刷新][留白]。
        /// 计数夹在搜索框和刷新之间、右对齐 —— 和 uGUI 那条行同一个排法。
        /// </summary>
        private void DrawHeader(EditorStudioOverlay overlay)
        {
            float avail = ImGui.GetContentRegionAvail().X;

            // 计数先算出来 —— **搜索框的宽度是拿"给计数留多少"倒推的**，
            // 而搜索生效时计数会从纯数字变成「命中 / 总数」，比数字宽得多。
            // 先按固定的 CountWidth 排版、再往里塞长文本，右对齐会让它向左长，
            // 直接压到搜索框上。
            int count = overlay.VisibleMedia.Count;
            string countText = string.IsNullOrEmpty(overlay.MediaSearchQuery)
                ? count.ToString()
                : count + " / " + overlay.MediaTotalCount;
            NVec2 countSize = ImGui.CalcTextSize(countText);
            float countSlot = Mathf.Max(CountWidth, countSize.X);

            // 两个 RowGap：一个在搜索框和计数之间，一个在计数和刷新之间。
            // 只留一个的话计数右端会正好贴着刷新按钮。
            float searchWidth = Mathf.Max(40f,
                avail - SearchInset * 2f - RefreshWidth - countSlot - RowGap * 2f);

            NVec2 row = ImGui.GetCursorScreenPos();
            float y = row.Y;

            ImGui.SetCursorScreenPos(new NVec2(row.X + SearchInset, y));
            ImGui.SetNextItemWidth(searchWidth);
            if (ImGui.InputTextWithHint("##media_search", "搜索文件名",
                ref _search, 128))
            {
                overlay.SetMediaSearch(_search);
            }

            // 计数：右对齐在它自己那一格里。
            ImGui.SetCursorScreenPos(new NVec2(
                row.X + SearchInset + searchWidth + RowGap + countSlot - countSize.X, y));
            ImGui.TextColored(ToVec4(Palette.AccentSecondary), countText);

            ImGui.SetCursorScreenPos(new NVec2(
                row.X + avail - SearchInset - RefreshWidth, y));
            if (ImGui.Button("刷新", new NVec2(RefreshWidth, SearchRowHeight)))
            {
                overlay.RefreshMedia();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("重新扫描谱面目录里的图片");
            }

            // 光标推到行下面 —— 上面全是绝对定位，不推的话下一块会叠上来。
            ImGui.SetCursorScreenPos(new NVec2(row.X, y + SearchRowHeight));
            ImGui.Dummy(new NVec2(0f, 0f));
        }

        private void DrawGrid(EditorStudioOverlay overlay)
        {
            List<EditorProjectMediaItem> items = overlay.VisibleMedia;
            float viewportWidth = ImGui.GetContentRegionAvail().X;
            if (items.Count == 0)
            {
                ImGui.Dummy(new NVec2(0f, 8f));
                ImGui.TextColored(ToVec4(Palette.TextMuted),
                    string.IsNullOrEmpty(overlay.MediaSearchQuery)
                        ? "谱面目录里没有找到图片"
                        : "没有匹配的图片");
                return;
            }

            // 列数和格子尺寸全部按 overlay 那组公式算 —— 公式只有一份。
            int columns = EditorStudioOverlay.MediaColumnsFor(viewportWidth);
            float cardWidth = (viewportWidth
                - EditorStudioOverlay.MediaGridPadding * 2f
                - EditorStudioOverlay.MediaGridColumnGap * (columns - 1)) / columns;
            float cardHeight = EditorStudioOverlay.MediaCardHeight
                - EditorStudioOverlay.MediaGridRowGap;
            float labelTop = EditorStudioOverlay.MediaCardHeight
                - EditorStudioOverlay.MediaGridRowGap - EditorStudioOverlay.MediaCardLabelHeight;
            float thumb = Mathf.Max(16f, Mathf.Min(
                cardWidth - EditorStudioOverlay.MediaCardInset * 2f,
                labelTop - EditorStudioOverlay.MediaCardInset));

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            NVec2 origin = ImGui.GetCursorScreenPos();
            string selected = overlay.SelectedMediaPath;

            for (int i = 0; i < items.Count; i++)
            {
                EditorProjectMediaItem item = items[i];
                int rowIndex = i / columns;
                int columnIndex = i % columns;
                NVec2 min = new NVec2(
                    origin.X + EditorStudioOverlay.MediaGridPadding
                        + columnIndex * (cardWidth + EditorStudioOverlay.MediaGridColumnGap),
                    origin.Y + EditorStudioOverlay.MediaGridPadding
                        + rowIndex * EditorStudioOverlay.MediaCardHeight);
                NVec2 max = new NVec2(min.X + cardWidth, min.Y + cardHeight);

                bool isSelected = string.Equals(selected, item.RelativePath,
                    System.StringComparison.OrdinalIgnoreCase);

                // 整格当点击目标。**ImGui 自己那套按钮样式全绕开** ——
                // 卡片的底衬我们自己画，用了按钮会多出一层和它对不齐的底色。
                ImGui.PushID(i);
                ImGui.SetCursorScreenPos(min);
                ImGui.InvisibleButton("##cell", new NVec2(cardWidth, cardHeight));
                bool hovered = ImGui.IsItemHovered();

                // **拖动开始**：按住这一格、移开超过阈值，就把"媒体拖放"武装起来。
                //
                // 落点判定、创建装饰物、松手提交 —— **这些一行都不用我们写**，
                // `EditorSceneInteraction` 自己的 Tick 里全有（它直接读
                // `Input.mousePosition`，和 ImGui 没关系）。这里只负责在正确的
                // 时机告诉它"开始拖了"，调的是和 uGUI 那版**同一个入口**。
                //
                // 为什么不用自己量位移：`IsMouseDragging` 就是"按下之后移开超过
                // 阈值"，ImGui 每帧在算，不用我们再存一个按下点。
                if (ImGui.IsItemActive()
                    && ImGui.IsMouseDragging(ImGuiMouseButton.Left, MediaDragThreshold)
                    && !string.Equals(_draggingPath, item.RelativePath,
                        System.StringComparison.Ordinal))
                {
                    _draggingPath = item.RelativePath;
                    EditorSceneInteraction.ArmMediaDrop(item.FileName, item.AbsolutePath);
                }

                if (ImGui.IsItemClicked())
                {
                    overlay.ActivateMediaIndex(i);
                }
                ImGui.PopID();

                Color background = isSelected
                    ? Palette.Selected
                    : (hovered ? Palette.SelectedHover : Palette.Raised);
                draw.AddRectFilled(min, max, ImGui.GetColorU32(ToVec4(background)),
                    EditorStudioOverlay.CornerSmall);

                // 缩略图：正方形框，居中；图按自己的长宽比装进去。
                NVec2 thumbMin = new NVec2(min.X + (cardWidth - thumb) * 0.5f,
                    min.Y + EditorStudioOverlay.MediaCardInset);
                Texture2D texture;
                if (overlay.TryGetMediaThumbnail(item, out texture) && texture != null)
                {
                    float aspect = texture.height > 0
                        ? (float)texture.width / texture.height : 1f;
                    float drawWidth, drawHeight;
                    FitBox(thumb, thumb, aspect, out drawWidth, out drawHeight);
                    float tx = thumbMin.X + (thumb - drawWidth) * 0.5f;
                    float ty = thumbMin.Y + (thumb - drawHeight) * 0.5f;
                    // 贴图是我们自己解出来的普通 Unity 贴图，**纵轴要换头**
                    // （理由同 EditorImGuiIcons.FromSprite：ImGui 的 v 向下）。
                    draw.AddImage(EditorImGuiBridge.RegisterTexture(texture),
                        new NVec2(tx, ty),
                        new NVec2(tx + drawWidth, ty + drawHeight),
                        new NVec2(0f, 1f), new NVec2(1f, 0f), 0xFFFFFFFFu);
                }
                else
                {
                    // 还没解出来 / 解码失败 / 本来不给解（gif）。一律是这个凹槽。
                    draw.AddRectFilled(thumbMin,
                        new NVec2(thumbMin.X + thumb, thumbMin.Y + thumb),
                        ImGui.GetColorU32(ToVec4(Palette.ThumbWell)),
                        EditorStudioOverlay.CornerSmall);
                }

                string label = FitText(item.FileName, cardWidth - EditorStudioOverlay.MediaCardInset * 2f);
                NVec2 labelSize = ImGui.CalcTextSize(label);
                draw.AddText(
                    new NVec2(min.X + (cardWidth - labelSize.X) * 0.5f, min.Y + labelTop),
                    ImGui.GetColorU32(ToVec4(isSelected ? Palette.TextPrimary : Palette.TextSecondary)),
                    label);
            }

            // 把内容高度撑到最后一行下面 —— 不撑的话滚动条算不出总长。
            int rows = (items.Count + columns - 1) / columns;
            ImGui.SetCursorScreenPos(new NVec2(origin.X,
                origin.Y + EditorStudioOverlay.MediaGridPadding * 2f
                    + rows * EditorStudioOverlay.MediaCardHeight));
            ImGui.Dummy(new NVec2(0f, 0f));
        }
    }
}
