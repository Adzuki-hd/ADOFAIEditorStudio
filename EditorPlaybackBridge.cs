using System.Text;
﻿using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ADOFAIEditorStudio.Preview;
using ADOFAIEditorStudio.Preview.Domains;

namespace ADOFAIEditorStudio
{
    internal enum EditorPlaybackState
    {
        Editing,
        Playing,
        Paused
    }

    internal struct EditorPlaybackSnapshot
    {
        public bool Available;
        public EditorPlaybackState State;
        public float CurrentTime;
        public float Duration;
    }

    /// <summary>
    /// 临时：量化求值器到原生交接的拼接质量，因为
    /// 「无缝」这个说法可以归结为三个数，而不是靠眼睛判断——
    /// 重建让画面停顿多久、音频实际落点
    /// 离被要求的位置有多远、原生运行时第一个球的角度
    /// 离求值器留在屏幕上的那一帧有多远。最后一项由后置补丁捕获，
    /// 打在 Update_RefreshAngles 上；其余从桥自己的入口处盖时间戳。
    /// </summary>
    internal static class HandoffProbe
    {
        private const long BucketMs = 500L;
        private const int MaxSeconds = 10;
        private static readonly List<string>[] Buckets = new List<string>[MaxSeconds];

        private static bool active;
        private static long armedAtMs;
        private static long lastFlushMs;
        private static bool finalFlushed;
        private static float evaluatorTime;
        private static float evaluatorRendered;
        private static float evaluatorSnap;
        private static float evaluatorLastHit;
        private static int teardownFrame = -1;
        private static long teardownAtMs = -1L;
        private static bool routeLogged;
        private static bool nativeFirstPending;
        private static bool nativeSawChosenTick;
        private static int nativeFirstFrame = -1;
        private static int lastTrajectoryFrame = -1;
        private static long nativeSettleAtMs = -1L;
        private static float nativeFirstAngle;
        private static float nativeFirstRendered;
        private static float nativeFirstSnap;
        private static float nativeFirstLastHit;
        private static float auditAudio;

        private static long NowMs()
        {
            return (long)(Time.realtimeSinceStartup * 1000f);
        }

        private static int SecondsBucket(long ms)
        {
            long index = ms / 1000L;
            return index < 0L ? 0 : (index >= MaxSeconds ? MaxSeconds - 1 : (int)index);
        }

        /// <summary>与 <see cref="Add"/> 相同，暴露出来是为了让桥能盖一个阶段标记。</summary>
        internal static void Record(string line)
        {
            Add(line);
        }

        /// <summary>
        /// 探针自己那些行用的 mm:ss.mmm 格式。刻意做成本地的：桥的格式化器
        /// 是它自己类的私有成员，而这个探针在类外。
        /// </summary>
        private static string TimeText(float seconds)
        {
            int minutes = (int)(seconds / 60f);
            return minutes.ToString("00") + ":" + (seconds - minutes * 60f).ToString("00.000");
        }

        private static void Add(string line)
        {
            if (!active)
            {
                return;
            }

            long offset = NowMs() - armedAtMs;
            int bucket = SecondsBucket(offset);
            if (Buckets[bucket] == null)
            {
                Buckets[bucket] = new List<string>();
            }
            Buckets[bucket].Add("+" + (offset / 1000.0).ToString("0.00") + "s f" + Time.frameCount + " " + line);
        }

        /// <summary>开始一次测量，并标记空格键走的是哪条路径。</summary>
        internal static void Arm(string route)
        {
            // **只在开发者设置打开"探针总闸"时才武装。**
            // 原来从工作台按播放会**无条件**启动它：日志里刷一串 [HandoffProbe]，
            // 顶栏还会被塞一句 "[探针] …"。那是排查交接帧时用的，不该出现在玩家那边。
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            for (int i = 0; i < Buckets.Length; i++)
            {
                if (Buckets[i] != null)
                {
                    Buckets[i].Clear();
                }
            }
            active = true;
            finalFlushed = false;
            routeLogged = false;
            nativeFirstPending = false;
            nativeSawChosenTick = false;
            nativeFirstFrame = -1;
            lastTrajectoryFrame = -1;
            nativeSettleAtMs = -1L;
            teardownFrame = -1;
            teardownAtMs = -1L;
            armedAtMs = NowMs();
            lastFlushMs = armedAtMs;
            Add("ARM route=" + route + " target=" + TimeText(EditorPlaybackBridge.HandoffProbeTargetTime));
            StampEvaluator("pre-arm");
        }

        /// <summary>求值器最后一组上屏数值，取自它仍掌控画面的时刻。</summary>
        internal static void StampEvaluator(string label)
        {
            if (!active)
            {
                return;
            }

            evaluatorTime = EditorPlaybackBridge.HandoffProbePreviewTime;
            scrPlanet planet = EditorPlaybackBridge.HandoffProbeChosenPlanet();
            if (planet != null)
            {
                evaluatorRendered = (float)(planet.angle + planet.cosmeticAngle);
                evaluatorSnap = (float)planet.snappedLastAngle;
                evaluatorLastHit = planet.player == null ? 0f : (float)planet.player.lastHit;
            }
            Add(label + " src=evaluator rendered=" + evaluatorRendered.ToString("0.0000")
                + " snap=" + evaluatorSnap.ToString("0.0000")
                + " lastHit=" + evaluatorLastHit.ToString("0.0000")
                + " clockT=" + TimeText(evaluatorTime));
        }

        /// <summary>求值器会话被拆除的那一刻，画面从这时起不再被驱动。</summary>
        internal static void MarkTeardown()
        {
            if (!active || teardownFrame >= 0)
            {
                return;
            }
            teardownFrame = Time.frameCount;
            teardownAtMs = NowMs();
            Add("TEARDOWN frame=" + teardownFrame);
        }

        /// <summary>原生运行时启动期间逐帧计数，这样这段停顿可以直接按帧数读。</summary>
        internal static void Tick()
        {
            if (!active)
            {
                return;
            }

            long now = NowMs();
            Sample();

            if (now - lastFlushMs >= BucketMs)
            {
                Flush();
            }

            if (teardownFrame >= 0 && nativeFirstFrame < 0)
            {
                Add("BUILDING frame=" + Time.frameCount + " stallFrames=" + (Time.frameCount - teardownFrame));
            }
        }

        /// <summary>从本次运行里读出交接路径，这样走了哪条路永远不用猜。</summary>
        internal static void LogRoute(string route)
        {
            if (!active || routeLogged)
            {
                return;
            }
            routeLogged = true;
            Add("ROUTE=" + route);
        }

        internal static void MarkAudio(float requested)
        {
            if (!active)
            {
                return;
            }
            auditAudio = requested;
            Add("AUDIO-REQUESTED request=" + TimeText(requested));
        }

        /// <summary>音频片段真实的落点，以及运行时被留在手里的那个时钟值。</summary>
        internal static void MarkAudioLanded(float clockTime)
        {
            if (!active)
            {
                return;
            }

            scrConductor conductor = scrConductor.instance;
            scrPlanet planet = EditorPlaybackBridge.HandoffProbeChosenPlanet();
            Add("AUDIO-LANDED clockT=" + TimeText(clockTime)
                + " requested=" + TimeText(auditAudio)
                + " deltaMs=" + ((clockTime - auditAudio) * 1000f).ToString("0.0")
                + (conductor == null || conductor.song == null
                    ? string.Empty
                    : " clipTime=" + conductor.song.time.ToString("0.000")
                      + " songpos=" + conductor.songposition_minusi.ToString("0.000"))
                + (planet == null
                    ? string.Empty
                    : " rendered=" + (planet.angle + planet.cosmeticAngle).ToString("0.0000")));
        }

        /// <summary>
        /// 原生驱动的第一帧。Update_RefreshAngles 每个行星跑一次，所以
        /// 后置补丁只立一个旗标；采样放在 <see cref="Tick"/> 里，它
        /// 一帧恰好只看一次。在后置补丁里读，一帧会采到九个行星，
        /// 首帧那一行就落在碰巧不动的那个上。
        /// </summary>
        internal static void OnNativeFrame(scrPlanet planet)
        {
            if (!active || !nativeFirstPending || planet == null || !planet.isChosen)
            {
                return;
            }
            nativeSawChosenTick = true;
        }

        /// <summary>
        /// 每帧一个稳定后的采样，外加交接之后紧接着若干帧的轨迹，
        /// 这样能自愈的瞬态错误也仍然看得见。
        /// </summary>
        internal static void Sample()
        {
            if (!active || !nativeFirstPending || !nativeSawChosenTick)
            {
                return;
            }

            scrPlanet planet = EditorPlaybackBridge.HandoffProbeChosenPlanet();
            if (planet == null)
            {
                return;
            }

            float rendered = (float)(planet.angle + planet.cosmeticAngle);
            if (nativeFirstFrame < 0)
            {
                nativeFirstFrame = Time.frameCount;
                nativeFirstAngle = (float)planet.angle;
                nativeFirstRendered = rendered;
                nativeFirstSnap = (float)planet.snappedLastAngle;
                nativeFirstLastHit = planet.player == null ? 0f : (float)planet.player.lastHit;
                long stallMs = teardownAtMs >= 0 ? NowMs() - teardownAtMs : -1L;
                Add("NATIVE-FIRST-FRAME seqID=" + (planet.currfloor == null ? -1 : planet.currfloor.seqID)
                    + " angle=" + nativeFirstAngle.ToString("0.0000")
                    + " rendered=" + rendered.ToString("0.0000")
                    + " snap=" + nativeFirstSnap.ToString("0.0000")
                    + " lastHit=" + nativeFirstLastHit.ToString("0.0000")
                    + " stallMs=" + stallMs
                    + " stallFrames=" + (teardownFrame >= 0 ? Time.frameCount - teardownFrame : -1)
                    + " | vs evaluator dRendered="
                    + (rendered - evaluatorRendered).ToString("0.0000"));
                nativeSettleAtMs = NowMs();
                return;
            }

            int delta = Time.frameCount - nativeFirstFrame;
            if (delta <= 12 && delta != lastTrajectoryFrame)
            {
                lastTrajectoryFrame = delta;
                Add("NATIVE-TRAJECTORY+" + delta
                    + " seqID=" + (planet.currfloor == null ? -1 : planet.currfloor.seqID)
                    + " rendered=" + rendered.ToString("0.0000")
                    + " snap=" + nativeFirstSnap.ToString("0.0000")
                    + " lastHit=" + (planet.player == null ? 0f : (float)planet.player.lastHit).ToString("0.0000"));
            }

            // 画面要么已经落到求值器的值上，要么还没有；不管哪种，
            // 测量都远早于周期性刷写本该落地的时刻就完成了。
            if (nativeSettleAtMs > 0 && NowMs() - nativeSettleAtMs > 1500L)
            {
                Flush();
                active = false;
            }
        }

        /// <summary>停止采样，按时间轴顺序返回所有行。</summary>
        internal static List<string> Finish()
        {
            Flush();
            List<string> rows = new List<string>();
            for (int i = 0; i < Buckets.Length; i++)
            {
                List<string> bucket = Buckets[i];
                if (bucket == null)
                {
                    continue;
                }
                for (int j = 0; j < bucket.Count; j++)
                {
                    rows.Add(bucket[j]);
                }
                bucket.Clear();
            }
            active = false;
            return rows;
        }

        private static void Flush()
        {
            if (!active)
            {
                return;
            }

            lastFlushMs = NowMs();
            List<string> rows = new List<string>();
            for (int i = 0; i < Buckets.Length; i++)
            {
                List<string> bucket = Buckets[i];
                if (bucket == null)
                {
                    continue;
                }
                for (int j = 0; j < bucket.Count; j++)
                {
                    rows.Add(bucket[j]);
                }
                bucket.Clear();
            }
            for (int i = 0; i < rows.Count; i++)
            {
                Main.Logger.Log("[HandoffProbe] " + rows[i]);
            }
            if (rows.Count > 0 && !finalFlushed)
            {
                finalFlushed = true;
                EditorPlaybackBridge.HandoffProbeStatus(Summary());
            }
        }

        private static string Summary()
        {
            if (nativeFirstFrame < 0)
            {
                return "交接测量中…（详见日志 [HandoffProbe]）";
            }

            // 球是个圆，所以差整整一圈等于没差。
            float jump = nativeFirstRendered - evaluatorRendered;
            while (jump > Mathf.PI)
            {
                jump -= Mathf.PI * 2f;
            }
            while (jump < -Mathf.PI)
            {
                jump += Mathf.PI * 2f;
            }
            int stallFrames = teardownFrame >= 0 && nativeFirstFrame >= 0
                ? nativeFirstFrame - teardownFrame
                : -1;
            return "首帧跳变 " + (jump * Mathf.Rad2Deg).ToString("0.0") + "°"
                + " · 重建停留 " + (stallFrames < 0 ? "无" : stallFrames + " 帧")
                + " · 落位相位差 " + ((nativeFirstLastHit - evaluatorLastHit) * 1000f).ToString("0")
                + "ms（详见 [HandoffProbe]）";
        }

        /// <summary>武装原生首帧捕获，但不开启新的测量。</summary>
        internal static void ExpectNativeFirstFrame()
        {
            if (!active)
            {
                return;
            }
            nativeFirstPending = true;
            nativeFirstFrame = -1;
            nativeSawChosenTick = false;
            lastTrajectoryFrame = -1;
            nativeSettleAtMs = -1L;
            Add("EXPECT-NATIVE-FIRST-FRAME");
        }
    }

    /// <summary>
    /// 临时：把一次 Track B 运行的最后几帧留在内存里，这样 transport 死掉的那一刻
    /// 可以往回读——之前每一帧时钟、状态和球分别在做什么，
    /// 以及是哪个调用结束了它。每秒只打印一次的痕迹
    /// 看不出发生在两次打印之间的状态跃迁，而这次运行
    /// 走过的每一条退出路径，截至目前恰好都落在两次打印之间。
    /// </summary>
    internal static class TrackBTrace
    {
        private const int Capacity = 180;
        private static readonly string[] Rows = new string[Capacity];
        private static int next;
        private static bool armed;

        internal static void Arm()
        {
            for (int i = 0; i < Capacity; i++)
            {
                Rows[i] = null;
            }
            next = 0;
            armed = true;
        }

        internal static bool Armed
        {
            get { return armed; }
        }

        internal static void Add(string row)
        {
            if (!armed)
            {
                return;
            }
            Rows[next] = "f" + Time.frameCount + " " + row;
            next = (next + 1) % Capacity;
        }

        /// <summary>按从旧到新的顺序，用调用方给的前缀打印缓冲区，然后解除武装。</summary>
        internal static void Dump(string reason)
        {
            if (!armed)
            {
                return;
            }
            armed = false;
            Main.Logger.Log("[TrackBTrace] ==== " + reason + " ==== dumping " + Capacity + " frames ====");
            for (int i = 0; i < Capacity; i++)
            {
                string row = Rows[(next + i) % Capacity];
                if (row != null)
                {
                    Main.Logger.Log("[TrackBTrace] " + row);
                }
            }
            Main.Logger.Log("[TrackBTrace] ==== end ====");
        }
    }

    /// <summary>
    /// 临时：兜住编辑器 transport 所有可能的结束方式，连同调用方的调用栈一起 dump 环形缓冲区，
    /// 这样结束这件事是被归因的，而不是靠推断。
    /// </summary>
    internal static class TrackBExitTracer
    {
        private const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool IsRecent()
        {
            return TrackBTrace.Armed
                || Time.frameCount - EditorPlaybackBridge.LastTrackBPressFrame < 36000;
        }

        [HarmonyPatch(typeof(scrController), "QuitToMainMenu")]
        internal static class QuitTrace
        {
            private static void Prefix()
            {
                if (IsRecent())
                {
                    TrackBTrace.Dump("scrController.QuitToMainMenu | " + Environment.StackTrace);
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "SwitchToEditMode")]
        internal static class EditModeTrace
        {
            private static void Prefix()
            {
                if (IsRecent())
                {
                    TrackBTrace.Dump("scnEditor.SwitchToEditMode | " + Environment.StackTrace);
                }
            }
        }

        [HarmonyPatch(typeof(scrLoader), "LoadSceneWithTransition")]
        internal static class LoadSceneTrace
        {
            private static void Prefix()
            {
                if (IsRecent())
                {
                    TrackBTrace.Dump("scrLoader.LoadSceneWithTransition | " + Environment.StackTrace);
                }
            }
        }

        [HarmonyPatch(typeof(scrLoader), "LoadScene")]
        internal static class LoadSceneSimpleTrace
        {
            private static void Prefix()
            {
                if (IsRecent())
                {
                    TrackBTrace.Dump("scrLoader.LoadScene | " + Environment.StackTrace);
                }
            }
        }

        [HarmonyPatch(typeof(MonsterLove.StateMachine.StateBehaviour), "ChangeState")]
        internal static class ChangeStateTrace
        {
            private static void Prefix(Enum newState)
            {
                if (!IsRecent())
                {
                    return;
                }
                string name = newState == null ? "null" : newState.ToString();
                TrackBTrace.Add("ChangeState -> " + name);
                if (name == "Won" || name == "Fail" || name == "Fail2" || name == "None")
                {
                    TrackBTrace.Dump("ChangeState -> " + name + " | " + Environment.StackTrace);
                }
            }
        }
    }

    /// <summary>
    /// 让预览播放头停在编辑器选中的格上，这样方向键既移动
    /// 画面，也移动选中项。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "SelectFloor")]
    internal static class SelectFloorFollowPatch
    {
        private static void Postfix(scrFloor floorToSelect)
        {
            EditorPlaybackBridge.OnFloorSelected(floorToSelect);
        }
    }

    [HarmonyPatch(typeof(scrPlanet), "Update_RefreshAngles")]
    internal static class HandoffProbePlanetPatch
    {
        private static void Postfix(scrPlanet __instance)
        {
            HandoffProbe.OnNativeFrame(__instance);
        }
    }

    /// <summary>
    /// 临时：给每次行星重定位记一条带堆栈的日志，这样球最终停在
    /// 没人指定的格上时，可以归因到调用方，而不是靠猜。
    /// </summary>
    [HarmonyPatch(typeof(scrPlanet), "ScrubToFloorNumber")]
    internal static class TrackBScrubTracer
    {
        private static void Prefix(int floorNum, float? windbackTime, bool movePos, bool doingRevive)
        {
            // 诊断门控。这条会 dump Environment.StackTrace——捕获完整调用栈
            // 本身就不便宜，而 UMM 的写入还是同步的 Console.WriteLine。
            // 它是排查「按一下之后那 600 帧」用的，不该在正常情况下跑。
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }
            if (Time.frameCount - EditorPlaybackBridge.LastTrackBPressFrame > 600)
            {
                return;
            }
            Main.Logger.Log("[TrackBScrub] floorNum=" + floorNum
                + " windback=" + (windbackTime.HasValue ? windbackTime.Value.ToString("0.000") : "null")
                + " movePos=" + movePos + " doingRevive=" + doingRevive
                + " frame=" + Time.frameCount
                + "\n" + Environment.StackTrace);
        }
    }

    internal static class EditorPlaybackBridge
    {
        private enum PreviewRestartPhase
        {
            None,
            WaitingForEditMode,
            WaitingToPlay,
            WaitingForRuntime,
            Aborting
        }

        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // 由 HandoffProbe 读取，它在类外：它需要求值器被留在手里的播放头、
        // 屏幕上的是哪个行星，以及一行结论该写在哪里。
        // 一行结论。
        internal static float HandoffProbePreviewTime
        {
            get { return previewClockTime; }
        }

        internal static float HandoffProbeTargetTime
        {
            get { return restartPhase == PreviewRestartPhase.None ? handoffTargetTime : restartTargetTime; }
        }

        internal static scrPlanet HandoffProbeChosenPlanet()
        {
            // players[0] 是玩家一，planetList[0].prev 是轮到该走的那个行星
            // ——与 scrController.ScrubToFloorNumber 的推导方式相同。改走
            // planetarySystem.chosenPlanet 读到的是多行星系统里 *当前正在动* 的那个行星，
            // 它与屏幕上那个不是同一个 scrPlanet；
            // 这就是为什么探针一帧里九个行星有八个采到 angle 0.0000。
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null
                || playerManager.players.Length == 0)
            {
                return null;
            }
            scrPlayer player = playerManager.players[0];
            if (player == null || player.planetarySystem == null
                || player.planetarySystem.planetList == null
                || player.planetarySystem.planetList.Count == 0)
            {
                return null;
            }
            scrPlanet first = player.planetarySystem.planetList[0];
            return first == null ? null : first.prev;
        }

        internal static void HandoffProbeStatus(string message)
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay != null)
            {
                overlay.SetSceneSelectionStatus("[探针] " + message);
            }
        }

        private static readonly FieldInfo ConductorNextBeatTimeField =
            typeof(scrConductor).GetField("nextBeatTime", InstanceFlags);
        private static readonly FieldInfo ConductorNextBarTimeField =
            typeof(scrConductor).GetField("nextBarTime", InstanceFlags);
        private static readonly FieldInfo ConductorCrotchetsPerBarField =
            typeof(scrConductor).GetField("crotchetsPerBar", InstanceFlags);
        private static readonly FieldInfo ConductorStartMusicCoroutineField =
            typeof(scrConductor).GetField("startMusicCoroutine", InstanceFlags);
        private static readonly FieldInfo ConductorToggleHasSongStartedCoroutineField =
            typeof(scrConductor).GetField("toggleHasSongStartedCourotine", InstanceFlags);
        // hitSoundsData 是私有的；读它来对比已排程的打击音时间
        // 和实时 dsp 时钟——音频与拍点失步就表现在这里。
        private static readonly FieldInfo ConductorHitSoundsDataField =
            typeof(scrConductor).GetField("hitSoundsData", InstanceFlags);
        private static readonly FieldInfo ConductorNextHitSoundField =
            typeof(scrConductor).GetField("nextHitSoundToSchedule", InstanceFlags);
        // scrVfxPlus 用只前进的游标驱动特效，每个特效触发后就从队列移除，
        // 所以已被消费的队列不会重放。两个都读，才能看出
        // 从更早的时间点恢复时还有没有东西可触发。
        private static readonly FieldInfo VfxCurrentIndexField =
            typeof(scrVfxPlus).GetField("currentVfxIndex", InstanceFlags);
        private static readonly FieldInfo CameraMoveXTweenField =
            typeof(ffxCameraPlus).GetField("moveXTween", StaticFlags);
        private static readonly FieldInfo CameraMoveYTweenField =
            typeof(ffxCameraPlus).GetField("moveYTween", StaticFlags);
        // scrPlanet 把这些暴露成只读属性，所以要读它们得靠反射。
        // 记下来是为了把拖动预览的值，与
        // FirstFloorAngleSetup 在正常开始时装入的值做对比。
        private static readonly PropertyInfo PlanetSnappedLastAngleProperty =
            typeof(scrPlanet).GetProperty("snappedLastAngle", InstanceFlags);
        private static readonly PropertyInfo PlanetTargetExitAngleProperty =
            typeof(scrPlanet).GetProperty("targetExitAngle", InstanceFlags);

        private static Harmony harmony;
        private static bool inputPatchAttempted;
        private static bool previewRequested;
        private static bool nativePlaybackStarted;
        private static bool desiredPaused = true;
        private static bool pauseTransitionPending;
        private static bool pauseReapplyRequested;
        private static bool nativeTransportHeld;
        private static PreviewRestartPhase restartPhase;
        private static int restartPhaseFrame;
        private static int restartAbortStableFrames;
        private static float restartStartedAt;
        private static float restartTargetTime;
        private static bool restartCommitExactTime;
        private static bool restartPlayIssued;
        private static bool allowNativeExit;
        private static bool autoModeOverridden;
        private static bool previousAutoMode;
        private static int lastToggleFrame = -1;
        private static int lastColliderRefreshFrame = -100;
        private static int lastTimelineScrubFloor = -1;
        private static int lastTimelineScrubEditorId;
        private static float lastTimelineScrubTime = float.NaN;
        private static bool timelineScrubActive;
        private static bool staticPreviewFrameActive;
        private static bool nativeHandoffPending;
        private static bool restartResumeAfterReady;
        private static float heldPreviewTime = float.NaN;

        private static string lastError;
        private static bool nativeScrubPrepared;
        private static readonly List<float> nativeScrubBoundaries = new List<float>();
        private static int lastNativeCameraDiagnosticFloor = -1;
        private static float lastNativeCameraDiagnosticTime = float.NaN;
        private static bool staticCameraStateCaptured;
        private static bool staticCameraWasEnabled;
        private static bool staticCameraSkipUpdate;
        private static readonly List<scrPlanet> staticPreviewDisabledPlanets =
            new List<scrPlanet>();
        private static readonly EvaluatorPreviewBackend evaluatorBackend = new EvaluatorPreviewBackend();
        private static int lastAudioSyncDiagnosticFrame = -100;
        /// <summary>画面时钟领先阶梯读数多少的诊断日志节流。</summary>
        private static int lastClockLeadLogFrame = -10000;

        // 预览自己拥有时钟。现在没有任何东西再用 Unity 的
        // 缩放时间驱动场景了，所以时钟按未缩放秒推进，
        // 再拿这个绝对时间去问求值器。这取代了旧模型：
        // 往 conductor 里写一个时间，然后让游戏去渲染它。
        private static float previewClockTime;
        /// <summary>
        /// 预览时钟对一次回退读数最多能忍多久，超过就把该读数
        /// 当成真重定位，而不是音频时钟抖动。M3 谱面上实测抖动
        /// 约 12ms；一次 seek 或重启移动的量远大于此。
        /// </summary>
        private const float MaxClockHoldSeconds = 0.05f;
        /// <summary>
        /// 画面时钟相对阶梯读数**最多能领先多少**。
        ///
        /// 时钟现在按帧时间累加、在阶梯跳跃时锐利对齐，所以两次跳跃之间
        /// 它是领先的。但领先必须封顶：帧时间累加略快、或者卡一帧，
        /// 领先量就会累积——实测尾部到过 48.6ms，等于把"落后一个缓冲区"
        /// 换成了"领先一个缓冲区"。取 20ms（约一帧多一点）。
        /// </summary>
        private const float MaxClockLeadSeconds = 0.02f;
        /// <summary>
        /// 起播排程至少留多少余量。
        ///
        /// 不留余量（排到"就是现在"）时，音频**有一半概率整整晚一个缓冲区**
        /// （48kHz × 2048 = 42.7ms）才开始——实测 `[ClipStart] actualSlip`
        /// 在 0 / 0 / 42.7 / 42.7 之间跳。这正是"拖动后播放时好时坏、
        /// 大约一半"的来源。取原版 `ScrubMusicToTime` 用的那个 0.1 秒。
        /// </summary>
        private const double MinSchedulingLeadSeconds = 0.1d;
        // 画面可以落后音频时钟多少，超过就硬贴上去，而不是
        // 缓动去追。一次 seek 或重启移动时钟的量远大于一个 DSP 缓冲区；
        // 这里实测的 2048 采样缓冲区是 0.0427s，所以这个值刚好在它之上，
        // 又在任何真重定位会产生的变化之下。
        private const float ClockHardResyncSeconds = 0.1f;
        // 每帧消掉的剩余误差比例。120fps 下大约十分之一秒
        // 就能追平一整个缓冲区，慢到看不出来，
        // 又快到足以阻止两个时钟分开。
        private const float ClockResyncShare = 0.08f;
        private static bool previewClockRunning;
        private static float previewClockLastUnscaledTime;
        private static bool evaluatorOwnedActive;

        /// <summary>
        /// 当前谱面有没有歌可以跟。
        ///
        /// 依据是谱面的 songFilename，不是 song.clip——后者是异步加载的，
        /// 会话刚起来时还是 null，用它判断会在每次启动都给出「没有歌」。
        /// </summary>
        private static bool HasSongForClock
        {
            get
            {
                scnGame level = scnGame.instance;
                ADOFAI.LevelData data = level == null ? null : level.levelData;
                return data != null && !string.IsNullOrEmpty(data.songFilename);
            }
        }

        // 在 StartEvaluatorPreviewAudio 的排程回调运行期间置位，这样它执行的
        // seek 不会把调用它的那个协程本身取消掉。
        private static bool evaluatorAudioStarting;
        // 在整个原生重启期间置位。重启期间真正播放歌曲的是 StartMusicCo
        // ——它由 scnGame.Play 启动——所以重启执行的那些 seek
        // 绝不能取消它。取消它会让片段没人排程、
        // 歌曲在时间零点无声，球被钉在 seek 到的那个格上。
        private static bool nativeRestartActive;

        /// <summary>
        /// Track B 的 transport 活在编辑器场景内部。与 Track A 不同，这里没有
        /// playMode 切换可以读状态，所以桥直接自己跟踪。
        /// </summary>
        private static bool trackBTransportActive;

        // 交接监视：从第一次拆除之前一直记到提交，逐帧记录视觉状态，
        // 这样「恢复播放时画面卡住/跳一下」这类反馈可以按位置读数，
        // 而不是靠猜。在任何东西被改之前就武装，所以相机基线和
        // 行星位置的重置也在记录范围内。
        private static int lastTrackCTrace = -100000;

        private static bool handoffWatchActive;
        private static int handoffWatchFrames;

        // 单独一个缓冲区：共享的 TrackBTrace 环形缓冲每帧还会收到
        // 实时状态记录器写入的一行，写入速率翻倍，把最早的交接
        // 帧挤出去，来不及 dump。
        private const int HandoffWatchCapacity = 400;
        private static readonly string[] HandoffWatchRows = new string[HandoffWatchCapacity];
        private static int handoffWatchNext;

        // 窗口测量：从发出 StartMusic 到它的排程回调之间的帧。
        private static int trackBStartFrame = -1;
        private static float trackBStartUnscaled;
        // 提交之后继续采样的帧数，这样只有在原生循环接管之后才稳定到
        // 错误状态的画面，也仍然被记录下来。
        private static int trackBPostCommitFrames;

        // 下一次场景加载完成后要恢复到哪个播放头，以及我们为等
        // 重载后的编辑器起来已经等了多少帧。由 HandleSceneReloaded 设置。
        private static float sceneReloadResumeTime = float.NaN;
        private static int sceneReloadWaitFrames;

        // transport 还活着的最后一帧时的播放头。场景加载的通知
        // 是在场景已经换掉之后才发的，所以等 HandleSceneReloaded 跑起来时，
        // conductor 已经属于新场景并报零——从那里读播放头
        // 无论这次运行走到哪，都会从头恢复。
        private static float lastKnownPlayhead;

        /// <summary>最后一次 Track B 按空格的帧号；用来开关重定位痕迹。</summary>
        internal static int LastTrackBPressFrame
        {
            get { return lastTrackBPressFrame; }
        }

        private static int lastTrackBPressFrame = -100000;
        private static int lastTrackBPeriodicFrame = -100000;
        // 从 StartEvaluatorPreviewAudio 开始，到它的排程回调把歌曲放好为止，
        // 有一段音频（重）启动在途。在那之前 conductor 的时钟没有意义
        // ——StartMusicCo 会把 dspTimeSong 回绕到 0，只有那个回调
        // 才把它放回去——所以求值器保持请求的时间，而不是去读时钟。
        private static bool audioStartPending;
        private static float audioStartTime;
        private static bool pendingAudioLogged;
        // 时钟为了等一个可能永远不来的排程回调，被保持了多少帧。
        // 一个被取消的回调会让画面冻在恢复时刻，
        // 而音频继续播下去——这就是持续性失步
        // 从内部看的样子。
        private static int audioStartPendingFrames;
        // 片段被排程到开始可听的 dsp 时刻。画面保持
        // 在恢复时刻不动，直到设备时钟越过它，这样两者一起开始。
        private static double audioStartDsp;
        /// <summary>
        /// 为了等 audioStartDsp 而定帧的帧数。超时就用 MaxAudioStartHoldFrames 放弃。
        /// </summary>
        private static int audioStartHoldFrames;
        /// <summary>
        /// 这次起播要不要"定住画面等音频"。只有排程余量那 0.1 秒才需要；
        /// 倒计时前奏那种长等待绝不定帧（否则画面会僵住整个倒计时）。
        /// </summary>
        private static bool holdPictureForAudioStart;
        // 兜底，防止一个始终不落地的排程回调把画面冻住。
        // 相对它要覆盖的 Windows ~0.5s 缓冲区来说，这个值很宽裕。
        private const int MaxAudioStartHoldFrames = 120;

        internal static bool PreviewRequested
        {
            get { return previewRequested; }
        }

        /// <summary>
        /// 开关详细的拖动/恢复痕迹。追查恢复缺陷时它们必不可少，
        /// 但太吵，不能默认开着。
        /// </summary>
        private static int lastTimeScaleDiagnosticFrame = -1000;

        private static bool VerboseDiagnostics
        {
            get { return Main.Settings != null && Main.Settings.VerboseDiagnostics; }
        }

        internal static bool IsRestartingPreview
        {
            get { return restartPhase != PreviewRestartPhase.None; }
        }

        /// <summary>
        /// 持有的预览时间和时钟状态，暴露出来给相机录制器的诊断用。
        /// 工作台掌控预览期间，每帧都从 conductor 读场景时钟，
        /// 所以某些帧回写到这里的一个值，就是那个时钟里
        /// 测到的回退步的候选原因。
        /// </summary>
        internal static float HeldPreviewTime
        {
            get { return heldPreviewTime; }
        }

        internal static bool IsPreviewClockRunning
        {
            get { return previewClockRunning; }
        }

        /// <summary>
        /// 求值器实际被驱动的那个时钟。预览时钟运行时，
        /// 每帧都会从 conductor 重读，所以把它和 conductor 的值并排记录，
        /// 就能看出抖动的场景时钟是传到了求值器，还是被滤掉了。
        /// </summary>
        internal static float PreviewClockTime
        {
            get { return previewClockTime; }
        }

        public static void InstallInputPatch()
        {
            if (inputPatchAttempted)
            {
                return;
            }

            inputPatchAttempted = true;
            try
            {
                MethodInfo updateMethod = typeof(scnEditor).GetMethod("Update", InstanceFlags, null, Type.EmptyTypes, null);
                MethodInfo updatePrefix = typeof(EditorUpdateInputPatch).GetMethod("Prefix", StaticFlags);
                MethodInfo exitMethod = typeof(scnEditor).GetMethod(
                    "SwitchToEditMode",
                    InstanceFlags,
                    null,
                    new[] { typeof(bool) },
                    null
                );
                MethodInfo exitPrefix = typeof(EditorExitPatch).GetMethod("Prefix", StaticFlags);
                MethodInfo dragCameraMethod = typeof(scnEditor).GetMethod(
                    "DragCamera",
                    InstanceFlags,
                    null,
                    new[] { typeof(Vector3) },
                    null
                );
                MethodInfo dragCameraPrefix = typeof(EditorDragCameraPatch).GetMethod("Prefix", StaticFlags);
                MethodInfo zoomCameraMethod = typeof(scnEditor).GetMethod(
                    "ZoomCamera",
                    InstanceFlags,
                    null,
                    new[] { typeof(float), typeof(bool), typeof(bool) },
                    null
                );
                MethodInfo zoomCameraPrefix = typeof(EditorZoomCameraPatch).GetMethod("Prefix", StaticFlags);

                // scnGame.ApplyEventsToFloors 有两个重载；四参数的那个
                // 静态重载是两个公开入口都汇入的本体，
                // 也是那个带着提前 return、把暂停状态的格晾在一边的重载。
                MethodInfo applyEventsMethod = null;
                foreach (MethodInfo candidate in typeof(scnGame).GetMethods(StaticFlags))
                {
                    if (candidate.Name == "ApplyEventsToFloors" && candidate.GetParameters().Length == 4)
                    {
                        applyEventsMethod = candidate;
                        break;
                    }
                }

                MethodInfo applyEventsPrefix = typeof(ApplyEventsToFloorsPatch).GetMethod("Prefix", StaticFlags);
                MethodInfo applyEventsPostfix = typeof(ApplyEventsToFloorsPatch).GetMethod("Postfix", StaticFlags);
                MethodInfo applyEventsFinalizer = typeof(ApplyEventsToFloorsPatch).GetMethod("Finalizer", StaticFlags);
                if (updateMethod == null || updatePrefix == null
                    || exitMethod == null || exitPrefix == null
                    || dragCameraMethod == null || dragCameraPrefix == null
                    || zoomCameraMethod == null || zoomCameraPrefix == null
                    || applyEventsMethod == null || applyEventsPrefix == null
                    || applyEventsPostfix == null || applyEventsFinalizer == null)
                {
                    throw new MissingMethodException("Could not find the editor preview patch targets.");
                }

                harmony = new Harmony("ADOFAIEditorStudio.PersistentPreview");
                harmony.Patch(updateMethod, new HarmonyMethod(updatePrefix));
                harmony.Patch(exitMethod, new HarmonyMethod(exitPrefix));
                harmony.Patch(dragCameraMethod, new HarmonyMethod(dragCameraPrefix));
                harmony.Patch(zoomCameraMethod, new HarmonyMethod(zoomCameraPrefix));
                harmony.Patch(
                    applyEventsMethod,
                    new HarmonyMethod(applyEventsPrefix),
                    new HarmonyMethod(applyEventsPostfix),
                    null,
                    new HarmonyMethod(applyEventsFinalizer));
                Main.Logger.Log("Persistent preview input and exit patches installed.");

                // 这个 Harmony 版本里 PatchAll(Type) 不是重载，而这个探针
                // 只是诊断——所以显式补丁它那一个方法，而不是
                // 扩大共享补丁集。
                MethodInfo refreshAngles = typeof(scrPlanet).GetMethod(
                    "Update_RefreshAngles", InstanceFlags, null, Type.EmptyTypes, null);
                MethodInfo probePostfix = typeof(HandoffProbePlanetPatch).GetMethod(
                    "Postfix", StaticFlags);
                if (refreshAngles != null && probePostfix != null)
                {
                    harmony.Patch(refreshAngles, null, new HarmonyMethod(probePostfix));
                }

                // 同样理由：重定位痕迹只是诊断，所以它那一个方法
                // 显式打补丁，而不是扩大共享补丁集。
                MethodInfo scrubToFloor = null;
                MethodInfo[] planetMethods = typeof(scrPlanet).GetMethods(InstanceFlags);
                for (int i = 0; i < planetMethods.Length; i++)
                {
                    if (planetMethods[i].Name == "ScrubToFloorNumber")
                    {
                        scrubToFloor = planetMethods[i];
                        break;
                    }
                }
                MethodInfo selectFloor = null;
                MethodInfo[] editorMethods = typeof(scnEditor).GetMethods(InstanceFlags);
                for (int i = 0; i < editorMethods.Length; i++)
                {
                    if (editorMethods[i].Name == "SelectFloor")
                    {
                        selectFloor = editorMethods[i];
                        break;
                    }
                }
                MethodInfo selectPostfix = typeof(SelectFloorFollowPatch).GetMethod("Postfix", StaticFlags);
                if (selectFloor != null && selectPostfix != null)
                {
                    harmony.Patch(selectFloor, null, new HarmonyMethod(selectPostfix));
                }

                MethodInfo scrubPrefix = typeof(TrackBScrubTracer).GetMethod("Prefix", StaticFlags);
                if (scrubToFloor != null && scrubPrefix != null)
                {
                    harmony.Patch(scrubToFloor, new HarmonyMethod(scrubPrefix));
                }

                // 退出追踪器：每一个都只是针对一个方法的诊断，所以用
                // 反射打补丁，而不是扩大基于特性的补丁集。
                PatchTracer(harmony, typeof(scrController), "QuitToMainMenu",
                    typeof(TrackBExitTracer.QuitTrace), "Prefix");
                PatchTracer(harmony, typeof(scnEditor), "SwitchToEditMode",
                    typeof(TrackBExitTracer.EditModeTrace), "Prefix");
                PatchTracer(harmony, typeof(scrLoader), "LoadSceneWithTransition",
                    typeof(TrackBExitTracer.LoadSceneTrace), "Prefix");
                PatchTracer(harmony, typeof(scrLoader), "LoadScene",
                    typeof(TrackBExitTracer.LoadSceneSimpleTrace), "Prefix");
                PatchTracer(harmony, typeof(MonsterLove.StateMachine.StateBehaviour), "ChangeState",
                    typeof(TrackBExitTracer.ChangeStateTrace), "Prefix");
            }
            catch (Exception exception)
            {
                ReportError("Failed to install persistent preview patches", exception);
            }
        }

        private static void PatchTracer(Harmony harmony, Type target, string methodName,
            Type patchType, string patchMethod)
        {
            MethodInfo candidate = null;
            MethodInfo[] methods = target.GetMethods(InstanceFlags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == methodName)
                {
                    candidate = methods[i];
                    break;
                }
            }
            MethodInfo patch = patchType.GetMethod(patchMethod, StaticFlags);
            if (candidate == null || patch == null)
            {
                Main.Logger.Log("[TrackBTrace] could not patch " + target.Name + "." + methodName + ".");
                return;
            }
            harmony.Patch(candidate, new HarmonyMethod(patch));
        }

        /// <summary>
        /// 为工作台打开预览会话。刻意不碰
        /// timelineScrubActive：那个旗标的意思是「指针正在拖动播放头」，
        /// 而空格处理器在它置位期间拒绝切换。这里复用它
        /// 会让空格失灵，直到第一次拖动碰巧把它清掉。
        /// </summary>
        public static bool TryBeginPreviewSession(out string status)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                status = "编辑器尚未就绪";
                return false;
            }

            EnsureEvaluatorSession();
            if (!evaluatorOwnedActive)
            {
                status = "预览求值器不可用，详情见 Mod 日志";
                return false;
            }

            previewRequested = true;
            previewClockRunning = false;
            status = "预览已就绪，按空格播放";
            return true;
        }

        public static void TickPreview(RectTransform viewport)
        {

            // 空闲时很便宜：只有在一次按空格即将把画面交出去时
            // 才会武装测量，而这个守卫是它检查的第一件事。
            HandoffProbe.Tick();
            TickHandoffWatch();
            ClearParticlesOnTeleport();
            TraceAudioSync();
            // 启动窗口的逐帧快照，这样从尚未提交的 transport
            // 画出来的画面能作为数据看见，而不是靠推断。
            LogTrackBWindowFrame();
            // transport 活着期间的周期性采样：音频偏移，以及相机
            // 相对球的摆位，这样「音乐抢拍」和「相机有偏移」
            // 可以按时间读成数字，而不是靠眼睛判断。
            if (trackBTransportActive)
            {
                scrConductor liveConductor = scrConductor.instance;
                if (liveConductor != null)
                {
                    lastKnownPlayhead = (float)liveConductor.songposition_minusi;
                }
            }
            else if (evaluatorOwnedActive)
            {
                // Track C 播放期间也得记播放头。
                //
                // 上面那条只在 trackBTransportActive 时更新，而求值器
                // 接管播放时那个旗标是 false——于是整个播放期间
                // lastKnownPlayhead 都停在旧值。场景重载要靠它恢复播放头
                // （HandleSceneReloaded: sceneReloadResumeTime = lastKnownPlayhead），
                // 拿到的是一个陈旧位置，看起来就是播放被重置。
                //
                // 求值器自己的时钟就是这段播放的权威位置，用它。
                lastKnownPlayhead = previewClockTime;
            }

            if (trackBTransportActive && Time.frameCount - lastTrackBPeriodicFrame >= 60)
            {
                lastTrackBPeriodicFrame = Time.frameCount;
                LogTrackBAudio("run", scrConductor.instance, previewClockTime);
                LogTrackBCamera("run");
                LogTrackBEffects("run");
            }

            if (restartPhase != PreviewRestartPhase.None)
            {
                TickPreviewRestart();
                return;
            }

            TickSceneReloadResume();

            if (!previewRequested)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            // 待处理的重建在这里处理，因为这是唯一一处不管时间轴拖动
            // 是否进行都会每帧运行的地方。重新分析会重建快照缓存的
            // 格/事件/装饰物引用，而这些正是被那次编辑作废的。
            // 播放头留在原地不动。
            if (evaluatorRebuildPending)
            {
                evaluatorRebuildPending = false;
                EnsureEvaluatorSession(true);
            }

            // 在这里清，而不是放进某个模式分支里：它必须在预览活着的每一帧
            // 都运行，而求值器接管的那条路径会在下面提前返回，
            // 到不了静态帧的处理。
            ClearStuckCountdownText();

            if (evaluatorOwnedActive)
            {
                TickEvaluatorOwnedPreview(viewport, editor);
                return;
            }

            if (staticPreviewFrameActive && evaluatorBackend.IsActive)
            {
                if (!float.IsNaN(heldPreviewTime))
                {
                    string evaluatorError;
                    if (!evaluatorBackend.TryEvaluate(heldPreviewTime, out evaluatorError))
                    {
                        ReportError("Failed to refresh evaluator preview frame", new InvalidOperationException(evaluatorError));
                    }
                }

                ApplyStaticPreviewState();
                if (Time.frameCount - lastColliderRefreshFrame >= 30)
                {
                    RestoreEditorClickability();
                }
                EditorSceneInteraction.Tick(viewport, ReadSnapshot());
                return;
            }

            scrController controller = scrController.instance;
            // Track A 通过 inStrictlyEditingMode 判断「用户离开了播放模式」：进入
            // 播放会清掉它，回到编辑器会置上它。Track B 从不离开编辑器，
            // 所以整个运行期间那个旗标都是 true，这个判断在它被置上的
            // 第一帧就杀掉了 transport——ApplyPlayingState 于是从没跑过，AudioListener.pause
            // 从没被清掉，而因为 AudioSettings.dspTime 在监听器暂停时会冻结，
            // scrConductor.Update 从不推进 dspTime，时钟就停住了。对 Track B
            // 来说，transport 自身的存活状态才是正确的信号。
            if (!trackBTransportActive && nativePlaybackStarted && editor.inStrictlyEditingMode)
            {
                nativePlaybackStarted = false;
                return;
            }
            // Track B 保持 nativePlaybackStarted 为 true，理由和 Track A 一样——
            // 正是它把 TickPreview 引到那条调用 ApplyPlayingState 的分支，
            // 而那次调用正是清掉 AudioListener.pause、让歌曲时钟能推进的原因。
            if (!nativePlaybackStarted && !trackBTransportActive)
            {
                return;
            }
            if (controller == null)
            {
                return;
            }

            if (desiredPaused)
            {
                ApplyPausedState(editor, controller, scrConductor.instance);
            }
            else
            {
                ApplyPlayingState(editor, controller);
                // 游戏在它自己的播放路径上会升起这个，只有在检查点开局时才降下，
                // 所以从第一格开始玩的关卡会一直把它留着。
                ClearNativeTransitionPanel();
                LogAudioSync();
                LogNativeRunStateOnce(controller);
            }

            if (Time.frameCount - lastColliderRefreshFrame >= 30)
            {
                RestoreEditorClickability();
            }

            EditorSceneInteraction.Tick(viewport, ReadSnapshot());
        }

        /// <summary>
        /// 临时：原生运行时自己的状态，每次恢复记一次。播放起来了，
        /// 球在动、在推进，但歌曲在时间零点无声、卡在某一格，
        /// 所以问题是那个片段到底有没有被排程过。
        /// </summary>
        private static void LogNativeRunStateOnce(scrController controller)
        {
            if (Time.frameCount - lastNativeRunStateFrame < 120)
            {
                return;
            }
            lastNativeRunStateFrame = Time.frameCount;

            scrConductor conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return;
            }

            // 行星渲染出的角度是 `snappedLastAngle + elapsed * pi * speed * dir`，
            // 所以如果球落地时差半圈，错的是 snappedLastAngle，不是
            // 时钟。把它和格自己的入口角并排报出来，
            // 因为角度本该是贴到那个值上的。
            string planetText = " planet=n/a";
            scrPlanet tracked = controller.chosenPlanet;
            if (tracked != null)
            {
                scrFloor onFloor = tracked.currfloor;
                double entryAngle = onFloor == null ? double.NaN : onFloor.entryangle;
                // 渲染器按 `angle + cosmeticAngle` 绘制，所以残留的 cosmeticAngle
                // 会转动球却不改变它落地的位置——这正是「球
                // 偏了 180 度但打击点落得准」的样子。求值器
                // 直接写那个字段，而游戏只在一个
                // tween 的 OnKill 里清它，所以值得显式盯着。
                planetText = " snap=" + tracked.snappedLastAngle.ToString("0.000")
                    + " entryangle=" + entryAngle.ToString("0.000")
                    + " diff=" + (tracked.snappedLastAngle - entryAngle).ToString("0.000")
                    + " angle=" + tracked.angle.ToString("0.000")
                    + " cosmetic=" + tracked.cosmeticAngle.ToString("0.000")
                    + " rendered=" + (tracked.angle + tracked.cosmeticAngle).ToString("0.000")
                    + " lastHit=" + tracked.player.lastHit.ToString("0.000")
                    + " pos=" + tracked.transform.position.x.ToString("0.00") + ","
                    + tracked.transform.position.y.ToString("0.00");

                scrCamera cam = scrCamera.instance;
                scnGame lvl = scnGame.instance;
                if (cam != null)
                {
                    planetText += " camFollow=" + cam.followMode
                        + " zoom=" + cam.zoomSize.ToString("0.000")
                        + " camLocal=" + cam.transform.localPosition.x.ToString("0.00")
                        + "," + cam.transform.localPosition.y.ToString("0.00")
                        + " camParent=" + (lvl == null || lvl.camParent == null
                            ? "n/a"
                            : lvl.camParent.position.x.ToString("0.00") + ","
                                + lvl.camParent.position.y.ToString("0.00"));
                }
            }

            Main.Logger.Log("[NativeRun] f=" + Time.frameCount
                + " state=" + controller.state
                + " controllerPaused=" + controller.paused
                + " pausedInPlay=" + (scnEditor.instance == null ? "?" : scnEditor.instance.pausedInPlayMode.ToString())
                + " timeScale=" + Time.timeScale.ToString("0.##")
                + " songIsPlaying=" + conductor.song.isPlaying
                + " songTime=" + conductor.song.time.ToString("0.000")
                + " songSched=" + conductor.song.isPlaying
                + " dspSong=" + conductor.dspTimeSong.ToString("0.000")
                + " hasStarted=" + conductor.hasSongStarted
                + " seqID=" + controller.currentSeqID
                + " listenerPause=" + AudioListener.pause
                + planetText);
        }

        private static int lastNativeRunStateFrame = -1000;

        public static EditorPlaybackSnapshot ReadSnapshot()        {
            var snapshot = new EditorPlaybackSnapshot
            {
                Available = false,
                State = EditorPlaybackState.Editing,
                CurrentTime = 0f,
                Duration = 0f
            };

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return snapshot;
            }

            try
            {
                snapshot.Available = true;

                // 求值器接管的预览报自己的时钟：原生 transport
                // 字段不参与，所以从它们推导状态会报
                // 「Editing」，而画面明显正在被驱动。
                if (evaluatorOwnedActive || evaluatorBackend.IsActive)
                {
                    snapshot.State = previewClockRunning
                        ? EditorPlaybackState.Playing
                        : EditorPlaybackState.Paused;
                    snapshot.CurrentTime = Mathf.Max(0f, previewClockTime);
                    if (editor != null && editor.floors != null && editor.floors.Count > 0)
                    {
                        snapshot.Duration = Convert.ToSingle(
                            editor.floors[editor.floors.Count - 1].entryTime);
                    }
                    return snapshot;
                }

                scrController controller = scrController.instance;
                snapshot.State = staticPreviewFrameActive && evaluatorBackend.IsActive
                    ? EditorPlaybackState.Paused
                    : previewRequested && nativePlaybackStarted
                    ? desiredPaused ? EditorPlaybackState.Paused : EditorPlaybackState.Playing
                    : !editor.playMode
                        ? EditorPlaybackState.Editing
                        : editor.pausedInPlayMode || (controller != null && controller.paused)
                            ? EditorPlaybackState.Paused
                            : EditorPlaybackState.Playing;

                scrConductor conductor = scrConductor.instance;
                if (desiredPaused && !float.IsNaN(heldPreviewTime))
                {
                    snapshot.CurrentTime = Mathf.Max(0f, heldPreviewTime);
                }
                else if (conductor != null)
                {
                    snapshot.CurrentTime = Mathf.Max(0f, Convert.ToSingle(conductor.songposition_minusi));
                }

                if (editor.floors != null && editor.floors.Count > 0)
                {
                    snapshot.Duration = Mathf.Max(0f, Convert.ToSingle(editor.floors[editor.floors.Count - 1].entryTime));
                }
                if (conductor != null && conductor.song != null && conductor.song.clip != null)
                {
                    snapshot.Duration = Mathf.Max(snapshot.Duration, conductor.song.clip.length);
                }

                lastError = null;
            }
            catch (Exception exception)
            {
                ReportError("Failed to read persistent preview state", exception);
            }

            return snapshot;
        }

        public static bool TryTogglePlayback(out string status)
        {
            status = "编辑器尚未就绪";
            if (timelineScrubActive)
            {
                status = "正在拖动时间线";
                return false;
            }
            if (restartPhase != PreviewRestartPhase.None)
            {
                status = "正在回到开始位置";
                return false;
            }
            if (nativeHandoffPending)
            {
                // 兜底。清除它的是捕获的回调，而如果把视口在武装
                // 与捕获之间隐藏了起来，那个回调会被跳过——这会让
                // 旗标永久置位，空格完全失效。
                if (Time.frameCount - handoffArmedFrame > 30)
                {
                    nativeHandoffPending = false;
                    status = "正在切换到原生播放";
                    return TryBeginPreviewRestart(handoffTargetTime, true, out status, true);
                }
                status = "正在准备原生播放";
                return true;
            }
            if (lastToggleFrame == Time.frameCount)
            {
                status = null;
                return true;
            }
            lastToggleFrame = Time.frameCount;

            scnEditor currentEditor = scnEditor.instance;
            if (currentEditor == null)
            {
                status = "编辑器尚未就绪";
                return false;
            }

            // 两种模式。作者编辑或拖动时由求值器渲染，
            // 播放则归游戏自己的 transport。
            //
            // 原版播放值得这次交接，因为这样一来画面就是游戏
            // 画出来的样子，包含求值器表达不了的一切：滤镜
            // 自己的动画、粒子、视频，任何由引擎时钟驱动
            // 而不是谱面时间轴函数的东西。它也让「暂停」成为真正的
            // 暂停——timeScale 为 0，整个场景冻住，和截屏一致。
            // 这里是从一个活着的运行时恢复，不是重建。
            //
            // 让运行时继续活着正是「暂停而不是退出」的全部意义，而
            // 一次拖动恰好把它留在那个状态：已暂停，仍停在
            // 播放头处，只把画面交给求值器。继续往下走到
            // 重启会拆掉那个运行时——SwitchToEditMode，然后 Play——
            // 再重建它，这正是重新进入时画面跳一下的原因。
            //
            // 拖动进行中时决定权归拖动定位，所以这里让位；
            // TryBeginTimelineScrub 自己会把交接驱动回去。
            // Track B 是现在唯一的 transport，所以没有第二条分支会丢掉
            // 路由。Track A 的恢复过去就坐在这里，而它无法与
            // 编辑器的静止状态区分开：scnEditor.playMode 是个计算属性
            //
            //     if (!pausedInPlayMode) return !controller.paused;
            //     return true;
            //
            // 只要 controller 没暂停，它在编辑器里就已经报 true，
            // 而 NativeRuntimeIsLive() 又把它和一个早先 Track A 运行留下的旗标做 OR——
            // 所以那条分支总会被走，Track B 不可达。与其继续
            // 找一个能把两者分开的谓词，不如把分支删掉：
            // 用户已经放弃的那个 transport 不再是候选了。
            // 活着的 Track B transport 拥有暂停和恢复。TrySetPaused 对它走
            // 最小路径：因为什么都没被拆掉，nativeTransportHeld 保持 false，
            // 整个重建并 seek 的代码块被跳过，只剩下游戏自己的暂停
            // 开关。
            // Track C：播放归求值器，所以没有交接、没有什么
            // 需要做无缝。它在下面那些原生分支之前到达，而那些分支现在只被
            // 尚未移除的重启路径用到。
            if (!timelineScrubActive)
            {
                return TrySetEvaluatorPlayback(!previewClockRunning, out status);
            }

            if (trackBTransportActive && !timelineScrubActive)
            {
                bool wantPaused = !desiredPaused;

                // 这里暂停不需要求值器会话。Track A 暂停时把画面
                // 交给求值器，因为它已经把运行时拆了；Track B 的
                // 运行时就是画面，而游戏自己的暂停已经把它完全冻住，
                // 和真游戏里的方式一样：
                //     scrVfxPlus.Update    在 ctrl.paused 时返回，所以特效和滤镜停止
                //     scrConductor.Update  在监听器暂停期间停止推进 dspTime
                //                          （AudioSettings.dspTime 冻结），所以球、它的
                //                          角度和跟随相机都随之停下
                // 在这里开一个会话会适得其反：EnsureEvaluatorSession 会把
                // 求值器时钟重置为 0，而 ApplyPausedState 修不了它，因为它在
                // nativeTransportHeld 置位后就会提前返回，于是冻住的那帧显示的是
                // 谱面的开头，而不是播放头。
                if (!wantPaused && evaluatorOwnedActive)
                {
                    evaluatorOwnedActive = false;
                    EndEvaluatorOwnedScrub();
                }

                status = wantPaused ? "预览已冻结" : "继续播放";
                HandoffProbe.LogRoute(wantPaused ? "trackB-pause" : "trackB-resume");
                return TrySetPaused(wantPaused, out status);
            }

            // Track B：transport 直接在编辑器场景里启动，所以没有
            // 拆除要反向操作，也没有原生启动流程要用手工重建。过去在这里
            // 武装的交接捕获，存在只是为了在 scnEditor.Play 换场景时
            // 盖住屏幕；既然没有东西在换场景，就没有什么要盖，
            // 画面直接从拖动留下的那一帧继续。
            HandoffProbe.Arm("trackB");
            HandoffProbe.StampEvaluator("pre-trackB");
            return StartEditorNativePlayback(Mathf.Max(0f, previewClockTime), out status);
        }

        /// <summary>
        /// 一次交接应该从哪个时间重新开始播放，在交接被武装的那一帧
        /// 与它的捕获完成那一帧之间保存。
        /// </summary>
        private static float handoffTargetTime;
        private static int handoffArmedFrame;

        /// <summary>
        /// 是否有一个原生预览运行时已加载并正在运行关卡，与之相对的是
        /// 编辑器停在编辑模式。
        /// </summary>
        private static bool NativeRuntimeIsLive(scnEditor editor)
        {
            return nativePlaybackStarted
                || (editor != null && editor.playMode && !editor.inStrictlyEditingMode);
        }

        internal static bool TryBeginTimelineScrub(out string status)
        {
            status = null;
            // 用户自己动播放头了，跳转的走位立刻让位 —— 两个写入者同时改
            // previewClockTime 会让画面一顿一顿地往回拉。
            CancelPlayheadTravel();
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                status = "编辑器尚未就绪";
                return false;
            }

            // 拖动把画面交还给求值器。一个仍在播放的原生运行时
            // 必须先停止推进——相机上有两个写入者
            // 是这个项目踩得最多的缺陷。
            // 选暂停而不是退出：退出会在好几帧里慢慢拆掉运行时，
            // 而这个入口只被调用一次，拖动就会被直接丢掉。
            // 暂停保住运行时和播放头，空格就从
            // 这里原样恢复。
            if (NativeRuntimeIsLive(editor))
            {
                string pauseStatus;
                TrySetPaused(true, out pauseStatus);
            }

            // 拖动会移动播放头，之后 Track B 无法原地恢复：
            // 它的恢复路径是 TrySetPaused，只翻转游戏的暂停，不做
            // 新位置需要的任何事——事件组件必须重新烘焙，
            // 特效队列要从它们重新填充，运行时要被拖动到新格。这些
            // 全都在 StartEditorNativePlayback 里，那是被验证过
            // 能工作的路径。在这里清掉旗标，会把下一次空格送回去走它，
            // 而不是走恢复路径——这正是第二次拖动没有相机、
            // 轨道也没恢复的原因。
            trackBTransportActive = false;

            // 求值器直接渲染编辑器场景，从不进入播放
            // 模式，所以原生 transport 状态（重启阶段、原生
            // 交接、暂停切换）在这里都不再适用。
            EnsureEvaluatorSession();

            // 抓住播放头就从播放手里接过时钟；让它继续
            // 跑，会让指针底下的帧自己往前走。
            if (previewClockRunning)
            {
                previewClockRunning = false;
                StopEvaluatorPreviewAudio();
            }

            lastTimelineScrubFloor = -1;
            lastTimelineScrubEditorId = 0;
            lastTimelineScrubTime = float.NaN;
            lastNativeCameraDiagnosticFloor = -1;
            lastNativeCameraDiagnosticTime = float.NaN;
            timelineScrubActive = true;
            status = "拖动播放头以定位画面";
            return true;
        }

        /// <summary>
        /// 拖动时间轴标尺/播放头时的定位。由时间轴的 OnPointerDown → SubmitPendingScrub 调用。
        ///
        /// 【现状:仍在走原生机制,待改造】
        /// 下面 nativeScrubPrepared 这条分支用的是 Track A/B 留下的常驻原生运行时 —— 通过
        /// TrySeekResidentNativeScene 去挪原生的时间轴。下面的 staticPreviewFrameActive 分支
        /// 才是 Track C 那一套(直接设逻辑时钟 + 冻结画面)。
        ///
        /// 后续改造方向:把 nativeScrubPrepared 分支去掉,只保留静态预览分支,这样拖动定位
        /// 也是求值器直接重绘,和空格播放一样无感。
        /// </summary>
        internal static bool TryScrubTimelineToTime(float requestedTime, out string status)
        {
            status = "无法定位预览";
            scnEditor editor = scnEditor.instance;
            scrController controller = scrController.instance;
            scrConductor conductor = scrConductor.instance;
            if (editor == null || controller == null || conductor == null
                || editor.floors == null || editor.floors.Count == 0)
            {
                return false;
            }

            try
            {
                if (!timelineScrubActive)
                {
                    return false;
                }

                float targetTime = Mathf.Max(0f, requestedTime);

                if (nativeScrubPrepared)
                {
                    string nativeError;
                    if (!TrySeekResidentNativeScene(editor, controller, conductor, targetTime, out nativeError))
                    {
                        status = "原生时间容器定位失败：" + nativeError;
                        return false;
                    }

                    lastTimelineScrubTime = targetTime;
                    heldPreviewTime = targetTime;
                    desiredPaused = true;
                    nativeTransportHeld = false;
                    pauseReapplyRequested = false;
                    ApplyPausedState(editor, controller, conductor);
                    Cursor.visible = true;
                    status = "已定位到 " + FormatTimelineTime(targetTime);
                    return true;
                }

                if (staticPreviewFrameActive)
                {
                    AudioListener.pause = true;
                    SetLogicalConductorTime(conductor, targetTime);
                    heldPreviewTime = targetTime;
                    desiredPaused = true;
                    nativeTransportHeld = false;

                    string evaluatorError;
                    if (!evaluatorBackend.TryEvaluate(targetTime, out evaluatorError))
                    {
                        status = "静态预览求值失败：" + evaluatorError;
                        return false;
                    }

                    lastTimelineScrubTime = targetTime;
                    ApplyStaticPreviewState();
                    Cursor.visible = true;
                    status = "已定位到 " + FormatTimelineTime(targetTime);
                    return true;
                }

                // 求值器掌控整个画面。这里不再推进任何原生
                // 东西：不拖动球、不写 conductor、不冻结场景。
                // 只是把时钟设好，然后拿这个时间去问求值器。
                AudioListener.pause = true;
                previewClockTime = targetTime;
                previewClockLastUnscaledTime = Time.unscaledTime;

                // 这些记录之后一次恢复必须把原生运行时放到哪里，上面
                // 两条分支出于同样理由设置它们。这里漏掉它们
                // 意味着拖动只移动了求值器的时钟：画面跟着走，
                // 但恢复随后把实时的 conductor 时间——它从没动过
                // ——当作目标，完全跳过了 seek。结果是时间轴
                // 和歌曲还停在旧位置，而画面显示的是
                // 新位置。
                lastTimelineScrubTime = targetTime;
                heldPreviewTime = targetTime;
                desiredPaused = true;
                nativeTransportHeld = false;
                pauseReapplyRequested = false;

                string vfxError;
                if (evaluatorBackend.IsActive
                    && !evaluatorBackend.TryEvaluate(targetTime, out vfxError))
                {
                    Main.Logger.Log("[EvaluatorOwned] evaluation failed at "
                        + FormatTimelineTime(targetTime) + ": " + vfxError);
                }

                Cursor.visible = true;
                status = "已定位到 " + FormatTimelineTime(targetTime);
                return true;
            }
            catch (Exception exception)
            {
                AudioListener.pause = true;
                lastTimelineScrubFloor = -1;
                lastTimelineScrubEditorId = 0;
                lastTimelineScrubTime = float.NaN;
                ReportError("Failed to scrub the editor timeline", exception);
                status = "时间线定位失败，详情见 Mod 日志";
                return false;
            }
        }

        internal static void EndTimelineScrub()
        {
            EndTimelineScrub(true);
        }

        /// <summary>
        /// 拖动定位会话是否还活着。时间轴用它判断一次待提交的
        /// 定位请求是不是迟到的残留。
        /// </summary>
        internal static bool IsTimelineScrubActive
        {
            get { return timelineScrubActive; }
        }

        /// <summary>
        /// 结束拖动定位。
        ///
        /// applyTime 为 true 时把时钟停在最后定位到的时间（正常松手的行为）；
        /// 为 false 时只清状态、不碰时钟——给「播放要开始了，把残留的
        /// 拖动状态丢掉」用。
        ///
        /// 后者是必要的：一个从拖动留下的 lastTimelineScrubTime 会在
        /// 播放开始后被提交，把求值器时钟拉回拖动时的位置，看起来就是
        /// 时间线突然退一段、前后画面接不上。
        /// </summary>
        internal static void EndTimelineScrub(bool applyTime)
        {
            if (!timelineScrubActive)
            {
                return;
            }

            timelineScrubActive = false;
            float targetTime = lastTimelineScrubTime;
            lastTimelineScrubFloor = -1;
            lastTimelineScrubEditorId = 0;
            lastTimelineScrubTime = float.NaN;

            if (applyTime && !float.IsNaN(targetTime))
            {
                // 松开指针让画面留在原处；时钟
                // 就此停住。不需要冻任何东西，因为画面是
                // 时钟的纯函数。
                previewClockTime = targetTime;
                previewClockRunning = false;
                Main.Logger.Log("[EvaluatorOwned] scrub held at "
                    + FormatTimelineTime(targetTime) + ".");
                LogFloorRouteState("scrub-end");
            }
            Cursor.visible = true;
        }

        internal static void CancelTimelineScrub()
        {
            lastTimelineScrubFloor = -1;
            lastTimelineScrubEditorId = 0;
            lastTimelineScrubTime = float.NaN;
            if (evaluatorOwnedActive)
            {
                evaluatorOwnedActive = false;
                EndEvaluatorOwnedScrub();
            }
        }

        /// <summary>
        /// 「回到开头」。由时间轴上的回到开头按钮调用。
        ///
        /// **走的是「把播放头拖到最前面」那条路**：进入拖动定位 → 定位到 0 → 退出。
        ///
        /// 原来它转调 `TryBeginPreviewRestart`（Track A/B 留下的原生重建）：停掉求值器、
        /// 切回游戏自己的 transport、再由状态机把画面重建一遍。问题是**那个重建不会把
        /// 已经生效的东西全部退回开头状态** —— 实测按一下回到开头，位置确实回去了，但
        /// 一堆元素还留在原处（这个毛病从这个按钮诞生起就有）。
        ///
        /// 而拖动定位这条路由求值器直接重绘、画面是时钟的纯函数，拖到 0 时画出来的就是
        /// 开头那一帧，没有"残留"这回事。所以这里不再动原生运行时，直接复用那条路 ——
        /// 也正是原本记在 `TryBeginPreviewRestart` 上的改造方向。
        ///
        /// （拖动那条路里还有一条 `nativeScrubPrepared` 的遗留原生分支，但它只被赋过
        /// false、从没被置 true，实际走的一直是求值器那条。）
        /// </summary>
        internal static bool TryReturnToStart(out string status)
        {
            if (!TryBeginTimelineScrub(out status))
            {
                return false;
            }

            if (!TryScrubTimelineToTime(0f, out status))
            {
                // 定位没成功：把拖动状态丢掉，别留下一个"还在拖"的假状态 ——
                // 那会让下一次播放开始后被拖回旧时间（见 EndTimelineScrub 的说明）。
                EndTimelineScrub(false);
                return false;
            }

            // 停在最后定位到的时间（这里是 0），画面就留在开头。
            EndTimelineScrub(true);
            return true;
        }

        private static bool HasNativeScrubBoundaryBetween(float lower, float upper)
        {
            int low = 0;
            int high = nativeScrubBoundaries.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (nativeScrubBoundaries[middle] <= lower + 0.0001f)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low < nativeScrubBoundaries.Count
                && nativeScrubBoundaries[low] <= upper + 0.0001f;
        }

        private static bool TrySeekResidentNativeScene(
            scnEditor editor,
            scrController controller,
            scrConductor conductor,
            float targetTime,
            out string error)
        {
            error = null;
            try
            {
                int floorIndex = FindFloorAtOrBefore(editor.floors, targetTime);
                int editorId = editor.GetInstanceID();
                bool needsRebuild = lastTimelineScrubFloor < 0
                    || editorId != lastTimelineScrubEditorId
                    || float.IsNaN(lastTimelineScrubTime)
                    || (targetTime + 0.0001f < lastTimelineScrubTime
                        && (floorIndex != lastTimelineScrubFloor
                            || HasNativeScrubBoundaryBetween(targetTime, lastTimelineScrubTime)));

                AudioListener.pause = true;
                Time.timeScale = 0f;

                if (needsRebuild)
                {
                    Main.Logger.Log("[NativeScrub] checkpoint rebuild floor=" + floorIndex
                        + " target=" + FormatTimelineTime(targetTime) + ".");
                    if (!RebuildResidentNativeCheckpoint(editor, controller, conductor,
                        floorIndex, targetTime, out error))
                    {
                        return false;
                    }
                }
                else
                {
                    if (floorIndex != lastTimelineScrubFloor)
                    {
                        Main.Logger.Log("[NativeScrub] advance floor=" + floorIndex
                            + " target=" + FormatTimelineTime(targetTime) + ".");
                        AdvanceResidentNativeFloor(editor, floorIndex);
                    }

                    controller.currentSeqID = floorIndex;
                    SetLogicalConductorTime(conductor, targetTime);
                    UpdateResidentNativePlayers(editor, floorIndex);
                    ApplyResidentNativeEffects(editor, controller, floorIndex, targetTime);
                }

                lastTimelineScrubFloor = floorIndex;
                lastTimelineScrubEditorId = editorId;
                lastTimelineScrubTime = targetTime;
                Physics2D.SyncTransforms();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                ReportError("Failed to seek resident native preview", exception);
                return false;
            }
        }

        private static bool RebuildResidentNativeCheckpoint(
            scnEditor editor,
            scrController controller,
            scrConductor conductor,
            int floorIndex,
            float targetTime,
            out string error)
        {
            error = null;
            if (editor.customLevel == null)
            {
                error = "The custom level runtime is unavailable.";
                return false;
            }

            try
            {
                KillResidentNativeScrubTweens();
                GCS.checkpointNum = 0;

                // ResetScene + FinishCustomLevelLoading 只是原生
                // 重启生命周期的一半。它漏掉了 Conductor.Rewind/Start、
                // Controller.Awake_Rewind/Start_Rewind 和每个 player 的 Rewind，
                // 让行星、打击音和事件队列都还挂在旧的那次
                // 运行上。改用和游戏自己 ResetCustomLevel 相同的 ResetScene -> Play 链条，
                // 然后在 StartMusicCo 走到
                // 它那步延后的 PlayScheduled/song.time = 0 之前接管。
                editor.customLevel.ResetScene(false);
                if (!editor.customLevel.Play(0, false))
                {
                    error = "The native level restart was rejected.";
                    return false;
                }
                CancelPendingConductorMusicStart(conductor);
                editor.customLevel.isLoading = false;
                GCS.checkpointNum = 0;
                controller.currentSeqID = 0;
                controller.startedFromCheckpoint = false;
                controller.forceNoCountdown = true;
                conductor.fastTakeoff = true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
            if (controller.currentState != States.PlayerControl)
            {
                controller.ChangeState(States.PlayerControl);
            }

            SetExactConductorTime(conductor, targetTime, false);
            AudioListener.pause = true;
            if (AudioManager.Instance != null)
            {
                AudioManager.Instance.StopAllSounds();
            }

            ApplyResidentNativeEffects(editor, controller, floorIndex, targetTime);
            Main.Logger.Log("[NativeScrub] full native lifecycle rebuilt at "
                + FormatTimelineTime(targetTime) + "; floor=" + floorIndex
                + " checkpoint=" + GCS.checkpointNum + ".");
            return true;
        }

        private static void AdvanceResidentNativeFloor(scnEditor editor, int floorIndex)
        {
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                player.planetarySystem.ScrubToFloorNumber(floorIndex, null, true);
            }
        }

        private static void UpdateResidentNativePlayers(scnEditor editor, int floorIndex)
        {
            if (editor.floors == null || editor.floors.Count == 0)
            {
                return;
            }

            double floorTime = editor.floors[Mathf.Clamp(floorIndex, 0, editor.floors.Count - 1)].entryTime;
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                player.lastHit = floorTime;
                scrPlanet chosenPlanet = player.planetarySystem.chosenPlanet;
                if (chosenPlanet != null)
                {
                    chosenPlanet.Update_RefreshAngles();
                }
            }
        }

        private static void ApplyResidentNativeEffects(
            scnEditor editor,
            scrController controller,
            int floorIndex,
            float targetTime)
        {
            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx != null)
            {
                AdvanceResidentNativeScrubTweens(vfx, targetTime);
                vfx.ScrubToTime(targetTime);
                PauseScrubbedVfxTweens(vfx);
            }

            scrCamera camera = scrCamera.instance;
            if (camera != null && camera.camobj != null)
            {
                camera.UpdateSize();
                camera.camobj.enabled = true;
            }

            scrDecorationManager decorationManager = scrDecorationManager.instance;
            bool wasPaused = controller != null && controller.paused;
            if (controller != null)
            {
                // scrDecoration.UpdatePosition 在暂停期间会刻意压制跟随
                // 行星的偏移。刷新已经驻留的装饰物对象时，
                // 临时把原生状态暴露出来。
                controller.paused = false;
            }
            if (decorationManager != null && decorationManager.allDecorations != null)
            {
                foreach (scrDecoration decoration in decorationManager.allDecorations)
                {
                    if (decoration != null)
                    {
                        decoration.UpdatePosition();
                    }
                }
            }
            if (controller != null)
            {
                controller.paused = wasPaused;
            }

            SyncResidentNativeCameraFocus(controller);
            LogResidentNativeCameraState(editor, floorIndex, targetTime);
        }

        /// <summary>
        /// 清除 seek 之后行星的粒子系统。尾迹在世界空间发射，
        /// 所以被瞬移的行星会把尾迹留在旧位置；
        /// 原生 Rewind 出于同样理由清除它们。
        /// </summary>
        // 每个行星上一帧的位置，这样能把跳跃和正常移动区分开。
        private static readonly List<Vector3> lastPlanetPositions = new List<Vector3>();
        // 行星一帧里移动超过这个距离就是被重定位了（一次拖动、一次 seek），
        // 而不是在行进：一格是 1.5 单位，一帧的行程只是它的一小部分。
        private const float TeleportClearDistance = 2f;

        /// <summary>
        /// 当行星是被重定位而不是被移动时，清除它的粒子。
        ///
        /// 尾迹在世界空间发射，所以被瞬移的行星——拖动播放头
        /// 做的就是这件事——会把尾迹留在它来的那个位置，
        /// 尾迹就停在那儿，而球继续往前走。原生 Rewind 出于同样理由
        /// 清除粒子系统（PlanetRenderer.DisableParticles 用的是
        /// StopEmittingAndClear）。ClearParticles 早就为这个存在，却从来
        /// 没被调用过。
        ///
        /// 用阈值，而不是每次 seek 都清：拖动走播放头时球
        /// 在连续移动，它的尾迹是真实运动，应当保留。只有帧间
        /// 的跳跃才算重定位。
        /// </summary>
        // 音画关系的滚动窗口，这样间歇性失步能留下
        // 记录，而不用有人守在旁边抓现行。行只是环里的廉价字符串；
        // 当测到的偏差越过阈值，整个环就被打印出来，覆盖
        // 偏差之前的那些帧以及偏差期间。
        private const int SyncTraceCapacity = 240;
        private static readonly string[] SyncTraceRows = new string[SyncTraceCapacity];
        private static int syncTraceNext;
        private static bool syncTraceDumped;
        private static int syncTraceClearFrames;
        // 片段与场景时钟声称的位置之间 30ms 的偏差。低于
        // 这个值是解码器量化；高于它，人能在拍子上看出来。
        private const double SyncTraceThreshold = 0.03d;

        /// <summary>
        /// 临时：记录音乐实际在哪，对照画面声称它
        /// 该在哪，求值器播放的每一帧都记。
        ///
        /// drift 才是关键数字——song.time 是片段的真实位置，
        /// expectedClip 由求值器绘制所用的同一个场景时钟推导，所以它们的
        /// 差正好就是音画偏移。一直贴近零的一次运行是同步的；
        /// 离开零并持续下去的台阶就是间歇性失步。
        /// </summary>
        private static void TraceAudioSync()
        {
            // 诊断门控。这条日志走 UMM 的 Logger，而它内部第一句是
            // Console.WriteLine——同步写控制台。实测一条这样的日志单独
            // 占掉过一整帧 176ms。这一族都是排查特定问题时加的，
            // 不该在正常播放路径上跑。
            if (!VerboseDiagnostics)
            {
                return;
            }
            if (!previewClockRunning)
            {
                syncTraceNext = 0;
                syncTraceDumped = false;
                syncTraceClearFrames = 0;
                return;
            }

            scrConductor conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return;
            }

            double pitch = Math.Max(0.001d, conductor.song.pitch);
            double scene = conductor.songposition_minusi;

            // **只在歌真的在放的时候量。**
            //
            // 不设这道闸的话，暂停 / 场景重建那几帧里 `songTime` 是 0 而
            // 场景时间还在别处，算出来是几百毫秒到几十秒的假"漂移"，
            // 一下就把真正要看的几十毫秒淹掉（实测就是这么被淹没的：
            // 四条分歧全是 playing=False）。
            if (!conductor.song.isPlaying || !conductor.hasSongStarted)
            {
                return;
            }

            double songTime = conductor.song.time;
            double countdown = GetSeparateCountdownTime(conductor);
            // **必须带上 `cal_i * pitch`。** AES 自己的推导（见 5267 行附近）：
            //   clip = scene + addoffset - countdown + cal_i * pitch
            // 少了最后一项时，量出来的"漂移"会正好等于校准值——
            // 实测 cal_i=0.248 而 deltaMs=238~330ms，那个 248ms 全是这一项。
            double expectedClip = scene + conductor.addoffset - countdown
                + scrConductor.calibration_i * pitch;
            // **不能乘 pitch。**
            //
            // `conductor.song.time` 已经是**片段位置**，而 AudioSource 在
            // pitch=1.5 时片段位置本来就是按 1.5 倍速前进的——再乘一次
            // pitch 等于把它按 pitch² 缩放，报出来的是假的漂移。
            //
            // 场景和片段是同一套单位（见 scrConductor 的
            // `scene = (dsp - dspTimeSong - cal) * pitch - addoffset`，
            // 而那个 `* pitch` 正是片段位置），所以直接相减。
            // pitch=100 时两式等价，所以这个错误只在 pitch≠100 时显形。
            double drift = songTime - expectedClip;

            string row = "f" + Time.frameCount
                + " scene=" + scene.ToString("0.000")
                + " songTime=" + songTime.ToString("0.000")
                + " expectedClip=" + expectedClip.ToString("0.000")
                + " driftMs=" + (drift * 1000d).ToString("0.0")
                + " playing=" + conductor.song.isPlaying
                + " started=" + conductor.hasSongStarted
                + " dspSong=" + conductor.dspTimeSong.ToString("0.000")
                + " dsp=" + AudioSettings.dspTime.ToString("0.000")
                + " tScale=" + Time.timeScale.ToString("0.##");

            SyncTraceRows[syncTraceNext] = row;
            syncTraceNext = (syncTraceNext + 1) % SyncTraceCapacity;

            double magnitude = Math.Abs(drift);
            if (magnitude >= SyncTraceThreshold)
            {
                syncTraceClearFrames = 0;
                if (!syncTraceDumped)
                {
                    syncTraceDumped = true;
                    Main.Logger.Log("[SyncTrace] divergence " + (drift * 1000d).ToString("0.0")
                        + "ms at frame " + Time.frameCount + "; dumping "
                        + SyncTraceCapacity + " frames.");
                    for (int i = 0; i < SyncTraceCapacity; i++)
                    {
                        string dump = SyncTraceRows[(syncTraceNext + i) % SyncTraceCapacity];
                        if (dump != null)
                        {
                            Main.Logger.Log("[SyncTrace] " + dump);
                        }
                    }
                    Main.Logger.Log("[SyncTrace] ==== end ====");
                }
                return;
            }

            // 回到同步：在持续低于阈值一段时间之后，为下一次武装。
            syncTraceClearFrames++;
            if (syncTraceDumped && syncTraceClearFrames > 120)
            {
                syncTraceDumped = false;
                Main.Logger.Log("[SyncTrace] back within " + (SyncTraceThreshold * 1000d).ToString("0")
                    + "ms for " + syncTraceClearFrames + " frames.");
            }
        }

        private static void ClearParticlesOnTeleport()
        {
            scrPlayerManager players = scrPlayerManager.instance;
            if (players == null || players.players == null)
            {
                return;
            }

            int index = 0;
            for (int p = 0; p < players.players.Length; p++)
            {
                scrPlayer player = players.players[p];
                if (player == null || player.planetarySystem == null
                    || player.planetarySystem.planetList == null)
                {
                    continue;
                }

                for (int i = 0; i < player.planetarySystem.planetList.Count; i++)
                {
                    scrPlanet planet = player.planetarySystem.planetList[i];
                    if (planet == null)
                    {
                        index++;
                        continue;
                    }

                    Vector3 position = planet.transform.position;
                    if (index < lastPlanetPositions.Count)
                    {
                        float moved = (position - lastPlanetPositions[index]).sqrMagnitude;
                        if (moved > TeleportClearDistance * TeleportClearDistance
                            && planet.planetRenderer != null)
                        {
                            planet.planetRenderer.ClearParticles();
                        }
                        lastPlanetPositions[index] = position;
                    }
                    else
                    {
                        lastPlanetPositions.Add(position);
                    }
                    index++;
                }
            }
        }

        private static void SyncResidentNativeCameraFocus(scrController controller)
        {
            scrCamera camera = scrCamera.instance;
            if (camera == null || !camera.followMode || controller == null)
            {
                return;
            }

            scrPlanet focusPlanet = controller.playerOne == null
                || controller.playerOne.planetarySystem == null
                ? controller.chosenPlanet
                : controller.playerOne.planetarySystem.chosenPlanet;
            if (focusPlanet == null)
            {
                focusPlanet = camera.furthestPlanet;
            }
            if (focusPlanet == null)
            {
                return;
            }

            // 正常落地会调用 scrCamera.UpdateFollowCam()，然后
            // scrCamera.Update() 把聚焦行星的位置写进
            // 相机的本地变换。为时间轴拖动而暂停驻留场景时，
            // 这两条路径都被压制，所以要重现
            // 稳定后的原生跟随状态，而不碰 camParent。MoveCamera
            // 继续拥有 camParent，因此保留它的原生偏移。
            Vector2 focus = focusPlanet.transform.position;
            Vector2 resolved = focus + camera.offset + camera.holdOffset;
            camera.furthestPlanet = focusPlanet;
            camera.topos = focus;
            camera.frompos = resolved;
            camera.pos = resolved;
            camera.timer = Mathf.Max(camera.timer, camera.camspeed);
            Vector3 localPosition = camera.transform.localPosition;
            camera.transform.localPosition = new Vector3(
                resolved.x + camera.shake.x,
                resolved.y + camera.shake.y,
                localPosition.z);
        }

        private static void LogResidentNativeCameraState(
            scnEditor editor,
            int floorIndex,
            float targetTime)
        {
            if (floorIndex == lastNativeCameraDiagnosticFloor
                && !float.IsNaN(lastNativeCameraDiagnosticTime)
                && Mathf.Abs(targetTime - lastNativeCameraDiagnosticTime) < 0.5f)
            {
                return;
            }

            lastNativeCameraDiagnosticFloor = floorIndex;
            lastNativeCameraDiagnosticTime = targetTime;
            scrCamera camera = scrCamera.instance;
            if (camera == null)
            {
                Main.Logger.Log("[NativeScrubCamera] camera unavailable at target="
                    + FormatTimelineTime(targetTime) + ".");
                return;
            }

            Transform parent = editor == null || editor.customLevel == null
                ? camera.transform.parent
                : editor.customLevel.camParent;
            Tween moveX = CameraMoveXTweenField == null
                ? null
                : CameraMoveXTweenField.GetValue(null) as Tween;
            Tween moveY = CameraMoveYTweenField == null
                ? null
                : CameraMoveYTweenField.GetValue(null) as Tween;
            ffxCameraPlus latestEffect = null;
            int cameraEffectCount = 0;
            if (editor != null && editor.floors != null)
            {
                int maximum = Mathf.Clamp(floorIndex, 0, editor.floors.Count - 1);
                for (int i = 0; i <= maximum; i++)
                {
                    scrFloor floor = editor.floors[i];
                    if (floor == null || floor.plusEffects == null)
                    {
                        continue;
                    }
                    foreach (ffxPlusBase effect in floor.plusEffects)
                    {
                        ffxCameraPlus cameraEffect = effect as ffxCameraPlus;
                        if (cameraEffect == null)
                        {
                            continue;
                        }
                        cameraEffectCount++;
                        if (cameraEffect.startTime <= targetTime + 0.0001f
                            && (latestEffect == null
                                || cameraEffect.startTime >= latestEffect.startTime))
                        {
                            latestEffect = cameraEffect;
                        }
                    }
                }
            }

            string parentPosition = parent == null ? "null" : parent.position.ToString("F3");
            string latest = latestEffect == null
                ? "none"
                : latestEffect.movementType + "/" + latestEffect.finalPos.ToString("F3")
                    + "@" + latestEffect.startTime.ToString("0.000");
            Main.Logger.Log("[NativeScrubCamera] target=" + FormatTimelineTime(targetTime)
                + " floor=" + floorIndex
                + " follow=" + camera.followMode
                + " lastMode=" + camera.lastUsedMovementType
                + " parent=" + parentPosition
                + " local=" + camera.transform.localPosition.ToString("F3")
                + " world=" + camera.transform.position.ToString("F3")
                + " relative=" + camera.lastEventRelativePosition.ToString("F3")
                + " effects=" + cameraEffectCount
                + " latest=" + latest
                + " moveX=" + FormatTweenDiagnostic(moveX)
                + " moveY=" + FormatTweenDiagnostic(moveY) + ".");
        }

        private static string FormatTweenDiagnostic(Tween tween)
        {
            return tween == null
                ? "null"
                : (tween.active ? "active" : "inactive")
                    + "@" + tween.position.ToString("0.000");
        }

        private static void AdvanceResidentNativeScrubTweens(scrVfxPlus vfx, float targetTime)
        {
            if (vfx == null || vfx.pausedTweens == null
                || float.IsNaN(lastTimelineScrubTime)
                || Mathf.Approximately(targetTime, lastTimelineScrubTime))
            {
                return;
            }

            scrConductor conductor = scrConductor.instance;
            float pitch = conductor == null || conductor.song == null
                ? 1f
                : Mathf.Max(0.001f, conductor.song.pitch);
            float delta = (targetTime - lastTimelineScrubTime) / pitch;
            for (int i = vfx.pausedTweens.Count - 1; i >= 0; i--)
            {
                Tween tween = vfx.pausedTweens[i];
                if (tween == null || !tween.active)
                {
                    vfx.pausedTweens.RemoveAt(i);
                    continue;
                }

                tween.Goto(Mathf.Max(0f, tween.position + delta), false);
            }
        }

        private static void KillResidentNativeScrubTweens()
        {
            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx == null || vfx.pausedTweens == null)
            {
                return;
            }

            for (int i = vfx.pausedTweens.Count - 1; i >= 0; i--)
            {
                Tween tween = vfx.pausedTweens[i];
                if (tween != null && tween.active)
                {
                    tween.Kill(false);
                }
            }
            vfx.pausedTweens.Clear();
        }

        /// <summary>
        /// 临时：开始为一次交接记录视觉状态。在任何东西被
        /// 拆掉之前调用，所以恢复所改的一切都在记录里。
        /// </summary>
        private static void BeginHandoffWatch()
        {
            for (int i = 0; i < HandoffWatchCapacity; i++)
            {
                HandoffWatchRows[i] = null;
            }
            handoffWatchNext = 0;
            handoffWatchActive = true;
            handoffWatchFrames = 0;
            TickHandoffWatch();
        }

        /// <summary>
        /// 临时：交接在途期间每帧一行视觉状态。位置
        /// 才是关键：相机父节点和球就是停顿最终表现成的
        /// 跳跃，而求值器旗标说明当时到底有没有东西在画。
        /// </summary>
        /// <summary>
        /// 临时：球的渲染状态，这样能把「画面冻住」与
        /// 「时钟冻住」区分开。这件事不能用 CameraCmp——它记在
        /// nativePlaybackStarted 之后，而 Track C 从不设置那个旗标。
        /// </summary>
        private static string TraceBallState()
        {
            scrPlayerManager players = scrPlayerManager.instance;
            if (players == null || players.players == null || players.players.Length == 0)
            {
                return " ball=n/a";
            }
            scrPlayer player = players.players[0];
            if (player == null || player.planetarySystem == null)
            {
                return " ball=n/a";
            }
            scrPlanet planet = player.planetarySystem.chosenPlanet;
            if (planet == null)
            {
                return " ball=n/a";
            }
            scrCamera camera = scrCamera.instance;
            return " ball=" + planet.transform.position.x.ToString("0.000")
                + "," + planet.transform.position.y.ToString("0.000")
                + " angle=" + planet.angle.ToString("0.000")
                + " seq=" + (planet.currfloor == null ? -1 : planet.currfloor.seqID)
                + " lastHit=" + player.lastHit.ToString("0.000")
                + " cam=" + (camera == null
                    ? "n/a"
                    : camera.transform.position.x.ToString("0.000") + ","
                        + camera.transform.position.y.ToString("0.000"));
        }

        private static void TickHandoffWatch()
        {
            if (!handoffWatchActive)
            {
                return;
            }
            handoffWatchFrames++;
            if (handoffWatchFrames >= HandoffWatchCapacity)
            {
                return;
            }

            scrCamera camera = scrCamera.instance;
            scnGame level = scnGame.instance;
            scrPlayerManager playerManager = scrPlayerManager.instance;
            scrPlanet planet = null;
            if (playerManager != null && playerManager.players != null
                && playerManager.players.Length > 0 && playerManager.players[0] != null
                && playerManager.players[0].planetarySystem != null)
            {
                planet = playerManager.players[0].planetarySystem.chosenPlanet;
            }

            StringBuilder b = new StringBuilder();
            b.Append("eval=").Append(evaluatorOwnedActive)
                .Append(" active=").Append(evaluatorBackend.IsActive)
                .Append(" listening=").Append(!AudioListener.pause)
                .Append(" tScale=").Append(Time.timeScale.ToString("0.##"));
            if (camera != null)
            {
                b.Append(" cam=").Append(camera.transform.position.x.ToString("0.000"))
                    .Append(',').Append(camera.transform.position.y.ToString("0.000"))
                    .Append(" camOn=").Append(camera.enabled);
            }
            b.Append(" camParent=").Append(level == null || level.camParent == null
                ? "n/a"
                : level.camParent.position.x.ToString("0.000") + ","
                    + level.camParent.position.y.ToString("0.000"));
            b.Append(" ball=").Append(planet == null
                ? "n/a"
                : planet.transform.position.x.ToString("0.000") + ","
                    + planet.transform.position.y.ToString("0.000"))
                .Append(" ballOn=").Append(planet == null ? "?" : planet.enabled.ToString());
            HandoffWatchRows[handoffWatchNext] = "f" + Time.frameCount + " " + b;
            handoffWatchNext = (handoffWatchNext + 1) % HandoffWatchCapacity;
        }

        /// <summary>临时：停止交接记录并打印它。</summary>
        private static void EndHandoffWatch(string reason)
        {
            if (!handoffWatchActive)
            {
                return;
            }
            TickHandoffWatch();
            handoffWatchActive = false;
            Main.Logger.Log("[HandoffWatch] ==== " + reason + " ==== " + handoffWatchFrames + " frames, oldest first ====");
            for (int i = 0; i < HandoffWatchCapacity; i++)
            {
                string row = HandoffWatchRows[(handoffWatchNext + i) % HandoffWatchCapacity];
                if (row != null)
                {
                    Main.Logger.Log("[HandoffWatch] " + row);
                }
            }
            Main.Logger.Log("[HandoffWatch] ==== end ====");
        }

        /// <summary>
        /// Track B：在编辑器场景内部启动原生播放，而不离开它。
        ///
        /// 这件事不能用 scnEditor.Play。它的方法体调用 customLevel.Play，
        /// 那会走到一次资源重载，把场景从工作台底下换掉：日志
        /// 显示几帧之后重新初始化，也就是第二次场景加载，之后
        /// 编辑器还在屏幕上，播放头还在跑，而下一次按空格
        /// 失灵，因为它的宿主场景没了。所以改成直接套用 scnGame.Play 里
        /// 预览真正需要的那些片段。
        ///
        /// 其中两片是承重的，而且直到现在才补上：
        ///
        ///   chosenPlanet——scrGame.Play 逐格挑选它：
        ///       planetarySystem.chosenPlanet = (floor.seqID % 2 == 0) ? planetRed : planetBlue;
        ///   少了它，一对行星里真正在动的是错的那个，而因为两者相隔
        ///   半圈，球会渲染得差 180 度。
        ///
        ///   相机基线——scrGame.Play 清除 MoveCamera 所相对的状态：
        ///       camera.Rewind(); camParent.position = zero;
        ///       camera.lastEventRelativePosition = zero;
        ///       camera.lastUsedMovementType = CamMovementType.Player;
        ///   留着不更新的话，第一个 MoveCamera 特效会拿上一次运行的偏移
        ///   算出它的最终位置，把视野钉住。
        ///
        /// 序列的其余部分（事件重新烘焙、队列重新填充、拖动定位、conductor 启动）
        /// 是这个方法上一版已经做了、并且被验证能工作的部分
        /// ——播放、音乐、相机和特效都正确——所以按原样保留。
        /// </summary>
        private static bool StartEditorNativePlayback(float targetTime, out string status)
        {
            status = "正在启动原生播放";
            scnEditor editor = scnEditor.instance;
            scrController controller = scrController.instance;
            scrConductor conductor = scrConductor.instance;
            if (editor == null || controller == null || conductor == null
                || editor.floors == null || editor.floors.Count == 0)
            {
                status = "编辑器尚未就绪";
                return false;
            }

            try
            {
                // 在任何东西改变之前武装，所以记录覆盖相机基线
                // 和行星位置的重置，也覆盖窗口本身。
                BeginHandoffWatch();

                // 求值器在整个窗口期间继续绘制。
                //
                // StartMusic 的排程回调就是提交，而 StartMusicCo 会先等完 0.1s
                // 前奏才触发——大约十五帧。在这里就结束求值器，会让
                // 那些帧完全没有渲染器，而下面的重置又在这些帧里移动了球和
                // 相机，于是用户看到的是画面跳到错的
                // 姿态、停在那儿、然后在提交时又跳一下。这个坑可以绕开：
                // 只要预览器在整段窗口里持续刷新、不停下来，那几帧里
                // 屏幕上留着的就是最后一帧拖动画面（静止帧），而不是错帧。
                //
                // 让会话活着意味着最后一帧拖动画面原封不动留在屏幕上，
                // 直到 transport 起来，而交接在提交那一帧一次完成。
                // 停止被移到 seek 之前一步，在那里它仍然领先于
                // 所有会写同一批变换的东西。
                staticPreviewFrameActive = false;
                timelineScrubActive = false;
                // 行星和相机保持冻结，直到 seek 被提交，这正是
                // 这个窗口的全部意义。在这里就还原它们——那是在 StartMusic 之前，
                // 而它的回调才是提交——会让 scrPlanet.Update 在排程协程花掉的
                // 那 18 帧里跑起来（StartMusicCo 要等完 0.1s 前奏），而它
                // 会用没人设置过的状态重算角度：
                //
                //   angle = snappedLastAngle + (songposition_minusi - lastHit)/crotchet*pi*speed*dir
                //
                // snappedLastAngle 还是求值器的 -10.996，songposition_minusi
                // 还是上一次运行的 21.626，一帧之后掉到 -1.033，这让
                // 渲染角度变成 -17.757 rad——比谱面超前近三圈。窗口
                // 长度随帧率变化，所以每次运行错的画面都不一样。
                // 保持冻结意味着最后一帧拖动画面留在屏幕上，直到真实
                // 状态就位，此后原生循环只从已提交的值绘制。

                int landingFloor = FindFloorAtOrBefore(editor.floors, targetTime);
                if (!conductor.gameObject.activeSelf)
                {
                    conductor.gameObject.SetActive(true);
                }
                conductor.enabled = true;
                controller.enabled = true;
                if (controller.paused)
                {
                    controller.TogglePauseGame();
                }
                Time.timeScale = 1f;
                AudioListener.pause = true;

                GCS.checkpointNum = 0;
                controller.forceNoCountdown = true;
                conductor.fastTakeoff = true;
                // gameworld 是 scrPlanet.AutoShouldHitNow 在推进
                // 球之前检查的东西：
                //     if (!controller.gameworld || seqID >= listFloors.Count - 1) return false;
                // 它默认是 false。scnGame.Play 把它设为 true，Track A 就是这样拿到
                // 自动打击的；手写启动流程把它留成 false，所以从来没有登记过
                // 落地。球于是只靠自己的角度一直往前走，直到追上一个
                // 入口时间对得上的格——表现为画面从好几秒处开始
                // （seq=5 在 songpos 0.08，而那个格的入口时间是 3.360）。
                controller.gameworld = true;
                EnableAutoMode();

                // scnGame.Play 贡献的、预览需要的两片东西，是在
                // 提交时（HandleEditorMusicScheduled）套用的，不是在这里。
                //
                // 它们过去跑在窗口开头，而记录正是这样显示的：
                // ResetPlanetsForNativePlay 把这一对行星摆在 localPosition zero 和
                // Vector3.right，这个姿态对于从零开始的场景是对的，
                // 对于正在恢复的运行是错的。在这里套用，它把球
                // 从 (-4.569, 16.951) 瞬移到 (1.000, 0.000)，持续窗口的十五帧，
                // 然后 seek 又把它拖到 (-6.000, 16.500)——本该没有跳跃的地方
                // 出现两次可见跳跃。两件事都放到提交时做，就把整个变化
                // 压进求值器交棒的那一帧。
                conductor.Rewind();

                desiredPaused = true;
                nativePlaybackStarted = false;
                pauseTransitionPending = false;
                pauseReapplyRequested = false;
                heldPreviewTime = targetTime;
                nativeTransportHeld = false;
                previewRequested = true;
                restartPhase = PreviewRestartPhase.None;
                lastTimelineScrubFloor = -1;
                lastTimelineScrubEditorId = 0;
                lastTimelineScrubTime = float.NaN;
                EditorSceneInteraction.Reset();

                Main.Logger.Log("[TrackB] starting native transport in the editor scene; floor="
                    + landingFloor + " target=" + FormatTimelineTime(targetTime) + ".");

                // 发出 StartMusic 到它的排程回调运行之间的这个窗口，
                // transport 已经活着但什么都还没提交。它的长度
                // 就是 StartMusicCo 的 0.1s 前奏恰好要花多少帧，所以如果
                // 期间画面是从未提交状态画出来的，缺陷就会变。
                // shape每 run. These stamps measure the window.
                trackBStartFrame = Time.frameCount;
                trackBStartUnscaled = Time.unscaledTime;
                lastTrackBPressFrame = Time.frameCount;
                TrackBTrace.Arm();
                Main.Logger.Log("[TrackBWindow] issued f=" + Time.frameCount
                    + " t=" + Time.unscaledTime.ToString("0.0000")
                    + " controllerPaused=" + controller.paused
                    + " state=" + controller.currentState
                    + " timeScale=" + Time.timeScale.ToString("0.##")
                    + " listenerPause=" + AudioListener.pause
                    + " hasSongStarted=" + conductor.hasSongStarted
                    + " songpos=" + conductor.songposition_minusi.ToString("0.000") + ".");

                conductor.StartMusic(null, delegate
                {
                    Main.Logger.Log("[TrackBWindow] callback f=" + Time.frameCount
                        + " t=" + Time.unscaledTime.ToString("0.0000")
                        + " frames=" + (Time.frameCount - trackBStartFrame)
                        + " seconds=" + (Time.unscaledTime - trackBStartUnscaled).ToString("0.0000")
                        + ".");
                    HandleEditorMusicScheduled(landingFloor, targetTime);
                });
                status = "已从 " + FormatTimelineTime(targetTime) + " 开始原生播放";
                return true;
            }
            catch (Exception exception)
            {
                AbortEditorNativePlayback();
                ReportError("Failed to start native playback in the editor scene", exception);
                status = "启动原生播放失败，详情见 Mod 日志";
                return false;
            }
        }

        /// <summary>
        /// 临时：Track B 启动窗口开启期间每帧一行，这样用户
        /// 在这期间看到的画面可以从球、相机和时钟读出来，
        /// 而不是靠猜。窗口就是从发出 StartMusic 到它的
        /// 排程回调之间的那些帧。
        /// </summary>
        private static void LogTrackBWindowFrame()
        {
            LogTrackBWindowFrame(string.Empty);
        }

        private static void LogTrackBWindowFrame(string label)
        {
            if (trackBStartFrame < 0 && trackBPostCommitFrames <= 0)
            {
                return;
            }
            if (trackBPostCommitFrames > 0)
            {
                trackBPostCommitFrames--;
            }

            scrController controller = scrController.instance;
            scrConductor conductor = scrConductor.instance;
            scrPlayerManager playerManager = scrPlayerManager.instance;
            scrPlanet planet = null;
            scrPlayer player = null;
            if (playerManager != null && playerManager.players != null && playerManager.players.Length > 0)
            {
                player = playerManager.players[0];
                if (player != null && player.planetarySystem != null)
                {
                    planet = player.planetarySystem.chosenPlanet;
                }
            }

            StringBuilder builder = new StringBuilder("[TrackBWindow] ")
                .Append(label).Append(" f=");
            TrackBTrace.Add("state=" + (controller == null ? "?" : controller.currentState.ToString())
                + " paused=" + (controller == null ? "?" : controller.paused.ToString())
                + " gw=" + (controller == null ? "?" : controller.gameworld.ToString())
                + " songStarted=" + (conductor == null ? "?" : conductor.hasSongStarted.ToString())
                + " songpos=" + (conductor == null ? "?" : conductor.songposition_minusi.ToString("0.000"))
                + " scene=" + (conductor == null ? "?" : conductor.songposition_minusi.ToString("0.000"))
                + " tScale=" + Time.timeScale.ToString("0.##")
                + " listening=" + !AudioListener.pause);
            builder.Append(Time.frameCount)
                .Append(" d=").Append(Time.frameCount - trackBStartFrame)
                .Append(" state=").Append(controller == null ? "?" : controller.currentState.ToString())
                .Append(" paused=").Append(controller == null ? "?" : controller.paused.ToString())
                .Append(" songStarted=").Append(conductor == null ? "?" : conductor.hasSongStarted.ToString())
                .Append(" songpos=").Append(conductor == null
                    ? "?" : conductor.songposition_minusi.ToString("0.000"));
            if (planet != null)
            {
                builder.Append(" seq=").Append(planet.currfloor == null ? -1 : planet.currfloor.seqID)
                    .Append(" angle=").Append(planet.angle.ToString("0.000"))
                    .Append(" snap=").Append(planet.snappedLastAngle.ToString("0.000"))
                    .Append(" lastHit=").Append(player.lastHit.ToString("0.000"))
                    .Append(" pos=").Append(planet.transform.position.x.ToString("0.00"))
                    .Append(',').Append(planet.transform.position.y.ToString("0.00"));
            }
            Main.Logger.Log(builder.ToString());
        }

        /// <summary>
        /// 拖动定位之后把行星放回落地格自己的角度。
        ///
        /// scrPlanet.ScrubToFloorNumber 会根据它落在的格设置 snappedLastAngle，但
        /// 它的最后一句是 HandlePause，而 HandlePause 会从
        /// *下一个* 格重算两个角度：
        ///
        ///     double num4 = floor.nextfloor.entryangle - PI * num;
        ///     p.SetTargetExitAngle(num4);
        ///     p.SetSnappedLastAngle(num5);        // num5 由 num4 推出
        ///
        /// 这对「暂停 *在* 一块砖上」是正确的基准，游戏用它就是这个目的。
        /// 对一次普通的恢复，它让球瞄准错的地方：在测试谱面上它
        /// 把 snappedLastAngle 设成 17.279（5.5pi），而格的 entryangle 是 4.712（1.5pi），
        /// 差了四圈，这让渲染角度远远超过 targetExitAngle。AutoShouldHitNow
        /// 于是在第一个 PlayerControl 帧就读到 true，自动循环把整份
        /// 逐帧预算全花在推进格上——球从第 0 格跳到第 5 格，
        /// 而时钟还接近零。
        ///
        /// 所以把 ScrubToFloorNumber 本来就算对的那两个值写回去：
        ///     snappedLastAngle = floor.entryangle + inverseAnglePerBeat
        ///     targetExitAngle  = snappedLastAngle + floor.angleLength * dir
        /// 而 angle 留给 scrPlanet.Update 的下一趟，它会从时钟推导它。
        /// </summary>
        private static void NormalizePlanetAfterScrub(scnEditor editor, int floorIndex)
        {
            if (editor == null || editor.floors == null
                || floorIndex < 0 || floorIndex >= editor.floors.Count)
            {
                return;
            }

            scrFloor floor = editor.floors[floorIndex];
            if (floor == null)
            {
                return;
            }

            scrConductor conductor = scrConductor.instance;
            double nextEntryTime = floorIndex + 1 < editor.floors.Count
                && editor.floors[floorIndex + 1] != null
                ? editor.floors[floorIndex + 1].entryTime
                : floor.entryTime;

            LogFloorAngleFacts(editor, floorIndex);

            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            // 第 0 格是谱面的起点，游戏对它有自己的设置，其中算进了
            // 倒计时：
            //     snappedLastAngle = PI * (0.5 - adjustedCountdownTicks);     // 4 ticks 时是 -3.5*PI
            //     targetExitAngle  = snappedLastAngle + angleLength;           // -3.5*PI + 4*PI
            // 所以球正好转过整个倒计时（4 ticks 时是 4 圈），然后
            // 在 1.92s 到达第一格的出口。下面的规范化会把这些
            // 换成格的普通开口角，那等于删掉倒计时的预算：
            // 球于是只剩 PI 的旋转量可走，整整提前一个倒计时落地——
            // 也就是「球从一开始就抢跑」这个症状。FirstFloorAngleSetup 是
            // 游戏自己的答案，所以用它，而不是手搓一个等价物。
            if (floorIndex == 0)
            {
                for (int i = 0; i < playerManager.players.Length; i++)
                {
                    scrPlayer startPlayer = playerManager.players[i];
                    if (startPlayer == null || startPlayer.planetarySystem == null)
                    {
                        continue;
                    }
                    // 球要落到下一格必须转过的量，由格的 *时长* 决定，
                    // 而不是由它的几何决定：
                    //     每 crotchet 角度推进 PI * speed   (scrPlanet.Update_RefreshAngles)
                    //     这个格持续 (nextEntry - entryTime) 秒
                    // 所以目标是 snap + PI * speed * (duration / crotchet)。测试谱面的
                    // 第 0 格在 0.480s 的 crotchet 下持续 1.920s——四拍——所以它需要
                    // 4*PI，这样球正好在第 1 格的入口时间落在它上面。
                    //
                    // FirstFloorAngleSetup 装入 snap = PI*(0.5 - adjustedCountdownTicks)，
                    // 然后 targetExit = snap + angleLength，只有当一个格的 angleLength
                    // 恰好等于它的旋转量时两者才相同。对倒计时格来说
                    // angleLength 是 PI，而旋转量是 4*PI，所以 targetExit 少了三
                    // 拍：球在 0.48s 而不是 1.92s 落地，整整抢跑了一个
                    // 倒计时。保留 snap、从时长推导 targetExit，
                    // 同时修好了半圈偏移和抢跑，因为正是 snap
                    // 让球在原地转完倒计时、回到 0.5*PI。
                    double beats = conductor == null || conductor.crotchetAtStart <= 0.000001d
                        ? 0d
                        : (nextEntryTime - floor.entryTime) / conductor.crotchetAtStart;
                    foreach (scrPlanet startPlanet in startPlayer.planetarySystem.planetList)
                    {
                        if (startPlanet == null)
                        {
                            continue;
                        }
                        startPlanet.FirstFloorAngleSetup();
                        int spinDir = startPlayer.planetarySystem.isCW ? 1 : -1;
                        startPlanet.SetTargetExitAngle(startPlanet.snappedLastAngle
                            + Math.PI * floor.speed * spinDir * beats);
                        // 否则渲染角度就是 seek 留下的值（测试谱面上是 23.739，
                        // 而 snap 是 -10.996）。在 seek 那一刻
                        // Update_RefreshAngles 会算
                        //     angle = snappedLastAngle + (songposition_minusi - lastHit)/crotchet*...
                        // 而这两者都是格的入口时间，所以 angle 等于 snapped。
                        startPlanet.angle = startPlanet.snappedLastAngle;
                        // FirstFloorAngleSetup 还会点亮行星的光环：
                        //     if (currfloor != null && !dummyPlanets)
                        //         planetRenderer.ringComp.Switch(chosen: true);
                        // 所以对一对里的两个行星都调用它，会让两个光环都亮。只有
                        // 被选中的行星才该显示光环，而显式把另一个关掉
                        // 才能恢复这一点。
                        if (startPlanet.planetRenderer != null
                            && startPlanet.planetRenderer.ringComp != null
                            && startPlanet != startPlayer.planetarySystem.chosenPlanet)
                        {
                            startPlanet.planetRenderer.ringComp.Switch(chosen: false, instant: true);
                        }
                    }
                    startPlayer.lastHit = floor.entryTime;
                }
                return;
            }

            // Pause 砖是例外。scrPlanet.ScrubToFloorNumber 最后会调用
            // HandlePause，而对带 extraBeats 的砖，那个辅助函数的全部目的就是
            // 装入暂停所需的角度——它的守卫字面上就是
            //     if (!floor.freeroam && !(floor.extraBeats <= 0f))
            // 而它设置的值算进了 extraBeats：
            //     num5 = nextfloor.entryangle - PI*num - PI*(extraBeats + 1)*num - angleDiff + num3
            // 用格的开口角覆盖它，正是让球在 Pause 砖之前
            // 乱了节奏的原因，读起来像它「等了一下又追上来」
            // （测试谱面的第 87 格带 Pause）。所以 Pause 砖保留 HandlePause
            // 装入的值，只对普通情况做规范化。
            if (floor.extraBeats > 0f && !floor.freeroam)
            {
                for (int i = 0; i < playerManager.players.Length; i++)
                {
                    scrPlayer p = playerManager.players[i];
                    if (p != null)
                    {
                        p.lastHit = floor.entryTime;
                    }
                }
                return;
            }

            double inverse = scrMisc.GetInverseAnglePerBeatMultiplanet(floor.numPlanets);
            int dir = floor.isCCW ? -1 : 1;
            double snapped = floor.entryangle + inverse * dir;
            double targetExit = snapped + floor.angleLength * dir;

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                foreach (scrPlanet planet in player.planetarySystem.planetList)
                {
                    if (planet == null)
                    {
                        continue;
                    }
                    planet.SetSnappedLastAngle(snapped);
                    planet.SetTargetExitAngle(targetExit);
                    // angle 也必须一起带上。HandlePause 让它与它装入的
                    // 错误 snappedLastAngle 保持一致，而 scrPlayer.CheckPostHoldFail
                    // 每个 PlayerControl 帧都拿它和 targetExitAngle 比较：
                    //     double num2 = chosenPlanet.angle - chosenPlanet.targetExitAngle;
                    //     if (controller.gameworld && ... && num2 > PI) Die();
                    // 两个值都修正了但 angle 还是旧的时，差约 15.9 rad，所以
                    // 这次运行在提交后大约四帧就死了。在 seek 那一刻
                    // 球坐在格的开口角上——Update_RefreshAngles 会算
                    // snappedLastAngle + (songposition_minusi - lastHit)/crotchet*...，而这两者
                    // 都是格的入口时间——所以这里 angle 等于 snapped。
                    planet.angle = snapped;
                }
                // 球应该坐在它被拖动定位到的格上，所以该格内的
                // 时钟已过时间在 seek 那一刻是零。
                player.lastHit = floor.entryTime;
            }
        }

        /// <summary>
        /// 临时：原生特效队列的头部，连同每条目的开始时间和
        /// 已触发旗标，摆在场景时钟旁边。「相机晚了好几秒才开始」要么是
        /// 某个特效的 startTime 错了，要么是某个特效在本不该触发时已标记为触发，
        /// 要么是队列在它本该存在的那个窗口之后才被重建。
        /// 只有这份 dump 能把它们区分开。
        /// </summary>
        private static void LogTrackBEffects(string phase)
        {
            // 诊断门控。这条日志走 UMM 的 Logger，而它内部第一句是
            // Console.WriteLine——同步写控制台。实测一条这样的日志单独
            // 占掉过一整帧 176ms。这一族都是排查特定问题时加的，
            // 不该在正常播放路径上跑。
            if (!VerboseDiagnostics)
            {
                return;
            }
            scrVfxPlus vfx = scrVfxPlus.instance;
            scrConductor conductor = scrConductor.instance;
            if (vfx == null || vfx.effects == null)
            {
                return;
            }

            int shown = 0;
            for (int i = 0; i < vfx.effects.Count && shown < 6; i++)
            {
                ffxPlusBase effect = vfx.effects[i];
                if (effect == null)
                {
                    continue;
                }
                Main.Logger.Log("[TrackBEffect] " + phase + " idx=" + i
                    + " type=" + effect.GetType().Name
                    + " floor=" + (effect.floor == null ? -1 : effect.floor.seqID)
                    + " start=" + effect.startTime.ToString("0.000")
                    + " offset=" + effect.startEffectOffset.ToString("0.000")
                    + " triggered=" + effect.triggered
                    + " scene=" + (conductor == null
                        ? "?" : conductor.songposition_minusi.ToString("0.000"))
                    + ".");
                shown++;
            }
            Main.Logger.Log("[TrackBEffect] " + phase + " queueCount="
                + vfx.effects.Count + " cursor=" + (VfxCurrentIndexField == null
                    ? -1 : Convert.ToInt32(VfxCurrentIndexField.GetValue(vfx))) + ".");
        }

        /// <summary>
        /// 临时：相机权威的摆位，与球的并排，节奏和
        /// 音频探针相同。跟随相机与球有偏移就是
        /// 「相机有偏移」的样子；camParent 加上相机的本地位置，
        /// 就是 MoveCamera 特效和跟随层各自贡献的地方。
        /// </summary>
        private static void LogTrackBCamera(string phase)
        {
            // 诊断门控。这条日志走 UMM 的 Logger，而它内部第一句是
            // Console.WriteLine——同步写控制台。实测一条这样的日志单独
            // 占掉过一整帧 176ms。这一族都是排查特定问题时加的，
            // 不该在正常播放路径上跑。
            if (!VerboseDiagnostics)
            {
                return;
            }
            scrCamera camera = scrCamera.instance;
            scnGame level = scnGame.instance;
            if (camera == null)
            {
                return;
            }
            scrPlayerManager playerManager = scrPlayerManager.instance;
            scrPlanet planet = null;
            if (playerManager != null && playerManager.players != null && playerManager.players.Length > 0
                && playerManager.players[0] != null && playerManager.players[0].planetarySystem != null)
            {
                planet = playerManager.players[0].planetarySystem.chosenPlanet;
            }
            Vector3 camPos = camera.transform.position;
            scrConductor camConductor = scrConductor.instance;
            Main.Logger.Log("[TrackBCam] " + phase
                + " scene=" + (camConductor == null
                    ? "?" : camConductor.songposition_minusi.ToString("0.000"))
                + " cam=" + camPos.x.ToString("0.000") + "," + camPos.y.ToString("0.000")
                + " camLocal=" + camera.transform.localPosition.x.ToString("0.000")
                + "," + camera.transform.localPosition.y.ToString("0.000")
                + " camParent=" + (level == null || level.camParent == null
                    ? "n/a"
                    : level.camParent.position.x.ToString("0.000") + ","
                        + level.camParent.position.y.ToString("0.000"))
                + " zoom=" + camera.zoomSize.ToString("0.000")
                + " follow=" + camera.followMode
                + " lastMode=" + camera.lastUsedMovementType
                + " lastRel=" + camera.lastEventRelativePosition.x.ToString("0.000")
                + "," + camera.lastEventRelativePosition.y.ToString("0.000")
                + " holdOffset=" + camera.holdOffset.x.ToString("0.000")
                + "," + camera.holdOffset.y.ToString("0.000")
                + (planet == null
                    ? string.Empty
                    : " ball=" + planet.transform.position.x.ToString("0.000")
                        + "," + planet.transform.position.y.ToString("0.000")
                        + " seq=" + (planet.currfloor == null ? -1 : planet.currfloor.seqID))
                + ".");
        }

        /// <summary>
        /// 临时：音乐偏移背后的排程数字。expectedClip 是
        /// 场景时钟所暗示片段位置应该是多少；song.time 是它实际在哪；
        /// clipTime/delay 是 SetExactConductorTime 刚算出并请求的值。
        /// </summary>
        private static void LogTrackBAudio(string phase, scrConductor conductor, float targetTime)
        {
            // 诊断门控。这条日志走 UMM 的 Logger，而它内部第一句是
            // Console.WriteLine——同步写控制台。实测一条这样的日志单独
            // 占掉过一整帧 176ms。这一族都是排查特定问题时加的，
            // 不该在正常播放路径上跑。
            if (!VerboseDiagnostics)
            {
                return;
            }
            if (conductor == null || conductor.song == null)
            {
                return;
            }
            double pitch = Math.Max(0.001d, conductor.song.pitch);
            double countdown = GetSeparateCountdownTime(conductor);
            double scene = conductor.songposition_minusi;
            double expectedClip = scene + conductor.addoffset - countdown;
            double clipTime = targetTime + conductor.addoffset - countdown
                + scrConductor.calibration_i;
            Main.Logger.Log("[TrackBAudio] " + phase
                + " target=" + targetTime.ToString("0.000")
                + " scene=" + scene.ToString("0.000")
                + " songTime=" + conductor.song.time.ToString("0.000")
                + " expectedClip=" + expectedClip.ToString("0.000")
                + " wantClip=" + clipTime.ToString("0.000")
                + " addoffset=" + conductor.addoffset.ToString("0.000")
                + " countdown=" + countdown.ToString("0.000")
                + " calib=" + scrConductor.calibration_i.ToString("0.000")
                + " pitch=" + pitch.ToString("0.000")
                + " dspTimeSong=" + conductor.dspTimeSong.ToString("0.000")
                + " dsp=" + AudioSettings.dspTime.ToString("0.000")
                + " playing=" + conductor.song.isPlaying
                + " scheduled=" + conductor.hasSongStarted + ".");
        }

        /// <summary>
        /// 临时：PlayerControl 开始时自动打击闸门看到的东西。一个角度
        /// 被留在远超它所在格位置上的球会让 AutoShouldHitNow 为 true，自动
        /// 循环于是把整份逐帧预算全花在推进格上。
        /// </summary>
        private static void LogAutoDecision(scrController controller)
        {
            scrPlayerManager playerManager = scrPlayerManager.instance;
            scrPlanet planet = null;
            scrPlayer player = null;
            if (playerManager != null && playerManager.players != null && playerManager.players.Length > 0)
            {
                player = playerManager.players[0];
                if (player != null && player.planetarySystem != null)
                {
                    planet = player.planetarySystem.chosenPlanet;
                }
            }
            if (planet == null)
            {
                return;
            }
            Main.Logger.Log("[TrackBAuto] gameworld=" + (controller == null ? "?" : controller.gameworld.ToString())
                + " state=" + (controller == null ? "?" : controller.currentState.ToString())
                + " seq=" + (planet.currfloor == null ? -1 : planet.currfloor.seqID)
                + " angle=" + planet.angle.ToString("0.000")
                + " snap=" + planet.snappedLastAngle.ToString("0.000")
                + " targetExit=" + planet.targetExitAngle.ToString("0.000")
                + " lastHit=" + player.lastHit.ToString("0.000")
                + " shouldHit=" + planet.AutoShouldHitNow()
                + " useOldAuto=" + RDC.useOldAuto + ".");
        }

        /// <summary>
        /// 临时：计算落地球必须达到的角度所需的一切。球
        /// 每 crotchet 推进 pi*speed，所以能让它正好在下一格
        /// 入口时间落地的角度可以从这些字段推出来，而不是靠猜一个公式；
        /// 之前的尝试都失败了，因为倒计时占的旋转量
        /// 没有被算进去。
        /// </summary>
        private static void LogFloorAngleFacts(scnEditor editor, int floorIndex)
        {
            scrConductor conductor = scrConductor.instance;
            scrFloor floor = editor.floors[floorIndex];
            if (floor == null || conductor == null)
            {
                return;
            }
            scrFloor next = floorIndex + 1 < editor.floors.Count ? editor.floors[floorIndex + 1] : null;
            StringBuilder b = new StringBuilder("[TrackBAngleFacts] floor=" + floorIndex);
            b.Append(" entryangle=").Append(floor.entryangle.ToString("0.000"))
                .Append(" exitangle=").Append(floor.exitangle.ToString("0.000"))
                .Append(" angleLength=").Append(floor.angleLength.ToString("0.000"))
                .Append(" speed=").Append(floor.speed.ToString("0.000"))
                .Append(" isCCW=").Append(floor.isCCW)
                .Append(" numPlanets=").Append(floor.numPlanets)
                .Append(" extraBeats=").Append(floor.extraBeats.ToString("0.000"))
                .Append(" countdownTicks=").Append(floor.countdownTicks)
                .Append(" entryTime=").Append(floor.entryTime.ToString("0.000"))
                .Append(" | nextEntry=")
                .Append(next == null ? "n/a" : next.entryTime.ToString("0.000"))
                .Append(" | crotchetAtStart=").Append(conductor.crotchetAtStart.ToString("0.000"))
                .Append(" adjustedCountdownTicks=").Append(conductor.adjustedCountdownTicks.ToString("0.000"))
                .Append(" separateCountdown=").Append(conductor.separateCountdownTime)
                .Append(" fastTakeoff=").Append(conductor.fastTakeoff)
                .Append(" d_stationary=").Append(GCS.d_stationary)
                .Append(" | forceNoCountdown=")
                .Append(scrController.instance == null ? "?" : scrController.instance.forceNoCountdown.ToString())
                .Append(".");
            Main.Logger.Log(b.ToString());
        }

        /// <summary>
        /// 按 scnGame.Play 的方式设置正在动的行星，这样行进的那个球就是
        /// 编辑器显示的那个。一对里两个行星相隔半圈，所以挑
        /// 错了会让球渲染得差 180 度——读起来正好就是「球
        /// 差半圈」，而不是「少了一个特效」。
        /// </summary>
        private static void ResetPlanetsForNativePlay(scnEditor editor, int floorIndex)
        {
            if (editor == null || editor.floors == null
                || floorIndex < 0 || floorIndex >= editor.floors.Count)
            {
                return;
            }

            scrFloor landing = editor.floors[floorIndex];
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                PlanetarySystem system = player.planetarySystem;
                if (system.planetRed == null || system.planetBlue == null)
                {
                    continue;
                }

                foreach (scrPlanet planet in system.planetList)
                {
                    if (planet != null)
                    {
                        planet.transform.localPosition = Vector3.zero;
                    }
                }
                system.planetBlue.transform.localPosition = Vector3.right;
                system.isCW = !landing.isCCW;
                system.speed = landing.speed;
                system.chosenPlanet = landing.seqID % 2 == 0 ? system.planetRed : system.planetBlue;
            }
        }

        /// <summary>
        /// 清除 MoveCamera 特效所相对的相机状态。scnGame.Play 会在
        /// 一次运行开头做同样的事，但这里只重置 MoveCamera 真正读取的字段：
        /// camera.Rewind() 是完整的一次运行拆除，还会把 tosize 归零并重启
        /// 缩放，这在场景开头是对的，在一个活着的场景内部是错的，那里
        /// 读起来就是视图在跳。
        /// </summary>
        private static void ResetCameraBaselineForNativePlay()
        {
            scrCamera camera = scrCamera.instance;
            if (camera == null)
            {
                return;
            }

            camera.SetHoldOffset(Vector3.zero);
            camera.lastEventRelativePosition = Vector2.zero;
            camera.lastUsedMovementType = CamMovementType.Player;

            scnGame level = scnGame.instance;
            if (level != null && level.camParent != null)
            {
                level.camParent.position = Vector3.zero;
            }
        }

        /// <summary>
        /// 在 conductor 排好歌曲之后运行：用游戏自己的拖动定位把运行时
        /// seek 到播放头，然后把控制权交给 player 状态。
        /// </summary>
        private static void HandleEditorMusicScheduled(int landingFloor, float targetTime)
        {
            scrController controller = scrController.instance;
            scrConductor conductor = scrConductor.instance;
            scnEditor editor = scnEditor.instance;
            if (controller == null || conductor == null || editor == null)
            {
                return;
            }

            try
            {
                // 编辑器场景没有事件组件可跑，而这不是谱面的
                // bug——这是编辑器自身的保持方式。ApplyEventsToFloors 会销毁每个格上的
                // 每个 ffxPlusBase 并清空 plusEffects，而且是在它的暂停检查 *之前*：
                //
                //     foreach (floor) { DestroyImmediate(GetComponents<ffxPlusBase>()); plusEffects.Clear(); }
                //     ...
                //     if (scrController.instance.paused) return;
                //
                // 而编辑器里 scrController.paused 是 true，所以 return 落地时
                // 组件已经没了、什么都没有重建。这就是为什么 PrepVfx 没有东西
                // 可以填队列，也没有任何事件——MoveCamera、滤镜、什么都一样——生效。
                // RebakeFloors 在烘焙期间写入被清空的值，这正是
                // FinishCustomLevelLoading 在一次真实播放中所依赖的，它之后
                // 会恢复编辑器的值，所以编辑器没有变化。
                RebakeFloors(editor, false);
                if (editor.customLevel != null)
                {
                    editor.customLevel.PrepVfx(landingFloor, false);
                }

                // 求值器在这里把画面交出去，就在 seek 前一句，所以
                // StartMusic 到这个回调之间的窗口完全由它绘制。更早就结束它
                // 正是记录里那次两段式跳跃交接的来源。
                evaluatorBackend.End();
                evaluatorOwnedActive = false;
                previewClockRunning = false;
                StopEvaluatorPreviewAudio();

                // scnGame.Play 在一次运行开头做的那些重置，改在提交时套用，
                // 而不是在窗口开头，这样变化落在这一帧里，
                // 而不是整个窗口都看得见。ResetPlanets 必须排在 seek 之前：Scrub
                // 会重定位行星，而只有 chosenPlanet 已经从落地格
                // 设置好之后，它才能从一对里挑对的那个。
                ResetPlanetsForNativePlay(editor, landingFloor);
                ResetCameraBaselineForNativePlay();

                // controller.Scrub 就是游戏自己的「把运行时放到这里」：它重定位每个
                // 行星系统，通过 ScrubToTime 回卷特效队列并移动
                // 音乐，而不重建任何东西。它落在格入口上，并按倒计时 ticks
                // 把音乐往回退，所以精确的格内时间紧接着
                // 被提交。
                LogTrackBWindowFrame("pre-scrub");
                controller.Scrub(landingFloor, true);
                NormalizePlanetAfterScrub(editor, landingFloor);
                LogTrackBWindowFrame("post-scrub");
                AudioListener.pause = true;
                // startAudio 传 true，并在这次调用期间保持 nativeRestartActive。
                //
                // false 看起来更安全，因为 true 会走到 CancelPendingConductorMusicStart，
                // 而这次运行要播的歌曲正是那个协程排程的。但 false 会跳过
                // 重新排程，而重新排程正是承载倒计时的东西：
                //
                //     clipTime = targetTime + addoffset - countdownTime + calib;   // t=0 时是 -1.487
                //     if (clipTime < 0) delay = -clipTime / pitch;                 //  1.487
                //     if (startAudio) song.SetScheduledStartTime(dsp + delay);
                //     song.time = Max(0, clipTime);                                //  0
                //
                // 没有它，片段立刻从零开始，这会让音乐整整
                // 超前谱面一个倒计时——实测为 scene=6.705 而 songTime=6.699，
                // 而 expectedClip 是 4.970。nativeRestartActive 正是为这种情形
                // 早就存在的守卫：它让 SetExactConductorTime 不去碰那个
                // 待启动协程，Track A 在它自己的提交前后就是这么做的。
                nativeRestartActive = true;
                SetExactConductorTime(conductor, targetTime, true);
                nativeRestartActive = false;
                LogTrackBWindowFrame("post-clock");
                LogAutoDecision(controller);
                LogTrackBAudio("post-clock", conductor, targetTime);

                // Scrub 刚刚为落地格装入了真实的 snappedLastAngle，
                // SetExactConductorTime 装入了真实的时钟，所以现在可以把画面
                // 交给原生循环，而它们不会读到任何未设置的值。这是
                // 在整个启动窗口保持冻结的另一半。
                RestoreStaticPreviewNativeUpdates();

                // PlayerControl 是驱动逐帧落地循环的状态
                // （scrController: "if (state == PlayerControl && dest == PlayerControl)"），所以
                // 谱面只在它被保持期间推进。
                if (controller.currentState != States.PlayerControl)
                {
                    controller.ChangeState(States.PlayerControl);
                }

                desiredPaused = false;
                nativeTransportHeld = false;
                heldPreviewTime = float.NaN;
                nativePlaybackStarted = true;
                trackBTransportActive = true;
                editor.pausedInPlayMode = false;
                Time.timeScale = 1f;
                AudioListener.pause = false;
                conductor.enabled = true;

                // StartMusicCo 在 checkpointNum 为 0 时自己就会调 PlayHitTimes，就在
                // 它调用我们的排程回调之前，所以打击音表已经建好，
                // 这里不需要谁去要它。（这条路径的早期版本加过一次调用，
                // 那是多余的——音从来不是点击跟不上拍的原因。）
                // 再调一次只是为了让 requestEvaluatorRebuild 式的重入
                // 无法把表留成过期的。
                conductor.PlayHitTimes();

                Main.Logger.Log("[TrackB] transport live; floor=" + landingFloor
                    + " songpos=" + conductor.songposition_minusi.ToString("0.000") + ".");
                EndHandoffWatch("committed");
                // 提交之后继续采样：反馈是「球跑到更后面的格上了」，
                // and the committed state itself reads seq=0 at the origin, so whatever moves it
                // 而这件事发生在原生循环接管之后。
                LogTrackBWindowFrame();
                LogTrackBAudio("committed", conductor, targetTime);
                LogTrackBCamera("committed");
                LogTrackBEffects("committed");
                trackBStartFrame = -1;
                trackBPostCommitFrames = 40;
            }
            catch (Exception exception)
            {
                AbortEditorNativePlayback();
                ReportError("Failed to seek the editor-scene native runtime", exception);
            }
        }

        private static void AbortEditorNativePlayback()
        {
            desiredPaused = true;
            nativePlaybackStarted = false;
            trackBTransportActive = false;
            AudioListener.pause = true;
            Time.timeScale = 1f;
            scnEditor editor = scnEditor.instance;
            scrController controller = scrController.instance;
            if (editor != null && controller != null)
            {
                ApplyPausedState(editor, controller, scrConductor.instance);
            }
        }

        /// <summary>
        /// Track A/B 的原生重建入口:拆掉求值器会话,让游戏自己的 transport 重新把画面建起来。
        ///
        /// 【现状:活路径,勿删】
        /// 这里的 restartPhase 状态机由 TickPreview → TickPreviewRestart 推进,当前仍被
        /// 「回到开头」(TryReturnToStart)和拖动定位(nativeScrubPrepared 分支)使用。
        /// 注意它内部没有 Track C 的早退分支,调用即进入完整的原生重建,所以这两条路径
        /// 到现在都还会闪一下。等它们改用求值器重绘之后,整个状态机才可以删除。
        /// </summary>
        private static bool TryBeginPreviewRestart(float targetTime, bool commitExactTime, out string status,
            bool resumeAfterReady = false)
        {
            status = "正在回到开始位置";
            if (restartPhase != PreviewRestartPhase.None)
            {
                return true;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                status = "编辑器尚未就绪";
                return false;
            }

            try
            {
                evaluatorBackend.End();
                // 求值器必须把画面交出去，否则这里搭起来的 transport
                // 永远到不了：TickPreview 先测 evaluatorOwnedActive，所以它
                // 置位期间下面那条原生分支跑不了，交接会悄无声息地
                // 什么都不做。它的音频也停了，因为 scnGame.Play 会启动自己的音频，两个
                // 音源会互相盖着播。
                evaluatorOwnedActive = false;
                previewClockRunning = false;
                StopEvaluatorPreviewAudio();
                HandoffProbe.MarkTeardown();
                // 重启结束之前，歌曲归游戏自己的 StartMusicCo 所有，
                // 不能被这次重启执行的 seek 取消。
                nativeRestartActive = true;
                staticPreviewFrameActive = false;
                RestoreStaticPreviewNativeUpdates();
                previewRequested = true;
                desiredPaused = true;
                restartTargetTime = Mathf.Max(0f, targetTime);
                restartCommitExactTime = commitExactTime;
                restartResumeAfterReady = resumeAfterReady;
                heldPreviewTime = restartTargetTime;
                nativeTransportHeld = !commitExactTime;
                nativePlaybackStarted = false;
                pauseTransitionPending = false;
                pauseReapplyRequested = false;
                lastTimelineScrubFloor = -1;
                lastTimelineScrubEditorId = 0;
                lastTimelineScrubTime = float.NaN;
                timelineScrubActive = false;
                nativeScrubPrepared = false;
                nativeScrubBoundaries.Clear();
                restartPlayIssued = false;
                restartAbortStableFrames = 0;
                EditorSceneInteraction.Reset();

                scrVfxPlus vfx = scrVfxPlus.instance;
                if (vfx != null && vfx.pausedTweens != null)
                {
                    vfx.pausedTweens.Clear();
                }

                scrConductor conductor = scrConductor.instance;
                if (conductor != null)
                {
                    // 先重新激活再 enable：SwitchToEditMode 会让这个 GameObject
                    // 处于非激活，而非激活 GameObject 上的东西什么都不跑——协程
                    // 连排队都排不了。editor.Play 也会走到这里，但一次从不经过 Play 的
                    // 恢复，否则会对着一个惰性的
                    // conductor 启动。见 ApplyPlayingState。
                    if (!conductor.gameObject.activeSelf)
                    {
                        conductor.gameObject.SetActive(true);
                    }
                    conductor.enabled = true;
                }
                scrController controller = scrController.instance;
                if (controller != null)
                {
                    controller.enabled = true;
                    if (controller.paused)
                    {
                        editor.pausedInPlayMode = false;
                        TryApplyNativePause(controller, false, false);
                    }
                }

                Time.timeScale = 1f;
                AudioListener.pause = true;
                restartPhase = PreviewRestartPhase.WaitingForEditMode;
                restartPhaseFrame = Time.frameCount;
                restartStartedAt = Time.unscaledTime;
                allowNativeExit = true;

                // 这里刻意不调 DOTween.KillAll。scrController.Start_Rewind 确实会
                // 跑一次，但它跑在原生运行时创建任何自己的相机
                // tween 之前。此时 MoveCamera 特效已经存在，它们的
                // tweens live (ffxCameraPlus.moveXTween/moveYTween/rotationTween/zoomTween),
                // 而杀掉那些会让 camParent 钉在原点——CameraCmp 显示
                // 整个运行期间 camParent=(0,0)，只有跟随球的层在动，
                // 读起来就是「相机只是跟着走，完全没有取景」。
                editor.SwitchToEditMode(false);
                return true;
            }
            catch (Exception exception)
            {
                AbortPreviewRestart();
                ReportError("Failed to restart persistent preview from the beginning", exception);
                status = "回到开始失败，详情见 Mod 日志";
                return false;
            }
            finally
            {
                allowNativeExit = false;
                AudioListener.pause = restartPhase != PreviewRestartPhase.None;
                Cursor.visible = true;
            }
        }

        private static void TickPreviewRestart()
        {
            try
            {
                if (restartPhase == PreviewRestartPhase.Aborting)
                {
                    TickPreviewAbort();
                    return;
                }

                if (Time.unscaledTime - restartStartedAt > 8f)
                {
                    throw new TimeoutException("The native preview restart did not become ready in time.");
                }

                Cursor.visible = true;
                AudioListener.pause = true;
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                {
                    return;
                }

                if (restartPhase == PreviewRestartPhase.WaitingForEditMode)
                {
                    Time.timeScale = 1f;
                    if (Time.frameCount <= restartPhaseFrame || !editor.inStrictlyEditingMode
                        || editor.playMode
                        || editor.floors == null || editor.floors.Count == 0)
                    {
                        return;
                    }

                    int targetFloor = FindFloorAtOrBefore(editor.floors, restartTargetTime);
                    MarkNextFloorSelect(FloorSelectPreviewRestart);
                    editor.SelectFloor(editor.floors[targetFloor], cameraJump: false);
                    editor.DeselectAnyUIGameObject();
                    restartPhase = PreviewRestartPhase.WaitingToPlay;
                    restartPhaseFrame = Time.frameCount;
                    return;
                }

                if (restartPhase == PreviewRestartPhase.WaitingToPlay)
                {
                    if (Time.frameCount <= restartPhaseFrame
                        || !editor.inStrictlyEditingMode
                        || editor.playMode
                        || editor.floors == null
                        || editor.floors.Count == 0)
                    {
                        return;
                    }

                    EnableAutoMode();
                    int targetFloor = FindFloorAtOrBefore(editor.floors, restartTargetTime);
                    MarkNextFloorSelect(FloorSelectPreviewRestart);
                    editor.SelectFloor(editor.floors[targetFloor], cameraJump: false);
                    editor.DeselectAnyUIGameObject();
                    Time.timeScale = 1f;
                    restartPlayIssued = true;
                    editor.Play();

                    // editor.Play 把选中的格复制进 GCS.checkpointNum，而
                    // 非零值会让 OnMusicScheduled 走它的检查点分支：
                    //
                    //     if (GCS.checkpointNum != 0) { hasSongStarted = true;
                    //         Scrub(GCS.checkpointNum, ...); ChangeState(States.Checkpoint); }
                    //
                    // 那是游戏自己的「从这里恢复」，它用
                    // 自己的拖动定位 seek 运行时，并保持 States.Checkpoint——音量从零淡入
                    // ——直到场景时钟到达那个格的入口时间。它与
                    // 这个重启紧接着执行的精确提交对抗，
                    // 两者合起来产生了画面移动前几百毫秒的暂停，
                    // 以及一个不再和音频匹配的场景时钟。
                    //
                    // 清成零，这样起点被当作普通播放；提交
                    // 仍然把运行时放到被要求的确切时间。Play()
                    // 已经读过选中项，所以这不会改变关卡制作器
                    // 是围绕哪个格准备的。
                    GCS.checkpointNum = 0;

                    // 清掉它同时也解除了音量那条路径，所以必须在这里
                    // 还原。Play 在起点看起来像检查点时会静音歌曲：
                    //
                    //     if (GCS.checkpointNum != 0) conductor.song.volume = 0f;
                    //
                    // 而唯一会再次抬高它的，是 Checkpoint_Update 的
                    // 淡入，那只在检查点状态激活期间运行。分支解除武装后
                    // 没有任何东西恢复它，音乐会整场运行保持无声。
                    // 关卡自己的音量是 SetupConductorWithLevelData
                    // 装入的那个，所以就把它设回去。
                    scrConductor volumeConductor = scrConductor.instance;
                    scnGame volumeLevel = scnGame.instance;
                    if (volumeConductor != null && volumeConductor.song != null
                        && volumeLevel != null && volumeLevel.levelData != null)
                    {
                        volumeConductor.song.volume =
                            Mathf.Clamp01((float)volumeLevel.levelData.volume * 0.01f);
                    }

                    // Play 把这两个都推导为「RDC.auto && seqID == 0」，而这里的 seqID
                    // 就是被选中的格，所以从第一格以后任何地方开始
                    // 都会让它们为 false：倒计时会跑起来，视图会停在
                    // 它的黑屏上。精确时间的提交稍后会再把它们设好，
                    // 但 OnMusicScheduled 可能在那之前触发，所以现在也设上。
                    scrController playingController = scrController.instance;
                    if (playingController != null)
                    {
                        playingController.forceNoCountdown = true;
                    }
                    scrConductor playingConductor = scrConductor.instance;
                    if (playingConductor != null)
                    {
                        playingConductor.fastTakeoff = true;
                    }

                    AudioListener.pause = true;
                    nativePlaybackStarted = editor.playMode;
                    HandoffProbe.Record("PLAY-ISSUED editor.playMode=" + editor.playMode);
                    restartPhase = PreviewRestartPhase.WaitingForRuntime;
                    restartPhaseFrame = Time.frameCount;
                    return;
                }

                if (restartPhase != PreviewRestartPhase.WaitingForRuntime
                    || Time.frameCount <= restartPhaseFrame)
                {
                    return;
                }

                scrController controller = scrController.instance;
                scrConductor conductor = scrConductor.instance;
                if (!IsPreviewRuntimeReady(editor, controller, conductor))
                {
                    Time.timeScale = 1f;
                    AudioListener.pause = true;
                    return;
                }

                // 歌曲必须在暂停生效之前排好。StartMusicCo 用
                // out a 0.1s lead-in with `timer -= Time.deltaTime`, and deltaTime is zero
                // 而 timeScale 为零时 deltaTime 也是零——所以先暂停会让那个循环无法
                // 结束。歌曲于是从未被排程，OnMusicScheduled 从未运行，
                // 控制器永远停在 States.Start：球在走，
                // 相机在跟，但没有音乐，也没有任何砖块落地。
                if (!conductor.hasSongStarted && !conductor.song.isPlaying)
                {
                    Time.timeScale = 1f;
                    AudioListener.pause = true;
                    return;
                }

                if (!TryApplyNativePause(controller, true, false))
                {
                    editor.pausedInPlayMode = false;
                    controller.enabled = true;
                    conductor.enabled = true;
                    Time.timeScale = 1f;
                    AudioListener.pause = true;
                    return;
                }

                bool exactCommit = restartCommitExactTime;
                bool resumeAfterReady = restartResumeAfterReady;
                float nativePauseTime = ReadConductorTime();
                if (exactCommit)
                {
                    CommitRestartAtExactTime(editor, controller, conductor, restartTargetTime);
                    nativePauseTime = restartTargetTime;
                }
                nativePlaybackStarted = true;
                heldPreviewTime = nativePauseTime;
                desiredPaused = true;
                nativeTransportHeld = !exactCommit;
                editor.pausedInPlayMode = true;
                Time.timeScale = 0f;
                AudioListener.pause = true;
                conductor.enabled = !exactCommit;
                pauseTransitionPending = false;
                pauseReapplyRequested = false;
                restartPlayIssued = false;
                restartAbortStableFrames = 0;
                restartTargetTime = 0f;
                restartCommitExactTime = false;
                restartResumeAfterReady = false;
                restartPhase = PreviewRestartPhase.None;
                RestoreEditorClickability();
                if (Main.Settings == null || Main.Settings.LockPreviewZoom)
                {
                    ResetPreviewZoom();
                }
                if (resumeAfterReady)
                {
                    editor.pausedInPlayMode = false;
                    SetExactConductorTime(conductor, nativePauseTime, true);
                    ResumeScrubbedVfxTweens();
                    conductor.PlayHitTimes();
                    desiredPaused = false;
                    nativeTransportHeld = false;
                    heldPreviewTime = float.NaN;

                    // 必须告诉控制器去播放。scnController.OnMusicScheduled
                    // 是另一件唯一能把它推出 States.Start 的事，而它
                    // 从 StartMusicCo 的回调里运行——上面那些 seek 会取消那个回调，所以
                    // 它永远不会到。留在 Start 状态下，关卡看起来像在跑——
                    // 球在走、相机在跟——但歌曲在
                    // 时间零点无声，球穿过每一块砖都不落地，因为
                    // 驱动打击循环的是 States.PlayerControl。
                    //
                    // 先恢复播放，这样状态切换落在一个
                    // 没有暂停的运行时上。
                    ApplyPlayingState(editor, controller);
                    controller.forceNoCountdown = true;
                    conductor.fastTakeoff = true;
                    if (controller.currentState != States.PlayerControl)
                    {
                        controller.ChangeState(States.PlayerControl);
                    }

                    Main.Logger.Log("Switched from static preview to native playback at "
                        + FormatTimelineTime(nativePauseTime) + ".");
                    HandoffProbe.MarkAudioLanded(nativePauseTime);
                    // Track B 从不进入播放模式，所以 NativeRuntimeIsLive 无法把它的
                    // transport 与静止的编辑器区分开；桥显式跟踪它，
                    // 而这里就是 transport 变为活着的地方。
                    trackBTransportActive = true;
                }

                // 只在恢复结束后才清。它内部的 seek 会查这个
                // 旗标，所以在那次 seek 之前就清它，会取消那个正在
                // 启动歌曲的协程。
                nativeRestartActive = false;
                EditorStudioOverlay handoffOverlay = Main.Overlay;
                if (handoffOverlay != null)
                {
                    handoffOverlay.ReleasePreviewHandoffAfterNativeFrame();
                }
            }
            catch (Exception exception)
            {
                AbortPreviewRestart();
                ReportError("Failed while restarting persistent preview from the beginning", exception);
            }
        }

        private static void CommitRestartAtExactTime(
            scnEditor editor,
            scrController controller,
            scrConductor conductor,
            float targetTime)
        {
            targetTime = Mathf.Max(0f, targetTime);
            int floorIndex = FindFloorAtOrBefore(editor.floors, targetTime);

            AudioListener.pause = true;
            controller.Scrub(floorIndex, true);
            AudioListener.pause = true;
            if (AudioManager.Instance != null)
            {
                AudioManager.Instance.StopAllSounds();
            }

            double floorTime = editor.floors[floorIndex].entryTime;
            scrPlanet activePlanet = null;
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager != null && playerManager.players != null)
            {
                foreach (scrPlayer player in playerManager.players)
                {
                    if (player == null || player.planetarySystem == null)
                    {
                        continue;
                    }

                    // 向后 seek 之后 controller.Scrub() 并不能可靠地重建 player 运行时。
                    // 对齐实时拖动定位那条路径，在提交精确 conductor 时间之前
                    // 重建每个行星系统。
                    player.planetarySystem.ScrubToFloorNumber(floorIndex, null, true);
                    player.lastHit = floorTime;
                    scrPlanet chosenPlanet = player.planetarySystem.chosenPlanet;
                    if (chosenPlanet != null)
                    {
                        chosenPlanet.Update_RefreshAngles();
                        if (activePlanet == null)
                        {
                            activePlanet = chosenPlanet;
                        }
                    }
                }
            }

            SetExactConductorTime(conductor, targetTime, false);

            scrCamera camera = scrCamera.instance;
            scrPlanet cameraPlanet = activePlanet ?? controller.chosenPlanet;
            if (camera != null && cameraPlanet != null)
            {
                camera.ViewObjectInstant(cameraPlanet.transform, false);
            }
            Main.Logger.Log("Committed timeline seek at " + FormatTimelineTime(targetTime)
                + "; rebuilt floor " + floorIndex + " and player planet state.");

            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx != null)
            {
                // 原生 Scrub 会消费到格入口为止的特效；把剩下的
                // 有序特效推进到精确的格内时间。
                vfx.ScrubToTime(targetTime);
                PauseScrubbedVfxTweens(vfx);
            }

            scrDecorationManager decorationManager = scrDecorationManager.instance;
            if (decorationManager != null && decorationManager.allDecorations != null)
            {
                foreach (scrDecoration decoration in decorationManager.allDecorations)
                {
                    if (decoration != null)
                    {
                        decoration.UpdatePosition();
                    }
                }
            }
            Physics2D.SyncTransforms();

            editor.pausedInPlayMode = true;
            TryApplyNativePause(controller, true, false);
            AudioListener.pause = true;
        }

        private static void AbortPreviewRestart()
        {
            bool runtimeMayExist = RuntimeMayStillBeStarting();
            evaluatorBackend.End();
            staticPreviewFrameActive = false;
            RestoreStaticPreviewNativeUpdates();
            nativeHandoffPending = false;
            if (Main.Overlay != null)
            {
                Main.Overlay.HidePreviewHandoffImmediate();
            }
            previewRequested = false;
            nativePlaybackStarted = false;
            desiredPaused = false;
            nativeTransportHeld = false;
            heldPreviewTime = float.NaN;
            pauseTransitionPending = false;
            pauseReapplyRequested = false;
            timelineScrubActive = false;
            nativeScrubPrepared = false;
            nativeScrubBoundaries.Clear();
            restartTargetTime = 0f;
            restartCommitExactTime = false;
            restartResumeAfterReady = false;
            BeginPreviewAbort(runtimeMayExist);
        }

        private static void BeginPreviewAbort(bool runtimeMayExist)
        {
            restartAbortStableFrames = 0;
            restartPhaseFrame = Time.frameCount;
            restartStartedAt = Time.unscaledTime;
            Time.timeScale = 1f;

            if (!runtimeMayExist)
            {
                FinishPreviewAbort();
                return;
            }

            restartPhase = PreviewRestartPhase.Aborting;
            AudioListener.pause = true;
            RequestNativeExit();
        }

        private static void TickPreviewAbort()
        {
            Cursor.visible = true;
            Time.timeScale = 1f;
            AudioListener.pause = true;

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                restartAbortStableFrames++;
                if (restartAbortStableFrames >= 2)
                {
                    FinishPreviewAbort();
                }
                return;
            }

            if (editor.inStrictlyEditingMode && !editor.playMode)
            {
                restartAbortStableFrames++;
                if (restartAbortStableFrames >= 3)
                {
                    FinishPreviewAbort();
                }
                return;
            }

            restartAbortStableFrames = 0;
            RequestNativeExit();
        }

        private static bool RuntimeMayStillBeStarting()
        {
            scnEditor editor = scnEditor.instance;
            return nativePlaybackStarted
                || restartPlayIssued
                || restartPhase == PreviewRestartPhase.WaitingForRuntime
                || (editor != null && (!editor.inStrictlyEditingMode || editor.playMode));
        }

        private static void RequestNativeExit()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            try
            {
                allowNativeExit = true;
                editor.SwitchToEditMode(false);
            }
            catch
            {
                // 中止阶段会重试，直到编辑器确认进入严格编辑模式。
            }
            finally
            {
                allowNativeExit = false;
            }
        }

        private static void FinishPreviewAbort()
        {
            restartPhase = PreviewRestartPhase.None;
            nativeRestartActive = false;
            restartPlayIssued = false;
            restartAbortStableFrames = 0;
            restartTargetTime = 0f;
            restartCommitExactTime = false;
            allowNativeExit = false;
            RestoreAutoMode();
            Time.timeScale = 1f;
            AudioListener.pause = false;
            Cursor.visible = true;
            nativeHandoffPending = false;
            if (Main.Overlay != null)
            {
                Main.Overlay.HidePreviewHandoffImmediate();
            }
        }

        private static bool IsPreviewRuntimeReady(
            scnEditor editor,
            scrController controller,
            scrConductor conductor)
        {
            if (editor == null || editor.inStrictlyEditingMode || !editor.playMode
                || controller == null || conductor == null || conductor.song == null
                || conductor.song.clip == null)
            {
                return false;
            }

            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return false;
            }
            foreach (scrPlayer player in playerManager.players)
            {
                if (player != null && player.planetarySystem != null
                    && player.planetarySystem.chosenPlanet != null)
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool TrySetPaused(bool paused, out string status)
        {
            status = paused ? "预览已冻结" : "继续播放";
            scnEditor editor = scnEditor.instance;
            scrController controller = scrController.instance;
            if (editor == null || controller == null || (!editor.playMode && !nativePlaybackStarted))
            {
                status = "预览尚未启动";
                return false;
            }

            bool currentlyPaused = previewRequested
                ? desiredPaused
                : editor.pausedInPlayMode || controller.paused;
            if (currentlyPaused == paused)
            {
                if (paused)
                {
                    ApplyPausedState(editor, controller, scrConductor.instance);
                }
                else
                {
                    ApplyPlayingState(editor, controller);
                }
                return true;
            }

            bool previousDesiredPaused = desiredPaused;
            float previousHeldTime = heldPreviewTime;
            bool previousTransitionPending = pauseTransitionPending;
            bool previousReapplyRequested = pauseReapplyRequested;
            bool previousNativeTransportHeld = nativeTransportHeld;
            try
            {
                scrConductor conductor = scrConductor.instance;
                if (paused)
                {
                    // 暂停位置要用求值器自己的时钟，不能一律读 conductor。
                    //
                    // conductor 的 songposition_minusi 由
                    // (dspTime - dspTimeSong - calibration) 推导，而没音乐的
                    // 谱面根本没有那个锚点，它停在旧值不推进。求值器却一直
                    // 在按帧时间走，于是暂停时用 conductor 的值覆盖
                    // heldPreviewTime，会把时间线拉回一个更早的位置——
                    // 恢复播放后画面就从那儿接上，前后对不上。
                    //
                    // 有歌时两者本来就一致（求值器的时钟就是跟着它走的），
                    // 所以这里用哪个都一样；没歌时只有求值器那个是对的。
                    float pauseTime = previewClockRunning || evaluatorOwnedActive
                        ? previewClockTime
                        : ReadConductorTime();
                    editor.pausedInPlayMode = true;
                    nativeTransportHeld = true;
                    bool nativePaused = TryApplyNativePause(controller, true, false);
                    heldPreviewTime = pauseTime;
                    desiredPaused = true;
                    pauseReapplyRequested = false;
                    pauseTransitionPending = !nativePaused;
                    ApplyPausedState(editor, controller, conductor);
                }
                else
                {
                    // 兜底同理：heldPreviewTime 缺失时不能读 conductor——
                    // 没音乐的谱面它的时钟不推进，会把恢复位置拉回旧值。
                    float resumeTime = float.IsNaN(heldPreviewTime)
                        ? (evaluatorOwnedActive ? previewClockTime : ReadConductorTime())
                        : heldPreviewTime;
                    bool resumeExactTime = !nativeTransportHeld;
                    editor.pausedInPlayMode = false;
                    if (resumeExactTime)
                    {
                        LogConductorState(conductor, "resume-before", resumeTime);
                        LogPlanetState("resume-before", resumeTime);
                        LogFloorTransformState("resume-before");
                        LogVfxQueueState("resume-before");
                        if (VerboseDiagnostics)
                        {
                            Main.Logger.Log("[ResumeDebug] checkpointNum=" + GCS.checkpointNum
                                + " controllerSeqID=" + (scrController.instance == null
                                    ? -1 : scrController.instance.currentSeqID)
                                + " resumeTime=" + FormatTimelineTime(resumeTime)
                                + " floorCount=" + (editor.floors == null ? 0 : editor.floors.Count) + ".");
                        }
                        controller.forceNoCountdown = true;
                        if (conductor != null)
                        {
                            conductor.fastTakeoff = true;
                        }
                        // 精确恢复到播放头所在处。上一个实现
                        // 会把场景推进到独立倒计时的末尾
                        // (max(resumeTime, countdownTime - addoffset)), which forced a
                        // 跳，结果跳到约 1.735s，让场景和拖动预览失步。
                        // SetExactConductorTime 已经把游戏时间映射到片段时间
                        // (clipTime = targetTime + addoffset - countdownTime) and
                        // 并用匹配的延迟排程歌曲，所以倒计时
                        // 自然播完，音频保持对齐。
                        // 拖动可以把播放头移到另一个格上，而这次恢复
                        // 不重建运行时——所以球还站在拖动
                        // *开始* 时的那个格上。它的 snappedLastAngle 是那个格的，
                        // 而原生角度公式建立在它之上，所以无论时钟多准，
                        // 画面都会落后整整一格。重定位对普通
                        // 暂停/恢复，以及在一个格内的拖动，都是空操作，
                        // 而那是常见情形，所以有这个格比较。
                        RelocatePlanetForResume(editor, resumeTime);
                        SetExactConductorTime(conductor, resumeTime, true);
                        // 特效队列必须先重建才能被拖动定位，而那是
                        // 两步——scnGame.FinishCustomLevelLoading 处的原生恢复
                        // 两个都跑，顺序如下：
                        //     scnGame.SetFxPlusFromComponents(listFloors, useComponentNotationForFx);
                        //     scnGame.PrepVfx(listFloors, GCS.checkpointNum);
                        // SetFxPlusFromComponents 把每个格的 ffxPlusBase 组件走成
                        // floor.plusEffects；PrepVfx 再从那些里重新填充 scrVfxPlus.effects。
                        // 过去只调了 PrepVfx，所以 plusEffects 是空的，PrepVfx
                        // 重建出一个空队列——这就是为什么「修复」之后队列仍然测到 count=0，
                        // 相机也没有东西可套用。plusEffects 会被 scrFloor.Rewind 清空，
                        // 而 ApplyEventsToFloors 会走到那里，所以这次
                        // 重新填充是缺的那一半，而不是重复。
                        scnGame resumeLevel = editor.customLevel;
                        if (resumeLevel != null)
                        {
                            int landingFloor = FindFloorAtOrBefore(editor.floors, resumeTime);
                            scnGame.SetFxPlusFromComponents(editor.floors, controller.useComponentNotationForFx);
                            resumeLevel.PrepVfx(landingFloor, false);
                        }
                        scrVfxPlus resumeVfx = scrVfxPlus.instance;
                        if (resumeVfx != null)
                        {
                            if (VfxCurrentIndexField != null)
                            {
                                VfxCurrentIndexField.SetValue(resumeVfx, 0);
                            }
                            resumeVfx.ScrubToTime(resumeTime);
                        }

                        // 谱面的相机旋转根本不由特效队列承载。
                        // scrPlanet.MoveToNextFloor 会触发
                        //     if (!floor.hasLit) camy.RotateSmooth(floor.rotatecamera);   // torot += angle
                        // 球每落一格一次，所以一个跳过若干格的 seek 会丢掉
                        // 旧位置和新位置之间的每一次旋转——这就是为什么
                        // 相机跟着球走，却完全没显示出谱面的
                        // 取景。原生恢复会在播放头处及之前的格上
                        // 重放同样的累积，然后贴到它上面：
                        //     foreach (floor.seqID <= checkpointNum) {
                        //         camy.torot += floor.rotatecamera; camy.fromrot = camy.torot; }
                        // 从第 0 格重新算，而不是加到实时值上，所以
                        // 结果就是一次线性播放到这里会持有的旋转，
                        // 与播放头是怎么到这儿的无关。
                        scrCamera resumeCamera = scrCamera.instance;
                        if (resumeCamera != null && editor.floors != null)
                        {
                            int cameraFloor = FindFloorAtOrBefore(editor.floors, resumeTime);
                            resumeCamera.torot = 0f;
                            for (int i = 0; i <= cameraFloor && i < editor.floors.Count; i++)
                            {
                                scrFloor cameraStep = editor.floors[i];
                                if (cameraStep != null)
                                {
                                    resumeCamera.torot += cameraStep.rotatecamera;
                                }
                            }
                            resumeCamera.fromrot = resumeCamera.torot;
                        }
                        KillFloorAnimationTweens();
                        LogConductorState(conductor, "resume-after", resumeTime);
                        LogPlanetState("resume-after", resumeTime);
                        LogFloorTransformState("resume-after");
                        LogVfxQueueState("resume-after");
                    }
                    if (!TryApplyNativePause(controller, false, false))
                    {
                        if (resumeExactTime)
                        {
                            SetExactConductorTime(conductor, resumeTime, false);
                        }
                        editor.pausedInPlayMode = true;
                        ApplyPausedState(editor, controller, conductor);
                        status = "游戏正在切换状态，请稍后再试";
                        return false;
                    }
                    if (resumeExactTime)
                    {
                        ResumeScrubbedVfxTweens();
                        if (conductor != null)
                        {
                            conductor.PlayHitTimes();
                            if (VerboseDiagnostics)
                            {
                                Main.Logger.Log("[ResumeDebug] afterPlayHitTimes checkpointNum="
                                    + GCS.checkpointNum
                                    + " dspTimeSong=" + conductor.dspTimeSong.ToString("0.000")
                                    + " dsp=" + AudioSettings.dspTime.ToString("0.000")
                                    + " hitSoundVolume=" + conductor.hitSoundVolume.ToString("0.000")
                                    + " forceHitSounds=" + conductor.forceHitSounds + ".");
                            }
                        }
                    }
                    desiredPaused = false;
                    nativeTransportHeld = false;
                    heldPreviewTime = float.NaN;
                    pauseTransitionPending = false;
                    pauseReapplyRequested = false;
                    ApplyPlayingState(editor, controller);
                }

                Cursor.visible = true;
                return true;
            }
            catch (Exception exception)
            {
                desiredPaused = previousDesiredPaused;
                heldPreviewTime = previousHeldTime;
                pauseTransitionPending = previousTransitionPending;
                pauseReapplyRequested = previousReapplyRequested;
                nativeTransportHeld = previousNativeTransportHeld;
                if (previousDesiredPaused)
                {
                    TryApplyNativePause(controller, true, false);
                    ApplyPausedState(editor, controller, scrConductor.instance);
                }
                else
                {
                    TryApplyNativePause(controller, false, false);
                    ApplyPlayingState(editor, controller);
                }
                ReportError("Failed to change persistent preview pause state", exception);
                status = "预览状态切换失败";
                return false;
            }
        }

        // 只读诊断：记录格的路线旗标（Twirl/isSwirl、isCCW）
        // 和行星当前所在的格，这样能看出把播放头拖回起点时
        // 丢的是哪一份状态。
        private static void LogFloorRouteState(string phase)
        {
            if (!VerboseDiagnostics) { return; }
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null)
            {
                Main.Logger.Log("[RouteState] " + phase + " editor/floors unavailable.");
                return;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[RouteState] ").Append(phase).Append(" floors=");
            int limit = Mathf.Min(8, editor.floors.Count);
            for (int i = 0; i < limit; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null)
                {
                    builder.Append(" f").Append(i).Append("=null");
                    continue;
                }
                builder.Append(" f").Append(i)
                    .Append("(swirl=").Append(floor.isSwirl ? 1 : 0)
                    .Append(",ccw=").Append(floor.isCCW ? 1 : 0)
                    .Append(")");
            }

            scrPlayerManager players = scrPlayerManager.instance;
            if (players != null && players.players != null)
            {
                for (int i = 0; i < players.players.Length; i++)
                {
                    scrPlayer player = players.players[i];
                    if (player == null || player.planetarySystem == null)
                    {
                        continue;
                    }
                    scrPlanet chosen = player.planetarySystem.chosenPlanet;
                    int chosenFloor = chosen == null || chosen.currfloor == null
                        ? -1
                        : chosen.currfloor.seqID;
                    builder.Append(" p").Append(i)
                        .Append("(planets=").Append(player.planetarySystem.planetList == null
                            ? 0 : player.planetarySystem.planetList.Count)
                        .Append(",chosenFloor=").Append(chosenFloor)
                        .Append(")");
                }
            }
            builder.Append(".");
            Main.Logger.Log(builder.ToString());
        }

        internal static void ResetPreviewZoom()
        {
            scnEditor editor = scnEditor.instance;
            scrCamera camera = scrCamera.instance;
            if (editor == null || camera == null)
            {
                return;
            }

            editor.camUserSizeMultiplier = 1f;
            camera.userSizeMultiplier = 1f;
            if (ADOBase.controller != null)
            {
                camera.UpdateSize();
            }
        }

        internal static void EnforcePreviewZoomLock()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || !overlay.IsVisible || !overlay.IsPreviewZoomLocked)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            scrCamera camera = scrCamera.instance;
            if (editor == null || camera == null)
            {
                return;
            }

            bool changed = false;
            if (!Mathf.Approximately(editor.camUserSizeMultiplier, 1f))
            {
                editor.camUserSizeMultiplier = 1f;
                changed = true;
            }
            if (!Mathf.Approximately(camera.userSizeMultiplier, 1f))
            {
                camera.userSizeMultiplier = 1f;
                changed = true;
            }
            if (changed && ADOBase.controller != null)
            {
                camera.UpdateSize();
            }
        }

        /// <summary>
        /// 把位置锁定推到求值器上。相机没有像缩放的
        /// camUserSizeMultiplier 那样的「用户位置」字段，所以这把锁由
        /// 累积手动平移的那个域来执行；这里只是把设置镜像到它上面。
        /// </summary>
        internal static void ApplyPreviewPositionLock(bool locked)
        {
            CameraPreviewDomain.PositionLocked = locked;
        }

        /// <summary>
        /// 在一次场景重载之后把一次运行重新接起来。
        ///
        /// 别的 mod 完成自己的启动时可能重载编辑器场景，而在一次很慢或被
        /// 阻塞的更新检查下，那是作者开始播放几十秒之后。这次重载
        /// 会销毁谱面、运行时以及我们的 transport，然后对方重新打开同一份
        /// 谱面（ADOBase.RestartScene 通过 scnEditor.levelToOpenOnLoad 传递路径）。这里
        /// 等那份谱面回来，并在这次运行所处的播放头处重启 transport，
        /// 所以中断只是闪一下，而不是重置回开头。
        ///
        /// 这个闸门和 StartEditorNativePlayback 需要的是同一个：一个格存在的关卡，
        /// 且 conductor 能跑。等待是有界的，因为一次永远
        /// 产生不出可用编辑器的重载，不该让桥在剩下的
        /// 会话里一直等下去。
        /// </summary>
        internal static void TickSceneReloadResume()
        {
            if (float.IsNaN(sceneReloadResumeTime))
            {
                return;
            }

            sceneReloadWaitFrames++;
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                if (sceneReloadWaitFrames > 1800)
                {
                    Main.Logger.Log("[TrackB] gave up waiting for the reloaded editor.");
                    sceneReloadResumeTime = float.NaN;
                }
                return;
            }

            // 求值器会话属于那个被销毁的场景，所以在 transport 启动之前
            // 先确认没有过期的东西占着画面。
            evaluatorBackend.End();
            evaluatorOwnedActive = false;
            previewRequested = true;
            RestoreStaticPreviewNativeUpdates();

            float resumeAt = sceneReloadResumeTime;
            sceneReloadResumeTime = float.NaN;
            sceneReloadWaitFrames = 0;
            string status;
            Main.Logger.Log("[TrackB] resuming at " + FormatTimelineTime(resumeAt)
                + " after the scene reload.");
            StartEditorNativePlayback(resumeAt, out status);
        }

        /// <summary>
        /// 在场景加载之后让工作台停摆，无论起因是谁。
        ///
        /// transport 和它引用的一切都活在刚刚被替换掉的那个场景里，
        /// 所以之后唯一安全的状态就是「什么都没在播放」。每个旗标都由
        /// TryStopPlayback 清掉，它同时拆掉求值器会话，并还原
        /// 它曾冻结的原生相机/行星更新。叠加层会在下一次 tick 时
        /// 从新场景重建自己的 UI。
        ///
        /// 它存在是因为场景加载不总是我们引起的。别的 mod 会从
        /// OnEnable/OnDisable 重置编辑器场景，而它的 enable 是异步的，所以重置可能落在
        /// 一次运行的中间——表现就是 transport 死掉、编辑器每次尝试
        /// 都在不同的位置重新出现。
        /// </summary>
        /// <summary>Called by the SelectFloor patch; see FollowSelectedFloor.</summary>
        /// <summary>
        /// 「这次 `SelectFloor` 是谁发起的」。一次性，`OnFloorSelected` 读完立刻复位。
        ///
        /// **为什么需要它**：`scnEditor.SelectFloor` 是所有人共用的入口 ——
        /// 用户点轨道、点事件块（我们要同步原生选中）、把选中挪到播放头、
        /// 预览重启定位，走的都是它。原来只靠 `LocateOnTrackClick` /
        /// `FollowSelectionOnClick` 两个开关在补丁里猜来源，结果两个开关**互相串**：
        /// 开了「定位」点事件块也会 1 秒加速走过去，开了「选中跟随」点轨道也会跳。
        /// </summary>
        internal const int FloorSelectNative = 0;
        internal const int FloorSelectTimelineEvent = 1;
        internal const int FloorSelectPlayheadSync = 2;
        internal const int FloorSelectPreviewRestart = 3;

        private static int nextFloorSelectKind;

        /// <summary>在调用 `SelectFloor` **之前**标一下来源。</summary>
        internal static void MarkNextFloorSelect(int kind)
        {
            nextFloorSelectKind = kind;
        }

        internal static void OnFloorSelected(scrFloor floor)
        {
            int kind = nextFloorSelectKind;
            nextFloorSelectKind = FloorSelectNative;

            bool locate = Main.Settings != null && Main.Settings.LocateOnTrackClick;
            bool follow = Main.Settings != null && Main.Settings.FollowSelectionOnClick;
            if (floor != null && Main.VerboseLog && (locate || follow))
            {
                // 一行一拍。这个入口只在"选中项真的变了"时进来（方向键、点轨道、
                // 原生格子面板），不是每帧，留着能直接看出点轨道到底有没有走到这里。
                Main.Logger.Log("[定位] 选格 f" + floor.seqID
                    + " t=" + ((float)floor.entryTime).ToString("0.000")
                    + " 来源=" + kind + " 定位=" + locate + " 选中跟随=" + follow + ".");
            }

            // **自己人同步选中，不带动画面。**
            // 把选中挪到播放头、预览重启定位这两条都是"状态跟随"，反过来再让
            // 画面跟着跳会形成回环（尤其定位开着时：选格 → 走位 → 又改选格）。
            if (kind == FloorSelectPlayheadSync || kind == FloorSelectPreviewRestart)
            {
                return;
            }

            // 点事件块：只听「点事件跟随」，**和点轨道一样 1 秒加速走过去**
            // （两处手感统一，用户不用记哪边是瞬跳哪边是走位）。
            if (kind == FloorSelectTimelineEvent)
            {
                FollowSelectedFloor(floor, false, true);
                return;
            }

            // 用户真的点了轨道（或按方向键）：**只听「定位」**。
            // 定位关着就什么都不动 —— 否则「选中跟随」会连带把点轨道也变成跳画面，
            // 两个开关就又串回去了。
            if (!locate)
            {
                return;
            }
            FollowSelectedFloor(floor, true, true);
        }

        /// <summary>
        /// 直接按格跳（定位模式点轨道用，走位版）。
        ///
        /// 和 `JumpToNativeSelection` 的区别：一个是"指定格 + 加速走过去"，
        /// 另一个是"打开工作台时瞬跳到原生选中项"。两条都落到同一个
        /// `FollowSelectedFloor`，只是 `animate` 不同。
        /// </summary>
        internal static bool JumpPreviewToFloor(scrFloor floor)
        {
            if (floor == null)
            {
                return false;
            }
            if (!evaluatorOwnedActive && !previewRequested)
            {
                return false;
            }

            FollowSelectedFloor(floor, true, true);
            return true;
        }

        /// <summary>
        /// 打开工作台时把播放头落到原生编辑器当前选中的那一格。
        ///
        /// 返回 false 表示预览会话还没起来（打开工作台是异步的：媒体预载的加载页
        /// 可能还盖着，求值器还没接管），调用方下一帧再试 —— 见
        /// `EditorStudioOverlay.TickPendingSelectionJump`。
        /// 返回 true 表示这件事已经了结（跳过了，或者本来就没有选中格）。
        /// </summary>
        internal static bool JumpToNativeSelection()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return false;
            }
            if (editor.selectedFloors == null || editor.selectedFloors.Count == 0)
            {
                // 没选中格：没有可跳的目标，别让调用方一直重试。
                return true;
            }

            scrFloor floor = editor.selectedFloors[0];
            if (floor == null)
            {
                return true;
            }

            if (!evaluatorOwnedActive && !previewRequested)
            {
                return false;
            }

            // 打开工作台属于"用户明确要过去"，不受 `FollowSelectionOnClick`
            // 那个开关限制 —— 那个开关管的是"点事件块时要不要跟"。
            //
            // **这里是瞬跳，不走位**：打开工作台那一瞬间画面就应该已经在
            // 选中的格上；"1 秒内加速过去"只属于在 AES 里点轨道那条路。
            FollowSelectedFloor(floor, true, false);
            if (Main.VerboseLog)
            {
                Main.Logger.Log("[定位] 打开工作台瞬跳到 f" + floor.seqID
                    + " t=" + ((float)floor.entryTime).ToString("0.000") + ".");
            }
            return true;
        }

        internal static void HandleSceneReloaded(string sceneName)
        {
            if (!trackBTransportActive && !nativePlaybackStarted && !previewRequested)
            {
                return;
            }

            Main.Logger.Log("[TrackB] standing down after a scene load into '" + sceneName + "'.");

            // 记住作者在哪，这样重载后的编辑器可用时运行能重新接起来。
            // 对方会自己重新打开同一份谱面（ADOBase.RestartScene
            // 把 editor.customLevel.levelPath 塞进 scnEditor.levelToOpenOnLoad），所以
            // 恢复只需要等关卡回来，然后在这个时间点
            // 重新进入播放。
            sceneReloadResumeTime = Mathf.Max(0f, lastKnownPlayhead);
            sceneReloadWaitFrames = 0;
            Main.Logger.Log("[TrackB] will resume at " + FormatTimelineTime(sceneReloadResumeTime)
                + " once the reloaded editor is ready.");

            // 刻意只置旗标。sceneLoaded 是在新场景还在起来的过程中
            // 派发的，所以 TryStopPlayback 会去拿的那些单例（conductor、相机、
            // controller）可能还不存在——在这里调用它，是拿一个半成品
            // 场景去跑拆除。它本该释放的一切都归属于那个刚被销毁的
            // 场景，所以已经没有我们的东西可释放：对象已经
            // 没了，剩下的唯一工作就是不再以为我们在播放。
            previewRequested = false;
            nativePlaybackStarted = false;
            trackBTransportActive = false;
            trackBStartFrame = -1;
            trackBPostCommitFrames = 0;
            desiredPaused = true;
            pauseTransitionPending = false;
            pauseReapplyRequested = false;
            nativeTransportHeld = false;
            heldPreviewTime = float.NaN;
            timelineScrubActive = false;
            nativeHandoffPending = false;
            staticPreviewFrameActive = false;
            restartPhase = PreviewRestartPhase.None;
            restartTargetTime = 0f;
            lastTimelineScrubFloor = -1;
            lastTimelineScrubEditorId = 0;
            lastTimelineScrubTime = float.NaN;
            lastTrackBPressFrame = -100000;
            EditorSceneInteraction.Reset();
            EditorCommandHistory.Clear();
        }

        /// <summary>
        /// 关闭工作台/重载场景时的全面清理。由 EditorStudioOverlay 调用(隐藏与重建两处)。
        ///
        /// 【现状:活路径,勿删】
        /// 它内部会调 BeginPreviewAbort,而后者属于 Track A/B 的重启状态机 —— 也就是
        /// restartPhase。所以那个状态机不能单独删除:中止这一半至今仍在用。
        /// 清理 Track A/B 时,要把 BeginPreviewAbort / TickPreviewAbort / RequestNativeExit
        /// 这条中止链一起迁移到 Track C 的拆除流程里,再删。
        /// </summary>
        public static bool TryStopPlayback()
        {
            bool runtimeMayExist = RuntimeMayStillBeStarting();
            scrConductor previewConductor = scrConductor.instance;
            if (staticPreviewFrameActive && previewConductor != null && previewConductor.song != null)
            {
                previewConductor.song.Pause();
            }
            previewRequested = false;
            nativePlaybackStarted = false;
            // 走位一起撤：关掉工作台之后没人再 tick 它，留着等于下次打开
            // 从一半的位置接着走。
            CancelPlayheadTravel();
            // Track B 自己的旗标得和其他一起清：正是它把一次按空格
            // 路由到暂停/恢复分支，而它指向的运行时已经被场景重载
            // 销毁了。留着它，下一次按键会试图去暂停一个
            // 不再存在的 transport，而因为 scnEditor.playMode 在空闲编辑器里读到 true，
            // 没有别的旗标能纠正它。
            trackBTransportActive = false;
            trackBStartFrame = -1;
            trackBPostCommitFrames = 0;
            desiredPaused = false;
            pauseTransitionPending = false;
            pauseReapplyRequested = false;
            nativeTransportHeld = false;
            heldPreviewTime = float.NaN;
            lastTimelineScrubFloor = -1;
            lastTimelineScrubEditorId = 0;
            lastTimelineScrubTime = float.NaN;
            timelineScrubActive = false;
            if (Main.Overlay != null)
            {
                Main.Overlay.HidePreviewHandoffImmediate();
            }
            evaluatorBackend.End();
            RestoreStaticPreviewNativeUpdates();
            restartTargetTime = 0f;
            restartCommitExactTime = false;
            EditorCommandHistory.Clear();
            EditorSceneInteraction.Reset();
            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx != null && vfx.pausedTweens != null)
            {
                KillResidentNativeScrubTweens();
            }

            scnEditor editor = scnEditor.instance;
            try
            {
                scrConductor conductor = scrConductor.instance;
                if (conductor != null)
                {
                    conductor.enabled = true;
                }
                scrController controller = scrController.instance;
                if (controller != null)
                {
                    controller.enabled = true;
                }
                BeginPreviewAbort(runtimeMayExist);
                return true;
            }
            catch (Exception exception)
            {
                ReportError("Failed to close persistent preview", exception);
                return false;
            }
            finally
            {
                allowNativeExit = false;
                Time.timeScale = 1f;
                if (restartPhase == PreviewRestartPhase.Aborting)
                {
                    AudioListener.pause = true;
                }
                else
                {
                    RestoreAutoMode();
                    AudioListener.pause = false;
                }
                Cursor.visible = true;
            }
        }

        internal static bool ShouldBlockNativeExit(bool clsToEditor)
        {
            EditorStudioOverlay overlay = Main.Overlay;
            return previewRequested
                && !allowNativeExit
                && overlay != null
                && overlay.IsVisible;
        }

        private static int FindFloorAtOrBefore(System.Collections.Generic.List<scrFloor> floors, float time)
        {
            int low = 0;
            int high = floors.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                scrFloor floor = floors[middle];
                if (floor != null && Convert.ToSingle(floor.entryTime) <= time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return Mathf.Clamp(low - 1, 0, floors.Count - 1);
        }

        /// <summary>
        /// 把行星重新指向播放头现在所在的格，用于一次不
        /// 重建运行时的恢复。
        ///
        /// 求值器从自己的状态绘制拖动，所以它能显示任何格；
        /// 原生运行时则保留它被留在的那个格。只有重建路径
        /// （RebuildResidentNativeCheckpoint、CommitRestartAtExactTime）过去会重定位，所以一次
        /// 跨过边界、又没有经过重建就恢复的拖动，会把球留在它旧的
        /// 格上。行星已经在正确位置时什么都不做，这让普通的
        /// 暂停/恢复——以及单个格内的拖动——不受影响。
        /// </summary>
        private static void RelocatePlanetForResume(scnEditor editor, float resumeTime)
        {
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return;
            }

            int floorIndex = FindFloorAtOrBefore(editor.floors, resumeTime);
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                scrPlanet chosen = player.planetarySystem.chosenPlanet;
                if (chosen == null || chosen.currfloor == null
                    || chosen.currfloor.seqID == floorIndex)
                {
                    continue;
                }

                player.planetarySystem.ScrubToFloorNumber(floorIndex, null, true);
                // lastHit 刻意不在这里设置：SetExactConductorTime 紧接着
                // 在它之后运行，写入落地格的入口时间，而那正是角度
                // 公式需要的。游戏会在下一次 Update 时从它重算角度，所以
                // 在序列的这一处不需要刷新任何东西。
                Main.Logger.Log("[ResumeRelocate] playhead floor became " + floorIndex
                    + " at " + FormatTimelineTime(resumeTime) + ".");
            }
        }

        /// <summary>
        /// 把 conductor 和歌曲放到精确的游戏时间上。
        ///
        /// 音频片段位置（targetTime + addoffset - countdownTime）在谱面的
        /// 独立倒计时还在跑的时候会
        /// 变成负数，而 AudioSource.time 会静默
        /// 拒绝负值。所以直接赋值会让歌曲
        /// 停在它被暂停的地方，读起来就是「音乐从来
        /// 没重置过」。改成把前奏转成排程器延迟，
        /// song.time 只会收到非负的片段位置。
        /// </summary>
        private static void SetExactConductorTime(scrConductor conductor, float targetTime, bool startAudio)
        {
            if (conductor == null)
            {
                return;
            }

            // scnGame.Play -> Start_Rewind 启动 StartMusicCo，它等
            // ~0.1s 之后重写 song.time = 0。杀掉它，就阻止那个待处理协程
            // 覆盖这次 seek。当我们自己就是从那个协程的排程回调
            // 被调用的时候，跳过这一段。
            //
            // 原生重启期间也跳过。那时协程才是播放歌曲的东西：
            // scnGame.Play 启动它，取消它会让片段
            // 没人排程，于是之后每一次 SetScheduledStartTime 和 song.time
            // 赋值都不起作用，播放起来无声、片段停在零。
            if (!evaluatorAudioStarting && !nativeRestartActive)
            {
                CancelPendingConductorMusicStart(conductor);
            }
            targetTime = Mathf.Max(0f, targetTime);
            double pitch = conductor.song == null ? 1d : Math.Max(0.001d, conductor.song.pitch);
            double countdownTime = GetSeparateCountdownTime(conductor);
            double currentDspTime = AudioSettings.dspTime;
            conductor.dspTime = currentDspTime;

            double startLead = 0d;
            // 本次调用先假定"不定帧"。只有真正起播、且走的是排程余量那条路
            // （rawClipTime >= 0）时才置位 —— 否则上一轮留下的 true 会让
            // 场景锚点被多加一次 0.1 秒。
            holdPictureForAudioStart = false;
            if (conductor.song != null && conductor.song.clip != null)
            {
                if (AudioManager.Instance != null)
                {
                    AudioManager.Instance.StopAllSounds();
                }

                // **`calibration_i` 要乘 pitch。**
                //
                // 从游戏源码推出来的关系是——`scrConductor.Update` 里
                //   scene = (dsp - dspTimeSong - cal_i) * pitch - addoffset
                // 而 `(dsp - dspTimeSong) * pitch` 正是片段位置，所以
                //   clip = scene + addoffset + cal_i * pitch - countdownTime
                //
                // 这里原来写的是 `+ calibration_i`（不乘），于是音频整体
                // 差了 `cal_i * (pitch - 1)`。pitch=100 时它恒为 0，
                // **只有 pitch≠100 才会暴露**：实测 pitch=160、
                // cal_i=0.248 时差 0.149 片段单位，用户用微调旋钮量到
                // 90ms（×1.6 = 0.144），正好对上。
                //
                // 这条第一轮就推出来过，但当时拿「差 0.7 秒」去套、
                // 觉得量级不对就否掉了——而 0.7 秒是个很粗的目测值。
                // **排程必须留余量，否则音频会滑到下一个缓冲区边界。**
                //
                // 实测（同一台机器、同一次会话，`[ClipStart]` 那几行）：
                //   scheduled=+0.0ms   → actualSlip = 0 / 0 / 42.7 / 42.7 ms
                //   scheduled=+881.5ms（倒计时本身就是余量）→ actualSlip = 14.5ms
                // 也就是"要求音频立刻开始"时，它有一半概率整整晚一个缓冲区
                // （48kHz × 2048 = 42.7ms）。这正是"拖动后播放时好时坏、
                // 大约一半"的来源。原版 `scrConductor.ScrubMusicToTime` 排到
                // `dspTime + 0.1`，就是在留这段余量。
                //
                // 余量只加在**排程与片段位置**上，场景锚点（下面 dspTimeSong /
                // songposition_minusi）不动：画面此刻仍在用户拖到的位置，
                // 音频 0.1 秒后从"那一刻场景该在的地方"开始——两者一致，
                // 只是开头多了一小段没有声音的等待（和倒计时的味道一样）。
                double rawClipTime = targetTime + conductor.addoffset - countdownTime
                    + scrConductor.calibration_i * pitch;

                // 倒计时前奏（rawClipTime < 0）本身就是余量，不能再加一遍。
                startLead = rawClipTime < 0d
                    ? -rawClipTime / pitch
                    : MinSchedulingLeadSeconds;

                // **余量只加在排程上，不加在片段位置上。**
                //
                // 原来这里写的是 `clipTime = rawClipTime + startLead`，等于
                // "音频晚 0.1 秒开始，但直接从 0.1 秒之后的内容播" ——
                // 于是那 0.1 秒的音乐被**凭空剪掉**。音画仍然同步（画面也走了
                // 0.1 秒），听起来却是"音乐开头少了一截、还顿一下"。
                //
                // 现在的做法：片段从 target 的内容开始，同时**把画面定住**
                // 直到音频真的开始（见 TickEvaluatorOwnedPreview 里的
                // audioStartDsp 等待分支）。这样既不剪内容、也不产生音画偏移，
                // 代价只是开头那一小段画面停顿（和倒计时的味道一样）。
                double clipTime = rawClipTime;
                if (clipTime < conductor.song.clip.length)
                {
                    // **排程无条件做，不再只在 startAudio 时做。**
                    //
                    // 原版 `scrConductor.ScrubMusicToTime` 是**只要定位就重新排程**
                    // （固定排到 dsp+0.1，把余量加在排程上）。我们原来只在
                    // startAudio 时排程，于是拖动/恢复
                    // 那条路（startAudio=false）**只写 `.time`、不重新排程**——
                    // 等于在一个还在播的源上裸 seek，落点最粗。
                    // 实测那条路也确实比从-0 起播更容易出现偏移。
                    //
                    // AudioSource.time 直接拒绝负值，所以倒计时的前奏必须由
                    // 排程的起始来承载，而不是靠片段偏移。
                    conductor.song.SetScheduledStartTime(currentDspTime + startLead);

                    if (startAudio)
                    {
                        // audioStartDsp 是"等声音变可听"的门卫标记，**只在真正起播时设**
                        // ——拖动定位时设它会让画面冻到排程时刻才动。
                        // 歌曲被排到一个仍在未来的 dsp 时刻，而 StartMusicCo 已经对它
                        // 跑过 PlayScheduled，所以音频要到设备时钟越过它才开始；
                        // 场景时钟上面已经被移到 targetTime，不等的话画面就会比音乐
                        // 快出正好这么多——听起来就是声音晚到一拍。
                        audioStartDsp = currentDspTime + startLead;
                        audioStartHoldFrames = 0;
                        // **只有"排程余量"那种情况才定帧等**。
                        // 倒计时前奏（rawClipTime < 0）时 startLead 是真实内容
                        // 长度，可能长达两秒 —— 那种情况绝不能定帧，否则每次
                        // 起播画面都会僵住整个倒计时（老注释里踩过这个坑）。
                        holdPictureForAudioStart = rawClipTime >= 0d;
                    }

                    conductor.song.time = (float)Math.Max(0d, clipTime);

                    // 压缩音频会量化 seek：Ogg Vorbis 片段没有采样
                }
            }

            // dspTimeSong 定义场景时钟：conductor 这样推导
            // songposition_minusi = (dspTime - dspTimeSong - calibration) * pitch
            // - addoffset。它必须 *此刻* 把场景放到 targetTime；
            // 把倒计时前奏折进它，会让场景从
            // 负时间开始，于是时间轴和画面冻住，直到
            // 倒计时走完。两者必须分开：dspTimeSong 放
            // 场景，SetScheduledStartTime 放音频。
            //
            // **但"定帧等音频"那条路要把锚点也一起后移。**
            // 音频是从 target 的内容开始的（`clipTime = rawClipTime`），
            // 而排程在 `now + startLead`。若锚点仍按 `now` 算，设备时钟一越过
            // 排程时刻，由 dsp 推导出来的场景时间就已经是 `target + startLead`，
            // 比音乐内容快出整整 0.1 秒 —— 音画会脱拍（上一版就是这么错的）。
            // 把锚点改成 `now + startLead`，两者在那一刻才对齐。
            double sceneAnchorDsp = holdPictureForAudioStart
                ? currentDspTime + startLead
                : currentDspTime;
            conductor.dspTimeSong = sceneAnchorDsp
                - targetTime / pitch
                - conductor.addoffset / pitch
                - scrConductor.calibration_i;
            conductor.songposition_minusi = targetTime;
            conductor.deltaSongPos = 0d;

            if (startAudio)
            {
                HandoffProbe.MarkAudio(targetTime);
                HandoffProbe.ExpectNativeFirstFrame();
            }
            // 拍/小节游标只由 scrConductor.Update 推进（每帧
            // 一步），所以必须在这里把它们移到 seek 目标，
            // 否则旧游标和新时间之间的每一拍都会被静默
            // 跳过。从 [ConductorState] 验证过：拖回 0 之后
            // 游标还停在 beat 8 / 3.840s。
            AlignConductorBeatCursor(conductor, targetTime);

            scrPlayerManager players = scrPlayerManager.instance;
            if (players != null && players.players != null)
            {
                // 球渲染出的角度是
                //     snappedLastAngle + (songposition_minusi - player.lastHit) / crotchetAtStart
                //                        * PI * speed * dir          -- scrPlanet.Update_RefreshAngles
                // 所以把 lastHit 设成 seek 目标会让那个差为零，把
                // 球钉在它那格的 *起点*。这正是反馈里的「拖动之后
                // 球从砖的开口角开始，而不是从拖动
                // 留下的地方继续」，它还悄悄撤销了 CommitRestartAtExactTime，
                // 后者在调用这里之前刚刚设了 lastHit = entryTime。
                // 游戏用的是落地格自己的入口时间（scrController.Scrub：
                // lastHit = scrFloor.entryTime），把差值留成 seek 在格内
                // 深入多远——也就是拖动正在显示的格内相位。
                float landingTime = targetTime;
                scnEditor landingEditor = scnEditor.instance;
                if (landingEditor != null && landingEditor.floors != null
                    && landingEditor.floors.Count > 0)
                {
                    landingTime = (float)landingEditor.floors[
                        FindFloorAtOrBefore(landingEditor.floors, targetTime)].entryTime;
                }

                for (int i = 0; i < players.players.Length; i++)
                {
                    if (players.players[i] != null)
                    {
                        players.players[i].lastHit = landingTime;
                    }
                }
            }

            if (startAudio)
            {
                Main.Logger.Log("[NativeAudio] target=" + FormatTimelineTime(targetTime)
                    + " clip=" + (targetTime + conductor.addoffset - countdownTime).ToString("0.000")
                    + " countdown=" + countdownTime.ToString("0.000")
                    + " startLead=" + startLead.ToString("0.000")
                    + " offset=" + conductor.addoffset.ToString("0.000")
                    + " pitch=" + pitch.ToString("0.000") + ".");
            }
        }

        /// <summary>
        /// 只读诊断。dump 恢复路径被怀疑
        /// 留成过期的每一个 conductor 字段（歌曲已启动旗标、crotchet、拍/小节
        /// 游标、dsp 锚点、片段位置）。在恢复前后各比一次，
        /// 这样真正的残留会出现在日志里，
        /// 而不是靠猜。
        /// </summary>
        private static void LogConductorState(scrConductor conductor, string phase, float targetTime)
        {
            if (!VerboseDiagnostics) { return; }
            if (conductor == null)
            {
                Main.Logger.Log("[ConductorState] " + phase + " conductor=null.");
                return;
            }

            double nextBeat = ConductorNextBeatTimeField == null
                ? -1d
                : Convert.ToDouble(ConductorNextBeatTimeField.GetValue(conductor));
            double nextBar = ConductorNextBarTimeField == null
                ? -1d
                : Convert.ToDouble(ConductorNextBarTimeField.GetValue(conductor));
            float songTime = conductor.song == null ? -1f : conductor.song.time;
            bool songPlaying = conductor.song != null && conductor.song.isPlaying;
            bool coroutinePending = ConductorStartMusicCoroutineField != null
                && ConductorStartMusicCoroutineField.GetValue(conductor) != null;

            Main.Logger.Log("[ConductorState] " + phase
                + " target=" + FormatTimelineTime(targetTime)
                + " hasSongStarted=" + conductor.hasSongStarted
                + " crotchetAtStart=" + conductor.crotchetAtStart.ToString("0.0000")
                + " beatNumber=" + conductor.beatNumber
                + " barNumber=" + conductor.barNumber
                + " nextBeat=" + nextBeat.ToString("0.000")
                + " nextBar=" + nextBar.ToString("0.000")
                + " dspTimeSong=" + conductor.dspTimeSong.ToString("0.000")
                + " songPos=" + conductor.songposition_minusi.ToString("0.000")
                + " addoffset=" + conductor.addoffset.ToString("0.000")
                + " songTime=" + songTime.ToString("0.000")
                + " songPlaying=" + songPlaying
                + " musicCoroutinePending=" + coroutinePending
                + " fastTakeoff=" + conductor.fastTakeoff
                + " separateCountdown=" + conductor.separateCountdownTime
                + " countdownTicks=" + conductor.countdownTicks
                + " enabled=" + conductor.enabled + ".");
        }

        /// <summary>
        /// 针对反馈的「球落地了，但轨道角度不对」——发生在拖回起点
        /// 再恢复之后——的只读诊断。dump 行星的角度
        /// 状态和当前格的旋转量，那正是 Update_RefreshAngles
        /// 在 stickToFloor 置位时减掉的东西。
        /// </summary>
        /// <summary>
        /// 针对反馈的「球落地了，但轨道角度不对」——发生在拖回起点
        /// 再恢复之后——的只读诊断。dump 拖动预览留下的
        /// 格变换，因为 MoveTrack 写格旋转，
        /// 而 Update_RefreshAngles 在 stickToFloor 置位时会减掉那次旋转。
        /// </summary>
        private static void LogFloorTransformState(string phase)
        {
            if (!VerboseDiagnostics) { return; }
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null)
            {
                Main.Logger.Log("[FloorTransform] " + phase + " floors unavailable.");
                return;
            }

            // 扫每一格，只报告那些实时变换
            // 不再匹配已烘焙起始状态的。打印前 8 格
            // 藏住了真正的罪魁，它们在谱面更深处。
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[FloorTransform] ").Append(phase).Append(" ");
            int mismatched = 0;
            int shown = 0;
            for (int i = 0; i < editor.floors.Count; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null)
                {
                    continue;
                }

                Vector3 position = floor.transform.position;
                Vector3 scale = floor.transform.localScale;
                float rotation = floor.transform.rotation.eulerAngles.z;
                bool moved = (position - floor.startPos).sqrMagnitude > 0.0001f
                    || Mathf.Abs(Mathf.DeltaAngle(rotation, floor.startRot.z)) > 0.05f
                    || Mathf.Abs(scale.x - floor.startScale.x) > 0.001f
                    || Mathf.Abs(floor.opacity - floor.opacityVal) > 0.01f;
                if (!moved)
                {
                    continue;
                }

                mismatched++;
                if (shown < 6)
                {
                    shown++;
                    builder.Append("f").Append(i)
                        .Append("(pos=").Append(position.x.ToString("0.00"))
                        .Append(",").Append(position.y.ToString("0.00"))
                        .Append(" want=").Append(floor.startPos.x.ToString("0.00"))
                        .Append(",").Append(floor.startPos.y.ToString("0.00"))
                        .Append(" rot=").Append(rotation.ToString("0.0"))
                        .Append(" wantRot=").Append(floor.startRot.z.ToString("0.0"))
                        .Append(" op=").Append(floor.opacity.ToString("0.00"))
                        .Append(" wantOp=").Append(floor.opacityVal.ToString("0.00"))
                        .Append(") ");
                }
            }
            builder.Append("mismatched=").Append(mismatched)
                .Append(" total=").Append(editor.floors.Count).Append('.');
            Main.Logger.Log(builder.ToString());
        }

        /// <summary>
        /// 只读诊断。原生播放期间它周期性比较
        /// 场景时钟和音频片段位置，这样音画漂移能
        /// 以数字呈现，而不是「感觉不同步」。
        /// </summary>
        private static void LogAudioSync()
        {
            if (!VerboseDiagnostics) { return; }
            if (Time.frameCount - lastAudioSyncDiagnosticFrame < 30)
            {
                return;
            }
            lastAudioSyncDiagnosticFrame = Time.frameCount;

            scrConductor conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return;
            }

            double sceneTime = conductor.songposition_minusi;
            double songTime = conductor.song.time;
            double pitch = Math.Max(0.001d, conductor.song.pitch);
            double countdownTime = GetSeparateCountdownTime(conductor);
            // 当前场景时间下歌曲应该在哪，用的是
            // 和原生 conductor 相同的片段时间轴。
            double expectedClip = sceneTime + conductor.addoffset - countdownTime;
            // 同上：`song.time` 已是片段位置，乘 pitch 会得到 pitch² 的假漂移。
            double drift = songTime - expectedClip;

            Main.Logger.Log("[AudioSync] scene=" + sceneTime.ToString("0.000")
                + " songTime=" + songTime.ToString("0.000")
                + " expectedClip=" + expectedClip.ToString("0.000")
                + " drift=" + drift.ToString("0.000")
                + " playing=" + conductor.song.isPlaying
                + " hasSongStarted=" + conductor.hasSongStarted
                + " dspTimeSong=" + conductor.dspTimeSong.ToString("0.000")
                + " dsp=" + AudioSettings.dspTime.ToString("0.000")
                + LogHitSoundCursor(conductor)
                + LogBallPhase(conductor, sceneTime)
                + CountMismatchedFloors()
                + LogCameraTrace() + ".");
        }

        /// <summary>
        /// 追踪播放期间的两种相机机制。拖动预览直接写
        /// scrCamera.transform，而 MoveCamera 特效驱动 camParent。
        /// 如果向后 seek 之后 camParent 留着过期偏移，跟随
        /// 相机和 MoveCamera 特效就会打架，视图看起来像冻住了。
        /// </summary>
        private static string LogCameraTrace()
        {
            scrCamera camera = scrCamera.instance;
            scnEditor editor = scnEditor.instance;
            if (camera == null)
            {
                return " cam=n/a";
            }

            Transform camParent = editor == null || editor.customLevel == null
                ? camera.transform.parent
                : editor.customLevel.camParent;
            Vector2 local = camera.transform.localPosition;
            Vector2 world = camera.transform.position;

            return " camParent=" + (camParent == null
                    ? "null"
                    : camParent.position.x.ToString("0.00") + "," + camParent.position.y.ToString("0.00"))
                + " camLocal=" + local.x.ToString("0.00") + "," + local.y.ToString("0.00")
                + " camWorld=" + world.x.ToString("0.00") + "," + world.y.ToString("0.00")
                + " follow=" + camera.followMode
                + " lastMode=" + camera.lastUsedMovementType
                + " lastTileFloor=" + camera.lastTileCamFloor;
        }

        /// <summary>
        /// 统计播放期间实时变换/不透明度偏离已烘焙
        /// 起始状态的格。恢复时刻的检查一直是干净的，所以
        /// 异常一定是只在时间推进之后才出现的东西。
        /// </summary>
        private static string CountMismatchedFloors()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null)
            {
                return " floorMismatch=n/a";
            }

            int mismatched = 0;
            int first = -1;
            int last = -1;
            for (int i = 0; i < editor.floors.Count; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null)
                {
                    continue;
                }

                Vector3 position = floor.transform.position;
                Vector3 scale = floor.transform.localScale;
                float rotation = floor.transform.rotation.eulerAngles.z;
                bool moved = (position - floor.startPos).sqrMagnitude > 0.0001f
                    || Mathf.Abs(Mathf.DeltaAngle(rotation, floor.startRot.z)) > 0.05f
                    || Mathf.Abs(scale.x - floor.startScale.x) > 0.001f
                    || Mathf.Abs(floor.opacity - floor.opacityVal) > 0.01f;
                if (!moved)
                {
                    continue;
                }

                mismatched++;
                if (first < 0)
                {
                    first = i;
                }
                last = i;
            }

            return " floorMismatch=" + mismatched
                + "/" + editor.floors.Count
                + " range=" + first + "-" + last;
        }

        /// <summary>
        /// 报告每个行星的角度以及它所隐含的场景时间。
        /// Update_RefreshAngles 算的 angle = snapped + (scene - lastHit)/crotchet
        /// * pi * speed * dir，所以隐含时间（lastHit + (angle - snapped) *
        /// crotchet / (pi * speed * dir)）必须等于场景时钟。有差就意味着
        /// 球跑在音乐前面或后面。
        /// </summary>
        private static string LogBallPhase(scrConductor conductor, double sceneTime)
        {
            scrPlayerManager players = scrPlayerManager.instance;
            if (players == null || players.players == null)
            {
                return " ball=n/a";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < players.players.Length; i++)
            {
                scrPlayer player = players.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                scrPlanet chosen = player.planetarySystem.chosenPlanet;
                if (chosen == null)
                {
                    continue;
                }

                double snapped = PlanetSnappedLastAngleProperty == null
                    ? 0d
                    : Convert.ToDouble(PlanetSnappedLastAngleProperty.GetValue(chosen, null));
                double speed = player.planetarySystem.speed;
                double crotchet = conductor.crotchetAtStart;
                double dir = player.planetarySystem.isCW ? 1d : -1d;
                double implied = double.NaN;
                if (crotchet > 0.000001d && speed > 0.000001d)
                {
                    implied = player.lastHit
                        + (chosen.angle - snapped) * crotchet / (Math.PI * speed * dir);
                }

                // gap 只检查当前格内的相位。一个整整领先一格的球
                // 相位是对的，仍然读到 gap=0，所以还要把时钟
                // 隐含的格和球实际站着的格做比较。floorGap 的单位是
                // 格；正值表示球领先时钟。
                int ballSeq = chosen.currfloor == null ? -1 : chosen.currfloor.seqID;
                int clockSeq = -1;
                scrLevelMaker lm = scrLevelMaker.instance;
                if (lm != null && lm.listFloors != null)
                {
                    List<scrFloor> list = lm.listFloors;
                    for (int f = 0; f < list.Count; f++)
                    {
                        if (list[f] != null && list[f].entryTime <= sceneTime)
                        {
                            clockSeq = f;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                builder.Append(" p").Append(i)
                    .Append("(angle=").Append(chosen.angle.ToString("0.000"))
                    .Append(",lastHit=").Append(player.lastHit.ToString("0.000"))
                    .Append(",implied=").Append(implied.ToString("0.000"))
                    .Append(",gap=").Append((implied - sceneTime).ToString("0.000"))
                    .Append(",seq=").Append(ballSeq)
                    .Append(",clockSeq=").Append(clockSeq)
                    .Append(",floorGap=").Append(ballSeq - clockSeq)
                    .Append(")");
            }
            return " ball=" + builder.ToString().Trim();
        }

        /// <summary>
        /// 报告下一个已排程打击音时间相对实时 dsp 时钟的位置。
        /// 正好排在 dspTime 的打击音现在就会响；这个差值告诉
        /// 我们拍点音是不是锚在和音乐同一个时钟上。
        /// </summary>
        private static string LogHitSoundCursor(scrConductor conductor)
        {
            if (ConductorHitSoundsDataField == null || ConductorNextHitSoundField == null)
            {
                return " hitCursor=unavailable";
            }

            System.Collections.IList sounds =
                ConductorHitSoundsDataField.GetValue(conductor) as System.Collections.IList;
            int nextIndex = Convert.ToInt32(ConductorNextHitSoundField.GetValue(conductor));
            if (sounds == null || sounds.Count == 0)
            {
                return " hitSounds=0";
            }

            if (nextIndex >= sounds.Count)
            {
                return " hitSounds=" + sounds.Count + " hitCursor=end";
            }

            object next = sounds[nextIndex];
            if (next == null)
            {
                return " hitSounds=" + sounds.Count + " hitCursor=null";
            }

            FieldInfo timeField = next.GetType().GetField("time");
            if (timeField == null)
            {
                return " hitSounds=" + sounds.Count + " hitCursor=noTimeField";
            }

            double scheduled = Convert.ToDouble(timeField.GetValue(next));
            double lead = scheduled - AudioSettings.dspTime;
            return " hitSounds=" + sounds.Count
                + " hitIdx=" + nextIndex
                + " hitLead=" + lead.ToString("0.000");
        }

        private static void LogPlanetState(string phase, float targetTime)
        {
            if (!VerboseDiagnostics) { return; }
            scrPlayerManager players = scrPlayerManager.instance;
            if (players == null || players.players == null)
            {
                Main.Logger.Log("[PlanetState] " + phase + " players unavailable.");
                return;
            }

            for (int i = 0; i < players.players.Length; i++)
            {
                scrPlayer player = players.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                scrPlanet chosen = player.planetarySystem.chosenPlanet;
                scrFloor curr = chosen == null ? null : chosen.currfloor;
                string floorInfo = curr == null
                    ? "null"
                    : curr.seqID + "/rot=" + curr.transform.rotation.eulerAngles.z.ToString("0.00")
                        + "/startRot=" + curr.startRot.z.ToString("0.00")
                        + "/stick=" + (curr.stickToFloor ? 1 : 0)
                        + "/entry=" + curr.entryangle.ToString("0.000")
                        + "/exit=" + curr.exitangle.ToString("0.000")
                        + "/angleLen=" + curr.angleLength.ToString("0.000");

                double snapped = PlanetSnappedLastAngleProperty == null || chosen == null
                    ? 0d
                    : Convert.ToDouble(PlanetSnappedLastAngleProperty.GetValue(chosen, null));
                double targetExit = PlanetTargetExitAngleProperty == null || chosen == null
                    ? 0d
                    : Convert.ToDouble(PlanetTargetExitAngleProperty.GetValue(chosen, null));

                Main.Logger.Log("[PlanetState] " + phase
                    + " target=" + FormatTimelineTime(targetTime)
                    + " p" + i
                    + " chosen=" + (chosen == null ? "null" : chosen.name)
                    + " angle=" + (chosen == null ? 0d : chosen.angle).ToString("0.000")
                    + " snapped=" + snapped.ToString("0.000")
                    + " targetExit=" + targetExit.ToString("0.000")
                    + " lastHit=" + player.lastHit.ToString("0.000")
                    + " currFloor=" + floorInfo
                    + " isCW=" + player.planetarySystem.isCW
                    + " speed=" + player.planetarySystem.speed.ToString("0.000")
                    + " rotEase=" + (scrController.instance == null
                        ? "n/a" : scrController.instance.rotationEase.ToString()) + ".");
            }
        }

        /// <summary>
        /// 针对「拖回起点后相机冻住 / 轨道没恢复」的
        /// 只读诊断。scrVfxPlus 通过只前进的
        /// currentVfxIndex 触发特效，每个特效触发后就从队列移除，
        /// 所以被前一次播放消费掉的队列无法在第二遍
        /// 重新发射。报告游标、剩余队列，以及
        /// 剩余特效里有多少已经标记为已触发。
        /// </summary>
        private static void LogVfxQueueState(string phase)
        {
            if (!VerboseDiagnostics) { return; }
            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx == null)
            {
                Main.Logger.Log("[VfxQueue] " + phase + " vfx=null.");
                return;
            }

            int cursor = VfxCurrentIndexField == null
                ? -1
                : Convert.ToInt32(VfxCurrentIndexField.GetValue(vfx));
            int count = vfx.effects == null ? 0 : vfx.effects.Count;
            int triggered = 0;
            for (int i = 0; i < count; i++)
            {
                ffxPlusBase effect = vfx.effects[i];
                if (effect != null && effect.triggered)
                {
                    triggered++;
                }
            }

            // plusEffects 是 PrepVfx 取队列的池。单独报告它，
            // 能把「队列从没被重新填充」和「它取料的池
            // 是空的」区分开——两种失败都读成 count=0。
            int pooled = 0;
            scnEditor pooledEditor = scnEditor.instance;
            if (pooledEditor != null && pooledEditor.floors != null)
            {
                for (int i = 0; i < pooledEditor.floors.Count; i++)
                {
                    scrFloor pooledFloor = pooledEditor.floors[i];
                    if (pooledFloor != null && pooledFloor.plusEffects != null)
                    {
                        pooled += pooledFloor.plusEffects.Count;
                    }
                }
            }

            Main.Logger.Log("[VfxQueue] " + phase
                + " count=" + count
                + " pooled=" + pooled
                + " cursor=" + cursor
                + " triggered=" + triggered
                + " remaining=" + Math.Max(0, count - Math.Max(0, cursor))
                + " pausedTweens=" + (vfx.pausedTweens == null ? 0 : vfx.pausedTweens.Count)
                + " hasPlayed=" + vfx.hasPlayed + ".");
        }

        /// <summary>
        /// 杀掉动画特效启动的逐格 tween。
        ///
        /// ffxFloorAppearPlus / ffxFloorDisappearPlus 用普通（非
        /// 独立）DOTween tween 驱动格，直接写 transform、opacity 和 tweenRot。
        /// 拖动定位会把 Time.timeScale 设为 0，那只是暂停它们，所以
        /// 恢复时它们从停下的地方继续跑，轨道继续
        /// 掉出视野，而不是被还原。原生
        /// ResetScene 会杀掉同样的 tween（scnGame.cs，DOTween.PlayingTweens 循环）
        /// 再重建；这里只对格通道做等价的事，
        /// 让求值器保持是格状态的唯一拥有者。
        /// </summary>
        private static void KillFloorAnimationTweens()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null)
            {
                return;
            }

            int killed = 0;
            for (int i = 0; i < editor.floors.Count; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null || floor.moveTweens == null)
                {
                    continue;
                }

                foreach (Tween tween in floor.moveTweens.Values)
                {
                    if (tween != null && tween.active)
                    {
                        tween.Kill(false);
                        killed++;
                    }
                }
                floor.moveTweens.Clear();
            }

            if (VerboseDiagnostics)
            {
                Main.Logger.Log("[VfxQueue] killed " + killed + " floor animation tweens.");
            }
        }

        private static void CancelPendingConductorMusicStart(scrConductor conductor)
        {
            if (conductor == null)
            {
                return;
            }

            StopConductorCoroutine(conductor, ConductorStartMusicCoroutineField);
            StopConductorCoroutine(conductor, ConductorToggleHasSongStartedCoroutineField);
        }

        private static void StopConductorCoroutine(scrConductor conductor, FieldInfo coroutineField)
        {
            if (conductor == null || coroutineField == null)
            {
                return;
            }

            Coroutine coroutine = coroutineField.GetValue(conductor) as Coroutine;
            if (coroutine == null)
            {
                return;
            }

            conductor.StopCoroutine(coroutine);
            coroutineField.SetValue(conductor, null);
        }

        private static double GetSeparateCountdownTime(scrConductor conductor)
        {
            if (conductor == null || !conductor.separateCountdownTime)
            {
                return 0d;
            }
            return Math.Max(0d,
                conductor.crotchetAtStart * (double)conductor.adjustedCountdownTicks);
        }

        private static void AlignConductorBeatCursor(scrConductor conductor, float targetTime)
        {
            double crotchet = conductor.crotchetAtStart;
            if (crotchet <= 0.000001d)
            {
                crotchet = conductor.bpm <= 0f ? 0.5d : 60d / conductor.bpm;
            }

            double beatIndex = Math.Max(0d, Math.Ceiling(targetTime / crotchet - 0.000001d));
            conductor.beatNumber = (int)Math.Min(int.MaxValue, beatIndex);
            if (ConductorNextBeatTimeField != null)
            {
                ConductorNextBeatTimeField.SetValue(conductor, beatIndex * crotchet);
            }

            int crotchetsPerBar = 8;
            if (ConductorCrotchetsPerBarField != null)
            {
                crotchetsPerBar = Math.Max(1, (int)ConductorCrotchetsPerBarField.GetValue(conductor));
            }
            double barDuration = crotchet * crotchetsPerBar;
            double barIndex = Math.Max(0d, Math.Ceiling(targetTime / barDuration - 0.000001d));
            conductor.barNumber = (int)Math.Min(int.MaxValue, barIndex);
            if (ConductorNextBarTimeField != null)
            {
                ConductorNextBarTimeField.SetValue(conductor, barIndex * barDuration);
            }
        }

        private static void SetLogicalConductorTime(scrConductor conductor, float targetTime)
        {
            if (conductor == null)
            {
                return;
            }

            conductor.songposition_minusi = Mathf.Max(0f, targetTime);
            conductor.deltaSongPos = 0d;
        }

        private static float ReadConductorTime()
        {
            scrConductor conductor = scrConductor.instance;
            return conductor == null
                ? 0f
                : Mathf.Max(0f, Convert.ToSingle(conductor.songposition_minusi));
        }

        private static void ApplyPausedState(
            scnEditor editor,
            scrController controller,
            scrConductor conductor)
        {
            if (editor != null)
            {
                if (!editor.pausedInPlayMode)
                {
                    editor.pausedInPlayMode = true;
                }
            }
            if (controller != null)
            {
                if (!controller.paused)
                {
                    pauseTransitionPending = true;
                }
                if (pauseTransitionPending
                    && TryApplyNativePause(controller, true, pauseReapplyRequested))
                {
                    pauseTransitionPending = false;
                    pauseReapplyRequested = false;
                }
                if (pauseTransitionPending && controller.enabled)
                {
                    controller.enabled = false;
                }
            }

            Time.timeScale = 0f;
            AudioListener.pause = true;

            if (nativeTransportHeld)
            {
                if (conductor != null)
                {
                    conductor.enabled = !pauseTransitionPending;
                }
                return;
            }

            if (conductor == null || float.IsNaN(heldPreviewTime))
            {
                if (conductor != null && conductor.enabled)
                {
                    conductor.enabled = false;
                }
                return;
            }

            double pitch = conductor.song == null ? 1d : Math.Max(0.001d, conductor.song.pitch);
            double currentDspTime = AudioSettings.dspTime;
            conductor.dspTime = currentDspTime;
            conductor.dspTimeSong = currentDspTime
                - heldPreviewTime / pitch
                - conductor.addoffset / pitch
                - scrConductor.calibration_i;
            conductor.songposition_minusi = heldPreviewTime;
            conductor.deltaSongPos = 0d;
            if (conductor.enabled)
            {
                conductor.enabled = false;
            }
        }

        private static void ApplyStaticPreviewState()
        {
            scnEditor editor = scnEditor.instance;
            scrController controller = scrController.instance;
            scrConductor conductor = scrConductor.instance;

            if (editor != null)
            {
                editor.pausedInPlayMode = true;
            }

            FreezeStaticPreviewNativeUpdates();

            if (controller != null)
            {
                controller.enabled = false;
            }

            if (conductor != null)
            {
                conductor.enabled = false;
                if (!float.IsNaN(heldPreviewTime))
                {
                    SetLogicalConductorTime(conductor, heldPreviewTime);
                }
            }

            // 保持编辑器响应。静态后端拥有场景
            // 状态，所以不能用 Unity 的缩放时钟去推进它。
            Time.timeScale = 1f;
            AudioListener.pause = true;

            ClearStuckCountdownText();
        }

        /// <summary>
        /// 工作台掌控预览期间清掉倒计时标签。
        ///
        /// scrCountdown 在倒计时结束时显示 "GO"，然后由它自己 Update 里的
        /// 一个计时器清除它：
        ///
        ///     if (controller.goShown) { text = "GO"; timeGoTween = crotchet; }
        ///     if (isLevelEditor && editor.inStrictlyEditingMode) return;   // <-- 就是这里
        ///     if (timeGoTween > 0f) { timeGoTween -= unscaledDeltaTime; ... CancelGo(); }
        ///
        /// 严格编辑模式的那次提前 return 夹在两者之间，所以工作台
        /// 开着时（正是那个模式）计时器从不运行，标签
        /// 就一直停在 "GO"，只要会话还在。预览期间没有别的东西清它
        /// ——唯一的其他调用方是场景重置。
        ///
        /// 直接清空标签，而不是禁用组件，这样游戏
        /// 仍然拥有它，工作台一关就恢复自己的行为。
        /// </summary>
        /// <summary>
        /// 丢掉游戏在编辑器里开始播放时升起的黑色切换面板。
        /// editor.
        ///
        /// scrController.Start_Rewind 只要不是游戏世界就会调用 SetToBlack：
        ///
        ///     if (!gameworld &amp;&amp; scrUIController.instance != null) SetToBlack();
        ///
        /// 而再次降下它的调用坐在 OnMusicScheduled 里，条件
        /// `GCS.checkpointNum != 0`，从第一格开始玩关卡时它永不为真。面板
        /// 于是整场运行都立着——一块黑屏，
        /// 后面是关卡在播放。
        ///
        /// 和卡住的倒计时标签同一个形态：游戏自己播放路径上
        /// 升起的原生 UI，编辑器路线从不把它降下来。
        /// </summary>
        private static void ClearNativeTransitionPanel()
        {
            try
            {
                scrUIController uiController = scrUIController.instance;
                if (uiController == null || uiController.transitionPanel == null)
                {
                    return;
                }

                if (uiController.transitionPanel.gameObject.activeSelf)
                {
                    uiController.SetToTransparent();
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[NativeHandoff] could not clear the transition panel: "
                    + exception.Message);
            }
        }

        private static void ClearStuckCountdownText()
        {
            try
            {
                scrUIController uiController = scrUIController.instance;
                if (uiController == null || uiController.txtCountdown == null)
                {
                    return;
                }

                Text label = uiController.txtCountdown;
                if (label.text != null && label.text.Length != 0)
                {
                    label.text = string.Empty;
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview] could not clear the countdown label: "
                    + exception.Message);
            }
        }

        private static void ApplyPlayingState(scnEditor editor, scrController controller)
        {
            if (editor == null || controller == null)
            {
                return;
            }

            RestoreStaticPreviewNativeUpdates();

            if (editor.pausedInPlayMode)
            {
                editor.pausedInPlayMode = false;
            }
            if (!TryApplyNativePause(controller, false, false))
            {
                Time.timeScale = 0f;
                AudioListener.pause = true;
                return;
            }
            scrConductor conductor = scrConductor.instance;
            if (conductor != null)
            {
                // SwitchToEditMode 会停用 conductor 整个 GameObject：
                //
                //     ADOBase.conductor.KillAllSounds();
                //     ADOBase.conductor.gameObject.SetActive(false);
                //
                // 而非激活 GameObject 上的组件不跑 Start、不跑 Update，
                // 连协程都排不进去——Unity 会记日志 "Coroutine couldn't be
                // started because the game object 'Conductor' is inactive"。只有
                // scnEditor.Play 会把它重新打开，所以每一次不经过
                // Play 的恢复都必须在这里做；只设 `enabled` 会让它保持惰性。
                if (!conductor.gameObject.activeSelf)
                {
                    conductor.gameObject.SetActive(true);
                }
                conductor.enabled = true;
            }
            controller.enabled = true;
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        private static void FreezeStaticPreviewNativeUpdates()
        {
            scrCamera camera = scrCamera.instance;
            if (camera != null)
            {
                if (!staticCameraStateCaptured)
                {
                    staticCameraStateCaptured = true;
                    staticCameraWasEnabled = camera.enabled;
                    staticCameraSkipUpdate = camera.skipUpdate;
                    Main.Logger.Log("[StaticPreview] captured camera state enabled="
                        + staticCameraWasEnabled + " skipUpdate=" + staticCameraSkipUpdate + ".");
                }

                // 静态求值器同时拥有 transform 和 zoom。原生
                // scrCamera Update/LateUpdate 不能在指针帧之间与它竞争。
                camera.skipUpdate = true;
                camera.enabled = false;
            }

            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null
                    || player.planetarySystem.planetList == null)
                {
                    continue;
                }

                for (int j = 0; j < player.planetarySystem.planetList.Count; j++)
                {
                    scrPlanet planet = player.planetarySystem.planetList[j];
                    if (planet != null && planet.enabled)
                    {
                        if (!staticPreviewDisabledPlanets.Contains(planet))
                        {
                            staticPreviewDisabledPlanets.Add(planet);
                        }
                        planet.enabled = false;
                    }
                }
            }
        }

        private static void RestoreStaticPreviewNativeUpdates()
        {
            for (int i = 0; i < staticPreviewDisabledPlanets.Count; i++)
            {
                scrPlanet planet = staticPreviewDisabledPlanets[i];
                if (planet != null)
                {
                    planet.enabled = true;
                }
            }
            staticPreviewDisabledPlanets.Clear();

            if (staticCameraStateCaptured)
            {
                scrCamera camera = scrCamera.instance;
                if (camera != null)
                {
                    camera.enabled = staticCameraWasEnabled;
                    camera.skipUpdate = staticCameraSkipUpdate;
                }
                staticCameraStateCaptured = false;
                Main.Logger.Log("[StaticPreview] restored native camera and planet updates.");
            }
        }

        /// <summary>
        /// 把编辑器场景交给求值器：原生相机和行星停止
        /// 更新，这样没有东西会和求值出的值竞争，而 Time.timeScale 保持
        /// 为 1，让工作台 UI 继续接受输入。这是进入播放模式
        /// 的反操作。游戏运行时不启动，所以没有倒计时，
        /// 也没有要藏的游戏 UI。
        /// </summary>
        private static void BeginEvaluatorOwnedPreview(scnEditor editor)
        {
            if (editor != null)
            {
                editor.pausedInPlayMode = true;
            }

            FreezeStaticPreviewNativeUpdates();

            scrController controller = scrController.instance;
            if (controller != null)
            {
                controller.enabled = false;
            }

            scrConductor conductor = scrConductor.instance;
            if (conductor != null)
            {
                // conductor 即使在停止时也保持启用。它的 Update 既
                // 推进歌曲时钟，又触发已排队的逐格打击音
                // （scrConductor.Update: "while (nextHitSoundToSchedule < ...)"），所以
                // disabling it would silence the踩踏声 the preview is meant to have.
                conductor.enabled = true;
            }

            // 求值器是时钟的纯函数，所以不能靠冻结场景来
            // 保持一帧。Time.timeScale = 0 还会让工作台
            // UI 收不到输入，这就是旧路径只能暂停
            // 而不能停止的原因。
            Time.timeScale = 1f;
            AudioListener.pause = true;

            PrepareFloorTrackColours();
        }

        /// <summary>
        /// 套用每个格已烘焙的轨道颜色动画。
        ///
        /// scnGame 会给每个格挂一个 ffxChangeTrack，并填好它的颜色、
        /// 类型、脉冲和时长，但该组件只在 PrepFloor 里
        /// 才把这份配置复制到格上，而 PrepFloor 从 FinishCustomLevelLoading 运行——
        /// 那是 scnGame.Play 的一步。所以不进播放模式就进预览，会留下
        /// 每一格都是朴素颜色、没有 Glow/Pulse 动画，本该闪动的
        /// 轨道一直是平的。
        ///
        /// remakeFloors 刻意传 false：true 那条路还会加上出现和
        /// 消失组件，而第二次加上它们，正是轨道在拖动定位之后一直
        /// 掉出视野的原因。
        /// </summary>
        private static void PrepareFloorTrackColours()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null)
            {
                return;
            }

            // PrepFloor 会把动画时长除以歌曲 pitch，所以没有
            // 加载音频的关卡没有什么可据以准备。
            scrConductor conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return;
            }

            int prepared = 0;
            for (int i = 0; i < editor.floors.Count; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null)
                {
                    continue;
                }

                ffxChangeTrack changeTrack = floor.GetComponent<ffxChangeTrack>();
                if (changeTrack == null)
                {
                    continue;
                }

                changeTrack.PrepFloor(false);
                prepared++;
            }

            Main.Logger.Log("[EvaluatorOwned] prepared track colour on "
                + prepared + "/" + editor.floors.Count + " floors.");
        }

        private static void EndEvaluatorOwnedPreview()
        {
            RestoreStaticPreviewNativeUpdates();

            scrController controller = scrController.instance;
            if (controller != null)
            {
                controller.enabled = true;
            }

            scrConductor conductor = scrConductor.instance;
            if (conductor != null)
            {
                conductor.enabled = true;
            }

            Time.timeScale = 1f;
            AudioListener.pause = false;

            // 把编辑器这一味的烘焙装回去。
            //
            // 会话期间用的是 paused: false（播放态），它会丢掉 PositionTrack
            // 的仅限编辑器偏移。预览结束、画面交还给编辑器之后，那些偏移
            // 必须重新显示——它们本来就是给编辑器看的。
            //
            // 放在最后：ApplyEventsToFloors 依赖场景已经回到正常时序，
            // 而上面那几行正是把时序恢复回去的动作。
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                RebakeFloors(editor, true);
            }
        }

        /// <summary>
        /// 开始一次求值器接管的拖动定位会话。刻意不碰播放
        /// 模式：求值器渲染编辑器自己的场景，那个场景已经持有
        /// 它要写的格、装饰物和相机。
        /// </summary>
        /// <summary>
        /// 如果求值器接管的会话还没跑起来，就把它拉起来。
        /// 刻意与开始一次拖动分开：会话比任何单次拖动活得久，
        /// 而拖动旗标是空格处理器用来拒绝拖动中途切换的依据。
        /// </summary>
        private static void EnsureEvaluatorSession()
        {
            EnsureEvaluatorSession(false);
        }

        /// <summary>
        /// 如果求值器接管的会话还没跑起来，就把它拉起来。
        ///
        /// preserveTransport 供重建路径使用：一个因为关卡变了
        /// 而被重新分析的会话，绝不能移动播放头或停止播放，
        /// 而从零开始的会话应该从头打开。
        /// </summary>
        private static void EnsureEvaluatorSession(bool preserveTransport)
        {
            if (evaluatorOwnedActive)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            string error;
            // 必须排在 TryBegin 之前：时间轴上下文会快照每一格的静态
            // 状态，所以之后再烘焙，要到一次重建才会传到求值器。
            //
            // 用「播放」这一味烘焙，不是编辑器那一味。
            //
            // 求值器画的是一段播放中的画面，而原生 ApplyEventsToFloors
            // 在播放时会把 PositionTrack 的仅限编辑器偏移丢掉：
            //
            //   if ((!editorOnly || !scrController.instance.paused) && editorOnly) break;
            //
            // 也就是 paused 清掉时，editorOnly 的偏移被跳过。用 paused: true
            // 烘焙会把那些偏移留在格的 offsetPos 里，而快照正是从
            // startPos + offsetPos 取基线的——于是预览里能看见本该只在
            // 编辑器里出现的位移。
            //
            // 编辑器态在会话结束时补回来（见 EndEvaluatorOwnedPreview），
            // 所以退出预览后偏移照常显示。
            RebakeFloors(editor, false);
            if (!evaluatorBackend.TryBegin(editor, out error))
            {
                Main.Logger.Log("[EvaluatorOwned] evaluator unavailable: " + error);
                return;
            }

            float resumeTime = previewClockTime;
            bool resumeRunning = previewClockRunning;

            BeginEvaluatorOwnedPreview(editor);
            if (preserveTransport)
            {
                previewClockTime = resumeTime;
                previewClockRunning = resumeRunning;
            }
            else
            {
                previewClockTime = 0f;
                previewClockRunning = false;
            }

            // 重建必须把音频一起带回来。BeginEvaluatorOwnedPreview
            // 会暂停监听器，而重建发生在一个已经在播放的预览
            // 底下，所以只恢复运行旗标，会让时钟声称自己在跑
            // 而歌曲仍然静音——songposition_minusi 从不推进，
            // 画面停住直到播放头被拖动。在这里重启，
            // 才让重建对播放不可见。
            if (previewClockRunning)
            {
                StartEvaluatorPreviewAudio(previewClockTime);
            }

            previewClockLastUnscaledTime = Time.unscaledTime;
            evaluatorOwnedActive = true;
            Main.Logger.Log("[EvaluatorOwned] session started; domains="
                + evaluatorBackend.ActiveDomainCount + "/" + evaluatorBackend.DomainCount + ".");
        }

        /// <summary>
        /// 按游戏在指定 transport 状态下的方式烘焙格，
        /// 并重新锚定装饰物以匹配。
        ///
        /// 谱面的仅限编辑器 PositionTrack 偏移只在
        /// scrController.paused 置位时累积：
        ///
        ///     if ((!editorOnly || !paused) &amp;&amp; editorOnly) break;
        ///
        /// 原版在两端用同样方式解决：scnEditor.Play 走
        /// scnGame.Play，后者清掉 paused，并在编辑器里运行
        /// FinishCustomLevelLoading，因此它的 ApplyEventsToFloors 在播放期间
        /// 丢掉这些偏移；停止时又装回编辑器的烘焙。
        /// 预览通过在 transport 上重新烘焙来重现这一点，而不是试图
        /// 同时持有两个答案。
        ///
        /// paused 写到后备字段而不是通过属性，因为它的
        /// setter 还会重映射输入。
        /// </summary>
        private static void RebakeFloors(scnEditor editor, bool paused)
        {
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return;
            }

            scnGame level = scnGame.instance;
            if (level == null || level.levelData == null)
            {
                return;
            }

            scrController controller = scrController.instance;
            if (controller == null || ControllerPausedField == null)
            {
                return;
            }

            bool saved = controller.paused;
            try
            {
                if (saved != paused)
                {
                    ControllerPausedField.SetValue(controller, paused);
                }

                level.ApplyEventsToFloors(editor.floors);
            }
            catch (Exception exception)
            {
                ReportError("Failed to re-bake the floor state", exception);
            }
            finally
            {
                if (saved != paused)
                {
                    ControllerPausedField.SetValue(controller, saved);
                }
            }

            try
            {
                scrDecorationManager decorationManager = scrDecorationManager.instance;
                if (decorationManager != null && decorationManager.allDecorations != null)
                {
                    // 装饰物把它所依的格位置缓存在
                    // startPos 里，而 UpdatePosition 会从中减去实时格变换，
                    // 所以一次移动了格却没有重新锚定的烘焙，会让每个
                    // 锚定到砖的装饰物偏移掉移动的距离。这是
                    // 原生关卡加载所用的顺序。
                    decorationManager.FinishUpdateDecorations();
                }
            }
            catch (Exception exception)
            {
                ReportError("Failed to re-anchor the decorations", exception);
            }
        }

        // scrController.paused 的 setter 还会重映射输入，所以烘焙强制设的值
        // 直接写到后备字段。
        private static readonly FieldInfo ControllerPausedField =
            typeof(scrController).GetField(
                "_paused", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static void EndEvaluatorOwnedScrub()
        {
            previewClockRunning = false;
            evaluatorBackend.End();
            EndEvaluatorOwnedPreview();
            Main.Logger.Log("[EvaluatorOwned] scrub ended.");
        }

        /// <summary>
        /// 请求在下一个 tick 重建求值器的快照。
        ///
        /// 快照为每个格、事件和装饰物缓存一个引用，所以任何增删
        /// 这些的编辑都会让它失效——TryEvaluate 通过
        /// MatchesCurrentEditor 检测到这一点并拒绝运行。不过没有东西会自己重建它：
        /// 会话活着时 EnsureEvaluatorSession 直接返回，而唯一
        /// 清掉那个旗标的路径是时间轴拖动定位。所以一个在时间轴拖动之外
        /// 编辑关卡的玩法（从媒体面板拖入一个装饰物）会让
        /// 预览永久冻住，而编辑本身被正确保存了。
        ///
        /// 时钟和播放状态保留：只销毁缓存的快照，
        /// 下一次 EnsureEvaluatorSession 调用会重新分析每个域。
        /// </summary>
        internal static void RequestEvaluatorRebuild()
        {
            RequestEvaluatorRebuild("unspecified");
        }

        internal static void RequestEvaluatorRebuild(string reason)
        {
            if (!evaluatorOwnedActive)
            {
                // 没什么可重建；下一次 EnsureEvaluatorSession 会重新分析。
                return;
            }

            // End() 会销毁各域的状态；之前活着的东西会在下面重建。
            evaluatorBackend.End();
            evaluatorOwnedActive = false;
            evaluatorRebuildPending = true;
            Main.Logger.Log("[EvaluatorOwned] rebuild requested by " + reason + ".");
        }

        private static bool evaluatorRebuildPending;

        /// <summary>
        /// 求值器接管预览的逐帧驱动。时钟运行时，
        /// 时间是从 conductor 读回来的，而不是在这里累加：
        /// conductor 从 AudioSettings.dspTime 推导它，而在
        /// Time.unscaledTime 里累加会是第二个、缓慢漂移的时钟，喂给同一个
        /// 画面。停止时，时间就保持在它被留下的地方。
        /// </summary>
        private static void TickEvaluatorOwnedPreview(RectTransform viewport, scnEditor editor)
        {
            // 性能探针（`[TickCost]` 那四段拆分）跟着 VerboseDiagnostics 走。
            // 2026-10-16 定位"原版 160 帧 / 工作台 39 帧"时临时置过 true，
            // 结论记在知识库的问题清单里。
            bool profile = VerboseDiagnostics;
            long t0 = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            // 诊断门控。**这一条原先没有门控**，于是对一个公开发布的 mod，
            // 玩家正常播放时每 60 帧就会同步往控制台写一条长字符串
            // （UMM 的 Logger 内部第一句就是 Console.WriteLine）。
            // 实测这一条单独占掉一整帧 176ms。
            if (VerboseDiagnostics && Time.frameCount - lastTrackCTrace > 60)
            {
                lastTrackCTrace = Time.frameCount;
                scrConductor traceConductor = scrConductor.instance;
                Main.Logger.Log("[TrackC] tick running=" + previewClockRunning
                    + " owned=" + evaluatorOwnedActive
                    + " active=" + evaluatorBackend.IsActive
                    + " time=" + FormatTimelineTime(previewClockTime)
                    + " songpos=" + (traceConductor == null
                        ? "?" : traceConductor.songposition_minusi.ToString("0.000"))
                    + " pending=" + audioStartPending
                    + " listening=" + !AudioListener.pause
                    + " tScale=" + Time.timeScale.ToString("0.##")
                    + TraceBallState() + ".");
            }

            if (previewClockRunning)
            {
                scrConductor conductor = scrConductor.instance;

                if (conductor != null && audioStartPending)
                {
                    // StartMusicCo 会把 dspTimeSong 回绕到 0，只有它的排程回调
                    // 才把真实值放回去，所以 songposition_minusi 在
                    // 那个回调落地之前没有意义——它读出来是某个无关的位置。
                    // 画面在那些帧里保持不动，而不是去画那个位置。
                    //
                    // 只等回调，绝不等片段变成可听。
                    // 片段被排程所依据的是 targetTime 加上
                    // 谱面自己的前奏，而那个前奏是真实内容：在
                    // waitingforyou 上，四个 tick 的 separateCountdownTime 会让声音
                    // 出现在它所属场景时间之后 2.04s。等那一刻
                    // 会让每次开始都把画面冻住两秒，读起来像空格
                    // 坏了。回调一跑完，音频时钟就有效了，
                    // 而那点前奏只是时间轴的一部分。
                    previewClockTime = audioStartTime;
                    audioStartPendingFrames++;
                    if (audioStartDsp > 0d)
                    {
                        // 回调落地了，时钟可以信了。**但 audioStartDsp 留着** ——
                        // 下一步还要用它把画面定到"音频真正开始"的那一刻，
                        // 否则画面会先跑掉 0.1 秒，那 0.1 秒的音乐又变成被剪掉。
                        audioStartPending = false;
                    }
                    else if (audioStartPendingFrames > MaxAudioStartHoldFrames)
                    {
                        // 回调始终没落地。别再等了，让正常路径
                        audioStartPending = false;
                        // 也别再拿它定画面了，否则会一直定在起播位置。
                        audioStartDsp = 0d;
                        if (Main.VerboseLog)
                        {
                            Main.Logger.Log("[AudioResume] gave up waiting for the schedule "
                                + "callback after " + audioStartPendingFrames + " frames.");
                        }
                    }
                    else if (!pendingAudioLogged)
                    {
                        pendingAudioLogged = true;
                        Main.Logger.Log("[AudioResume] holding at "
                            + audioStartTime.ToString("0.000")
                            + " until the schedule callback lands; nowDsp="
                            + AudioSettings.dspTime.ToString("0.000"));
                    }
                }
                else if (holdPictureForAudioStart
                    && audioStartDsp > 0d && AudioSettings.dspTime < audioStartDsp - 0.002d)
                {
                    // **音频排程还在未来：把画面定住等它。**
                    //
                    // 歌曲被排到 dsp+0.1（`MinSchedulingLeadSeconds`），而片段
                    // 位置现在是 target 的内容。这几帧里若让画面照常跑，画面就
                    // 比音乐快出这 0.1 秒 —— 要么把片段往后挪 0.1 秒（那就是
                    // 早期版本"音乐凭空少一截"的做法），要么让画面等。这里选等：
                    // 内容不剪、音画不偏，只是开头停一小下。
                    previewClockTime = audioStartTime;
                    audioStartHoldFrames++;
                    if (audioStartHoldFrames > MaxAudioStartHoldFrames)
                    {
                        // 排程时刻迟迟不到（设备异常），放弃等待。
                        audioStartDsp = 0d;
                        if (Main.VerboseLog)
                        {
                            Main.Logger.Log("[AudioStart] 等不到排程时刻，放弃定帧。");
                        }
                    }
                }
                else if (conductor != null)
                {
                    // 走到这里说明排程时刻已过（或本来就没有要等的），
                    // 把"定帧等音频"的标记清掉，恢复正常推进。
                    if (holdPictureForAudioStart && Main.VerboseLog)
                    {
                        // 一行日志，证明"定帧等音频"这条路真的走到过：
                        // 帧数 × 帧时长 ≈ 定帧时长（约 0.1 秒）。
                        Main.Logger.Log("[AudioStart] 定帧等待 " + audioStartHoldFrames
                            + " 帧后与音乐同时开始（片段从 target 内容起，不剪音乐）。");
                    }
                    audioStartDsp = 0d;
                    audioStartHoldFrames = 0;
                    holdPictureForAudioStart = false;

                    // 画面按帧时间推进，方式和原生相机一样，
                    // 并被拉向音频时钟，而不是从它读取。
                    //
                    // scrCamera.Update accumulates `timer += Time.deltaTime` and
                    // 并据此插值，所以它的进度按构造就是单调的。
                    // songposition_minusi 不能同样使用：它由
                    // AudioSettings.dspTime 推导，而后者只在 DSP 缓冲区被填充时才走动，
                    // 所以它会静一会儿再跳一下。在这里实测的
                    // 48kHz、2048 采样缓冲区下，那一跳是 42.7ms，所以球
                    // 跨过砖块入口时间的那一帧因此会上下一个整缓冲区的抖动。
                    // 打击音不会，因为它们按 dsp 时间排程得
                    // 采样级精确，所以两者以变化的量互相错开——这正是
                    // 「砖块有时落得准、有时不准」的来历。
                    //
                    // 时钟直接从 conductor 读，不在这里累加。
                    //
                    // 它过去按帧时间推进，然后每帧被拉向
                    // 音频方向的 8%。那是一阶滞后，其
                    // 稳态误差是 (帧时间 / 0.08)——在它运行的帧率下
                    // 约 75ms。刚 seek 之后误差为零，所以播放
                    // 开得是对的，然后在几帧内稳定到那个滞后上
                    // 并停在那儿，这正是「开头总是对得上，其余
                    // 都对不上」。
                    //
                    // songposition_minusi 是 dsp 时间的纯函数，也是片段排程所
                    // 依据的同一个锚点，所以跟着它能让画面和
                    // 歌曲在整个播放期间保持一致。
                    // 没音乐的谱面不能跟音频时钟。songposition_minusi 由
                    // `(dspTime - dspTimeSong - calibration) * pitch - addoffset`
                    // 推导，没有歌曲时那个锚点不存在，它恒为 0。
                    //
                    // 而下面那句「小步回退就保持」的容差只有 50ms，时钟
                    // 跑到几秒之后差值远超它，保护不成立，于是每帧都被
                    // 拉回 0——表现就是播放时画面（和时间线）不断闪回开头。
                    //
                    // 这类谱面本来就没有音频要同步，用帧时间自己推进即可，
                    // 这也是原生 scrCamera.Update 的做法（timer += deltaTime）。
                    if (!HasSongForClock)
                    {
                        float now = Time.unscaledTime;
                        float delta = now - previewClockLastUnscaledTime;
                        previewClockLastUnscaledTime = now;
                        // 单帧最多吃掉 0.05 秒。正常一帧约 6ms，卡一下
                        // 也不会超；而「暂停后没重置基准」那种整段暂停
                        // 时长会被这道限住，时钟不会一帧弹出去。
                        //
                        // 用 Min 而不是丢弃：丢弃会让时钟永久落后于
                        // 真实经过的时间，Min 只截掉超出部分。
                        if (delta > 0f)
                        {
                            previewClockTime += Mathf.Min(delta, 0.05f);
                        }
                    }
                    else
                    {
                    // **累加帧时间 + 锐利重锚。**
                    //
                    // 这一段的历史是两次各错一边：
                    //   · 纯赋值（上一版）：画面时钟 = songposition_minusi 那条
                    //     阶梯的最大值，于是永远落后真实音频 0~一个缓冲区
                    //     （实测 48kHz × 2048 = 42.7ms），而且落后多少取决于
                    //     正好停在阶梯的哪一段——表现就是"每五次有一次的音画
                    //     偏移"。原生没有这个，因为 scrCamera.Update 累加帧时间、
                    //     是连续的。
                    //   · 累加 + 每帧朝音频拉 8%（更早的一版）：一阶滞后，
                    //     稳态误差 = 帧时间 / 0.08 ≈ 75ms，所以被改回赋值。
                    //
                    // 对的解法是两者都取：**按帧时间连续推进**，并在阶梯读数
                    // 跑到我们前面时**锐利地对齐过去**。锐利（而不是比例增益）
                    // 是关键——它不会引入 1/增益 的稳态滞后，那正是 8% 那版
                    // 失败的原因。稳态下误差 ≤ 一帧，且连续无阶梯。
                    float now = Time.unscaledTime;
                    float delta = now - previewClockLastUnscaledTime;
                    previewClockLastUnscaledTime = now;
                    if (delta > 0f)
                    {
                        // 与无歌曲那条分支同一道限幅：一帧最多吃 0.05 秒。
                        // 正常一帧约 6ms，卡一下也不会超；而"暂停后基准没重置"
                        // 那种整段暂停时长会被限住，时钟不会一帧弹出去。
                        previewClockTime += Mathf.Min(delta, 0.05f);
                    }

                    float audio = Mathf.Max(0f, Convert.ToSingle(conductor.songposition_minusi));
                    if (audio > previewClockTime)
                    {
                        // 阶梯跑到我们前面：说明我们累加得慢了，它才是真位置。
                        previewClockTime = audio;
                    }
                    else if (previewClockTime - audio > MaxClockHoldSeconds)
                    {
                        // 落后超过一个缓冲区的量级：不是阶梯抖动，是真重定位
                        // （seek / 重启 / 拖播放头），必须放行回退。
                        previewClockTime = audio;
                    }
                    else if (previewClockTime - audio > MaxClockLeadSeconds)
                    {
                        // **领先也要封顶。** 两次阶梯跳跃之间，如果帧时间累加
                        // 略快（或者卡了一帧），领先量会一路长起来——实测尾部
                        // 到过 48.6ms，等于把"落后一个缓冲区"换成了"领先一个
                        // 缓冲区"，白改。这里硬收回上限值：是**截断**，不是
                        // 比例增益，所以不会引入 1/增益 的稳态滞后。
                        previewClockTime = audio + MaxClockLeadSeconds;
                    }
                    // 其余（0 ≤ 领先 ≤ 上限）什么都不做：那正是「阶梯还没跳、
                    // 我们已经推进到真实位置」的正常状态，也正是要保留的。

                    // 画面时钟相对阶梯读数的领先量。这一条是"时钟重建是否跟得住"
                    // 的客观指标 —— 期望始终非负、且不超过 MaxClockLeadSeconds
                    // （改之前它是阶梯、后面还藏着最多一个缓冲区）。
                    // 走 VerboseDiagnostics 门后：每秒两条，属于诊断噪声。
                    if (Main.Settings != null && Main.Settings.VerboseDiagnostics
                        && Time.frameCount - lastClockLeadLogFrame >= 30)
                    {
                        lastClockLeadLogFrame = Time.frameCount;
                        Main.Logger.Log("[ClockLead] "
                            + ((previewClockTime - audio) * 1000f).ToString("0.0") + "ms");
                    }
                    }

                }
            }

            long t1 = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            // 媒体拖放在途时快照刻意是过期的（拖放新增了一个
            // 装饰物），所以求值每帧都会失败，更糟的是，会和新装饰物的
            // 自己的 setter 打架。拖放直接驱动那一个，
            // 其余的就保持最后一帧；重建在松开时运行。
            if (!EditorSceneInteraction.IsMediaDropActive)
            {
                evaluatorBackend.TryEvaluate(previewClockTime, out string ignored);
            }
            long t2 = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            if (Time.frameCount - lastColliderRefreshFrame >= 30)
            {
                RestoreEditorClickability();
            }
            long t3 = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            EditorSceneInteraction.Tick(viewport, ReadSnapshot());
            long t4 = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            // 格的颜色动画跑在 Time.time 上，而它在 timeScale
            // 为 0 时不推进，所以暂停的场景会把 Glow/Pulse 轨道冻成一片平
            // 色，而其他东西看起来都正常。报得稀疏，因为它只在
            // 出错时才有意义。
            if (profile && Time.timeScale != 1f
                && Time.frameCount - lastTimeScaleDiagnosticFrame >= 120)
            {
                lastTimeScaleDiagnosticFrame = Time.frameCount;
                Main.Logger.Log("[TimeScale] " + Time.timeScale.ToString("0.00")
                    + " while evaluator-owned; floor colour animation is frozen unless this is 1.");
            }

            if (profile && Time.frameCount - lastTickCostFrame >= 60)
            {
                lastTickCostFrame = Time.frameCount;
                double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
                Main.Logger.Log("[TickCost] clock=" + ((t1 - t0) / f).ToString("0.00")
                    + "ms evaluate=" + ((t2 - t1) / f).ToString("0.00")
                    + "ms clickability=" + ((t3 - t2) / f).ToString("0.00")
                    + "ms interaction=" + ((t4 - t3) / f).ToString("0.00")
                    + "ms.");
            }
        }

        private static int lastTickCostFrame = -10000;

        /// <summary>
        /// 启动歌曲并武装逐格打击音。
        ///
        /// StartMusic 是唯一会排程片段的路径，而它的协程还会
        /// 调用 PlayHitTimes，后者走编辑器自己的格，按 dspTimeSong
        /// 为每格排一个打击音。所以一次调用同时提供了音乐和
        /// the踩踏声; there is no separate hit-sound scheduling to write.
        /// </summary>
        /// <summary>
        /// 让预览播放头跟随编辑器选中的格。
        ///
        /// 方向键是游戏自己的绑定——SelectPreviousFloorEditorAction 和
        /// SelectNextFloorEditorAction 调用 scnEditor.SelectFloor——所以选中项在移动
        /// 而桥并不知道。求值器渲染的是时钟的纯函数，而
        /// 选格不改变时钟，所以选中项在动，画面却停着，
        /// 时间轴游标却跳了。把选中的格映射到
        /// 播放头上，才让两者一致。
        ///
        /// 拖动自己驱动播放头，不能被覆盖；正在运行的 transport
        /// 掌握着真实播放的时钟，所以两者都不动。
        /// </summary>
        private static void FollowSelectedFloor(scrFloor floor)
        {
            FollowSelectedFloor(floor, false, false);
        }

        /// <param name="force">
        /// true = 不看 `FollowSelectionOnClick` 开关。用在两处：
        /// 定位模式打开时的点轨道（用户明确要"点哪跳哪"）、
        /// 以及打开工作台时落到原生选中的那一格。
        /// </param>
        /// <param name="animate">
        /// true = 用 1 秒内的加速走位过去（**点轨道、点事件块这两条都走它**）。
        /// false = 瞬跳（打开工作台落选中格那条：那一刻画面应该**已经**在目标格上，
        /// 用户是从原生编辑器切过来的，等 1 秒没有任何信息量）。
        /// </param>
        private static void FollowSelectedFloor(scrFloor floor, bool force, bool animate)
        {
            if (floor == null || timelineScrubActive || previewClockRunning)
            {
                return;
            }
            // 开关关掉时点事件只选中，画面和时间轴都别动。
            // 默认就是关的：点一下通常只是想改参数，跟着跳会打断
            // 正在看的那一段。force 是"用户刚刚明确要求跳"的那几条路。
            if (!force && Main.Settings != null && !Main.Settings.FollowSelectionOnClick)
            {
                return;
            }
            if (!evaluatorOwnedActive && !previewRequested)
            {
                return;
            }

            float target = (float)floor.entryTime;
            if (Mathf.Abs(target - previewClockTime) < 0.0001f)
            {
                return;
            }

            if (animate)
            {
                // 点轨道：最多一秒、慢起步加速过去（见 BeginPlayheadTravel）。
                BeginPlayheadTravel(target);
                return;
            }

            // 其它路：瞬跳。先把可能还在跑的走位撤掉，否则它下一帧又把
            // 播放头拽回原来的轨迹上。
            CancelPlayheadTravel();
            previewClockTime = target;
            previewClockLastUnscaledTime = Time.unscaledTime;
            evaluatorBackend.TryEvaluate(previewClockTime, out string ignored);
            ApplyStaticPreviewState();
        }

        // ------------------------------------------------------------ 播放头走位
        //
        // 「跳转」原来是直接赋值：previewClockTime = 目标，一帧到位。
        // 现在只有**在 AES 里点轨道**这一条路改成走过去（从当前时间出发，
        // 慢起步加速，1 秒以内到达目标）；打开工作台落选中格、点事件块跟随
        // 仍然是瞬跳 —— 前者的画面应该一进去就已经在那一格上，等一秒没有
        // 任何信息量。
        //
        // 每一帧照常求值，所以走位途中看到的仍是真实的谱面画面。

        /// <summary>走位最长时间（秒）。用户要的是"1 秒内到"。</summary>
        private const float TravelMaxSeconds = 1f;

        /// <summary>走位最短时间：只跳一两格时走满一秒反而像慢动作。</summary>
        private const float TravelMinSeconds = 0.25f;

        /// <summary>
        /// 每秒谱面时间对应的走位时长。25 秒以上的跨度就会吃满 1 秒上限，
        /// 一两格的小跳只走四分之一秒。
        /// </summary>
        private const float TravelSecondsPerSongSecond = 0.04f;

        private static bool travelActive;
        private static float travelFromTime;
        private static float travelToTime;
        private static float travelStartedAt;
        private static float travelDuration = TravelMinSeconds;

        /// <summary>正在走位（定位点击、打开落格、选中跟随都走这条路）。</summary>
        internal static bool IsPlayheadTravelling
        {
            get { return travelActive; }
        }

        /// <summary>
        /// 开始一次播放头走位。已经在走的时候从**当前显示的时间**接着走，
        /// 所以连点几下轨道不会跳回去。
        /// </summary>
        internal static void BeginPlayheadTravel(float targetTime)
        {
            if (!evaluatorOwnedActive && !previewRequested)
            {
                return;
            }

            float distance = Mathf.Abs(targetTime - previewClockTime);
            if (distance < 0.0001f)
            {
                travelActive = false;
                return;
            }

            travelFromTime = Mathf.Max(0f, previewClockTime);
            travelToTime = Mathf.Max(0f, targetTime);
            travelDuration = Mathf.Clamp(
                distance * TravelSecondsPerSongSecond, TravelMinSeconds, TravelMaxSeconds);
            travelStartedAt = Time.unscaledTime;
            travelActive = true;

            // 一次跳转一行日志（不是每帧）。定位点轨道没反应时，靠它区分
            // "根本没走到这里"和"走了又被谁取消"。
            if (Main.VerboseLog)
            {
                Main.Logger.Log("[定位] 开始走位 " + travelFromTime.ToString("0.000")
                    + " → " + travelToTime.ToString("0.000")
                    + " 时长=" + travelDuration.ToString("0.000") + " 秒"
                    + " 求值器自有=" + evaluatorOwnedActive
                    + " 静帧=" + staticPreviewFrameActive + ".");
            }
        }

        /// <summary>取消走位（用户自己拖播放头、开始播放、关工作台时用）。</summary>
        internal static void CancelPlayheadTravel()
        {
            travelActive = false;
        }

        /// <summary>
        /// 每帧推进走位。由叠加层的 Update 调用 —— 静态预览下没有别的东西
        /// 在推进 `previewClockTime`。
        ///
        /// 曲线是 `t²`（加速：起步慢、越走越快），因为要求就是"加速过去"。
        /// 想换成两头缓的，把这一行换成 smoothstep 即可。
        /// </summary>
        internal static void TickPlayheadTravel()
        {
            if (!travelActive)
            {
                return;
            }

            // 播放（原生 transport 或求值器播放）和手动拖动都优先于走位：
            // 那两种情况下时钟由别人掌握，这里必须立刻让位。
            //
            // **不能用 `NativeRuntimeIsLive`**：它的第二个条件是
            // `editor.playMode && !editor.inStrictlyEditingMode`，而
            // `ApplyStaticPreviewState` 会让 `pausedInPlayMode` 置位 ——
            // 那个字段一置位，`scnEditor.playMode` 的 getter 就**无条件返回 true**，
            // 也就是工作台拥有预览的**整个期间**都成立。拿它当"原生正在播"的判据，
            // 走位一开就被自己取消，表现就是「点轨道完全没反应」。
            // 只认真正那几个"别人已经接管时钟"的旗标。
            if (!previewRequested
                || previewClockRunning
                || timelineScrubActive
                || nativePlaybackStarted
                || trackBTransportActive)
            {
                travelActive = false;
                if (Main.VerboseLog)
                {
                    Main.Logger.Log("[定位] 走位取消：previewRequested=" + previewRequested
                        + " playing=" + previewClockRunning
                        + " scrub=" + timelineScrubActive
                        + " nativePlayback=" + nativePlaybackStarted
                        + " trackB=" + trackBTransportActive + ".");
                }
                return;
            }

            float ratio = travelDuration <= 0.0001f
                ? 1f
                : Mathf.Clamp01((Time.unscaledTime - travelStartedAt) / travelDuration);
            float eased = ratio * ratio;

            float time = ratio >= 1f
                ? travelToTime
                : Mathf.LerpUnclamped(travelFromTime, travelToTime, eased);

            previewClockTime = time;
            previewClockLastUnscaledTime = Time.unscaledTime;
            evaluatorBackend.TryEvaluate(previewClockTime, out string ignored);
            ApplyStaticPreviewState();

            if (ratio >= 1f)
            {
                travelActive = false;
            }
        }

        /// <summary>
        /// Track C：播放就是求值器，由原生 conductor 的时钟驱动。
        ///
        /// 这个形态没有可见交接：同一个渲染器在播放之前、期间、
        /// 之后都在画，所以没有缝要藏——播放期间它照样持续刷新。
        ///
        /// 我们花了很长时间的另一条路——让原生 ffx 特效在播放期间
        /// 拥有画面——做不到无缝。两个渲染器对同一瞬间
        /// 东西在哪看法不同：提交时求值器把球放在
        /// (-4.551, 13.888)，在它那格内插值，而原生 Scrub 把它放到
        /// 格中心 (-6.000, 13.500)，相机差了 1.891 单位。那是
        /// 语义差异，不是要修的 bug，所以它们之间任何交接都会跳。
        /// 去掉这个差异的代价，是播放时画面出自求值器——也就是
        /// 接受这个取舍：滤镜、粒子和视频按求值器的保真度动，
        /// 而不是按引擎的。
        ///
        /// 求值器没有丢的是时钟：conductor 仍然拥有歌曲和
        /// songposition_minusi，所以音乐、打击音和画面都挂在同一个
        /// dsp 锚定的时间上。
        /// </summary>
        private static bool TrySetEvaluatorPlayback(bool playing, out string status)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                status = "编辑器尚未就绪";
                return false;
            }

            if (playing)
            {
                // 不要把拖动留下的定位请求带进播放。
                //
                // 拖动结束时会留一个 lastTimelineScrubTime，时间轴的
                // Update 每帧检查 scrubPending 并提交它；如果那次提交
                // 落在播放开始之后，TryScrubTimelineToTime 就会把
                // previewClockTime 拉回拖动时的位置——时间线突然退一段，
                // 前后画面接不上。
                //
                // applyTime: false 只清状态、不动时钟，播放从当前
                // 时钟位置继续。
                EndTimelineScrub(false);

                // preserveTransport：会话必须在预览已经在显示的播放头处
                // 起来，而不是在零处。
                EnsureEvaluatorSession(true);
                if (!evaluatorOwnedActive)
                {
                    status = "预览求值器不可用，详情见 Mod 日志";
                    return false;
                }

                previewRequested = true;
                previewClockRunning = true;

                // 时钟的帧时间基准必须在这里重置。
                //
                // 没音乐的谱面靠 previewClockLastUnscaledTime 累加帧时间
                // 推进时钟（见 TickEvaluatorOwnedPreview）。暂停期间它不被
                // 更新，所以恢复后的第一帧算出的 delta 是整个暂停时长——
                // 实测 0.43 秒——而那个上限（0.5）放它过去了，于是时钟
                // 一帧跳 0.43 秒，播放头线跟着弹一下。
                //
                // 重置之后第一帧的 delta 就是一帧，不再吃掉暂停时长。
                previewClockLastUnscaledTime = Time.unscaledTime;

                // StartMusic 之前 timeScale 必须是 1，这很容易漏，因为
                // 求值器本身并不需要缩放时间：StartMusicCo 用
                // `for (timer = 0.1f; timer >= 0; timer -= Time.deltaTime)` 等完它的 0.1s
                // 前奏，而 timeScale 为零时 deltaTime 也是零，所以那个循环永不结束，
                // 排程回调永不触发，播放就永远停在播放头处
                // （"[AudioResume] gave up waiting for the schedule callback"）。拖动
                // 会把 timeScale 留成 0，所以这是常见情况，不是边缘情况。
                Time.timeScale = 1f;
                StartEvaluatorPreviewAudio(previewClockTime);
                desiredPaused = false;
                status = "播放中";
                Main.Logger.Log("[TrackC] playing from " + FormatTimelineTime(previewClockTime) + ".");
                return true;
            }

            previewClockRunning = false;
            StopEvaluatorPreviewAudio();
            Main.Logger.Log("[TrackC] pause; time=" + FormatTimelineTime(previewClockTime) + ".");
            // 之后每帧追一次，看冻结的那一帧有没有被别的写入者改动。
            // 保持时间就保持那一帧，下一次播放从同一瞬间恢复。
            if (evaluatorOwnedActive)
            {
                string ignored;
                evaluatorBackend.TryEvaluate(previewClockTime, out ignored);
            }
            desiredPaused = true;
            status = "已暂停";

            // heldPreviewTime 必须跟着走。
            //
            // 暂停态的时钟有两条来源，而快照读的是这一条：
            //
            //   if (desiredPaused && !float.IsNaN(heldPreviewTime))
            //       snapshot.CurrentTime = heldPreviewTime;
            //
            // 这里以前只把 desiredPaused 置位、没同步 heldPreviewTime，
            // 于是它停在更早那次交接留下的位置（实测差约 1.2 秒），
            // 暂停一发生时间线就瞬间后退一段，前后画面接不上。
            heldPreviewTime = previewClockTime;
            return true;
        }

        private static void StartEvaluatorPreviewAudio(float fromTime)
        {
            scrConductor conductor = scrConductor.instance;
            if (conductor == null)
            {
                return;
            }

            GCS.checkpointNum = 0;
            conductor.Rewind();
            conductor.fastTakeoff = true;
            conductor.gameObject.SetActive(true);

            // 组件本身也必须启用。
            //
            // 冻结路径（ApplyPausedState）会把 conductor.enabled 置成 false——
            // 那里是对的，它把 songposition_minusi 手动摆到冻结位置，
            // 然后让组件停摆以免它自己重算。但「冻结」和「重新播放」
            // 是两条独立的路：重启提交（TickPreviewRestart 里
            // conductor.enabled = !exactCommit）在 exactCommit 时把它关掉，
            // 而这条启动路径过去只做了 gameObject.SetActive(true)——
            // 那不等于 enabled = true。
            //
            // 组件停着，scrConductor.Update 就不跑，songposition_minusi
            // 永远停在冻结那一刻的值；而音频是 AudioSource 按 dsp 时间
            // 自己播的，照走不误。结果就是「音乐在跑、画面不动」，
            // 而且画面冻在拖动定位时的位置上。
            //
            // 在日志里量到：time=00:08.289 冻结 112 条，songpos=8.289 不动，
            // 同时 song.isPlaying=True、song.time 从 10.065 一路涨到 10.662，
            // condEnabled=False。
            conductor.enabled = true;

            // 拖动定位期间静音是刻意的，但播放必须真的
            // 能听见，打击音才有意义。
            AudioListener.pause = false;

            float startTime = Mathf.Max(0f, fromTime);
            evaluatorAudioStarting = true;

            // 只有谱面真的带歌时才等音频排程。
            //
            // 判断依据必须是谱面的 songFilename，不能是 song.clip：
            // clip 是异步加载的，会话刚起来时它还是 null，用它判断
            // 会让每一次启动都跳进「没有音频」那条路——时钟于是在
            // 排程期间裸奔回 0，时间线读到就闪回开头。
            //
            // songFilename 是谱面自己的字段，和加载时序无关。
            scnGame audioLevel = scnGame.instance;
            ADOFAI.LevelData level = audioLevel == null ? null : audioLevel.levelData;
            bool hasSong = level != null && !string.IsNullOrEmpty(level.songFilename);

            audioStartPending = hasSong;
            audioStartTime = startTime;
            pendingAudioLogged = false;
            audioStartPendingFrames = 0;
            audioStartDsp = 0d;
            if (!hasSong)
            {
                Main.Logger.Log("[AudioWait] 谱面没有歌曲，跳过音频等待。");
            }
            try
            {
                conductor.StartMusic(null, delegate
                {
                    // seek 必须在排程回调里运行：dspTimeSong 只有
                    // 在协程排好片段之后才存在。
                    SetExactConductorTime(conductor, startTime, true);

                    // StartMusicCo 是按这次 seek 移动之前的 dspTimeSong 排的
                    // 打击音，所以现在每一个都偏移了
                    // 一个 seek 距离。按修正后的 dspTimeSong 重新排队是
                    // what puts the踩踏声 back on the floors. Safe to repeat: PlayHitTimes
                    // 会停掉所有声音并从头重建队列。
                    conductor.PlayHitTimes();

                    Main.Logger.Log("[EvaluatorOwned] audio started at "
                        + FormatTimelineTime(startTime) + ".");

                    // 片段不会落在被要求的位置。对 Ogg Vorbis 片段赋值
                    // AudioSource.time 会落在压缩块边界上，
                    // 因为这种格式不保留采样索引——所以按请求时间
                    // 摆放的画面，会与音乐实际所在位置
                    // 相差一个可变距离。这个误差只取决于目标，
                    // 这就是为什么某些播放头位置总是对得上、
                    // 另一些永远对不上，也是为什么最开始总是对的：那里
                    // 前奏把片段放在零，而零不需要 seek。
                    //
                    // **这里曾经做"读回片段落点、把场景时钟移过去"的校正，
                    // 已整个删掉。** 原因是读数本身不可用：`song.time` 报的是
                    // "已经交给声卡的位置"，天生比排定的时刻滞后约一个音频缓冲区
                    // （48kHz × 2048 = 42.7ms）。实测连跑八次的逐帧平均值全是
                    // −18 ~ −104ms，全部落在那条滞后带里——真实的 seek 落点误差
                    // 被这个恒定偏置淹没，照它去推时钟只会把画面推偏
                    // （调阈值、加样本、换中位数都试过，都改变不了这一点）。
                    //
                    // 原版**从不回头读**这个量，对齐纯靠"用同一个
                    // 目标时刻一次性摆好时钟与片段位置"——那就在上面的
                    // SetExactConductorTime 里做完了。跟着它。
                });
            }
            finally
            {
                evaluatorAudioStarting = false;
            }
        }

        // 周期性复查按这个节奏跑。足够频繁，让一次错误修正能在
        // 零点几秒内被撤掉；又足够稀疏，不会每帧都重建打击音队列。
        // every frame.
        private const int DesyncCheckIntervalFrames = 30;

        private static void StopEvaluatorPreviewAudio()
        {
            scrConductor conductor = scrConductor.instance;
            if (conductor != null && conductor.song != null && conductor.song.isPlaying)
            {
                conductor.song.Pause();
            }
            AudioListener.pause = true;
        }

        private static bool TryApplyNativePause(
            scrController controller,
            bool paused,
            bool reapplyPause)
        {
            if (controller == null)
            {
                return false;
            }

            controller.enabled = true;
            if (paused && reapplyPause && controller.paused)
            {
                bool resumed = controller.TogglePauseGame();
                AudioListener.pause = true;
                Time.timeScale = 0f;
                if (resumed)
                {
                    return false;
                }
            }

            if (controller.paused != paused)
            {
                bool actualPaused = controller.TogglePauseGame();
                AudioListener.pause = paused;
                Time.timeScale = paused ? 0f : 1f;
                return actualPaused == paused;
            }
            controller.enabled = !paused;
            AudioListener.pause = paused;
            Time.timeScale = paused ? 0f : 1f;
            return true;
        }

        private static void PauseScrubbedVfxTweens(scrVfxPlus vfx)
        {
            if (vfx == null || vfx.pausedTweens == null)
            {
                return;
            }

            foreach (Tween tween in vfx.pausedTweens)
            {
                if (tween != null && tween.active)
                {
                    tween.Pause();
                }
            }
        }

        private static void ResumeScrubbedVfxTweens()
        {
            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx == null || vfx.pausedTweens == null)
            {
                return;
            }

            foreach (Tween tween in vfx.pausedTweens)
            {
                if (tween != null && tween.active)
                {
                    tween.Play();
                }
            }
            vfx.pausedTweens.Clear();
        }

        private static string FormatTimelineTime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            int minutes = Mathf.FloorToInt(seconds / 60f);
            float remainder = seconds - minutes * 60f;
            return minutes.ToString("00") + ":" + remainder.ToString("00.000");
        }

        private static void RestoreEditorClickability()
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager != null)
            {
                manager.ToggleClickableBoxColliderForLevelEditor(true);
            }
            lastColliderRefreshFrame = Time.frameCount;
        }

        private static void EnableAutoMode()
        {
            if (autoModeOverridden)
            {
                return;
            }

            previousAutoMode = RDC.auto;
            RDC.auto = true;
            autoModeOverridden = true;
        }

        private static void RestoreAutoMode()
        {
            if (!autoModeOverridden)
            {
                return;
            }

            RDC.auto = previousAutoMode;
            autoModeOverridden = false;
        }

        /// <summary>
        /// 【兼容 PACL2,非抄袭】
        ///
        /// 这是可选的兼容层,目的是让本工作台的撤回/重做按钮在装了 PACL2 的环境里也能正确
        /// 显示可用状态。它不复制 PACL2 的任何代码,只是在运行时按类型名找一下 PACL2 自己
        /// 的静态列表并读取条目数。没装 PACL2 时类型找不到,直接返回 false,不产生任何影响。
        ///
        /// 为什么需要它:PACL2 会把 scnEditor.SaveState 整个替换掉,把快照存进它自己的静态
        /// 列表,于是 scnEditor.undoStates 永远是空的。但它的 editor.Undo() 仍然弹它自己的
        /// 列表 —— 也就是说撤回这个动作本身是正常的,只有"能不能撤回"的判断需要换个地方看。
        ///
        /// 用反射按名字查找而不是直接引用类型,就是为了让 PACL2 保持为可选依赖:没装的时候
        /// 类型解析失败返回 false,不会被拖垮。
        /// </summary>
        internal static bool Pacl2StackHasEntries(string fieldName)
        {
            try
            {
                Type patchType = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && patchType == null; i++)
                {
                    patchType = assemblies[i].GetType(
                        "PACL2.Features.FixChartLoad.CustomSaveState.SaveStatePatch", false);
                }
                if (patchType == null)
                {
                    return false;
                }

                FieldInfo field = patchType.GetField(
                    fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    return false;
                }

                System.Collections.ICollection collection =
                    field.GetValue(null) as System.Collections.ICollection;
                return collection != null && collection.Count > 0;
            }
            catch (Exception)
            {
                // 这里是兼容层,绝不能因为 PACL2 的类型缺失或改名而把撤回功能一起带崩。
                return false;
            }
        }

        private static void ReportError(string context, Exception exception)
        {
            Exception actual = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            string message = context + ": " + actual.GetType().Name + " - " + actual.Message;
            if (message == lastError)
            {
                return;
            }

            lastError = message;
            if (Main.Logger != null)
            {
                Main.Logger.Error(message);
            }
        }
    }

    internal static class EditorUpdateInputPatch
    {
        /// <summary>功能快捷键探针已输出几次（上限 20，避免按住按键时刷屏）。</summary>
        private static int featureProbeCount;
        /// <summary>
        /// 清除所有选中：游戏侧的格与装饰物，以及工作台自己维护的
        /// 时间轴事件和媒体卡片。
        ///
        /// 游戏那两个 API 正是它自己「点空白处」时走的一对
        /// （scnEditor.CycleObjectSelection），所以清完的状态和
        /// 原生的「什么都没选中」完全一致，不会留下半清半不清的中间态。
        /// </summary>
        private static void ClearEditorSelection()
        {
            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                editor.DeselectFloors();
                editor.DeselectAllDecorations();
            }

            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay != null)
            {
                overlay.ClearSelection();
            }
        }

        private static bool Prefix()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || !overlay.IsVisible)
            {
                return true;
            }

            // **加载遮罩期间吃掉输入。**
            //
            // uGUI 的遮罩挡住的是鼠标（GraphicRaycaster + 全屏 Backdrop），
            // 键盘它挡不住：按空格会直接走到下面的 togglePlayback，于是
            // 求值器预览开始播歌 —— 用户看到的是"加载页还盖着，音乐响了"。
            //
            // 只在**真有输入的那一帧**整帧吞掉（返回 false，连原生 Update
            // 一起跳过）；没有输入的帧照常放行，别把编辑器每帧该做的事也停掉。
            if (overlay.IsLoadingScreenShowing)
            {
                return !Input.anyKeyDown;
            }

            bool togglePlayback = Input.GetKeyDown(KeyCode.Space);
            bool controlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            bool undo = controlHeld && !shiftHeld && Input.GetKeyDown(KeyCode.Z);
            // Shift+C：清除选中。
            //
            // 原生编辑器把 KeyCode.C 绑到了「用字符 C 创建一格」
            // （KeyModifier.None 和 Shift 都注册了同一个动作）。我们只在
            // 工作台可见时抢——这种帧 Prefix 会返回 false、原生输入循环
            // 整帧被跳过，和 Space、Ctrl+Z 是同一套做法。工作台关掉时
            // Shift+C 仍然是原生的「建格」。
            bool clearSelection = shiftHeld && !controlHeld && !altHeld
                && Input.GetKeyDown(KeyCode.C);
            // Ctrl+S：保存。
            //
            // 原生编辑器本来就把 Ctrl+S 绑在 `SaveLevelEditorAction` 上，
            // 工作台不认的键也一律放行给原生 —— 但它显然没跑到，所以这里
            // 显式接住。**走的是原生那同一个 `scnEditor.SaveLevel()`**，
            // 也就是 `levelData.Encode()` + `RDFile.WriteAllText`，存出来的
            // 内容跟原版逐字节一样。绝不自己拼 JSON —— 那会把尾逗号、
            // `3.201651E-06` 这类格式改坏（这是本项目最早踩到的坑）。
            bool save = controlHeld && !shiftHeld && !altHeld
                && Input.GetKeyDown(KeyCode.S);
            // 时间轴选区的 复制 / 剪切 / 粘贴 / 删除。
            //
            // Ctrl+C 是空的（Shift+C 才是"清除选中"，见上面），所以不冲突。
            // 删除同时认 Delete 和 Backspace：前者是 Windows 的键，后者是
            // Mac 的；文本输入框里的那两种由 IsTextInputFocused 挡在前面。
            bool copySelection = controlHeld && !shiftHeld && !altHeld
                && Input.GetKeyDown(KeyCode.C);
            bool cutSelection = controlHeld && !shiftHeld && !altHeld
                && Input.GetKeyDown(KeyCode.X);
            bool pasteSelection = controlHeld && !shiftHeld && !altHeld
                && Input.GetKeyDown(KeyCode.V);
            // **只有 Delete 删块，退格不删。**
            //
            // 原来两个都认（Windows 用 Delete、Mac 用退格）。但 ImGui 那边的
            // 输入框**不在 `IsTextInputFocused` 的覆盖范围里**（那只认 uGUI），
            // 所以在一个 ImGui 框里按退格改字，会顺手把选中的事件块删掉。
            // 退格是打字时最常用的键，留着这个口子太容易误伤。
            bool deleteSelection = !controlHeld && !altHeld
                && Input.GetKeyDown(KeyCode.Delete);
            bool selectionShortcut = copySelection || cutSelection || pasteSelection
                || deleteSelection;
            // Ctrl+Y 和 Ctrl+Shift+Z，与 scnEditor 原生注册的快捷键一致。
            bool redo = controlHeld
                && ((!shiftHeld && Input.GetKeyDown(KeyCode.Y))
                    || (shiftHeld && Input.GetKeyDown(KeyCode.Z)));
            bool timelinePlus = Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);            bool timelineMinus = Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
            bool timelineReset = Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0);

            bool timelineShortcut = (controlHeld && (timelinePlus || timelineMinus || timelineReset))
                || (altHeld && (timelinePlus || timelineMinus));
            bool timelineWheel = (controlHeld || altHeld)
                && Mathf.Abs(Input.mouseScrollDelta.y) > 0.001f;

            // 定位（点轨道跳画面）与自动翻页的开关。
            //
            // **只有一组：Ctrl+J = 定位、Ctrl+K = 自动翻页。**
            //
            // 历史（别再走一遍）：
            //   · 最初用 Ctrl+Alt+字母，实测本机按住 Ctrl+Alt 时 Unity
            //     **根本收不到字母键**、`inputString` 也是空的
            //     （`[快捷键] ctrl=True alt=True L=False input=""`）——
            //     键在进 Unity 之前就被输入法拦掉了；
            //   · Ctrl+Shift+字母 也不行：中文输入法里那是"切换输入法"；
            //   · 功能性单键（F8/F9）按用户要求不采用。
            // 纯 Ctrl 组合不在上面这些名单里，所以只留这一组，不再挂备用。
            //
            // 选 J / K 是因为**原生编辑器没绑它们**（原生绑的是
            // N O V S X C Z Y T Q F E A H L P）。
            // 工作台可见时命中就整帧吃掉（返回 false）。
            bool ctrlOnly = controlHeld && !altHeld && !shiftHeld;
            bool locateToggle = ctrlOnly && Input.GetKeyDown(KeyCode.J);
            // Ctrl+K 现在是「定位到播放头」（一次性动作），不再是自动翻页开关。
            bool locatePlayhead = ctrlOnly && Input.GetKeyDown(KeyCode.K);
            // Ctrl+L：切换「固定排版 / 自由排版」。
            bool layoutToggle = ctrlOnly && Input.GetKeyDown(KeyCode.L);
            bool featureToggle = locateToggle || locatePlayhead || layoutToggle;

            // 探针：只记 Ctrl+J / Ctrl+K（上限 20 行）。判读：J/K 为 True 即通；
            // 都是 False 且 input="" 说明这组键也在进 Unity 之前被吃掉。
            if (ctrlOnly && (Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.K))
                && Main.VerboseLog && featureProbeCount < 20)
            {
                featureProbeCount++;
                Main.Logger.Log("[快捷键] ctrl=" + controlHeld + " alt=" + altHeld
                    + " shift=" + shiftHeld
                    + " J=" + Input.GetKeyDown(KeyCode.J)
                    + " K=" + Input.GetKeyDown(KeyCode.K)
                    + " input=\"" + Input.inputString + "\".");
            }

            // 原生编辑器也会消费这些快捷键。时间轴在悬停期间
            // 拥有它们，所以跳过原生输入循环的这一帧。
            if ((timelineWheel || timelineShortcut)
                && overlay.IsPointerInsideTimeline(Input.mousePosition)
                && !overlay.IsTextInputFocused())
            {
                return false;
            }

            // 只要工作台历史有东西可给，撤销/重做就归它管。它的栈
            // 是唯一能逆转从媒体面板创建的装饰物的栈，
            // 也是唯一能重做一个的——编辑器自己的栈在这里根本
            // 不能用（那个栈随时可能被替换掉，替换后的撤销只重放它自己
            // 保存范围窗口记录下的变化，而拖进来的装饰物从来不在其中）。
            bool workbenchUndo = undo && EditorCommandHistory.CanUndo;
            bool workbenchRedo = redo && EditorCommandHistory.CanRedo;
            bool nativeUndo = undo && !workbenchUndo;
            bool nativeRedo = redo && !workbenchRedo;

            if (!togglePlayback && !workbenchUndo && !workbenchRedo && !nativeUndo && !nativeRedo
                && !clearSelection && !save && !selectionShortcut && !featureToggle)
            {
                return true;
            }

            if (overlay.IsTextInputFocused())
            {
                // 走到这儿说明**已经按下了**一个该生效的快捷键，却被焦点挡掉了。
                // 这种情况以前只会静默地"没反应"，查起来毫无线索 —— 打一行
                // 出来，下次再出现直接能看到是哪个物体占着焦点。
                UnityEngine.EventSystems.EventSystem events
                    = UnityEngine.EventSystems.EventSystem.current;
                UnityEngine.GameObject focused = events == null
                    ? null : events.currentSelectedGameObject;
                Main.Logger.Log("[Keys] 快捷键被输入框焦点挡掉："
                    + (focused == null ? "(空)" : focused.name));
                return true;
            }

            if (clearSelection)
            {
                ClearEditorSelection();
                return false;
            }

            // 定位（开关）/ 定位到播放头（动作）。状态与提示都在时间轴那边，
            // 这里只负责把键接住、别让它落到原生编辑器上。
            if (featureToggle)
            {
                EditorTimelineView featureTimeline = overlay.Timeline;
                if (featureTimeline != null)
                {
                    if (locateToggle)
                    {
                        featureTimeline.ToggleLocateOnTrackClick();
                    }
                    if (locatePlayhead)
                    {
                        featureTimeline.LocateToPlayhead();
                    }
                    if (layoutToggle)
                    {
                        featureTimeline.ToggleFixedTimelineLayout();
                    }
                }
                return false;
            }

            if (save)
            {
                scnEditor saveEditor = scnEditor.instance;
                if (saveEditor != null)
                {
                    try
                    {
                        // 原生那条 `ShowNotification(levelSaved)` 其实弹了，
                        // 但它画在游戏自己的 UI 上、被工作台这块全屏面板整个
                        // 盖住 —— 实测用户完全看不见。所以结果要报进工作台
                        // 自己的顶栏读数。
                        bool hadPath = !string.IsNullOrEmpty(ADOBase.levelPath);
                        saveEditor.SaveLevel();
                        // **一次 Ctrl+S 存两样**：谱面（原生 SaveLevel）+ 工程排版
                        // （行号写进 .aesprj）。排版以前单独挂在 Ctrl+Alt+S 上，
                        // 所以按完 Ctrl+S 关工作台还会被问"布局还没保存"。
                        if (Main.Overlay != null)
                        {
                            Main.Overlay.SaveLayoutIfDirty();
                            Main.Overlay.SetTransientStatus(hadPath
                                ? "已保存  " + System.IO.Path.GetFileName(ADOBase.levelPath)
                                : "另存为…（在弹出的对话框里选位置）", 5f);
                        }
                    }
                    catch (Exception exception)
                    {
                        Main.Logger.Error("[Save] SaveLevel 失败：" + exception);
                        if (Main.Overlay != null)
                        {
                            Main.Overlay.SetTransientStatus("保存失败，见日志", 6f);
                        }
                    }
                }
                return false;
            }

            // 时间轴选区的 复制 / 剪切 / 粘贴 / 删除。
            //
            // 工作台能处理就自己处理掉（返回 false，跳过原生）；处理不了
            // （没选中东西、剪贴板是空的）就**放行给原生** —— 比如原生编辑器
            // 自己也有一个删除选中事件的键，那种情况下该由它来。
            if (selectionShortcut)
            {
                EditorTimelineView selectionTimeline =
                    Main.Overlay == null ? null : Main.Overlay.Timeline;
                string selectionStatus = null;
                bool handled = false;

                if (selectionTimeline != null)
                {
                    if (deleteSelection)
                    {
                        handled = selectionTimeline.DeleteSelection(out selectionStatus);
                    }
                    else if (copySelection)
                    {
                        int copied = selectionTimeline.CopySelection();
                        if (copied > 0)
                        {
                            selectionStatus = "已复制 " + copied + " 个事件";
                            handled = true;
                        }
                    }
                    else if (cutSelection)
                    {
                        handled = selectionTimeline.CutSelection(out selectionStatus);
                    }
                    else if (pasteSelection)
                    {
                        handled = selectionTimeline.PasteAtPlayhead(out selectionStatus);
                    }
                }

                if (!handled)
                {
                    // **被我们挡下的（只读模式那种）要把键吃掉。**
                    //
                    // 只有"我们压根没参与"（没选中东西、剪贴板空的）才放行给原生。
                    // 拦下来还放行的话，请求会落到**原版自己那把删除/粘贴键**上 ——
                    // 只读模式照样把事件删掉（实测就是这样）。
                    //
                    // 判据用"有没有留下一句话"：我们的守卫都会写明原因，
                    // 而"没选中东西"是不吭声的。
                    if (!string.IsNullOrEmpty(selectionStatus))
                    {
                        if (Main.Overlay != null)
                        {
                            Main.Overlay.SetTransientStatus(selectionStatus, 3f);
                        }
                        return false;
                    }
                    return true;
                }

                if (Main.Overlay != null)
                {
                    Main.Overlay.SetTransientStatus(selectionStatus, 3f);
                }
                return false;
            }

            if (togglePlayback)
            {
                overlay.TogglePlaybackFromShortcut();
            }

            if (workbenchUndo || workbenchRedo)
            {
                string historyStatus = null;
                bool wasPlaying = EditorPlaybackBridge.ReadSnapshot().State == EditorPlaybackState.Playing;
                bool pausedForHistory = false;
                if (wasPlaying)
                {
                    if (EditorPlaybackBridge.TrySetPaused(true, out historyStatus))
                    {
                        pausedForHistory = true;
                    }
                }

                if (!wasPlaying || pausedForHistory)
                {
                    bool historyApplied;
                    try
                    {
                        historyApplied = workbenchRedo
                            ? EditorCommandHistory.TryRedo(out historyStatus)
                            : EditorCommandHistory.TryUndo(out historyStatus);

                        // 装饰物集合变了，所以求值器缓存的引用
                        // 过期了。它会在下次求值时自愈，但现在就问
                        // 能避免一帧的不一致。
                        EditorPlaybackBridge.RequestEvaluatorRebuild("workbench-history");
                    }
                    finally
                    {
                        if (pausedForHistory)
                        {
                            EditorPlaybackBridge.TrySetPaused(false, out _);
                        }
                    }

                    // 工作台的撤回 / 重做是**直接写字段**，不换 levelData ——
                    // 面板拿不到任何「选中项变了」的信号，会一直显示撤回前的
                    // 数字。看起来就是「撤了但参数没变」。这里必须主动叫它重画。
                    if (historyApplied && Main.Overlay != null)
                    {
                        Main.Overlay.RefreshPropertyPanel();
                    }
                }

                if (!string.IsNullOrEmpty(historyStatus))
                {
                    overlay.SetSceneSelectionStatus(historyStatus);
                }
                return false;
            }

            if (nativeUndo || nativeRedo)
            {
                // 工作台历史为空时，回退到编辑器自己的撤销/重做
                // ——例如在游戏自带面板里做的一次编辑。
                //
                // 直接调用，而不是交给原生快捷键派发：
                // scnEditor.Update 只在
                //   !playMode
                // 时才跑 HandleKeyboardActions，而 pausedInPlayMode 一旦置位，
                // playMode 的 getter 就无条件返回 true，工作台拥有预览的整个期间
                // ApplyStaticPreviewState 正是这样做的。所以这些快捷键在工作台里
                // 是死的，一关掉工作台就开始生效。
                scnEditor editor = scnEditor.instance;
                string status = null;
                if (editor == null)
                {
                    status = "编辑器尚未就绪";
                }
                else
                {
                    // 可用状态必须从"当前编辑器实际在用的那个栈"去读。默认就是
                    // scnEditor.undoStates；但那个方法可能已被替换，快照被挪进
                    // 替换方自己的静态列表，此时编辑器那个列表永远是空的。
                    // 所以两种都探一下，谁有东西就信谁。
                    bool hasUndo = editor.undoStates != null && editor.undoStates.Count > 0;
                    bool hasRedo = editor.redoStates != null && editor.redoStates.Count > 0;
                    if (!hasUndo && !hasRedo)
                    {
                        hasUndo = EditorPlaybackBridge.Pacl2StackHasEntries("undoStates");
                        hasRedo = EditorPlaybackBridge.Pacl2StackHasEntries("redoStates");
                    }

                    bool available = nativeRedo ? hasRedo : hasUndo;
                    if (!available)
                    {
                        status = nativeRedo ? "没有可重做的操作" : "没有可撤回的操作";
                    }
                    else
                    {
                        if (nativeRedo)
                        {
                            editor.Redo();
                        }
                        else
                        {
                            editor.Undo();
                        }
                        status = nativeRedo ? "已重做" : "已撤回";
                    }
                }

                if (!string.IsNullOrEmpty(status))
                {
                    overlay.SetSceneSelectionStatus(status);
                }

                // UndoOrRedo 会重建每个装饰物对象，所以求值器缓存的
                // 引用过期了。它的快照在下次求值时自愈，但这里
                // 也请求一次重建，这样画面不会迟一帧。
                EditorPlaybackBridge.RequestEvaluatorRebuild("native-history");
            }

            return false;
        }
    }

    internal static class EditorExitPatch
    {
        private static bool Prefix(bool clsToEditor)
        {
            return !EditorPlaybackBridge.ShouldBlockNativeExit(clsToEditor);
        }
    }

    internal static class EditorDragCameraPatch
    {
        private static bool Prefix()
        {
            if (EditorSceneInteraction.IsCapturingPointer || EditorTimelineView.IsAnyPointerCapture)
            {
                return false;
            }

            EditorStudioOverlay overlay = Main.Overlay;
            return overlay == null
                || (!overlay.IsPointerOverNonPreviewUi && !overlay.IsCapturingNonPreviewPointer);
        }
    }

    internal static class EditorZoomCameraPatch
    {
        private static bool Prefix()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || !overlay.IsVisible || Input.mouseScrollDelta.y == 0f)
            {
                return true;
            }

            // **正在调装饰物时，滚轮归装饰物。**
            //
            // 原生这几档里 Ctrl 是直接放行的（下面那个 controlHeld 分支），
            // 所以拖着装饰物滚轮时：Alt / Ctrl 那几档被装饰物拿走、看着正常，
            // 而**纯滚轮**会漏到原生这里、变成缩放画面 —— 实测就是这个现象。
            // 调装饰物期间一律拦掉原生缩放。
            if (EditorSceneInteraction.IsAdjustingDecoration)
            {
                return false;
            }

            bool controlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (controlHeld)
            {
                return true;
            }

            return !overlay.ShouldBlockPreviewZoom(Input.mousePosition);
        }
    }

    /// <summary>
    /// 还原暂停的编辑器路径剥掉的逐格打击音音量。
    ///
    /// scnGame.ApplyEventsToFloors 会销毁每个格上的每个 ffxPlusBase，然后
    /// 重建它们，但 scrController.paused 一旦置位它就会带着提前 return
    /// 退出——而承载谱面打击音的一切都创建在那次
    /// return 之后。进入编辑器预览会让 paused 保持 true，所以格回来时
    /// 它们的音组件已被销毁、永远不会重建。
    ///
    /// scrConductor.PlayHitTimes 从那些组件上读音量：
    ///
    ///     ffxSetHitsound setHitsound = scrFloor2.setHitsound;
    ///     if (setHitsound != null) { volume = setHitsound.volume; }
    ///
    /// 所以组件缺失会让音量留在类默认值 1f，每块砖
    /// 都按满音量播，无视作者写的 hitsoundVolume。在
    /// waitingforyou 上实测（第 1 格写的是 4）：全部 868 个格都没有组件，
    /// conductor 把每个音都按 1.000 排了队。
    ///
    /// 手工重建那两个纯音组件，而不是让整个
    /// 暂停烘焙跑起来：那次 return 之后的一切还包括相机、滤镜、
    /// 闪烁和其他视觉特效，而那些正是求值器已经
    /// 在写的通道。ffxSetHitsound 和 ffxSetHoldsound 没有 Update，也不碰
    /// 渲染，所以不可能和它竞争。
    /// </summary>
    internal static class ApplyEventsToFloorsPatch
    {
        private const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // scrController.paused 的 setter 还会重映射输入，对只持续一次调用的
        // 翻转来说开销太大，所以直接写后备字段。
        private static readonly FieldInfo ControllerPausedField =
            typeof(scrController).GetField("_paused", Flags);

        private static bool pausedWasCleared;
        private static bool pausedSavedValue;

        /// <summary>
        /// 预览播放时清掉 scrController.paused 再烘焙，这样
        /// 结果和游戏在真实播放中产生的一致。
        ///
        /// 这个旗标在 ApplyEventsToFloors 内部决定两件独立的事，工作台
        /// 在它的时钟运行时两者都要「正在播放」的那个答案：
        ///
        ///   PositionTrack 在旗标清除时丢掉它的仅限编辑器偏移
        ///     （"if ((!editorOnly || !paused) &amp;&amp; editorOnly) break"），这正是真游戏
        ///     的行为：仅限编辑器的偏移在编辑器里显示，
        ///     播放一开始就没了。
        ///
        ///   "if (paused) return" 之后的一切——谱面携带的每个 ffxPlusBase，
        ///   包括打击音——都只在旗标清除时才构建。
        ///
        /// 时钟停止时编辑器自己的值不动，所以偏移
        /// 按关卡编辑器显示的样子保持可见。这样缺掉的组件
        /// 无所谓：停止的预览本来就会暂停监听器。
        /// </summary>
        private static bool Prefix()
        {
            pausedWasCleared = false;
            if (!EditorPlaybackBridge.IsPreviewClockRunning)
            {
                return true;
            }

            scrController controller = scrController.instance;
            if (controller == null || ControllerPausedField == null)
            {
                return true;
            }

            bool current;
            try
            {
                current = (bool)ControllerPausedField.GetValue(controller);
            }
            catch (Exception exception)
            {
                ReportError("read paused", exception);
                return true;
            }

            if (!current)
            {
                return true;
            }

            pausedSavedValue = true;
            pausedWasCleared = true;
            try
            {
                ControllerPausedField.SetValue(controller, false);
            }
            catch (Exception exception)
            {
                pausedWasCleared = false;
                ReportError("clear paused", exception);
            }

            return true;
        }

        // 用 finalizer 而不是 postfix，因为原版抛异常时 postfix 会被跳过，
        // 那会让编辑器在剩下的会话里一直处于未暂停状态。
        // 原样返回异常，让它继续往外传播。
        private static Exception Finalizer(Exception __exception)
        {
            if (!pausedWasCleared)
            {
                return __exception;
            }

            pausedWasCleared = false;
            scrController controller = scrController.instance;
            if (controller == null || ControllerPausedField == null)
            {
                return __exception;
            }

            try
            {
                ControllerPausedField.SetValue(controller, pausedSavedValue);
            }
            catch (Exception exception)
            {
                ReportError("restore paused", exception);
            }

            return __exception;
        }

        private static void ReportError(string what, Exception exception)
        {
            if (Main.Logger != null)
            {
                Main.Logger.Error("[Bake] failed to " + what + ": " + exception.Message);
            }
        }

        /// <summary>
        /// 重建暂停烘焙丢掉的音组件。
        ///
        /// ApplyEventsToFloors 会销毁每个格上的每个 ffxPlusBase，然后把它
        /// 建回来，但 scrController.paused 一旦置位它就提前返回，而谱面携带的
        /// 每个组件都创建在那次 return 之后。scrConductor.PlayHitTimes
        /// 从组件上读每格的音量：
        ///
        ///     ffxSetHitsound setHitsound = scrFloor2.setHitsound;
        ///     if (setHitsound != null) { volume = setHitsound.volume; }
        ///
        /// 所以缺一个就会把每块砖钉在类默认值 1f。在
        /// waitingforyou 上实测（第 1 格写的是 4）：全部 868 个格都没有组件，
        /// conductor 把每个音都按 1.000 排了队。
        ///
        /// 这一段保留，尽管现在播放态的烘焙会自己构建组件：对没有
        /// 仅限编辑器 PositionTrack 的谱面，重新烘焙会被跳过，而那些
        /// 谱面同样需要它们的音组件。在播放态烘焙上这是
        /// 空操作——prefix 已经清掉了 paused，所以下面的守卫会拒绝。
        /// </summary>
        private static void Postfix(
            List<scrFloor> floors,
            ADOFAI.LevelData levelData,
            scrLevelMaker lm,
            List<ADOFAI.LevelEvent> events)
        {
            try
            {
                scrController controller = scrController.instance;
                if (controller == null || !controller.paused
                    || floors == null || levelData == null || events == null)
                {
                    return;
                }

                if (scrConductor.instance == null || scrConductor.instance.song == null)
                {
                    return;
                }

                float bpm = levelData.bpm;
                float pitch = scrConductor.instance.song.pitch;
                int restored = 0;
                for (int i = 0; i < events.Count; i++)
                {
                    ADOFAI.LevelEvent levelEvent = events[i];
                    if (levelEvent == null || !levelEvent.active)
                    {
                        continue;
                    }

                    if (levelEvent.eventType != ADOFAI.LevelEventType.SetHitsound
                        && levelEvent.eventType != ADOFAI.LevelEventType.SetHoldSound)
                    {
                        continue;
                    }

                    int floorIndex = levelEvent.floor;
                    if (floorIndex < 0 || floorIndex >= floors.Count)
                    {
                        continue;
                    }

                    scrFloor floor = floors[floorIndex];
                    if (floor == null)
                    {
                        continue;
                    }

                    // 两种类型都是独占的，所以编辑器每格最多持有一个。
                    // SetHitsound 的解码不是幂等的——它会 AddComponent 出
                    // 第二份——所以已有的那个必须留着不动。
                    GameObject target = floor.gameObject;
                    ffxPlusBase effect = levelEvent.eventType == ADOFAI.LevelEventType.SetHitsound
                        ? (ffxPlusBase)target.GetComponent<ffxSetHitsound>()
                        : (ffxPlusBase)target.GetComponent<ffxSetHoldsound>();
                    if (effect == null)
                    {
                        effect = levelEvent.eventType == ADOFAI.LevelEventType.SetHitsound
                            ? (ffxPlusBase)target.AddComponent<ffxSetHitsound>()
                            : (ffxPlusBase)target.AddComponent<ffxSetHoldsound>();
                    }

                    effect.floorID = floorIndex;
                    effect.floors = floors;
                    effect.crotchet = 60f / (bpm * pitch * floor.speed);
                    effect.Decode(levelEvent);
                    effect.sourceLevelEvent = levelEvent;
                    if (!floor.plusEffects.Contains(effect))
                    {
                        floor.plusEffects.Add(effect);
                    }

                    restored++;
                }

                if (restored > 0)
                {
                    Main.Logger.Log("[HitSound] restored " + restored
                        + " sound component(s) the paused pass dropped.");
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[HitSound] restore failed: " + exception);
            }
        }
    }
}