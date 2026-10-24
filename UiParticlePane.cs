using System;
using ADOFAI;
using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 粒子参数面板：**基本 / 形状 / 变换** 三页，行名、顺序、单位照原生面板。
    ///
    /// 数据源不是 `AddParticle` 的事件属性，而是 `previewDec.particleSystem`
    /// 的模块（事件属性管"粒子摆在哪"，这里管"粒子长什么样"）。
    ///
    /// 大量字段是「最小 - 最大」两个值，用 `MinMaxCurve` 的
    /// `constantMin/constantMax`（模式 TwoConstants）读写。
    /// </summary>
    internal sealed class UiParticlePane : MonoBehaviour
    {
        private const float TabHeight = 30f;
        private const float RowHeight = 34f;
        private const float LabelWidth = 118f;
        private const float FieldHeight = 26f;
        private const float PairGap = 4f;

        private static readonly string[] TabNames = { "基本", "形状", "变换" };

        private EditorStudioOverlay overlay;
        private ParticleBinding binding;
        private LevelEvent targetEvent;
        private RectTransform root;
        private RectTransform content;
        private int page;
        private float width;
        private float height;

        internal int Version { get; private set; }

        internal static UiParticlePane Build(RectTransform parent, EditorStudioOverlay owner,
            scrParticleDecoration decoration, LevelEvent targetEvent, float x, float y, float w, float h)
        {
            RectTransform rect = CreateRect("ParticlePane", parent, x, y, w, h);
            UiParticlePane pane = rect.gameObject.AddComponent<UiParticlePane>();
            pane.overlay = owner;
            pane.binding = new ParticleBinding(decoration);
            pane.targetEvent = targetEvent;
            pane.root = rect;
            pane.width = w;
            pane.height = h;
            pane.BuildTabs();
            pane.BuildPage();
            return pane;
        }

        private void BuildTabs()
        {
            float tabWidth = width / TabNames.Length;
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                Button button = CreateButton(root, i * tabWidth, 0f, tabWidth, TabHeight,
                    TabNames[i], delegate { page = index; Rebuild(); });
                TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    label.fontSize = 12f;
                    label.color = i == page ? TextPrimary : TextMuted;
                }
            }

            content = CreateRect("Content", root, 0f, TabHeight, width, height - TabHeight);
        }

        private void Rebuild()
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name.StartsWith("Tab", StringComparison.Ordinal))
                {
                    TextMeshProUGUI label = child.GetComponentInChildren<TextMeshProUGUI>();
                    if (label != null)
                    {
                        label.color = child.GetSiblingIndex() == page
                            ? TextPrimary : TextMuted;
                    }
                }
            }
            ClearChildren(content);
            BuildPage();
        }

        private void BuildPage()
        {
            if (!binding.Valid)
            {
                CreateText(content, 6f, 8f, width - 12f, 24f,
                    "这个事件还没有粒子对象", 12f, TextMuted);
                return;
            }

            float y = 4f;
            switch (page)
            {
                case 0:
                    y = AddInt("最大粒子数", y, -1,
                        delegate { return binding.System.main.maxParticles; },
                        delegate(int value) { var m = binding.System.main; m.maxParticles = Mathf.Max(1, value); });
                    y = AddFloat("模拟速度", y, "%",
                        delegate { return binding.System.main.simulationSpeed * 100f; },
                        delegate(float value) { var m = binding.System.main; m.simulationSpeed = Mathf.Max(0.01f, value / 100f); });
                    y = AddFloat("播放时长", y, "秒",
                        delegate { return binding.System.main.duration; },
                        delegate(float value) { var m = binding.System.main; m.duration = Mathf.Max(0.1f, value); });
                    y = AddToggle("循环", y,
                        delegate { return binding.System.main.loop; },
                        delegate(bool value) { var m = binding.System.main; m.loop = value; });
                    y = AddToggle("使用固定随机种子", y,
                        delegate { return !binding.System.useAutoRandomSeed; },
                        delegate(bool value) { binding.System.useAutoRandomSeed = !value; });
                    break;

                case 1:
                    y = AddImageRow("图片", y);
                    y = AddPair("随机纹理平铺", y, string.Empty,
                        delegate { return binding.TextureTiling; },
                        delegate(Vector2 value) { binding.TextureTiling = value; });
                    y = AddPair("开始旋转", y, "°",
                        delegate { return Pair(binding.System.main.startRotation); },
                        delegate(Vector2 value) { var m = binding.System.main; m.startRotation = MakePair(value); });
                    y = AddColor("颜色", y);
                    y = AddPair("粒子持续时间", y, "秒",
                        delegate { return Pair(binding.System.main.startLifetime); },
                        delegate(Vector2 value) { var m = binding.System.main; m.startLifetime = MakePair(value); });
                    y = AddPair("粒子大小", y, "%",
                        delegate
                        {
                            Vector2 size = Pair(binding.System.main.startSize);
                            return new Vector2(size.x * 100f, size.y * 100f);
                        },
                        delegate(Vector2 value)
                        {
                            var m = binding.System.main;
                            m.startSize = MakePair(new Vector2(value.x / 100f, value.y / 100f));
                        });
                    break;

                default:
                    y = AddPair("随时间变速 X", y, "方块",
                        delegate { return Pair(binding.System.velocityOverLifetime.x); },
                        delegate(Vector2 value) { var m = binding.System.velocityOverLifetime; m.x = MakePair(value); });
                    y = AddPair("随时间变速 Y", y, "方块",
                        delegate { return Pair(binding.System.velocityOverLifetime.y); },
                        delegate(Vector2 value) { var m = binding.System.velocityOverLifetime; m.y = MakePair(value); });
                    y = AddPair("生命周期限速", y, "%",
                        delegate { return Pair(binding.System.limitVelocityOverLifetime.limit); },
                        delegate(Vector2 value) { var m = binding.System.limitVelocityOverLifetime; m.limit = MakePair(value); });
                    y = AddPair("随时间旋转", y, "°",
                        delegate { return Pair(binding.System.rotationOverLifetime.z); },
                        delegate(Vector2 value) { var m = binding.System.rotationOverLifetime; m.z = MakePair(value); });
                    y = AddToggle("随时间变色", y,
                        delegate { return binding.ColorOverLifetimeEnabled; },
                        delegate(bool value) { binding.ColorOverLifetimeEnabled = value; });
                    y = AddToggle("随时间缩放", y,
                        delegate { return binding.SizeOverLifetimeEnabled; },
                        delegate(bool value) { binding.SizeOverLifetimeEnabled = value; });
                    y = AddFloat("弧", y, "°",
                        delegate { return binding.ArcDegrees; },
                        delegate(float value) { binding.ArcDegrees = value; });
                    break;
            }
        }

        // ── 行工厂 ─────────────────────────────────────────────────

        private float AddInt(string label, float y, int unused,
            Func<int> read, Action<int> write)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);
            string value = read().ToString(System.Globalization.CultureInfo.InvariantCulture);
            TMP_InputField field = CreateField(content, 6f + LabelWidth,
                y + (RowHeight - FieldHeight) * 0.5f, width - LabelWidth - 18f, value);
            field.onEndEdit.AddListener(delegate(string text)
            {
                int parsed;
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return;
                }
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                write(parsed);
                RestartAll();
                Version++;
            });
            return y + RowHeight;
        }

        private float AddFloat(string label, float y, string unit,
            Func<float> read, Action<float> write)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);

            float unitWidth = string.IsNullOrEmpty(unit) ? 0f : 34f;
            float fieldX = 6f + LabelWidth;
            float fieldWidth = Mathf.Max(40f, width - fieldX - unitWidth - 12f);
            TMP_InputField field = CreateField(content, fieldX,
                y + (RowHeight - FieldHeight) * 0.5f, fieldWidth,
                EditorPropertyPanel.FormatFloat(read()));
            field.onEndEdit.AddListener(delegate(string text)
            {
                float parsed;
                if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return;
                }
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                write(parsed);
                RestartAll();
                Version++;
            });

            if (!string.IsNullOrEmpty(unit))
            {
                CreateText(content, fieldX + fieldWidth + 6f, y,
                    unitWidth, RowHeight, unit, 11f, TextMuted);
            }
            return y + RowHeight;
        }

        private float AddPair(string label, float y, string unit,
            Func<Vector2> read, Action<Vector2> write)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);

            float unitWidth = string.IsNullOrEmpty(unit) ? 0f : 30f;
            float available = width - (6f + LabelWidth) - unitWidth - 16f;
            float fieldWidth = Mathf.Max(38f, (available - PairGap * 2f - 8f) * 0.5f);
            float x = 6f + LabelWidth;
            Vector2 value = read();

            TMP_InputField low = CreateField(content, x,
                y + (RowHeight - FieldHeight) * 0.5f, fieldWidth,
                EditorPropertyPanel.FormatFloat(value.x));
            CreateText(content, x + fieldWidth + 2f, y, 8f, RowHeight, "-", 11f, TextMuted);
            TMP_InputField high = CreateField(content, x + fieldWidth + PairGap + 8f,
                y + (RowHeight - FieldHeight) * 0.5f, fieldWidth,
                EditorPropertyPanel.FormatFloat(value.y));

            low.onEndEdit.AddListener(delegate(string text)
            {
                float parsed;
                if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return;
                }
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                Vector2 current = read();
                write(new Vector2(parsed, current.y));
                RestartAll();
                Version++;
            });
            high.onEndEdit.AddListener(delegate(string text)
            {
                float parsed;
                if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return;
                }
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                Vector2 current = read();
                write(new Vector2(current.x, parsed));
                RestartAll();
                Version++;
            });

            if (!string.IsNullOrEmpty(unit))
            {
                CreateText(content, x + fieldWidth * 2f + PairGap + 16f, y,
                    unitWidth, RowHeight, unit, 11f, TextMuted);
            }
            return y + RowHeight;
        }

        private float AddToggle(string label, float y,
            Func<bool> read, Action<bool> write)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);

            bool value = read();
            float boxX = 6f + LabelWidth;
            RectTransform rect = CreateRect("Toggle", content, boxX,
                y + (RowHeight - 20f) * 0.5f, 20f, 20f);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0.08f);
            hit.raycastTarget = true;
            if (value)
            {
                CreateText(rect, 0f, 0f, 20f, 20f, "✓", 12f, TextPrimary,
                    TextAlignmentOptions.Center);
            }

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                write(!value);
                RestartAll();
                Version++;
                Rebuild();
            });
            return y + RowHeight;
        }

        private float AddColor(string label, float y)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);

            float fieldX = 6f + LabelWidth;
            float fieldWidth = Mathf.Max(60f, width - fieldX - 56f);
            Color value = binding.StartColor;
            TMP_InputField field = CreateField(content, fieldX,
                y + (RowHeight - FieldHeight) * 0.5f, fieldWidth,
                EditorPropertyPanel.ColorToHex(value));
            field.onEndEdit.AddListener(delegate(string text)
            {
                Color parsed;
                if (!EditorPropertyPanel.TryParseHex(text, out parsed))
                {
                    return;
                }
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                binding.StartColor = parsed;
                RestartAll();
                Version++;
                Rebuild();
            });

            RectTransform swatchRect = CreateRect("Swatch", content,
                fieldX + fieldWidth + 6f, y + (RowHeight - FieldHeight) * 0.5f,
                FieldHeight, FieldHeight);
            Image swatch = swatchRect.gameObject.AddComponent<Image>();
            swatch.color = value;
            // **点色块就开调色盘**（和项目里别处的调色盘一致），
            // 旁边那颗「∨」是多余的，去掉。
            swatch.raycastTarget = true;
            Button swatchButton = swatchRect.gameObject.AddComponent<Button>();
            swatchButton.targetGraphic = swatch;
            swatchButton.transition = Selectable.Transition.None;
            swatchButton.onClick.AddListener(delegate
            {
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                OpenColorPicker(swatchRect);
            });

            return y + RowHeight;
        }

        private static readonly Color[] PresetColors =
        {
            Color.white, Color.black, new Color(0.9f, 0.25f, 0.25f),
            new Color(0.95f, 0.6f, 0.2f), new Color(0.95f, 0.9f, 0.3f),
            new Color(0.35f, 0.8f, 0.4f), new Color(0.3f, 0.85f, 0.85f),
            new Color(0.35f, 0.6f, 0.95f), new Color(0.7f, 0.45f, 0.9f)
        };

        /// <summary>
        /// 调色盘：复用参数栏那只 `EditorColorPicker`（拖动只改显示、松手才提交）。
        /// 预设色那排保留着，但主入口是这颗「∨」打开调色盘。
        /// </summary>
        /// <summary>调色盘预览：只改两边颜色值，不重启（拖动中每帧重启会闪）。</summary>
        private void PreviewColor(Color color)
        {
            // 只读闸：调色盘拖动走的是这条路径，之前没拦。
            if (overlay != null && !overlay.AllowEdit())
            {
                return;
            }
            binding.StartColor = color;
            if (targetEvent == null)
            {
                return;
            }
            scrParticleDecoration live =
                scrDecorationManager.GetDecoration(targetEvent) as scrParticleDecoration;
            if (live == null || live.particleSystem == null)
            {
                return;
            }
            ParticleSystem.MainModule main = live.particleSystem.main;
            main.startColor = new ParticleSystem.MinMaxGradient(color);
            ParticleSystemRenderer renderer =
                live.particleSystem.GetComponent<ParticleSystemRenderer>();
            if (renderer != null && renderer.material != null)
            {
                if (renderer.material.HasProperty("_Color"))
                {
                    renderer.material.SetColor("_Color", color);
                }
                if (renderer.material.HasProperty("_TintColor"))
                {
                    renderer.material.SetColor("_TintColor", color);
                }
            }
        }

        private void OpenColorPicker(RectTransform anchor)
        {
            EditorStudioOverlay owner = overlay;
            if (owner == null || owner.PopupLayer == null || anchor == null)
            {
                return;
            }
            RectTransform layer = owner.PopupLayer;

            RectTransform blocker = CreateRect("Blocker", layer, 0f, 0f, 10f, 10f);
            Image blockerImage = blocker.gameObject.AddComponent<Image>();
            blockerImage.color = new Color(0f, 0f, 0f, 0.001f);
            blockerImage.raycastTarget = true;
            FillParent(blocker, layer);
            Button blockerButton = blocker.gameObject.AddComponent<Button>();
            blockerButton.targetGraphic = blockerImage;
            blockerButton.transition = Selectable.Transition.None;

            RectTransform popup = CreateRect("Picker", layer, 0f, 0f,
                EditorColorPicker.BodyWidth, EditorColorPicker.BodyHeight);
            Image background = popup.gameObject.AddComponent<Image>();
            background.color = new Color(0.043f, 0.043f, 0.043f, 1f);
            background.raycastTarget = true;

            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, anchor.position);
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                layer, screen, null, out local);
            float left = local.x + layer.rect.width * 0.5f - EditorColorPicker.BodyWidth * 0.5f;
            float top = layer.rect.height * 0.5f - local.y + 6f;
            SetTopLeft(popup, left, top,
                EditorColorPicker.BodyWidth, EditorColorPicker.BodyHeight);

            EditorColorPicker picker = popup.gameObject.AddComponent<EditorColorPicker>();
            picker.Build(popup, owner.UiFont, binding.StartColor,
                delegate(Color preview)
                {
                    // 拖动途中只改颜色值，不重启粒子（每帧重启会闪）。
                    PreviewColor(preview);
                },
                delegate(Color picked)
                {
                    // **松手不重启粒子。** 拖的时候已经实时改色了，再 Clear+Play
                    // 一次就会「重新喷」（用户反馈）。这里只把最终色落到两边，
                    // 也不 `Version++` —— 那会触发预览重建，同样是重喷。
                    PreviewColor(picked);
                });

            blockerButton.onClick.AddListener(delegate
            {
                Destroy(popup.gameObject);
                Destroy(blocker.gameObject);
                Rebuild();
            });
        }

        private static void FillParent(RectTransform rect, RectTransform parent)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetTopLeft(RectTransform rect, float x, float y,
            float w, float h)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
        }

        /// <summary>图片行：只读文件名 + 文件夹按钮（走原生选图器）。</summary>
        private float AddImageRow(string label, float y)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);

            float fieldX = 6f + LabelWidth;
            const float buttonWidth = 26f;
            const float gap = 6f;
            float textWidth = Mathf.Max(40f, width - fieldX - buttonWidth - gap - 10f);
            string name = binding.TextureName;
            CreateText(content, fieldX, y, textWidth, RowHeight,
                string.IsNullOrEmpty(name) ? "没有选中文件" : name, 11f, TextMuted);

            RectTransform rect = CreateRect("Browse", content,
                fieldX + textWidth + gap, y + (RowHeight - FieldHeight) * 0.5f,
                buttonWidth, FieldHeight);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0.05f);
            hit.raycastTarget = true;
            Sprite icon = NativeFileIcon.Get();
            if (icon != null)
            {
                Image image = CreateRect("Icon", rect, 5f, 5f, 16f, 16f)
                    .gameObject.AddComponent<Image>();
                image.sprite = icon;
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
            else
            {
                CreateText(rect, 0f, 0f, buttonWidth, FieldHeight, "…", 12f, TextMuted,
                    TextAlignmentOptions.Center);
            }

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (overlay != null && !overlay.AllowEdit())
                {
                    return;
                }
                string picked = binding.PickImage();
                if (string.IsNullOrEmpty(picked))
                {
                    return;
                }
                // **必须写回事件数据。** 预览重建时会 `Setup(target)`，
                // 贴图是从事件里那个文件名重新取的 —— 只改材质，下次 Setup
                // 就被覆盖回去（用户看到的就是「选图没变化」）。
                CommitImage(picked);
                Version++;
                Rebuild();
            });
            return y + RowHeight;
        }

        /// <summary>把选中的文件名写进事件的 File 型属性（预览靠它取图）。</summary>
        private void CommitImage(string filename)
        {
            if (targetEvent == null || targetEvent.info == null
                || targetEvent.info.propertiesInfo == null)
            {
                return;
            }
            foreach (System.Collections.Generic.KeyValuePair<string, ADOFAI.PropertyInfo> pair
                in targetEvent.info.propertiesInfo)
            {
                if (pair.Value == null || pair.Value.type != ADOFAI.PropertyType.File)
                {
                    continue;
                }
                if (overlay != null && overlay.PropertyPanel != null)
                {
                    overlay.PropertyPanel.Commit(targetEvent, pair.Key, filename);
                }
                return;
            }
        }

        /// <summary>
        /// 统一提交：重启编辑器预览，并把模块值镜像到关卡里那个活装饰物。
        ///
        /// 面板绑的是 `previewDec`（编辑器预览用它），而主预览渲染的是关卡
        /// 装饰物 —— 只写一份就会出现「编辑器有颜色、主预览没颜色」（用户实测）。
        /// </summary>
        private void RestartAll()
        {
            binding.Restart();
            MirrorToLive();
        }

        private void MirrorToLive()
        {
            if (targetEvent == null || !binding.Valid)
            {
                return;
            }
            scrParticleDecoration live =
                scrDecorationManager.GetDecoration(targetEvent) as scrParticleDecoration;
            if (live == null || live.particleSystem == null)
            {
                return;
            }

            ParticleSystem from = binding.System;
            ParticleSystem to = live.particleSystem;
            if (from == null || to == null || ReferenceEquals(from, to))
            {
                return;
            }

            ParticleSystem.MainModule fm = from.main;
            ParticleSystem.MainModule tm = to.main;
            tm.startColor = fm.startColor;
            tm.startSize = fm.startSize;
            tm.startLifetime = fm.startLifetime;
            tm.startRotation = fm.startRotation;
            tm.startSpeed = fm.startSpeed;

            ParticleSystem.EmissionModule fe = from.emission;
            ParticleSystem.EmissionModule te = to.emission;
            te.rateOverTime = fe.rateOverTime;

            ParticleSystemRenderer fromRenderer = from.GetComponent<ParticleSystemRenderer>();
            ParticleSystemRenderer toRenderer = to.GetComponent<ParticleSystemRenderer>();
            if (fromRenderer != null && toRenderer != null
                && toRenderer.material != null && fromRenderer.sharedMaterial != null)
            {
                Material material = toRenderer.material;
                material.mainTexture = fromRenderer.sharedMaterial.mainTexture;
                material.mainTextureScale = fromRenderer.sharedMaterial.mainTextureScale;
                Color tint = fm.startColor.color;
                if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", tint);
                }
                if (material.HasProperty("_TintColor"))
                {
                    material.SetColor("_TintColor", tint);
                }
            }

            to.Clear();
            to.Play();
        }

        private float AddText(string label, float y, string value)
        {
            CreateText(content, 6f, y, LabelWidth, RowHeight, label, 12f, TextSecondary);
            CreateText(content, 6f + LabelWidth, y, width - LabelWidth - 18f, RowHeight,
                value, 11f, TextMuted);
            return y + RowHeight;
        }

        // ── 曲线最小/最大 ────────────────────────────────────────────

        private static Vector2 Pair(ParticleSystem.MinMaxCurve curve)
        {
            return new Vector2(curve.constantMin, curve.constantMax);
        }

        private static ParticleSystem.MinMaxCurve MakePair(Vector2 value)
        {
            return new ParticleSystem.MinMaxCurve(
                Mathf.Min(value.x, value.y), Mathf.Max(value.x, value.y));
        }

        // ── 基础控件 ───────────────────────────────────────────────

        private Color TextPrimary { get { return UiTheme.Current.TextPrimary; } }
        private Color TextSecondary { get { return UiTheme.Current.TextSecondary; } }
        private Color TextMuted { get { return UiTheme.Current.TextMuted; } }

        private TMP_InputField CreateField(RectTransform parent, float x, float y,
            float w, string value)
        {
            RectTransform rect = CreateRect("Field", parent, x, y, w, FieldHeight);
            rect.gameObject.SetActive(false);

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.04f);
            background.raycastTarget = true;

            TextMeshProUGUI text = CreateStretchedText(rect, 6f, value, 12f);
            TMP_InputField field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.richText = false;
            field.characterLimit = 16;
            field.textViewport = rect;
            field.textComponent = text;
            field.text = value;
            rect.gameObject.SetActive(true);
            return field;
        }

        private Button CreateButton(RectTransform parent, float x, float y,
            float w, float h, string label, Action action)
        {
            RectTransform rect = CreateRect("Tab" + label, parent, x, y, w, h);
            Image hit = rect.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            TextMeshProUGUI text = CreateText(rect, 0f, 0f, w, h, label, 12f,
                TextMuted, TextAlignmentOptions.Center);
            text.raycastTarget = false;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { action(); });
            return button;
        }

        private static TextMeshProUGUI CreateText(Transform parent, float x, float y,
            float w, float h, string value, float size, Color color)
        {
            return CreateText(parent, x, y, w, h, value, size, color,
                TextAlignmentOptions.MidlineLeft);
        }

        private static TextMeshProUGUI CreateText(Transform parent, float x, float y,
            float w, float h, string value, float size, Color color,
            TextAlignmentOptions alignment)
        {
            RectTransform rect = CreateRect("Text", parent, x, y, w, h);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay != null && overlay.UiFont != null)
            {
                text.font = overlay.UiFont;
            }
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        private static TextMeshProUGUI CreateStretchedText(RectTransform parent,
            float inset, string value, float size)
        {
            GameObject go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, 0f);
            rect.offsetMax = new Vector2(-inset, 0f);
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay != null && overlay.UiFont != null)
            {
                text.font = overlay.UiFont;
            }
            text.text = value;
            text.fontSize = size;
            text.color = UiTheme.Current.TextPrimary;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        private static void ClearChildren(RectTransform parent)
        {
            if (parent == null)
            {
                return;
            }
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
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
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }
    }
}
