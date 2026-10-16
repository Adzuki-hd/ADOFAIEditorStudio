using System.Collections.Generic;
using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio.Multiplayer
{
    /// <summary>
    /// 联机面板：开房 / 加入 / 成员表 / 同步按钮。
    ///
    /// 挂在工作台的 `designRoot` 上（1920×1080 设计坐标），显示时置于最上层。
    /// 不做成嵌在某个面板里的子区域，是因为它是个「模式切换」——
    /// 开着的时候需要盖住整个工作台。
    ///
    /// **UI 辅助是自带的**，和 <see cref="EditorDecorationList"/> 同一个做法：
    /// 那些 `CreateText` / `CreateButton` 都是 `EditorStudioOverlay` 的私有成员，
    /// 这里够不着，所以自带一套，只从外面接过 font 与圆角 sprite。
    /// 唯一的外部依赖是 `EditorStudioOverlay.ApplyTextShadow`（internal static）。
    /// </summary>
    internal sealed class MultiplayerPanel : MonoBehaviour
    {
        // ---------------------------------------------------------------- 面板尺寸

        private const float PanelWidth = 560f;
        private const float PanelHeight = 520f;
        private const float Pad = 20f;
        private const float RowHeight = 34f;
        private const float Gap = 10f;

        // ---------------------------------------------------------------- 状态

        private RectTransform root;
        private RectTransform contentRoot;
        private RectTransform hostGroup;
        private RectTransform joinGroup;
        private RectTransform memberGroup;

        private TMP_FontAsset font;
        private Sprite roundedSprite;

        private TMP_InputField nameField;
        private TMP_InputField portField;
        private TMP_InputField addressField;
        private TextMeshProUGUI statusLabel;
        private TextMeshProUGUI portLabel;
        /// <summary>未联机状态下的提示（开房被拒、加入失败等）。</summary>
        private TextMeshProUGUI pickLabel;

        private readonly List<MemberRow> memberRows = new List<MemberRow>();
        private int lastMemberFingerprint = -1;

        private sealed class MemberRow
        {
            internal GameObject GameObject;
            internal TextMeshProUGUI Name;
        }

        // ---------------------------------------------------------------- 建立

        internal void Initialize(RectTransform host, TMP_FontAsset uiFont, Sprite rounded)
        {
            root = host;
            font = uiFont;
            roundedSprite = rounded;

            Build();
            SetVisible(false);
        }

        private void Build()
        {
            // 遮罩：面板摊开时压暗后面的工作台，也顺手吃掉点击，
            // 免得用户点到后面的控件。
            Image shade = CreateImage("Shade", root, 0f, 0f, 1920f, 1080f, new Color(0f, 0f, 0f, 0.55f));
            shade.raycastTarget = true;
            Button shadeButton = shade.gameObject.AddComponent<Button>();
            shadeButton.transition = Selectable.Transition.None;
            shadeButton.targetGraphic = shade;
            shadeButton.onClick.AddListener(delegate { SetVisible(false); });

            float x = (1920f - PanelWidth) * 0.5f;
            float y = (1080f - PanelHeight) * 0.5f;
            Image panel = CreateImage("Panel", root, x, y, PanelWidth, PanelHeight, Palette.Panel);
            panel.raycastTarget = true;
            panel.sprite = roundedSprite;
            panel.type = Image.Type.Sliced;
            contentRoot = panel.rectTransform;

            CreateText("Title", contentRoot, Pad, 14f, PanelWidth - Pad * 2f - 40f, 30f,
                "联机做谱", 16f, Palette.TextPrimary, TextAlignmentOptions.MidlineLeft, true);
            CreateButton("Close", contentRoot, PanelWidth - Pad - 30f, 14f, 30f, 30f, "×",
                delegate { SetVisible(false); });

            // ── 身份：名字 + 端口。开房和加入都要用，所以放最上面共用。
            float row = 56f;
            CreateText("NameLabel", contentRoot, Pad, row, 70f, RowHeight,
                "名字", 13f, Palette.TextSecondary, TextAlignmentOptions.MidlineLeft, false);
            nameField = CreateInputField(contentRoot, Pad + 74f, row, PanelWidth - Pad * 2f - 74f, RowHeight,
                "给自己起个名字");

            row += RowHeight + Gap;
            CreateText("PortLabel", contentRoot, Pad, row, 70f, RowHeight,
                "端口", 13f, Palette.TextSecondary, TextAlignmentOptions.MidlineLeft, false);
            portField = CreateInputField(contentRoot, Pad + 74f, row, 96f, RowHeight,
                DefaultPort.ToString());
            CreateText("PortHint", contentRoot, Pad + 74f + 104f, row, PanelWidth - Pad * 2f - 178f, RowHeight,
                "开房用这个端口；加入时地址里没写端口也用它", 11f, Palette.TextMuted,
                TextAlignmentOptions.MidlineLeft, false);

            // ── 未联机：开房 / 加入
            row += RowHeight + Gap;
            hostGroup = CreateRect("HostGroup", contentRoot, 0f, row, PanelWidth, 46f);
            CreateButton("HostButton", hostGroup, Pad, 0f, 150f, 38f, "开房", delegate { DoHost(); });

            // 这一组是**没联机时**才显示的，而「没打开谱面就点开房」的
            // 拒绝提示、以及加入失败的原因，都要在这里看得见——memberGroup
            // 里的状态行此时整个是隐藏的，写进去等于没写。
            pickLabel = CreateText("Pick", hostGroup, Pad + 160f, 0f, PanelWidth - Pad * 2f - 160f, 38f,
                string.Empty, 13f, UiTheme.Current.FeedbackDanger,
                TextAlignmentOptions.MidlineLeft, false);
            pickLabel.textWrappingMode = TextWrappingModes.Normal;
            portLabel = CreateText("Port", hostGroup, Pad + 160f, 0f, PanelWidth - Pad * 2f - 160f, 38f,
                string.Empty, 13f, Palette.TextMuted, TextAlignmentOptions.MidlineLeft, false);

            row += 50f;
            joinGroup = CreateRect("JoinGroup", contentRoot, 0f, row, PanelWidth, 46f);
            addressField = CreateInputField(joinGroup, Pad, 0f, PanelWidth - Pad * 2f - 120f, 38f,
                "主机地址，如 192.168.1.5:7777");
            CreateButton("JoinButton", joinGroup, Pad + PanelWidth - Pad * 2f - 110f, 0f, 110f, 38f, "加入",
                delegate { DoJoin(); });

            // ── 已联机：状态 + 成员 + 同步
            row += 50f;
            memberGroup = CreateRect("MemberGroup", contentRoot, 0f, row, PanelWidth, PanelHeight - row - Pad);
            statusLabel = CreateText("Status", memberGroup, Pad, 0f, PanelWidth - Pad * 2f, 24f,
                string.Empty, 13f, Palette.TextMuted, TextAlignmentOptions.MidlineLeft, false);

            float memberTop = 30f;
            for (int i = 0; i < 8; i++)
            {
                memberRows.Add(CreateMemberRow(memberGroup, memberTop + i * 30f));
            }

            float buttonTop = PanelHeight - row - Pad - 96f;
            // **没有「推送谱面」按钮了。**
            //
            // 改值走增量、结构改动自动整份推，两个方向都是自动的，
            // 留个手动推送只会在「同步没生效」时让人误以为要自己点一下。
            //
            // 保留「重新同步」：它是唯一的手动兜底——两边真分叉了
            // （各自加过事件），拿房主那份覆盖回来是唯一的修法。
            CreateButton("Pull", memberGroup, Pad, buttonTop, 130f, 34f, "重新同步",
                delegate { MultiplayerSession.PullChart(); });
            CreateButton("Leave", memberGroup, Pad + 140f, buttonTop, 100f, 34f, "断开",
                delegate { MultiplayerSession.Stop(); });

        }

        private MemberRow CreateMemberRow(RectTransform parent, float y)
        {
            GameObject rowObject = CreateRect("MemberRow", parent, 0f, y, PanelWidth, 26f).gameObject;
            TextMeshProUGUI name = CreateText("Name", rowObject.GetComponent<RectTransform>(),
                Pad, 0f, PanelWidth - Pad * 2f, 22f, string.Empty, 12.5f,
                Palette.TextSecondary, TextAlignmentOptions.MidlineLeft, false);

            rowObject.SetActive(false);
            return new MemberRow
            {
                GameObject = rowObject,
                Name = name
            };
        }

        // ---------------------------------------------------------------- 显隐

        internal bool IsVisible
        {
            get { return root != null && root.gameObject.activeSelf; }
        }

        internal void SetVisible(bool value)
        {
            if (root == null)
            {
                return;
            }

            root.gameObject.SetActive(value);
            if (value)
            {
                // 置顶：它要盖住整个工作台。
                root.SetAsLastSibling();
                if (nameField != null && Main.Settings != null)
                {
                    nameField.text = Main.Settings.MultiplayerName ?? string.Empty;
                }
                if (addressField != null && Main.Settings != null)
                {
                    addressField.text = Main.Settings.MultiplayerAddress ?? string.Empty;
                }
                if (portField != null)
                {
                    int saved = Main.Settings == null ? 0 : Main.Settings.MultiplayerPort;
                    // 0 表示还没设置过，回落到默认端口。
                    portField.text = (saved >= 1 && saved <= 65535 ? saved : DefaultPort).ToString();
                }
                Refresh();
            }
        }

        internal void Toggle()
        {
            SetVisible(!IsVisible);
        }

        // ---------------------------------------------------------------- 每帧

        internal void Tick()
        {
            // **会话必须每帧驱动，跟面板开没开着无关。**
            //
            // 一开始这里写的是「不可见就返回」，那等于「关掉面板 = 断开联机」——
            // 但用户点一下 × 只是把窗口收起来，他的会话、成员表、
            // 后台的收发线程都还在跑。何况谱面消息可能是这时候到的，
            // 不驱动就永远处理不了。
            //
            // 界面的刷新仍然只在可见时做：关着的时候没人看，
            // 每帧刷新那些 TMP 文本是白烧。
            MultiplayerSession.Tick();

            if (IsVisible)
            {
                Refresh();
            }
        }

        private void Refresh()
        {
            bool active = MultiplayerSession.IsActive;

            if (hostGroup != null)
            {
                hostGroup.gameObject.SetActive(!active);
            }
            if (joinGroup != null)
            {
                joinGroup.gameObject.SetActive(!active);
            }
            if (memberGroup != null)
            {
                memberGroup.gameObject.SetActive(active);
            }

            // 开房那一行右边只有一块地方，**错误和端口提示共用它**。
            // 有错误就显示错误（比如「先打开一张谱面再开房」），
            // 没有才显示端口——两者同时显示会叠在一起。
            string offlineError = active ? null : MultiplayerSession.LastError;
            bool showError = !string.IsNullOrEmpty(offlineError);

            if (pickLabel != null)
            {
                if (pickLabel.gameObject.activeSelf != showError)
                {
                    pickLabel.gameObject.SetActive(showError);
                }
                if (showError)
                {
                    pickLabel.text = offlineError;
                }
            }

            if (portLabel != null)
            {
                bool showPort = !showError;
                if (portLabel.gameObject.activeSelf != showPort)
                {
                    portLabel.gameObject.SetActive(showPort);
                }
                // 没在联机时显示「用哪个端口开房」，联机后显示实际监听端口。
                portLabel.text = active && MultiplayerSession.Role == MultiplayerRole.Host
                    ? "监听端口 " + MultiplayerSession.ListenPort + "（把它配到内网穿透上）"
                    : "端口 " + (CurrentPortOrEmpty());
            }

            if (statusLabel != null)
            {
                string status = MultiplayerSession.StatusText;
                string error = MultiplayerSession.LastError;
                string media = MultiplayerSession.MediaStatus;

                // 媒体进度优先显示：它是**正在发生的事**，而 StatusText
                // 是上一件事的结果。传音乐/贴图时用户最需要知道的就是
                // 「还在收、收到第几个了」。
                if (!string.IsNullOrEmpty(media))
                {
                    statusLabel.text = media;
                    statusLabel.color = Palette.TextMuted;
                }
                else
                {
                    statusLabel.text = string.IsNullOrEmpty(error) ? (status ?? string.Empty) : error;
                    statusLabel.color = string.IsNullOrEmpty(error)
                        ? Palette.TextMuted
                        : UiTheme.Current.FeedbackDanger;
                }
            }

            RefreshMembers();
        }

        private void RefreshMembers()
        {
            ICollection<PeerInfo> members = MultiplayerSession.Members;

            // 成员表变了才重建。每帧重建这些 TMP 文本是没有必要的开销。
            int fingerprint = members.Count;
            foreach (PeerInfo peer in members)
            {
                unchecked
                {
                    fingerprint = fingerprint * 31 + peer.Id;
                    fingerprint = fingerprint * 31 + (peer.Name == null ? 0 : peer.Name.GetHashCode());
                }
            }
            if (fingerprint == lastMemberFingerprint)
            {
                return;
            }
            lastMemberFingerprint = fingerprint;

            int index = 0;
            foreach (PeerInfo peer in members)
            {
                if (index >= memberRows.Count)
                {
                    break;
                }
                MemberRow row = memberRows[index++];
                row.GameObject.SetActive(true);
                row.Name.text = peer.Name + (peer.IsHost ? "（主机）" : string.Empty);
            }
            for (int i = index; i < memberRows.Count; i++)
            {
                memberRows[i].GameObject.SetActive(false);
            }
        }

        // ---------------------------------------------------------------- 交互

        private void DoHost()
        {
            SaveIdentity();
            // **端口从输入框读，默认就是 DefaultPort。**
            //
            // 不能让它随机：端口一随机，主机每次开房都得回内网穿透的
            // 控制台改隧道配置，还得重新通知朋友——那把「本可配一次就
            // 完事」的事变成了每局一次。给个默认值，配一次穿透就永久有效；
            // 想换端口的人也能在框里改。
            MultiplayerSession.StartHosting(CurrentName(), CurrentPort());
            Refresh();
        }

        private void DoJoin()
        {
            SaveIdentity();

            string address = addressField == null ? string.Empty : addressField.text;
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            string host = address.Trim();
            int port = CurrentPort();
            int colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                int parsed;
                if (int.TryParse(host.Substring(colon + 1), out parsed)
                    && parsed >= 1 && parsed <= 65535)
                {
                    port = parsed;
                    host = host.Substring(0, colon);
                }
            }

            MultiplayerSession.JoinHost(CurrentName(), host, port);
            Refresh();
        }

        /// <summary>端口框里现在是什么，供只读显示用。</summary>
        private string CurrentPortOrEmpty()
        {
            return portField == null ? string.Empty : (portField.text ?? string.Empty);
        }

        /// <summary>端口框里的端口。空着或填了非法值就回落默认端口。</summary>
        private int CurrentPort()
        {
            string typed = portField == null ? string.Empty : portField.text;
            int parsed;
            if (!string.IsNullOrWhiteSpace(typed)
                && int.TryParse(typed.Trim(), out parsed)
                && parsed >= 1 && parsed <= 65535)
            {
                return parsed;
            }
            return DefaultPort;
        }

        /// <summary>
        /// 默认端口。
        ///
        /// **主机开房固定用它，加入时地址里没写端口也默认用它。**
        /// 挑 21587 是因为它在动态端口区间（49152 起）之外，
        /// 不会和系统临时分配的端口撞上，也基本不撞常见软件。
        ///
        /// 用户可以改（面板上有端口框），加入时地址里写 `主机:端口`
        /// 也能临时指定。这里是**没写时的默认值**。
        ///
        /// 主机侧有占用兜底：从指定端口起连着试 6 个，实际用到的那个
        /// 会显示在面板上，配置内网穿透时照着填。
        /// </summary>
        internal const int DefaultPort = 21587;

        private string CurrentName()
        {
            string typed = nameField == null ? string.Empty : nameField.text;
            if (!string.IsNullOrWhiteSpace(typed))
            {
                return typed.Trim();
            }
            return "成员";
        }

        private void SaveIdentity()
        {
            if (Main.Settings == null)
            {
                return;
            }
            Main.Settings.MultiplayerName = CurrentName();
            Main.Settings.MultiplayerPort = CurrentPort();
            Main.Settings.MultiplayerAddress = addressField == null ? string.Empty : addressField.text;
            Main.SaveSettings();
        }

        // ---------------------------------------------------------------- UI 辅助

        private TMP_InputField CreateInputField(RectTransform parent, float x, float y,
            float width, float height, string placeholder)
        {
            RectTransform rect = CreateRect("Input", parent, x, y, width, height);

            // 先禁用再 AddComponent：TMP_InputField 在 OnEnable 里建光标，
            // 那时会去读 textComponent 和 textViewport。对活着的对象
            // AddComponent 会立刻跑 OnEnable，而这时两者都还是 null，
            // 光标就永远建不出来，字段再点也不会闪。
            rect.gameObject.SetActive(false);

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = Palette.FieldWell;
            background.raycastTarget = true;
            background.sprite = roundedSprite;
            background.type = Image.Type.Sliced;

            Outline rim = rect.gameObject.AddComponent<Outline>();
            rim.effectColor = new Color(1f, 1f, 1f, 0.10f);
            rim.effectDistance = new Vector2(1f, -1f);
            rim.useGraphicAlpha = false;

            // 文本节点用**拉伸锚点**，而且必须是新对象、枢轴走默认的
            // 正中。TMP_InputField 的光标是文本父节点下的兄弟节点，
            // 它的锚点/枢轴/位置/尺寸会整套从文本抄过去——枢轴一偏，
            // 光标就跟着偏，看起来就是「光标和文字对不齐」。
            GameObject textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(rect, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = 13f;
            text.color = Palette.TextPrimary;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);

            GameObject holderObject = new GameObject("Placeholder", typeof(RectTransform));
            holderObject.transform.SetParent(rect, false);
            RectTransform holderRect = holderObject.GetComponent<RectTransform>();
            holderRect.anchorMin = Vector2.zero;
            holderRect.anchorMax = Vector2.one;
            holderRect.pivot = new Vector2(0.5f, 0.5f);
            holderRect.offsetMin = new Vector2(8f, 0f);
            holderRect.offsetMax = new Vector2(-8f, 0f);
            TextMeshProUGUI holder = holderObject.AddComponent<TextMeshProUGUI>();
            holder.font = font;
            holder.fontSize = 13f;
            holder.color = Palette.TextMuted;
            holder.alignment = TextAlignmentOptions.MidlineLeft;
            holder.text = placeholder;
            holder.textWrappingMode = TextWrappingModes.NoWrap;
            holder.raycastTarget = false;

            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.richText = false;
            field.customCaretColor = true;
            field.caretColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.textViewport = rect;
            field.textComponent = text;
            field.placeholder = holder;

            rect.gameObject.SetActive(true);
            return field;
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
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        private Button CreateButton(string name, RectTransform parent, float x, float y,
            float width, float height, string label, UnityEngine.Events.UnityAction action)
        {
            Image image = CreateImage(name, parent, x, y, width, height, Palette.ButtonNormal);
            image.sprite = roundedSprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = true;

            Button button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.ColorTint;
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            button.onClick.AddListener(action);

            CreateText("Label", image.rectTransform, 0f, 0f, width, height, label, 12.5f,
                Palette.TextSecondary, TextAlignmentOptions.Center, false);
            return button;
        }

        private Image CreateImage(string name, RectTransform parent, float x, float y,
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

        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = TopLeftAnchor;
            rect.anchorMax = TopLeftAnchor;
            rect.pivot = TopLeftAnchor;
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
