using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview
{
    internal sealed class PreviewTimelineContext
    {
        /// <summary>
        /// TimeVerify 的总开关，默认关。
        ///
        /// 它是查「事件时间解析」bug 的一次性工具，不是常驻诊断，所以
        /// 不跟 VerboseDiagnostics 共用开关——总闸一开，它就在每次重建里
        /// 多跑 1.1 万次比较加一条同步日志。要复查那个 bug 时，
        /// 连同 Settings.xml 的 VerboseDiagnostics 一起打开即可。
        /// </summary>
        private const bool EnableTimeVerify = false;

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

            // 【临时探针·重建耗时】把构造函数拆成五段计时。
            //
            // 实测一次重建里「上下文快照」要 545~573ms，而它内部到底
            // 是格快照慢、事件索引慢、还是签名慢，光看总数分不出来。
            // 这里只在构造时写一行日志（一次重建一行）。
            long ctxStart = System.Diagnostics.Stopwatch.GetTimestamp();

            floorBindings = editor.floors == null ? new scrFloor[0] : editor.floors.ToArray();
            floors = new PreviewFloorSnapshot[floorBindings.Length];
            for (int i = 0; i < floorBindings.Length; i++)
            {
                floors[i] = new PreviewFloorSnapshot(floorBindings[i], i);
            }
            long afterFloors = System.Diagnostics.Stopwatch.GetTimestamp();

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
            long afterTables = System.Diagnostics.Stopwatch.GetTimestamp();
            AddEventsToIndex(editor.events, ref sequence);
            long afterEvents = System.Diagnostics.Stopwatch.GetTimestamp();
            AddEventsToIndex(editor.decorations, ref sequence);
            long afterDecorations = System.Diagnostics.Stopwatch.GetTimestamp();
            eventCount = editor.events == null ? 0 : editor.events.Count;
            decorationCount = editor.decorations == null ? 0 : editor.decorations.Count;
            eventSignature = ComputeSourceSignature(editor.events);
            decorationSignature = ComputeSourceSignature(editor.decorations);
            long afterSignatures = System.Diagnostics.Stopwatch.GetTimestamp();

            Main.Logger.Log("[RebuildCost] 上下文分解 格=" + MsBetween(ctxStart, afterFloors)
                + "ms 计时表=" + MsBetween(afterFloors, afterTables)
                + "ms 事件=" + MsBetween(afterTables, afterEvents)
                + "ms 装饰=" + MsBetween(afterEvents, afterDecorations)
                + "ms 签名=" + MsBetween(afterDecorations, afterSignatures)
                + "ms 合计=" + MsBetween(ctxStart, afterSignatures) + "ms");

            // 0 格是倒计时格。原生 FirstFloorAngleSetup 把
            // 行星放在 pi * (0.5 - adjustedCountdownTicks) 处，而不是格的
            // 进入角，在这张谱面上两者差了半圈。这里读一次，
            // 好让行星预览能对上原生的开场姿态。
            scrConductor conductor = scrConductor.instance;
            AdjustedCountdownTicks = conductor == null ? 0f : conductor.adjustedCountdownTicks;

            // **纯诊断，默认不跑。** 这段每次重建都要做 1.1 万次比较
            // （实测 compared=11597），末尾还无条件写一条日志——UMM 的
            // Logger 内部是同步 Console.WriteLine，等于一次同步 I/O。
            // 它还报出 mismatched=8903（77% 不匹配），可能指向一个真实的
            // 时间解析 bug，但尚未确认；原始证据已由取证留档
            // （取证-TimeVerify留档-2026-10-28.md）。将来要查那个 bug 时，
            // 把下面的 EnableTimeVerify 改成 true 即可复现。
            //
            // 2026-10-31 起它有自己的开关，不再挂在 VerboseDiagnostics 上。
            // 原因：VerboseDiagnostics 是看帧率诊断（[诊断] 行、逐域耗时）
            // 的总闸，而 TimeVerify 是查时间解析 bug 的一次性工具，两者
            // 用途无关。它跟着总闸走，就会在查拖动卡顿时被顺带打开——
            // 实测这次拖一次要重建 3 次，等于白付 3 遍 1.1 万次比较。
            if (EnableTimeVerify && Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                VerifyEventTimesAgainstNative(editor);
            }
        }

        /// <summary>把两个 Stopwatch 时间戳之差换算成毫秒（【临时探针】用）。</summary>
        private static string MsBetween(long from, long to)
        {
            return ((to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency)
                .ToString("0.0");
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
        //
        // **2026-10-31：64 → 32。** 补上 `Ev.Validate` 这条探针之后第一次
        // 拿到实测值：这一项恒定 **0.12ms/帧**，不随谱面位置变，也不随
        // 播放/暂停变 —— 等于每帧白付 165Hz 预算的 2%。而它防的是
        // 「格数量不变、但某个格对象被换掉」这一种编辑，检出延迟从
        // 0.35 秒变 0.7 秒（2600 格 / 32 / 150fps）没有实际差别：
        // 增删格由上面的数量检查立刻抓到，内容改动由下面的哈希抓到。
        private const int FloorChecksPerFrame = 32;
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
        ///
        /// **2026-10-30 修：这个校验器本身写错过，77% 的「不匹配」是它的锅。**
        ///
        /// 旧实现拿 `effect.sourceLevelEvent.floor`（**作者写事件的原始格号**）
        /// 去 `eventsByType` 桶里找第一条 `Floor` 相等的快照。可是原生
        /// `scnGame.ApplyEvent(evnt, bpm, pitch, floors, offset, customFloorID)`
        /// 被 RepeatEvents 循环调用时，传进去的 `customFloorID` 是**目标格**
        /// （`scnGame.cs:1059`），组件建在那个目标格上，`SetStartTime` 用的
        /// 也是目标格的 `entryTime`。于是：
        ///
        ///   · 展开实例的格号 = 目标格，而 `sourceLevelEvent.floor` 仍是原格；
        ///   · 拿原格去桶里找，命中的是**没展开的那一条**，它的时间当然对不上。
        ///
        /// 证据：原报告 `compared=11597`，而这张谱作者只写了 936 个事件 ——
        /// 多出来的近万次比较全部是 RepeatEvents 展开出来的实例，
        /// 而它们一条都不可能匹配成功。8903/11597 ≈ 77% 正好是展开项占比。
        ///
        /// 现在改成三件事：
        ///   1. 用 `effect.floorID`（原生实际建在哪个格）而不是原始格号；
        ///   2. 同一格同类型可能有多条展开实例，**取时间差最小的一条**，
        ///      而不是拿桶里第一条了事；
        ///   3. 额外统计「找不到候选」的条数 —— 它是一个独立的信号：
        ///      候选缺失说明我们的索引比原生少建了东西，和「时间算错」
        ///      是两种不同的问题，混在一个数字里没法归因。
        /// </summary>
        private void VerifyEventTimesAgainstNative(scnEditor editor)
        {
            if (editor == null || editor.floors == null)
            {
                return;
            }

            int compared = 0;
            int unmatched = 0;
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
                        unmatched++;
                        continue;
                    }

                    // **按原生真正的落格取候选。** floorID 是 ApplyEvent 写进去的
                    // 「这个组件建在哪一格」，对 RepeatEvents 展开实例它是目标格；
                    // 对普通事件它等于作者写的格。用 sourceLevelEvent.floor 会
                    // 把展开实例和它的原始事件错配到一起（根因见上面的说明）。
                    int nativeFloor = effect.floorID;
                    float native = Convert.ToSingle(effect.startTime);

                    // 同格同类型可能有多条（RepeatEvents 展开 + 作者本就写了多条）。
                    // 取时间差最小的一条：我们和原生是各自独立算的，唯一合理的
                    // 判据是「有没有算到同一个值」，不是「桶里第一条长什么样」。
                    PreviewEventSnapshot match = null;
                    float bestDelta = float.MaxValue;
                    for (int j = 0; j < bucket.Count; j++)
                    {
                        if (bucket[j].Floor != nativeFloor)
                        {
                            continue;
                        }
                        float candidateDelta = Mathf.Abs(native - (float)bucket[j].StartTime);
                        if (candidateDelta < bestDelta)
                        {
                            bestDelta = candidateDelta;
                            match = bucket[j];
                        }
                    }
                    if (match == null)
                    {
                        unmatched++;
                        continue;
                    }

                    compared++;
                    float mine = (float)match.StartTime;
                    float delta = Mathf.Abs(native - mine);
                    if (delta > 0.02f)
                    {
                        mismatched++;
                        if (delta > worstDelta)
                        {
                            worstDelta = delta;
                            worstDetail = effect.sourceLevelEvent.eventType + "@f" + nativeFloor
                                + " native=" + native.ToString("F3")
                                + " mine=" + mine.ToString("F3");
                        }
                    }
                }
            }

            Main.Logger.Log("[TimeVerify] compared=" + compared
                + " unmatched=" + unmatched
                + " mismatched=" + mismatched
                + " worstDelta=" + worstDelta.ToString("F3")
                + " worst=" + worstDetail + ".");
        }
        internal scrFloor GetFloorBinding(int index)
        {
            return index < 0 || index >= floorBindings.Length ? null : floorBindings[index];
        }

        /// <summary>
        /// 球在这一格上真正绕着的中心。
        ///
        /// 原生 scrPlanet 是二选一：
        ///   stickToFloor → scrFloor.transform.position（**活的**）
        ///   否则         → scrFloor.startPos（作者摆的布局位）
        ///
        /// 快照里的 BallPosition 是**静态近似**——它把活位置写成
        /// startPos + offsetPos，那对 PositionTrack 是对的（原生正是把格摆到
        /// 那里、并把同一个值写进 offsetPos）。但 MoveTrack 不是：
        /// ffxMoveFloorPlus 直接对 transform.position 起 DOTween，从不碰
        /// offsetPos。而快照建一次就定下来，装不下随时间变的位置，
        /// 所以粘住的格必须现读活位置——不读的话，轨道动到哪，
        /// 球都还停在快照拍下它的那一刻。
        ///
        /// 定义只留这一份：球从这里取，免得又多一处各算一遍。
        /// </summary>
        internal Vector3 ResolveBallCentre(int index)
        {
            PreviewFloorSnapshot floor = GetFloor(index);
            if (floor == null)
            {
                return Vector3.zero;
            }

            if (floor.StickToFloor)
            {
                scrFloor bound = GetFloorBinding(index);
                if (bound != null)
                {
                    return PreviewEvaluationEngine.FloorPositionThisFrame(bound);
                }
            }

            return floor.BallPosition;
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

        internal Vector2 GetVector2WithNaN(PreviewEventSnapshot levelEvent, string key, Vector2 fallback)
        {
            return levelEvent == null ? fallback : levelEvent.GetVector2WithNaN(key, fallback);
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
                //
                // **装饰物也要展开。** 原生 `ApplyEvent` 的 switch 对
                // `MoveDecorations` 同样 `AddComponent<ffxMoveDecorationsPlus>()`，
                // 而 `runManually` 只在标签命中「点击热区」时置位
                // （`hitboxEventTagDecorations.TryGetValue` 命中才设，
                // 本谱没有任何 hitbox 字段）——所以原生里带 eventTag 的
                // MoveDecorations 会照常重复。这里原来把整个装饰物类别都跳过，
                // 结果第 1045 格那批 `fps` / `3` / `4` 的闪色、换图只触发一次，
                // 相机被甩开的那 0.06 秒里画面比原生空。
                //
                // 只放开 `MoveDecorations`（动画类）：`AddDecoration` 被重复
                // 会凭空多出装饰物实例，那不是原生做的事。
                if (snapshot.EventType == LevelEventType.RepeatEvents
                    || (levelEvent.IsDecoration
                        && snapshot.EventType != LevelEventType.MoveDecorations))
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

        internal float GetFloorTravelAngle(PreviewFloorSnapshot floor)
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
