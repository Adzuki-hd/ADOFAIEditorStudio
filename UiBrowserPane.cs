using System;
using System.Collections.Generic;
using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 工作台左栏（标签条 + 装饰物页 + 媒体页）的 uGUI 版 —— **按 ImGui 现行实现重写的一份新代码**。
    ///
    /// 为什么不复用 `EditorStudioOverlay.BuildBrowserTabs / CreateMediaSearchControls`
    /// 和 `EditorDecorationList` 里那套行 UI：那是 ImGui 改造之前的旧骨架，
    /// 版式和现在的真值不一致（一行两行文字、色标、圆角卡片、按钮尺寸都不同），
    /// 直接打开旧代码只会把过时的结构一起带回来。
    ///
    /// 数据与副作用一律走向已有的入口：
    ///   · 装饰物：`EditorDecorationList.FillImGuiEntries` 读快照，
    ///     增删改 / 搜索 / 批量走它那几个 `FromImGui` 入口；
    ///   · 媒体：`EditorStudioOverlay.VisibleMedia` + 缩略图缓存 + `ArmMediaDrop`；
    ///   · 标签：`BrowserTabTitles / CurrentBrowserTabIndex / SwitchBrowserTabByIndex`。
    ///
    /// 本文件只画，不另存一份状态。唯一跨帧的缓存是滚动位置和行池。
    /// </summary>
    internal sealed class UiBrowserPane : MonoBehaviour
    {
        // ── 标签条 ────────────────────────────────────────────────────────
        // 行高 / 内边距 / 间距全部照 ImGui 那版的数（它已经把位置调到和标题行一致）。
        private const float TabRowHeight = 30f;
        private const float TabPadX = 8f;
        private const float TabGap = 10f;
        private const float TabFontSize = 13f;
        private const float TabUnderlineHeight = 2f;
        /// <summary>标签条贴在内容区上沿再往上 30 —— ImGui 版就是 listY - 30。</summary>
        private const float TabRowOffsetY = -30f;

        // ── 装饰物页 ──────────────────────────────────────────────────────
        private const float TileSize = 32f;
        private const float TileGap = 8f;
        private const float TileRowY = 0f;
        private const float CountWidth = 64f;
        private const float SearchTop = 40f;
        private const float SearchHeight = 30f;
        private const float BatchTop = 78f;
        private const float BatchHeight = 32f;
        private const float ListTop = 120f;

        // 卡片：内边距 < 卡间距，眼睛才分得清组内组外（ImGui 版的原话）。
        private const float CardPad = 12f;
        private const float CardGap = 16f;
        private const float TextLine = 24f;
        private const float ButtonSizeWide = 32f;
        private const float ButtonSizeNarrow = 24f;
        private const float ButtonGapWide = 8f;
        private const float ButtonGapNarrow = 4f;
        private const float RoomyWidth = 240f;
        private const float IconSize = 16f;
        private const float NameFontSize = 13f;

        // ── 媒体页 ────────────────────────────────────────────────────────
        private const float MediaHeaderHeight = 30f;
        private const float MediaSearchInset = 12f;
        private const float MediaRefreshWidth = 44f;
        private const float MediaRowGap = 12f;
        private const float MediaCountWidth = 48f;
        private const float MediaLabelFontSize = 11f;
        private const float MediaGridTop = 42f;
        private const float MediaDragThreshold = 6f;

        private static Color TextPrimary { get { return UiTheme.Current.TextPrimary; } }
        private static Color TextSecondary { get { return UiTheme.Current.TextSecondary; } }
        private static Color TextMuted { get { return UiTheme.Current.TextMuted; } }
        private static Color Accent { get { return UiTheme.Current.AccentSecondary; } }
        private static Color SelectedFill { get { return UiTheme.Current.SelectedFill; } }
        private static Color HoverFillColor { get { return new Color(1f, 1f, 1f, 0.08f); } }
        private static Color Hairline { get { return UiTheme.Current.BorderHairline; } }

        // ── 注入依赖 ──────────────────────────────────────────────────────
        private EditorStudioOverlay overlay;
        private EditorDecorationList model;
        private TMP_FontAsset font;

        // ── 容器 ──────────────────────────────────────────────────────────
        private RectTransform root;
        private RectTransform tabHost;
        private RectTransform tabContent;
        private RectTransform decoPage;
        private RectTransform mediaPage;
        private float width;
        private float height;

        // ── 标签条 ────────────────────────────────────────────────────────
        private readonly List<TabItem> tabs = new List<TabItem>();
        private float tabScrollX;
        private float tabContentWidth;
        /// <summary>上一次按哪个宽度排的标签。宽度没变就不重排。</summary>
        private float tabLayoutWidth = -1f;

        // ── 装饰物页 ──────────────────────────────────────────────────────
        private readonly List<ImGuiDecorationEntry> entries = new List<ImGuiDecorationEntry>();
        private readonly List<DecoRow> decoRows = new List<DecoRow>();
        private TextMeshProUGUI decoCount;
        private TMP_InputField decoSearch;
        private TextMeshProUGUI hideAllLabel;
        private TextMeshProUGUI lockAllLabel;
        private RectTransform decoViewport;
        private RectTransform decoContent;
        private ScrollRect decoScroll;

        // ── 媒体页 ────────────────────────────────────────────────────────
        private readonly List<MediaCard> mediaCards = new List<MediaCard>();
        private TextMeshProUGUI mediaCount;
        private TextMeshProUGUI mediaEmpty;
        private TMP_InputField mediaSearch;
        private RectTransform mediaViewport;
        private RectTransform mediaContent;
        private ScrollRect mediaScroll;
        private EditorProjectMediaItem mediaDragCandidate;
        private Vector2 mediaDragPressScreen;
        private bool mediaDragArmed;

        // ── 对外开关 ──────────────────────────────────────────────────────

        /// <summary>切页。设置页时两页都收起来（设置页正文由参数栏渲染器画）。</summary>
        internal void ShowPage(EditorStudioOverlay.BrowserPane pane)
        {
            ApplyPageVisibility();
            RefreshTabColors();
        }

        /// <summary>
        /// 页面的尺寸入口。左栏高度跟着时间轴走，拖一次就要重排一次；
        /// 只在建库时算一遍的话，压扁之后列表还停在旧高度上。
        /// </summary>
        internal void SetPaneRect(float x, float y, float paneWidth, float paneHeight)
        {
            if (root == null)
            {
                return;
            }
            width = Mathf.Max(80f, paneWidth);
            height = Mathf.Max(80f, paneHeight);
            SetTopLeft(root, x, y, width, height);
            Relayout();
        }

        private void ApplyPageVisibility()
        {
            if (tabHost != null && !tabHost.gameObject.activeSelf)
            {
                tabHost.gameObject.SetActive(true);
            }
            if (decoPage != null)
            {
                bool wanted = overlay != null && overlay.IsDecorationPane;
                if (decoPage.gameObject.activeSelf != wanted)
                {
                    decoPage.gameObject.SetActive(wanted);
                }
            }
            if (mediaPage != null)
            {
                bool wanted = overlay != null && overlay.IsMediaPane;
                if (mediaPage.gameObject.activeSelf != wanted)
                {
                    mediaPage.gameObject.SetActive(wanted);
                }
            }
        }

        // ── 建立 ──────────────────────────────────────────────────────────

        internal static UiBrowserPane Build(RectTransform parent, EditorStudioOverlay owner,
            EditorDecorationList decorationModel, TMP_FontAsset uiFont,
            float x, float y, float paneWidth, float paneHeight)
        {
            RectTransform rect = CreateRect("BrowserPane", parent, x, y, paneWidth, paneHeight);
            UiBrowserPane pane = rect.gameObject.AddComponent<UiBrowserPane>();
            pane.Initialize(owner, decorationModel, uiFont, paneWidth, paneHeight);
            pane.SetPaneRect(x, y, paneWidth, paneHeight);
            return pane;
        }

        private void Initialize(EditorStudioOverlay owner, EditorDecorationList decorationModel,
            TMP_FontAsset uiFont, float paneWidth, float paneHeight)
        {
            overlay = owner;
            model = decorationModel;
            font = uiFont;
            width = Mathf.Max(80f, paneWidth);
            height = Mathf.Max(80f, paneHeight);
            root = GetComponent<RectTransform>();

            BuildTabStrip();
            BuildDecorationPage();
            BuildMediaPage();

            ApplyPageVisibility();
            RefreshTabColors();
        }

        /// <summary>按当前宽高把所有子件重摆一遍。建库和拖时间轴后都走这里。</summary>
        private void Relayout()
        {
            if (root == null)
            {
                return;
            }

            // 标签条：宽 = 内容区宽，高 30，贴在内容区上沿往上 30。
            if (tabHost != null)
            {
                SetTopLeft(tabHost, 0f, TabRowOffsetY, width, TabRowHeight);
                tabLayoutWidth = -1f;
            }

            if (decoPage != null)
            {
                SetTopLeft(decoPage, 0f, 0f, width, height);
                if (decoCount != null)
                {
                    SetTopLeft(decoCount.rectTransform, width - CountWidth, TileRowY,
                        CountWidth, TileSize);
                }
                if (decoSearch != null)
                {
                    SetTopLeft(decoSearch.GetComponent<RectTransform>(), 0f, SearchTop,
                        width, SearchHeight);
                }
                float halfWidth = Mathf.Max(40f, (width - 8f) * 0.5f);
                RectTransform hideAll = hideAllLabel == null
                    ? null : hideAllLabel.transform.parent as RectTransform;
                RectTransform lockAll = lockAllLabel == null
                    ? null : lockAllLabel.transform.parent as RectTransform;
                if (hideAll != null)
                {
                    SetTopLeft(hideAll, 0f, BatchTop, halfWidth, BatchHeight);
                }
                if (lockAll != null)
                {
                    SetTopLeft(lockAll, halfWidth + 8f, BatchTop, halfWidth, BatchHeight);
                }
                if (decoScroll != null)
                {
                    SetTopLeft(decoScroll.GetComponent<RectTransform>(), 0f, ListTop,
                        width, Mathf.Max(32f, height - ListTop));
                }
            }

            if (mediaPage != null)
            {
                SetTopLeft(mediaPage, 0f, 0f, width, height);
                if (mediaScroll != null)
                {
                    SetTopLeft(mediaScroll.GetComponent<RectTransform>(), 0f, MediaGridTop,
                        width, Mathf.Max(32f, height - MediaGridTop));
                }
                if (mediaEmpty != null)
                {
                    SetTopLeft(mediaEmpty.rectTransform, 0f, 8f,
                        Mathf.Max(40f, width), 24f);
                }
                UpdateMediaHeaderLayout();
            }
        }

        // ── 标签条 ────────────────────────────────────────────────────────

        private sealed class TabItem
        {
            internal RectTransform Rect;
            internal TextMeshProUGUI Label;
            internal Image Underline;
            internal int Index;
        }

        private void BuildTabStrip()
        {
            tabHost = CreateRect("TabStrip", root, 0f, TabRowOffsetY, width, TabRowHeight);
            tabHost.gameObject.AddComponent<RectMask2D>();

            tabContent = CreateRect("Content", tabHost, 0f, 0f, width, TabRowHeight);
            TabScroller scroller = tabHost.gameObject.AddComponent<TabScroller>();
            scroller.Owner = this;
        }

        private void EnsureTabs()
        {
            string[] titles = EditorStudioOverlay.BrowserTabTitles();
            if (titles == null || titles.Length == 0 || tabs.Count == titles.Length)
            {
                return;
            }

            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Rect != null)
                {
                    Destroy(tabs[i].Rect.gameObject);
                }
            }
            tabs.Clear();
            for (int i = 0; i < titles.Length; i++)
            {
                AddTab(i, titles[i]);
            }
            tabLayoutWidth = -1f;
        }

        private void AddTab(int index, string title)
        {
            RectTransform rect = CreateRect("Tab" + index, tabContent, 0f, 0f, 80f, TabRowHeight);

            // 悬停反馈用一层极淡白，不做常驻底色。
            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = true;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);

            TextMeshProUGUI label = CreateText("Label", rect, TabPadX, 0f, 80f, TabRowHeight,
                title, TabFontSize, TextMuted, TextAlignmentOptions.MidlineLeft);
            label.overflowMode = TextOverflowModes.Ellipsis;

            Image underline = CreateImage("Underline", rect, TabPadX,
                TabRowHeight - TabUnderlineHeight, 20f, TabUnderlineHeight, Accent);
            underline.raycastTarget = false;
            underline.enabled = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hover;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            int captured = index;
            button.onClick.AddListener(delegate { ClickTab(captured); });
            AttachHover(rect.gameObject, hover, HoverFillColor);

            TabItem item = new TabItem();
            item.Rect = rect;
            item.Label = label;
            item.Underline = underline;
            item.Index = index;
            tabs.Add(item);
        }

        private void ClickTab(int index)
        {
            if (overlay != null)
            {
                overlay.SwitchBrowserTabByIndex(index);
            }
        }

        /// <summary>
        /// 重排标签：宽度按文字实际宽度量，超出可视区就靠滚动偏移看。
        /// 顺序、间隙和 ImGui 那版一致（PadX 8 / Gap 10）。
        /// </summary>
        private void LayoutTabs()
        {
            if (tabContent == null || tabHost == null)
            {
                return;
            }
            float hostWidth = Mathf.Max(1f, tabHost.rect.width);
            if (Mathf.Approximately(tabLayoutWidth, hostWidth) && tabContentWidth > 0f)
            {
                return;
            }
            tabLayoutWidth = hostWidth;

            float cursor = 0f;
            for (int i = 0; i < tabs.Count; i++)
            {
                TabItem item = tabs[i];
                float textWidth = item.Label.GetPreferredValues(
                    item.Label.text, 4000f, TabRowHeight).x;
                float itemWidth = textWidth + TabPadX * 2f;
                SetTopLeft(item.Rect, cursor, 0f, itemWidth, TabRowHeight);
                SetTopLeft(item.Label.rectTransform, TabPadX, 0f,
                    Mathf.Max(8f, itemWidth - TabPadX * 2f), TabRowHeight);
                SetTopLeft(item.Underline.rectTransform, TabPadX,
                    TabRowHeight - TabUnderlineHeight,
                    Mathf.Max(8f, itemWidth - TabPadX * 2f), TabUnderlineHeight);
                cursor += itemWidth + TabGap;
            }
            tabContentWidth = Mathf.Max(0f, cursor - TabGap);
            tabContent.sizeDelta = new Vector2(
                Mathf.Max(hostWidth, tabContentWidth), TabRowHeight);
            ClampTabScroll();
            RefreshTabColors();
        }

        private float MaxTabScroll
        {
            get { return Mathf.Max(0f, tabContentWidth - tabHost.rect.width); }
        }

        private void ClampTabScroll()
        {
            tabScrollX = Mathf.Clamp(tabScrollX, 0f, MaxTabScroll);
            if (tabContent != null)
            {
                tabContent.anchoredPosition = new Vector2(-tabScrollX, 0f);
            }
        }

        private void ScrollTabs(float delta)
        {
            tabScrollX += delta;
            ClampTabScroll();
        }

        private void RefreshTabColors()
        {
            int current = overlay == null ? 0 : overlay.CurrentBrowserTabIndex();
            for (int i = 0; i < tabs.Count; i++)
            {
                TabItem item = tabs[i];
                bool active = item.Index == current;
                if (item.Label != null)
                {
                    item.Label.color = active ? Accent : TextMuted;
                }
                if (item.Underline != null)
                {
                    item.Underline.enabled = active;
                }
            }
        }

        /// <summary>
        /// 标签条的横滚：滚轮按格偏移，左键拖动是「抓住内容拖」。
        /// 和 ImGui 那版同一套手感（滚轮 40 一档）。
        /// </summary>
        private sealed class TabScroller : MonoBehaviour, IScrollHandler, IDragHandler
        {
            internal UiBrowserPane Owner;

            public void OnScroll(PointerEventData eventData)
            {
                if (Owner == null)
                {
                    return;
                }
                Owner.ScrollTabs(-eventData.scrollDelta.y * 40f);
                eventData.Use();
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (Owner == null)
                {
                    return;
                }
                Owner.ScrollTabs(-eventData.delta.x);
                eventData.Use();
            }
        }

        // ── 装饰物页 ──────────────────────────────────────────────────────

        private void BuildDecorationPage()
        {
            decoPage = CreateRect("Decorations", root, 0f, 0f, width, height);

            // 第一行：四个新建方块 + 计数。方块画事件类型图标，名字不出文字
            // （32 见方放不下两个汉字，和 ImGui 版同样处理）。
            for (int i = 0; i < EditorDecorationList.TypeCount; i++)
            {
                int slot = i;
                RectTransform rect = CreateRect("Create" + i, decoPage,
                    i * (TileSize + TileGap), TileRowY, TileSize, TileSize);

                Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
                hover.raycastTarget = true;
                Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);

                Image icon = CreateImage("Icon", rect, (TileSize - IconSize) * 0.5f,
                    (TileSize - IconSize) * 0.5f, IconSize, IconSize, TextPrimary);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                Sprite typeIcon = TypeIconOf(EditorDecorationList.TypeAt(i));
                icon.sprite = typeIcon;
                icon.enabled = typeIcon != null;

                TextMeshProUGUI fallback = CreateText("Fallback", rect, 0f, 0f,
                    TileSize, TileSize, FirstCharacter(EditorDecorationList.TypeNameAt(i)),
                    11f, TextPrimary, TextAlignmentOptions.Center);
                fallback.raycastTarget = false;
                fallback.gameObject.SetActive(typeIcon == null);

                Button button = rect.gameObject.AddComponent<Button>();
                button.targetGraphic = hover;
                button.transition = Selectable.Transition.None;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                button.onClick.AddListener(delegate { CreateDecoration(slot); });
                AttachHover(rect.gameObject, hover, HoverFillColor);
            }

            decoCount = CreateText("Count", decoPage,
                width - CountWidth, TileRowY, CountWidth, TileSize,
                "0", 12f, Accent, TextAlignmentOptions.MidlineRight);

            decoSearch = CreateSearchField(decoPage, "DecoSearch", 0f, SearchTop,
                width, SearchHeight, "搜索（type: / tag: / floor:）", OnDecoSearchChanged);

            // 第三行：两个批量开关。只改运行时状态，不写谱面。
            float halfWidth = Mathf.Max(40f, (width - 8f) * 0.5f);
            CreateBatchButton("HideAll", 0f, halfWidth, "全部隐藏", out hideAllLabel,
                delegate { ToggleBatchHidden(); });
            CreateBatchButton("LockAll", halfWidth + 8f, halfWidth, "全部锁定", out lockAllLabel,
                delegate { ToggleBatchLocked(); });

            decoScroll = CreateScroll("DecoScroll", decoPage, 0f, ListTop,
                width, Mathf.Max(32f, height - ListTop), out decoViewport, out decoContent);
        }

        private void CreateBatchButton(string name, float x, float w, string label,
            out TextMeshProUGUI text, Action action)
        {
            RectTransform rect = CreateRect(name, decoPage, x, BatchTop, w, BatchHeight);

            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = true;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);

            text = CreateText("Label", rect, 0f, 0f, w, BatchHeight, label,
                13f, TextSecondary, TextAlignmentOptions.Center);
            text.raycastTarget = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hover;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { action(); });
            AttachHover(rect.gameObject, hover, HoverFillColor);
        }

        private sealed class DecoRow
        {
            internal RectTransform Rect;
            internal Image Background;
            internal TextMeshProUGUI Name;
            internal IconSlot Eye;
            internal IconSlot Lock;
            internal IconSlot Delete;
            internal int BoundIndex = -1;
        }

        private void EnsureDecoRows(int required)
        {
            while (decoRows.Count < required)
            {
                decoRows.Add(CreateDecoRow(decoRows.Count));
            }
        }

        private DecoRow CreateDecoRow(int index)
        {
            DecoRow row = new DecoRow();
            RectTransform rect = CreateRect("Row" + index, decoContent, 0f, 0f, 100f, 48f);
            row.Rect = rect;

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;
            row.Background = background;

            // 悬停那层：只叠白，不参与选中态（选中靠 Background 填 14%）。
            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = false;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);

            row.Name = CreateText("Name", rect, CardPad, 0f, 100f, TextLine,
                string.Empty, NameFontSize, TextPrimary, TextAlignmentOptions.MidlineLeft);
            row.Name.raycastTarget = false;
            row.Name.overflowMode = TextOverflowModes.Ellipsis;

            row.Eye = CreateIconSlot(rect, "Eye");
            row.Lock = CreateIconSlot(rect, "Lock");
            row.Delete = CreateIconSlot(rect, "Delete");

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            DecoRow capturedRow = row;
            button.onClick.AddListener(delegate { ActivateRow(capturedRow); });
            AttachHover(rect.gameObject, hover, HoverFillColor);

            row.Eye.Button.onClick.AddListener(delegate { ToggleVisible(capturedRow); });
            row.Lock.Button.onClick.AddListener(delegate { ToggleLocked(capturedRow); });
            row.Delete.Button.onClick.AddListener(delegate { DeleteRow(capturedRow); });

            // **新行默认收起。** 池子是按可见区预建的，建出来就激活的话，
            // 列表为空时左边会留一行空壳（三个图标槽还停在 (0,0)，白方块就是它们）。
            rect.gameObject.SetActive(false);
            return row;
        }

        private sealed class IconSlot
        {
            internal RectTransform Rect;
            internal Image Icon;
            internal TextMeshProUGUI Fallback;
            internal Button Button;
            internal string SpriteName;
            internal string Text;
        }

        private IconSlot CreateIconSlot(RectTransform parent, string name)
        {
            IconSlot slot = new IconSlot();
            RectTransform rect = CreateRect(name, parent, 0f, 0f, ButtonSizeWide, ButtonSizeWide);
            slot.Rect = rect;

            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = true;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);

            Image icon = CreateImage("Icon", rect, 0f, 0f, IconSize, IconSize, TextPrimary);
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            // **没贴图就别画。** Image 没有 sprite 时是一块纯白实心方块，
            // 在深色行上非常显眼 —— 那正是"左边多了个白色色块"。
            icon.enabled = false;
            slot.Icon = icon;

            TextMeshProUGUI fallback = CreateText("Fallback", rect, 0f, 0f,
                ButtonSizeWide, ButtonSizeWide, string.Empty, 11f, TextPrimary,
                TextAlignmentOptions.Center);
            fallback.raycastTarget = false;
            slot.Fallback = fallback;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hover;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            slot.Button = button;
            AttachHover(rect.gameObject, hover, HoverFillColor);
            return slot;
        }

        /// <summary>
        /// 装饰物页每帧同步。**只算看得见的那几行** —— 大谱面上装饰物成百上千，
        /// 每一行都写 TMP 文本会把主线程拖住，而屏幕上一屏只有十行左右。
        /// </summary>
        private void SyncDecorations()
        {
            if (model == null || decoContent == null || decoViewport == null)
            {
                return;
            }

            model.FillImGuiEntries(entries);

            string countText = model.FilteredCount == model.TotalCount
                ? model.TotalCount.ToString()
                : model.FilteredCount + "/" + model.TotalCount;
            if (decoCount != null && decoCount.text != countText)
            {
                decoCount.text = countText;
            }
            UpdateBatchLabel(hideAllLabel, "全部隐藏", model.BatchHidden);
            UpdateBatchLabel(lockAllLabel, "全部锁定", model.BatchLocked);

            float viewportWidth = Mathf.Max(1f, decoViewport.rect.width);
            bool roomy = viewportWidth - CardPad * 2f >= RoomyWidth;
            float buttonSize = roomy ? ButtonSizeWide : ButtonSizeNarrow;
            float buttonGap = roomy ? ButtonGapWide : ButtonGapNarrow;
            float contentH = Mathf.Max(buttonSize, TextLine);
            float cardH = CardPad * 2f + contentH;
            float step = cardH + CardGap;

            float contentHeight = Mathf.Max(decoViewport.rect.height,
                entries.Count * step + CardGap);
            if (!Mathf.Approximately(decoContent.sizeDelta.y, contentHeight))
            {
                decoContent.sizeDelta = new Vector2(0f, contentHeight);
            }

            float scrollY = Mathf.Max(0f, decoContent.anchoredPosition.y);
            int first = Mathf.FloorToInt(scrollY / step);
            int visible = Mathf.CeilToInt(decoViewport.rect.height / step) + 2;
            int pool = Mathf.Clamp(visible + 1, 1, 32);
            EnsureDecoRows(pool);

            // 图标一帧只取一次：属性里带着「原版图标没就绪就重试」那一步，
            // 每行各问一遍等于每一帧问十几遍。
            spriteEyeOpen = model.EyeOpenSprite;
            spriteEyeClosed = model.EyeClosedSprite;
            spriteLockOpen = model.LockOpenSprite;
            spriteLockClosed = model.LockClosedSprite;
            spriteDelete = model.DeleteSprite;

            for (int i = 0; i < decoRows.Count; i++)
            {
                DecoRow row = decoRows[i];
                int index = first + i;
                bool use = index >= 0 && index < entries.Count && i < pool;
                if (!use)
                {
                    if (row.BoundIndex != -1)
                    {
                        row.BoundIndex = -1;
                        row.Rect.gameObject.SetActive(false);
                    }
                    continue;
                }
                if (!row.Rect.gameObject.activeSelf)
                {
                    row.Rect.gameObject.SetActive(true);
                }
                row.BoundIndex = index;
                BindDecoRow(row, entries[index], index, viewportWidth,
                    buttonSize, buttonGap, contentH, cardH, step);
            }
        }

        // 行上三个图标的一帧快照（见 SyncDecorations）。
        private Sprite spriteEyeOpen;
        private Sprite spriteEyeClosed;
        private Sprite spriteLockOpen;
        private Sprite spriteLockClosed;
        private Sprite spriteDelete;

        private static void UpdateBatchLabel(TextMeshProUGUI label, string name, bool on)
        {
            if (label == null)
            {
                return;
            }
            string text = on ? name + " ✓" : name;
            if (label.text != text)
            {
                label.text = text;
            }
            label.color = on ? Accent : TextSecondary;
        }

        private void BindDecoRow(DecoRow row, ImGuiDecorationEntry entry, int index,
            float viewportWidth, float buttonSize, float buttonGap,
            float contentH, float cardH, float step)
        {
            SetTopLeft(row.Rect, 0f, index * step, viewportWidth, cardH);

            // 卡片底：未选中透明，选中 14% 白。悬停由独立的 8% 白层叠上去 ——
            // 和整个界面的「默认无底、悬停才浮出」是同一条规则。
            row.Background.color = entry.Selected ? SelectedFill : Color.clear;

            float buttonArea = 3f * buttonSize + 2f * buttonGap;
            float textWidth = Mathf.Max(40f, viewportWidth - CardPad * 2f - buttonArea - buttonGap);
            SetTopLeft(row.Name.rectTransform, CardPad,
                CardPad + (contentH - TextLine) * 0.5f, textWidth, TextLine);
            row.Name.color = entry.Selected ? Accent : TextPrimary;

            string title = entry.DisplayName;
            if (string.IsNullOrEmpty(title))
            {
                title = entry.Type.ToString();
            }
            if (row.Name.text != title)
            {
                row.Name.text = title;
            }

            float buttonY = CardPad + (contentH - buttonSize) * 0.5f;
            float buttonX = viewportWidth - CardPad - buttonArea;
            BindIconSlot(row.Eye, buttonX, buttonY, buttonSize,
                entry.Visible ? spriteEyeOpen : spriteEyeClosed, "隐");
            buttonX += buttonSize + buttonGap;
            BindIconSlot(row.Lock, buttonX, buttonY, buttonSize,
                entry.Locked ? spriteLockClosed : spriteLockOpen, "锁");
            buttonX += buttonSize + buttonGap;
            BindIconSlot(row.Delete, buttonX, buttonY, buttonSize, spriteDelete, "删");
        }

        private static void BindIconSlot(IconSlot slot, float x, float y, float size,
            Sprite sprite, string fallback)
        {
            if (slot == null)
            {
                return;
            }
            SetTopLeft(slot.Rect, x, y, size, size);
            SetTopLeft(slot.Icon.rectTransform, (size - IconSize) * 0.5f,
                (size - IconSize) * 0.5f, IconSize, IconSize);
            SetTopLeft(slot.Fallback.rectTransform, 0f, 0f, size, size);

            string spriteName = sprite == null ? null : sprite.name;
            if (slot.SpriteName != spriteName)
            {
                slot.SpriteName = spriteName;
                slot.Icon.sprite = sprite;
            }
            // 显隐每次都要对齐：新槽位的 `SpriteName` 本来就是 null，取不到图标时
            // 上面那段会被跳过，于是 icon 一直停在「启用 + 无贴图」= 白方块。
            bool hasSprite = sprite != null;
            if (slot.Icon.enabled != hasSprite)
            {
                slot.Icon.enabled = hasSprite;
            }
            if (slot.Fallback.gameObject.activeSelf == hasSprite)
            {
                slot.Fallback.gameObject.SetActive(!hasSprite);
            }
            if (slot.Text != fallback)
            {
                slot.Text = fallback;
                slot.Fallback.text = fallback;
            }
        }

        private void ActivateRow(DecoRow row)
        {
            if (model == null || row == null || row.BoundIndex < 0 || row.BoundIndex >= entries.Count)
            {
                return;
            }
            model.ActivateEntry(model.EntryFor(entries[row.BoundIndex].Source));
        }

        private void ToggleVisible(DecoRow row)
        {
            if (model == null || row == null || row.BoundIndex < 0 || row.BoundIndex >= entries.Count)
            {
                return;
            }
            model.ToggleVisibleEntry(model.EntryFor(entries[row.BoundIndex].Source));
        }

        private void ToggleLocked(DecoRow row)
        {
            if (model == null || row == null || row.BoundIndex < 0 || row.BoundIndex >= entries.Count)
            {
                return;
            }
            model.ToggleLockedEntry(model.EntryFor(entries[row.BoundIndex].Source));
        }

        private void DeleteRow(DecoRow row)
        {
            if (model == null || row == null || row.BoundIndex < 0 || row.BoundIndex >= entries.Count)
            {
                return;
            }
            model.DeleteEntry(model.EntryFor(entries[row.BoundIndex].Source));
        }

        private void CreateDecoration(int slot)
        {
            if (model != null)
            {
                model.CreateDecorationFromImGui(EditorDecorationList.TypeAt(slot));
            }
        }

        private void ToggleBatchHidden()
        {
            if (model != null)
            {
                model.ToggleBatchHiddenFromImGui();
            }
        }

        private void ToggleBatchLocked()
        {
            if (model != null)
            {
                model.ToggleBatchLockedFromImGui();
            }
        }

        private void OnDecoSearchChanged(string value)
        {
            if (model != null)
            {
                model.SetSearchText(value);
            }
        }

        // ── 媒体页 ────────────────────────────────────────────────────────

        private void BuildMediaPage()
        {
            mediaPage = CreateRect("Media", root, 0f, 0f, width, height);

            mediaSearch = CreateSearchField(mediaPage, "MediaSearch",
                MediaSearchInset, 0f, 100f, MediaHeaderHeight,
                "搜索文件名", OnMediaSearchChanged);

            mediaCount = CreateText("Count", mediaPage, 0f, 0f, MediaCountWidth,
                MediaHeaderHeight, "0", 12f, Accent, TextAlignmentOptions.MidlineRight);

            RectTransform refreshRect = CreateRect("Refresh", mediaPage,
                width - MediaSearchInset - MediaRefreshWidth, 0f,
                MediaRefreshWidth, MediaHeaderHeight);
            Image refreshHover = CreateImage("Hover", refreshRect,
                0f, 0f, 0f, 0f, Color.clear);
            refreshHover.raycastTarget = true;
            Stretch(refreshHover.rectTransform, 0f, 0f, 0f, 0f);
            TextMeshProUGUI refreshLabel = CreateText("Label", refreshRect,
                0f, 0f, MediaRefreshWidth, MediaHeaderHeight, "刷新",
                12f, TextSecondary, TextAlignmentOptions.Center);
            refreshLabel.raycastTarget = false;
            // 「刷新」是动作入口：链接蓝 + 下划线，和版本号同一套"能点"的视觉。
            refreshLabel.color = overlay != null
                ? overlay.VersionLinkColor
                : new Color(0.55f, 0.72f, 1f, 1f);
            refreshLabel.fontStyle = FontStyles.Underline;
            Button refresh = refreshRect.gameObject.AddComponent<Button>();
            refresh.targetGraphic = refreshHover;
            refresh.transition = Selectable.Transition.None;
            refresh.navigation = new Navigation { mode = Navigation.Mode.None };
            refresh.onClick.AddListener(delegate { RefreshMedia(); });
            AttachHover(refreshRect.gameObject, refreshHover, HoverFillColor);

            mediaScroll = CreateScroll("MediaScroll", mediaPage, 0f, MediaGridTop,
                width, Mathf.Max(32f, height - MediaGridTop), out mediaViewport, out mediaContent);

            mediaEmpty = CreateText("Empty", mediaViewport, 0f, 8f,
                Mathf.Max(40f, width), 24f, string.Empty,
                12f, TextMuted, TextAlignmentOptions.MidlineLeft);
            mediaEmpty.raycastTarget = false;
        }

        private sealed class MediaCard
        {
            internal RectTransform Rect;
            internal Image Background;
            internal RawImage Thumb;
            internal Image Well;
            internal TextMeshProUGUI Label;
            internal int BoundIndex = -1;
            internal string BoundPath;
        }

        private void EnsureMediaCards(int required)
        {
            while (mediaCards.Count < required)
            {
                mediaCards.Add(CreateMediaCard(mediaCards.Count));
            }
        }

        private MediaCard CreateMediaCard(int index)
        {
            MediaCard card = new MediaCard();
            RectTransform rect = CreateRect("MediaCard" + index, mediaContent, 0f, 0f, 80f, 100f);
            card.Rect = rect;

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;
            card.Background = background;

            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = false;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);

            // 凹槽：缩略图还没解出来时占位。一圈极淡白，不做实心底。
            Image well = CreateImage("Well", rect, 0f, 0f, 40f, 40f, Hairline);
            well.raycastTarget = false;
            card.Well = well;

            RawImage thumb = CreateRawImage("Thumb", rect, 0f, 0f, 40f, 40f);
            thumb.raycastTarget = false;
            card.Thumb = thumb;

            TextMeshProUGUI label = CreateText("Label", rect, 0f, 0f, 80f,
                EditorStudioOverlay.MediaCardLabelHeight, string.Empty,
                MediaLabelFontSize, TextSecondary, TextAlignmentOptions.Center);
            label.raycastTarget = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            card.Label = label;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            MediaCard captured = card;
            button.onClick.AddListener(delegate { ActivateMedia(captured); });
            AttachHover(rect.gameObject, hover, HoverFillColor);
            return card;
        }

        private void SyncMedia()
        {
            if (overlay == null || mediaContent == null || mediaViewport == null)
            {
                return;
            }

            List<EditorProjectMediaItem> items = overlay.VisibleMedia;
            int count = items == null ? 0 : items.Count;
            float viewportWidth = Mathf.Max(1f, mediaViewport.rect.width);
            int columns = EditorStudioOverlay.MediaColumnsFor(viewportWidth);
            float cardWidth = (viewportWidth
                - EditorStudioOverlay.MediaGridPadding * 2f
                - EditorStudioOverlay.MediaGridColumnGap * (columns - 1)) / Mathf.Max(1, columns);
            float cardHeight = EditorStudioOverlay.MediaCardHeight
                - EditorStudioOverlay.MediaGridRowGap;
            float labelTop = EditorStudioOverlay.MediaCardHeight
                - EditorStudioOverlay.MediaGridRowGap
                - EditorStudioOverlay.MediaCardLabelHeight;
            float thumbSize = Mathf.Max(16f, Mathf.Min(
                cardWidth - EditorStudioOverlay.MediaCardInset * 2f,
                labelTop - EditorStudioOverlay.MediaCardInset));

            int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)columns));
            float contentHeight = Mathf.Max(mediaViewport.rect.height,
                EditorStudioOverlay.MediaGridPadding * 2f
                + rows * EditorStudioOverlay.MediaCardHeight);
            if (!Mathf.Approximately(mediaContent.sizeDelta.y, contentHeight))
            {
                mediaContent.sizeDelta = new Vector2(0f, contentHeight);
            }

            UpdateMediaHeader(count);

            if (mediaEmpty != null)
            {
                bool show = count == 0;
                if (mediaEmpty.gameObject.activeSelf != show)
                {
                    mediaEmpty.gameObject.SetActive(show);
                }
                if (show)
                {
                    string message = string.IsNullOrEmpty(overlay.MediaSearchQuery)
                        ? "谱面目录里没有找到图片"
                        : "没有匹配的图片";
                    if (mediaEmpty.text != message)
                    {
                        mediaEmpty.text = message;
                    }
                }
            }

            float scrollY = Mathf.Max(0f, mediaContent.anchoredPosition.y);
            int firstRow = Mathf.Clamp(
                Mathf.FloorToInt(scrollY / EditorStudioOverlay.MediaCardHeight),
                0, Mathf.Max(0, rows - 1));
            int visibleRows = Mathf.CeilToInt(
                mediaViewport.rect.height / EditorStudioOverlay.MediaCardHeight) + 2;
            int required = Mathf.Min(count, Mathf.Max(columns, (visibleRows + 1) * columns));
            EnsureMediaCards(required);

            int firstIndex = firstRow * columns;
            for (int i = 0; i < mediaCards.Count; i++)
            {
                MediaCard card = mediaCards[i];
                int index = firstIndex + i;
                bool use = index >= 0 && index < count && i < required;
                if (!use)
                {
                    if (card.BoundIndex != -1)
                    {
                        card.BoundIndex = -1;
                        card.Rect.gameObject.SetActive(false);
                    }
                    continue;
                }
                if (!card.Rect.gameObject.activeSelf)
                {
                    card.Rect.gameObject.SetActive(true);
                }
                BindMediaCard(card, items, index, columns, cardWidth, cardHeight,
                    labelTop, thumbSize);
            }
        }

        private void UpdateMediaHeader(int count)
        {
            string countText = string.IsNullOrEmpty(overlay.MediaSearchQuery)
                ? count.ToString()
                : count + " / " + overlay.MediaTotalCount;

            if (mediaCount == null)
            {
                return;
            }
            if (mediaCount.text != countText)
            {
                mediaCount.text = countText;
            }

            float textWidth = mediaCount.GetPreferredValues(countText, 1000f,
                MediaHeaderHeight).x;
            float slot = Mathf.Max(MediaCountWidth, textWidth);
            float refreshX = width - MediaSearchInset - MediaRefreshWidth;
            SetTopLeft(mediaCount.rectTransform, refreshX - MediaRowGap - slot, 0f,
                slot, MediaHeaderHeight);

            if (mediaSearch != null)
            {
                float searchWidth = Mathf.Max(40f,
                    refreshX - MediaRowGap - slot
                    - MediaSearchInset - MediaRowGap);
                SetTopLeft(mediaSearch.GetComponent<RectTransform>(),
                    MediaSearchInset, 0f, searchWidth, MediaHeaderHeight);
            }
        }

        /// <summary>只重排、不读数据 —— Relayout 时用。</summary>
        private void UpdateMediaHeaderLayout()
        {
            if (mediaCount == null)
            {
                return;
            }
            float textWidth = mediaCount.GetPreferredValues(mediaCount.text, 1000f,
                MediaHeaderHeight).x;
            float slot = Mathf.Max(MediaCountWidth, textWidth);
            float refreshX = width - MediaSearchInset - MediaRefreshWidth;
            SetTopLeft(mediaCount.rectTransform, refreshX - MediaRowGap - slot, 0f,
                slot, MediaHeaderHeight);
            SetTopLeft(mediaSearch.GetComponent<RectTransform>(), MediaSearchInset, 0f,
                Mathf.Max(40f, refreshX - MediaRowGap - slot - MediaSearchInset - MediaRowGap),
                MediaHeaderHeight);
        }

        private void BindMediaCard(MediaCard card, List<EditorProjectMediaItem> items, int index,
            int columns, float cardWidth, float cardHeight, float labelTop, float thumbSize)
        {
            EditorProjectMediaItem item = items[index];
            int row = index / columns;
            int column = index % columns;
            SetTopLeft(card.Rect,
                EditorStudioOverlay.MediaGridPadding
                    + column * (cardWidth + EditorStudioOverlay.MediaGridColumnGap),
                EditorStudioOverlay.MediaGridPadding
                    + row * EditorStudioOverlay.MediaCardHeight,
                cardWidth, cardHeight);

            bool selected = string.Equals(overlay.SelectedMediaPath, item.RelativePath,
                StringComparison.OrdinalIgnoreCase);
            card.Background.color = selected ? SelectedFill : Color.clear;
            card.Label.color = selected ? TextPrimary : TextSecondary;

            if (card.BoundIndex != index || !string.Equals(card.BoundPath, item.RelativePath,
                StringComparison.Ordinal))
            {
                card.BoundIndex = index;
                card.BoundPath = item.RelativePath;
                if (card.Label.text != item.FileName)
                {
                    card.Label.text = item.FileName;
                }
            }

            float boxX = (cardWidth - thumbSize) * 0.5f;
            float boxY = EditorStudioOverlay.MediaCardInset;
            SetTopLeft(card.Well.rectTransform, boxX, boxY, thumbSize, thumbSize);
            SetTopLeft(card.Thumb.rectTransform, boxX, boxY, thumbSize, thumbSize);
            SetTopLeft(card.Label.rectTransform, EditorStudioOverlay.MediaCardInset,
                labelTop,
                Mathf.Max(16f, cardWidth - EditorStudioOverlay.MediaCardInset * 2f),
                EditorStudioOverlay.MediaCardLabelHeight);

            Texture2D texture;
            if (overlay.TryGetMediaThumbnail(item, out texture) && texture != null)
            {
                if (card.Thumb.texture != texture)
                {
                    card.Thumb.texture = texture;
                }
                card.Thumb.enabled = true;
                card.Well.enabled = false;
            }
            else
            {
                card.Thumb.enabled = false;
                card.Well.enabled = true;
            }
        }

        private void ActivateMedia(MediaCard card)
        {
            if (overlay != null && card != null && card.BoundIndex >= 0)
            {
                overlay.ActivateMediaIndex(card.BoundIndex);
            }
        }

        private void RefreshMedia()
        {
            if (overlay != null)
            {
                overlay.RefreshMedia();
            }
        }

        private void OnMediaSearchChanged(string value)
        {
            if (overlay != null)
            {
                overlay.SetMediaSearch(value);
            }
        }

        // ── 每帧 ──────────────────────────────────────────────────────────

        private void Update()
        {
            EnsureTabs();
            LayoutTabs();

            if (decoPage != null && decoPage.gameObject.activeSelf)
            {
                SyncDecorations();
            }
            if (mediaPage != null && mediaPage.gameObject.activeSelf)
            {
                SyncMedia();
                TickMediaDrag();
            }
        }

        /// <summary>
        /// 拖动媒体卡去预览区 = 建一个装饰物。
        ///
        /// **落点判定和创建都不在这里** —— `EditorSceneInteraction` 自己的 Tick 里有，
        /// 它直接读 `Input.mousePosition`。这里只负责在按下并移开超过阈值的那一刻
        /// 告诉它「开始拖了」，调的是和 ImGui 版同一个入口（ArmMediaDrop）。
        /// </summary>
        private void TickMediaDrag()
        {
            if (Input.GetMouseButtonDown(0))
            {
                mediaDragCandidate = FindMediaCardAt(Input.mousePosition);
                mediaDragPressScreen = Input.mousePosition;
                mediaDragArmed = false;
                return;
            }

            if (Input.GetMouseButton(0) && mediaDragCandidate != null && !mediaDragArmed
                && !EditorSceneInteraction.IsMediaDropActive)
            {
                if (Vector2.Distance(mediaDragPressScreen, Input.mousePosition) < MediaDragThreshold)
                {
                    return;
                }

                // 只读模式下 ArmMediaDrop 会拒绝 —— 那时不该把手势吞掉，
                // 列表照常滚动。
                if (!EditorSceneInteraction.ArmMediaDrop(
                        mediaDragCandidate.FileName, mediaDragCandidate.AbsolutePath))
                {
                    mediaDragCandidate = null;
                    return;
                }
                mediaDragArmed = true;
                if (mediaScroll != null)
                {
                    mediaScroll.enabled = false;
                }
                return;
            }

            if (Input.GetMouseButtonUp(0))
            {
                mediaDragCandidate = null;
                mediaDragArmed = false;
                if (mediaScroll != null && !mediaScroll.enabled)
                {
                    mediaScroll.enabled = true;
                }
            }
        }

        private EditorProjectMediaItem FindMediaCardAt(Vector2 screenPoint)
        {
            List<EditorProjectMediaItem> items = overlay == null ? null : overlay.VisibleMedia;
            if (items == null)
            {
                return null;
            }
            for (int i = 0; i < mediaCards.Count; i++)
            {
                MediaCard card = mediaCards[i];
                if (card == null || card.Rect == null || !card.Rect.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (card.BoundIndex < 0 || card.BoundIndex >= items.Count)
                {
                    continue;
                }
                if (RectTransformUtility.RectangleContainsScreenPoint(
                        card.Rect, screenPoint, null))
                {
                    return items[card.BoundIndex];
                }
            }
            return null;
        }

        // ── UI 辅助 ───────────────────────────────────────────────────────

        private static string FirstCharacter(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Substring(0, 1);
        }

        private static Sprite TypeIconOf(ADOFAI.LevelEventType type)
        {
            if (GCS.levelEventIcons == null)
            {
                return null;
            }
            Sprite sprite;
            return GCS.levelEventIcons.TryGetValue(type, out sprite) ? sprite : null;
        }

        private static void AttachHover(GameObject target, Image fill, Color hoverColor)
        {
            HoverFill hover = target.AddComponent<HoverFill>();
            hover.Configure(fill, hoverColor);
        }

        /// <summary>
        /// 悬停时才浮出的一层极淡白。**不做常驻底色** —— 整屏只有一种底（背景黑），
        /// 控件靠 hover 的这点亮证明自己能点。
        /// </summary>
        private sealed class HoverFill : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private Image fill;
            private Color hoverColor = new Color(1f, 1f, 1f, 0.08f);

            internal void Configure(Image image, Color color)
            {
                fill = image;
                hoverColor = color;
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (fill != null)
                {
                    fill.color = hoverColor;
                }
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (fill != null)
                {
                    fill.color = Color.clear;
                }
            }
        }

        private static RectTransform CreateRect(string name, Transform parent,
            float x, float y, float w, float h)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            SetTopLeft(rect, x, y, w, h);
            return rect;
        }

        private static void SetTopLeft(RectTransform rect, float x, float y, float w, float h)
        {
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
        }

        private static void Stretch(RectTransform rect, float left, float bottom,
            float right, float top)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static Image CreateImage(string name, Transform parent,
            float x, float y, float w, float h, Color color)
        {
            RectTransform rect = CreateRect(name, parent, x, y, w, h);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static RawImage CreateRawImage(string name, Transform parent,
            float x, float y, float w, float h)
        {
            RectTransform rect = CreateRect(name, parent, x, y, w, h);
            RawImage image = rect.gameObject.AddComponent<RawImage>();
            image.color = Color.white;
            return image;
        }

        private TextMeshProUGUI CreateText(string name, Transform parent,
            float x, float y, float w, float h, string value, float size,
            Color color, TextAlignmentOptions alignment)
        {
            RectTransform rect = CreateRect(name, parent, x, y, w, h);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// 搜索框：**不要外壳，只要一条下划线**。
        /// 底色透明、无描边，文字和占位符照旧 —— 和整屏「控件默认无底」一致。
        /// </summary>
        private TMP_InputField CreateSearchField(Transform parent, string name,
            float x, float y, float w, float h, string placeholder,
            UnityEngine.Events.UnityAction<string> onChanged)
        {
            RectTransform rect = CreateRect(name, parent, x, y, w, h);
            // 先在非激活状态下接线：对活着的对象 AddComponent 会立刻跑 OnEnable，
            // 那时 textComponent / textViewport 还没赋值，光标根本不会建。
            rect.gameObject.SetActive(false);

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;

            // 底线：**2px、白 28%**（和参数栏的输入框同一档）。
            Image underline = CreateImage("Underline", rect, 0f, 0f, 0f, 2f,
                new Color(1f, 1f, 1f, 0.28f));
            underline.raycastTarget = false;
            underline.rectTransform.anchorMin = new Vector2(0f, 0f);
            underline.rectTransform.anchorMax = new Vector2(1f, 0f);
            underline.rectTransform.pivot = new Vector2(0.5f, 0f);
            underline.rectTransform.offsetMin = new Vector2(0f, 0f);
            underline.rectTransform.offsetMax = new Vector2(0f, 2f);

            TextMeshProUGUI text = CreateStretchedText("Text", rect, 7f,
                string.Empty, 12f, TextPrimary);
            text.overflowMode = TextOverflowModes.Ellipsis;
            TextMeshProUGUI hint = CreateStretchedText("Placeholder", rect, 7f,
                placeholder, 12f, TextMuted);
            hint.overflowMode = TextOverflowModes.Ellipsis;

            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.richText = false;
            field.characterLimit = 128;
            field.customCaretColor = true;
            field.caretColor = TextPrimary;
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.textViewport = rect;
            field.textComponent = text;
            field.placeholder = hint;
            if (onChanged != null)
            {
                field.onValueChanged.AddListener(onChanged);
            }

            rect.gameObject.SetActive(true);
            return field;
        }

        private TextMeshProUGUI CreateStretchedText(string name, RectTransform parent,
            float inset, string value, float size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, 0f);
            rect.offsetMax = new Vector2(-inset, 0f);

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                text.font = font;
            }
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        private static ScrollRect CreateScroll(string name, Transform parent,
            float x, float y, float w, float h,
            out RectTransform viewport, out RectTransform content)
        {
            RectTransform host = CreateRect(name, parent, x, y, w, h);
            Image background = host.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;

            ScrollRect scroll = host.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            scroll.scrollSensitivity = 28f;

            GameObject viewportObject = new GameObject(name + ".Viewport",
                typeof(RectTransform), typeof(RectMask2D));
            viewportObject.transform.SetParent(host, false);
            viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;

            GameObject contentObject = new GameObject(name + ".Content",
                typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);
            content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, h);

            scroll.viewport = viewport;
            scroll.content = content;
            return scroll;
        }
    }
}
