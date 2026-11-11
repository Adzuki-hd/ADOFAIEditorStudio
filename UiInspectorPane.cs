using System;
using System.Collections.Generic;
using System.Globalization;
using ADOFAI;
using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 右栏参数栏（以及左栏的设置页正文）的 uGUI 版 —— **按 ImGui 现行实现重写**。
    ///
    /// 写入一律走 `EditorPropertyPanel`（那份面板只初始化数据、不建 UI）：
    /// 只读拦截、批量合成一条撤销、装饰物活对象刷新、设置页副作用都在 `Commit`
    /// 里，另写一条写路径迟早两边不一样。
    ///
    /// 本文件只负责「怎么摆、画成什么样」：行池、宽/窄两种排法、14 种控件。
    /// </summary>
    internal sealed class UiInspectorPane : MonoBehaviour
    {
        // ── 布局刻度（照 ImGui 那版） ──────────────────────────────────
        private const float CardPad = 12f;
        private const float CardGap = 8f;
        private const float LabelGap = 8f;
        private const float LabelLeftGap = 8f;
        private const float EnableWidth = 22f;
        private const float MinimumValueWidth = 118f;
        private const float MixedChipWidth = 56f;
        private const float ControlHeight = 26f;
        private const float WideRowHeight = 44f;
        private const float StackedRowHeight = 66f;
        private const float PreviewHeight = 116f;
        private const float UnitGap = 8f;
        private const float PairGap = 6f;

        private const float PollInterval = 0.12f;

        private static Color TextPrimary { get { return UiTheme.Current.TextPrimary; } }
        private static Color TextSecondary { get { return UiTheme.Current.TextSecondary; } }
        private static Color TextMuted { get { return UiTheme.Current.TextMuted; } }
        private static Color Accent { get { return UiTheme.Current.AccentSecondary; } }
        private static Color Hairline { get { return UiTheme.Current.BorderHairline; } }
        private static Color HoverColor { get { return new Color(1f, 1f, 1f, 0.08f); } }

        private EditorStudioOverlay overlay;
        private EditorPropertyPanel panel;
        private TMP_FontAsset font;
        private bool settingsMode;

        private RectTransform root;
        private RectTransform viewport;
        private RectTransform content;
        private ScrollRect scroll;
        private RectTransform headerHost;
        /// <summary>顶部开关 + 预览图的容器。每次 Rebuild 整层重建（见 BuildHeader）。</summary>
        private RectTransform headerLayer;
        private TextMeshProUGUI emptyLabel;
        private float width;
        private float height;

        /// <summary>
        /// 内容区过渡：切换事件时淡入 + 轻微放大。
        ///
        /// **挂在 `viewport` 上而不是 `content` 上。** `content` 的位置归
        /// `ScrollRect` 管，过渡组件会写 `anchoredPosition`，两者打架 ——
        /// 滚动条会被动画拽回去。`viewport` 是拉伸节点（锚点铺满父级、
        /// 偏移全 0），pivot 在 (0.5,0.5)，缩放补偿量算出来正好是 0，
        /// 于是只剩透明度 + 中心缩放，完全不碰位置。
        /// </summary>
        private UiTransition contentTransition;

        /// <summary>下一次 `Rebuild` 要不要播过渡。只有"换了选中事件"才置真。</summary>
        private bool animateNextRebuild;

        /// <summary>设置页正文顶上的页名（右栏由面板标题负责，这里是空的）。</summary>
        private TextMeshProUGUI pageTitle;

        private readonly List<Row> rows = new List<Row>();
        private readonly List<KeyValuePair<string, ADOFAI.PropertyInfo>> visible
            = new List<KeyValuePair<string, ADOFAI.PropertyInfo>>();
        private LevelEvent target;
        private float nextPoll;
        private float lastBuiltWidth = -1f;
        private int lastBatchCount = -1;
        private string lastPage;

        /// <summary>
        /// 固定显示哪一页设置。`None` = 跟着 overlay 的当前选中走（老行为）。
        ///
        /// **传送带式左栏用这个**：6 个设置页各建一个实例、各自钉死一页，
        /// 整体横向平移。不钉死的话，6 个实例都会去读
        /// `overlay.SelectedSettingsEvent`、全都显示同一页。
        /// </summary>
        private LevelEventType pinnedSettingsPage = LevelEventType.None;

        // 弹层（枚举 / 调色盘）共用一块 32000 画布
        private RectTransform popupLayer;
        private RectTransform popupHost;
        private RectTransform popupBlocker;
        private EditorColorPicker colorPicker;

        /// <summary>
        /// 当前打开着的弹层是由哪个下拉按钮触发的。
        ///
        /// 收起时要把它的箭头转回朝下。**关闭路径不止一条**（点外面、
        /// 选中一项、重复打开另一个下拉），全部都会走到 `ClosePopup`，
        /// 所以收口放在那里，不用在每个调用点各写一遍。
        /// </summary>
        private UiArrowSpin openArrow;

        private sealed class Row
        {
            internal RectTransform Rect;
            internal Image Background;
            internal RectTransform EnableRect;
            internal Button EnableButton;
            internal TextMeshProUGUI EnableCheck;
            internal TextMeshProUGUI Label;
            internal TextMeshProUGUI Mixed;
            internal RectTransform ValueHost;
            /// <summary>值那一块的交互闸：属性被禁用时整块不接点击、也压暗。</summary>
            internal CanvasGroup ValueGroup;
            internal string Key;
            internal ADOFAI.PropertyInfo Info;
            internal float Height;

            // 刷新时回填用
            internal TMP_InputField Field1;
            internal TMP_InputField Field2;
            internal TextMeshProUGUI EnumLabel;
            /// <summary>
            /// `EnumLabel` 是不是一个**真正由枚举/滤镜驱动的**标签。
            ///
            /// `RefreshValues` 末尾有一段无条件回填 `EnumLabel` 的逻辑，走的是
            /// `ReadEnumName` / `DisplayEnumName`。Tile 那一行也借 `EnumLabel`
            /// 存它的模式标签（「本格/起点/终点」），但 Tile 不是枚举 ——
            /// 让它进那段逻辑会被写成错误的值。这个标记就是为了把两类分开。
            /// </summary>
            internal bool EnumLabelIsEnum;
            internal Image Swatch;
            /// <summary>上一帧画出来的评分数（`PropertyType.Rating`）。值变了才重建。</summary>
            internal int RatingValue = -1;
        }

        // ── 对外 ─────────────────────────────────────────────────────────

        /// <summary>设置页模式：只看一页设置，不跟时间轴选中、不画预览与生效开关。</summary>
        /// <summary>
        /// 把这一实例钉死在某一页设置上。传送带式左栏用。
        ///
        /// 钉死之后内容不再跟着 overlay 的选中走 —— 6 个实例各显示各的，
        /// 才能并排铺开。
        /// </summary>
        internal void PinSettingsPage(LevelEventType page)
        {
            pinnedSettingsPage = page;
            lastPage = null;
            target = null;
            Rebuild();
        }

        internal void SetSettingsMode(bool value)        {
            if (!value && panel != null)
            {
                // 离开设置页要**清掉那个标记**：留着的话，右栏以后打开装饰物
                // 事件会被当成设置页，装饰物预览图永远不显示。
                panel.MarkSettingsPage(null);
            }
            settingsMode = value;
            target = null;
            lastPage = null;
            Rebuild();
        }

        internal void SetPaneRect(float x, float y, float paneWidth, float paneHeight)
        {
            if (root == null)
            {
                return;
            }
            width = Mathf.Max(64f, paneWidth);
            height = Mathf.Max(64f, paneHeight);
            SetTopLeft(root, x, y, width, height);
            if (Mathf.Abs(lastBuiltWidth - width) > 0.5f)
            {
                Rebuild();
            }
            else
            {
                LayoutScroll();
            }
        }

        internal static UiInspectorPane Build(RectTransform parent, EditorStudioOverlay owner,
            EditorPropertyPanel writePanel, TMP_FontAsset uiFont,
            float x, float y, float paneWidth, float paneHeight, bool asSettingsPage)
        {
            RectTransform rect = CreateRect("InspectorPane", parent, x, y, paneWidth, paneHeight);
            UiInspectorPane pane = rect.gameObject.AddComponent<UiInspectorPane>();
            pane.overlay = owner;
            pane.panel = writePanel;
            pane.font = uiFont;
            pane.settingsMode = asSettingsPage;
            pane.width = Mathf.Max(64f, paneWidth);
            pane.height = Mathf.Max(64f, paneHeight);
            pane.root = rect;
            pane.BuildShell();
            pane.SetPaneRect(x, y, paneWidth, paneHeight);
            return pane;
        }

        private void OnDestroy()
        {
            ClosePopup();
        }

        // ── 外壳 ─────────────────────────────────────────────────────────

        private void BuildShell()
        {
            pageTitle = CreateText("PageTitle", root, CardPad, 0f,
                Mathf.Max(40f, width - CardPad * 2f), 24f, string.Empty,
                13f, Accent, TextAlignmentOptions.MidlineLeft);
            pageTitle.gameObject.SetActive(settingsMode);

            headerHost = CreateRect("Header", root, 0f, 0f, width, 0f);

            RectTransform scrollHost = CreateRect("Scroll", root, 0f, 0f, width, height);
            Image background = scrollHost.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;
            scroll = scrollHost.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            scroll.scrollSensitivity = 28f;

            GameObject viewportObject = new GameObject("Viewport",
                typeof(RectTransform), typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollHost, false);
            viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;

            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);
            content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, height);

            scroll.viewport = viewport;
            scroll.content = content;

            // 内容区过渡挂在 viewport 上 —— 理由见 `contentTransition` 的注释。
            contentTransition = UiTransition.Attach(viewportObject);

            emptyLabel = CreateText("Empty", viewport, CardPad, 8f,
                Mathf.Max(40f, width - CardPad * 2f), 24f, string.Empty,
                12f, TextMuted, TextAlignmentOptions.MidlineLeft);
            emptyLabel.gameObject.SetActive(false);
        }

        private void LayoutScroll()
        {
            float top = settingsMode ? 28f : 0f;
            if (pageTitle != null)
            {
                SetTopLeft(pageTitle.rectTransform, CardPad, 4f,
                    Mathf.Max(40f, width - CardPad * 2f), 22f);
            }
            if (scroll != null)
            {
                SetTopLeft(scroll.GetComponent<RectTransform>(), 0f, top,
                    width, Mathf.Max(40f, height - top));
            }
            if (emptyLabel != null)
            {
                SetTopLeft(emptyLabel.rectTransform, CardPad, 8f,
                    Mathf.Max(40f, width - CardPad * 2f), 24f);
            }
        }

        // ── 每帧 ─────────────────────────────────────────────────────────

        private void Update()
        {
            // 【临时探针】见 EditorPropertyPanel.Update 里的说明。
            long __watch = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateMeasured();
            PreviewDiagnostics.RecordSince("Insp.U", __watch);
        }

        private void UpdateMeasured()
        {
            if (overlay == null || panel == null || !overlay.IsVisible)
            {
                return;
            }

            Poll();

            if (settingsMode)
            {
                if (target != null && target.info != null && target.info.propertiesInfo != null)
                {
                    panel.MarkSettingsPage(target);
                }
            }

            RefreshValues();

            // 评分（星星串）没法原地刷新，值变了就整块重建一次。
            if (pendingRatingRebuild)
            {
                pendingRatingRebuild = false;
                Rebuild();
            }
        }

        /// <summary>这一帧发现评分数变了，需要重建整块（星星是按钮，改不了文本）。</summary>
        private bool pendingRatingRebuild;

        /// <summary>
        /// 选集以**时间轴那份**为准；批量时抓手取这一批的第一个。
        /// 设置页走 `SelectedSettingsEvent`，不经过这里。
        /// </summary>
        private void Poll()
        {
            float now = Time.unscaledTime;
            if (now < nextPoll)
            {
                return;
            }
            nextPoll = now + PollInterval;

            if (settingsMode)
            {
                string page;
                LevelEvent settingsTarget;
                if (pinnedSettingsPage != LevelEventType.None)
                {
                    // **钉死了一页**：内容不跟着 overlay 的选中走，
                    // 这样传送带里 6 个实例才能各显示各的。
                    page = overlay.SettingsTitleOf(pinnedSettingsPage);
                    settingsTarget = overlay.FindSettingsPageOf(pinnedSettingsPage);
                }
                else
                {
                    page = overlay.SelectedSettingsTitle;
                    settingsTarget = overlay.SelectedSettingsEvent;
                }
                if (!string.Equals(page, lastPage, StringComparison.Ordinal))
                {
                    lastPage = page;
                    if (pageTitle != null)
                    {
                        pageTitle.text = page;
                    }
                }
                if (!ReferenceEquals(settingsTarget, target))
                {
                    target = settingsTarget;
                    // 换了页才播过渡；同一页的刷新不播，否则每 0.12 秒闪一次。
                    animateNextRebuild = true;
                    Rebuild();
                }
                return;
            }

            List<LevelEvent> selection = overlay.Timeline == null
                ? null : overlay.Timeline.PanelSelectionWithDecorations();
            bool batch = selection != null && selection.Count > 1 && panel.EnterBatch(selection);
            if (!batch)
            {
                // 退到零/一个：必须退出批量，否则下一次单选会把值写给一批旧事件。
                panel.ExitBatch();
            }

            LevelEvent single = selection != null && selection.Count == 1 ? selection[0] : null;
            LevelEvent anchor = batch && panel.BatchEvents != null && panel.BatchEvents.Count > 0
                ? panel.BatchEvents[0]
                : single;

            int batchCount = panel.InBatch ? panel.BatchEvents.Count : 0;
            if (!ReferenceEquals(anchor, target) || batchCount != lastBatchCount)
            {
                // 只有"选中对象换了"才播过渡。批量人数变化（多加/少选一个）
                // 内容大同小异，跟着闪反而吵。
                animateNextRebuild = !ReferenceEquals(anchor, target);
                target = anchor;
                lastBatchCount = batchCount;
                Rebuild();
            }
        }

        /// <summary>收集这一屏要画的行 —— 过滤规则只有这一处。</summary>
        private void CollectVisible()
        {
            visible.Clear();
            if (target == null || target.info == null || target.info.propertiesInfo == null)
            {
                return;
            }
            // **属性表里没有 `filterProperties` 这一项时的兜底。**
            //
            // 实测有谱面的 `SetFilterAdvanced` 属性表里根本没有这个键 ——
            // 参数栏于是只显示「滤镜」下拉，下面的强度/扭曲/半径一个都不出来
            // （用户原话：「滤镜畸变梦境有个扭曲参数，我没看见」）。
            //
            // 而事件数据里 `filter_*` 是实打实存在的。所以这里不看属性表，
            // 直接按事件数据 + 滤镜类的字段表展开，补在最后。
            if (target.eventType == LevelEventType.SetFilterAdvanced)
            {
                AddFilterRowsIfMissing();
            }

            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in target.info.propertiesInfo)
            {
                ADOFAI.PropertyInfo info = pair.Value;
                if (info == null || string.IsNullOrEmpty(info.name))
                {
                    continue;
                }

                // **`filterProperties` 要在 invisible 过滤之前处理。**
                //
                // 实测（`[FilterProps]` 探针）高级滤镜事件里它的元数据是
                // `filterProperties: FilterProperties/invisible` —— **invisible 为真**。
                // 而下面第一道闸就是 `if (info.invisible) continue;`，
                // 所以展开代码要是写在它后面，一行都出不来。
                //
                // 原生那边这个属性是"容器"：它自己不出控件，只负责装那一串
                // `filter_*` 子控件的宿主。所以 invisible 对它本来就该是"不画自己、
                // 画子项"，而不是"整块跳过"。
                if (info.type == PropertyType.FilterProperties)
                {
                    List<KeyValuePair<string, ADOFAI.PropertyInfo>> filterRows =
                        EditorPropertyPanel.ExpandFilterProperties(target);
                    if (filterRows != null && filterRows.Count > 0)
                    {
                        for (int i = 0; i < filterRows.Count; i++)
                        {
                            if (panel.InBatch && !panel.BatchHasKey(filterRows[i].Key))
                            {
                                continue;
                            }
                            visible.Add(filterRows[i]);
                        }
                        continue;
                    }
                    // 展不开（没选滤镜、类名找不到）时继续往下走，
                    // 让原来的占位行露出来 —— 也好过整块消失。
                }

                if (info.invisible || info.controlType == ControlType.Hidden
                    || info.controlType == ControlType.ToggleGroup)
                {
                    continue;
                }
                if (panel.InBatch && !panel.BatchHasKey(pair.Key))
                {
                    continue;
                }

                visible.Add(pair);
            }
        }

        /// <summary>
        /// `PropertyType.Rating` —— 一排可点的星星。
        ///
        /// 原生 `PropertyControl_Rating` 的语义很简单：点第 N 颗就把值设成 N，
        /// 高亮 1..N 颗（`SetInt(Array.IndexOf(buttons, button) + 1)`）。
        /// 值本身是个整数（评几星）。
        ///
        /// 这里不画星形贴图，用字符 ★ / ☆ —— 中文字体里就有，
        /// 不必为五颗星额外引一张图。
        /// </summary>
        private void BuildRating(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            const int maxStars = 10;
            const float starWidth = 22f;

            int current = Mathf.RoundToInt(
                EditorPropertyPanel.SafeGet(target, info, 0f));
            // 记下初始值：`RefreshValues` 拿它比对，值没变就不重建。
            row.RatingValue = current;

            RectTransform strip = CreateRect("Rating", host, 0f, 0f,
                Mathf.Min(valueWidth, maxStars * starWidth), ControlHeight);
            row.Field1 = null;

            for (int i = 0; i < maxStars; i++)
            {
                int starIndex = i + 1;
                bool filled = starIndex <= current;

                RectTransform cell = CreateRect("Star" + starIndex, strip,
                    i * starWidth, 0f, starWidth, ControlHeight);
                Image hit = cell.gameObject.AddComponent<Image>();
                hit.color = Color.clear;
                hit.raycastTarget = true;

                TextMeshProUGUI glyph = CreateText("Glyph", cell,
                    0f, 0f, starWidth, ControlHeight,
                    filled ? "★" : "☆", 15f,
                    filled ? Accent : TextMuted, TextAlignmentOptions.Center);
                glyph.raycastTarget = false;

                Button button = cell.gameObject.AddComponent<Button>();
                button.targetGraphic = hit;
                button.transition = Selectable.Transition.None;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                // 再点同一颗就取消（回到 0 星）—— 否则 0 星没法表达。
                int captured = starIndex;
                button.onClick.AddListener(delegate
                {
                    int next = captured == current ? 0 : captured;
                    CommitValue(target, key, next);
                    Rebuild();
                });
            }
        }

        /// <summary>
        /// `PropertyType.Export` —— 一个按钮，点开游戏的导出窗口。
        ///
        /// 那是游戏自己的谱面导出流程（`scnEditor.ShowExportWindow`），
        /// 和 AES 无关，所以只做个入口，不自己实现。
        /// </summary>
        private void BuildExportButton(RectTransform host, float valueWidth)
        {
            RectTransform rect = CreateRect("Export", host, 0f, 0f, valueWidth, ControlHeight);
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = HoverColor;
            background.raycastTarget = true;

            TextMeshProUGUI label = CreateText("Label", rect, 0f, 0f, valueWidth, ControlHeight,
                "导出…", 12f, TextPrimary, TextAlignmentOptions.Center);
            label.raycastTarget = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                {
                    return;
                }
                try
                {
                    editor.ShowExportWindow(0);
                }
                catch (Exception exception)
                {
                    Main.Logger.Error("[参数栏] 打开导出窗口失败：" + exception);
                }
            });
        }

        // ── 重建 ─────────────────────────────────────────────────────────

        /// <summary>
        /// `PropertyType.Note` —— 一行说明文字。
        ///
        /// 原生把它当纯文本控件（`PropertyControl_Note`），内容取
        /// `propertyInfo.noteKey` 那条本地化。高级滤镜页顶上那条
        /// 「警告！…」就是它 —— 它在提醒作者某些滤镜会把画面糊掉之类。
        ///
        /// 文字取不到时退回键名本身，至少能看出"这里本来有一条提示"。
        /// </summary>
        private void BuildNote(RectTransform host, float availableWidth, ADOFAI.PropertyInfo info)
        {
            string key = info == null ? null : info.noteKey;
            string text = null;
            if (!string.IsNullOrEmpty(key))
            {
                try
                {
                    text = RDString.Get(key);
                    if (string.Equals(text, key, StringComparison.Ordinal))
                    {
                        // 查不到时 RDString.Get 会把键原样返回，那种情况当作没有。
                        text = null;
                    }
                }
                catch
                {
                    text = null;
                }
            }
            if (string.IsNullOrEmpty(text))
            {
                text = key ?? string.Empty;
            }

            TextMeshProUGUI label = CreateText("Note", host, 0f, 0f,
                Mathf.Max(40f, availableWidth), ControlHeight, text,
                12f, TextMuted, TextAlignmentOptions.TopLeft);
            label.raycastTarget = false;
            // 提示一般比一行长，允许折行 —— 截断等于把说明藏掉一半。
            label.overflowMode = TextOverflowModes.Overflow;
            label.textWrappingMode = TextWrappingModes.Normal;
        }
        /// <summary>
        /// 属性表里没有 `filterProperties` 时，按事件数据自己补一组子参数行。
        ///
        /// 为什么要这一条：`SetFilterAdvanced` 的属性表**不是每张谱都一样** ——
        /// 有的是新格式（带 `filterProperties` 那一项），有的是旧格式（没有）。
        /// 旧格式下参数栏只显示「滤镜」一个下拉，强度/扭曲/半径全不见，
        /// 而那些 `filter_*` 值明明就存在事件数据里。
        ///
        /// 这里只做属性表缺失时的补充，不重复展开 —— 有 `filterProperties`
        /// 那一项时走 `CollectVisible` 里的正常分支。
        /// </summary>
        private void AddFilterRowsIfMissing()
        {
            if (target == null || target.info == null || target.info.propertiesInfo == null)
            {
                return;
            }

            // 属性表里有这一项就交给正常路径，别重复加。
            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in target.info.propertiesInfo)
            {
                if (pair.Value != null && pair.Value.type == PropertyType.FilterProperties)
                {
                    return;
                }
            }

            List<KeyValuePair<string, ADOFAI.PropertyInfo>> expanded =
                EditorPropertyPanel.ExpandFilterProperties(target);
            if (expanded == null || expanded.Count == 0)
            {
                return;
            }

            for (int i = 0; i < expanded.Count; i++)
            {
                // 批量模式下只保留每个事件都有的键（和正常路径同一条规矩）。
                if (panel.InBatch && !panel.BatchHasKey(expanded[i].Key))
                {
                    continue;
                }
                visible.Add(expanded[i]);
            }
        }
        private void Rebuild()
        {
            if (root == null)
            {
                return;
            }
            lastBuiltWidth = width;
            LayoutScroll();
            CollectVisible();

            bool hasTarget = target != null && target.info != null
                && target.info.propertiesInfo != null;
            if (emptyLabel != null)
            {
                emptyLabel.gameObject.SetActive(!hasTarget);
                if (!hasTarget)
                {
                    emptyLabel.text = settingsMode ? "这一页暂时读不到" : "没有选中事件";
                }
            }

            HideAllRows();
            ClosePopup();
            ClearHeader();
            if (!hasTarget)
            {
                content.sizeDelta = new Vector2(0f, Mathf.Max(height, 40f));
                PlayContentTransition();
                return;
            }

            float top = BuildHeader();
            float available = Mathf.Max(80f, width - CardPad * 2f);
            float labelNeed = MeasureLongestLabel();
            float labelColumn = Mathf.Min(labelNeed + LabelGap, available * 0.5f);
            float valueNeed = MeasureValueNeed();
            bool stacked = labelNeed + LabelGap > available * 0.5f
                || available - EnableWidth - LabelLeftGap - labelColumn < valueNeed;

            float y = top;
            int index = 0;
            for (int i = 0; i < visible.Count; i++)
            {
                Row row = ObtainRow(index++);
                ADOFAI.PropertyInfo info = visible[i].Value;
                // Vector2Range 的值本身是「X 一行、Y 一行」两条，得单独留高度。
                bool twoLine = info.type == PropertyType.Vector2Range;
                float rowHeight = twoLine ? 86f : (stacked ? StackedRowHeight : WideRowHeight);
                BindRow(row, visible[i].Key, info, y, rowHeight, stacked || twoLine,
                    labelColumn, available);
                y += rowHeight + CardGap;
            }
            content.sizeDelta = new Vector2(0f, Mathf.Max(height, y + CardPad));
            PlayContentTransition();
        }

        /// <summary>
        /// 切换事件时播一次淡入。**只在 `animateNextRebuild` 置真时播** ——
        /// 宽度变化、评分数变化、批量人数变化都会重建，那些跟着闪会吵。
        ///
        /// 时长用 `UiMotion.Fast`（0.12s）：参数栏是高频操作的面板，
        /// 拖选取框扫过一串事件时每换一个都要播，慢了会拖手感。
        /// </summary>
        private void PlayContentTransition()
        {
            if (!animateNextRebuild)
            {
                return;
            }
            animateNextRebuild = false;
            if (contentTransition != null)
            {
                contentTransition.PlayShow(UiMotion.Fast);
            }
        }

        /// <summary>
        /// 拆掉顶部那一层（开关 + 预览）。
        ///
        /// **必须由 Rebuild 在分岔之前调用**：取消选中时会走「没有目标」那条
        /// 早退分支，如果清理写在 BuildHeader 里，那条路根本到不了 ——
        /// 表现就是「点空白处之后，上一份生效开关还留在那儿，压在空态文字上」。
        /// </summary>
        private void ClearHeader()
        {
            if (headerLayer == null)
            {
                return;
            }
            headerLayer.gameObject.SetActive(false);
            Destroy(headerLayer.gameObject);
            headerLayer = null;
        }

        /// <summary>顶部：生效/不生效 + 可见 + 装饰物预览。返回它占掉的高度。</summary>
        private float BuildHeader()
        {
            // 没选中事件时 ImGui 那边连开关都不画，这里照做。
            if (target == null)
            {
                return 0f;
            }

            headerLayer = CreateRect("HeaderLayer", content, 0f, 0f,
                Mathf.Max(40f, width - CardPad * 2f), 10f);

            float y = 0f;
            // 设置页没有"事件生效"这回事，也不画装饰物预览。
            if (!settingsMode)
            {
                y = BuildActiveToggle(y);
                y = BuildDecorationPreview(y);
            }
            return y;
        }

        private float BuildActiveToggle(float y)
        {
            RectTransform host = CreateRect("Active", headerLayer, CardPad, y,
                Mathf.Max(80f, width - CardPad * 2f), 28f);

            // 位置**按宽度一个个排**，不再写死坐标：
            // 原来标签固定 80 宽、开关钉在 86、「可见」钉在 118 —— 三者互相压，
            // 表现就是「两个开关按钮重叠」「可见两个字被盖住」。
            const float labelWidth = 62f;
            const float trackWidth = 46f;
            const float trackHeight = 22f;
            const float afterLabel = 4f;
            const float betweenGroups = 20f;

            TextMeshProUGUI label = CreateText("Label", host, 0f, 0f, labelWidth, 28f,
                target != null && target.active ? "生效" : "不生效", 12f,
                TextSecondary, TextAlignmentOptions.MidlineLeft);
            label.raycastTarget = false;

            float trackX = labelWidth + afterLabel;
            RectTransform track = CreateRect("Track", host, trackX, 3f,
                trackWidth, trackHeight);
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = target != null && target.active
                ? new Color(1f, 1f, 1f, 0.18f) : Hairline;
            trackImage.raycastTarget = true;
            Image knob = CreateImage("Knob", track,
                target != null && target.active ? 24f : 3f, 3f, 16f, 16f, Accent);
            knob.raycastTarget = false;

            Button button = track.gameObject.AddComponent<Button>();
            button.targetGraphic = trackImage;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (target != null)
                {
                    panel.SetEventActive(target, !target.active);
                    Rebuild();
                }
            });

            // 装饰物才有"可见"，值就是谱面里的 visible。
            if (target != null && target.info != null && target.info.propertiesInfo != null
                && target.info.propertiesInfo.ContainsKey("visible"))
            {
                bool visibleValue = EditorPropertyPanel.SafeGet(target, "visible", true);

                float textX = trackX + trackWidth + betweenGroups;
                TextMeshProUGUI visibleLabel = CreateText("VisibleLabel", host,
                    textX, 0f, 34f, 28f, "可见", 12f, TextMuted,
                    TextAlignmentOptions.MidlineLeft);
                visibleLabel.raycastTarget = false;

                float visibleTrackX = textX + 34f + 2f;
                RectTransform visibleHost = CreateRect("Visible", host,
                    visibleTrackX, 3f, trackWidth, trackHeight);
                Image visibleTrack = visibleHost.gameObject.AddComponent<Image>();
                visibleTrack.color = visibleValue
                    ? new Color(1f, 1f, 1f, 0.18f) : Hairline;
                visibleTrack.raycastTarget = true;
                Image visibleKnob = CreateImage("Knob", visibleHost,
                    visibleValue ? 24f : 3f, 3f, 16f, 16f, Accent);
                visibleKnob.raycastTarget = false;
                Button visibleButton = visibleHost.gameObject.AddComponent<Button>();
                visibleButton.targetGraphic = visibleTrack;
                visibleButton.transition = Selectable.Transition.None;
                visibleButton.onClick.AddListener(delegate
                {
                    if (target != null)
                    {
                        CommitValue(target, "visible", !visibleValue);
                        Rebuild();
                    }
                });
            }

            return y + 28f + CardPad;
        }

        private float BuildDecorationPreview(float y)
        {
            Sprite sprite;
            string filename;
            if (!panel.TryGetDecorationPreview(target, out sprite, out filename))
            {
                return y;
            }

            float slotWidth = Mathf.Max(40f, width - CardPad * 2f);
            RectTransform host = CreateRect("Preview", headerLayer, CardPad, y, slotWidth, PreviewHeight);
            float boxW = slotWidth - 2f;
            float boxH = PreviewHeight - 22f - 2f - 4f;

            Image box = CreateImage("Box", host, 1f, 2f, boxW, boxH, Color.clear);
            box.raycastTarget = false;

            if (sprite != null)
            {
                Image image = CreateImage("Image", host, 1f, 2f, boxW, boxH, Color.white);
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
            else
            {
                // 无图：凹槽 + 文字。和 ImGui 版一样，不画空图。
                Image well = CreateImage("Well", host, 1f, 2f, boxW, boxH,
                    new Color(1f, 1f, 1f, 0.04f));
                well.raycastTarget = false;
                TextMeshProUGUI none = CreateText("None", host, 1f, 2f, boxW, boxH,
                    "<无图片>", 11f, TextMuted, TextAlignmentOptions.Center);
                none.raycastTarget = false;
            }

            TextMeshProUGUI caption = CreateText("Caption", host, 1f,
                2f + boxH + 2f, boxW, 22f,
                string.IsNullOrEmpty(filename) ? "<无图片>" : filename,
                11f, TextMuted, TextAlignmentOptions.MidlineLeft);
            caption.overflowMode = TextOverflowModes.Ellipsis;
            caption.raycastTarget = false;

            return y + PreviewHeight + CardPad + CardGap;
        }

        private float MeasureLongestLabel()
        {
            float need = 0f;
            for (int i = 0; i < visible.Count; i++)
            {
                need = Mathf.Max(need, EditorPropertyPanel.ResolveLabel(
                    target, visible[i].Value).Length * 13f * 0.9f);
            }
            return Mathf.Min(need, width * 0.5f);
        }

        private float MeasureValueNeed()
        {
            float need = MinimumValueWidth;
            for (int i = 0; i < visible.Count; i++)
            {
                ADOFAI.PropertyInfo info = visible[i].Value;
                if (info.type == PropertyType.Enum && info.enumType != null)
                {
                    string[] names = Enum.GetNames(info.enumType);
                    for (int j = 0; j < names.Length; j++)
                    {
                        string text = EditorPropertyPanel.DisplayEnumName(
                            target, info, names[j]);
                        need = Mathf.Max(need, text.Length * 12f * 0.9f + 12f);
                    }
                }
                else if (info.type == PropertyType.Vector2Range
                    || info.type == PropertyType.MinMaxGradient)
                {
                    need = Mathf.Max(need, MinimumValueWidth + 24f);
                }
            }
            return need;
        }

        private Row ObtainRow(int index)
        {
            while (rows.Count <= index)
            {
                rows.Add(CreateRow(rows.Count));
            }
            Row row = rows[index];
            if (!row.Rect.gameObject.activeSelf)
            {
                row.Rect.gameObject.SetActive(true);
            }
            return row;
        }

        private void HideAllRows()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Rect.gameObject.activeSelf)
                {
                    rows[i].Rect.gameObject.SetActive(false);
                }
                rows[i].Key = null;
                rows[i].Info = null;
            }
        }

        private Row CreateRow(int index)
        {
            Row row = new Row();
            RectTransform rect = CreateRect("Row" + index, content, CardPad, 0f, 100f,
                WideRowHeight);
            row.Rect = rect;

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;
            row.Background = background;

            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = false;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);
            HoverFill hoverFill = rect.gameObject.AddComponent<HoverFill>();
            hoverFill.Configure(hover, HoverColor);

            RectTransform enableRect = CreateRect("Enable", rect, 0f, 0f, EnableWidth, 22f);
            Image enableHit = CreateImage("Hit", enableRect, 0f, 0f, 0f, 0f, Color.clear);
            enableHit.raycastTarget = true;
            Stretch(enableHit.rectTransform, 0f, 0f, 0f, 0f);
            // 方框用极淡白填充当轮廓：Image 画不出 1px 边，Outline 在透明图上
            // 也不可见 —— 实心 8% 白是这套主题里“看得见又不抢眼”的那一档。
            Image box = CreateImage("Box", enableRect, 2f, 2f, 18f, 18f, Hairline);
            box.raycastTarget = false;
            TextMeshProUGUI check = CreateText("Check", enableRect, 2f, 2f, 18f, 18f,
                "✓", 12f, TextSecondary, TextAlignmentOptions.Center);
            check.raycastTarget = false;
            row.EnableCheck = check;
            Button enableButton = enableRect.gameObject.AddComponent<Button>();
            enableButton.targetGraphic = enableHit;
            enableButton.transition = Selectable.Transition.None;
            enableButton.onClick.AddListener(delegate { ToggleDisabled(row); });
            row.EnableButton = enableButton;
            row.EnableRect = enableRect;

            row.Label = CreateText("Label", rect, EnableWidth + LabelLeftGap, 0f,
                100f, 22f, string.Empty, 13f, TextSecondary,
                TextAlignmentOptions.MidlineLeft);
            row.Label.overflowMode = TextOverflowModes.Ellipsis;
            row.Label.raycastTarget = false;

            row.Mixed = CreateText("Mixed", rect, 0f, 0f, MixedChipWidth, 22f,
                "多个值", 11f, TextMuted, TextAlignmentOptions.MidlineRight);
            row.Mixed.raycastTarget = false;
            row.Mixed.gameObject.SetActive(false);

            row.ValueHost = CreateRect("Value", rect, 0f, 0f, 100f, ControlHeight);
            // 挂在 ValueHost 自己身上：它的子物体每次绑定都会被清空重建，
            // 挂在这一层才不会被一起清掉。
            row.ValueGroup = row.ValueHost.gameObject.AddComponent<CanvasGroup>();
            return row;
        }

        private void BindRow(Row row, string key, ADOFAI.PropertyInfo info, float y,
            float rowHeight, bool stacked, float labelColumn, float available)
        {
            row.Key = key;
            row.Info = info;
            row.Height = rowHeight;
            SetTopLeft(row.Rect, CardPad, y, available, rowHeight);

            // **`Note` 是整行说明文字，没有标签列、也没有勾选框。**
            // 让它占满整行宽度，否则一行字被挤在右侧一小块里读不下去。
            if (info.type == PropertyType.Note)
            {
                row.Label.gameObject.SetActive(false);
                row.EnableButton.gameObject.SetActive(false);
                row.EnableRect.gameObject.SetActive(false);
                row.Mixed.gameObject.SetActive(false);
                ClearChildren(row.ValueHost);
                row.ValueHost.gameObject.SetActive(true);
                SetTopLeft(row.ValueHost, 0f, 0f, available, rowHeight);
                BuildValue(row, info, key, row.ValueHost, available);
                return;
            }
            row.EnableRect.gameObject.SetActive(true);
            // 行对象是复用的：上一个绑定可能是 Note 行（把标签藏了），
            // 换回普通属性时得重新显示出来。
            row.Label.gameObject.SetActive(true);
            row.EnableButton.gameObject.SetActive(true);

            string label = EditorPropertyPanel.ResolveLabel(target, info);
            if (row.Label.text != label)
            {
                row.Label.text = label;
            }

            bool canDisable = info.canBeDisabled;
            bool disabled = canDisable && EditorPropertyPanel.IsDisabled(target, info.name);
            row.EnableButton.gameObject.SetActive(canDisable);
            row.EnableCheck.text = disabled ? string.Empty : "✓";
            row.EnableCheck.color = disabled ? TextMuted : TextSecondary;

            // **禁用要看得出来、也要真的改不动。**
            //
            // 只把 ✓ 去掉太轻了：勾完看不出区别，就以为「勾选框是摆设」。
            // 所以标签和值一起压暗，并让值那一块整块不接交互 —— 滑块拖不动、
            // 输入框点不进去、开关点不亮。想改先勾回来，和原生面板一致。
            row.Label.color = disabled ? TextMuted : TextSecondary;
            if (row.ValueGroup != null)
            {
                row.ValueGroup.interactable = !disabled;
                row.ValueGroup.alpha = disabled ? 0.45f : 1f;
            }

            // **勾选框要和行中线对齐。** 它建的时候是钉在 y=0 的固定 22 高，
            // 宽排法行高 44、窄排法 66，不重新摆就贴在行顶（用户看到的就是
            // 「勾选框没对齐中间」）。窄排法对齐的是第一行标签。
            float enableY = stacked ? 3f : Mathf.Round((rowHeight - 22f) * 0.5f);
            SetTopLeft(row.EnableRect, 0f, enableY, EnableWidth, 22f);

            ClearChildren(row.ValueHost);

            float valueLeft;
            float valueWidth;
            if (stacked)
            {
                SetTopLeft(row.Label.rectTransform, LabelLeftGap + EnableWidth, 4f,
                    Mathf.Max(40f, available - EnableWidth - LabelLeftGap), 20f);
                valueLeft = CardPad;
                valueWidth = Mathf.Max(60f, available - CardPad * 2f);
                bool twoLines = rowHeight > 70f;
                SetTopLeft(row.ValueHost, valueLeft, twoLines ? 26f : 30f, valueWidth,
                    twoLines ? 56f : ControlHeight);
            }
            else
            {
                SetTopLeft(row.Label.rectTransform, EnableWidth + LabelLeftGap, 0f,
                    Mathf.Max(40f, labelColumn), rowHeight);
                valueLeft = EnableWidth + LabelLeftGap + labelColumn;
                valueWidth = Mathf.Max(60f, available - valueLeft);
                SetTopLeft(row.ValueHost, valueLeft,
                    (rowHeight - ControlHeight) * 0.5f, valueWidth, ControlHeight);
            }

            bool mixed = panel.InBatch && !panel.BatchIsUniform(key);
            row.Mixed.gameObject.SetActive(mixed);
            if (mixed)
            {
                SetTopLeft(row.Mixed.rectTransform,
                    available - MixedChipWidth, (rowHeight - 22f) * 0.5f,
                    MixedChipWidth, 22f);
                row.ValueHost.sizeDelta = new Vector2(
                    Mathf.Max(40f, valueWidth - MixedChipWidth - LabelLeftGap),
                    row.ValueHost.sizeDelta.y);
            }

            BuildValue(row, info, key, row.ValueHost, row.ValueHost.sizeDelta.x);
        }

        /// <summary>
        /// 参数栏的一律写入口：**先过只读闸，再交给数据层**。
        ///
        /// `Commit` 里本来也有这道闸，但那只在拿到 owner 时生效；这里再拦一次
        /// 更稳（用户报过「只读模式下很多参数还能改」）。提示语由 `AllowEdit`
        /// 统一弹，不另写。
        /// </summary>
        private int commitVersion;

        /// <summary>提交过多少次。外部（粒子预览）靠它判断要不要重建。</summary>
        internal int CommitVersion { get { return commitVersion; } }

        private void CommitValue(LevelEvent levelEvent, string key, object value)
        {
            if (overlay != null && !overlay.AllowEdit())
            {
                return;
            }
            panel.Commit(levelEvent, key, value);
            commitVersion++;
        }

        private void ToggleDisabled(Row row)
        {
            if (row == null || row.Info == null || target == null)
            {
                return;
            }
            if (overlay != null && !overlay.AllowEdit())
            {
                return;
            }
            bool disabled = EditorPropertyPanel.IsDisabled(target, row.Info.name);
            panel.SetDisabled(target, row.Info.name, !disabled);
            Rebuild();
        }

        // ── 控件 ─────────────────────────────────────────────────────────

        private void BuildValue(Row row, ADOFAI.PropertyInfo info, string key,
            RectTransform host, float availableWidth)
        {
            string unit = ResolveUnit(info);
            float unitWidth = string.IsNullOrEmpty(unit)
                ? 0f : unit.Length * 11f + UnitGap;
            float valueWidth = Mathf.Max(40f, availableWidth - unitWidth);

            if (info.type == PropertyType.Int && info.name == "floor")
            {
                row.Field1 = CreateField(host, 0f, valueWidth,
                    target.floor.ToString(CultureInfo.InvariantCulture),
                    delegate(string text)
                    {
                        int parsed;
                        if (int.TryParse(text, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out parsed))
                        {
                            CommitFloor(parsed);
                        }
                    });
            }
            else
            {
                // **高级滤镜的「滤镜」要下拉，不能是文本框。**
                // 它的值是类名字符串（`CameraFilterPack_AAA_SuperComputer`），
                // 通用分支会当成普通字符串画成输入框 —— 既列不出可选项，
                // 也几乎不可能手打对。
                if (IsAdvancedFilterProperty(info, key))
                {
                    BuildFilterDropdown(host, valueWidth, info, key, row);
                    return;
                }

                switch (info.type)
                {
                    case PropertyType.Bool:
                        BuildBool(host, valueWidth, info, key);
                        break;
                    case PropertyType.Int:
                        row.Field1 = CreateField(host, 0f, valueWidth,
                            EditorPropertyPanel.FormatInt(
                                EditorPropertyPanel.SafeGet(target, info, 0f)),
                            delegate(string text)
                            {
                                // **空值。** Int 没有 NaN 这一说，空只能理解成
                                // "这次什么都没改"：把框设回原值，不写事件数据。
                                if (string.IsNullOrEmpty(text))
                                {
                                    row.Field1.SetTextWithoutNotify(
                                        EditorPropertyPanel.FormatInt(
                                            EditorPropertyPanel.SafeGet(target, info, 0f)));
                                    return;
                                }
                                float parsed;
                                if (float.TryParse(text, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out parsed))
                                {
                                    CommitValue(target, key, Mathf.RoundToInt(parsed));
                                }
                            });
                        break;
                    case PropertyType.Float:
                        row.Field1 = CreateField(host, 0f, valueWidth,
                            EditorPropertyPanel.FormatFloat(
                                EditorPropertyPanel.SafeGet(target, info, 0f)),
                            delegate(string text)
                            {
                                // **清空 = 允许留空。**
                                //
                                // 原版用 `NaN` 表达"这一项没设过"：空字符串
                                // 存成 NaN，读回来显示成空字符串 —— 界面上
                                // 一个字符都不显示，不是 0。`FormatFloat(NaN)`
                                // 已经做了同样的转换（见它的说明），这里补上
                                // 写入侧。
                                //
                                // 原来清空落进「解析失败」那一支，什么都不写，
                                // 重画时读到回落值 `0f`，看起来就像被强制
                                // 改成了 0 —— 用户报的就是这个。
                                if (string.IsNullOrEmpty(text))
                                {
                                    CommitValue(target, key, float.NaN);
                                    return;
                                }
                                float parsed;
                                if (float.TryParse(text, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out parsed))
                                {
                                    CommitValue(target, key, parsed);
                                }
                            });
                        break;
                    case PropertyType.String:
                    case PropertyType.LongString:
                        row.Field1 = CreateField(host, 0f, valueWidth,
                            EditorPropertyPanel.SafeGet(target, info, string.Empty),
                            delegate(string text)
                            {
                                CommitValue(target, key, text ?? string.Empty);
                            });
                        break;
                    case PropertyType.File:
                        BuildFile(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.Enum:
                        BuildEnum(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.Color:
                        BuildColor(host, Mathf.Max(valueWidth, 120f), info, key, row);
                        break;
                    case PropertyType.Vector2:
                        BuildVector2(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.FloatPair:
                        BuildFloatPair(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.Vector2Range:
                        BuildVector2Range(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.MinMaxGradient:
                        BuildGradient(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.Tile:
                        BuildTile(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.ParticlePlayback:
                        BuildParticle(host, valueWidth);
                        break;
                    case PropertyType.Rating:
                        // 星星评分。原生 `PropertyControl_Rating` 就是一排
                        // 可点的星星，点第 N 颗写入整数 N。
                        BuildRating(host, valueWidth, info, key, row);
                        break;
                    case PropertyType.Export:
                        // 「导出」按钮。原生那个会拉起游戏的导出窗口
                        // （`ShowExportWindow`），和谱面数据无关。
                        BuildExportButton(host, valueWidth);
                        break;
                    case PropertyType.Array:
                    case PropertyType.List:
                        // **这两类故意不给控件。**
                        //
                        // `List` 在原生里只服务两个属性：`decorations` 和
                        // `events` —— 那是原生自己那两栏列表，AES 有独立的
                        // 装饰物栏和时间轴，不需要再来一份。
                        //
                        // `Array` 是内部数据结构（原生也没有任何控件，
                        // 存盘时原样写回）。给它个"暂不支持"纯属噪声。
                        CreateText("Passthrough", host, 0f, 0f, valueWidth, ControlHeight,
                            "（由编辑器管理）", 11f, TextMuted,
                            TextAlignmentOptions.MidlineLeft).raycastTarget = false;
                        break;
                    case PropertyType.Note:
                        // **纯说明文字，不是可编辑的值。**
                        //
                        // 原生 `PropertyControl_Note` 就一句话：
                        // `noteText.text = RDString.Get(propertyInfo.noteKey)` ——
                        // 没有输入框、没有按钮，只是给作者看的提示
                        // （高级滤镜那一页顶上那条"警告！..."就是它）。
                        //
                        // 以前这里走 default，显示成「暂不支持 Note」。
                        // 那个"暂不支持"其实是我方没实现，不是游戏的问题。
                        BuildNote(host, availableWidth, info);
                        break;
                    default:
                        CreateText("Unsupported", host, 0f, 0f, valueWidth, ControlHeight,
                            "<暂不支持 " + info.type + ">", 11f, TextMuted,
                            TextAlignmentOptions.MidlineLeft).raycastTarget = false;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(unit))
            {
                CreateText("Unit", host, valueWidth + UnitGap, 0f,
                    Mathf.Max(1f, availableWidth - valueWidth - UnitGap), ControlHeight,
                    unit, 11f, TextMuted, TextAlignmentOptions.MidlineLeft).raycastTarget = false;
            }
        }

        private void BuildBool(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key)
        {
            bool value = EditorPropertyPanel.SafeGet(target, info, false);
            RectTransform rect = CreateRect("Bool", host, 0f, 0f, 20f, ControlHeight);
            Image hit = CreateImage("Hit", rect, 0f, 0f, 0f, 0f, Color.clear);
            hit.raycastTarget = true;
            Stretch(hit.rectTransform, 0f, 0f, 0f, 0f);
            Image box = CreateImage("Box", rect, 1f, 2f, 18f, 18f, Hairline);
            box.raycastTarget = false;
            if (value)
            {
                CreateText("Check", rect, 1f, 2f, 18f, 18f, "✓", 12f,
                    TextSecondary, TextAlignmentOptions.Center).raycastTarget = false;
            }
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { CommitValue(target, key, !value); });
        }

        private void BuildFile(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            // 有原生文件夹图标时按钮只需 26 宽 —— 原来按文字「浏览」留的 56
            // 会在图标周围空一大块，还会和输入框的右缘错开。
            float buttonWidth = NativeFileIcon.Get() != null ? 26f : 56f;
            const float gap = 6f;
            float fieldWidth = Mathf.Max(1f, valueWidth - buttonWidth - gap);

            row.Field1 = CreateField(host, 0f, fieldWidth,
                EditorPropertyPanel.SafeGet(target, info, string.Empty),
                delegate(string text) { CommitValue(target, key, text ?? string.Empty); });

            Button browse = CreateBrowseButton(host, fieldWidth + gap,
                (ControlHeight - 22f) * 0.5f, buttonWidth, 22f,
                delegate
                {
                    if (overlay != null && !overlay.AllowEdit())
                    {
                        return;
                    }
                    string picked = PickFile(info);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        CommitValue(target, key, picked);
                    }
                });
            // 「浏览」是动作入口，**用链接蓝**（和版本号同色）——
            // 白字小按钮在深底上读不出"可以点"。
            StyleAsLink(browse);
        }

        /// <summary>
        /// 一颗「选文件」按钮：有原生文件夹图标就用图标，没有退回文字「浏览」。
        /// 所有会拉起 Windows 资源管理器的入口都走这里（File / browsable 枚举）。
        /// </summary>
        private Button CreateBrowseButton(RectTransform host, float x, float y,
            float width, float height, Action action)
        {
            Sprite icon = NativeFileIcon.Get();
            Button button = CreateButton(host, x, y, width, height,
                icon == null ? "浏览" : string.Empty, delegate { action(); });
            if (icon == null)
            {
                StyleAsLink(button);
                return button;
            }

            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
            Image image = CreateImage("Icon", button.GetComponent<RectTransform>(),
                (width - 16f) * 0.5f, (height - 16f) * 0.5f, 16f, 16f, Color.white);
            image.sprite = icon;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return button;
        }

        /// <summary>把一颗文字按钮染成链接蓝，并加下划线，明确"这是能点的"。</summary>
        private void StyleAsLink(Button button)
        {
            if (button == null)
            {
                return;
            }
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label == null)
            {
                return;
            }
            label.color = overlay != null
                ? overlay.VersionLinkColor
                : new Color(0.55f, 0.72f, 1f, 1f);
            label.fontStyle = FontStyles.Underline;
        }

        private void BuildEnum(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            string current = EditorPropertyPanel.ReadEnumName(target, info);
            string display = EditorPropertyPanel.DisplayEnumName(target, info, current);
            float width = valueWidth;
            float browseWidth = NativeFileIcon.Get() != null ? 26f : 56f;
            if (info.browsable)
            {
                width = Mathf.Max(60f, valueWidth - browseWidth - 6f);
            }

            RectTransform rect = CreateRect("Enum", host, 0f, 0f, width, ControlHeight);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            row.EnumLabel = CreateText("Label", rect, 6f, 0f, width - 20f, ControlHeight,
                display, 12f, TextPrimary, TextAlignmentOptions.MidlineLeft);
            row.EnumLabel.overflowMode = TextOverflowModes.Ellipsis;
            row.EnumLabel.raycastTarget = false;
            // 这是真正的枚举标签，交给 `RefreshValues` 末尾那段按枚举规则回填。
            row.EnumLabelIsEnum = true;
            UiArrowSpin enumArrow = CreateDropArrow(rect, width - 24f, 0f, 22f, ControlHeight);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;            button.onClick.AddListener(delegate
            {
                OpenEnumPopup(rect, info, key, enumArrow);
            });

            if (info.browsable)
            {
                Button browse = CreateBrowseButton(host, width + 6f,
                    (ControlHeight - 22f) * 0.5f, browseWidth, 22f, delegate
                    {
                        if (overlay != null && !overlay.AllowEdit())
                        {
                            return;
                        }
                        string picked = PickFile(info);
                        if (!string.IsNullOrEmpty(picked))
                        {
                            CommitValue(target, key, picked);
                        }
                    });
                StyleAsLink(browse);
            }
        }

        private void BuildColor(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            Color value = EditorPropertyPanel.SafeGetColor(target, info);
            const float swatchSize = 26f;
            const float gap = 6f;
            float hexWidth = Mathf.Max(60f, valueWidth - swatchSize - gap);

            RectTransform swatchRect = CreateRect("Swatch", host, 0f, 0f, swatchSize, ControlHeight);
            Image swatch = swatchRect.gameObject.AddComponent<Image>();
            swatch.color = value;
            swatch.raycastTarget = true;
            row.Swatch = swatch;

            Button button = swatchRect.gameObject.AddComponent<Button>();
            button.targetGraphic = swatch;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { OpenColorPopup(swatchRect, info, key, row); });

            // HEX 直接写成可编辑输入框（和 ImGui 版一致：色块 + HEX 两件）。
            string hex = EditorPropertyPanel.ColorToHex(value);
            row.Field1 = CreateField(host, swatchSize + gap, hexWidth, hex,
                delegate(string text)
                {
                    Color parsed;
                    if (EditorPropertyPanel.TryParseHex(text, out parsed))
                    {
                        CommitValue(target, key, EditorPropertyPanel.ColorToHex(parsed));
                    }
                });
        }

        private void BuildVector2(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            Vector2 value = EditorPropertyPanel.SafeGet(target, info, Vector2.zero);
            float half = Mathf.Max(24f, (valueWidth - PairGap) * 0.5f);
            // **另一分量要在提交时现读，不能用绑定时抓的那份快照。**
            //
            // 快照是建行那一刻的：先改 X 提交成功、再改 Y 时，Y 的闭包里
            // `value.x` 还是旧值 —— 于是把刚改好的 X 覆写回去。表现就是
            // "改第一个没生效、改第二个成功了"（反向操作同样丢前一个）。
            row.Field1 = CreateField(host, 0f, half,
                FieldNumber(value.x),
                delegate(string text)
                {
                    // 空 = 留空（存 NaN，`FieldNumber` 里会显示成空）。
                    if (string.IsNullOrEmpty(text))
                    {
                        Vector2 current = EditorPropertyPanel.SafeGet(
                            target, info, Vector2.zero);
                        CommitValue(target, key, new Vector2(float.NaN, current.y));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Vector2 current = EditorPropertyPanel.SafeGet(
                            target, info, Vector2.zero);
                        CommitValue(target, key, new Vector2(parsed, current.y));
                    }
                });
            row.Field2 = CreateField(host, half + PairGap, half,
                FieldNumber(value.y),
                delegate(string text)
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        Vector2 current = EditorPropertyPanel.SafeGet(
                            target, info, Vector2.zero);
                        CommitValue(target, key, new Vector2(current.x, float.NaN));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Vector2 current = EditorPropertyPanel.SafeGet(
                            target, info, Vector2.zero);
                        CommitValue(target, key, new Vector2(current.x, parsed));
                    }
                });
        }

        private void BuildFloatPair(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            Tuple<float, float> pair = EditorPropertyPanel.SafeGet<Tuple<float, float>>(
                target, info, null);
            if (pair == null)
            {
                pair = new Tuple<float, float>(0f, 0f);
            }
            float low = pair.Item1;
            float high = pair.Item2;
            float half = Mathf.Max(24f, (valueWidth - PairGap) * 0.5f);

            // 同上：另一头现读，别用绑定时的快照。
            row.Field1 = CreateField(host, 0f, half,
                FieldNumber(low),
                delegate(string text)
                {
                    // 空 = 留空（存 NaN，显示仍是空）。见 `BuildValue` 的说明。
                    Tuple<float, float> current =
                        EditorPropertyPanel.SafeGet<Tuple<float, float>>(
                            target, info, null)
                        ?? new Tuple<float, float>(0f, 0f);
                    if (string.IsNullOrEmpty(text))
                    {
                        CommitValue(target, key,
                            new Tuple<float, float>(float.NaN, current.Item2));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key,
                            new Tuple<float, float>(parsed, current.Item2));
                    }
                });
            row.Field2 = CreateField(host, half + PairGap, half,
                FieldNumber(high),
                delegate(string text)
                {
                    Tuple<float, float> current =
                        EditorPropertyPanel.SafeGet<Tuple<float, float>>(
                            target, info, null)
                        ?? new Tuple<float, float>(0f, 0f);
                    if (string.IsNullOrEmpty(text))
                    {
                        CommitValue(target, key,
                            new Tuple<float, float>(current.Item1, float.NaN));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key,
                            new Tuple<float, float>(current.Item1, parsed));
                    }
                });
        }

        /// <summary>
        /// 两对 Vector2（`Tuple<Vector2,Vector2>`）：X 一行、Y 一行，
        /// 每行「最小 - 最大」。挤成一行读不出哪个是最小、哪个是最大。
        /// </summary>
        private void BuildVector2Range(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            Tuple<Vector2, Vector2> range =
                EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(target, info, null);
            if (range == null)
            {
                range = new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
            }
            Vector2 a = range.Item1;
            Vector2 b = range.Item2;
            float fieldWidth = Mathf.Max(24f, (valueWidth - 18f - 14f) * 0.5f);
            float secondX = 18f + fieldWidth + 18f;

            // X 行
            CreateText("AxisX", host, 0f, 0f, 16f, ControlHeight,
                "X", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;
            row.Field1 = CreateField(host, 18f, fieldWidth,
                EditorPropertyPanel.FormatFloat(a.x),
                delegate(string text)
                {
                    // 另三格现读：`a`/`b` 是建行时的快照，连续改两格时
                    // 后面的会把前面刚写进去的值覆写回旧值。
                    Tuple<Vector2, Vector2> current =
                        EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                            target, info, null)
                        ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                    a = current.Item1;
                    b = current.Item2;
                    // 空 = 留空（存 NaN，显示仍是空）。见 `BuildValue` 的说明。
                    if (string.IsNullOrEmpty(text))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            new Vector2(float.NaN, a.y), b));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            new Vector2(parsed, a.y), b));
                    }
                });
            CreateText("DashX", host, 18f + fieldWidth + 4f, 0f, 10f, ControlHeight,
                "-", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;
            row.Field2 = CreateField(host, secondX, fieldWidth,
                EditorPropertyPanel.FormatFloat(b.x),
                delegate(string text)
                {
                    Tuple<Vector2, Vector2> current =
                        EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                            target, info, null)
                        ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                    a = current.Item1;
                    b = current.Item2;
                    if (string.IsNullOrEmpty(text))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            a, new Vector2(float.NaN, b.y)));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            a, new Vector2(parsed, b.y)));
                    }
                });

            // Y 行
            CreateText("AxisY", host, 0f, 28f, 16f, ControlHeight,
                "Y", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;
            CreateField(host, 18f, fieldWidth, EditorPropertyPanel.FormatFloat(a.y),
                delegate(string text)
                {
                    Tuple<Vector2, Vector2> current =
                        EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                            target, info, null)
                        ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                    a = current.Item1;
                    b = current.Item2;
                    if (string.IsNullOrEmpty(text))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            new Vector2(a.x, float.NaN), b));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            new Vector2(a.x, parsed), b));
                    }
                });
            CreateText("DashY", host, 18f + fieldWidth + 4f, 28f, 10f, ControlHeight,
                "-", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;
            CreateField(host, secondX, fieldWidth, EditorPropertyPanel.FormatFloat(b.y),
                delegate(string text)
                {
                    Tuple<Vector2, Vector2> current =
                        EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                            target, info, null)
                        ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                    a = current.Item1;
                    b = current.Item2;
                    if (string.IsNullOrEmpty(text))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            a, new Vector2(b.x, float.NaN)));
                        return;
                    }
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            a, new Vector2(b.x, parsed)));
                    }
                });
        }

        private void BuildGradient(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            ParticleSystem.MinMaxGradient gradient;
            try
            {
                gradient = target.GetMinMaxGradient(info.name);
            }
            catch
            {
                gradient = default(ParticleSystem.MinMaxGradient);
            }
            bool two = gradient.mode == ParticleSystemGradientMode.TwoColors;
            Color low = two ? gradient.colorMin : gradient.color;
            Color high = two ? gradient.colorMax : gradient.color;
            float half = Mathf.Max(24f, (valueWidth - PairGap) * 0.5f);

            BuildGradientHalf(host, 0f, half, low, delegate(Color picked)
            {
                CommitGradient(key, picked, high);
            });
            BuildGradientHalf(host, half + PairGap, half, high, delegate(Color picked)
            {
                CommitGradient(key, low, picked);
            });
        }

        private void BuildGradientHalf(RectTransform host, float x, float width,
            Color value, Action<Color> apply)
        {
            RectTransform swatchRect = CreateRect("Swatch", host, x, 0f, 24f, ControlHeight);
            Image swatch = swatchRect.gameObject.AddComponent<Image>();
            swatch.color = value;
            swatch.raycastTarget = true;
            Button button = swatchRect.gameObject.AddComponent<Button>();
            button.targetGraphic = swatch;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                OpenColorPicker(swatchRect, value, delegate(Color picked)
                {
                    apply(picked);
                });
            });
            CreateText("Hex", host, x + 26f, 0f, Mathf.Max(20f, width - 26f),
                ControlHeight, EditorPropertyPanel.ColorToHex(value),
                11f, TextMuted, TextAlignmentOptions.MidlineLeft).raycastTarget = false;
        }

        private void CommitGradient(string key, Color low, Color high)
        {
            ADOFAI.Editor.Models.SerializedMinMaxGradient value =
                new ADOFAI.Editor.Models.SerializedMinMaxGradient
                {
                    mode = ParticleSystemGradientMode.TwoColors,
                    color1 = EditorPropertyPanel.ColorToHex(low),
                    color2 = EditorPropertyPanel.ColorToHex(high)
                };
            CommitValue(target, key, value);
        }

        private void BuildTile(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            Tuple<int, TileRelativeTo> current = ReadTile(info);
            float numberWidth = Mathf.Clamp(valueWidth * 0.42f, 48f, valueWidth);
            float modeWidth = Mathf.Max(40f, valueWidth - numberWidth - PairGap);

            row.Field1 = CreateField(host, 0f, numberWidth,
                current.Item1.ToString(CultureInfo.InvariantCulture),
                delegate(string text)
                {
                    int parsed;
                    if (int.TryParse(text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        CommitValue(target, key,
                            new Tuple<int, TileRelativeTo>(parsed, ReadTile(info).Item2));
                    }
                });

            RectTransform modeRect = CreateRect("Mode", host,
                numberWidth + PairGap, 0f, modeWidth, ControlHeight);
            Image hit = modeRect.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            // **标签要存进 row，`RefreshValues` 才刷得到它。**
            //
            // 原来这里是直接 `CreateText(...)` 丢掉返回值，于是「本格 / 起点 / 终点」
            // 只在建行那一刻写死一次 —— 之后不管选哪个，标签都不动。值确实写进
            // 谱面了（行为是对的），只有显示没跟上，看起来就是「选了不生效」。
            TextMeshProUGUI modeLabel = CreateText("Label", modeRect, 6f, 0f,
                modeWidth - 18f, ControlHeight, TileRelativeLabel(current.Item2),
                12f, TextPrimary, TextAlignmentOptions.MidlineLeft);
            modeLabel.raycastTarget = false;
            // 复用 `EnumLabel`：它本来就是「下拉/切换类控件显示当前值的那个标签」，
            // Tile 的模式框正是这一类。再加一个新字段只会多一处要记得同步的地方。
            row.EnumLabel = modeLabel;
            UiArrowSpin tileArrow = CreateDropArrow(modeRect, modeWidth - 24f, 0f, 22f, ControlHeight);
            Button modeButton = modeRect.gameObject.AddComponent<Button>();
            modeButton.targetGraphic = hit;
            modeButton.transition = Selectable.Transition.None;
            modeButton.onClick.AddListener(delegate
            {
                OpenChoicePopup(modeRect, new[] { "本格", "起点", "终点" }, tileArrow,
                    delegate(int choice)
                    {
                        TileRelativeTo mode = choice == 1
                            ? TileRelativeTo.Start
                            : (choice == 2 ? TileRelativeTo.End : TileRelativeTo.ThisTile);
                        CommitValue(target, key,
                            new Tuple<int, TileRelativeTo>(ReadTile(info).Item1, mode));
                    });
            });
        }

        private void BuildParticle(RectTransform host, float valueWidth)
        {
            const float gap = 6f;
            float buttonWidth = Mathf.Max(24f, (valueWidth - gap * 3f) * 0.25f);
            string[] labels = { "播放", "停止", "重置", "编辑" };
            for (int i = 0; i < labels.Length; i++)
            {
                int slot = i;
                CreateButton(host, i * (buttonWidth + gap),
                    (ControlHeight - 24f) * 0.5f, buttonWidth, 24f, labels[i],
                    delegate { HandleParticle(slot); });
            }
        }

        private void HandleParticle(int slot)
        {
            if (target == null)
            {
                return;
            }

            // **「编辑」不依赖活的粒子对象。** 原来它和播放/停止/重置共用
            // 下面那串 `particle == null` 的早退，于是没有实例化过的粒子
            // 事件点「编辑」什么都不发生 —— 用户看到的就是"浮窗打不开"。
            if (slot == 3)
            {
                // 预览走共用宿主（`ParticlePreviewHost`），面板是 uGUI 浮窗。
                UiParticleWindow.Open(target);
                return;
            }

            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null)
            {
                return;
            }
            scrParticleDecoration particle =
                scrDecorationManager.GetDecoration(target) as scrParticleDecoration;
            if (particle == null || particle.particleSystem == null)
            {
                return;
            }
            ParticleSystem system = particle.particleSystem;

            switch (slot)
            {
                case 0:
                    ParticleSystem.MainModule main = system.main;
                    main.useUnscaledTime = true;
                    if (system.isPlaying)
                    {
                        system.Pause();
                    }
                    else
                    {
                        system.Play();
                    }
                    break;
                case 1:
                    system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    break;
                case 2:
                    system.Clear();
                    break;
                case 3:
                    // 打开新的 uGUI 浮窗；ImGui 那扇不再开。
                    UiParticleWindow.Open(target);
                    break;
            }
        }

        private Tuple<int, TileRelativeTo> ReadTile(ADOFAI.PropertyInfo info)
        {
            Tuple<int, TileRelativeTo> value =
                EditorPropertyPanel.SafeGet<Tuple<int, TileRelativeTo>>(target, info, null);
            return value ?? new Tuple<int, TileRelativeTo>(0, TileRelativeTo.ThisTile);
        }

        private static string TileRelativeLabel(TileRelativeTo mode)
        {
            string key = "enum.TileRelativeTo." + mode;
            try
            {
                string localized = RDString.Get(key);
                if (!string.IsNullOrWhiteSpace(localized)
                    && !string.Equals(localized, key, StringComparison.Ordinal))
                {
                    return localized;
                }
            }
            catch
            {
            }
            switch (mode)
            {
                case TileRelativeTo.Start:
                    return "起点";
                case TileRelativeTo.End:
                    return "终点";
                default:
                    return "本格";
            }
        }

        private void CommitFloor(int floor)
        {
            if (target == null)
            {
                return;
            }
            // floor 不在 data 字典里，走不了 Commit；只读闸要单独拦。
            if (overlay != null && !overlay.AllowEdit())
            {
                return;
            }
            // floor 挂在 LevelEvent 字段上，不在 data 字典里，不能走 Commit。
            target.floor = floor;
        }

        private string PickFile(ADOFAI.PropertyInfo info)
        {
            switch (info.fileType)
            {
                case ADOFAI.FileType.Audio:
                    return RDEditorUtils.ShowFileSelectorForAudio(
                        RDString.Get("editor.dialog.selectSound"), -1L);
                case ADOFAI.FileType.Image:
                    return RDEditorUtils.ShowFileSelectorForImage(
                        RDString.Get("editor.dialog.selectImage"), -1L);
                case ADOFAI.FileType.Video:
                    return RDEditorUtils.ShowFileSelectorForVideo(
                        RDString.Get("editor.dialog.selectVideo"), -1L);
                default:
                    return null;
            }
        }

        private static string ResolveUnit(ADOFAI.PropertyInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.unit))
            {
                return string.Empty;
            }
            bool exists;
            string localized = RDString.GetWithCheck("editor.unit." + info.unit, out exists);
            if (exists && !string.IsNullOrEmpty(localized))
            {
                return localized;
            }
            return info.unit;
        }

        // ── 值刷新 ───────────────────────────────────────────────────────

        /// <summary>
        /// 只回填值、不重建控件。输入框正在编辑时跳过 —— 否则每帧回填会
        /// 冲掉用户正在打的字。
        /// </summary>
        private void RefreshValues()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (!row.Rect.gameObject.activeSelf || row.Info == null || target == null)
                {
                    continue;
                }
                ADOFAI.PropertyInfo info = row.Info;

                switch (info.type)
                {
                    case PropertyType.Int:
                        SetFieldText(row.Field1,
                            info.name == "floor"
                                ? target.floor.ToString(CultureInfo.InvariantCulture)
                                : EditorPropertyPanel.FormatInt(
                                    EditorPropertyPanel.SafeGet(target, info, 0f)));
                        break;
                    case PropertyType.Float:
                        SetFieldText(row.Field1, EditorPropertyPanel.FormatFloat(
                            EditorPropertyPanel.SafeGet(target, info, 0f)));
                        break;
                    case PropertyType.String:
                    case PropertyType.LongString:
                    case PropertyType.File:
                        SetFieldText(row.Field1,
                            EditorPropertyPanel.SafeGet(target, info, string.Empty));
                        break;
                    case PropertyType.Color:
                    {
                        Color color = EditorPropertyPanel.SafeGetColor(target, info);
                        if (row.Swatch != null)
                        {
                            row.Swatch.color = color;
                        }
                        SetFieldText(row.Field1, EditorPropertyPanel.ColorToHex(color));
                        break;
                    }
                    case PropertyType.Vector2:
                    {
                        Vector2 value = EditorPropertyPanel.SafeGet(
                            target, info, Vector2.zero);
                        SetFieldText(row.Field1, FieldNumber(value.x));
                        SetFieldText(row.Field2, FieldNumber(value.y));
                        break;
                    }
                    case PropertyType.FloatPair:
                    {
                        Tuple<float, float> pair =
                            EditorPropertyPanel.SafeGet<Tuple<float, float>>(
                                target, info, null);
                        if (pair != null)
                        {
                            SetFieldText(row.Field1, FieldNumber(pair.Item1));
                            SetFieldText(row.Field2, FieldNumber(pair.Item2));
                        }
                        break;
                    }
                    case PropertyType.Tile:
                    {
                        Tuple<int, TileRelativeTo> tile = ReadTile(info);
                        SetFieldText(row.Field1,
                            tile.Item1.ToString(CultureInfo.InvariantCulture));
                        // **模式标签也要回填。** 少了这一句，从下拉里选了「起点 / 终点」
                        // 之后标签一直停在建行时的那个值 —— 值写进谱面了，行为是对的，
                        // 只有显示不动，看起来像「选了没反应」。
                        if (row.EnumLabel != null)
                        {
                            row.EnumLabel.text = TileRelativeLabel(tile.Item2);
                        }
                        break;
                    }
                    case PropertyType.Rating:
                    {
                        // 星星是按钮串，没法像输入框那样改文本。
                        // 值真变了（联机对方改、撤销）才整块重建，普通帧什么都不做。
                        int nowStars = Mathf.RoundToInt(
                            EditorPropertyPanel.SafeGet(target, info, 0f));
                        if (row.RatingValue != nowStars)
                        {
                            row.RatingValue = nowStars;
                            pendingRatingRebuild = true;
                            return;
                        }
                        break;
                    }
                }

                if (row.EnumLabel != null && row.EnumLabelIsEnum)
                {
                    // **高级滤镜那一行单独走。**
                    //
                    // `filter` 的 PropertyType 是 String，值是滤镜类名；它的显示名
                    // 要从滤镜表里查（`FilterDisplayFor`），不能走 `DisplayEnumName`
                    // —— 那条路是按 Enum 类型名查本地化，对类名字符串查不到，
                    // 会原样回落成 `CameraFilterPack_XXX` 这种又长又难看的字符串。
                    string current;
                    string display;
                    if (IsAdvancedFilterProperty(info, row.Key))
                    {
                        current = EditorPropertyPanel.SafeGet<string>(target, info, null)
                            ?? string.Empty;
                        display = FilterDisplayFor(current);
                    }
                    else
                    {
                        current = EditorPropertyPanel.ReadEnumName(target, info);
                        display = EditorPropertyPanel.DisplayEnumName(target, info, current);
                    }
                    if (row.EnumLabel.text != display)
                    {
                        row.EnumLabel.text = display;
                    }
                }
            }
        }

        /// <summary>
        /// 成对数值框里显示的数字。
        ///
        /// 谱面里没写过的分量读出来是 NaN（`[null, null]`）。**NaN 显示成空串**，
        /// 和单值框的 `FormatFloat` 一致 —— 那正是「这一项没设过」的诚实表达，
        /// 也是原版 `PropertyControl_Vector2` 的做法（`ConvertNaNToEmpty`）。
        ///
        /// 这里原来对 NaN 显示 `"0"`，理由是"空着会被读成坏了/没加载出来"。
        /// 但那样作者就永远分不清「值是 0」和「没设过」，而且清空框之后
        /// 立刻又弹回 0，看起来像退格不起作用 —— 用户后来报的就是这个。
        /// </summary>
        private static string FieldNumber(float value)
        {
            return EditorPropertyPanel.FormatFloat(value);
        }

        /// <summary>回填一个输入框 —— 正在编辑的那个跳过，否则会冲掉用户打的字。</summary>
        private static void SetFieldText(TMP_InputField field, string text)
        {
            if (field == null || field.isFocused || text == null)
            {
                return;
            }
            if (!string.Equals(field.text, text, StringComparison.Ordinal))
            {
                field.text = text;
            }
        }

        // ── 弹层：枚举 / 颜色 ─────────────────────────────────────────────

        /// <summary>
        /// 弹层统一挂到工作台共用的浮层画布（32000）。
        ///
        /// 自己建一层嵌套 Canvas 试过：列表只出来个底、里面看不见内容 ——
        /// 而菜单用的是 `overlay.PopupLayer`，那套已经验证能正常显示，
        /// 所以这里复用同一块画布，不再另起一层。
        /// </summary>
        private void EnsurePopupLayer()
        {
            if (popupLayer != null || overlay == null)
            {
                return;
            }
            popupLayer = overlay.PopupLayer;
        }

        private void OpenPopupShell(RectTransform anchor, float popupWidth, float popupHeight,
            Action<RectTransform> buildContent)
        {
            OpenPopupShell(anchor, popupWidth, popupHeight, null, buildContent);
        }

        /// <summary>
        /// <paramref name="triggerArrow"/> 是触发这次弹层的下拉按钮上的箭头。
        ///
        /// **必须在这里转，不能在调用方 OpenPopupShell 之前转** —— 这个方法
        /// 开头就会 `ClosePopup()`（重复开弹层时先关旧的），那一句会把箭头
        /// 转回 0°。先转再进来的话，动画刚起步就被反向覆盖，看着就是
        /// 「箭头闪了一下不动」。
        /// </summary>
        private void OpenPopupShell(RectTransform anchor, float popupWidth, float popupHeight,
            UiArrowSpin triggerArrow, Action<RectTransform> buildContent)
        {
            EnsurePopupLayer();
            ClosePopup();

            // `ClosePopup` 已经跑完了，现在把箭头转到「展开」位。
            if (triggerArrow != null)
            {
                openArrow = triggerArrow;
                triggerArrow.SpinTo(-90f, ArrowSpinSeconds);
            }

            // 全屏挡板：点外面收起。
            RectTransform blocker = CreateRect("Blocker", popupLayer, 0f, 0f, 0f, 0f);
            Image blockerImage = blocker.gameObject.AddComponent<Image>();
            blockerImage.color = new Color(0f, 0f, 0f, 0.001f);
            blockerImage.raycastTarget = true;
            Button blockerButton = blocker.gameObject.AddComponent<Button>();
            blockerButton.targetGraphic = blockerImage;
            blockerButton.transition = Selectable.Transition.None;
            blockerButton.onClick.AddListener(ClosePopup);
            Stretch(blocker, 0f, 0f, 0f, 0f);
            popupBlocker = blocker;

            RectTransform host = CreateRect("Popup", popupLayer, 0f, 0f,
                popupWidth, popupHeight);
            Image background = host.gameObject.AddComponent<Image>();
            background.color = UiTheme.Current.SurfaceTopBand;
            background.raycastTarget = true;
            // 一圈极淡描边：弹层压在面板上，没有边界时读不出是浮起来的。
            Outline outline = host.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.16f);
            outline.effectDistance = new Vector2(1f, 1f);
            popupHost = host;

            // 弹层坐标：`ScreenPointToLocalPointInRectangle` 给的是**以中心为原点**
            // 的局部坐标，而子节点用的是「左上角为锚」，所以要把半个尺寸加回去。
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, anchor.position);
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                popupLayer, screen, null, out local);
            float left = local.x + popupLayer.rect.width * 0.5f - popupWidth * 0.5f;
            float top = popupLayer.rect.height * 0.5f - local.y + 4f;
            SetTopLeft(host, left, top, popupWidth, popupHeight);

            // 一行取证：弹层出问题时先看它到底建在哪个画布、落到哪个坐标。
            // （用户报过“下拉列表里看不见内容”，有这行就能分清是没建出来、
            //  建到屏幕外、还是被更高的一层盖住。）
            Main.Logger.Log("[弹层] " + anchor.name + " 层=" + popupLayer.name
                + " 层排序=" + popupLayer.GetComponentInParent<Canvas>().sortingOrder
                + " 层尺寸=" + popupLayer.rect.width + "x" + popupLayer.rect.height
                + " 弹层=" + left.ToString("0") + "," + top.ToString("0")
                + " " + popupWidth.ToString("0") + "x" + popupHeight.ToString("0"));

            buildContent(host);
        }

        private void OpenChoicePopup(RectTransform anchor, string[] options, Action<int> pick)
        {
            OpenChoicePopup(anchor, options, null, pick);
        }

        private void OpenChoicePopup(RectTransform anchor, string[] options,
            UiArrowSpin arrow, Action<int> pick)
        {
            float width = 140f;
            float rowHeight = 24f;
            float height = options.Length * rowHeight + 8f;
            OpenPopupShell(anchor, width, height, arrow, delegate(RectTransform host)
            {
                for (int i = 0; i < options.Length; i++)
                {
                    int choice = i;
                    Button button = CreateButton(host, 4f, 4f + i * rowHeight,
                        width - 8f, rowHeight, options[i], delegate
                        {
                            ClosePopup();
                            pick(choice);
                        });
                }
            });
        }

        /// <summary>高级滤镜的「滤镜」属性（值是类名字符串）。</summary>
        private bool IsAdvancedFilterProperty(ADOFAI.PropertyInfo info, string key)
        {
            return info != null && string.Equals(key, "filter", StringComparison.Ordinal)
                && target != null && target.eventType == LevelEventType.SetFilterAdvanced;
        }

        /// <summary>
        /// 高级滤镜的可选项。**完全照原生那套规则来**（`PropertiesPanel` 的
        /// `case "filter"` 分支）：
        ///
        /// 1. 取 `Assembly-CSharp-firstpass` 里所有 `CameraFilterPack_*`；
        /// 2. 跳过 `ffxSetFilterAdvancedPlus.blacklistedFilterKeywords` 里命中的；
        /// 3. 显示名 = `RDString.GetWithCheck("editor.CameraFilterPack." + 去掉前缀)`
        ///    —— 这就是原版中文名的来源；查不到才退回"拆驼峰"的英文。
        ///
        /// 值仍然写**完整类名**，和原生写进谱面的值一致。
        /// </summary>
        private static string[] filterDisplays;
        private static string[] filterValues;

        private static void EnsureFilterOptions()
        {
            if (filterValues != null)
            {
                return;
            }
            List<string> values = new List<string>();
            List<string> displays = new List<string>();
            List<string> blacklist = FilterBlacklist();
            try
            {
                System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    System.Reflection.Assembly assembly = assemblies[i];
                    if (assembly == null
                        || assembly.GetName().Name != "Assembly-CSharp-firstpass")
                    {
                        continue;
                    }
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    for (int t = 0; t < types.Length; t++)
                    {
                        Type type = types[t];
                        if (type == null || !type.Name.StartsWith("CameraFilterPack_",
                            StringComparison.Ordinal))
                        {
                            continue;
                        }
                        string full = type.Name;
                        bool blacklisted = false;
                        for (int b = 0; b < blacklist.Count; b++)
                        {
                            string keyword = blacklist[b];
                            if (!string.IsNullOrEmpty(keyword) && full.Contains(keyword))
                            {
                                blacklisted = true;
                                break;
                            }
                        }
                        if (blacklisted)
                        {
                            continue;
                        }
                        string shortName = full.Substring("CameraFilterPack_".Length);
                        bool exists;
                        string localized = RDString.GetWithCheck(
                            "editor.CameraFilterPack." + shortName, out exists);
                        string display = exists && !string.IsNullOrEmpty(localized)
                            ? localized
                            : PrettifyFilterName(shortName);
                        values.Add(full);
                        displays.Add(display);
                    }
                    break;
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[参数栏] 列举滤镜类型失败：" + exception.Message);
            }
            filterValues = values.ToArray();
            filterDisplays = displays.ToArray();
        }

        /// <summary>原生那份"不许出现"的关键字表（拿不到就当空表）。</summary>
        private static List<string> FilterBlacklist()
        {
            List<string> result = new List<string>();
            try
            {
                System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type holder = assemblies[i] == null ? null
                        : assemblies[i].GetType("ffxSetFilterAdvancedPlus", false);
                    if (holder == null)
                    {
                        continue;
                    }
                    System.Reflection.FieldInfo field = holder.GetField(
                        "blacklistedFilterKeywords",
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Static);
                    System.Collections.IEnumerable source = field == null ? null
                        : field.GetValue(null) as System.Collections.IEnumerable;
                    if (source == null)
                    {
                        break;
                    }
                    foreach (object item in source)
                    {
                        string keyword = item as string;
                        if (!string.IsNullOrEmpty(keyword))
                        {
                            result.Add(keyword);
                        }
                    }
                    break;
                }
            }
            catch (Exception)
            {
                // 拿不到就不过滤 —— 宁可多几项，也别把列表清空。
            }
            return result;
        }

        /// <summary>查不到本地化时把 `AAA_SuperComputer2` 拆成 `AAA Super Computer 2`。</summary>
        private static string PrettifyFilterName(string raw)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '_')
                {
                    builder.Append(' ');
                    continue;
                }
                bool previousLower = i > 0 && char.IsLower(raw[i - 1]);
                bool previousDigit = i > 0 && char.IsDigit(raw[i - 1]);
                if (i > 0
                    && ((char.IsUpper(c) && (previousLower || previousDigit))
                        || (char.IsDigit(c) && !previousDigit)))
                {
                    builder.Append(' ');
                }
                builder.Append(c);
            }
            return builder.ToString().Trim();
        }

        /// <summary>
        /// 完整类名 → 原版中文名（时间轴上的高级滤镜块名也用它，别再各写一套）。
        /// </summary>
        internal static string FilterDisplayFor(string value)
        {
            EnsureFilterOptions();
            for (int i = 0; i < filterValues.Length; i++)
            {
                if (string.Equals(filterValues[i], value, StringComparison.Ordinal))
                {
                    return filterDisplays[i];
                }
            }
            return string.IsNullOrEmpty(value) ? string.Empty : value;
        }

        private void BuildFilterDropdown(RectTransform host, float valueWidth,
            ADOFAI.PropertyInfo info, string key, Row row)
        {
            string current = EditorPropertyPanel.SafeGet<string>(target, info, null)
                ?? string.Empty;
            RectTransform rect = CreateRect("FilterValue", host, 0f, 0f, valueWidth,
                ControlHeight);
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;

            TextMeshProUGUI label = CreateText("Value", rect, 2f, 0f,
                Mathf.Max(20f, valueWidth - 20f), ControlHeight,
                FilterDisplayFor(current), 13f,
                TextPrimary, TextAlignmentOptions.MidlineLeft);
            label.raycastTarget = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            // **必须记进 row。**
            //
            // RefreshValues 是按行的 `EnumLabel` 来回填下拉显示的；这一行原来
            // 建了个局部 label 就没管了，于是选完滤镜值写进了谱面、框上却还
            // 显示旧名字 —— 用户看到的就是「点了，没反应」。
            // 存的这一位让 RefreshValues 能刷到它（选中的高亮也靠它）。
            row.EnumLabel = label;
            // 高级滤镜也是「枚举类」标签：`RefreshValues` 那段按 `FilterDisplayFor`
            // 回填显示名，正是这里需要的。
            row.EnumLabelIsEnum = true;

            UiArrowSpin filterArrow = CreateDropArrow(rect,
                Mathf.Max(0f, valueWidth - 24f), 0f, 22f, ControlHeight);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                EnsureFilterOptions();
                OpenStringListPopup(rect, info, key, filterDisplays, filterValues, filterArrow);
            });

            // 和值控件一样受"禁用"闸控制：属性被勾掉时点不开。
            CanvasGroup group = rect.gameObject.AddComponent<CanvasGroup>();
            group.interactable = row.ValueGroup == null || row.ValueGroup.interactable;
            if (row.ValueGroup != null)
            {
                group.alpha = row.ValueGroup.alpha;
            }
        }

        private void OpenEnumPopup(RectTransform anchor, ADOFAI.PropertyInfo info, string key,
            UiArrowSpin arrow)
        {
            OpenOptionListPopup(anchor, info, key, Enum.GetNames(info.enumType), false,
                null, arrow);
        }

        /// <summary>
        /// 字符串下拉：值是**类名字符串**的那种（高级滤镜有几百项）。
        /// 和枚举下拉共用一个实现，差别只在"当前值怎么读、选中怎么提交"。
        /// </summary>
        private void OpenStringListPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, string[] names, string[] values, UiArrowSpin arrow)
        {
            OpenOptionListPopup(anchor, info, key, names, true, values, arrow);
        }

        private void OpenOptionListPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, string[] names, bool stringValue, string[] values)
        {
            OpenOptionListPopup(anchor, info, key, names, stringValue, values, null);
        }

        private void OpenOptionListPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, string[] names, bool stringValue, string[] values, UiArrowSpin arrow)
        {
            // 缓速枚举每个选项前面有一段缩略曲线，宽度要把它算进去。
            bool easeEnum = !stringValue && EditorPropertyPanel.IsEaseEnum(info);
            float graphWidth = easeEnum ? EditorPropertyPanel.OptionGraphWidth : 0f;

            // **只有缓速和高级滤镜带搜索与收藏。**
            //
            // 别的枚举（TrackStyle 那种两三项的）加上这两样只会更难点：
            // 搜索框占掉一行高度，收藏只有一个「收藏了唯一一项」的用处。
            // 判据和下面「收藏存哪份」用的是同一个，别写两遍。
            bool richList = easeEnum || IsAdvancedFilterProperty(info, key);
            // 收藏种类的判据：缓速走 FavoriteEases，其余 richList（也就是滤镜）走 FavoriteFilters。
            bool favoriteKind = !easeEnum;

            float starColumn = richList ? StarColumnWidth : 0f;
            float searchArea = richList ? SearchAreaHeight : 0f;

            // 宽度按**最长选项**量：缓速那类名字（"OutExpo" 等）比枚举名长得多，
            // 按枚举名算宽度会把选项截成半截。
            float longest = 0f;
            for (int i = 0; i < names.Length; i++)
            {
                longest = Mathf.Max(longest, EditorPropertyPanel.DisplayEnumName(
                    target, info, names[i]).Length * 12f);
            }
            float width = Mathf.Max(160f + starColumn, graphWidth + longest + 32f + starColumn);
            // **卡片之间留 2px 缝。** 紧贴的圆角块读起来像一整块被切开的格子，
            // 留一道细缝才看得出是一张张独立的卡片。缝隙随卡片一起算进
            // 行高（见 `OptionCardGap`），列表总高也按它算。
            float rowHeight = OptionCardHeight + OptionCardGap;
            float height = Mathf.Min(richList ? 400f : 320f,
                names.Length * rowHeight + searchArea + 8f);
            OpenPopupShell(anchor, width, height, arrow, delegate(RectTransform host)
            {
                float listTop = 4f + searchArea;
                float listHeight = height - listTop - 4f;

                RectTransform viewportHost = CreateRect("View", host, 4f, listTop,
                    width - 8f, listHeight);
                Image viewBackground = viewportHost.gameObject.AddComponent<Image>();
                viewBackground.color = Color.clear;
                viewBackground.raycastTarget = true;
                viewportHost.gameObject.AddComponent<RectMask2D>();
                ScrollRect scrollRect = viewportHost.gameObject.AddComponent<ScrollRect>();
                scrollRect.horizontal = false;
                scrollRect.vertical = true;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
                scrollRect.scrollSensitivity = 24f;

                RectTransform listContent = CreateRect("Content", viewportHost,
                    0f, 0f, width - 8f, names.Length * rowHeight);
                listContent.anchorMin = new Vector2(0f, 1f);
                listContent.anchorMax = new Vector2(1f, 1f);
                listContent.pivot = new Vector2(0.5f, 1f);
                // **横向拉伸时 sizeDelta.x 必须是 0**，否则实际宽度 = 视口宽 + 内容宽，
                // 内容整体左移半个视口，文字全被 RectMask2D 裁在左边外面 ——
                // 表现就是「下拉列表空白」。
                listContent.sizeDelta = new Vector2(0f, names.Length * rowHeight);
                listContent.anchoredPosition = Vector2.zero;
                scrollRect.viewport = viewportHost;
                scrollRect.content = listContent;

                string current = stringValue
                    ? EditorPropertyPanel.SafeGet<string>(target, info, null)
                    : EditorPropertyPanel.ReadEnumName(target, info);

                // **列表要能整段重建**（搜索时边打字边过滤、点星标后要换图标
                // 并调顺序）。做成局部函数，第一次调用时建出全部行，之后每次
                // 清空重来 —— 行数是几十到几百，重建一次的开销远小于维护
                // 「哪一行该显示、该在第几位」这套增量逻辑的出错成本。
                Action<string> rebuild = null;
                rebuild = delegate(string filter)
                {
                    for (int i = listContent.childCount - 1; i >= 0; i--)
                    {
                        Destroy(listContent.GetChild(i).gameObject);
                    }

                    string trimmed = filter == null ? string.Empty : filter.Trim();
                    bool searching = trimmed.Length > 0;

                    // 过滤结果：先攒成条目，再决定顺序。
                    // 结构是 (显示名, 写入值, 是否收藏)。
                    List<OptionEntry> visible = new List<OptionEntry>();
                    for (int i = 0; i < names.Length; i++)
                    {
                        string name = names[i];
                        string display = EditorPropertyPanel.DisplayEnumName(target, info, name);
                        string rawValue = values != null && i < values.Length ? values[i] : name;
                        if (searching
                            && display.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0
                            && rawValue.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        visible.Add(new OptionEntry
                        {
                            Display = display,
                            Value = rawValue,
                            EnumName = name,
                            Favorite = richList && EditorFavoriteStore.Contains(favoriteKind, rawValue)
                        });
                    }

                    // **搜索时不留收藏分组。** 搜「out」的时候把收藏项继续压在最上面，
                    // 会让人以为搜索没生效。不搜索时才把收藏提到前面。
                    if (!searching)
                    {
                        visible.Sort(delegate(OptionEntry a, OptionEntry b)
                        {
                            if (a.Favorite == b.Favorite)
                            {
                                return 0;
                            }
                            return a.Favorite ? -1 : 1;
                        });
                    }

                    float contentHeight = visible.Count * rowHeight;
                    listContent.sizeDelta = new Vector2(0f, contentHeight);

                    for (int i = 0; i < visible.Count; i++)
                    {
                        OptionEntry entry = visible[i];
                        // 显示名与写入值分开：滤镜下拉显示中文，但谱面里要写类名。
                        //
                        // **比的是写入值，不是显示名。**
                        //
                        // 滤镜那一路 `name` 是中文显示名、`current` 是谱面里的类名，
                        // 直接比永远不等 —— 于是下拉里一项都不会高亮，用户分不清
                        // 当前选的是哪个。
                        bool selected = string.Equals(entry.Value, current, StringComparison.Ordinal);
                        float cardWidth = width - 8f;
                        float buttonWidth = cardWidth - starColumn;
                        float cardY = i * rowHeight;

                        // **卡片先建、横跨整行。**
                        //
                        // 卡片和按钮是两个独立的兄弟节点，不是父子 —— 卡片要包住
                        // 右边的星标，而按钮的可点区域只到星标左边（星标自己是个
                        // 按钮，压在一起会抢事件）。做成父子的话，卡片只能是按钮
                        // 的宽度，永远包不住星标；把星标塞进按钮里又会变成
                        // 「点星也顺带选中这一项」。
                        PaintOptionCard(listContent, 0f, cardY, cardWidth, selected);

                        Button button = CreateButton(listContent, 0f, cardY,
                            buttonWidth, OptionCardHeight, entry.Display, delegate
                            {
                                ClosePopup();
                                if (stringValue)
                                {
                                    // 显示名可能是中文，写回谱面的必须是原始值。
                                    CommitValue(target, key, entry.Value);
                                    return;
                                }
                                object parsed = EditorPropertyPanel.ParseEnumValue(info, entry.EnumName);
                                if (parsed != null)
                                {
                                    CommitValue(target, key, parsed);
                                }
                            });
                        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
                        if (label != null)
                        {
                            label.color = selected ? Accent : TextPrimary;
                            label.alignment = TextAlignmentOptions.MidlineLeft;
                            float labelX = easeEnum
                                ? EditorPropertyPanel.OptionGraphX + graphWidth + 8f : 8f;
                            SetTopLeft(label.rectTransform, labelX, 0f,
                                Mathf.Max(20f, buttonWidth - labelX - 12f), OptionCardHeight);
                        }

                        if (easeEnum)
                        {
                            DG.Tweening.Ease ease;
                            if (EditorPropertyPanel.TryParseEase(entry.EnumName, out ease))
                            {
                                AddEaseGraph(button.GetComponent<RectTransform>(), ease);
                            }
                        }

                        if (richList)
                        {
                            BuildStarButton(listContent, width - 4f - StarColumnWidth,
                                cardY, entry.Value, favoriteKind, rebuild);
                        }
                    }
                };

                if (richList)
                {
                    // **「搜索」两个字固定在左边，输入区只有一条下划线。**
                    //
                    // 原来是整行一个带底线的输入框，读起来像参数栏里的数字框；
                    // 这里要的是「一个能检索的框」，把标签和输入区分开之后，
                    // 一眼就知道这行是干什么的，也不用靠占位符（那个一打字就没了）。
                    CreateText("SearchLabel", host, 6f, 4f, SearchLabelWidth,
                        SearchFieldHeight, "搜索", 12f, TextMuted,
                        TextAlignmentOptions.MidlineLeft).raycastTarget = false;

                    // **不用 `onEndEdit`。** 那是回车或失焦才触发的，
                    // 而搜索要边打字边过滤。`onValueChanged` 每敲一个字符都来一次。
                    float fieldX = 6f + SearchLabelWidth;
                    TMP_InputField search = CreateField(host, fieldX,
                        width - fieldX - 8f, string.Empty, null);
                    RectTransform fieldRect = search.GetComponent<RectTransform>();
                    SetTopLeft(fieldRect, fieldX, 4f, width - fieldX - 8f, SearchFieldHeight);
                    // `CreateField` 自带的底线是贴在**它自己那个 rect 的底部**的，
                    // 而那个 rect 已经被我们挪过位置 —— 挪完之后底线还跟着走，
                    // 正好落在「搜索」两个字的基线上，所以不用另外画一条。
                    search.onValueChanged.AddListener(delegate(string text) { rebuild(text); });
                }

                rebuild(string.Empty);
            });
        }

        /// <summary>搜索区总高：输入行 + 上下留白。</summary>
        private const float SearchAreaHeight = 34f;
        /// <summary>搜索输入行的行高（「搜索」两个字和下划线都在这一行里）。</summary>
        private const float SearchFieldHeight = 24f;
        /// <summary>「搜索」两个字占的宽度，输入区从它右边起。</summary>
        private const float SearchLabelWidth = 40f;
        /// <summary>收藏星标那一列的宽度（含左右留白）。</summary>
        private const float StarColumnWidth = 26f;
        /// <summary>卡片高度（不含间隙）。</summary>
        private const float OptionCardHeight = 26f;
        /// <summary>卡片之间的缝。取 2 是有意的：再大列表会散，再小圆角挤在一起看不清。</summary>
        private const float OptionCardGap = 2f;
        /// <summary>卡片圆角半径。</summary>
        private const float OptionCardRadius = 5f;
        /// <summary>
        /// 箭头旋转的时长。
        ///
        /// 0.22 秒 —— 比 UI 过渡的常规 0.3 略快一点：箭头是个小零件的状态提示，
        /// 拖得太久会显得迟钝。再快（< 0.15）就看不出弹的劲了。
        /// </summary>
        private const float ArrowSpinSeconds = 0.22f;

        /// <summary>
        /// 给一行选项画卡片底：圆角矩形 + 极淡填充，选中项再加描边。
        ///
        /// **卡片是列表内容的独立子节点，不是行按钮的子节点。** 它要横跨整行、
        /// 把右边的收藏星标一起包住；而行按钮的可点区只到星标左边（星标自己
        /// 是个按钮）。做成父子的话卡片只能是按钮的宽度，永远包不住星标。
        ///
        /// 描边用 `Outline` 而不是再叠一张 `frame` 圆角图：`GetRoundedSprite`
        /// 的描边版本是按固定像素宽画在纹理里的，缩放到 26 高时会糊；
        /// `Outline` 是 uGUI 自带的描边效果，跟缩放无关。
        /// </summary>
        private static void PaintOptionCard(RectTransform parent, float x, float y,
            float width, bool selected)
        {
            Image card = CreateImage("Card", parent, x, y, width, OptionCardHeight,
                selected ? UiTheme.Current.SurfaceBand : OptionCardFill);
            card.sprite = EditorStudioOverlay.GetRoundedSprite(
                20, OptionCardRadius, false, 0f);
            card.type = Image.Type.Sliced;
            card.raycastTarget = false;
            // **不用 `SetAsFirstSibling`。** 每行都把自己的卡片挤到最前面的话，
            // 后建的卡片会压住先建的那些行 —— 列表看上去就是一叠错位的纸。
            // 同一行的相对顺序由建节点的先后决定：卡片先建、按钮其次、星标最后，
            // 而 `SetAsFirstSibling` 在这里没有「本行内」的概念。

            if (!selected)
            {
                // 未选中的卡片不描边：几十张都描边的话，列表会变成一片格子。
                return;
            }

            Outline outline = card.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.22f);
            outline.effectDistance = new Vector2(1f, 1f);
        }

        /// <summary>下拉里一张普通卡片的填充。比弹层底色亮一档，才看得出是卡片。</summary>
        private static Color OptionCardFill
        {
            get { return new Color(1f, 1f, 1f, 0.05f); }
        }

        private struct OptionEntry
        {
            internal string Display;
            internal string Value;
            internal string EnumName;
            internal bool Favorite;
        }

        /// <summary>
        /// 一行右侧的收藏星标。点它**只切收藏**，不关弹窗、不改谱面。
        ///
        /// 它是叠在行按钮上的独立 `Button`：uGUI 的 `onClick` 不会从子控件
        /// 冒泡到父控件，所以点星不会顺带触发那一行的「选中并提交」。
        /// </summary>
        private void BuildStarButton(RectTransform parent, float x, float y,
            string value, bool favoriteKind, Action<string> rebuild)
        {
            RectTransform rect = CreateRect("Star", parent, x, y,
                StarColumnWidth, OptionCardHeight);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;

            bool filled = EditorFavoriteStore.Contains(favoriteKind, value);
            Image icon = CreateImage("Icon", rect, 0f, 0f, 0f, 0f,
                filled ? Accent : TextMuted);
            icon.sprite = UiStarIcon.Get(filled);
            icon.type = Image.Type.Simple;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            const float iconSize = 16f;
            SetTopLeft(icon.rectTransform, (StarColumnWidth - iconSize) * 0.5f,
                (OptionCardHeight - iconSize) * 0.5f, iconSize, iconSize);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            string captured = value;
            button.onClick.AddListener(delegate
            {
                EditorFavoriteStore.Toggle(favoriteKind, captured);
                // 重建整个列表而不是只换这一行的图标：收藏项要挪位置。
                if (rebuild != null)
                {
                    rebuild(null);
                }
            });
        }

        private void OpenColorPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, Row row)
        {
            Color initial = EditorPropertyPanel.SafeGetColor(target, info);
            OpenPopupShell(anchor, EditorColorPicker.BodyWidth, EditorColorPicker.BodyHeight,
                delegate(RectTransform host)
                {
                    colorPicker = host.gameObject.AddComponent<EditorColorPicker>();
                    colorPicker.Build(host, font, initial,
                        delegate(Color preview)
                        {
                            // 拖动途中只改色块，**不写谱面**（每帧写会重建装饰物）。
                            if (row.Swatch != null)
                            {
                                row.Swatch.color = preview;
                            }
                        },
                        delegate(Color picked)
                        {
                            CommitValue(target, key, EditorPropertyPanel.ColorToHex(picked));
                            if (row.Swatch != null)
                            {
                                row.Swatch.color = picked;
                            }
                        });
                });
        }

        private void OpenColorPicker(RectTransform anchor, Color initial, Action<Color> commit)
        {
            OpenPopupShell(anchor, EditorColorPicker.BodyWidth, EditorColorPicker.BodyHeight,
                delegate(RectTransform host)
                {
                    colorPicker = host.gameObject.AddComponent<EditorColorPicker>();
                    colorPicker.Build(host, font, initial,
                        delegate(Color preview)
                        {
                            // 拖动途中只改色块，不写谱面。
                            if (colorPicker != null)
                            {
                                colorPicker.SetColor(preview, false);
                            }
                        },
                        commit);
                });
        }

        /// <summary>
        /// 下拉选项左侧那条缩略曲线：和原版 / 旧参数栏同一套画法 ——
        /// `UILineRenderer`（游戏第一方 UI Extensions）+ 逐点采样。
        ///
        /// 轴心必须是**左下角**：UILineRenderer 把 Points 当作相对左下角的坐标。
        /// </summary>
        private static void AddEaseGraph(RectTransform option, DG.Tweening.Ease ease)
        {
            const float rowHeight = 26f;
            float height = EditorPropertyPanel.EaseGraphHeight;
            float top = (rowHeight - height) * 0.5f;

            RectTransform graph = CreateRect("Graph", option,
                EditorPropertyPanel.OptionGraphX, top,
                EditorPropertyPanel.OptionGraphWidth, height);
            graph.anchorMin = new Vector2(0f, 1f);
            graph.anchorMax = new Vector2(0f, 1f);
            graph.pivot = new Vector2(0f, 0f);
            graph.anchoredPosition = new Vector2(
                EditorPropertyPanel.OptionGraphX, -(top + height));
            graph.sizeDelta = new Vector2(EditorPropertyPanel.OptionGraphWidth, height);

            UnityEngine.UI.Extensions.UILineRenderer line =
                graph.gameObject.AddComponent<UnityEngine.UI.Extensions.UILineRenderer>();
            line.color = TextSecondary;
            line.LineThickness = 1f;
            line.LineCaps = false;
            line.LineJoins = UnityEngine.UI.Extensions.UILineRenderer.JoinType.Bevel;
            line.BezierMode = UnityEngine.UI.Extensions.UILineRenderer.BezierType.None;
            line.RelativeSize = false;
            line.Points = EditorPropertyPanel.SampleEaseCurve(ease,
                EditorPropertyPanel.EaseGraphPoints,
                EditorPropertyPanel.OptionGraphWidth, height);
        }

        /// <summary>
        /// 下拉按钮上那枚三角箭头。
        ///
        /// 以前是三处各自 `CreateText(..., "▾", ...)`。工作台用的字体里没有
        /// 这个字形，打出来是个豆腐块；而且它没法做旋转 —— 字符只能整串换掉，
        /// 中间没有过渡。现在统一走这张程序化纹理，配合 `UiArrowSpin` 转。
        ///
        /// **pivot 必须是 (0.5, 0.5)。** 这里的节点是用 `SetTopLeft` 摆的，
        /// 那个函数会把 pivot 设成左上角；绕左上角转，视觉上是绕着角甩出去，
        /// 不是绕中心转。所以摆完位置之后要把 pivot 改回中心，再把
        /// `anchoredPosition` 补回去（pivot 变了，同一个左上角对应的
        /// anchoredPosition 就不一样了）。
        /// </summary>
        private static UiArrowSpin CreateDropArrow(Transform parent, float x, float y,
            float width, float height)
        {
            // **22px，不是字符时代的 11。**
            //
            // `▾` 那个字符在 11 号字里是满高的；换成图之后，纹理里只有中间
            // 一段是三角（上下要留白给旋转），所以同样写 11 实际可见的
            // 只有 ~6.8px —— 比原来还小。
            //
            // 22px × 纹理里 55% 的三角高度 ≈ 12px 可见高度。行高是 26，
            // 上下各留 2px，再大就要顶出去了。
            const float size = 22f;
            float left = x + (width - size) * 0.5f;
            float top = y + (height - size) * 0.5f;

            Image arrow = CreateImage("Arrow", parent, left, top, size, size, TextMuted);
            arrow.sprite = UiTriangleIcon.Get();
            arrow.type = Image.Type.Simple;
            arrow.preserveAspect = true;
            arrow.raycastTarget = false;

            // 把 pivot 从左上角挪到中心，并补偿位置 ——
            // pivot=(0,1) 时 anchoredPosition 指的是左上角；
            // 换成 (0.5,0.5) 之后同一个 anchoredPosition 指的是中心，
            // 所以要把中心点算出来再填进去。
            RectTransform rect = arrow.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(left + size * 0.5f, -(top + size * 0.5f));
            // **直接把旋转组件带回去，不靠节点名回查。**
            // `rect.Find("Arrow")` 那种写法看着能用，但它把节点名变成了
            // 隐式契约 —— 谁改个名，动画就静默没了，而且没有任何报错。
            return UiArrowSpin.Attach(rect);
        }

        private void ClosePopup()
        {
            // **箭头先转回去。** 关闭路径有三条（点外面 / 选中一项 / 再开一个），
            // 全都会走到这里，所以收口放在这一处。
            if (openArrow != null)
            {
                openArrow.SpinTo(0f, ArrowSpinSeconds);
                openArrow = null;
            }
            if (popupHost != null)
            {
                Destroy(popupHost.gameObject);
                popupHost = null;
            }
            if (popupBlocker != null)
            {
                Destroy(popupBlocker.gameObject);
                popupBlocker = null;
            }
            colorPicker = null;
        }

        // ── UI 辅助 ───────────────────────────────────────────────────────

        private TMP_InputField CreateField(RectTransform parent, float x, float width,
            string value, UnityEngine.Events.UnityAction<string> onEndEdit)
        {
            RectTransform rect = CreateRect("Field", parent, x, 0f, width, ControlHeight);
            rect.gameObject.SetActive(false);

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;

            // 底线：**2px、白 28%**。1px 的 8% 白在 1080p 上几乎看不见，
            // 用户反馈“线条太细”，所以加粗一档、提亮一档。
            Image underline = CreateImage("Underline", rect, 0f, 0f, 0f, 2f,
                new Color(1f, 1f, 1f, 0.28f));
            underline.raycastTarget = false;
            underline.rectTransform.anchorMin = new Vector2(0f, 0f);
            underline.rectTransform.anchorMax = new Vector2(1f, 0f);
            underline.rectTransform.pivot = new Vector2(0.5f, 0f);
            underline.rectTransform.offsetMin = new Vector2(0f, 0f);
            underline.rectTransform.offsetMax = new Vector2(0f, 2f);

            TextMeshProUGUI text = CreateStretchedText("Text", rect, 5f,
                value ?? string.Empty, 12f, TextPrimary);
            text.overflowMode = TextOverflowModes.Ellipsis;

            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.richText = false;
            field.characterLimit = 256;
            field.customCaretColor = true;
            field.caretColor = TextPrimary;
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.textViewport = rect;
            field.textComponent = text;
            if (onEndEdit != null)
            {
                field.onEndEdit.AddListener(onEndEdit);
            }

            // **必须显式写一次 `field.text`。** TMP_InputField 初始化时会把
            // 自己的 text（默认空串）同步到 textComponent，光在 CreateStretchedText
            // 里给初值会被它清掉 —— 表现就是「一行里只有一个框有字」。
            field.text = value ?? string.Empty;

            rect.gameObject.SetActive(true);
            return field;
        }

        private Button CreateButton(RectTransform parent, float x, float y,
            float width, float height, string label, Action action)
        {
            RectTransform rect = CreateRect("Button", parent, x, y, width, height);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;

            Image hover = CreateImage("Hover", rect, 0f, 0f, 0f, 0f, Color.clear);
            hover.raycastTarget = false;
            Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);
            HoverFill fill = rect.gameObject.AddComponent<HoverFill>();
            fill.Configure(hover, HoverColor);

            CreateText("Label", rect, 0f, 0f, width, height, label,
                12f, TextSecondary, TextAlignmentOptions.Center).raycastTarget = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { action(); });
            return button;
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

        private sealed class HoverFill : MonoBehaviour,
            UnityEngine.EventSystems.IPointerEnterHandler,
            UnityEngine.EventSystems.IPointerExitHandler
        {
            private Image fill;
            private Color color = new Color(1f, 1f, 1f, 0.08f);

            internal void Configure(Image image, Color hoverColor)
            {
                fill = image;
                color = hoverColor;
            }

            public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
            {
                if (fill != null)
                {
                    fill.color = color;
                }
            }

            public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
            {
                if (fill != null)
                {
                    fill.color = Color.clear;
                }
            }
        }

        private static void ClearChildren(RectTransform host)
        {
            if (host == null)
            {
                return;
            }
            for (int i = host.childCount - 1; i >= 0; i--)
            {
                GameObject child = host.GetChild(i).gameObject;
                // Destroy 是延迟到帧末的：先关掉，免得这一帧新旧两套控件叠着画。
                child.SetActive(false);
                Destroy(child);
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
    }
}
