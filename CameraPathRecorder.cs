using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ADOFAI;
using ADOFAIEditorStudio.Preview;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIEditorStudio
{
/// <summary>
/// 每帧记录场景相机的最终状态，不管当前跑的是哪种模式，这样同一段谱面的
/// 原生播放和工作的拖动定位就能直接对差分。
    ///
/// 一条路径覆盖两种模式，因为读到的值永远是所有写入者都跑完之后
/// 的场景相机：原生播放时由游戏自己的 scrCamera
/// Update/LateUpdate 掌管 transform，而工作台接管预览时那个
/// 组件被禁用，改由 CameraPreviewDomain 写入。记录结果
/// 而不是记录任何一方，意味着两次运行可以直接对比。
    ///
/// 时间取自 scrConductor.songposition_minusi，工作台让它与
/// 自己的播放头保持同步，所以两次运行的行在时间列上能对齐。
/// </summary>
    internal sealed class CameraPathRecorder : MonoBehaviour
    {
        private const string FileNamePrefix = "camera-path-";

        private static CameraPathRecorder instance;
        private static StreamWriter writer;
        private static int framesUntilFlush;
        private static int rowsWritten;
        private static int framesUntilProgress;
        private static int minFloorSeen = int.MaxValue;
        private static int maxFloorSeen = int.MinValue;
        private static string startSummary = "-";

        /// <summary>本次录制的文件路径。**路径只算一次**：带时间戳的名字
        /// 每次调用都会变，算两次就会出现「写进 A、日志里报 B」。</summary>
        private static string lastPath = "-";

        /// <summary>
        /// 输出文件名带**启动时刻**，所以两次运行各留一份、互不覆盖 ——
        /// 一次原生播放、一次工作台拖动，两份并排比。
        ///
        /// 原来是个固定名 `camera-path.csv`，第二次 Start 会把它清掉，
        /// 而对比恰恰需要两份同时在。
        /// </summary>
        private static string BuildFileName()
        {
            return FileNamePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
        }

        internal static bool Recording
        {
            get { return writer != null; }
        }

        internal static string OutputPath
        {
            get { return Path.Combine(ResolveDirectory(), BuildFileName()); }
        }

        private static string ResolveDirectory()
        {
            try
            {
                if (Main.Mod != null && !string.IsNullOrEmpty(Main.Mod.Path))
                {
                    return Main.Mod.Path;
                }
            }
            catch (Exception)
            {
// 兜底落到游戏自己的目录。
            }
            return Directory.GetCurrentDirectory();
        }

        internal static void Ensure()
        {
            if (instance != null)
            {
                return;
            }

            GameObject host = new GameObject("ADOFAIEditorStudio.CameraRecorder");
            UnityEngine.Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<CameraPathRecorder>();
            InstallPatch();
        }

// ---------------------------------------------------------------- camAngle 探针
        //
// scrVfxPlus.camAngle 是原生相机旋转 tween 唯一写入的字段，而
// 它的 setter 还会把值推到 cam.transform.rotation 上。记录每一次写入
// ——值、帧号，以及发起写入的栈帧——就能回答：记录里看到的
// 单调旋转来自一条 tween，还是两个写入者在互相
// 打架。这个 mod 里别的办法都看不出来：只采样最终值会把
// 两者混在一起。
        private static int angleWritesThisFrame;
        private static float angleWriteMin = float.NaN;
        private static float angleWriteMax = float.NaN;
        private static int lastAngleWriteFrame = -1;

        internal static void RecordAngleWrite(float value)
        {
            if (writer == null)
            {
                return;
            }
            if (Time.frameCount != lastAngleWriteFrame)
            {
                lastAngleWriteFrame = Time.frameCount;
                angleWritesThisFrame = 0;
                angleWriteMin = float.NaN;
                angleWriteMax = float.NaN;
            }
            angleWritesThisFrame++;
            if (float.IsNaN(angleWriteMin) || value < angleWriteMin) angleWriteMin = value;
            if (float.IsNaN(angleWriteMax) || value > angleWriteMax) angleWriteMax = value;
        }

/// <summary>
/// 每有一帧写入 camAngle 就记一行，带上写入次数和值的
/// 范围。一帧内写入超过一次，说明有两条 tween 同时在跑。
/// </summary>
        internal static void FlushAngleWrites()
        {
            if (writer == null || angleWritesThisFrame == 0)
            {
                return;
            }
            try
            {
                writer.WriteLine("angleWrite," + Time.frameCount + ","
                    + angleWritesThisFrame + ","
                    + angleWriteMin.ToString("0.0000", CultureInfo.InvariantCulture) + ","
                    + angleWriteMax.ToString("0.0000", CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
            }
            angleWritesThisFrame = 0;
        }

/// <summary>
/// 打补丁是为了在游戏刚摆好相机的那一刻就抓到原生帧，
/// 而不是等这个组件碰巧跑到。编辑器帧由这个组件自己的
/// LateUpdate 采集，因为那个被打了补丁的方法在那里是禁用的。
/// </summary>
        private static void InstallPatch()
        {
            try
            {
                MethodInfo target = typeof(scrCamera).GetMethod(
                    "LateUpdate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (target == null)
                {
                    Main.Logger.Error("[CameraRecorder] scrCamera.LateUpdate not found.");
                    return;
                }

                Harmony harmony = new Harmony("ADOFAIEditorStudio.CameraRecorder");
                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(typeof(CameraPathRecorderPatch).GetMethod(
                        "Postfix",
                        BindingFlags.Static | BindingFlags.NonPublic)));

// 旋转 tween 通过这个属性写入，所以它的 setter 就是
// 观察写入模式的地方。
                MethodInfo angleSetter = typeof(scrVfxPlus).GetProperty(
                    "camAngle",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);
                if (angleSetter != null)
                {
                    harmony.Patch(
                        angleSetter,
                        postfix: new HarmonyMethod(typeof(CamAngleWritePatch).GetMethod(
                            "Postfix",
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    Main.Logger.Log("[CameraRecorder] camAngle write spy installed.");
                }
                Main.Logger.Log("[CameraRecorder] patch installed; Ctrl+Shift+C toggles recording.");
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[CameraRecorder] patch failed: " + exception);
            }
        }

        internal static void Toggle()
        {
            if (Recording)
            {
                Stop();
            }
            else
            {
                Start();
            }
        }

        private static void Start()
        {
            if (writer != null)
            {
                return;
            }

            try
            {
                // 路径只算一次：带时间戳的名字每次调用都会变，
                // 算两次就会「写进一个文件、日志里报另一个」。
                lastPath = OutputPath;
                writer = new StreamWriter(lastPath, false, Encoding.UTF8);
                writer.WriteLine(
                    "mode,time,frame,floor,worldX,worldY,worldZ,localX,localY,parentX,parentY,"
                    + "zoom,rot,shakeX,shakeY,followMode,camEnabled,"
// 原生跟随状态，这样读到的是游戏真正在追的锚点，
// 而不是推断出来的。topos 只在落地时由 UpdateFollowCam 赋值，
// 所以它保存的就是那一瞬的行星位置；furthestPlanet
// 是它来自的那颗行星。
                    + "toposX,toposY,fromposX,fromposY,furthestX,furthestY,camspeed,timer,"
// 工作台预览时钟，用来区分后退一步是被写回的
// 陈旧值造成的，还是求值器自己造成的。
                    + "held,clockRunning,previewClock,"
// Flash 系列的缓动把角度写到 scrVfxPlus.camAngle，而不是
// scrCamera.rot，所以旋转通道需要自己的列，否则
// 那段振荡拖尾在对比里根本看不见。
                    + "camAngle,"
// 墙钟时间。conductor 时钟在原生播放期间是冻结的，所以
// 没有这一列，原生行就没法放到真实时间轴上，
// 事件的时长只能靠帧数去猜。
                    + "wall");
                rowsWritten = 0;
                framesUntilFlush = 0;
                framesUntilProgress = 0;
                minFloorSeen = int.MaxValue;
                maxFloorSeen = int.MinValue;

                scrController controller = scrController.instance;
                int startFloor = controller != null && controller.chosenPlanet != null
                    && controller.chosenPlanet.currfloor != null
                    ? controller.chosenPlanet.currfloor.seqID
                    : -1;
                startSummary = startFloor.ToString();
                Main.Logger.Log("[CameraRecorder] recording to " + lastPath
                    + " -- started at floor " + startSummary
                    + ". The level must actually be playing: record the native pass and the"
                    + " workbench scrub separately, and note that native playback resumes from"
                    + " wherever the playhead currently is.");
            }
            catch (Exception exception)
            {
                writer = null;
                Main.Logger.Error("[CameraRecorder] could not open the log: " + exception);
            }
        }

        private static void Stop()
        {
            if (writer == null)
            {
                return;
            }

            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception)
            {
// 关闭过程中没什么可做的。
            }
            writer = null;
            Main.Logger.Log("[CameraRecorder] stopped after " + rowsWritten
                + " rows, floors " + (minFloorSeen == int.MaxValue ? "-" : minFloorSeen + ".." + maxFloorSeen)
                + " (started at " + startSummary + "): " + OutputPath
                + (minFloorSeen == maxFloorSeen
                    ? " -- WARNING: the floor never changed, so nothing is comparable."
                    : string.Empty));
        }

        private void Update()
        {
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift)
                && Input.GetKeyDown(KeyCode.C))
            {
                Toggle();
                // 把路径即时说出来：文件名带启动时刻，光看日志目录认不出哪份是哪次。
                Main.Logger.Log("[CameraRecorder] "
                    + (Recording ? "开始录制（再按一次 Ctrl+Shift+C 停止）" : "停止录制")
                    + " → " + lastPath);
            }

            if (writer == null)
            {
                return;
            }

            if (++framesUntilFlush >= 60)
            {
                framesUntilFlush = 0;
                try
                {
                    writer.Flush();
                }
                catch (Exception)
                {
                    Stop();
                }
            }

// 每约 2 秒打一次进度。连续四次记录到的是停住的
// 相机，因为直到把文件读回来都没人提示，所以记录器现在
// 在运行时就报告它实际看到的东西。
            if (++framesUntilProgress >= 120)
            {
                framesUntilProgress = 0;
                bool moving = minFloorSeen != maxFloorSeen;
                Main.Logger.Log("[CameraRecorder] rows=" + rowsWritten
                    + " floors=" + (minFloorSeen == int.MaxValue ? "-" : minFloorSeen + ".." + maxFloorSeen)
                    + (moving ? " (moving)" : " (STATIONARY -- is the level actually playing?)"));
            }
        }

        private void LateUpdate()
        {
// 原生帧由补丁采集，它在游戏摆完相机的那一刻就跑。
// 这里覆盖工作台，那个组件在那里是禁用的。
            Record();
            FlushAngleWrites();
        }

        internal static void Record()
        {
            if (writer == null)
            {
                return;
            }

            scrCamera camera = scrCamera.instance;
            if (camera == null || camera.transform == null)
            {
                return;
            }

            try
            {
                Transform t = camera.transform;
                Transform parent = t.parent;
                scrConductor conductor = scrConductor.instance;
                double songTime = conductor == null ? 0d : conductor.songposition_minusi;
                string mode = PreviewEvaluationEngine.IsActive ? "editor" : "native";

// 两次运行对齐靠的是格索引，不是歌曲时间。
// 相机事件锚定在格上，而原生播放期间
// songposition_minusi 不前进（实测：相机移动时连续 196 行
// 时间相同），所以时间列没法把两次运行
// 排齐。两种模式下控制器的当前格都是有意义的。
                scrController controller = scrController.instance;
                int floorId = -1;
                if (controller != null && controller.chosenPlanet != null
                    && controller.chosenPlanet.currfloor != null)
                {
                    floorId = controller.chosenPlanet.currfloor.seqID;
                }

                if (floorId < minFloorSeen)
                {
                    minFloorSeen = floorId;
                }
                if (floorId > maxFloorSeen)
                {
                    maxFloorSeen = floorId;
                }

                StringBuilder row = new StringBuilder(200);
                row.Append(mode).Append(',')
                    .Append(songTime.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Time.frameCount).Append(',')
                    .Append(floorId).Append(',')
                    .Append(t.position.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.position.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.position.z.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.localPosition.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.localPosition.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(parent == null ? "0.000" : parent.position.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(parent == null ? "0.000" : parent.position.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.zoomSize.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.rot.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.shake.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.shake.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.followMode ? 1 : 0).Append(',')
                    .Append(camera.enabled ? 1 : 0).Append(',')
                    .Append(camera.topos.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.topos.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.frompos.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.frompos.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.furthestPlanet == null
                        ? "0.000"
                        : camera.furthestPlanet.transform.position.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.furthestPlanet == null
                        ? "0.000"
                        : camera.furthestPlanet.transform.position.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.camspeed.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(camera.timer.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(EditorPlaybackBridge.HeldPreviewTime.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(EditorPlaybackBridge.IsPreviewClockRunning ? 1 : 0).Append(',')
                    .Append(EditorPlaybackBridge.PreviewClockTime.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(scrVfxPlus.instance == null
                        ? "0.000"
                        : scrVfxPlus.instance.camAngle.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Time.realtimeSinceStartup.ToString("0.0000", CultureInfo.InvariantCulture));

                writer.WriteLine(row.ToString());
                rowsWritten++;
            }
            catch (Exception)
            {
// 半成品的一行不值得为它中断整个记录。
            }
        }
    }

    [HarmonyPatch(typeof(scrCamera), "LateUpdate")]
    internal static class CameraPathRecorderPatch
    {
        private static void Postfix()
        {
            CameraPathRecorder.Record();
        }
    }

    [HarmonyPatch(typeof(scrVfxPlus), "set_camAngle")]
    internal static class CamAngleWritePatch
    {
        private static void Postfix(float value)
        {
            CameraPathRecorder.RecordAngleWrite(value);
        }
    }
}