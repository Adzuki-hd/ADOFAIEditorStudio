using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityModManagerNet;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;
using ModLogger = UnityModManagerNet.UnityModManager.ModEntry.ModLogger;
using ModSettings = UnityModManagerNet.UnityModManager.ModSettings;

namespace ADOFAIEditorStudio
{
    public static class Main
    {
        public static ModEntry Mod { get; private set; }
        public static ModLogger Logger { get; private set; }
        public static Settings Settings { get; private set; }
        public static EditorStudioOverlay Overlay { get; private set; }

        /// <summary>
        /// 逐动作探针日志的开关：`Settings.xml` 里的 VerboseDiagnostics。
        ///
        /// 这些日志每条对应一次用户动作（点一格、翻一页、起播一次），不是每帧，
        /// 排查时非常有用；对正常使用纯属噪声，所以统一挂在这个开关后面。
        /// </summary>
        internal static bool VerboseLog
        {
            get { return Logger != null && Settings != null && Settings.VerboseDiagnostics; }
        }

        public static void Load(ModEntry modEntry)
        {
            Mod = modEntry;
            Logger = modEntry.Logger;
            Settings = ModSettings.Load<Settings>(modEntry);
            modEntry.OnToggle = OnToggle;
            modEntry.OnUnload = OnUnload;
            EditorPlaybackBridge.InstallInputPatch();
            // 版本闸门。低于清单要求的 min 就自动下载新版、
            // 覆盖、重启游戏，用户不用做任何事。
            VersionGate.Check(modEntry);
            // 清掉上一次崩溃留下的联机会话副本。
            Multiplayer.ChartTransfer.SweepLeftoversInBackground();
        }

        /// <summary>
        /// mod 被卸载（关游戏也走这条）：**把没存的布局静默写一份**。
        ///
        /// 这里**弹不出 UI** —— 退出流程里没人看得到窗口。所以只能直接保存。
        /// 总比"在时间轴上摆了一晚上、关游戏全丢"好。
        ///
        /// 用户按过 `Ctrl+Alt+S`（或者菜单里的「保存工程」）的话这儿是空操作 ——
        /// 只有真存在未保存改动时才写。
        /// </summary>
        private static bool OnUnload(ModEntry modEntry)
        {
            try
            {
                EditorStudioOverlay overlay = Overlay;
                if (overlay != null)
                {
                    overlay.SaveProjectBeforeQuit();
                }
            }
            catch (Exception exception)
            {
                if (Logger != null)
                {
                    Logger.Warning("[工程] 关游戏前自动保存失败：" + exception.Message);
                }
            }
            return true;   // 返回 true 表示"可以卸载"
        }

        private static bool OnToggle(ModEntry modEntry, bool toggle)
        {
            if (toggle)
            {
                if (Settings == null)
                {
                    Settings = ModSettings.Load<Settings>(modEntry);
                }

                if (Overlay == null)
                {
                    var host = new GameObject("ADOFAIEditorStudio.Host");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    Overlay = host.AddComponent<EditorStudioOverlay>();

                    // 编辑器左下角那颗「打开工作台」的按钮：自己一块画布，
                    // 位置和配色都照原生底栏那一排小按钮。
                    AesOpenButton.Install(host.transform);
                }

                Overlay.Configure(Settings);

                // 三个记录器是排查轨道/行星问题的调试仪器(行星角度 / 相机路径 / 球轨迹),
                // 会各自 Harmony patch 一个游戏方法。它们只在按下热键后才写文件,输出也没有
                // 任何代码在读,所以默认不挂载 —— 对一个公开发布的 mod 来说,无条件 patch
                // 游戏方法只为产出没人看的日志是不合适的。
                //
                // 需要诊断时把 Settings.xml 里的 VerboseDiagnostics 打开即可,热键与之前相同
                // (行星角度 Ctrl+Shift+A,相机路径 Ctrl+Shift+C)。
                if (Settings.VerboseDiagnostics)
                {
                    PlanetAngleRecorder.Ensure();
                    CameraPathRecorder.Ensure();
                    // 球轨迹记录器：逐帧写下每颗球**各自**的位置，用来查长按里
                    // 那颗球的轨迹和双球角度差。它 patch 的是 scrPlanet.LateUpdate，
                    // 输出 CSV 会长到几百 MB，所以和上面两个一样只在诊断开关
                    // 打开时挂载。双球角度那条还没收，需要时把 Settings.xml 里的
                    // VerboseDiagnostics 打开即可。
                    PlanetTrajectoryRecorder.Ensure();
                }

                Logger.Log("ADOFAI Editor Studio " + Mod.Version + " loaded");
            }
            else
            {
                SaveSettings();

                // 模组被卸载时把联机会话收干净：删掉会话副本、
                // 断开连接。不做的话那个副本会一直留在谱面目录里。
                Multiplayer.MultiplayerSession.Stop();

                if (Overlay != null)
                {
                    UnityEngine.Object.Destroy(Overlay.gameObject);
                    Overlay = null;
                }
            }

            return true;
        }

        internal static void SaveSettings()
        {
            if (Settings != null && Mod != null)
            {
                ModSettings.Save(Settings, Mod);
            }
        }
    }

    public class Settings : ModSettings
    {
        public bool LockPreviewZoom = true;
        // 把预览视图锁在谱面自带的取景上。它与缩放锁分开，
        // 这样作者可以放开缩放，同时仍让相机停在谱面指定的位置
        // （反过来也一样）；工具栏用一个按钮同时控制两者。
        public bool LockPreviewPosition = true;
        // 对看不见的装饰物跳过每帧的 transform 写入。
        //
        // 推送 transform 的开销与它的 CPU 成本不成比例：去掉任意单个
        // setter 没带来任何变化，而整个去掉一个装饰物的写入块，
        // 在密集谱面上大约回收了 50ms。开销来自 Unity 对被动过对象的延迟
        // 处理，每个装饰物只付一次，与改了多少个属性无关。所以真正的
        // 杠杆是「碰了多少个装饰物」。
        //
        // hidden：作者填的 `visible` 标记为隐藏，且当前没有在渲染。
        // transparent：不透明度为 0。光看作者标记不够——在 Not Too
        //   Long 谱面上，1253 个装饰物里有 1001 个作者标记为可见，而每帧写入的 646 个里有 490 个
        //   不透明度是 0。在那里实测：20fps -> 70fps+。
        //
        // 不透明度和可见性仍然每帧写入，所以装饰物重新变
        // 可见或重新变不透明时，会在同一帧就被放到正确位置。
        public bool CullHiddenDecorations = true;
        public bool CullTransparentDecorations = true;
        // 装饰物探针（**默认关**，设置 → 开发者里打开）：预览里一旦有
        // 「求值说该显示、组件却没画」的装饰物，就往日志写一行
        // （tag / 图片 / 渲染器 / sprite / mesh）；每 2 秒还有一条汇总。
        // 这是排查「画面里装饰物不见了」的唯一可靠证据 —— 只看代码分不清
        // 是求值判了隐藏，还是组件上的图像被清掉了。
        //
        // **默认关**的理由：它是诊断工具，不是功能。510 件装饰物的谱面上，
        // 汇总那一条就是几百个 tag，正式版一直开着等于往玩家日志里灌噪声。
        // 排查时到设置里打开即可，和其它探针（轨道探针等）保持一致。
        public bool DecorationProbe = false;
        // 轨道探针（默认关）：逐 0.5 秒把「当前格 + 几个固定格」的渲染器
        // 状态写进日志 —— 渲染器开关、屏幕坐标、颜色 alpha、排序层。
        // 用来区分「轨道被相机甩出屏幕」和「轨道被画成透明/没画」。
        public bool TrackProbe = false;
        // 时间轴定位以原生引擎优先。旧的静态求值器
        // 只作为兼容回退保留，不是默认路径。
        public bool UseStaticTimelineEvaluation = false;
        public bool UseLegacyTimelineScrubFallback = false;
        // 输出 [AudioSync] / [PlanetState] / [VfxQueue] / [ConductorState]
        // 这些 trace，用来诊断「拖动定位后再恢复播放」的缺陷。默认关闭：它们
        // 很吵，只在排查特定播放问题时才需要。
        public bool VerboseDiagnostics = false;

        // 预览时**完全不应用装饰物遮罩**。默认关：遮罩已改为只对屏幕内的
        // 装饰物生效，常态下开销可接受。只有当某张谱遮罩极多、帧率仍不够时，
        // 才需要打开它彻底关掉遮罩。
        //
        // 遮罩一旦生效，原生会对每件带遮罩的装饰物逐帧重合成一张 RT
        // （`ApplyAlphaMaskToRT`）。杪夏这类谱在上百件遮罩同时可见时，
        // 单帧要 200ms 以上，帧率掉到 4 帧。关掉后这些装饰物按普通贴图画，
        // 代价是「该被遮住的部分会露出来」。
        public bool SimplifyDecorationMasks = false;

        // ── uGUI 迁移：已经全部完成 ─────────────────────────────────────
        //
        // 原来的规则是「迁完一块就删掉对应的 ImGui 实现与开关，最终成品里不再有
        // ImGui」。2026-10-24 收尾：最后四扇窗（说明 / 版本说明 / 创建工程 /
        // 悬浮球）都换成了 uGUI，`EditorImGuiBridge`、`EditorImGuiDraw` 与
        // ImGui.NET 依赖已整体删除。

        // 预览时用**老的 SpriteMask 管线**（`disableV15Features = true`）。
        //
        // 原生两套遮罩实现：V15 那套对每件可见遮罩件逐帧重合成 RT（贵到 4 帧），
        // 老那套只是切 `spriteRenderer.enabled` / `spriteMask.enabled`（几乎免费）。
        // 老管线的代价是不支持「自定义遮罩深度」，但预览里那点差异基本看不出。
        public bool UseLegacySpriteMask = true;

        // 上一次「看过更新内容」的版本号。
        //
        // 更新窗口只在**换了版本之后第一次**打开工作台时弹一次，判据就是这个
        // 字段和当前版本号比 —— 不是一个「弹过了」的布尔：那样永远只弹一次，
        // 之后每次更新都不弹了。空字符串表示从没看过（新装的第一进来就弹）。
        public string LastSeenVersion = string.Empty;
        // 媒体缩略图的磁盘缓存目录。
        //
        // **留空表示用默认**（mod 目录下的 `MediaCache/`）。
        //
        // 缓存放 mod 目录而不是谱面目录，是为了守住「只读模式打开谱面不产生
        // 任何文件」那条 —— 往玩家的谱面目录里塞东西是越界，哪怕只是个 cache。
        // 多张谱共用一份也没问题：缓存文件名里含源图的绝对路径，不会串。
        public string MediaCacheDirectory = string.Empty;
        // 选中一个事件时，是否把预览画面和时间轴一起挪过去。
        //
        // 默认关掉：点一下事件只是想改它的参数，画面跟着跳会打断正在看的
        // 那一段。要「点哪看哪」的把它打开。
        public bool FollowSelectionOnClick = false;
        // 定位模式：在预览画面里点轨道，播放头立刻跳到那一格。
        //
        // 默认关。它改的是**点击**的含义（点轨道＝跳过去），和上面那个
        // 「选中事件时跟随」是两件事：那个管时间轴上的事件块，这个管画面里的轨道。
        // 快捷键 Ctrl+J。
        public bool LocateOnTrackClick = false;
        /// <summary>
        /// 时间轴排版模式。
        ///
        /// · **固定排版（true）**：事件块的行号由类型优先级自动决定 ——
        ///   移动装饰物 → 移动摄像头 → 滤镜 → 高级滤镜 → 其余，同类型内按时间先后
        ///   自上而下紧凑排布；每次改完事件自动重排一次，手动拖出来的行号会被覆盖。
        /// · **自由排版（false）**：行号完全归用户（拖动 / 撤销），保持现状。
        ///
        /// 标题行最右那颗「排版」按钮切换，快捷键 Ctrl+L。
        /// </summary>
        public bool FixedTimelineLayout = true;

        // 时间轴的自动翻页已取消（0.34.0）：视图不再自己跟着播放头跑。
        // 想回到播放头，用标题行那颗「定位到播放头」按钮（Ctrl+K）。
        // 参数栏的显示缩放。参数栏是按 1920x1080 的设计尺寸排的，
        // 在 1440p / 4K 上会显得比工作台其它面板小一号。
        // 1.0 = 原始大小；面板不变大，里面的字和控件等比放大。
        public float PropertyPanelScale = 1.5f;
        // 时间轴上拖动事件块时，要不要吸附到格边界。
        //
        // 默认开：ADOFAI 里一格就是一拍，所以对齐格边界天然就是
        // 节拍吸附，这正是做谱时绝大多数时候想要的。要微调时
        // 按住 Alt 可临时反转，不用来回点开关。
        public bool SnapTimelineDrag = true;
        public float TimelineHeight = 292f;
        public float TimelineRowHeight = 30f;

        // ---------------------------------------------------------------- 联机
        //
        // 这三项只是「下次打开联机面板时预填什么」，不是联机状态本身——
        // 会话是临时的，关掉工作台就断，没有东西需要持久化。

        /// <summary>上次用的名字。省得每局都重打。</summary>
        public string MultiplayerName = string.Empty;
        /// <summary>开房/加入用的端口。0 = 还没设置过，回落到默认端口。</summary>
        public int MultiplayerPort = 0;
        /// <summary>上次连的地址。主机换了地址时要记得改。</summary>
        public string MultiplayerAddress = string.Empty;
    }

    internal static class EditorBridge
    {
        private static Type editorType;
        private static FieldInfo instanceField;
        private static PropertyInfo instanceProperty;
        private static FieldInfo cameraField;
        private static PropertyInfo cameraProperty;
        private static bool searched;

        public static bool IsEditorActive()
        {
            if (GetEditorInstance() != null)
            {
                return true;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            return sceneName.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static object GetEditorInstance()
        {
            FindEditorType();
            if (editorType == null)
            {
                return null;
            }

            try
            {
                object instance = instanceField == null ? null : instanceField.GetValue(null);
                if (instance == null && instanceProperty != null)
                {
                    instance = instanceProperty.GetValue(null, null);
                }

                return instance;
            }
            catch
            {
                // 场景切换期间，编辑器单例可能暂时失效。
                return null;
            }
        }

        public static Type GetEditorType()
        {
            FindEditorType();
            return editorType;
        }

        public static Camera GetEditorCamera()
        {
            try
            {
                scrCamera cameraRig = scrCamera.instance;
                if (cameraRig != null && cameraRig.camobj != null)
                {
                    return cameraRig.camobj;
                }
            }
            catch
            {
                // 原生相机装置可能在场景切换期间重建。
            }

            object editor = GetEditorInstance();
            if (editor == null)
            {
                return null;
            }

            try
            {
                Camera camera = cameraProperty == null ? null : cameraProperty.GetValue(editor, null) as Camera;
                if (camera == null && cameraField != null)
                {
                    camera = cameraField.GetValue(editor) as Camera;
                }

                return camera == null ? Camera.main : camera;
            }
            catch
            {
                return Camera.main;
            }
        }

        public static Camera GetPreviewOutputCamera()
        {
            try
            {
                scrCamera cameraRig = scrCamera.instance;
                if (cameraRig != null
                    && cameraRig.Overlaycam != null
                    && cameraRig.Overlaycam.gameObject.activeInHierarchy
                    && cameraRig.camobj != null
                    && cameraRig.camobj.targetTexture != null)
                {
                    return cameraRig.Overlaycam;
                }
            }
            catch
            {
                // render-texture 相机栈可能在场景切换期间重建。
            }

            return GetEditorCamera();
        }

        private static void FindEditorType()
        {
            if (searched && editorType != null)
            {
                return;
            }

            searched = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    editorType = assembly.GetType("scnEditor", false)
                        ?? assembly.GetType("ADOFAI.scnEditor", false);
                    if (editorType != null)
                    {
                        break;
                    }
                }
                catch
                {
                    // 忽略动态程序集或未完全加载的程序集。
                }
            }

            if (editorType != null)
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                instanceField = editorType.GetField("instance", flags);
                instanceProperty = editorType.GetProperty("instance", flags);

                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                cameraField = editorType.GetField("camera", instanceFlags);
                cameraProperty = editorType.GetProperty("camera", instanceFlags);
            }
        }
    }
}