using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine;
using UnityEngine.Audio;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// `PlaySound` —— 在谱面的某一刻播一段音效。
    ///
    /// 对照原生 `ffxPlaySound`：
    ///
    ///   · `hitsound` 字段是 `HitSound` 枚举名 → 播打拍音（`"snd" + 枚举名`），
    ///     并按 `gc.hitSoundOffsets` 里那个音效自带的偏移做提前量；
    ///   · 否则当作自定义音效键 → 去 `audioManager.audioLib` 里按
    ///     `键名 + "*external"` 找（那条后缀是原生 `Decode` 加的）。
    ///
    /// 字段单位（原生 `Decode` 里除掉的）：
    ///   `offset`       毫秒 → 秒
    ///   `playDuration` 毫秒 → 秒
    ///   `pitch`        百分比 → 倍数
    ///   `hitsoundVolume` 百分比 → 0~1
    ///
    /// **只在播放时触发，拖播放头不响。** 这条和 `EmitParticle` 是同一个理由
    /// （见 ParticlePreviewDomain 里 `ApplyEmit` 那段长注释）：
    /// 拖拽浏览时每帧都可能跨过事件，声音会糊成一片；而且用户拖定位的
    /// 目的通常是"看画面"，不是"听音效"。
    ///
    /// 触发判据也沿用粒子那套：`previousTime &lt; 起点 &lt;= 当前时间`。
    /// 回拖到事件之前会清闩，再往前跨过时自然重播。
    /// </summary>
    internal sealed class PlaySoundPreviewDomain : IPreviewDomain
    {
        /// <summary>一条 PlaySound 事件解析后的播放参数。</summary>
        private sealed class SoundKeyframe
        {
            internal double StartTime;
            internal bool UseHitSound;
            internal string SoundId;
            internal double Offset;
            internal double PlayDuration;
            internal float Pitch;
            internal float Volume;
        }

        /// <summary>
        /// 判定「这一步跨过了这条事件」时允许的最大时间跨度。
        ///
        /// 拖得飞快、或者直接跳到事件之后，原生也不会补这一声
        /// （音频早该播完了）。和粒子那条用同一个思路。
        /// </summary>
        private const float CrossingMaxStep = 0.35f;

        /// <summary>
        /// 提前多久把音效排进音频时间轴。
        ///
        /// 只在「跨过事件那一帧」才调 Play 的话，最早也只能从那一帧的 dsp
        /// 时刻开始 —— 天然晚一帧，再叠上缓冲区对齐就成了听得出的延迟。
        ///
        /// 0.2 秒足够覆盖几帧求值 + 一次音频缓冲区，而 `PlayScheduled`
        /// 收的是绝对时刻，提前多久调都不影响它什么时候响。
        /// </summary>
        private const float PreScheduleAhead = 0.2f;

        private readonly List<SoundKeyframe> keyframes = new List<SoundKeyframe>();

        /// <summary>
        /// 已经排过的事件下标。
        ///
        /// 用集合而不是单个下标：预排程是按「前 0.2 秒内的所有事件」批量排的，
        /// 同一帧可能排好几条，单值会互相覆盖、导致重复播放。
        /// </summary>
        private readonly HashSet<int> scheduled = new HashSet<int>();

        /// <summary>上一次求值的时间。判"跨过"要用。</summary>
        private float previousTime = float.NaN;

        /// <summary>混音组。取不到就退回默认组，不影响播放。</summary>
        private static AudioMixerGroup group;
        private static bool groupResolved;

        public string Name
        {
            get { return "PlaySound"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            scheduled.Clear();
            previousTime = float.NaN;

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.PlaySound);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];

                string hitsound = context.GetValue(
                    levelEvent, "hitsound", string.Empty) ?? string.Empty;
                bool useHitSound;
                string soundId;
                // 原生：能 Parse 成 HitSound 枚举就播打拍音，否则当自定义键。
                if (!string.IsNullOrEmpty(hitsound)
                    && Enum.IsDefined(typeof(HitSound), hitsound))
                {
                    useHitSound = true;
                    soundId = "snd" + hitsound;
                }
                else
                {
                    useHitSound = false;
                    soundId = hitsound + "*external";
                }

                keyframes.Add(new SoundKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    UseHitSound = useHitSound,
                    SoundId = soundId,
                    // 原生这些字段都是毫秒 / 百分比，这里照它的除法。
                    Offset = context.GetValue(levelEvent, "offset", 0) / 1000.0,
                    PlayDuration = context.GetValue(levelEvent, "playDuration", 0) / 1000.0,
                    Pitch = context.GetValue(levelEvent, "pitch", 100) / 100f,
                    Volume = context.GetValue(levelEvent, "hitsoundVolume", 50) / 100f
                });
            }

            PreviewStableSort.Sort(keyframes,
                (left, right) => left.StartTime.CompareTo(right.StartTime));
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            float time = frame.Time;

            // **只在播放时响。** 拖动浏览不播（见类注释）。
            bool playing = IsPlaying();

            if (!playing)
            {
                // 不在播放：记住时间，并把"已排过"清掉 ——
                // 下次播放时同一条事件要能重新排。
                //
                // **只在真的没在播时清。** 播放中 `IsPlaying()` 偶尔抖一帧
                // 为 false 也没关系：那一帧清掉之后下一帧会重新排，
                // 最坏是重复一次。不清的话暂停再播就再也不响了。
                previousTime = time;
                scheduled.Clear();
                return;
            }

            if (float.IsNaN(previousTime))
            {
                previousTime = time;
                return;
            }

            float step = time - previousTime;

            // **时间倒退就清闩**，下次再往前经过时重新排一次。
            if (step < 0f)
            {
                previousTime = time;
                scheduled.Clear();
                return;
            }

            // 跳得太远（拖得快 / 直接定位）就不补 —— 那一声早该播完了。
            if (step > CrossingMaxStep)
            {
                previousTime = time;
                return;
            }

            for (int i = 0; i < keyframes.Count; i++)
            {
                SoundKeyframe key = keyframes[i];

                // **提前排程，而不是等跨过才播。**
                //
                // 只在 `previousTime < 起点 <= time` 那一帧才调 Play 的话，
                // 音频最早也只能从"那一帧的 dsp 时刻"开始 —— 天然晚一帧
                // （16ms 以上），再叠上缓冲区对齐就成了听得出来的延迟。
                //
                // 改成：在事件到达前 <see cref="PreScheduleAhead"/> 秒就把它
                // 排到 dsp 时间轴上的**精确时刻**。`PlayScheduled` 收的是
                // 绝对时刻，所以提前多久调不影响它什么时候响。
                double remaining = key.StartTime - time;
                bool shouldSchedule = remaining <= PreScheduleAhead && remaining > -CrossingMaxStep;
                if (!shouldSchedule)
                {
                    continue;
                }

                // 同一条事件只排一次。
                if (scheduled.Contains(i))
                {
                    continue;
                }
                scheduled.Add(i);
                Play(key);
            }

            previousTime = time;
        }

        private static bool IsPlaying()
        {
            // **用 AES 自己的预览时钟，不是 `conductor.song.isPlaying`。**
            //
            // 实测（`[PlaySound] 状态` 探针）：按空格播放、谱面时间从 0 一路
            // 推进到 5.5 秒，`conductor.song.isPlaying` 却全程是 False。
            // 因为工作台的预览不调原版 `Play()`，歌曲是它自己排程的，
            // 那颗 AudioSource 的状态和预览是不是在跑没有必然关系。
            //
            // `IsPreviewClockRunning` 才是"画面现在在动"的那个判据。
            bool clockRunning = EditorPlaybackBridge.IsPreviewClockRunning;

            // 兜底：原生 transport 真的在跑时（工作台没接管），
            // 也要能响。
            if (!clockRunning)
            {
                scrConductor conductor = ADOBase.conductor;
                if (conductor != null && conductor.song != null
                    && conductor.song.isPlaying)
                {
                    return true;
                }
            }
            return clockRunning;
        }

        /// <summary>
        /// 把「谱面时间」换算成「音频 dsp 时刻」。
        ///
        /// **完全照原生 `ffxPlaySound.StartEffect` 那一行：**
        ///
        ///     num = conductor.dspTimeSongPosZero + startTime / song.pitch - offset
        ///
        /// 而 `dspTimeSongPosZero`（原生定义）是：
        ///
        ///     dspTimeSong + addoffset / song.pitch
        ///
        /// 展开即：
        ///
        ///     dsp = dspTimeSong + addoffset / pitch + scene / pitch
        ///
        /// **注意不要用 `scrConductor` 那条反解式**
        /// （`scene = (dsp - dspTimeSong - cal_i) * pitch - addoffset`）：
        /// 那条多了一个 `cal_i`、`addoffset` 的除法位置也不同。它是给
        /// 「从 dsp 反推场景时间」用的，拿它反解会整体偏掉
        /// `cal_i - addoffset/pitch + addoffset`。
        ///
        /// `offset` 是事件自己的「提前量」（毫秒，负数才提前），
        /// 由调用方在拿到结果之后再减。
        /// </summary>
        private static bool TryMapToDspTime(double sceneTime, out double dspTime)
        {
            dspTime = 0.0;

            scrConductor conductor = ADOBase.conductor;
            if (conductor == null || conductor.song == null)
            {
                return false;
            }

            float pitch = conductor.song.pitch;
            if (Mathf.Approximately(pitch, 0f))
            {
                return false;
            }

            try
            {
                // 原样照抄：dspTimeSongPosZero + startTime / pitch
                dspTime = conductor.dspTimeSongPosZero + sceneTime / pitch;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Play(SoundKeyframe key)
        {
            if (string.IsNullOrEmpty(key.SoundId))
            {
                return;
            }

            try
            {
                AudioMixerGroup mixerGroup = ResolveGroup();
                if (mixerGroup == null)
                {
                    return;
                }

                // **按谱面时间反算 dsp 时刻，提前排程。**
                //
                // 以前传 `dspTime + 0.05`：那 50ms 是为了躲开音频缓冲区量化
                // （项目里实测过，不加有一半概率晚 42.7ms），代价是**所有音效
                // 一律晚 50ms**。用户听到的就是"有延迟"。
                //
                // 现在不再手加余量：把事件在谱面上的时刻换算成 dsp 时刻，
                // 直接排到那个点上 —— 音频引擎自己按缓冲区对齐，
                // 但至少不会因为我们多等一帧而整体后移。
                double scheduled;
                double targetDsp;
                if (TryMapToDspTime(key.StartTime, out targetDsp))
                {
                    // 原生那句是 `dspTimeSongPosZero + startTime/pitch - offset`，
                    // 这里补上 offset（事件自己的提前量）。
                    targetDsp -= key.Offset;
                    // 排程时刻不能是过去。已经过了就立刻播。
                    double now = AudioSettings.dspTime;
                    scheduled = targetDsp > now ? targetDsp : now;
                }
                else
                {
                    scheduled = AudioSettings.dspTime;
                }

                if (key.UseHitSound && ADOBase.gc != null
                    && ADOBase.gc.hitSoundOffsets != null)
                {
                    // 打拍音自身带一个偏移：原生把它减掉、再把采样位置往前挪。
                    double soundOffset = 0.0;
                    HitSound parsed;
                    if (Enum.TryParse(key.SoundId.Substring(3), out parsed))
                    {
                        ADOBase.gc.hitSoundOffsets.TryGetValue(parsed, out soundOffset);
                    }
                    scheduled -= soundOffset;
                    if (scheduled < AudioSettings.dspTime)
                    {
                        scheduled = AudioSettings.dspTime;
                    }
                }

                AudioSource source = AudioManager.Play(key.SoundId, scheduled,
                    mixerGroup, Mathf.Clamp01(key.Volume));
                if (source == null)
                {
                    return;
                }

                if (key.PlayDuration > 0.0)
                {
                    source.SetScheduledEndTime(scheduled + key.PlayDuration);
                }
                source.pitch = Mathf.Approximately(key.Pitch, 0f) ? 1f : key.Pitch;
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/PlaySound] 播放 " + key.SoundId
                    + " 失败：" + exception.Message);
            }
        }

        private static AudioMixerGroup ResolveGroup()
        {
            if (groupResolved && group != null)
            {
                return group;
            }
            try
            {
                group = RDUtils.GetMixerGroup("ConductorPlaySound");
            }
            catch
            {
                group = null;
            }
            groupResolved = true;
            return group;
        }

        public void EndSession()
        {
            keyframes.Clear();
            scheduled.Clear();
            previousTime = float.NaN;
        }
    }
}
