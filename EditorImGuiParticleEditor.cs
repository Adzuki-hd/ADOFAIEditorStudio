using System;
using System.Collections.Generic;
using ADOFAI;
using ImGuiNET;
using UnityEngine;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 粒子编辑器（复刻原版那扇）。
    ///
    /// **为什么自己画**：原版那扇是原生 uGUI 面板，挂在 `levelEditorScene`
    /// （canvas order=1）上。工作台两层画布（ImGui 32767 / uGUI 30000）把它压在
    /// 下面；试过"两层都压到 0"和"整个关掉工作台"，面板仍然看不见 —— 说明那条
    /// 路走不通，只能自己画。
    ///
    /// **复刻规格来自反编译**（`ADOFAI.Editor.ParticleEditor.ParticleEditor`）：
    /// 三个页签、每个页签里那串属性名，一个不多一个不少。那些属性在正常参数栏里
    /// 全是 `control: Hidden`（所以平时看不到），只有这里会露出来。
    /// </summary>
    internal sealed class EditorImGuiParticleEditor
    {
        private const float WindowWidth = 500f;
        private const float LabelWidth = 150f;
        private const float RowGap = 4f;

        /// <summary>面板开着没有。由参数栏那个「编辑」按钮置起来。</summary>
        internal bool Visible;

        /// <summary>
        /// 窗口在**设计空间**里的矩形。桥接那边拿它铺一块 uGUI 点击遮挡 ——
        /// 不铺的话鼠标事件会同时落到游戏里（拖窗口时把后面的画面一起拖走）。
        /// </summary>
        internal Rect Rect;

        /// <summary>
        /// 关掉这一层（Esc 和「关闭」按钮都走这里）。
        /// **只是关它自己**，不动工作台 —— 退出要逐层来。
        /// </summary>
        internal void Close()
        {
            Visible = false;
            Rect = new Rect(0f, 0f, 0f, 0f);
            ReleasePreview();
        }

        private int _tab;

        // ------------------------------------------------------------ 预览

        /// <summary>预览区尺寸（像素）。正方形，边长 = 窗口内容宽度（500 - 内边距）。</summary>
        private const int PreviewWidth = 476;
        private const int PreviewHeight = 476;
        /// <summary>预览专用的层：相机只渲染这一层，游戏里的东西进不来。</summary>
        private const int PreviewLayer = 31;
        /// <summary>预览对象摆得离场景很远，避免和真的谱面装饰物撞上。</summary>
        private static readonly Vector3 PreviewOrigin = new Vector3(10000f, 10000f, 10000f);

        private GameObject _previewRoot;
        private scrParticleDecoration _previewDecoration;
        private Camera _previewCamera;
        private RenderTexture _previewTexture;
        private IntPtr _previewTextureId;
        private LevelEvent _previewEvent;
        /// <summary>上一次重建预览时参数栏的提交计数（用来判断参数改过没有）。</summary>
        private int _previewCommitVersion = -1;

        /// <summary>
        /// 建/换预览对象。
        ///
        /// **做法和原版一致**：造一个 `scrParticleDecoration` 调 `Setup(event)`，
        /// 它自己会建 ParticleSystem。区别只在"看"的方式 —— 原版是把它摆在游戏
        /// 相机前面、透过半透明窗口看；我们是给它配一台**只渲染预览层**的相机，
        /// 渲进 RenderTexture，再贴到窗口底下。
        /// </summary>
        private void EnsurePreview(LevelEvent target)
        {
            if (_previewEvent == target && _previewRoot != null)
            {
                return;
            }
            ReleasePreview();
            _previewEvent = target;
            string step = "取原版预览对象";
            try
            {
                // **照着原版那个预览对象 clone 一份。**
                //
                // 自己 `AddComponent<scrParticleDecoration>()` 是不行的：那个组件的
                // `particleSystem` 是**预制体上挂好的**，新建出来的对象上是 null，
                // `Setup` 里一碰就 NullReferenceException（实测）。
                // 原版 `particleEditor.previewDec` 就是个现成的实例，clone 它最稳。
                scnEditor editor = scnEditor.instance;
                scrParticleDecoration template = editor == null || editor.particleEditor == null
                    ? null : editor.particleEditor.previewDec;

                step = "建 GameObject";
                if (template != null)
                {
                    _previewRoot = (GameObject)UnityEngine.Object.Instantiate(template.gameObject);
                    _previewRoot.name = "AES.ParticlePreview";
                    _previewRoot.transform.SetParent(null, false);
                    _previewDecoration = _previewRoot.GetComponent<scrParticleDecoration>();
                }
                else
                {
                    _previewRoot = new GameObject("AES.ParticlePreview");
                    _previewDecoration = _previewRoot.AddComponent<scrParticleDecoration>();
                }

                step = "摆位置 / 铺层";
                _previewRoot.transform.position = PreviewOrigin;
                _previewRoot.SetActive(true);
                SetLayerRecursive(_previewRoot, PreviewLayer);

                step = "设 manager / decType";
                _previewDecoration.manager = scrDecorationManager.instance;
                _previewDecoration.decType = DecorationType.Particle;

                step = "Setup";
                bool setupOk;
                _previewDecoration.Setup(target, out setupOk);
                step = "再次铺层";
                SetLayerRecursive(_previewRoot, PreviewLayer);

                step = "建 RenderTexture";                _previewTexture = new RenderTexture(PreviewWidth, PreviewHeight, 16,
                    RenderTextureFormat.ARGB32);
                _previewTexture.Create();
                _previewTextureId = EditorImGuiBridge.RegisterTexture(_previewTexture);

                GameObject cameraObject = new GameObject("AES.ParticlePreviewCamera");
                cameraObject.transform.position = PreviewOrigin + new Vector3(0f, 0f, -6f);
                cameraObject.transform.LookAt(PreviewOrigin);
                _previewCamera = cameraObject.AddComponent<Camera>();
                _previewCamera.clearFlags = CameraClearFlags.SolidColor;
                _previewCamera.backgroundColor = new Color(0.055f, 0.065f, 0.085f, 1f);
                _previewCamera.cullingMask = 1 << PreviewLayer;
                _previewCamera.targetTexture = _previewTexture;
                _previewCamera.fieldOfView = 35f;
                _previewCamera.nearClipPlane = 0.05f;
                _previewCamera.farClipPlane = 40f;
                _previewCamera.enabled = true;

                ParticleSystem system = _previewDecoration.particleSystem;
                if (system != null)
                {
                    ParticleSystem.MainModule main = system.main;
                    main.useUnscaledTime = true;
                    _previewDecoration.ResetParticle(target, true);
                    system.Play();
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Log("[粒子编辑器] 建预览失败（步骤=" + step + "）："
                    + exception);
                ReleasePreview();
            }
        }

        /// <summary>参数改过之后按新数据重建预览。</summary>
        private void RefreshPreview(LevelEvent target)
        {
            if (_previewDecoration == null || _previewEvent != target)
            {
                return;
            }
            try
            {
                bool setupOk;
                _previewDecoration.Setup(target, out setupOk);
                // Setup 可能新建子物体，层要重新铺一遍，否则它们不会进预览相机。
                SetLayerRecursive(_previewRoot, PreviewLayer);
                ParticleSystem system = _previewDecoration.particleSystem;
                if (system != null)
                {
                    ParticleSystem.MainModule main = system.main;
                    main.useUnscaledTime = true;
                    _previewDecoration.ResetParticle(target, true);
                    system.Play();
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Log("[粒子编辑器] 刷新预览失败：" + exception.Message);
            }
        }

        private void ReleasePreview()
        {
            _previewEvent = null;
            _previewTextureId = IntPtr.Zero;
            if (_previewCamera != null)
            {
                _previewCamera.targetTexture = null;
                UnityEngine.Object.Destroy(_previewCamera.gameObject);
                _previewCamera = null;
            }
            if (_previewRoot != null)
            {
                UnityEngine.Object.Destroy(_previewRoot);
                _previewRoot = null;
            }
            _previewDecoration = null;
            if (_previewTexture != null)
            {
                _previewTexture.Release();
                UnityEngine.Object.Destroy(_previewTexture);
                _previewTexture = null;
            }
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            root.layer = layer;
            Transform t = root.transform;
            int count = t.childCount;
            for (int i = 0; i < count; i++)
            {
                SetLayerRecursive(t.GetChild(i).gameObject, layer);
            }
        }

        /// <summary>
        /// 三个页签的属性清单 —— **直接从原版 `ParticleEditor.DrawSettings` 抄的**。
        /// 顺序也照抄，玩家换过来不会找不到东西。
        /// </summary>
        private static readonly string[][] TabProperties =
        {
            new[]
            {
                "maxParticles", "simulationSpace", "simulationSpeed",
                "playDuration", "loop", "randomSeed"
            },
            new[]
            {
                "decorationImage", "randomTextureTiling", "startRotation",
                "color", "particleLifetime", "particleSize",
                "shapeType", "shapeRadius", "emissionRate"
            },
            new[]
            {
                "velocity", "velocityLimitOverLifetime", "rotationOverTime",
                "colorOverLifetime", "sizeOverLifetime", "arc", "arcMode"
            }
        };

        private static readonly string[] TabNames = { "通用", "形状", "变换" };

        internal void Draw(EditorImGuiInspector inspector)
        {
            if (!Visible || inspector == null)
            {
                Rect = new Rect(0f, 0f, 0f, 0f);
                return;
            }
            LevelEvent target = inspector.Target;
            if (target == null || target.eventType != LevelEventType.AddParticle)
            {
                Visible = false;
                Rect = new Rect(0f, 0f, 0f, 0f);
                ReleasePreview();
                return;
            }

            // **高度交给 ImGui 自动撑。**
            //
            // 手算过两次：少算标题栏、少算行高，结论都是底部被裁。
            // `AlwaysAutoResize` 让窗口贴合真实内容，宽度用 size constraints 锁死。
            NVec2 display = ImGui.GetIO().DisplaySize;

            ImGui.SetNextWindowPos(
                new NVec2((display.X - WindowWidth) * 0.5f, 24f), ImGuiCond.Appearing);
            ImGui.SetNextWindowSizeConstraints(
                new NVec2(WindowWidth, 0f),
                // 上限放宽到几乎整屏：正方形预览（边长=内容宽度736）让窗口很高，
                // 上限卡在 display.Y-96 会把底部裁掉。
                new NVec2(WindowWidth, display.Y - 32f));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToVec4(Palette.Panel));
            ImGui.PushStyleColor(ImGuiCol.TitleBg, ToVec4(Palette.SurfaceBand));
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ToVec4(Palette.SurfaceBand));
            ImGui.PushStyleColor(ImGuiCol.Border, ToVec4(Palette.PanelRule));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(12f, 10f));

            bool open = ImGui.Begin("粒子编辑器###aes_particle_editor",
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);
            if (!open)
            {
                ImGui.End();
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(4);
                Visible = false;
                Rect = new Rect(0f, 0f, 0f, 0f);
                ReleasePreview();
                return;
            }

            // 记下窗口矩形，交给桥接铺点击遮挡。
            NVec2 windowPos = ImGui.GetWindowPos();
            NVec2 windowSize = ImGui.GetWindowSize();
            Rect = new Rect(windowPos.X, windowPos.Y, windowSize.X, windowSize.Y);

            try
            {
                // **先塞一个够宽的占位。**
                //
                // `AlwaysAutoResize` 是按内容撑的，而 `SetNextWindowSizeConstraints`
                // 在自动撑的窗口上**不保证**会顶到最小宽度 —— 实测窗口只有 430 宽，
                // 右列的值被窗口边缘切掉。这个 1 像素高的占位把内容宽度顶到目标值，
                // 自动撑就跟着够了。
                ImGui.Dummy(new NVec2(WindowWidth - 26f, 1f));

                DrawPlayback(target);
                ImGui.Separator();
                DrawTabs();

                // **行直接排在窗口里，不套子窗口。**
                //
                // 套过一层固定高度的子窗口，高度靠行数估算 —— 估少了就把最后一行
                // 裁掉，还把滚动条关了，用户根本看不出下面还有没有东西。
                // 直接用窗口自身：`AlwaysAutoResize` 会按真实内容撑高，裁不掉。
                // 窗口收窄到 500 之后两列会挤（每列只剩 240），改回单列 ——
                // 预览是正方形，行区高出来的那点相比预览可以忽略。
                string[] names = TabProperties[_tab];
                for (int i = 0; i < names.Length; i++)
                {
                    DrawRow(inspector, target, names[i]);
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextUnformatted("预览");
                ImGui.Spacing();

                bool justCreated = _previewEvent != target || _previewRoot == null;
                EnsurePreview(target);
                if (justCreated)
                {
                    _previewCommitVersion = inspector.CommitVersion;
                }
                else if (_previewCommitVersion != inspector.CommitVersion)
                {
                    // 参数改过 → 按新数据重建预览，否则画面还是改之前的。
                    _previewCommitVersion = inspector.CommitVersion;
                    RefreshPreview(target);
                }
                DrawPreviewImage();
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(4);
            }
        }

        private void DrawPlayback(LevelEvent target)
        {
            scrParticleDecoration decoration = null;
            try
            {
                if (scrDecorationManager.instance != null)
                {
                    decoration = scrDecorationManager.GetDecoration(target) as scrParticleDecoration;
                }
            }
            catch
            {
                decoration = null;
            }
            ParticleSystem system = decoration == null ? null : decoration.particleSystem;

            const float gap = 6f;
            float buttonWidth = (WindowWidth - 24f - gap * 3f) * 0.25f;

            bool playing = system != null && system.isPlaying;
            ImGui.BeginDisabled(system == null);
            if (ImGui.Button((playing ? "暂停" : "播放") + "##pe_play",
                new NVec2(buttonWidth, 0f)))
            {
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
            }
            ImGui.SameLine(0f, gap);
            if (ImGui.Button("停止##pe_stop", new NVec2(buttonWidth, 0f)))
            {
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.time = 0f;
            }
            ImGui.SameLine(0f, gap);
            if (ImGui.Button("重置##pe_reset", new NVec2(buttonWidth, 0f)))
            {
                system.Clear();
                system.time = 0f;
            }
            ImGui.EndDisabled();
            ImGui.SameLine(0f, gap);
            if (ImGui.Button("关闭##pe_close", new NVec2(buttonWidth, 0f)))
            {
                Close();
            }
        }

        /// <summary>
        /// 把预览用的 RenderTexture 贴到窗口底下 —— **正方形，边长 = 可用宽度**。
        /// 铺满宽度所以没有左右黑边；高度跟着宽度走，不会压成长方形。
        /// </summary>
        private void DrawPreviewImage()
        {
            if (_previewTextureId == IntPtr.Zero || _previewTexture == null)
            {
                return;
            }
            NVec2 origin = ImGui.GetCursorScreenPos();
            float side = ImGui.GetContentRegionAvail().X;
            ImGui.GetWindowDrawList().AddImage(_previewTextureId, origin,
                new NVec2(origin.X + side, origin.Y + side));
            ImGui.Dummy(new NVec2(side, side));
        }

        private void DrawTabs()
        {
            for (int i = 0; i < TabNames.Length; i++)
            {
                if (i > 0)
                {
                    ImGui.SameLine(0f, 6f);
                }
                bool selected = _tab == i;
                if (selected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, ToVec4(Palette.Selected));
                }
                if (ImGui.Button(TabNames[i] + "##pe_tab" + i, new NVec2(84f, 0f)))
                {
                    _tab = i;
                }
                if (selected)
                {
                    ImGui.PopStyleColor();
                }
            }
            ImGui.Spacing();
        }

        private void DrawRow(EditorImGuiInspector inspector, LevelEvent target, string key)
        {
            if (target.info == null || target.info.propertiesInfo == null)
            {
                return;
            }
            ADOFAI.PropertyInfo info;
            if (!target.info.propertiesInfo.TryGetValue(key, out info) || info == null)
            {
                return;
            }

            float startX = ImGui.GetCursorPosX();
            float avail = ImGui.GetContentRegionAvail().X;
            float valueWidth = Mathf.Max(60f, avail - LabelWidth);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(LabelOf(key));
            // **从"这一格自己的起点"量**，不是从窗口左边量 —— 两列表格里
            // 右列起点在屏幕中部，按窗口量会把值画到左边去。
            ImGui.SameLine(startX + LabelWidth);
            inspector.DrawValueFor(target, info, valueWidth);
            ImGui.Dummy(new NVec2(0f, RowGap));
        }

        /// <summary>
        /// 属性标签：先用原版的本地化 key，取不到就退回字段名。
        /// 那批 Hidden 属性大多没有 key，所以英文名也不奇怪（原版同样如此）。
        /// </summary>
        private static string LabelOf(string key)
        {
            try
            {
                string localized = RDString.Get("editor.AddParticle." + key);
                if (!string.IsNullOrWhiteSpace(localized)
                    && !string.Equals(localized, "editor.AddParticle." + key, StringComparison.Ordinal))
                {
                    return localized;
                }
            }
            catch
            {
                // 没这个 key 的字段就用原名。
            }
            return key;
        }
    }
}
