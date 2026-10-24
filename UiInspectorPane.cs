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

        // 弹层（枚举 / 调色盘）共用一块 32000 画布
        private RectTransform popupLayer;
        private RectTransform popupHost;
        private RectTransform popupBlocker;
        private EditorColorPicker colorPicker;

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
            internal Image Swatch;
            internal TextMeshProUGUI SwatchHex;
            internal bool Refreshing;
        }

        // ── 对外 ─────────────────────────────────────────────────────────

        /// <summary>设置页模式：只看一页设置，不跟时间轴选中、不画预览与生效开关。</summary>
        internal void SetSettingsMode(bool value)
        {
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
        }

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
                string page = overlay.SelectedSettingsTitle;
                if (!string.Equals(page, lastPage, StringComparison.Ordinal))
                {
                    lastPage = page;
                    if (pageTitle != null)
                    {
                        pageTitle.text = page;
                    }
                }
                LevelEvent settingsTarget = overlay.SelectedSettingsEvent;
                if (!ReferenceEquals(settingsTarget, target))
                {
                    target = settingsTarget;
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
            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in target.info.propertiesInfo)
            {
                ADOFAI.PropertyInfo info = pair.Value;
                if (info == null || string.IsNullOrEmpty(info.name))
                {
                    continue;
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

        // ── 重建 ─────────────────────────────────────────────────────────

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
            CreateText("Caret", rect, width - 16f, 0f, 12f, ControlHeight,
                "▾", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                OpenEnumPopup(rect, info, key);
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
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Tuple<float, float> current =
                            EditorPropertyPanel.SafeGet<Tuple<float, float>>(
                                target, info, null)
                            ?? new Tuple<float, float>(0f, 0f);
                        CommitValue(target, key,
                            new Tuple<float, float>(parsed, current.Item2));
                    }
                });
            row.Field2 = CreateField(host, half + PairGap, half,
                FieldNumber(high),
                delegate(string text)
                {
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Tuple<float, float> current =
                            EditorPropertyPanel.SafeGet<Tuple<float, float>>(
                                target, info, null)
                            ?? new Tuple<float, float>(0f, 0f);
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
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        // 另三格现读：`a`/`b` 是建行时的快照，连续改两格时
                        // 后面的会把前面刚写进去的值覆写回旧值。
                        Tuple<Vector2, Vector2> current =
                            EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                                target, info, null)
                            ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                        a = current.Item1;
                        b = current.Item2;
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
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Tuple<Vector2, Vector2> current =
                            EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                                target, info, null)
                            ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                        a = current.Item1;
                        b = current.Item2;
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
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Tuple<Vector2, Vector2> current =
                            EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                                target, info, null)
                            ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                        a = current.Item1;
                        b = current.Item2;
                        CommitValue(target, key, new Tuple<Vector2, Vector2>(
                            new Vector2(a.x, parsed), b));
                    }
                });
            CreateText("DashY", host, 18f + fieldWidth + 4f, 28f, 10f, ControlHeight,
                "-", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;
            CreateField(host, secondX, fieldWidth, EditorPropertyPanel.FormatFloat(b.y),
                delegate(string text)
                {
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        Tuple<Vector2, Vector2> current =
                            EditorPropertyPanel.SafeGet<Tuple<Vector2, Vector2>>(
                                target, info, null)
                            ?? new Tuple<Vector2, Vector2>(Vector2.zero, Vector2.zero);
                        a = current.Item1;
                        b = current.Item2;
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
            CreateText("Label", modeRect, 6f, 0f, modeWidth - 18f, ControlHeight,
                TileRelativeLabel(current.Item2), 12f, TextPrimary,
                TextAlignmentOptions.MidlineLeft).raycastTarget = false;
            CreateText("Caret", modeRect, modeWidth - 16f, 0f, 12f, ControlHeight,
                "▾", 11f, TextMuted, TextAlignmentOptions.Center).raycastTarget = false;
            Button modeButton = modeRect.gameObject.AddComponent<Button>();
            modeButton.targetGraphic = hit;
            modeButton.transition = Selectable.Transition.None;
            modeButton.onClick.AddListener(delegate
            {
                OpenChoicePopup(modeRect, new[] { "本格", "起点", "终点" },
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
                        break;
                    }
                }

                if (row.EnumLabel != null)
                {
                    string current = EditorPropertyPanel.ReadEnumName(target, info);
                    string display = EditorPropertyPanel.DisplayEnumName(target, info, current);
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
        /// 谱面里没写过的分量读出来是 NaN（`[null, null]`），`FormatFloat` 的约定
        /// 是把 NaN 写成空串。但在这个场景里空着会被读成“坏了/没加载出来”——
        /// 用户明确要求默认写 0，所以这里对 NaN 显示 0。
        /// **只是显示**：不编辑就不写回，数据仍然是 NaN。
        /// </summary>
        private static string FieldNumber(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }
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
            EnsurePopupLayer();
            ClosePopup();

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
            float width = 140f;
            float rowHeight = 24f;
            float height = options.Length * rowHeight + 8f;
            OpenPopupShell(anchor, width, height, delegate(RectTransform host)
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

            TextMeshProUGUI arrow = CreateText("Arrow", rect,
                Mathf.Max(0f, valueWidth - 16f), 0f, 14f, ControlHeight, "▾", 11f,
                TextMuted, TextAlignmentOptions.Center);
            arrow.raycastTarget = false;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                EnsureFilterOptions();
                OpenStringListPopup(rect, info, key, filterDisplays, filterValues);
            });

            // 和值控件一样受"禁用"闸控制：属性被勾掉时点不开。
            CanvasGroup group = rect.gameObject.AddComponent<CanvasGroup>();
            group.interactable = row.ValueGroup == null || row.ValueGroup.interactable;
            if (row.ValueGroup != null)
            {
                group.alpha = row.ValueGroup.alpha;
            }
        }

        private void OpenEnumPopup(RectTransform anchor, ADOFAI.PropertyInfo info, string key)
        {
            OpenOptionListPopup(anchor, info, key, Enum.GetNames(info.enumType), false);
        }

        /// <summary>
        /// 字符串下拉：值是**类名字符串**的那种（高级滤镜有几百项）。
        /// 和枚举下拉共用一个实现，差别只在"当前值怎么读、选中怎么提交"。
        /// </summary>
        private void OpenStringListPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, string[] names, string[] values)
        {
            OpenOptionListPopup(anchor, info, key, names, true, values);
        }

        private void OpenOptionListPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, string[] names, bool stringValue)
        {
            OpenOptionListPopup(anchor, info, key, names, stringValue, null);
        }

        private void OpenOptionListPopup(RectTransform anchor, ADOFAI.PropertyInfo info,
            string key, string[] names, bool stringValue, string[] values)
        {
            // 缓速枚举每个选项前面有一段缩略曲线，宽度要把它算进去。
            bool easeEnum = !stringValue && EditorPropertyPanel.IsEaseEnum(info);
            float graphWidth = easeEnum ? EditorPropertyPanel.OptionGraphWidth : 0f;
            // 宽度按**最长选项**量：缓速那类名字（"OutExpo" 等）比枚举名长得多，
            // 按枚举名算宽度会把选项截成半截。
            float longest = 0f;
            for (int i = 0; i < names.Length; i++)
            {
                longest = Mathf.Max(longest, EditorPropertyPanel.DisplayEnumName(
                    target, info, names[i]).Length * 12f);
            }
            float width = Mathf.Max(160f, graphWidth + longest + 32f);
            float rowHeight = 26f;
            float height = Mathf.Min(320f, names.Length * rowHeight + 8f);
            OpenPopupShell(anchor, width, height, delegate(RectTransform host)
            {
                // 枚举动辄三四十项（缓速），必须能滚 —— 只画放得下的那几项
                // 等于把后面的选项藏死。
                RectTransform viewportHost = CreateRect("View", host, 4f, 4f,
                    width - 8f, height - 8f);
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
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i];
                    // 显示名与写入值分开：滤镜下拉显示中文，但谱面里要写类名。
                    string rawValue = values != null && i < values.Length ? values[i] : name;
                    bool selected = string.Equals(name, current, StringComparison.Ordinal);
                    Button button = CreateButton(listContent, 0f, i * rowHeight,
                        width - 8f, rowHeight,
                        EditorPropertyPanel.DisplayEnumName(target, info, name),
                        delegate
                        {
                            ClosePopup();
                            if (stringValue)
                            {
                                // 显示名可能是中文，写回谱面的必须是原始值。
                                CommitValue(target, key, rawValue);
                                return;
                            }
                            object parsed = EditorPropertyPanel.ParseEnumValue(info, name);
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
                            Mathf.Max(20f, width - labelX - 12f), rowHeight);
                    }

                    if (easeEnum)
                    {
                        DG.Tweening.Ease ease;
                        if (EditorPropertyPanel.TryParseEase(name, out ease))
                        {
                            AddEaseGraph(button.GetComponent<RectTransform>(), ease);
                        }
                    }
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

        private void ClosePopup()
        {
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
