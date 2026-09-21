using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview
{
    internal sealed class PreviewTimelineContext
    {
        private static readonly PreviewEventSnapshot[] EmptyEvents = new PreviewEventSnapshot[0];
        private readonly Dictionary<LevelEventType, List<PreviewEventSnapshot>> eventsByType =
            new Dictionary<LevelEventType, List<PreviewEventSnapshot>>();
        private readonly PreviewFloorSnapshot[] floors;
        private readonly scrFloor[] floorBindings;
        // 按格预算好的计时表，用不可变的谱面数据构建一次，
        // 之后从不重读实时场景：SetSpeed 运行期间 conductor.bpm 会变，
        // 实时推导时间会把后面每个事件都挪位。
        private readonly float[] floorBpm;
        private readonly double[] floorTravelAngle;
        private readonly int eventCount;
        private readonly int decorationCount;
        private readonly int eventSignature;
        private readonly int decorationSignature;

        internal PreviewTimelineContext(scnEditor editor)
        {
            Editor = editor;
            EditorInstanceId = editor.GetInstanceID();
            floorBindings = editor.floors == null ? new scrFloor[0] : editor.floors.ToArray();
            floors = new PreviewFloorSnapshot[floorBindings.Length];
            for (int i = 0; i < floorBindings.Length; i++)
            {
                floors[i] = new PreviewFloorSnapshot(floorBindings[i], i);
            }

            scrController controller = scrController.instance;
            TileSize = controller == null ? 1f : controller.tileSize;



            // 事件时间是按谱面基准 BPM 定义的，不是按实时的
            // conductor 读数。SetSpeed 事件跑起来时 conductor.bpm 会变，
            // 而它并不是原生 SetStartTime 当初烤入起始时间时用的值，
            // 所以在这里读它会让任何变速之后的事件时间都偏移。
            LevelData levelData = scnGame.instance == null ? null : scnGame.instance.levelData;
            BaseBpm = levelData != null && levelData.bpm > 0f
                ? levelData.bpm
                : (scrConductor.instance != null && scrConductor.instance.bpm > 0f
                    ? scrConductor.instance.bpm
                    : 100f);

            // 计时表只建一次。下面每个事件时间都读这些
            // 数组，而不是从场景重新算。
            floorBpm = new float[floors.Length];
            floorTravelAngle = new double[floors.Length];
            for (int i = 0; i < floors.Length; i++)
            {
                PreviewFloorSnapshot floor = floors[i];
                float speed = floor.Speed <= 0f ? 1f : floor.Speed;
                floorBpm[i] = Mathf.Max(0.001f, BaseBpm * speed);
                floorTravelAngle[i] = floorBpm[i] * 3d
                    * (floor.NextEntryTime - floor.EntryTime);
            }

            int sequence = 0;
            AddEventsToIndex(editor.events, ref sequence);
            AddEventsToIndex(editor.decorations, ref sequence);
            eventCount = editor.events == null ? 0 : editor.events.Count;
            decorationCount = editor.decorations == null ? 0 : editor.decorations.Count;
            eventSignature = ComputeSourceSignature(editor.events);
            decorationSignature = ComputeSourceSignature(editor.decorations);

            // 0 格是倒计时格。原生 FirstFloorAngleSetup 把
            // 行星放在 pi * (0.5 - adjustedCountdownTicks) 处，而不是格的
            // 进入角，在这张谱面上两者差了半圈。这里读一次，
            // 好让行星预览能对上原生的开场姿态。
            scrConductor conductor = scrConductor.instance;
            AdjustedCountdownTicks = conductor == null ? 0f : conductor.adjustedCountdownTicks;

            VerifyEventTimesAgainstNative(editor);
        }

        internal scnEditor Editor { get; private set; }
        internal int EditorInstanceId { get; private set; }
        internal float TileSize { get; private set; }
        internal float BaseBpm { get; private set; }
        internal float AdjustedCountdownTicks { get; private set; }
        internal int FloorCount { get { return floors.Length; } }
        internal IList<PreviewFloorSnapshot> Floors { get { return floors; } }

        internal bool MatchesCurrentEditor()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.GetInstanceID() != EditorInstanceId
                || editor.floors == null || editor.floors.Count != floorBindings.Length
                || (editor.events == null ? 0 : editor.events.Count) != eventCount
                || (editor.decorations == null ? 0 : editor.decorations.Count) != decorationCount)
            {
                return false;
            }

            // 格引用扫描是增量的，不是一次扫完。
            //
            // scrFloor 是 UnityEngine.Object，所以每个 `!=` 都会进原生代码做
            // 已销毁对象检查。走完 3516 个格实测 7.2ms，比所有
            // 预览域加起来还多，单它就超过一个 165fps 的整帧。
            // 把扫描并进内容哈希所在的同一个 15 帧槽位只是把开销挪了个地方：
            // 毛刺变成 7.2ms + 1.7ms 一起出现，对它落到的那一帧来说
            // 比摊开更糟。
            //
            // 每帧只查一个有界的切片只要约 0.15ms，几个零点几秒内
            // 仍然能走完整张谱面，所以一个换了格但没改数量的编辑
            // 照样能及时抓到，且没有任何一帧要付全部代价。
            // 上面的数量检查每帧都跑，所以增删——常见的
            // 编辑——仍然立刻可见。
            if (floorBindings.Length == 0)
            {
                return true;
            }

            if (floorCheckCursor >= floorBindings.Length)
            {
                floorCheckCursor = 0;
            }
            int floorCheckEnd = Mathf.Min(floorCheckCursor + FloorChecksPerFrame, floorBindings.Length);
            for (int i = floorCheckCursor; i < floorCheckEnd; i++)
            {
                if (editor.floors[i] != floorBindings[i])
                {
                    return false;
                }
            }

            bool sweptWholeChart = floorCheckEnd >= floorBindings.Length;
            floorCheckCursor = sweptWholeChart ? 0 : floorCheckEnd;

            // 内容哈希是唯一能抓住「数量不变、格引用也不变」这种编辑的
            // 检查，但它要走遍每个事件的每个字段并对字符串值做哈希，
            // 而 .NET 不缓存字符串哈希。它每完成一次格扫描跑一次
            // ——而且绝不比这个间隔更频繁——所以两者
            // 永远不会落在同一帧上。
            if (!sweptWholeChart
                || Time.frameCount - lastSignatureCheckFrame < SignatureCheckIntervalFrames)
            {
                return true;
            }
            lastSignatureCheckFrame = Time.frameCount;

            if (eventSignature != ComputeSourceSignature(editor.events)
                || decorationSignature != ComputeSourceSignature(editor.decorations))
            {
                return false;
            }

            return true;
        }

        // 同时约束了每帧开销（64 个时约 0.15ms）和一个被换掉的格最多
        // 藏多久不被发现（3516 / 64 = 55 帧，165fps 下约三分之一秒）。
        private const int FloorChecksPerFrame = 64;
        private int floorCheckCursor;

        // 60fps 下约 0.25s。上面那些便宜的引用/数量检查仍然每帧都跑，
        // 所以改动这些的重建会立刻被抓到。
        private const int SignatureCheckIntervalFrames = 15;
        private int lastSignatureCheckFrame = -1000;

        /// <summary>
        /// 仅诊断用：把我们的事件起始时间跟游戏烤进它自己
        /// ffxPlusBase 组件的起始时间作对比。任何不一致都意味着
        /// 求值器会把效果放在错误的时间上，用户看到的就是
        /// 效果提前触发。只读，绝不修改场景状态。
        /// </summary>
        private void VerifyEventTimesAgainstNative(scnEditor editor)
        {
            if (editor == null || editor.floors == null)
            {
                return;
            }

            int compared = 0;
            int mismatched = 0;
            float worstDelta = 0f;
            string worstDetail = "none";

            for (int i = 0; i < editor.floors.Count; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null || floor.plusEffects == null)
                {
                    continue;
                }

                foreach (ffxPlusBase effect in floor.plusEffects)
                {
                    if (effect == null || effect.sourceLevelEvent == null
                        || effect.runManually || effect.runOnHit)
                    {
                        continue;
                    }

                    List<PreviewEventSnapshot> bucket;
                    if (!eventsByType.TryGetValue(effect.sourceLevelEvent.eventType, out bucket))
                    {
                        continue;
                    }

                    PreviewEventSnapshot match = null;
                    for (int j = 0; j < bucket.Count; j++)
                    {
                        if (bucket[j].Floor == effect.sourceLevelEvent.floor)
                        {
                            match = bucket[j];
                            break;
                        }
                    }
                    if (match == null)
                    {
                        continue;
                    }

                    compared++;
                    float native = Convert.ToSingle(effect.startTime);
                    float mine = (float)match.StartTime;
                    float delta = Mathf.Abs(native - mine);
                    if (delta > 0.02f)
                    {
                        mismatched++;
                        if (delta > worstDelta)
                        {
                            worstDelta = delta;
                            worstDetail = effect.sourceLevelEvent.eventType + "@f" + match.Floor
                                + " native=" + native.ToString("F3")
                                + " mine=" + mine.ToString("F3");
                        }
                    }
                }
            }

            Main.Logger.Log("[TimeVerify] compared=" + compared
                + " mismatched=" + mismatched
                + " worstDelta=" + worstDelta.ToString("F3")
                + " worst=" + worstDetail + ".");
        }

        internal scrFloor GetFloorBinding(int index)
        {
            return index < 0 || index >= floorBindings.Length ? null : floorBindings[index];
        }

        internal PreviewFloorSnapshot GetFloor(int index)
        {
            return index < 0 || index >= floors.Length ? null : floors[index];
        }

        internal IList<PreviewEventSnapshot> GetEvents(LevelEventType eventType)
        {
            List<PreviewEventSnapshot> events;
            if (!eventsByType.TryGetValue(eventType, out events))
            {
                return EmptyEvents;
            }
            return events;
        }

        internal double GetEventStartTime(PreviewEventSnapshot levelEvent)
        {
            return levelEvent == null ? 0d : levelEvent.StartTime;
        }

        internal float GetEventDuration(PreviewEventSnapshot levelEvent)
        {
            return levelEvent == null ? 0f : levelEvent.Duration;
        }

        internal Ease GetEase(PreviewEventSnapshot levelEvent)
        {
            return levelEvent == null ? Ease.Linear : levelEvent.GetValue("ease", Ease.Linear);
        }

        internal bool IsPropertyEnabled(PreviewEventSnapshot levelEvent, string key)
        {
            return levelEvent != null && levelEvent.IsPropertyEnabled(key);
        }

        internal bool HasPropertyKey(PreviewEventSnapshot levelEvent, string key)
        {
            return levelEvent != null && levelEvent.HasPropertyKey(key);
        }

        internal T GetValue<T>(PreviewEventSnapshot levelEvent, string key, T fallback)
        {
            return levelEvent == null ? fallback : levelEvent.GetValue(key, fallback);
        }

        internal Color GetColor(PreviewEventSnapshot levelEvent, string key, Color fallback)
        {
            return levelEvent == null ? fallback : levelEvent.GetColor(key, fallback);
        }

        internal int ResolveTile(Tuple<int, TileRelativeTo> tile, int eventFloor)
        {
            if (floors.Length == 0)
            {
                return 0;
            }
            if (tile == null)
            {
                return ClampFloor(eventFloor);
            }

            int resolved;
            switch (tile.Item2)
            {
                case TileRelativeTo.Start:
                    resolved = tile.Item1;
                    break;
                case TileRelativeTo.End:
                    resolved = floors.Length - 1 + tile.Item1;
                    break;
                default:
                    resolved = eventFloor + tile.Item1;
                    break;
            }
            return ClampFloor(resolved);
        }

        internal int FindFloorAtOrBefore(float time)
        {
            int low = 0;
            int high = floors.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (floors[middle].EntryTime <= time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return Mathf.Clamp(low - 1, 0, floors.Length - 1);
        }

        private int ClampFloor(int floor)
        {
            return Mathf.Clamp(floor, 0, floors.Length - 1);
        }

        private void AddEventsToIndex(IList<LevelEvent> source, ref int sequence)
        {
            if (source == null)
            {
                return;
            }

            Dictionary<int, Dictionary<string, RepeatRule>> repeatRules = BuildRepeatRules(source);
            for (int i = 0; i < source.Count; i++)
            {
                LevelEvent levelEvent = source[i];
                int currentSequence = sequence++;
                if (levelEvent == null || !levelEvent.active)
                {
                    continue;
                }

                PreviewEventSnapshot snapshot = new PreviewEventSnapshot(levelEvent, currentSequence);
                snapshot.StartTime = CalculateEventStartTime(snapshot);
                snapshot.Duration = CalculateEventDuration(snapshot);
                AddEventToBucket(snapshot);

                // RepeatEvents：原生 ApplyEvent 循环把作者写的一个
                // 事件重放到后面的格或拍上。跳过这一步让 936 个事件里有 446 个
                // 起始时间错误（最坏差 38.4 秒），这就是为什么拖动之后
                // 轨道消失动画一直回不来。
                if (snapshot.EventType == LevelEventType.RepeatEvents || levelEvent.IsDecoration)
                {
                    continue;
                }

                string eventTag = levelEvent.GetString("eventTag");
                if (string.IsNullOrEmpty(eventTag))
                {
                    continue;
                }

                Dictionary<string, RepeatRule> byTag;
                if (!repeatRules.TryGetValue(snapshot.Floor, out byTag))
                {
                    continue;
                }

                RepeatRule rule = null;
                string[] tags = eventTag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int t = 0; t < tags.Length; t++)
                {
                    if (byTag.TryGetValue(tags[t], out rule))
                    {
                        break;
                    }
                    rule = null;
                }
                if (rule == null)
                {
                    continue;
                }

                for (int rep = 1; rep <= rule.Count; rep++)
                {
                    bool beatMode = rule.Interval > 0f;
                    int targetFloor = beatMode
                        ? snapshot.Floor
                        : snapshot.Floor + rep * rule.GapLength;
                    if (targetFloor < 0 || targetFloor >= floors.Length)
                    {
                        break;
                    }

                    float offsetDegrees = beatMode
                        ? rule.Interval * rep * 180f
                        : (rule.ExecuteOnCurrentFloor
                            ? 0f
                            : Convert.ToSingle(floors[targetFloor].EntryBeat - floors[snapshot.Floor].EntryBeat) * 180f);

                    PreviewEventSnapshot expanded = snapshot.CreateExpanded(
                        targetFloor, sequence++, snapshot.StartTime);
                    // 原生 ApplyEvent 在它「目标」格上构建每个重放，
                    // 然后做：
                    //   evnt.TryGet<float>("angleOffset", out var a);
                    //   SetStartTime(bpm, a + offset);
                    // 所以起始时间是事件「自己」的 angleOffset 「加上」重放
                    // 偏移。漏掉事件自己的 angleOffset 会让重放晚整整一个
                    // 前导——谱面 2 开头那个被重复的 MoveTrack 带着 angleOffset -1440
                    // （八拍），于是每块都在游戏摆放位置之后
                    // 八拍才出现。
                    float eventAngleOffset = snapshot.GetValue("angleOffset", 0f);
                    double targetEntryTime = floors[targetFloor].EntryTime;
                    float degrees = eventAngleOffset + offsetDegrees;
                    expanded.StartTime = Mathf.Max(0f, Convert.ToSingle(targetEntryTime)
                        + degrees / 180f * 60f / GetFloorBpm(floors[targetFloor]));
                    AddEventToBucket(expanded);

                    if (IsSoloType(snapshot.EventType))
                    {
                        break;
                    }
                }
            }
        }

        private void AddEventToBucket(PreviewEventSnapshot snapshot)
        {
            List<PreviewEventSnapshot> bucket;
            if (!eventsByType.TryGetValue(snapshot.EventType, out bucket))
            {
                bucket = new List<PreviewEventSnapshot>();
                eventsByType.Add(snapshot.EventType, bucket);
            }
            bucket.Add(snapshot);
        }

        private sealed class RepeatRule
        {
            internal int Count;
            internal float Interval;
            internal bool ExecuteOnCurrentFloor;
            internal int GapLength;
        }

        private static Dictionary<int, Dictionary<string, RepeatRule>> BuildRepeatRules(
            IList<LevelEvent> source)
        {
            Dictionary<int, Dictionary<string, RepeatRule>> rules =
                new Dictionary<int, Dictionary<string, RepeatRule>>();
            for (int i = 0; i < source.Count; i++)
            {
                LevelEvent levelEvent = source[i];
                if (levelEvent == null || !levelEvent.active
                    || levelEvent.eventType != LevelEventType.RepeatEvents
                    || levelEvent.GetData() == null)
                {
                    continue;
                }

                bool beatMode = (RepeatType)levelEvent["repeatType"] == RepeatType.Beat;
                RepeatRule rule = new RepeatRule
                {
                    Count = beatMode ? levelEvent.GetInt("repetitions") : levelEvent.GetInt("floorCount"),
                    Interval = beatMode ? levelEvent.GetFloat("interval") : -1f,
                    ExecuteOnCurrentFloor = levelEvent.GetBool("executeOnCurrentFloor"),
                    GapLength = Mathf.Max(1, levelEvent.GetInt("gapLength"))
                };

                string tag = levelEvent.GetString("tag");
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                Dictionary<string, RepeatRule> byTag;
                if (!rules.TryGetValue(levelEvent.floor, out byTag))
                {
                    byTag = new Dictionary<string, RepeatRule>();
                    rules.Add(levelEvent.floor, byTag);
                }

                string[] tags = tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int t = 0; t < tags.Length; t++)
                {
                    byTag[tags[t]] = rule;
                }

                // gapLength 在很多谱面里是缺的，而它解出来的值决定
                // 重放是堆在同一格上还是每格铺一个。记日志是为了让
                // 这个问题由游戏自己的读取器来回答，而不是靠假定。
                if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
                {
                    bool gapDisabled;
                    bool gapAbsent = levelEvent.disabled != null
                        && levelEvent.disabled.TryGetValue("gapLength", out gapDisabled)
                        && gapDisabled;
                    Main.Logger.Log("[RepeatRule] floor=" + levelEvent.floor
                        + " tag=" + tag
                        + " beat=" + beatMode
                        + " count=" + rule.Count
                        + " gap=" + rule.GapLength
                        + " rawGap=" + levelEvent.GetInt("gapLength")
                        + " gapAbsent=" + gapAbsent
                        + " executeOnCurrent=" + rule.ExecuteOnCurrentFloor + ".");
                }
            }
            return rules;
        }

        private static bool IsSoloType(LevelEventType eventType)
        {
            switch (eventType)
            {
                case LevelEventType.Twirl:
                case LevelEventType.Multitap:
                case LevelEventType.Checkpoint:
                case LevelEventType.SetHitsound:
                case LevelEventType.ChangeTrack:
                case LevelEventType.ColorTrack:
                case LevelEventType.AnimateTrack:
                case LevelEventType.SetPlanetRotation:
                case LevelEventType.KillPlayer:
                case LevelEventType.Hold:
                case LevelEventType.SetHoldSound:
                case LevelEventType.SetConditionalEvents:
                case LevelEventType.MultiPlanet:
                case LevelEventType.FreeRoam:
                case LevelEventType.Pause:
                case LevelEventType.AutoPlayTiles:
                case LevelEventType.Hide:
                case LevelEventType.ScaleMargin:
                case LevelEventType.ScaleRadius:
                case LevelEventType.TileDimensions:
                case LevelEventType.Bookmark:
                    return true;
                default:
                    return false;
            }
        }

        private double CalculateEventStartTime(PreviewEventSnapshot levelEvent)
        {
            int floorIndex = ClampFloor(levelEvent.Floor);
            PreviewFloorSnapshot floor = floors[floorIndex];
            float angleOffset = levelEvent.GetValue("angleOffset", 0f);
            // 格的行程角是 floorBpm * 3 * (nextEntryTime - entryTime)，
            // 而不是 scrFloor.angleLength。在长按和多拍格上两者
            // 不一致，这决定了下面那个小 angleOffset 修正
            // 生不生效，从而挪动那一格上每个事件的起始时间。
            double travelAngle = GetFloorTravelAngle(floor);
            if (travelAngle > 0d && angleOffset >= travelAngle)
            {
                angleOffset -= 0.01f;
            }

            // 整条式子留在 double 里算，不要 Convert.ToSingle。
            //
            // 原版 ffxPlusBase.SetStartTime 就是
            //   entryTime + (degreeOffset * (PI/180) / PI * (60/(bpm*speed)))
            // 且 startTime 是 double。这不是"精度更高"而已——作者会用
            // 极小的 angleOffset 指定同一格上两个事件的先后，换算成时间只有
            // 几微秒；先截成 float 再相加，那点差值会被 ULP 直接吃掉，
            // 两个事件时间相等，顺序就交给排序算法了。
            // 详见 IPreviewTimedKeyframe.Start 的说明。
            double seconds = angleOffset / 180d * 60d / GetFloorBpm(floor);
            return Math.Max(0d, Convert.ToDouble(floor.EntryTime) + seconds);
        }

        private float GetFloorTravelAngle(PreviewFloorSnapshot floor)
        {
            if (floor == null || floor.Index < 0 || floor.Index >= floorTravelAngle.Length)
            {
                return 0f;
            }
            return Convert.ToSingle(floorTravelAngle[floor.Index]);
        }

        private float CalculateEventDuration(PreviewEventSnapshot levelEvent)
        {
            int floorIndex = ClampFloor(levelEvent.Floor);
            float beats = levelEvent.GetValue("duration", 0f);
            return Mathf.Max(0f, beats * 60f / GetFloorBpm(floors[floorIndex]));
        }

        private float GetFloorBpm(PreviewFloorSnapshot floor)
        {
            if (floor == null || floor.Index < 0 || floor.Index >= floorBpm.Length)
            {
                return Mathf.Max(0.001f, BaseBpm);
            }
            return floorBpm[floor.Index];
        }

        private static int ComputeSourceSignature(IList<LevelEvent> source)
        {
            unchecked
            {
                int hash = 17;
                if (source == null)
                {
                    return hash;
                }

                for (int i = 0; i < source.Count; i++)
                {
                    LevelEvent levelEvent = source[i];
                    if (levelEvent == null)
                    {
                        hash = hash * 31;
                        continue;
                    }

                    hash = hash * 31 + levelEvent.floor;
                    hash = hash * 31 + (int)levelEvent.eventType;
                    hash = hash * 31 + (levelEvent.active ? 1 : 0);
                    Dictionary<string, object> data = levelEvent.GetData();
                    if (data == null)
                    {
                        continue;
                    }
                    foreach (KeyValuePair<string, object> pair in data)
                    {
                        hash = hash * 31 + pair.Key.GetHashCode();
                        hash = hash * 31 + (pair.Value == null ? 0 : pair.Value.GetHashCode());
                    }
                }
                return hash;
            }
        }
    }
}
