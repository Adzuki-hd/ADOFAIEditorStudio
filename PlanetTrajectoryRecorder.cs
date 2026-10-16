using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 诊断仪器：逐帧记录每颗球**各自**的位置，用来把长按里那颗球的
    /// 真实轨迹拿下来，查双球角度差。
    ///
    /// 为什么不复用 planet-angles.csv：那份挂在
    /// scrPlanet.Update_RefreshAngles 的 postfix 上，那是**帧中途**。
    /// 那之后 scrPlanet.Update 的长按分支还会重写滑动那颗的
    /// transform.position，LateUpdate 还会再写一次；而且
    /// `movingToNext.transform.position = actualPos` 会把一颗球算出来的
    /// 位置写到另一颗身上。位置列因此既不是最终值、又是两颗球搅在一起的。
    ///
    /// 所以这个挂在 scrPlanet.LateUpdate 的 postfix 上——两处写入都做完了，
    /// 那时 transform.position 就是这一帧真正画出去的位置。
    /// addBob 是私有的，用反射读；它正是"波浪"最可能的来源，不能不看。
    ///
    /// 由 Main 在 VerboseDiagnostics 打开时挂载（输出会长到几百 MB，
    /// 不能对普通用户无条件 patch 游戏方法）。
    /// </summary>
    internal sealed class PlanetTrajectoryRecorder : MonoBehaviour
    {
        // 换个文件名（freeroam 排查用），免得和上一轮那份表头对不上的数据混在
        // 一个文件里——本记录器是追加写、表头只在文件为空时落一次。
        private const string FileName = "planet-trajectory-fr2.csv";

        private static PlanetTrajectoryRecorder instance;
        private static StreamWriter writer;
        private static int framesUntilFlush;
        private static int rowsWritten;

        /// <summary>私有字段 addBob：那颗球的"小幅摆动"来源。</summary>
        private static readonly FieldInfo AddBobField = typeof(scrPlanet).GetField(
            "addBob", BindingFlags.Instance | BindingFlags.NonPublic);
        /// <summary>私有字段 movingToNext：这一帧被这颗球摆到别处的那颗。</summary>
        private static readonly FieldInfo MovingToNextField = typeof(scrPlanet).GetField(
            "movingToNext", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>私有字段 swirlTween：Twirl 格上多行星的 1→0 摆动进度。</summary>
        private static readonly FieldInfo SwirlTweenField = typeof(scrPlanet).GetField(
            "swirlTween", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>私有字段 mpHoldTween：多行星长按格上的 0→1 修正进度。</summary>
        private static readonly FieldInfo MpHoldTweenField = typeof(scrPlanet).GetField(
            "mpHoldTween", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>私有字段 aliveTime：这颗球"活着"多久了，freeroam 用它决定能否跳格。</summary>
        private static readonly FieldInfo AliveTimeField = typeof(scrPlanet).GetField(
            "aliveTime", BindingFlags.Instance | BindingFlags.NonPublic);

        private static float ReadTween(scrPlanet planet, FieldInfo field)
        {
            if (planet == null || field == null)
            {
                return 0f;
            }

            try
            {
                object raw = field.GetValue(planet);
                return raw is float ? (float)raw : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        internal static bool Recording
        {
            get { return writer != null; }
        }

        internal static string OutputPath
        {
            get { return Path.Combine(ResolveDirectory(), FileName); }
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

            GameObject host = new GameObject("ADOFAIEditorStudio.TrajectoryRecorder");
            UnityEngine.Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<PlanetTrajectoryRecorder>();
            InstallPatch();
        }

        private static void InstallPatch()
        {
            try
            {
                MethodInfo target = typeof(scrPlanet).GetMethod(
                    "LateUpdate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (target == null)
                {
                    Main.Logger.Error("[Trajectory] scrPlanet.LateUpdate not found.");
                    return;
                }

                Harmony harmony = new Harmony("ADOFAIEditorStudio.TrajectoryRecorder");
                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(typeof(PlanetTrajectoryRecorderPatch).GetMethod(
                        "Postfix",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                Main.Logger.Log("[Trajectory] patch installed on scrPlanet.LateUpdate.");
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[Trajectory] patch failed: " + exception);
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
                // **追加**，不是覆盖。之前是覆盖写，重新部署会把上一次
                // 已经采到的轨迹整份冲掉——排查一轮就得重跑一遍游戏。
                // 表头只在文件为空时写，每次开始再落一行 #RUN 标记，
                // 这样一个文件里可以攒好几轮，按标记切开看。
                bool needsHeader;
                try
                {
                    needsHeader = !File.Exists(OutputPath)
                        || new FileInfo(OutputPath).Length == 0L;
                }
                catch (Exception)
                {
                    needsHeader = true;
                }

                writer = new StreamWriter(OutputPath, true, Encoding.UTF8);
                if (needsHeader)
                {
                    writer.WriteLine(
                        "time,frame,floorIndex,planetIndex,planetName,isChosen,"
                        + "posX,posY,angle,cosmeticRadius,currFloorX,currFloorY,"
                        + "holdLength,holdDistance,stickToFloor,radiusScale,"
                        + "addBobX,addBobY,movingToNextIndex,"
                        + "entryAngle,exitAngle,angleLength,storedX,storedY,"
                        + "holdCompletion,holdCompletionEased,entryTime,nextEntryTime,"
                        // 多行星排查用：planetList 当前长度（原生增减球会改它），
                        // 以及这一格的 numPlanets。
                        + "planetListCount,numPlanets,isSwirl,swirlTween,mpHoldTween,"
                        // freeroam 排查用：这一格是不是 freeroam、是生成格的话
                        // 它在网格里的 (x,y)、以及那颗球的 aliveTime（原生用它
                        // 和 popuptime 比较来决定能不能跳到下一格）。
                        + "freeroam,freeroamGenerated,freePosX,freePosY,aliveTime,"
                        // freeroam 出场后球的角度对不上（少半圈），这几列用来定
                        // 原生到底把起点角重设成了什么：snappedLastAngle 是权威
                        // 的硬值，floorIsCCW 决定那半圈往哪边偏。
                        + "snappedLastAngle,targetExitAngle,floorIsCCW,freeEndBeats");
                }
                writer.WriteLine("#RUN " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                rowsWritten = 0;
                framesUntilFlush = 0;
                Main.Logger.Log("[Trajectory] recording to " + OutputPath);
            }
            catch (Exception exception)
            {
                writer = null;
                Main.Logger.Error("[Trajectory] could not open the log: " + exception);
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
            Main.Logger.Log("[Trajectory] stopped after " + rowsWritten + " rows.");
        }

        private void Update()
        {
            // 和行星角度记录器同一套自动起停：原生在推进、求值器没接管
            // 就记；控制器整个没了（换场景）才收尾。
            bool playing = scrController.instance != null
                && scrController.instance.state == States.PlayerControl
                && !Preview.PreviewEvaluationEngine.IsActive;
            if (playing && !Recording)
            {
                Start();
            }
            else if (scrController.instance == null && Recording)
            {
                Stop();
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
        }

        /// <summary>
        /// 取 GameObject 的名字。逗号换成下划线——名字里带逗号会把 CSV 的
        /// 列冲掉，那正是要看的证据，不能让它把行读歪。
        /// </summary>
        private static string SafeName(GameObject gameObject)
        {
            try
            {
                if (gameObject == null)
                {
                    return "-";
                }
                return gameObject.name.Replace(',', '_');
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string F(float value)
        {
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 由 scrPlanet.LateUpdate 的 postfix 调用。一帧里每颗球各写一行，
        /// 这样两颗球的轨迹可以分别看，不会被互相覆盖搅在一起。
        /// </summary>
        internal static void Record(scrPlanet planet)
        {
            if (writer == null || planet == null)
            {
                return;
            }

            try
            {
                scrFloor floor = planet.currfloor;
                Vector3 addBob = Vector3.zero;
                if (AddBobField != null)
                {
                    object raw = AddBobField.GetValue(planet);
                    if (raw is Vector3)
                    {
                        addBob = (Vector3)raw;
                    }
                }

                scrPlanet movingToNext = null;
                if (MovingToNextField != null)
                {
                    movingToNext = MovingToNextField.GetValue(planet) as scrPlanet;
                }

                float swirlTween = ReadTween(planet, SwirlTweenField);
                float mpHoldTween = ReadTween(planet, MpHoldTweenField);
                float aliveTime = ReadTween(planet, AliveTimeField);
                int planetCount = -1;
                if (planet.planetarySystem != null && planet.planetarySystem.planetList != null)
                {
                    planetCount = planet.planetarySystem.planetList.Count;
                }

                Vector3 position = planet.transform.position;
                StringBuilder row = new StringBuilder(220);
                row.Append(Time.time.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Time.frameCount).Append(',')
                    .Append(floor == null ? -1 : floor.seqID).Append(',')
                    .Append(planet.planetIndex).Append(',')
                    .Append(SafeName(planet.gameObject)).Append(',')
                    .Append(planet.isChosen ? 1 : 0).Append(',')
                    .Append(F(position.x)).Append(',')
                    .Append(F(position.y)).Append(',')
                    .Append(planet.angle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(F(planet.cosmeticRadius)).Append(',')
                    .Append(floor == null ? "0" : F(floor.transform.position.x)).Append(',')
                    .Append(floor == null ? "0" : F(floor.transform.position.y)).Append(',')
                    .Append(floor == null ? -1 : floor.holdLength).Append(',')
                    .Append(floor == null ? "0" : floor.holdDistance.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor != null && floor.stickToFloor ? 1 : 0).Append(',')
                    .Append(floor == null ? "1" : floor.radiusScale.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(F(addBob.x)).Append(',')
                    .Append(F(addBob.y)).Append(',')
                    .Append(movingToNext == null ? -1 : movingToNext.planetIndex).Append(',')
                    .Append(floor == null ? "0" : floor.entryangle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null ? "0" : floor.exitangle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null ? "0" : floor.angleLength.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(F(planet.storedPosition.x)).Append(',')
                    .Append(F(planet.storedPosition.y)).Append(',')
                    .Append(floor == null ? "0" : floor.holdCompletion.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null ? "0" : floor.holdCompletionEased.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null ? "0" : floor.entryTime.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null || floor.nextfloor == null
                        ? "0"
                        : floor.nextfloor.entryTime.ToString("0.0000", CultureInfo.InvariantCulture))
                    .Append(',').Append(planetCount).Append(',')
                    .Append(floor == null ? 0 : floor.numPlanets).Append(',')
                    .Append(floor != null && floor.isSwirl ? 1 : 0).Append(',')
                    .Append(swirlTween.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(mpHoldTween.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor != null && floor.freeroam ? 1 : 0).Append(',')
                    .Append(floor != null && floor.freeroamGenerated ? 1 : 0).Append(',')
                    .Append(floor == null ? 0 : floor.freeroamPosition.x).Append(',')
                    .Append(floor == null ? 0 : floor.freeroamPosition.y).Append(',')
                    .Append(aliveTime.ToString("0.0000", CultureInfo.InvariantCulture))
                    .Append(',').Append(planet.snappedLastAngle.ToString("0.0000", CultureInfo.InvariantCulture))
                    .Append(',').Append(planet.targetExitAngle.ToString("0.0000", CultureInfo.InvariantCulture))
                    .Append(',').Append(floor != null && floor.isCCW ? 1 : 0)
                    .Append(',').Append(floor == null ? 0 : floor.freeroamEndEarlyBeats);

                writer.WriteLine(row.ToString());
                rowsWritten++;
            }
            catch (Exception)
            {
                // 半成品的一行不值得为它中断整个记录。
            }
        }
    }

    [HarmonyPatch(typeof(scrPlanet), "LateUpdate")]
    internal static class PlanetTrajectoryRecorderPatch
    {
        private static void Postfix(scrPlanet __instance)
        {
            PlanetTrajectoryRecorder.Record(__instance);
        }
    }
}
