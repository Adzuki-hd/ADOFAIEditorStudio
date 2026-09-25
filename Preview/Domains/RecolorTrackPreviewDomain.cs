using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// RecolorTrack 的绝对时间求值。它刻意写回原来的 scrFloor
    /// renderer，而不是另画替代的格。
    /// </summary>
    internal sealed class RecolorTrackPreviewDomain : IPreviewDomain
    {
        private sealed class RecolorEvent : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal int EventFloor;
            internal int StartFloor;
            internal int EndFloor;
            internal int GapLength;
            internal Color TrackColor;
            internal Color SecondaryTrackColor;
            internal float AnimationDuration;
            internal TrackColorType ColorType;
            internal TrackColorPulse Pulse;
            internal int PulseLength;
            internal TrackStyle Style;
            internal float GlowIntensity;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<RecolorEvent> events = new List<RecolorEvent>();

        /// <summary>
        /// 能影响每一格的事件，在 Analyze 里预先算好。以前对每个格都
        /// 搜完整事件列表，等于每帧每个格都走一遍 events.Count；
        /// 谱面上 163 个 recolor 事件铺在 1133 个格上，就是每帧约 18.5 万次
        /// 范围判断，实测约 3ms。
        /// </summary>
        private List<RecolorEvent>[] eventsByFloor = new List<RecolorEvent>[0];

        // 本域上次为每个格写下的颜色和 glow。用来跳过值没变的写入
        // ——但只在颜色来源不是动画的时候。
        //
        // scrFloor.Update 每帧都从它烤入的 specialColor* 字段写
        // floorRenderer.color，覆盖 Glow / Rainbow / Blink / Switch / Volume（这个 switch
        // 只要 specialColorType 不是 Single 或 Stripes 就会跑，而那个判断条件
        // 恒为真）。对这些格，游戏下一帧就会覆盖掉这里写的任何值，
        // 所以缓存的值不再描述格上实际是什么，跳过写入
        // 会让颜色晚一帧才掉。
        // Single 和 Stripes 不在那个 switch 里，所以没有别的东西碰它们，
        // 缓存是精确的。在 Digital Warrior 谱面上，106 个 recolor 事件里有 74 个是 Single，
        // 所以省下来的开销落在安全的一侧。
        private Color[] appliedColor = new Color[0];
        private float[] appliedGlow = new float[0];
        private bool[] appliedValid = new bool[0];

        /// <summary>
        /// 至少被一个事件覆盖的格。只求这些，能让这一趟的开销
        /// 与谱面的 recolor 事件数成正比，而不是与谱面长度成正比。
        /// </summary>
        private readonly List<int> floorsWithEvents = new List<int>();

        /// <summary>
        /// 上次推给每个格的 TrackStyle，没有则 -1。SetTrackStyle 要写十个
        /// 材质属性和网格尺寸，而它只取决于样式
        /// 和格自己的尺寸——跟相机、跟时间都无关——所以只在
        /// 样式真正变化时才需要跑。颜色不一样：它每帧
        /// 都插值，每一趟都还得写。
        /// </summary>
        private int[] appliedStyle = new int[0];

        /// <summary>
        /// 每格的颜色和 glow 在加载时被游戏烤入的样子，在 Analyze 里
        /// 捕获一次。这才是正确的基线和还原目标：游戏
        /// 已经把 ColorTrack 和别的所有颜色来源应用到了格上，所以拿
        /// 关卡设置的默认值既会跟屏上的东西不一致，在没有
        /// 事件生效时还会把烤入的颜色覆盖成一个素色。
        /// </summary>
        private Color[] floorBaseColor = new Color[0];
        private float[] floorBaseGlow = new float[0];

        private Color defaultSecondaryColor = Color.white;
        private TrackColorType defaultColorType;
        private TrackColorPulse defaultPulse;
        private int defaultPulseLength = 1;
        private TrackStyle defaultStyle;
        private float defaultAnimationDuration = 1f;
        private float defaultGlowIntensity = 100f;
        private Color defaultTrackColor = Color.white;


        public string Name
        {
            get { return "RecolorTrack"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            events.Clear();
            LevelData levelData = scnGame.instance == null ? null : scnGame.instance.levelData;
            if (levelData != null)
            {
                defaultSecondaryColor = levelData.secondaryTrackColor;
                defaultColorType = levelData.trackColorType;
                defaultPulse = levelData.trackColorPulse;
                defaultPulseLength = Mathf.Max(1, levelData.trackPulseLength);
                defaultStyle = levelData.trackStyle;
                defaultAnimationDuration = Mathf.Max(0.0001f, levelData.trackColorAnimDuration);
                defaultGlowIntensity = levelData.trackGlowIntensity;
                defaultTrackColor = levelData.trackColor;
            }

            CaptureFloorBaselines(context);

            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.RecolorTrack);
            for (int i = 0; i < source.Count; i++)
            {
                PreviewEventSnapshot levelEvent = source[i];
                int startFloor;
                int endFloor;
                ResolveRange(context, levelEvent, out startFloor, out endFloor);
                events.Add(new RecolorEvent
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    EventFloor = Mathf.Clamp(levelEvent.Floor, 0, context.Floors.Count - 1),
                    StartFloor = startFloor,
                    EndFloor = endFloor,
                    GapLength = Mathf.Max(0, context.GetValue(levelEvent, "gapLength", 0)),
                    TrackColor = ReadColor(levelEvent, "trackColor", BaselineColor(startFloor)),
                    SecondaryTrackColor = ReadColor(levelEvent, "secondaryTrackColor", defaultSecondaryColor),
                    AnimationDuration = Mathf.Max(0.0001f,
                        context.GetValue(levelEvent, "trackColorAnimDuration", defaultAnimationDuration)),
                    ColorType = context.GetValue(levelEvent, "trackColorType", defaultColorType),
                    Pulse = context.GetValue(levelEvent, "trackColorPulse", defaultPulse),
                    PulseLength = Mathf.Max(1, context.GetValue(levelEvent, "trackPulseLength", defaultPulseLength)),
                    Style = context.GetValue(levelEvent, "trackStyle", defaultStyle),
                    GlowIntensity = context.GetValue(levelEvent, "trackGlowIntensity", defaultGlowIntensity)
                });
            }

            PreviewStableSort.Sort(events, (left, right) => left.Start.CompareTo(right.Start));
            BuildFloorBuckets(context.Floors.Count);
            LogFloorColourState(context);
            Main.Logger.Log("[StaticPreview/RecolorTrack] analyzed " + events.Count + " events.");
        }

        /// <summary>
        /// 只读。把少数几个格上烤入的颜色动画字段打出来，这样一条
        /// 看起来平板的轨道能追到原因：这些格到底有没有被赋予
        /// Glow/Pulse 动画，用的什么颜色、时长和偏移。
        /// </summary>
        private static void LogFloorColourState(PreviewTimelineContext context)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            int count = context.Floors.Count;
            int[] probes = { 0, 1, 2, 100, 185, 186, 187, 248, 249, 328, 400 };
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("[FloorColour] floors=").Append(count);
            for (int i = 0; i < probes.Length; i++)
            {
                int index = probes[i];
                if (index >= count)
                {
                    continue;
                }
                scrFloor floor = context.GetFloorBinding(index);
                if (floor == null)
                {
                    continue;
                }
                builder.Append(" | f").Append(index)
                    .Append(" type=").Append(floor.specialColorType)
                    .Append(" pulse=").Append(floor.specialColorPulse)
                    .Append(" dur=").Append(floor.specialAnimDuration.ToString("0.00"))
                    .Append(" c1=").Append(ToHex(floor.specialColor1))
                    .Append(" c2=").Append(ToHex(floor.specialColor2))
                    .Append(" now=")
                    .Append(floor.floorRenderer == null ? "-" : ToHex(floor.floorRenderer.color));
            }
            Main.Logger.Log(builder.ToString());
        }

        private static string ToHex(Color color)
        {
            return ((int)(Mathf.Clamp01(color.r) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.g) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.b) * 255f)).ToString("x2");
        }

        private void CaptureFloorBaselines(PreviewTimelineContext context)
        {
            int count = context.Floors.Count;
            floorBaseColor = new Color[count];
            floorBaseGlow = new float[count];
            for (int i = 0; i < count; i++)
            {
                scrFloor floor = context.GetFloorBinding(i);
                // 基线取谱面设置，而不是 floorRenderer.color 的快照。
                //
                // 快照取决于 Analyze 跑的那一刻游戏有没有把轨道色烤进格子里，
                // 没烤到就抓到白色/默认值，之后 Lerp(base, target, eased) 在
                // eased < 1 的区间就会整体偏亮——而且随刷新时机时好时坏。
                // 谱面设置是确定的，不受时机影响。
                floorBaseColor[i] = defaultTrackColor;
                floorBaseGlow[i] = defaultGlowIntensity / 100f;
            }
        }

        private Color BaselineColor(int floorIndex)
        {
            if (floorBaseColor.Length == 0)
            {
                return Color.white;
            }
            return floorBaseColor[Mathf.Clamp(floorIndex, 0, floorBaseColor.Length - 1)];
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            // 只管真正被某个事件覆盖的格。走遍全部 1133 个格，会让
            // 这一帧对绝大多数根本没有 recolor 事件的格调用 FindLastAffectingEvent，
            // 而每次调用都只是返回 null。
            for (int listIndex = 0; listIndex < floorsWithEvents.Count; listIndex++)
            {
                int floorIndex = floorsWithEvents[listIndex];
                scrFloor floor = frame.Context.GetFloorBinding(floorIndex);
                if (floor == null || floor.floorRenderer == null)
                {
                    continue;
                }

                RecolorEvent current = FindLastAffectingEvent(floorIndex, frame.Time);
                if (current == null)
                {
                    // 没有东西拥有这一格的颜色，所以别管它。
                    //
                    // 这一格每帧自己驱动颜色，来源是烤入的
                    // specialColor/Pulse 字段（scrFloor.Update），Glow 和
                    // Pulse 轨道就是这么来的。在这里写任何东西——关卡默认值，
                    // 甚至只是某一帧的快照——都会覆盖那个动画，
                    // 把轨道冻成一片平色。
                    continue;
                }

                float ratio = current.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((frame.Time - (float)current.StartTime) / current.Duration);
                float eased = PreviewEase.EasedValue(0f, 1f, ratio, current.Ease);
                Color target = GenerateColor(floorIndex, current, frame.Time);
                Color baseline = floorBaseColor[Mathf.Clamp(floorIndex, 0, floorBaseColor.Length - 1)];
                Color color = Color.LerpUnclamped(baseline, target, eased);

                // SetTrackStyle 是这里最贵的调用（十次材质写入加
                // 网格尺寸）。它只取决于样式，而样式是个离散值，
                // 所以只在这一格的样式真正变化时才跑。
                int styleKey = (int)current.Style;
                bool styleChanged = floorIndex >= appliedStyle.Length
                    || appliedStyle[floorIndex] != styleKey;
                if (styleChanged)
                {
                    if (floorIndex < appliedStyle.Length)
                    {
                        appliedStyle[floorIndex] = styleKey;
                    }
                    floor.SetTrackStyle(current.Style, false, null);
                }

                float glow = Mathf.LerpUnclamped(
                    floorBaseGlow[Mathf.Clamp(floorIndex, 0, floorBaseGlow.Length - 1)],
                    current.GlowIntensity / 100f,
                    eased);

                // 见 appliedColor 的声明：游戏每帧从它自己的烤入动画
                // 改写掉的值不能缓存，所以只允许 scrFloor.Update
                // 不碰的那些类型跳过写入。
                //
                // 样式变化也强制写入。SetTrackStyle 会换材质，而
                // 换材质不保证保住它的颜色，所以把一个没变的颜色
                // 当成「已经应用过」，可能让格停在新样式的
                // 默认值上。样式变化很少见，强制写的代价可以忽略。
                // **接管必须在缓存判断之前。**
                //
                // 游戏的辉度 tween 是每次 StartEffect 重新建的，而预览
                // 拖动会重建特效——所以那条 tween 会反复复活。要是把接管
                // 放在下面那个「值没变就 continue」之后，值一稳定下来
                // 我们就再也不会去杀新生的 tween，辉度又会被改回去。
                //
                // 放在这里就是每帧都确认一次所有权：杀过即移出字典，
                // 之后的 TryGetValue 都是廉价失败。
                PreviewEase.TakeOverGlow(floor);

                bool cacheSafe = !styleChanged
                    && (current.ColorType == TrackColorType.Single
                        || current.ColorType == TrackColorType.Stripes);
                if (cacheSafe
                    && floorIndex < appliedValid.Length
                    && appliedValid[floorIndex]
                    && appliedColor[floorIndex] == color
                    && appliedGlow[floorIndex] == glow)
                {
                    continue;
                }

                floor.SetColor(color);
                floor.glowMultiplier = glow;


                // 让游戏自己去驱动这些格的颜色，而不是只把颜色写上去。
                //
                // 为什么必须这么做：scrFloor.Update 每帧都跑，并且从
                // specialColor1/2 + specialColorType + specialAnimDuration
                // 重算 floorRenderer.color（Glow/Rainbow/Blink/Switch/Volume
                // 全都走这条路）。只调 SetColor 的话，写进去的值下一帧就被
                // 那句重算覆盖掉——所以 Glow/Rainbow 型的 RecolorTrack
                // 看起来「完全没生效」，轨道一直停在烘焙时的旧色上。
                //
                // 正确做法是改 Update 的输入，而不是抢它的输出。
                // Update 里那句是：
                //
                //     float num = Time.time + specialTimeOffset;
                //     color = Lerp(specialColor1, specialColor2, f(num / duration));
                //
                // 把 specialTimeOffset 反解成能让 num 等于预览时刻的值，
                // Update 算出来的相位就和真实播放一致：
                //
                //     num = Time.time + (previewTime + 相位 - Time.time)
                //         = previewTime + 相位
                //
                // 相位项（每格在脉冲周期里错开的起点）照原生
                // scrFloor.ColorFloor 的公式来，见 PulsePhaseOffset。
                //
                // 颜色本身仍以我们求出的插值结果为准：SetColor 在上面已经
                // 写了精确值，Update 只是让它保持正确而不是漂回旧色。
                floor.specialColorType = current.ColorType;
                floor.specialColor1 = current.TrackColor;
                floor.specialColor2 = current.SecondaryTrackColor;
                floor.specialAnimDuration = Mathf.Max(0.0001f, current.AnimationDuration);
                floor.specialTimeOffset = frame.Time + PulsePhaseOffset(floorIndex, current)
                    - Time.time;

                if (cacheSafe && floorIndex < appliedValid.Length)
                {
                    appliedColor[floorIndex] = color;
                    appliedGlow[floorIndex] = glow;
                    appliedValid[floorIndex] = true;
                }
            }
        }

        public void EndSession()
        {
            events.Clear();
            appliedValid = new bool[0];
        }

        private void BuildFloorBuckets(int floorCount)
        {
            eventsByFloor = new List<RecolorEvent>[floorCount];
            for (int i = 0; i < floorCount; i++)
            {
                eventsByFloor[i] = new List<RecolorEvent>();
            }

            for (int i = 0; i < events.Count; i++)
            {
                RecolorEvent value = events[i];
                int stride = Mathf.Max(1, value.GapLength + 1);
                int first = Mathf.Clamp(value.StartFloor, 0, floorCount - 1);
                int last = Mathf.Clamp(value.EndFloor, 0, floorCount - 1);
                for (int floorIndex = first; floorIndex <= last; floorIndex += stride)
                {
                    eventsByFloor[floorIndex].Add(value);
                }
            }

            int bound = 0;
            floorsWithEvents.Clear();
            appliedStyle = new int[floorCount];
            appliedColor = new Color[floorCount];
            appliedGlow = new float[floorCount];
            appliedValid = new bool[floorCount];
            for (int i = 0; i < floorCount; i++)
            {
                appliedStyle[i] = -1;
                bound += eventsByFloor[i].Count;
                if (eventsByFloor[i].Count > 0)
                {
                    floorsWithEvents.Add(i);
                }
            }
            Main.Logger.Log("[StaticPreview/RecolorTrack] floor bindings=" + bound
                + " floorsTouched=" + floorsWithEvents.Count + ".");
        }

        private RecolorEvent FindLastAffectingEvent(int floorIndex, float time)
        {
            if (floorIndex < 0 || floorIndex >= eventsByFloor.Length)
            {
                return null;
            }

            List<RecolorEvent> candidates = eventsByFloor[floorIndex];
            int count = candidates.Count;
            if (count == 0)
            {
                return null;
            }

            // 二分查找最后一个 Start <= time 的候选。这个桶是用
            // `events` 建的，而 Analyze 在分桶前按起始时间对它做了稳定排序，
            // 所以起始时间是单调不减的，判定条件也是单调的。
            //
            // 这替代了从索引 0 开始的线性遍历。在 Digital Warrior 谱面上，
            // RecolorTrack 事件展开成 3517 个格上的 59644 个 (格, 事件) 绑定——
            // 平均每格约 17 个候选——而以前每帧对每一个都要遍历一遍，
            // 仅仅 106 个作者事件就实测每帧 2.2ms。
            if (candidates[0].Start > time)
            {
                return null;
            }

            int low = 0;
            int high = count - 1;
            while (low < high)
            {
                // 把中点往上偏，这样 low + 1 == high 时循环不会卡住。
                int mid = low + ((high - low) + 1) / 2;
                if (candidates[mid].Start <= time)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return candidates[low];
        }

        /// <summary>
        /// 这一格在脉冲周期里的相位偏移，不含已经走过的时间。
        ///
        /// 对应原生 scrFloor.ColorFloor 里对 specialTimeOffset 的赋值
        /// （Assembly-CSharp 里那段 switch (pulseType)），
        /// 让每一格在脉冲周期里各有一个错开的起点——这就是 Glow
        /// 轨道上那道沿轨道滚动的波纹。
        /// </summary>
        private static float PulsePhaseOffset(int floorIndex, RecolorEvent value)
        {
            switch (value.Pulse)
            {
                case TrackColorPulse.Forward:
                    return (1f - (floorIndex % value.PulseLength) / (float)value.PulseLength)
                        * value.AnimationDuration;
                case TrackColorPulse.Backward:
                    return (floorIndex % value.PulseLength) / (float)value.PulseLength
                        * value.AnimationDuration;
                default:
                    return 0f;
            }
        }

        private static Color GenerateColor(int floorIndex, RecolorEvent value, float currentTime)
        {
            int distance = floorIndex - value.EventFloor;
            // 相位原点取「事件起点 + 该格的脉冲错开量」，和下面写进
            // specialTimeOffset 的是同一个式子。两边必须一致：Update 每帧
            // 拿 specialTimeOffset 重算颜色，公式不同就会来回打架，
            // 表现为轨道闪烁。
            float pulseOffset = Mathf.Max(0f, currentTime - (float)value.StartTime)
                + PulsePhaseOffset(floorIndex, value);

            float phase = pulseOffset / value.AnimationDuration;
            switch (value.ColorType)
            {
                case TrackColorType.Stripes:
                    // 奇偶从事件所在格数起，与原生 ffxRecolorTrack
                    // 的 (seqID - start) % 2 一致。
                    // 用正模，避免事件格之前的格算出负数。
                    return (((distance % 2) + 2) % 2) == 0
                        ? value.TrackColor : value.SecondaryTrackColor;
                case TrackColorType.Glow:
                    return Color.Lerp(value.TrackColor, value.SecondaryTrackColor,
                        (1f - Mathf.Cos(Mathf.PI * 2f * phase)) * 0.5f);
                case TrackColorType.Blink:
                    // 方波：半个周期一个色。原来是 (phase % 1) < 0.5，
                    // 那是三角波，和 Update 的实现对不上。
                    return ((phase % 1f) + 1f) % 1f < 0.5f
                        ? value.TrackColor : value.SecondaryTrackColor;
                case TrackColorType.Switch:
                    // 三角波：从一色线性推到另一色再推回来。
                    // 原来直接用 Repeat(phase, 1)，那是锯齿不是三角。
                    float tri = ((phase % 1f) + 1f) % 1f;
                    return Color.Lerp(value.TrackColor, value.SecondaryTrackColor,
                        tri < 0.5f ? tri * 2f : (1f - tri) * 2f);
                case TrackColorType.Rainbow:
                    float h;
                    float s;
                    float v;
                    Color.RGBToHSV(value.TrackColor, out h, out s, out v);
                    return Color.HSVToRGB(Mathf.Repeat(phase, 1f), s, v);
                default:
                    return value.TrackColor;
            }
        }

        private static void ResolveRange(PreviewTimelineContext context, PreviewEventSnapshot levelEvent,
            out int startFloor, out int endFloor)
        {
            startFloor = Mathf.Clamp(levelEvent.Floor, 0, context.Floors.Count - 1);
            endFloor = startFloor;
            try
            {
                Tuple<int, TileRelativeTo> start = levelEvent.GetTile("startTile");
                Tuple<int, TileRelativeTo> end = levelEvent.GetTile("endTile");
                startFloor = Mathf.Clamp(context.ResolveTile(start, levelEvent.Floor),
                    0, context.Floors.Count - 1);
                endFloor = Mathf.Clamp(context.ResolveTile(end, levelEvent.Floor),
                    0, context.Floors.Count - 1);
                if (endFloor < startFloor)
                {
                    int swap = startFloor;
                    startFloor = endFloor;
                    endFloor = swap;
                }
            }
            catch
            {
                // 有些老谱面省略了 tile 对。应用到事件的
                // 格上，比凭空编一个范围更安全。
            }
        }

        private static Color ReadColor(PreviewEventSnapshot levelEvent, string key, Color fallback)
        {
            try
            {
                return levelEvent.GetColor(key, fallback);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
