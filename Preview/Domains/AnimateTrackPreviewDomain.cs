using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// AnimateTrack 的绝对时间求值。
    ///
    /// 原生游戏把每个 AnimateTrack 事件变成 ffxFloorAppearPlus /
    /// ffxFloorDisappearPlus 组件（scnGame.cs, ffxChangeTrack.PrepFloor），再用
    /// tween 驱动它们。这些 tween 一次性消耗掉自己，所以播放头往前跑过
    /// 再被拖回来后，格还留着后一趟的淡出缩放和
    /// 不透明度，再也回不来——「轨道没被还原」。
    ///
    /// 本域从事件数据在绝对时间上重新推导每格的动画状态，
    /// 而不是重放 tween。公式、偏移和缓动都
    /// 取自 ffxFloorAppearPlus / ffxFloorDisappearPlus：
    ///
    ///   appear:    startTime = entryTime - beatsAhead * crotchet
    ///              duration  = min(crotchet * 0.5, 0.5)      （Drop/Rise 翻倍）
    ///   disappear: startTime = nextFloor.entryTime + beatsBehind * crotchet
    ///              duration  = min(crotchet * 0.5, 0.5)
    ///
    /// 初始的散开偏移用 UnityEngine.Random，跟原生代码完全一样，
    /// 但生成器状态会被存下来再还原，这样本求值器
    /// 不会扰动游戏自己的随机序列。
    /// </summary>
    internal sealed class AnimateTrackPreviewDomain : IPreviewDomain
    {
        private sealed class Segment
        {
            internal double Start;
            internal float Duration;
            internal Ease Ease;
            internal bool HasPosition;
            internal Vector3 Position;
            internal bool HasRotation;
            internal float Rotation;
            internal bool HasScale;
            internal Vector2 Scale;
            internal bool HasOpacity;
            internal float Opacity;

            /// <summary>
            /// 这一段跑完之后，它驱动的通道要不要**继续保持**在终点值上。
            ///
            /// 两类段的语义正好相反，用一个标志区分：
            ///
            ///   · 落地段（appear 的 settle）：不保持。跑完就松手，
            ///     把姿态交还给同时在跑的其它域（MoveTrack 的长位移）。
            ///     早期的毛病是它**永远**在生效，每帧把 MoveTrack 写好的
            ///     旋转改回基线——那才是「MoveTrack 从没生效」的根因。
            ///
            ///   · 消失段（disappear）：保持。它要把格淡出/缩没，跑完之后
            ///     必须一直停在那儿。松手的话，MoveTrack 会照写它自己的
            ///     透明度，把已经消失的格又写回来——表现就是
            ///     「前面的轨道该消失却还在」。
            /// </summary>
            internal bool PersistAfter;
        }

        private sealed class FloorAnimation
        {
            internal Vector3 BasePosition;
            internal float BaseRotation;
            internal Vector3 BaseScale;
            internal readonly List<Segment> Segments = new List<Segment>();
        }

        // 原生组件用的缓动值：Linear、OutSine、InOutSine。
        private const Ease AnimEase = Ease.OutSine;
        private const Ease LinearEase = Ease.Linear;

        private FloorAnimation[] floors = new FloorAnimation[0];

        public string Name
        {
            get { return "AnimateTrack"; }
        }


        public void Analyze(PreviewTimelineContext context)
        {
            int count = context.FloorCount;
            floors = new FloorAnimation[count];
            for (int i = 0; i < count; i++)
            {
                if (context.GetFloor(i) == null)
                {
                    continue;
                }

                PreviewFloorSnapshot snapshot = context.GetFloor(i);
                floors[i] = new FloorAnimation
                {
                    BasePosition = snapshot.StartPosition,
                    BaseRotation = snapshot.StartRotation.z,
                    BaseScale = snapshot.StartScale
                };
            }

            List<PreviewEventSnapshot> events = CollectAnimateEvents(context);

            // 从「关卡」自己的设置播种，不是从 None。谱面可以全局声明它的
            // 出现/消失动画（settings.trackAnimation /
            // trackDisappearAnimation），一个 AnimateTrack 事件都没有，而原生
            // 代码正是从这两个起步：
            //   scnGame: animationType = levelData.trackAnimation;
            //            animationType2 = levelData.trackDisappearAnimation;
            // 从 None 起步意味着只写了全局设置的谱面永远不会有动画。
            // 没有事件时提前返回，效果一样。
            TrackAnimationType appear = TrackAnimationType.None;
            TrackAnimationType2 disappear = TrackAnimationType2.None;
            float beatsAhead = 0f;
            float beatsBehind = 0f;
            int aheadFloor = 0;
            int behindFloor = 0;

            // 事件的**原始**拍数。每个格都从它重算一次缩放，
            // 绝不拿上一格算好的结果再乘——那样是逐格累乘，
            // 在 BPM 突变处（有的谱面瞬时几万 BPM）会指数级偏离，
            // 表现就是「原版十几个格一起出现，这里一格一格来」。
            float rawBeatsAhead = 0f;
            float rawBeatsBehind = 0f;

            // 全局动画设置来自关卡数据。scnGame.instance 在编辑器路径上
            // 可能为空，那就退回编辑器自己持有的 customLevel——否则
            // 「只写了全局 trackAnimation / trackDisappearAnimation、
            // 一个 AnimateTrack 事件都没有」的谱面会被整个丢掉，
            // 表现就是那两个参数像没生效（China E 就是这样）。
            scnGame game = scnGame.instance;
            if (game == null && ADOBase.editor != null)
            {
                game = ADOBase.editor.customLevel;
            }

            LevelData levelData = game == null ? null : game.levelData;
            if (levelData != null)
            {
                appear = levelData.trackAnimation;
                disappear = levelData.trackDisappearAnimation;
                beatsAhead = levelData.trackBeatsAhead;
                beatsBehind = levelData.trackBeatsBehind;
                rawBeatsAhead = beatsAhead;
                rawBeatsBehind = beatsBehind;
            }

            int cursor = 0;
            for (int floorIndex = 0; floorIndex < count; floorIndex++)
            {
                if (cursor < events.Count && events[cursor].Floor == floorIndex)
                {
                    PreviewEventSnapshot levelEvent = events[cursor];
                    cursor++;

                    // 这里只维护**原始值**与锚点格；缩放挪到下面每格都做。
                    if (context.IsPropertyEnabled(levelEvent, "trackAnimation"))
                    {
                        appear = context.GetValue(levelEvent, "trackAnimation", appear);
                        rawBeatsAhead = context.GetValue(levelEvent, "beatsAhead", rawBeatsAhead);
                        aheadFloor = floorIndex;
                    }

                    if (context.IsPropertyEnabled(levelEvent, "trackDisappearAnimation"))
                    {
                        disappear = context.GetValue(levelEvent, "trackDisappearAnimation", disappear);
                        rawBeatsBehind = context.GetValue(levelEvent, "beatsBehind", rawBeatsBehind);
                        behindFloor = floorIndex;
                    }
                }

                // **每格都重算缩放**，这是关键。
                //
                // 游戏那边（scnGame 逐格烘入）：
                //     float num18 = speed / num6;        // speed 是**当前格**的
                //     orAddComponent.tilesAhead = num3 * num18;
                // 所以提前量随当前格 BPM 一起变。
                //
                // 之前只在遇到 AnimateTrack 事件那一格才重算，中间一直沿用旧值，
                // 于是 BPM 涨上去之后提前量还是老值——表现就是
                // 「原版十几个格一起出现，这里一格一格来」。
                float scaledAhead = ScaleBeats(context, rawBeatsAhead, aheadFloor, floorIndex);
                float scaledBehind = ScaleBeats(context, rawBeatsBehind, behindFloor, floorIndex);
                beatsAhead = scaledAhead;
                beatsBehind = scaledBehind;

                BuildFloor(context, floorIndex, appear, scaledAhead, disappear, scaledBehind);
            }

            // 段必须按起始时间升序，Evaluate 才能用「遇到还没开始的段就停」
            // 这个短路，而且后启动的段要最后应用（后者写值，就赢）。
            //
            // AppendAppear 与 AppendDisappear 各自追加时不保证这个顺序：
            // 出现动画的 scattered 段是 -∞、settle 段在格进入前几拍，而
            // 消失动画的 gone 段可能远早于 settle（原版不钳，见该处注释）。
            // 于是数组序会是 [-∞, 15.3, -247]，短路失效，而且 gone 最后应用、
            // 直接把 settle 盖掉——格就停在散开状态。
            //
            // 原版里这两个是各自独立的 tween，后启动的最后写值，所以出现
            // 动画的 settle 赢过消失动画。按 Start 排序正是复现这个先后。
            for (int i = 0; i < floors.Length; i++)
            {
                FloorAnimation animation = floors[i];
                if (animation != null && animation.Segments.Count > 1)
                {
                    PreviewStableSort.Sort(animation.Segments,
                        (left, right) => left.Start.CompareTo(right.Start));
                }
            }

            int segmentCount = 0;
            for (int i = 0; i < floors.Length; i++)
            {
                if (floors[i] != null)
                {
                    segmentCount += floors[i].Segments.Count;
                }
            }
            Main.Logger.Log("[StaticPreview/AnimateTrack] analyzed " + events.Count
                + " events, " + segmentCount + " segments.");
        }
        private static List<PreviewEventSnapshot> CollectAnimateEvents(PreviewTimelineContext context)
        {
            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.AnimateTrack);
            List<PreviewEventSnapshot> result = new List<PreviewEventSnapshot>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                result.Add(source[i]);
            }
            PreviewStableSort.Sort(result, (left, right) => left.Floor.CompareTo(right.Floor));
            return result;
        }

        /// <summary>
        /// 事件没改 beatsAhead 时，原生代码按设置它的那个格与当前格
        /// 之间的 BPM 比例重新缩放它。
        /// </summary>
        private static float ScaleBeats(PreviewTimelineContext context, float beats,
            int fromFloor, int toFloor)
        {
            if (beats <= 0f)
            {
                return beats;
            }

            float fromBpm = GetFloorBpm(context, fromFloor);
            float toBpm = GetFloorBpm(context, toFloor);
            if (fromBpm <= 0.0001f)
            {
                return beats;
            }

            return beats * toBpm / fromBpm;
        }

        private static float GetFloorBpm(PreviewTimelineContext context, int floorIndex)
        {
            PreviewFloorSnapshot snapshot = context.GetFloor(floorIndex);
            if (snapshot == null)
            {
                return Mathf.Max(0.001f, context.BaseBpm);
            }

            float speed = snapshot.Speed <= 0f ? 1f : snapshot.Speed;
            return Mathf.Max(0.001f, context.BaseBpm * speed);
        }

        private static float GetCrotchet(PreviewTimelineContext context, int floorIndex)
        {
            return 60f / GetFloorBpm(context, floorIndex);
        }

        private void BuildFloor(PreviewTimelineContext context, int floorIndex,
            TrackAnimationType appear, float beatsAhead,
            TrackAnimationType2 disappear, float beatsBehind)
        {
            FloorAnimation animation = floors[floorIndex];
            if (animation == null)
            {
                return;
            }

            // 不要拿「编辑器暂停态有没有 ffxFloorAppearPlus /
            // ffxFloorDisappearPlus」当门。暂停时它们确实一个都没有
            // （ApplyEventsToFloors 走到 `if (paused) return;` 就不再往下建），
            // 但开始游玩时 WaitForStartCo 会调
            // scnGame.PrepVfx(floors, checkpointNum)，而那个重载的
            // remakeFloors 默认 true——两个组件都会建出来，动画是生效的。
            //
            // 真正出问题的是段的应用顺序，见 Analyze 末尾的排序。
            float crotchet = GetCrotchet(context, floorIndex);
            float duration = Mathf.Min(crotchet * 0.5f, 0.5f);

            if (appear != TrackAnimationType.None)
            {
                AppendAppear(context, animation, floorIndex, appear, beatsAhead, crotchet, duration);
            }

            if (disappear != TrackAnimationType2.None
                && floorIndex < context.FloorCount - 1)
            {
                AppendDisappear(context, animation, floorIndex, disappear, beatsBehind, duration);
            }
        }

        private void AppendAppear(PreviewTimelineContext context, FloorAnimation animation,
            int floorIndex, TrackAnimationType appear, float beatsAhead, float crotchet, float duration)
        {
            bool dropOrRise = appear == TrackAnimationType.Drop || appear == TrackAnimationType.Rise;

            // ffxFloorAppearPlus.SetStartTime：Drop/Rise 等两倍久，tween 走
            // tilesAhead 拍；其余用半个拍，上限 0.5 秒。
            float tiles = dropOrRise ? beatsAhead * 2f : beatsAhead;
            PreviewFloorSnapshot snapshot = context.GetFloor(floorIndex);
            float entryTime = snapshot == null ? 0f : Convert.ToSingle(snapshot.EntryTime);
            float start = Mathf.Max(0f, entryTime - Mathf.Max(0f, tiles) * crotchet);

            float tweenDuration = dropOrRise
                ? Mathf.Max(0.0001f, beatsAhead * crotchet)
                : duration;

            // 1) 散开状态，从格存在之前一直保持
            animation.Segments.Add(CreateScattered(floorIndex, appear, float.NegativeInfinity, 0f));

            // 2) 落回正常状态
            Segment settle = new Segment
            {
                Start = start,
                Duration = tweenDuration,
                Ease = dropOrRise ? LinearEase : AnimEase,
                HasPosition = true,
                Position = animation.BasePosition,
                HasRotation = true,
                Rotation = animation.BaseRotation,
                HasScale = true,
                Scale = new Vector2(animation.BaseScale.x * 100f, animation.BaseScale.y * 100f)
            };
            if (appear == TrackAnimationType.Fade)
            {
                settle.HasOpacity = true;
                settle.Opacity = 1f;
            }
            animation.Segments.Add(settle);
        }

        private static Segment CreateScattered(int floorIndex, TrackAnimationType appear,
            float start, float duration)
        {
            Segment segment = new Segment
            {
                Start = start,
                Duration = duration,
                Ease = LinearEase,
                HasPosition = true,
                HasRotation = true,
                HasScale = true,
                Scale = Vector2.one * 100f
            };

            // ffxFloorAppearPlus.FloorSetup 给每格随机化位置/旋转。
            // InitState(floor) 让散开状态在反复求值之间保持稳定，且
            // 生成器状态会被还原，所以游戏其余部分不受影响。
            UnityEngine.Random.State savedState = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(floorIndex);
                switch (appear)
                {
                    case TrackAnimationType.Assemble:
                        Scatter(segment, 4f, floorIndex, true);
                        break;
                    case TrackAnimationType.Assemble_Far:
                        Scatter(segment, 8f, floorIndex, true);
                        break;
                    case TrackAnimationType.Extend:
                        segment.HasScale = true;
                        segment.Scale = Vector2.zero;
                        break;
                    case TrackAnimationType.Grow:
                        segment.HasScale = true;
                        segment.Scale = Vector2.zero;
                        break;
                    case TrackAnimationType.Grow_Spin:
                        segment.HasScale = true;
                        segment.Scale = Vector2.zero;
                        break;
                    case TrackAnimationType.Fade:
                        segment.HasOpacity = true;
                        segment.Opacity = 0f;
                        break;
                    case TrackAnimationType.Drop:
                        Scatter(segment, 0f, floorIndex, false);
                        segment.Position += Vector3.up * 8f;
                        segment.HasScale = true;
                        segment.Scale = Vector2.zero;
                        break;
                    case TrackAnimationType.Rise:
                        Scatter(segment, 0f, floorIndex, false);
                        segment.Position += Vector3.down * 8f;
                        segment.HasScale = true;
                        segment.Scale = Vector2.zero;
                        break;
                }
            }
            finally
            {
                UnityEngine.Random.state = savedState;
            }

            return segment;
        }

        private static void Scatter(Segment segment, float range, int floorIndex, bool includeRotation)
        {
            // 调用方负责播种和还原生成器；在这里读偏移，好让
            // 抽取顺序跟原生组件一致。
            float x = UnityEngine.Random.Range(-range, range);
            float y = UnityEngine.Random.Range(-range, range);
            segment.HasPosition = true;
            segment.Position = new Vector3(x, y, 0f);
            if (includeRotation)
            {
                segment.HasRotation = true;
                segment.Rotation = UnityEngine.Random.Range(-75f, 75f);
            }
        }

        private void AppendDisappear(PreviewTimelineContext context, FloorAnimation animation,
            int floorIndex, TrackAnimationType2 disappear, float beatsBehind, float duration)
        {
            PreviewFloorSnapshot next = context.GetFloor(floorIndex + 1);
            if (next == null)
            {
                return;
            }

            float crotchet = GetCrotchet(context, floorIndex);
            // 不钳 beatsBehind，忠实原版
            //   ffxFloorDisappearPlus.SetStartTime:
            //     startTime = nextfloor.entryTime + tilesBehind * num
            // （注意它和 appear 不同——appear 那边有 Mathf.Max(..., 0f)）。
            // tilesBehind 为 -2000 这种值时 startTime 落到谱面开始之前，
            // 散开动画在开头就跑完了。保持它很负，好让出现动画的 settle 段
            // 在 Analyze 末尾排序后位于它之后、把它盖回去——那正是原版
            // 两个独立 tween 的先后关系。
            float start = Convert.ToSingle(next.EntryTime) + beatsBehind * crotchet;

            // 只设置这个动画真正驱动的通道。原生
            // ffxFloorDisappearPlus 给每种类型建不同的 tween：
            //   Retract      -> 缩放到零 + 移到下一格
            //   Scatter      -> 按随机偏移移动 + 旋转
            //   Scatter_Far  -> 同上，范围更大
            //   Shrink       -> 缩放到零
            //   Shrink_Spin  -> 缩放到零 + 旋转 -180
            //   Fade         -> 不透明度归零，别的什么都不做
            // 给每种类型都写 position/rotation/scale，意味着 Fade 也会写这些
            // 通道，而本域跑在 MoveTrack 之后，就把轨道位移
            // 拉回了基线——整条轨道消失了。
            var gone = new Segment
            {
                Start = start,
                Duration = duration,
                Ease = AnimEase,
                // 消失是「终态」：跑完必须一直保持，否则 MoveTrack 会把
                // 已经淡出的格又写回来（前面的轨道该不见却还在）。
                PersistAfter = true
            };

            UnityEngine.Random.State savedState = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(floorIndex);
                switch (disappear)
                {
                    case TrackAnimationType2.Retract:
                        gone.HasPosition = true;
                        gone.Position = next.StartPosition;
                        gone.HasScale = true;
                        gone.Scale = Vector2.zero;
                        break;
                    case TrackAnimationType2.Scatter:
                        gone.HasPosition = true;
                        gone.Position = animation.BasePosition + new Vector3(
                            UnityEngine.Random.Range(-4f, 4f),
                            UnityEngine.Random.Range(-4f, 4f), 0f);
                        gone.HasRotation = true;
                        gone.Rotation = UnityEngine.Random.Range(-75f, 75f);
                        break;
                    case TrackAnimationType2.Scatter_Far:
                        gone.HasPosition = true;
                        gone.Position = animation.BasePosition + new Vector3(
                            UnityEngine.Random.Range(-8f, 8f),
                            UnityEngine.Random.Range(-8f, 8f), 0f);
                        gone.HasRotation = true;
                        gone.Rotation = UnityEngine.Random.Range(-75f, 75f);
                        break;
                    case TrackAnimationType2.Shrink:
                        gone.HasScale = true;
                        gone.Scale = Vector2.zero;
                        break;
                    case TrackAnimationType2.Shrink_Spin:
                        gone.HasScale = true;
                        gone.Scale = Vector2.zero;
                        gone.HasRotation = true;
                        gone.Rotation = -180f;
                        break;
                    case TrackAnimationType2.Fade:
                        gone.HasOpacity = true;
                        gone.Opacity = 0f;
                        break;
                }
            }
            finally
            {
                UnityEngine.Random.state = savedState;
            }

            animation.Segments.Add(gone);
        }
        /// <summary>
        /// 宿主 MoveTrack 域。本域由它驱动，写入也经它的延迟队列下发，
        /// 这样两者在帧末的先后关系与原来直写时一致。
        /// </summary>
        private MoveTrackPreviewDomain ownerTrack;

        internal void AttachOwner(MoveTrackPreviewDomain track)
        {
            ownerTrack = track;
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            PreviewTimelineContext context = frame.Context;

            LogDisappearSchedule(frame.Time);
            for (int floorIndex = 0; floorIndex < floors.Length; floorIndex++)
            {
                FloorAnimation animation = floors[floorIndex];
                if (animation == null || animation.Segments.Count == 0)
                {
                    continue;
                }

                scrFloor binding = context.GetFloorBinding(floorIndex);
                if (binding == null)
                {
                    continue;
                }

                Vector3 position = animation.BasePosition;
                float rotation = animation.BaseRotation;
                Vector2 scale = new Vector2(animation.BaseScale.x, animation.BaseScale.y) * 100f;
                float opacity = 100f;
                // 动画真正驱动哪些通道。它从不碰的通道
                // 绝不能被写回：本域跑在 MoveTrack 之后，是为了能在它
                // 动画的格属性上胜出，但把没管的通道也一起写
                // 会盖掉 MoveTrack 刚设好的值——有张谱面用
                // MoveTrack(NoMove, opacity 0) 藏住后面的格，结果隐藏被撤销了，
                // 因为全局消失动画把不透明度改写回了基线。
                bool touchedPosition = false;
                bool touchedRotation = false;
                bool touchedScale = false;
                bool touchedOpacity = false;

                // 最后启动的那一段。段升序，所以扫到第一个「还没开始」
                // 就能停。
                int lastStarted = -1;
                for (int i = 0; i < animation.Segments.Count; i++)
                {
                    if (animation.Segments[i].Start > frame.Time)
                    {
                        break;
                    }
                    lastStarted = i;
                }

                // 这一帧还有没有别人（MoveTrack）在写这一格。位置和不透明度
                // 两处判据用的是同一个值：有别人在管这一格，本域就让位。
                bool contestedWrite = ownerTrack != null
                    && ownerTrack.HasPendingWrite(binding);

                // 有没有「已开始的段在主张透明度」，由下面的值循环填进
                // touchedOpacity；循环跑完它还是 false 的话，说明这一格
                // **没有任何人在管透明度**，那里会替它写回 1。
                // 判据和原因见循环之后那段。

                for (int i = 0; i < animation.Segments.Count; i++)
                {
                    Segment segment = animation.Segments[i];
                    if (segment.Start > frame.Time)
                    {
                        break;
                    }

                    // **值**按 Start 升序逐段叠加（后启动者覆盖），这一层
                    // 和原生「后启动的 tween 最后写值」等价（升序由 Analyze
                    // 末尾的排序保证）。
                    //
                    // 跑完的段**保持终态**，不「松手」。原生每个段是一次性
                    // tween，跑完停在终点；求值器每帧重算，所以「已经结束」
                    // 也必须继续按终态写——否则格会退回上一次写它的值。
                    // 实测这张谱：散开段写完之后，settle 一跑完就不写，
                    // 格永远停在散开；而且负责「初始透明」的 scattered 段
                    // （O=0）窗口被中间插入的消失段截断，settle 变成从 100
                    // lerp 到 100，淡入整个消失。
                    //
                    // **但通道的归属要另算。** 原生那条跑完的 tween 会
                    // 释放通道，之后 MoveTrack 的 tween 才能接管；求值器
                    // 如果让跑完的段继续主张通道，它就会每帧把位置写回
                    // 烘焙值，把后入队的 MoveTrack 位移抹掉——Dream Flower
                    // 第 174 格就是这么不动的（实测同一帧两笔写入：
                    // MoveTrack 写 (-36.6213)，其后一笔又写回 (-35.1213)，
                    // 落回烘焙值，因为那条全轨道 Fade 的 appear 段早跑完了
                    // 却还在每帧主张位置）。
                    //
                    // 归属规则：**只有最后启动的那一段**、以及标记了
                    // PersistAfter 的终态段（消失）才主张通道。
                    // 「最后启动」这一条是必需的——早先试过单纯「跑完松手」，
                    // 结果 Duration=0 的散开初态又开始主张，格永远停在散开。
                    //
                    // **位置另有一条让位规则**：跑完的 appear 段，在
                    // 「这一帧确实有别的域在写这一格的位置」时把位置让出去。
                    // 只在有竞争时才让（而不是一律松手），是因为一旦一律松手，
                    // 拖动播放头整个跨过该格的动画时，那一帧没有更早的写入
                    // 打底，格就永久卡在散开初态。
                    //
                    // 旋转/缩放/透明度不让：settle 段这三个通道的目标值就是
                    // 烘焙值本身（BaseRotation / BaseScale / 1），和 MoveTrack
                    // 非位移事件写的是同一个数，谁写都一样；而透明度是全局
                    // 消失动画的通道，必须保持「后写者胜」，否则已经淡出的格
                    // 会被 MoveTrack 写回来。
                    float ratio = segment.Duration <= 0f
                        ? 1f
                        : Mathf.Clamp01((frame.Time - (float)segment.Start) / segment.Duration);
                    float eased = PreviewEase.EasedValue(0f, 1f, ratio, segment.Ease);
                    bool completed = segment.Duration > 0f
                        && frame.Time > (float)segment.Start + segment.Duration;
                    bool owns = segment.PersistAfter || i == lastStarted;
                    // 终态段（消失）永远主张位置——它必须把轨道收走。
                    // 其余段只有「最后启动」时才主张，跑完且这一帧有竞争时让位。
                    bool claimsPosition = segment.PersistAfter
                        || (i == lastStarted && !(completed && contestedWrite));

                    if (segment.HasPosition)
                    {
                        Vector3 target = segment.Start == float.NegativeInfinity
                            ? animation.BasePosition + segment.Position
                            : segment.Position;
                        position = Vector3.LerpUnclamped(position, target, eased);
                        if (claimsPosition) { touchedPosition = true; }
                    }
                    if (segment.HasRotation)
                    {
                        float target = segment.Start == float.NegativeInfinity
                            ? animation.BaseRotation + segment.Rotation
                            : segment.Rotation;
                        rotation = Mathf.LerpUnclamped(rotation, target, eased);
                        if (owns) { touchedRotation = true; }
                    }
                    if (segment.HasScale)
                    {
                        scale = Vector2.LerpUnclamped(scale, segment.Scale, eased);
                        if (owns) { touchedScale = true; }
                    }
                    if (segment.HasOpacity)
                    {
                        opacity = Mathf.LerpUnclamped(opacity, segment.Opacity * 100f, eased);
                        if (owns) { touchedOpacity = true; }
                    }
                }

                // **没有任何已开始的段主张透明度 → 这一格此刻就该是正常
                // 可见，必须显式写回 1。**
                //
                // 原版的消失 tween 是一次性的：跑完就把格停在 opacity 0。
                // 播放头往回跳时，那些消失段的 Start 落在播放头之后，
                // 值循环第一句就 `break` 掉了；而 MoveTrack 的
                // RestoreBaseline 刻意不恢复透明度（那是本域的通道）。
                // 于是格永远停在被淡出过的样子——实测 Offshore 往回拖到
                // t=1.8、球已经回到第 1 格，场景里仍有 96 格 opacity=0。
                //
                // 判据**不能**写成「一段都没开始」（原来的 `lastStarted < 0`）。
                // 这张谱每格都有一个 appear 的散开段 `s0@-inf`——它一直
                // 是"已开始"的，所以 lastStarted 从来不是 -1，那条兜底
                // 一次都没生效过。真正要看的是**有没有已开始的段在写
                // 透明度这个通道**：s0/s1 都是 `o=-`（不碰透明度），
                // 只有消失段 s2 才带 `o=0.00`。实测 t=3.90 时
                // f20/f50/f100 的 last 分别是 1/0/0 而 live 全是 0。
                //
                // contestedWrite 是让位规则：MoveTrack 这一帧也在写这一格
                // （比如 `MoveTrack(opacity 0)` 藏住后面的格）时本域不插手，
                // 否则会把那次隐藏撤销掉。
                if (!touchedOpacity && !contestedWrite)
                {
                    opacity = 100f;
                    touchedOpacity = true;
                }

                // 不直接写，交给 MoveTrack 的延迟队列在帧末统一下发。
                //
                // 两个理由：
                //   · 游戏的原生组件跑在求值器之后，会把这些 transform
                //     重置回烘焙值，直写等于白写。
                //   · 本域的写入必须排在 MoveTrack 之后才能「胜出」，
                //     而两者原来就是直写时的先后关系；走同一条队列、
                //     按入队顺序下发，这个先后关系原样保留。
                //
                // 只标 touched 的通道：本域从不碰的通道绝不能被写回，
                // 否则会盖掉 MoveTrack 设的值。
                if (ownerTrack == null)
                {
                    // 宿主没接上时退回直写。正常路径不会走到这里
                    // （AttachAnimateTrack 会一并注入宿主），但宁可写得
                    // 难看一点，也不要因为接不上就整段不生效。
                    if (touchedPosition) { binding.transform.position = position; }
                    if (touchedRotation) { binding.transform.rotation = Quaternion.Euler(0f, 0f, rotation); }
                    if (touchedScale)
                    {
                        binding.transform.localScale = new Vector3(
                            scale.x / 100f, scale.y / 100f, animation.BaseScale.z);
                    }
                    if (touchedOpacity) { binding.SetOpacity(Mathf.Clamp01(opacity / 100f)); }
                    continue;
                }
                ownerTrack.QueueWrite(new MoveTrackPreviewDomain.PendingWrite
                {
                    Floor = binding,
                    WritePosition = touchedPosition,
                    WriteRotation = touchedRotation,
                    WriteScale = touchedScale,
                    WriteOpacity = touchedOpacity,
                    Position = position,
                    Rotation = Quaternion.Euler(0f, 0f, rotation),
                    Scale = new Vector3(scale.x / 100f, scale.y / 100f, animation.BaseScale.z),
                    Opacity = Mathf.Clamp01(opacity / 100f)
                });
            }
        }

        private int lastScheduleLogFrame = -10000;

        /// <summary>
        /// 只读。打印头几格什么时候淡出，这样「全都消失了」可以
        /// 归因到是一张从时间 0 开始的时刻表，还是从格退出时刻开始的那张，
        /// 而不是从画面上倒推。
        /// </summary>
        private void LogDisappearSchedule(float time)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }
            if (Time.frameCount - lastScheduleLogFrame < 120)
            {
                return;
            }
            lastScheduleLogFrame = Time.frameCount;

            System.Text.StringBuilder builder = new System.Text.StringBuilder("[DisappearSchedule] t=");
            builder.Append(time.ToString("0.00")).Append(" floors=").Append(floors.Length);
            int shown = 0;
            for (int i = 0; i < floors.Length && shown < 6; i++)
            {
                FloorAnimation animation = floors[i];
                if (animation == null || animation.Segments.Count == 0)
                {
                    continue;
                }
                builder.Append(" | f").Append(i);
                for (int j = 0; j < animation.Segments.Count && j < 3; j++)
                {
                    Segment s = animation.Segments[j];
                    builder.Append(" st=").Append(
                        float.IsNegativeInfinity((float)s.Start) ? "-inf" : s.Start.ToString("0.00"))
                        .Append(" d=").Append(s.Duration.ToString("0.00"))
                        .Append(s.HasOpacity ? " op=" + s.Opacity.ToString("0.00") : "")
                        .Append(s.HasScale ? " sc=" + s.Scale.x.ToString("0") : "");
                }
                shown++;
            }
            Main.Logger.Log(builder.ToString());
        }

        public void EndSession()
        {
            floors = new FloorAnimation[0];
        }
    }
}
