using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 逐帧把每一格轨道的**实际**位置/旋转/缩放/透明度写成 CSV。
    ///
    /// 存在的理由：「轨道散开之后回不来」这类问题，只看代码只能推出
    /// 「哪个域应该赢」，推不出屏幕上到底发生了什么。原版和 AES 各跑一遍
    /// 同一段，把同一个时间点、同一格的两行数据摆在一起比，才谈得上取证。
    ///
    /// **不挂游戏方法，自己在 LateUpdate 里遍历全谱。** 试过两个 Harmony 挂点，
    /// 都不可靠：
    ///   · `scrFloor.LateUpdate` —— 开头一道
    ///     `if (!controller.gameworld &amp;&amp; controller.currFloor != null …) return;`，
    ///     编辑器暂停态直接吃掉每一次调用，一条都记不到（实测 0 行）。
    ///   · `scrFloor.Update` —— 能过闸，但编辑器里只调当前那一格，
    ///     61 帧里只录到第 120 格一格（实测 61 行、单格）。
    ///
    /// 自己的 LateUpdate 不受这两条限制，而且跑在 DOTween 与原生特效之后，
    /// 读到的就是这一帧渲染前真正生效的值。
    ///
    /// 输出：`Mods/ADOFAIEditorStudio/track-frames.csv`
    ///   frame,time,floor,posX,posY,rotZ,scaleX,scaleY,opacity
    /// </summary>
    internal sealed class TrackFrameRecorder : MonoBehaviour
    {
        private const string FileName = "track-frames.csv";

        private static TrackFrameRecorder instance;
        private static StreamWriter writer;
        private static int rowsWritten;
        private static int framesUntilFlush;

        /// <summary>要记录哪些格。空 = 全记录。</summary>
        private static readonly HashSet<int> watchedFloors = new HashSet<int>();

        internal static bool Recording
        {
            get { return writer != null; }
        }

        internal static string OutputPath
        {
            get
            {
                // 每次开始录制换一个新文件名，否则第二轮会覆盖第一轮
                // ——原版和 AES 各录一遍时，第一份数据就这么没了。
                string dir = ResolveDirectory();
                string path = Path.Combine(dir, FileName);
                int n = 1;
                while (File.Exists(path))
                {
                    path = Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(FileName) + "-" + n + ".csv");
                    n++;
                }
                return path;
            }
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

            GameObject host = new GameObject("ADOFAIEditorStudio.TrackFrameRecorder");
            UnityEngine.Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<TrackFrameRecorder>();
            Main.Logger.Log("[TrackRecorder] ready; Ctrl+Shift+T starts/stops recording.");
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

        /// <summary>
        /// 设观察窗口：只记 [first, last] 这一段格。
        /// 大谱面全记会把文件撑爆，而定位问题基本只看一段。
        /// </summary>
        internal static void WatchRange(int first, int last)
        {
            watchedFloors.Clear();
            if (first >= 0 && last >= first)
            {
                for (int i = first; i <= last; i++)
                {
                    watchedFloors.Add(i);
                }
            }
            Main.Logger.Log("[TrackRecorder] watching floors "
                + (watchedFloors.Count == 0 ? "ALL" : first + ".." + last)
                + " (" + watchedFloors.Count + " floors)");
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
                writer.WriteLine("frame,time,songtime,floor,posX,posY,rotZ,scaleX,scaleY,opacity");
                rowsWritten = 0;
                framesUntilFlush = 0;
                Main.Logger.Log("[TrackRecorder] recording to " + OutputPath
                    + " -- press Ctrl+Shift+T again to stop.");
            }
            catch (Exception exception)
            {
                writer = null;
                Main.Logger.Error("[TrackRecorder] could not open the log: " + exception);
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
                // 关闭途中没什么可做的。
            }
            writer = null;
            Main.Logger.Log("[TrackRecorder] stopped after " + rowsWritten
                + " rows: " + OutputPath);
        }

        private void Update()
        {
            // Ctrl+Shift+T 切换。避开工作台 F8 / Ctrl+Z，
            // 也避开另外两个记录器（Ctrl+Shift+A / C）。
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift)
                && Input.GetKeyDown(KeyCode.T))
            {
                Toggle();
            }
        }

        private void LateUpdate()
        {
            if (writer == null)
            {
                return;
            }

            scrLevelMaker maker = ADOBase.lm;
            if (maker == null || maker.listFloors == null)
            {
                return;
            }

            List<scrFloor> floors = maker.listFloors;
            for (int i = 0; i < floors.Count; i++)
            {
                scrFloor floor = floors[i];
                if (floor == null)
                {
                    continue;
                }
                if (watchedFloors.Count > 0 && !watchedFloors.Contains(floor.seqID))
                {
                    continue;
                }
                Record(floor);
            }

            if (++framesUntilFlush >= 30)
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

        private void OnApplicationQuit()
        {
            Stop();
        }

        private static void Record(scrFloor floor)
        {
            try
            {
                Transform t = floor.transform;
                Vector3 pos = t.position;
                Vector3 scale = t.localScale;
                // **谱面时间，不是 Time.time。**
                //
                // 原版和 AES 各录一遍时，两边的 `Time.time` 起点完全不同
                // （实测一份从 12.8s 开始、另一份从 42.8s 开始），按它对齐
                // 永远对不上。`conductor.songposition_minusi` 是谱面自己的
                // 时间轴，两边同一个数值就代表同一个时刻。
                double songTime = 0d;
                scrConductor conductor = ADOBase.conductor;
                if (conductor != null)
                {
                    songTime = conductor.songposition_minusi;
                }
                StringBuilder row = new StringBuilder(160);
                row.Append(Time.frameCount).Append(',')
                    .Append(Time.time.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(songTime.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor.seqID).Append(',')
                    .Append(pos.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(pos.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(t.eulerAngles.z.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(scale.x.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(scale.y.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(floor.opacity.ToString("0.000", CultureInfo.InvariantCulture));

                writer.WriteLine(row.ToString());
                rowsWritten++;
            }
            catch (Exception)
            {
                // 半成品的一行不值得为它中断整个记录。
            }
        }
    }
}
