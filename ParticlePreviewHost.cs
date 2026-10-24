using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 粒子预览的共用宿主：把一件事的粒子渲进一张 RenderTexture。
    ///
    /// 这套流程是从旧 ImGui 粒子编辑器里抽出来的（那边已经验证可用）：
    ///
    ///   1. 模板取 **`editor.particleEditor.previewDec`** —— 游戏原版粒子编辑器
    ///      里那个现成实例。自己 `AddComponent&lt;scrParticleDecoration&gt;()`
    ///      不行：`particleSystem` 是预制体上挂好的，新建出来是 null，
    ///      `Setup` 里一碰就空引用（那边踩过，注释里写着）。
    ///   2. 挪到 `(10000,10000,10000)` 这个隔离点，整棵子树铺到 31 层，
    ///      用只渲染 31 层的相机拍，不会拍到关卡里的东西。
    ///   3. `Setup(target)` 之后再铺一次层（Setup 会新建子物体）。
    ///
    /// 谁需要预览谁持有一个实例：uGUI 浮窗把 `Texture` 贴到 RawImage 上。
    /// </summary>
    internal sealed class ParticlePreviewHost
    {
        internal const int PreviewWidth = 476;
        internal const int PreviewHeight = 476;
        private const int PreviewLayer = 31;
        private static readonly Vector3 PreviewOrigin = new Vector3(10000f, 10000f, 10000f);

        private GameObject root;
        private scrParticleDecoration decoration;
        private Camera camera;
        private RenderTexture texture;
        private LevelEvent target;
        /// <summary>预览对象是不是我们建的（决定 Release 时要不要销毁）。</summary>
        private bool ownsRoot;

        // 借来的对象（原生 previewDec）关窗要原样还回去，否则原生粒子编辑器
        // 再用它时位置/父级/层级全错。
        private Transform originalParent;
        private Vector3 originalPosition;
        private Quaternion originalRotation;
        private Vector3 originalScale;
        private bool originalActive;
        private readonly System.Collections.Generic.List<GameObject> layerTargets
            = new System.Collections.Generic.List<GameObject>();
        private readonly System.Collections.Generic.List<int> layerValues
            = new System.Collections.Generic.List<int>();
        private scrParticleDecoration templateSource;
        private int desiredWidth = PreviewWidth;
        private int desiredHeight = PreviewHeight;

        internal RenderTexture Texture { get { return texture; } }

        /// <summary>供面板绑定的那份预览装饰物（复制体，不是原生 previewDec）。</summary>
        internal scrParticleDecoration Decoration { get { return decoration; } }

        /// <summary>同一件事重复调用直接复用；换了事件才重建。</summary>
        internal void Ensure(LevelEvent levelEvent, int width, int height)
        {
            if (levelEvent == null)
            {
                return;
            }
            if (target == levelEvent && root != null && texture != null)
            {
                return;
            }
            Release();
            target = levelEvent;
            desiredWidth = Mathf.Max(64, width);
            desiredHeight = Mathf.Max(64, height);

            try
            {
                scnEditor editor = scnEditor.instance;
                scrParticleDecoration template = editor == null || editor.particleEditor == null
                    ? null : editor.particleEditor.previewDec;

                if (template != null)
                {
                    // 复制一份来渲染：直接挪原版对象时它一粒子都不喷（层级/父子
                    // 关系都跟原生面板绑着）。复制之后再把面板上改过的参数
                    // **手工同步过来**（见 SyncFrom），这样颜色/大小也能生效。
                    // **直接用原版预览对象**（旧 ImGui 编辑器就是这么做的）。
                    // 复制体会被关卡的时间轴更新牵连，拖播放头时主预览卡死。
                    root = template.gameObject;
                    originalParent = root.transform.parent;
                    originalPosition = root.transform.position;
                    originalRotation = root.transform.rotation;
                    originalScale = root.transform.localScale;
                    originalActive = root.activeSelf;
                    root.SetActive(true);
                    // 从原生面板的父级摘出来，否则它会跟着面板显隐。
                    root.transform.SetParent(null, true);
                    decoration = template;
                }
                else
                {
                    root = new GameObject("AES.ParticlePreview");
                    decoration = root.AddComponent<scrParticleDecoration>();
                }
                ownsRoot = false;
                templateSource = template;

                root.transform.position = PreviewOrigin;
                root.SetActive(true);
                ApplyPreviewLayer();

                decoration.manager = scrDecorationManager.instance;
                decoration.decType = DecorationType.Particle;

                bool setupOk;
                decoration.Setup(levelEvent, out setupOk);
                ApplyPreviewLayer();

                // 面板改的是 `previewDec` 上的模块；复制体要手工同步一遍，
                // 否则颜色/大小这些永远停在事件里的旧值。

                // **把副本从关卡更新里摘出去。**
                //
                // 复制出来的对象仍然带着装饰物脚本，拖动播放头时游戏会按时间
                // 重算所有装饰物，它被一并重建 —— 主预览就卡住（实测）。
                // 粒子本身由 `ParticleSystem` 独立驱动，禁用脚本不影响喷射。

                texture = new RenderTexture(desiredWidth, desiredHeight, 16,
                    RenderTextureFormat.ARGB32);
                texture.Create();

                GameObject cameraObject = new GameObject("AES.ParticlePreviewCamera");
                cameraObject.transform.position = PreviewOrigin + new Vector3(0f, 0f, -8f);
                cameraObject.transform.LookAt(PreviewOrigin);
                camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                // 和面板底色同一档（#0B0B0B）—— 原来那点蓝是旧编辑器窗口的
                // 配色，贴到浮窗里会和左右两栏的灰黑打架。
                camera.backgroundColor = new Color(0.043f, 0.043f, 0.043f, 1f);
                camera.cullingMask = 1 << PreviewLayer;
                camera.targetTexture = texture;
                camera.fieldOfView = 40f;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 40f;
                camera.enabled = true;

                ParticleSystem system = decoration.particleSystem;
                if (system != null)
                {
                    ParticleSystem.MainModule main = system.main;
                    main.useUnscaledTime = true;
                    decoration.ResetParticle(levelEvent, true);
                    system.Play();
                }
            }
            catch (System.Exception exception)
            {
                Main.Logger.Log("[粒子预览] 建预览失败：" + exception);
                Release();
            }
        }

        /// <summary>参数改过之后按新数据重建（换事件也走这里）。</summary>
        internal void Rebuild(LevelEvent levelEvent)
        {
            // **原地重建，不换对象。**
            //
            // 之前是销毁旧副本再建新的：面板攥着旧系统，提交一次颜色就变成
            // 「没有粒子对象」；窗口每帧换绑又会打断连续的调色操作。
            // 现在同一个对象上重跑 Setup，身份不变，面板不用换绑。
            if (root != null && decoration != null && levelEvent == target
                && decoration.particleSystem != null)
            {
                // **重建前记下面板上改过的值，Setup 之后再盖回去。**
                // Setup 会按事件数据把模块重置成谱面原值 —— 换图后颜色变白、
                // 面板却还显示红，就是这个原因。
                ParticleSystem system = decoration.particleSystem;
                ParticleSystem.MainModule before = system.main;
                Color savedColor = before.startColor.color;
                ParticleSystem.MinMaxCurve savedSize = before.startSize;
                ParticleSystem.MinMaxCurve savedLifetime = before.startLifetime;
                ParticleSystem.MinMaxCurve savedRotation = before.startRotation;
                ParticleSystem.MinMaxCurve savedSpeed = before.startSpeed;

                bool setupOk;
                decoration.Setup(levelEvent, out setupOk);

                ParticleSystem.MainModule after = system.main;
                after.startColor = new ParticleSystem.MinMaxGradient(savedColor);
                after.startSize = savedSize;
                after.startLifetime = savedLifetime;
                after.startRotation = savedRotation;
                after.startSpeed = savedSpeed;
                // 材质颜色也补一次（着色器不吃顶点色，颜色在材质上）。
                ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.material != null)
                {
                    if (renderer.material.HasProperty("_Color"))
                    {
                        renderer.material.SetColor("_Color", savedColor);
                    }
                    if (renderer.material.HasProperty("_TintColor"))
                    {
                        renderer.material.SetColor("_TintColor", savedColor);
                    }
                }

                ApplyPreviewLayer();
                system.Clear();
                system.Play();
                return;
            }

            target = null;
            Ensure(levelEvent, desiredWidth, desiredHeight);
        }

        /// <summary>拖动播放头期间临时藏起预览对象（它可能被游戏侧一并重建）。</summary>
        internal void SetVisible(bool value)
        {
            if (root != null && root.activeSelf != value)
            {
                root.SetActive(value);
            }
        }

        internal void Release()
        {
            if (root != null)
            {
                if (ownsRoot)
                {
                    Object.Destroy(root);
                }
                else
                {
                    // **借来的对象要原样还回去**：父级、位姿、显隐、层级。
                    // 不还的话，原生粒子编辑器下次用它时位置和父子关系全错。
                    RestoreOriginal();
                }
                root = null;
            }
            decoration = null;
            if (camera != null)
            {
                Object.Destroy(camera.gameObject);
                camera = null;
            }
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
                texture = null;
            }
            target = null;
        }

        /// <summary>设置预览层，第一次调用时记录原始层级。</summary>
        private void ApplyPreviewLayer()
        {
            if (root == null)
            {
                return;
            }
            if (layerTargets.Count == 0)
            {
                CaptureLayers(root.transform);
            }
            SetLayerRecursive(root.transform, PreviewLayer);
        }

        private void CaptureLayers(Transform node)
        {
            layerTargets.Add(node.gameObject);
            layerValues.Add(node.gameObject.layer);
            for (int i = 0; i < node.childCount; i++)
            {
                CaptureLayers(node.GetChild(i));
            }
        }

        /// <summary>把借来的预览对象恢复到借用前的状态。</summary>
        private void RestoreOriginal()
        {
            if (templateSource == null)
            {
                return;
            }
            Transform target = templateSource.transform;
            for (int i = 0; i < layerTargets.Count && i < layerValues.Count; i++)
            {
                if (layerTargets[i] != null)
                {
                    layerTargets[i].layer = layerValues[i];
                }
            }
            layerTargets.Clear();
            layerValues.Clear();

            target.SetParent(originalParent, true);
            target.position = originalPosition;
            target.rotation = originalRotation;
            target.localScale = originalScale;
            templateSource.gameObject.SetActive(originalActive);
        }

        private static void SetLayerRecursive(Transform node, int layer)
        {
            node.gameObject.layer = layer;
            for (int i = 0; i < node.childCount; i++)
            {
                SetLayerRecursive(node.GetChild(i), layer);
            }
        }
    }
}
