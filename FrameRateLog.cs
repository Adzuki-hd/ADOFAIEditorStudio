using System;
using System.Text;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 帧率跳动日志：设置 → 开发者里打开后，**每秒往日志写三行**。
    ///
    /// 为什么不能直接用 `PreviewDiagnostics` 那套：
    /// 那份报表是「120 帧 / 2 秒一行，按整段平均」的，回答的是"这段时间
    /// 哪一段最贵"。可它会把抖动抹平 —— 一个 2 秒窗口里如果有 3 帧掉到
    /// 40ms，平均下来只多 1ms，报表上看不出任何异常。而做谱时最难受的
    /// 恰恰是抖动：画面一顿一顿的，平均帧率却漂漂亮亮。
    ///
    /// 所以这一份刻意用**很短的窗口（1/3 秒）**，并且每行都带
    /// **本段最差的那一帧**和**掉帧计数**。三行一秒，是"能看清抖动、
    /// 又不至于把日志刷爆"的折中。
    ///
    /// 每行内容：
    ///   · 本段平均帧率 / 平均帧耗时 / 本段最差帧 / 掉帧数
    ///   · **当前格**（取 `scrFloor.seqID`，和标尺上的格号同一套编号）
    ///   · 当前时间、播放状态、工作台是否可见
    ///
    /// 后面这三样不是凑数的：同一个帧率在「播放中 / 暂停 / 工作台关着」
    /// 三种情况下含义完全不同 —— 不标出来，这个数字就没法归类，前面
    /// 排查时正是靠这个才没有把不同工况的样本混在一起平均。
    /// </summary>
    internal static class FrameRateLog
    {
        /// <summary>每行覆盖的时长：1/3 秒，也就是每秒三行。</summary>
        private const float SliceSeconds = 1f / 3f;

        /// <summary>两帧间隔超过这个时间就算掉帧（60 帧/秒的预算，16.7ms）。</summary>
        private const float SlowFrameSeconds = 1f / 60f;

        /// <summary>
        /// 日志是否处于「已开始累计」的状态。
        /// 开关刚打开时先把累加器清零，否则第一行会把开关打开之前
        /// 那段时间（可能是好几分钟）的统计一起算进来。
        /// </summary>
        private static bool armed;

        /// <summary>本段已经累计的时长。</summary>
        private static float clock;

        /// <summary>本段的帧数。</summary>
        private static int frames;

        /// <summary>本段帧间隔之和（秒）。</summary>
        private static float sum;

        /// <summary>本段最差的那一帧（秒）。</summary>
        private static float worst;

        /// <summary>本段掉帧数（帧间隔超过 16.7ms 的帧）。</summary>
        private static int slowFrames;

        /// <summary>每行现拼，避免每帧分配字符串。</summary>
        private static readonly StringBuilder builder = new StringBuilder(192);

        /// <summary>
        /// 每帧调一次。开关关着时只有一次布尔判断，可以放心挂在
        /// `UpdateCore` 的主干上。
        /// </summary>
        internal static void Tick(bool workspaceVisible)
        {
            Settings settings = Main.Settings;
            if (settings == null || !settings.FrameRateLog)
            {
                armed = false;
                return;
            }

            if (!armed)
            {
                armed = true;
                clock = 0f;
                frames = 0;
                sum = 0f;
                worst = 0f;
                slowFrames = 0;
            }

            // 用 unscaled 的：预览会在某些时候改 timeScale，
            // 用 deltaTime 的话"游戏变慢"会被算成"帧率变高"。
            float delta = Time.unscaledDeltaTime;
            frames++;
            sum += delta;
            if (delta > worst)
            {
                worst = delta;
            }
            if (delta > SlowFrameSeconds)
            {
                slowFrames++;
            }
            clock += delta;
            if (clock < SliceSeconds)
            {
                return;
            }

            Emit(workspaceVisible);

            clock = 0f;
            frames = 0;
            sum = 0f;
            worst = 0f;
            slowFrames = 0;
        }

        private static void Emit(bool workspaceVisible)
        {
            float average = sum / Mathf.Max(1, frames);
            float fps = average > 0.0001f ? 1f / average : 0f;

            builder.Length = 0;
            builder.Append("[帧率] ")
                .Append(fps.ToString("0.0")).Append("fps 帧")
                .Append((average * 1000f).ToString("0.0")).Append("ms 最差")
                .Append((worst * 1000f).ToString("0.0")).Append("ms 掉帧")
                .Append(slowFrames).Append('/').Append(frames);

            AppendChartPosition();

            builder.Append(workspaceVisible ? " 工作台可见" : " 工作台关闭");

            Log(builder.ToString());
        }

        /// <summary>
        /// 接上「当前格 + 时间 + 播放状态」。
        ///
        /// 单独 try 一段：这段要碰游戏对象，万一在场景切换的中间态取到
        /// null，也不能把整行帧率日志带没 —— 帧率本身是能算出来的，
        /// 位置取不到就少这一段而已。
        /// </summary>
        private static void AppendChartPosition()
        {
            try
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null || editor.floors == null || editor.floors.Count == 0)
                {
                    return;
                }

                EditorPlaybackSnapshot snapshot = EditorPlaybackBridge.ReadSnapshot();
                float time = snapshot.CurrentTime;
                int index = FindFloorAtOrBefore(editor.floors, time);
                scrFloor floor = editor.floors[index];

                builder.Append(" | 格").Append(floor == null ? index : floor.seqID)
                    .Append('/').Append(editor.floors.Count)
                    .Append(" t=").Append(time.ToString("0.000")).Append('s');

                switch (snapshot.State)
                {
                    case EditorPlaybackState.Playing:
                        builder.Append(" 播放中");
                        break;
                    case EditorPlaybackState.Paused:
                        builder.Append(" 暂停");
                        break;
                    default:
                        builder.Append(" 编辑");
                        break;
                }
            }
            catch (Exception)
            {
                // 位置取不到就不写这一段。帧率是这一行的主题，不能因为
                // 附属信息失败就整行丢掉。
            }
        }

        /// <summary>
        /// 二分找「时间落在哪一格」。判据和 `EditorPlaybackBridge` /
        /// `PreviewTimelineContext` 里那两份一致：用 `floor.entryTime`
        /// （谱面时间）和传入时间比较。三处保持同一个口径，
        /// 否则日志里的格号会和时间轴播放头对不上。
        /// </summary>
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

        private static void Log(string message)
        {
            try
            {
                if (Main.Logger != null)
                {
                    Main.Logger.Log(message);
                }
            }
            catch (Exception)
            {
                // 日志系统本身出问题时不要再抛，否则会打断 Update。
            }
        }
    }
}
