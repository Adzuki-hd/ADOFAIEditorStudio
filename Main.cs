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

        public static void Load(ModEntry modEntry)
        {
            Mod = modEntry;
            Logger = modEntry.Logger;
            Settings = ModSettings.Load<Settings>(modEntry);
            modEntry.OnToggle = OnToggle;
            EditorPlaybackBridge.InstallInputPatch();
            // 版本闸门。低于清单要求的 min 就自动下载新版、
            // 覆盖、重启游戏，用户不用做任何事。
            VersionGate.Check(modEntry);
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
                }

                Overlay.Configure(Settings);

                // 两个记录器是排查暂停格交接问题时用的调试仪器(行星角度 / 相机路径),
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
                }

                Logger.Log("ADOFAI Editor Studio " + Mod.Version + " loaded");
            }
            else
            {
                SaveSettings();
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
        public int Shortcut = (int)KeyCode.F8;
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
        // 时间轴定位以原生引擎优先。旧的静态求值器
        // 只作为兼容回退保留，不是默认路径。
        public bool UseStaticTimelineEvaluation = false;
        public bool UseLegacyTimelineScrubFallback = false;
        // 输出 [AudioSync] / [PlanetState] / [VfxQueue] / [ConductorState]
        // 这些 trace，用来诊断「拖动定位后再恢复播放」的缺陷。默认关闭：它们
        // 很吵，只在排查特定播放问题时才需要。
        public bool VerboseDiagnostics = false;
        // 选中一个事件时，是否把预览画面和时间轴一起挪过去。
        //
        // 默认关掉：点一下事件只是想改它的参数，画面跟着跳会打断正在看的
        // 那一段。要「点哪看哪」的把它打开。
        public bool FollowSelectionOnClick = false;
        // 参数栏的显示缩放。参数栏是按 1920x1080 的设计尺寸排的，
        // 在 1440p / 4K 上会显得比工作台其它面板小一号。
        // 1.0 = 原始大小；面板不变大，里面的字和控件等比放大。
        public float PropertyPanelScale = 1.5f;
        public float TimelineHeight = 292f;
        public float TimelineRowHeight = 30f;
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