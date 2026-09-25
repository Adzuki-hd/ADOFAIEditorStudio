using System;
using System.Collections.Generic;
using System.Text;
using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 时间轴左侧的装饰物列表。仿原版编辑器左栏的那个列表：
    /// 列出当前谱面所有装饰物，能选中、单独隐藏/锁定、删除、批量隐藏/锁定，
    /// 以及按名字、tag、类型、所在格搜索。
    ///
    /// 它不做拖拽排序——那个功能要改 .adofai 文件里 decorations 数组的
    /// 顺序（游戏按这个顺序决定渲染层级），影响面远超一个列表该有的分量。
    ///
    /// **这个类不写任何文件，也不依赖 PACL2**。增删改一律把活交给
    /// scnEditor，由它自己的存档与撤销快照兜底；删除走工作台自己的
    /// 路径（见 DeleteRow 的注释），因为原版那条会把每个装饰物重建一遍。
    /// </summary>
    internal sealed class EditorDecorationList : MonoBehaviour
    {
        /// <summary>一行的高度。30 够放下图标、名字和三个小按钮。</summary>
        private const float RowHeight = 30f;
        /// <summary>行与行之间的缝。留一条，选中框才读得出是单独一行。</summary>
        private const float RowGap = 2f;
        private const float PanePadding = 8f;
        /// <summary>
        /// 固定头的高度：新建按钮行、搜索框、批量开关行。
        /// 这几行不参与滚动——列表滚到底时它们还得在，否则就没法换搜索词了。
        ///
        /// 这三行之外能省的都省了：类型筛选原来占一行，但搜索的
        /// `type:图片` 前缀做的是同一件事，留两个入口只会两头都占地方。
        /// 面板本身不高（跟时间轴走），每一行都得算着用。
        /// </summary>
        private const float HeaderHeight = 96f;

        private const float IconButtonSize = 18f;
        /// <summary>图标的点击区宽度。比图标大一圈，好点中。</summary>
        private const float IconHitSize = 22f;
        private const float EyeWidth = IconHitSize;
        private const float LockWidth = IconHitSize;
        private const float DeleteWidth = IconHitSize;

        private RectTransform root;
        private RectTransform rowViewport;
        private RectTransform rowContent;
        private ScrollRect rowScroll;
        private TMP_FontAsset font;
        private Sprite roundedSprite;
        private float paneWidth;

        private TMP_InputField searchField;
        private TextMeshProUGUI countLabel;

        private readonly List<DecoRow> rows = new List<DecoRow>();
        private readonly List<DecoEntry> entries = new List<DecoEntry>();
        private readonly List<DecoEntry> filtered = new List<DecoEntry>();

        private string searchText = string.Empty;

        private bool batchHidden;
        private bool batchLocked;

        private long dataFingerprint = long.MinValue;
        private int selectionFingerprint = int.MinValue;
        // 三种脏状态分开记，因为重建代价差得很远：装饰物表变了要遍历
        // 整个 decorations 并给每项取名字（最贵）；筛选变了只需重跑一遍
        // 过滤（中等）；只有位置或选中变了，连过滤都不用碰（最便宜）。
        // 滚动每帧都会走到 UpdateRows，三件事混成一个标志会让最贵的
        // 那条路被反复触发。
        private bool entriesDirty = true;
        private bool filterDirty = true;
        private bool rowsDirty = true;

        private Sprite eyeOpen;
        private Sprite eyeClosed;
        private Sprite lockOpen;
        private Sprite lockClosed;
        private Sprite deleteIcon;

        /// <summary>
        /// 行上出现过的图标。游戏的装饰物图标表（GCS.levelEventIcons）只按
        /// 事件类型给一张图，一个事件类型四个装饰物长得一模一样——
        /// 列表要靠名字区分它们，图标只用来提示类型。
        /// </summary>
        private static readonly LevelEventType[] DecoTypes =
        {
            LevelEventType.AddDecoration,
            LevelEventType.AddText,
            LevelEventType.AddParticle,
            LevelEventType.AddObject
        };

        private static readonly string[] DecoTypeNames = { "图片", "文本", "粒子", "对象" };

        // ---------------------------------------------------------------- 行数据

        /// <summary>
        /// 一行背后的数据快照。
        ///
        /// 名字和 tag 都是**从 LevelEvent 上抄下来的副本**，不是每帧回读
        /// 运行时对象：拖动装饰物时 EditorCommandHistory 会临时改写
        /// scrDecoration.decorationTag 去绑定关键帧（松手才还原），
        /// 回读运行时对象会在拖动期间读到假的 tag。
        /// </summary>
        private sealed class DecoEntry
        {
            internal LevelEvent Source;
            internal string DisplayName;
            internal string Tag;
            internal LevelEventType Type;
            internal int Floor;
            internal bool Visible;
            internal bool Locked;
        }

        private sealed class DecoRow
        {
            internal GameObject GameObject;
            internal RectTransform Rect;
            internal Image Background;
            internal Image TypeIcon;
            internal TextMeshProUGUI Name;
            internal TextMeshProUGUI Tag;
            internal Button Eye;
            internal Image EyeIcon;
            internal Button Lock;
            internal Image LockIcon;
            internal Button Delete;
            internal Image DeleteIcon;
            internal int BoundIndex = -1;
            /// <summary>鼠标是否正停在这一行上。只影响三个小图标的亮度。</summary>
            internal bool Hovered;
        }

        // ---------------------------------------------------------------- 生命周期

        internal void Initialize(RectTransform host, TMP_FontAsset uiFont, Sprite rounded, float width)
        {
            root = host;
            font = uiFont;
            roundedSprite = rounded;
            paneWidth = width;
            BuildSprites();
            BuildHeader();
            BuildRowScroll();
            // BuildSprites 跑在 BuildHeader 之前，那时批量按钮还不存在，
            // 所以它读到的图标没地方放。这里补一次——否则「全部隐藏」
            // 「全部锁定」两个按钮会一直空着（iconsResolved 已经置位，
            // 后面的重试也会早退，再没机会补）。
            ApplyIconSprites();
            RefreshNow();
        }

        /// <summary>
        /// 面板尺寸的唯一入口。
        ///
        /// 高度不能在 Initialize 里定死：可用的高度取决于事件按钮排了几行，
        /// 而那要等调色板自己布局完才知道。所以由时间轴在每次算出
        /// headerHeight 之后调这里，尺寸和内容一起更新。
        /// </summary>
        internal void SetPaneRect(float x, float y, float width, float height)
        {
            if (root == null)
            {
                return;
            }

            paneWidth = width;
            SetTopLeft(root, x, y, width, height);

            float listTop = HeaderHeight;
            float listHeight = Mathf.Max(RowHeight, height - listTop - PanePadding);
            if (rowScroll != null)
            {
                SetTopLeft(rowScroll.GetComponent<RectTransform>(), PanePadding, listTop,
                    Mathf.Max(40f, width - PanePadding * 2f), listHeight);
            }

            rowsDirty = true;
            UpdateRows();
        }

        /// <summary>谱面数据可能变了。由时间轴的 0.5 秒节拍驱动。</summary>
        internal void MarkDataDirty()
        {
            long fingerprint = ComputeDataFingerprint();
            if (fingerprint == dataFingerprint)
            {
                return;
            }

            dataFingerprint = fingerprint;
            entriesDirty = true;
            filterDirty = true;
            rowsDirty = true;
            RetryIconsIfNeeded();
            UpdateRows();
        }

        /// <summary>选中集可能变了。由时间轴的 0.12 秒节拍驱动。</summary>
        internal void MarkSelectionDirty()
        {
            int fingerprint = ComputeSelectionFingerprint();
            if (fingerprint == selectionFingerprint)
            {
                return;
            }

            selectionFingerprint = fingerprint;
            rowsDirty = true;
            UpdateRows();
        }

        /// <summary>增删改之后立刻重建，不等下一次轮询。</summary>
        internal void RefreshNow()
        {
            dataFingerprint = ComputeDataFingerprint();
            selectionFingerprint = ComputeSelectionFingerprint();
            entriesDirty = true;
            filterDirty = true;
            rowsDirty = true;
            UpdateRows();
        }

        private void OnDisable()
        {
            // 批量开关只改运行时对象，本来就活不过场景重载。这里显式收掉，
            // 免得工作台被关掉之后装饰物还留在隐藏状态。
            ClearBatch();
        }

        private void OnDestroy()
        {
            ClearBatch();
        }

        // ---------------------------------------------------------------- 构建

        private void BuildHeader()
        {
            float w = paneWidth - PanePadding * 2f;

            // 第一行：新建按钮 + 计数。
            //
            // 原版这里就是四个方形图标按钮，图取自事件图标表
            // （GCS.levelEventIcons）——和时间块上、事件面板上是同一套。
            // 做谱的人一眼认得出，比四个中文词既省地方又清楚。
            float tile = 26f;
            float gap = 4f;
            for (int i = 0; i < DecoTypes.Length; i++)
            {
                LevelEventType type = DecoTypes[i];
                Button button = CreateIconTile("Create" + type, root,
                    PanePadding + i * (tile + gap), 6f, tile, type,
                    delegate { CreateDecoration(type); });
                SetButtonPalette(button, false);
            }

            float countWidth = 44f;
            countLabel = CreateText("Count", root, w - countWidth + PanePadding, 6f, countWidth, tile,
                "0", 10f, Palette.TextMuted, TextAlignmentOptions.MidlineRight, false);

            // 第二行：搜索框。原版支持 tag: / type: / floor: 前缀，这里沿用。
            searchField = CreateSearchField(root, PanePadding, 38f, w, 22f);

            // 第三行：批量开关。
            //
            // 这两个只改运行时状态、不写进谱面——它们是「我想看一眼没有
            // 装饰物的谱面长什么样」用的，不该顺手改掉作者的数据。
            // 图标沿用行上那两个（闭眼 / 闭锁），和原版的总开关一致。
            float batchWidth = (w - 4f) * 0.5f;
            hideAllButton = CreateBatchButton("HideAll", root, PanePadding, 66f, batchWidth,
                "全部隐藏", delegate { ToggleBatchHidden(); }, out hideAllIcon);
            lockAllButton = CreateBatchButton("LockAll", root, PanePadding + batchWidth + 4f, 66f, batchWidth,
                "全部锁定", delegate { ToggleBatchLocked(); }, out lockAllIcon);
        }

        private Button hideAllButton;
        private Button lockAllButton;
        private Image hideAllIcon;
        private Image lockAllIcon;

        /// <summary>
        /// 方形图标按钮：一个圆角底 + 居中一张事件图标。
        /// 图取自 GCS.levelEventIcons，和事件面板上那排按钮同一个来源。
        /// </summary>
        private Button CreateIconTile(string name, RectTransform parent, float x, float y,
            float size, LevelEventType type, Action action)
        {
            Image background = CreateImage(name, parent, x, y, size, size, Palette.ButtonNormal);
            background.sprite = roundedSprite;
            background.type = Image.Type.Sliced;
            background.raycastTarget = true;

            Button button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { action(); });

            Sprite icon = null;
            if (GCS.levelEventIcons != null)
            {
                GCS.levelEventIcons.TryGetValue(type, out icon);
            }

            float inset = 4f;
            Image iconImage = CreateImage("Icon", background.rectTransform, inset, inset,
                size - inset * 2f, size - inset * 2f, Palette.TextPrimary);
            iconImage.sprite = icon;
            iconImage.preserveAspect = true;
            iconImage.enabled = icon != null;
            iconImage.raycastTarget = false;
            return button;
        }

        /// <summary>带图标的文字按钮，给批量开关用。图标留待图标读进来之后再补。</summary>
        private Button CreateBatchButton(string name, RectTransform parent, float x, float y,
            float width, string label, Action action, out Image iconImage)
        {
            Image background = CreateImage(name, parent, x, y, width, 22f, Palette.ButtonNormal);
            background.sprite = roundedSprite;
            background.type = Image.Type.Sliced;
            background.raycastTarget = true;

            Button button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { action(); });

            float iconSize = 14f;
            iconImage = CreateImage("Icon", background.rectTransform, 6f, (22f - iconSize) * 0.5f,
                iconSize, iconSize, Palette.TextMuted);
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            CreateText("Label", background.rectTransform, 6f + iconSize + 4f, 0f,
                Mathf.Max(10f, width - iconSize - 16f), 22f, label, 10f,
                Palette.TextSecondary, TextAlignmentOptions.MidlineLeft, false);
            return button;
        }

        private void BuildRowScroll()
        {
            Image background = CreateImage("RowScroll", root, PanePadding, HeaderHeight,
                Mathf.Max(40f, paneWidth - PanePadding * 2f), RowHeight, Palette.PanelAlt);
            background.raycastTarget = true;
            rowScroll = background.gameObject.AddComponent<ScrollRect>();
            rowScroll.horizontal = false;
            rowScroll.vertical = true;
            rowScroll.movementType = ScrollRect.MovementType.Clamped;
            rowScroll.inertia = true;
            rowScroll.decelerationRate = 0.12f;
            rowScroll.scrollSensitivity = 28f;

            GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportObject.transform.SetParent(background.rectTransform, false);
            rowViewport = viewportObject.GetComponent<RectTransform>();
            rowViewport.anchorMin = Vector2.zero;
            rowViewport.anchorMax = Vector2.one;
            rowViewport.offsetMin = Vector2.zero;
            rowViewport.offsetMax = Vector2.zero;
            Image viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(Palette.PanelAlt.r, Palette.PanelAlt.g, Palette.PanelAlt.b, 0.01f);
            viewportImage.raycastTarget = true;

            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(rowViewport, false);
            rowContent = contentObject.GetComponent<RectTransform>();
            rowContent.anchorMin = new Vector2(0f, 1f);
            rowContent.anchorMax = new Vector2(1f, 1f);
            rowContent.pivot = new Vector2(0.5f, 1f);
            rowContent.anchoredPosition = Vector2.zero;
            rowContent.sizeDelta = new Vector2(0f, RowHeight);

            rowScroll.viewport = rowViewport;
            rowScroll.content = rowContent;
            rowScroll.onValueChanged.AddListener(delegate { OnScrollChanged(); });
        }

        // ---------------------------------------------------------------- 数据

        private static scnEditor Editor
        {
            get { return scnEditor.instance; }
        }

        private void RebuildEntries()
        {
            entries.Clear();
            scnEditor editor = Editor;
            if (editor == null || editor.decorations == null)
            {
                return;
            }

            for (int i = 0; i < editor.decorations.Count; i++)
            {
                LevelEvent source = editor.decorations[i];
                if (source == null)
                {
                    continue;
                }

                string tag;
                if (!source.TryGet<string>("tag", out tag))
                {
                    tag = string.Empty;
                }

                entries.Add(new DecoEntry
                {
                    Source = source,
                    DisplayName = ResolveName(source, tag),
                    Tag = tag ?? string.Empty,
                    Type = source.eventType,
                    Floor = source.floor,
                    Visible = source.visible,
                    Locked = source.locked
                });
            }

            ApplyFilter();
        }

        /// <summary>
        /// 取一个能读的名字。
        ///
        /// 先读 decorationImage——也就是图片文件名，这才是真正区分
        /// 两个装饰物的东西。取样思路跟 EditorSceneInteraction.GetDecorationLabel
        /// 一致。
        ///
        /// **不能拿 tag 当名字**：tag 右边单独占一列，再拿它当名字就
        /// 把同一个值印了两遍，还白占掉名字那一列的宽度。文本类的装饰物
        /// 没有 decorationImage，这时才退回 scrDecoration.decorationName
        /// （对文本类它就是文本内容），而那个返回值带富文本标记
        /// （“&lt;i&gt;(无文本)&lt;/i&gt;”、“&lt;color=#9D00FF&gt;x&lt;/color&gt;”），得剥掉。
        /// </summary>
        private static string ResolveName(LevelEvent source, string tag)
        {
            string image;
            if (source.TryGet<string>("decorationImage", out image) && !string.IsNullOrWhiteSpace(image))
            {
                return image.Trim();
            }

            string text;
            if (source.TryGet<string>("decText", out text) && !string.IsNullOrWhiteSpace(text))
            {
                return StripRichText(text).Trim();
            }

            scrDecoration decoration = scrDecorationManager.GetDecoration(source);
            if (decoration != null && !string.IsNullOrWhiteSpace(decoration.decorationName))
            {
                string stripped = StripRichText(decoration.decorationName).Trim();
                if (stripped.Length > 0)
                {
                    return stripped;
                }
            }

            return GetTypeName(source.eventType);
        }

        private static string GetTypeName(LevelEventType type)
        {
            for (int i = 0; i < DecoTypes.Length; i++)
            {
                if (DecoTypes[i] == type)
                {
                    return DecoTypeNames[i];
                }
            }

            return type.ToString();
        }

        private static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0)
            {
                return value;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            bool inTag = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '<')
                {
                    inTag = true;
                }
                else if (c == '>')
                {
                    inTag = false;
                }
                else if (!inTag)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private void ApplyFilter()
        {
            filtered.Clear();
            ParseSearch(out string text, out int floorFilter, out string tagFilter, out int typeFilterSlot);

            for (int i = 0; i < entries.Count; i++)
            {
                DecoEntry entry = entries[i];

                if (floorFilter >= 0 && entry.Floor != floorFilter)
                {
                    continue;
                }

                if (typeFilterSlot >= 0 && TypeSlotOf(entry.Type) != typeFilterSlot)
                {
                    continue;
                }

                if (tagFilter != null
                    && !string.Equals(entry.Tag.Trim(), tagFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (text != null
                    && entry.DisplayName.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Tag.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                filtered.Add(entry);
            }

            if (countLabel != null)
            {
                // 只有 46px 宽，放不下「装饰物 N」这种长文案。
                // 筛选生效时才写成 "3/40"，一眼能看出被筛掉了多少。
                countLabel.text = filtered.Count == entries.Count
                    ? entries.Count.ToString()
                    : filtered.Count + "/" + entries.Count;
            }
        }

        /// <summary>
        /// 解析搜索词。语义照原版：`floor:` / `tag:` / `type:` 三个前缀
        /// 各管一件事，无前缀的词按名字和 tag 模糊匹配。
        /// </summary>
        private void ParseSearch(out string text, out int floor, out string tag, out int typeSlot)
        {
            text = null;
            floor = -1;
            tag = null;
            typeSlot = -1;

            if (string.IsNullOrWhiteSpace(searchText))
            {
                return;
            }

            string[] tokens = searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.StartsWith("floor:", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (int.TryParse(token.Substring(6), out parsed))
                    {
                        floor = parsed;
                    }
                }
                else if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                {
                    tag = token.Substring(4);
                }
                else if (token.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    typeSlot = TypeSlotOfName(token.Substring(5));
                }
                else
                {
                    // 多个无前缀词按“最后一个说了算”会丢词，这里退而求其次
                    // 只保留第一个：真正的多词 AND 需要把每个词单独匹配，
                    // 而列表里的名字本来就短，一个词足够定位。
                    if (text == null)
                    {
                        text = token;
                    }
                }
            }
        }

        private static int TypeSlotOfName(string name)
        {
            for (int i = 0; i < DecoTypes.Length; i++)
            {
                if (string.Equals(DecoTypeNames[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            if (string.Equals(name, "image", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            if (string.Equals(name, "text", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (string.Equals(name, "particle", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            if (string.Equals(name, "object", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            return -1;
        }

        private static int TypeSlotOf(LevelEventType type)
        {
            for (int i = 0; i < DecoTypes.Length; i++)
            {
                if (DecoTypes[i] == type)
                {
                    return i;
                }
            }

            return -1;
        }

        private long ComputeDataFingerprint()
        {
            scnEditor editor = Editor;
            if (editor == null || editor.decorations == null)
            {
                return 0L;
            }

            unchecked
            {
                long hash = 17L;
                hash = hash * 31L + editor.decorations.Count;
                for (int i = 0; i < editor.decorations.Count; i++)
                {
                    LevelEvent source = editor.decorations[i];
                    if (source == null)
                    {
                        continue;
                    }
                    hash = hash * 31L + source.GetHashCode();
                    hash = hash * 31L + source.floor;
                    hash = hash * 31L + (source.visible ? 1 : 0);
                    hash = hash * 31L + (source.locked ? 2 : 0);
                }
                return hash;
            }
        }

        private int ComputeSelectionFingerprint()
        {
            scnEditor editor = Editor;
            if (editor == null || editor.selectedDecorations == null)
            {
                return 0;
            }

            unchecked
            {
                int hash = 19;
                for (int i = 0; i < editor.selectedDecorations.Count; i++)
                {
                    LevelEvent selected = editor.selectedDecorations[i];
                    hash = hash * 31 + (selected == null ? 0 : selected.GetHashCode());
                }
                return hash;
            }
        }

        // ---------------------------------------------------------------- 行

        private void EnsureRows(int count)
        {
            while (rows.Count < count)
            {
                rows.Add(CreateRow(rows.Count));
            }
        }

        private DecoRow CreateRow(int index)
        {
            GameObject rowObject = new GameObject("DecoRow" + index, typeof(RectTransform), typeof(Image), typeof(Button));
            rowObject.transform.SetParent(rowContent, false);
            RectTransform rect = rowObject.GetComponent<RectTransform>();
            SetTopLeft(rect, 0f, 0f, 100f, RowHeight - RowGap);

            Image background = rowObject.GetComponent<Image>();
            background.color = Palette.ButtonNormal;
            background.raycastTarget = true;

            Button button = rowObject.GetComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            // 先把行对象建出来再挂三个按钮的点击回调。
            // 回调要闭包捕获 row，而编译器不允许在对象初始化器里引用
            // 正在构造的那个变量——所以分两步写。
            DecoRow row = new DecoRow
            {
                GameObject = rowObject,
                Rect = rect,
                Background = background,
                TypeIcon = CreateImage("TypeIcon", rect, 8f, (RowHeight - RowGap - 16f) * 0.5f, 16f, 16f, Color.white),
                Name = CreateText("Name", rect, 28f, 0f, 100f, RowHeight - RowGap, string.Empty, 11f,
                    Palette.TextSecondary, TextAlignmentOptions.MidlineLeft, false),
                Tag = CreateText("Tag", rect, 100f, 0f, 10f, RowHeight - RowGap, string.Empty, 10f,
                    Palette.TextMuted, TextAlignmentOptions.MidlineRight, false)
            };
            row.Eye = CreateIconButton("Eye", rect, 0f, eyeOpen, delegate { ToggleRowVisible(row); }, out row.EyeIcon);
            row.Lock = CreateIconButton("Lock", rect, 0f, lockOpen, delegate { ToggleRowLocked(row); }, out row.LockIcon);
            row.Delete = CreateIconButton("Delete", rect, 0f, deleteIcon, delegate { DeleteRow(row); }, out row.DeleteIcon);
            row.TypeIcon.raycastTarget = false;
            row.TypeIcon.preserveAspect = true;
            row.Name.overflowMode = TextOverflowModes.Ellipsis;
            row.Tag.overflowMode = TextOverflowModes.Ellipsis;

            // 悬停时才把眼睛/锁/删除露出来。常驻的话一行五个可点目标，
            // 点错删除的代价又不可逆，不如让它们等着鼠标过来。
            DecoRowHover hover = rowObject.AddComponent<DecoRowHover>();
            hover.Initialize(this, row);

            button.onClick.AddListener(delegate { ActivateRow(row); });

            rowObject.SetActive(false);
            return row;
        }

        /// <summary>
        /// 行尾一个纯图标按钮。
        ///
        /// **不画底衬**：原来给每个图标垫了一个半透明小方块，一行三个方块
        /// 读起来像三颗按钮浮在文字边上，而这里要的是「图标」。
        ///
        /// 点击区仍然独立，而且比图标本身大一圈（占满行高）——没有
        /// 可见的底之后，如果命中区只有图标那么大，就得瞄准图标正中心
        /// 才点得中。那个 Image 的 alpha 是 0，但仍能接射线。
        /// </summary>
        private Button CreateIconButton(string name, RectTransform parent, float x, Sprite sprite, Action action, out Image icon)
        {
            Image hitArea = CreateImage(name, parent, x, 0f, IconHitSize, RowHeight - RowGap,
                new Color(0f, 0f, 0f, 0f));
            hitArea.raycastTarget = true;

            Button button = hitArea.gameObject.AddComponent<Button>();
            button.targetGraphic = hitArea;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { action(); });

            float iconSize = IconButtonSize;
            icon = CreateImage(name + "Icon", hitArea.rectTransform,
                (IconHitSize - iconSize) * 0.5f, (RowHeight - RowGap - iconSize) * 0.5f,
                iconSize, iconSize, Palette.TextSecondary);
            icon.sprite = sprite;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            return button;
        }

        private void UpdateRows()
        {
            if (rowScroll == null || rowContent == null)
            {
                return;
            }

            if (entriesDirty)
            {
                RebuildEntries();
                entriesDirty = false;
                filterDirty = false;
            }
            else if (filterDirty)
            {
                ApplyFilter();
                filterDirty = false;
            }

            if (!rowsDirty)
            {
                return;
            }

            rowsDirty = false;

            float viewportHeight = rowViewport == null ? RowHeight : rowViewport.rect.height;
            int visibleCount = Mathf.CeilToInt(viewportHeight / RowHeight) + 2;
            EnsureRows(Mathf.Max(4, visibleCount));

            float contentHeight = Mathf.Max(viewportHeight, filtered.Count * RowHeight + RowGap);
            rowContent.sizeDelta = new Vector2(0f, contentHeight);

            int first = Mathf.Clamp(
                Mathf.FloorToInt(Mathf.Max(0f, rowContent.anchoredPosition.y) / RowHeight),
                0,
                Mathf.Max(0, filtered.Count - 1));

            for (int i = 0; i < rows.Count; i++)
            {
                int index = first + i;
                DecoRow row = rows[i];
                if (index >= filtered.Count)
                {
                    row.BoundIndex = -1;
                    row.GameObject.SetActive(false);
                    continue;
                }

                BindRow(row, index, filtered[index]);
            }
        }

        private void BindRow(DecoRow row, int index, DecoEntry entry)
        {
            float width = rowContent.rect.width;
            if (width < 40f)
            {
                width = Mathf.Max(40f, paneWidth - PanePadding * 2f);
            }

            row.BoundIndex = index;
            SetTopLeft(row.Rect, 0f, index * RowHeight, width, RowHeight - RowGap);

            bool selected = IsSelected(entry.Source);
            row.Background.color = selected ? Palette.Selected : Palette.ButtonNormal;
            row.Name.color = selected ? Palette.TextPrimary : Palette.TextSecondary;

            Sprite typeIcon = null;
            if (GCS.levelEventIcons != null)
            {
                GCS.levelEventIcons.TryGetValue(entry.Type, out typeIcon);
            }
            row.TypeIcon.sprite = typeIcon;
            row.TypeIcon.enabled = typeIcon != null;

            row.Name.text = entry.DisplayName;
            row.Name.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;

            // 名字和 tag 抢同一段横向空间。名字给大头（它才是用来分辨
            // 两个装饰物的），tag 只在确实有内容时才占一截。
            //
            // 这里刻意不按固定比例分：tag 是作者用来绑定关键帧的短标识
            // （vni、s1 这种），多数只有几个字符；而名字是文件名，
            // 长得多。按比例分会让 tag 白占掉一截该给名字的地方。
            float buttonsWidth = EyeWidth + LockWidth + DeleteWidth;
            float textRight = width - 8f - buttonsWidth;
            float textLeft = 26f;
            float textHeight = RowHeight - RowGap;
            if (string.IsNullOrEmpty(entry.Tag))
            {
                row.Tag.text = string.Empty;
                SetTopLeft(row.Name.rectTransform, textLeft, 0f,
                    Mathf.Max(20f, textRight - textLeft), textHeight);
            }
            else
            {
                float tagWidth = Mathf.Min(64f, Mathf.Max(34f, (textRight - textLeft) * 0.30f));
                row.Tag.text = entry.Tag;
                SetTopLeft(row.Tag.rectTransform, textRight - tagWidth, 0f, tagWidth, textHeight);
                SetTopLeft(row.Name.rectTransform, textLeft, 0f,
                    Mathf.Max(20f, textRight - tagWidth - 6f - textLeft), textHeight);
            }

            // 从右往左排：删除最右，然后锁，然后眼睛。
            SetTopLeft(row.Delete.GetComponent<RectTransform>(), width - 8f - DeleteWidth, 0f, DeleteWidth, RowHeight - RowGap);
            SetTopLeft(row.Lock.GetComponent<RectTransform>(), width - 8f - DeleteWidth - LockWidth, 0f, LockWidth, RowHeight - RowGap);
            SetTopLeft(row.Eye.GetComponent<RectTransform>(), width - 8f - DeleteWidth - LockWidth - EyeWidth, 0f, EyeWidth, RowHeight - RowGap);

            row.EyeIcon.sprite = entry.Visible ? eyeOpen : eyeClosed;
            row.LockIcon.sprite = entry.Locked ? lockClosed : lockOpen;
            // 不重置 row.Hovered：行池项滚动时只是换绑到别的装饰物，
            // 位置没动。鼠标还压着它的话，重置会让图标闪一下变暗。
            // 真的移开时 OnPointerExit 会把它清掉。
            ApplyRowIconColors(row, entry);

            row.GameObject.SetActive(true);
        }

        /// <summary>
        /// 行内三个按钮的位置是固定的，露面与否只改透明度。
        ///
        /// 它们**常显**。原来是悬停才出现，但那样每次要点隐藏或锁定都得先
        /// 把鼠标瞄准到那一行上，列表密的时候很烦；而且躲着的按钮没法
        /// 一眼看出哪些装饰物被藏了、哪些被锁了——那正是这个列表要回答的问题。
        ///
        /// 代价是三个可点目标一直杵在那儿，删除尤其容易被误点。所以
        /// 不用悬停来「显示」，只用它来「提亮」：默认 72% 不透明度，
        /// 鼠标进到这一行才满。这样它们平时不抢注意力，靠近了又够清楚。
        /// </summary>
        private void ApplyRowIconColors(DecoRow row, DecoEntry entry)
        {
            if (row == null || entry == null)
            {
                return;
            }

            float alpha = row.Hovered ? 1f : 0.72f;

            Color eyeColor = entry.Visible ? Palette.TextMuted : new Color(1f, 0.55f, 0.45f, 1f);
            eyeColor.a = alpha;
            row.EyeIcon.color = eyeColor;

            Color lockColor = entry.Locked ? Palette.Accent : Palette.TextMuted;
            lockColor.a = alpha;
            row.LockIcon.color = lockColor;

            Color deleteColor = Palette.TextMuted;
            deleteColor.a = alpha;
            row.DeleteIcon.color = deleteColor;
        }

        private DecoEntry EntryOf(DecoRow row)
        {
            if (row == null || row.BoundIndex < 0 || row.BoundIndex >= filtered.Count)
            {
                return null;
            }
            return filtered[row.BoundIndex];
        }

        private bool IsSelected(LevelEvent source)
        {
            scnEditor editor = Editor;
            if (editor == null || editor.selectedDecorations == null || source == null)
            {
                return false;
            }
            for (int i = 0; i < editor.selectedDecorations.Count; i++)
            {
                if (ReferenceEquals(editor.selectedDecorations[i], source))
                {
                    return true;
                }
            }
            return false;
        }

        private void OnScrollChanged()
        {
            rowsDirty = true;
            UpdateRows();
        }

        // ---------------------------------------------------------------- 操作

        private void ActivateRow(DecoRow row)
        {
            DecoEntry entry = EntryOf(row);
            scnEditor editor = Editor;
            if (entry == null || editor == null)
            {
                return;
            }

            // 点一下就是「只选这个」，所以先清空再选。ignoreDeselection 传 true
            // 是让 SelectDecoration 别自己再清一遍——我们要的是
            // 「清空 → 选中」这个确定结果，不是它的默认组合行为。
            bool alreadySelected = IsSelected(entry.Source);
            editor.DeselectAllDecorations();
            if (alreadySelected && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                // Ctrl+点已选中项 = 取消选中，和原版一致。
                MarkSelectionDirty();
                rowsDirty = true;
                UpdateRows();
                return;
            }

            editor.SelectDecoration(entry.Source, jumpToDecoration: false, showPanel: false,
                ignoreDeselection: true, ignoreAdjustRect: true);

            MarkSelectionDirty();
            rowsDirty = true;
            UpdateRows();
        }

        private void ToggleRowVisible(DecoRow row)
        {
            DecoEntry entry = EntryOf(row);
            scnEditor editor = Editor;
            if (entry == null || editor == null)
            {
                return;
            }

            // ShowEvent 会把 visible 写进 LevelEvent，也就是会落到谱面文件里。
            // 这是有意的：单行眼睛管的就是作者数据里的显隐。批量开关
            // 才是纯临时的那一个（见 ToggleBatchHidden）。
            using (new SaveStateScope(editor))
            {
                editor.ShowEvent(entry.Source, !entry.Visible);
            }
            RefreshNow();
        }

        private void ToggleRowLocked(DecoRow row)
        {
            DecoEntry entry = EntryOf(row);
            scnEditor editor = Editor;
            if (entry == null || editor == null)
            {
                return;
            }

            using (new SaveStateScope(editor))
            {
                editor.LockEvent(entry.Source, !entry.Locked);
            }
            RefreshNow();
        }

        private void DeleteRow(DecoRow row)
        {
            DecoEntry entry = EntryOf(row);
            scnEditor editor = Editor;
            if (entry == null || editor == null || editor.decorations == null)
            {
                return;
            }

            int index = editor.decorations.IndexOf(entry.Source);
            if (index < 0)
            {
                return;
            }

            // 走工作台自己的删除路径，不要调原版的
            // DeleteMultiSelectionDecorations。后者走 RemoveEvent 的默认分支，
            // 会链到 UpdateDecorationObjects → decManager.ClearDecorations()，
            // 把每个装饰物重建一遍，顺手摧毁预览求值器仍然持有的对象引用。
            // 我们的 RecordDecorationRemove 底层用 skipDecorationUpdate:true
            // 绕开它，并且记进撤销历史。
            EditorCommandHistory.RecordDecorationRemove(editor, entry.Source, index);
            RefreshNow();
        }

        private void OnSearchChanged(string value)
        {
            string next = value ?? string.Empty;
            if (string.Equals(next, searchText, StringComparison.Ordinal))
            {
                return;
            }
            searchText = next;
            filterDirty = true;
            rowsDirty = true;
            UpdateRows();
        }

        private void ToggleBatchHidden()
        {
            batchHidden = !batchHidden;
            scnEditor editor = Editor;
            if (editor != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    scrDecoration decoration = scrDecorationManager.GetDecoration(entries[i].Source);
                    if (decoration != null)
                    {
                        editor.ForceHideEvent(decoration, batchHidden);
                    }
                }
            }
            if (hideAllButton != null)
            {
                SetButtonPalette(hideAllButton, batchHidden);
            }
        }

        private void ToggleBatchLocked()
        {
            batchLocked = !batchLocked;
            scnEditor editor = Editor;
            if (editor != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    scrDecoration decoration = scrDecorationManager.GetDecoration(entries[i].Source);
                    if (decoration != null)
                    {
                        editor.ForceLockEvent(decoration, batchLocked);
                    }
                }
            }
            if (lockAllButton != null)
            {
                SetButtonPalette(lockAllButton, batchLocked);
            }
        }

        /// <summary>
        /// 把批量开关收回去。这两个只改运行时对象、不写文件，
        /// 所以关工作台或换谱面时必须主动复原，否则装饰物会一直
        /// 停在「被批量隐藏」的状态里，而作者根本找不到是谁干的。
        /// </summary>
        private void ClearBatch()
        {
            if (!batchHidden && !batchLocked)
            {
                return;
            }

            scnEditor editor = Editor;
            if (editor != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    scrDecoration decoration = scrDecorationManager.GetDecoration(entries[i].Source);
                    if (decoration == null)
                    {
                        continue;
                    }
                    if (batchHidden)
                    {
                        editor.ForceHideEvent(decoration, false);
                    }
                    if (batchLocked)
                    {
                        editor.ForceLockEvent(decoration, false);
                    }
                }
            }

            batchHidden = false;
            batchLocked = false;
            if (hideAllButton != null)
            {
                SetButtonPalette(hideAllButton, false);
            }
            if (lockAllButton != null)
            {
                SetButtonPalette(lockAllButton, false);
            }
        }

        /// <summary>
        /// 新建一个装饰物。
        ///
        /// 注意建出来的是**空白装饰物**：图片类没有图就什么都不画。
        /// 这是原版行为，不是缺陷——但得让用户知道，
        /// 否则会以为「点了没反应」。图片建议从媒体网格拖进来。
        /// </summary>
        private void CreateDecoration(LevelEventType type)
        {
            scnEditor editor = Editor;
            if (editor == null)
            {
                return;
            }

            LevelEvent created = editor.AddDecoration(type);
            if (created != null)
            {
                int index = editor.decorations.IndexOf(created);
                if (index >= 0)
                {
                    EditorCommandHistory.RecordDecorationCreate(editor, created, index);
                }
            }

            RefreshNow();
            if (created != null)
            {
                editor.SelectDecoration(created, jumpToDecoration: false, showPanel: false,
                    ignoreDeselection: true, ignoreAdjustRect: true);
            }
        }

        // ---------------------------------------------------------------- 图标

        /// <summary>
        /// 取行上那几个小图标。
        ///
        /// 眼睛和锁直接读原版编辑器左栏那张列表上挂着的精灵
        /// （scnEditor.propertyControlDecorationsList 是 public 字段，
        /// 那四个精灵也是 public）。原版那几张是在游戏分辨率下调过的，
        /// 自己按 12x12 画出来的在 18px 下笔画全糊在一起，比不过。
        ///
        /// 删除用原版「删除选中装饰物」按钮上的那张图。
        ///
        /// 读不到时退回自绘——工作台有可能在编辑器原生 UI 就绪之前
        /// 被打开，那时这些引用还是空的。
        /// </summary>
        private void BuildSprites()
        {
            iconsResolved = TryLoadNativeIcons();

            if (eyeOpen == null)
            {
                eyeOpen = BuildGlyph("DecoEyeOpen", EyeGlyph(true));
            }
            if (eyeClosed == null)
            {
                eyeClosed = BuildGlyph("DecoEyeClosed", EyeGlyph(false));
            }
            if (lockOpen == null)
            {
                lockOpen = BuildGlyph("DecoLockOpen", LockGlyph(false));
            }
            if (lockClosed == null)
            {
                lockClosed = BuildGlyph("DecoLockClosed", LockGlyph(true));
            }
            if (deleteIcon == null)
            {
                deleteIcon = BuildGlyph("DecoDelete", CrossGlyph());
            }
        }

        private bool iconsResolved;

        /// <summary>再试一次读原版图标。数据节拍里会调，直到读到为止。</summary>
        private void RetryIconsIfNeeded()
        {
            if (iconsResolved)
            {
                return;
            }
            if (TryLoadNativeIcons())
            {
                iconsResolved = true;
                ApplyIconSprites();
                rowsDirty = true;
            }
        }

        /// <summary>
        /// 把当前读到的图标刷到所有用它的地方：每一行的三个小按钮，
        /// 以及批量开关那两个。
        /// </summary>
        private void ApplyIconSprites()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].EyeIcon.sprite = eyeOpen;
                rows[i].LockIcon.sprite = lockOpen;
                rows[i].DeleteIcon.sprite = deleteIcon;
            }

            // 批量开关用行上那两个的「关闭」态：闭眼 / 闭锁——原版的总开关
            // 就是这么表示「全部隐藏 / 全部锁定」的。
            if (hideAllIcon != null)
            {
                hideAllIcon.sprite = eyeClosed;
            }
            if (lockAllIcon != null)
            {
                lockAllIcon.sprite = lockClosed;
            }
        }

        private bool TryLoadNativeIcons()
        {
            scnEditor editor = Editor;
            PropertyControl_DecorationsList native = editor == null
                ? null
                : editor.propertyControlDecorationsList;
            if (native == null)
            {
                return false;
            }

            bool any = false;
            if (native.eyeOpenSprite != null)
            {
                eyeOpen = native.eyeOpenSprite;
                any = true;
            }
            if (native.eyeClosedSprite != null)
            {
                eyeClosed = native.eyeClosedSprite;
                any = true;
            }
            if (native.lockOpenSprite != null)
            {
                lockOpen = native.lockOpenSprite;
                any = true;
            }
            if (native.lockClosedSprite != null)
            {
                lockClosed = native.lockClosedSprite;
                any = true;
            }

            Sprite removeSprite = ResolveRemoveSprite(native);
            if (removeSprite != null)
            {
                deleteIcon = removeSprite;
                any = true;
            }

            return any;
        }

        /// <summary>
        /// 找原版「删除」按钮上的图标。
        ///
        /// **不能拿 removeButton.targetGraphic 的精灵**：那是按钮的底图，
        /// 一个圆角矩形，拿来当图标就会在行尾贴出一个实心小方块。
        /// 原版把图标挂在按钮的子节点上，所以优先翻子节点。
        ///
        /// 翻不到就返回 null，让调用方退回自绘的叉——宁可用自绘的，
        /// 也不要显示一个矩形。
        /// </summary>
        private static Sprite ResolveRemoveSprite(PropertyControl_DecorationsList native)
        {
            if (native.removeButton == null)
            {
                return null;
            }

            Transform buttonTransform = native.removeButton.transform;
            for (int i = 0; i < buttonTransform.childCount; i++)
            {
                Image child = buttonTransform.GetChild(i).GetComponent<Image>();
                if (child != null && child.sprite != null)
                {
                    return child.sprite;
                }
            }

            return null;
        }

        private Sprite BuildGlyph(string name, bool[] pixels)
        {
            const int size = 12;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = name + "Texture";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color32[] colors = new Color32[size * size];
            Color32 on = new Color32(255, 255, 255, 255);
            Color32 off = new Color32(255, 255, 255, 0);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 贴图坐标 y 向上，字形数组 y 向下，这里翻一下。
                    colors[(size - 1 - y) * size + x] = pixels[y * size + x] ? on : off;
                }
            }
            texture.SetPixels32(colors);
            texture.Apply();

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = name;
            return sprite;
        }

        /// <summary>眼睛：一个椭圆轮廓，闭眼时中间加一道斜杠。</summary>
        private static bool[] EyeGlyph(bool open)
        {
            const int size = 12;
            bool[] pixels = new bool[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - 5.5f) / 5.5f;
                    float dy = (y - 5.5f) / 3.2f;
                    float distance = dx * dx + dy * dy;
                    bool outline = distance <= 1f && distance >= 0.42f;
                    if (outline)
                    {
                        pixels[y * size + x] = true;
                    }
                }
            }

            if (open)
            {
                // 瞳孔
                for (int y = 4; y <= 7; y++)
                {
                    for (int x = 4; x <= 7; x++)
                    {
                        pixels[y * size + x] = true;
                    }
                }
            }
            else
            {
                // 斜杠
                for (int y = 1; y < size - 1; y++)
                {
                    int x = (size - 1) - y;
                    for (int t = 0; t < 2; t++)
                    {
                        int xx = Mathf.Clamp(x + t, 0, size - 1);
                        pixels[y * size + xx] = true;
                    }
                }
            }

            return pixels;
        }

        /// <summary>锁：一个方体加一道弧形的梁。</summary>
        private static bool[] LockGlyph(bool closed)
        {
            const int size = 12;
            bool[] pixels = new bool[size * size];

            // 锁体
            for (int y = 5; y <= 10; y++)
            {
                for (int x = 2; x <= 9; x++)
                {
                    bool edge = y == 5 || y == 10 || x == 2 || x == 9;
                    if (edge)
                    {
                        pixels[y * size + x] = true;
                    }
                }
            }

            // 锁梁。开着的时候往右偏出去，读起来就是「没扣上」。
            int offset = closed ? 0 : 3;
            for (int y = 1; y <= 5; y++)
            {
                float dx = (5.5f + offset - 5.5f) / 4.0f;
                float dy = (y - 5.0f) / 4.5f;
                float distance = dx * dx + dy * dy;
                if (distance <= 1f && distance >= 0.5f)
                {
                    int left = Mathf.Clamp(3 + offset, 0, size - 1);
                    int right = Mathf.Clamp(8 + offset, 0, size - 1);
                    pixels[y * size + left] = true;
                    pixels[y * size + right] = true;
                }
            }

            return pixels;
        }

        /// <summary>删除：一个叉。</summary>
        private static bool[] CrossGlyph()
        {
            const int size = 12;
            bool[] pixels = new bool[size * size];
            for (int i = 2; i <= 9; i++)
            {
                for (int t = 0; t < 2; t++)
                {
                    int a = Mathf.Clamp(i + t, 0, size - 1);
                    pixels[i * size + a] = true;
                    pixels[i * size + (size - 1 - a)] = true;
                }
            }
            return pixels;
        }

        // ---------------------------------------------------------------- UI 辅助

        private TMP_InputField CreateSearchField(RectTransform parent, float x, float y, float width, float height)
        {
            RectTransform rect = CreateRect("Search", parent, x, y, width, height);
            // 先在非激活状态下接好线，最后再打开。TMP_InputField 在 OnEnable
            // 里创建光标，那时会去读 textComponent 和 textViewport；对活着的
            // 对象 AddComponent 会立刻跑 OnEnable，而这时两者都还没赋值，
            // 于是光标根本不会建，字段再点也不会闪。
            rect.gameObject.SetActive(false);

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Palette.FieldWell;
            background.raycastTarget = true;

            Outline rim = rect.gameObject.AddComponent<Outline>();
            rim.effectColor = new Color(1f, 1f, 1f, 0.10f);
            rim.effectDistance = new Vector2(1f, -1f);
            rim.useGraphicAlpha = false;

            // 文本与占位符都铺满输入框（拉伸锚点），而不是左上锚点定尺寸。
            // TMP_InputField 会写 textComponent 的 anchoredPosition 去跟光标，
            // 左上锚点那套会跟它打架，文本和光标就一起往上偏。
            TextMeshProUGUI text = CreateStretchedText("Text", rect, 7f,
                string.Empty, 11f, Palette.TextPrimary);
            TextMeshProUGUI placeholder = CreateStretchedText("Placeholder", rect, 7f,
                "名字 / tag: / type: / floor:", 9.5f, Palette.TextMuted);
            text.overflowMode = TextOverflowModes.Ellipsis;
            placeholder.overflowMode = TextOverflowModes.Ellipsis;

            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.richText = false;
            field.customCaretColor = true;
            field.caretColor = new Color(0.960f, 0.960f, 0.960f, 1f);
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.textViewport = rect;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.onValueChanged.AddListener(delegate(string value) { OnSearchChanged(value); });

            rect.gameObject.SetActive(true);
            return field;
        }

        private static void SetButtonPalette(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }
            ColorBlock colors = button.colors;
            colors.normalColor = selected ? Palette.Selected : Palette.ButtonNormal;
            colors.highlightedColor = Palette.ButtonHover;
            colors.pressedColor = Palette.Selected;
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(Palette.ButtonNormal.r, Palette.ButtonNormal.g, Palette.ButtonNormal.b, 0.45f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }

        private TextMeshProUGUI CreateText(string name, RectTransform parent, float x, float y,
            float width, float height, string value, float size, Color color,
            TextAlignmentOptions alignment, bool bold)
        {
            RectTransform rect = CreateRect(name, parent, x, y, width, height);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        private Button CreateButton(string name, RectTransform parent, float x, float y,
            float width, float height, string label, Action action)
        {
            Image image = CreateImage(name, parent, x, y, width, height, Palette.ButtonNormal);
            image.sprite = roundedSprite;
            image.type = Image.Type.Sliced;
            Button button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { action(); });
            CreateText("Label", image.rectTransform, 0f, 0f, width, height, label, 10.5f,
                Palette.TextSecondary, TextAlignmentOptions.Center, false);
            return button;
        }

        private static Image CreateImage(string name, RectTransform parent, float x, float y,
            float width, float height, Color color)
        {
            RectTransform rect = CreateRect(name, parent, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static RectTransform CreateRect(string name, RectTransform parent, float x, float y,
            float width, float height)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            SetTopLeft(rect, x, y, width, height);
            return rect;
        }

        private static readonly Vector2 TopLeftAnchor = new Vector2(0f, 1f);

        /// <summary>
        /// 一个铺满父节点（左右各留 inset）的文本。给输入框内部用——
        /// 见 CreateSearchField 里那段说明。
        /// </summary>
        private TextMeshProUGUI CreateStretchedText(string name, RectTransform parent, float inset,
            string value, float size, Color color)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, 0f);
            rect.offsetMax = new Vector2(-inset, 0f);

            TextMeshProUGUI text = gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            if (rect == null)
            {
                return;
            }
            if (rect.anchorMin != TopLeftAnchor)
            {
                rect.anchorMin = TopLeftAnchor;
            }
            if (rect.anchorMax != TopLeftAnchor)
            {
                rect.anchorMax = TopLeftAnchor;
            }
            if (rect.pivot != TopLeftAnchor)
            {
                rect.pivot = TopLeftAnchor;
            }
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>
        /// 悬停时把一行的三个小按钮露出来。做成独立组件而不是在行上实现
        /// IPointerEnterHandler——行身上已经有一个 Button 在做选中，
        /// 再让同一个物体实现指针接口容易和它的按下逻辑打架。
        /// </summary>
        private sealed class DecoRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private EditorDecorationList owner;
            private DecoRow row;

            internal void Initialize(EditorDecorationList host, DecoRow target)
            {
                owner = host;
                row = target;
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (owner != null)
                {
                    owner.OnRowHover(row, true);
                }
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (owner != null)
                {
                    owner.OnRowHover(row, false);
                }
            }
        }

        private void OnRowHover(DecoRow row, bool hover)
        {
            DecoEntry entry = EntryOf(row);
            if (entry == null || row.Hovered == hover)
            {
                return;
            }
            row.Hovered = hover;
            ApplyRowIconColors(row, entry);
        }
    }
}
