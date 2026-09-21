using System;
using System.Collections.Generic;
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
    /// 记录游戏自己为行星算出的角度，好让求值器能跟真实播放比对，
    /// 而不是跟一份反编译源码的解读比对。
    ///
    /// 这东西的存在是因为暂停格交接扛过了八轮源码阅读：
    /// 代码说在被测谱面上角度修正偏移约为 0，
    /// 而屏幕上明显看到球在行进前先退了一步。与其再猜第九次，
    /// 不如把球实际做的事写下来——角度、cosmeticAngle、
    /// snappedLastAngle、targetExitAngle、位置和时间——原生播放每帧一次。
    ///
    /// 只有原生播放才会跑 scrPlanet.Update，所以编辑器自己那套
    /// 求值器驱动的预览刻意不在这里记录。
    /// </summary>
    internal sealed class PlanetAngleRecorder : MonoBehaviour
    {
        private const string FileName = "planet-angles.csv";
        private static PlanetAngleRecorder instance;
        private static StreamWriter writer;
        private static int framesUntilFlush;
        private static int rowsWritten;

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
                writer = new StreamWriter(OutputPath, false, Encoding.UTF8);
                writer.WriteLine(
                    "time,floor,planetIndex,isChosen,angle,cosmeticAngle,snappedLastAngle,"
                    + "targetExitAngle,posX,posY,extraBeats,angleCorrectionType,"
                    + "entryAngle,exitAngle,angleLength,nextEntryAngle");
                rowsWritten = 0;
                framesUntilFlush = 0;
                Main.Logger.Log("[AngleRecorder] recording to " + OutputPath
                    + " -- play the level natively, then press the key again to stop.");
            }
            catch (Exception exception)
            {
                writer = null;
                Main.Logger.Error("[AngleRecorder] could not open the log: " + exception);
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
            Main.Logger.Log("[AngleRecorder] stopped after " + rowsWritten
                + " rows: " + OutputPath);
        }

        internal static void Ensure()
        {
            if (instance != null)
            {
                return;
            }

            GameObject host = new GameObject("ADOFAIEditorStudio.AngleRecorder");
            UnityEngine.Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<PlanetAngleRecorder>();
            InstallPatch();
        }

        /// <summary>
        /// 手工打补丁，而不是走 PatchAll，跟这个 mod 其余挂钩的安装方式一致：
        /// 目标被显式写出名字，这样将来游戏版本改了名，这里会大声失败，
        /// 而不是悄悄不记录。
        /// </summary>
        private static void InstallPatch()
        {
            try
            {
                MethodInfo target = typeof(scrPlanet).GetMethod(
                    "Update_RefreshAngles",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (target == null)
                {
                    Main.Logger.Error("[AngleRecorder] scrPlanet.Update_RefreshAngles not found.");
                    return;
                }

                Harmony harmony = new Harmony("ADOFAIEditorStudio.AngleRecorder");
                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(typeof(PlanetAngleRecorderPatch).GetMethod(
                        "Postfix",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                Main.Logger.Log("[AngleRecorder] patch installed; Ctrl+Shift+A starts recording.");
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[AngleRecorder] patch failed: " + exception);
            }
        }

        private void Update()
        {
            // Ctrl+Shift+A 切换。这样选是为了不和编辑器自己的键冲突，
            // 也不和工作台的 F8 / Ctrl+Z 处理冲突。
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift)
                && Input.GetKeyDown(KeyCode.A))
            {
                Toggle();
            }

            if (writer == null)
            {
                return;
            }

            // 别让缓冲区在记录器一直开着时无限增长。
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
        /// 由 scrPlanet.Update_RefreshAngles 的 postfix 调用，那是游戏
        /// 为这一帧定下行星角度的唯一位置。
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
                StringBuilder row = new StringBuilder(160);
                row.Append(Time.time.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null ? -1 : floor.seqID).Append(',')
                    .Append(planet.planetIndex).Append(',')
                    .Append(planet.isChosen ? 1 : 0).Append(',')
                    .Append(planet.angle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(planet.cosmeticAngle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(planet.snappedLastAngle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(planet.targetExitAngle.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(planet.transform.position.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(planet.transform.position.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor == null ? 0f : floor.extraBeats).Append(',')
                    .Append(floor == null ? -1f : floor.angleCorrectionType).Append(',')
                    .Append(floor == null ? 0d : floor.entryangle).Append(',')
                    .Append(floor == null ? 0d : floor.exitangle).Append(',')
                    .Append(floor == null ? 0d : floor.angleLength).Append(',')
                    .Append(floor == null || floor.nextfloor == null ? 0d : floor.nextfloor.entryangle);

                writer.WriteLine(row.ToString());
                rowsWritten++;
            }
            catch (Exception)
            {
                // 半成品的一行不值得为它中断整个记录。
            }
        }
    }

    [HarmonyPatch(typeof(scrPlanet), "Update_RefreshAngles")]
    internal static class PlanetAngleRecorderPatch
    {
        private static void Postfix(scrPlanet __instance)
        {
            PlanetAngleRecorder.Record(__instance);
        }
    }
}