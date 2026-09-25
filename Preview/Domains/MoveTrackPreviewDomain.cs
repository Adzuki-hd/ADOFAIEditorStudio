using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 编辑器场景的绝对 MoveTrack 求值。它刻意在每次采样时
    /// 都从捕获的格默认值起步，这样反向拖动
    /// 不会留下 tween 或 transform 残迹。
    /// </summary>
    internal sealed class MoveTrackPreviewDomain : IPreviewDomain
    {
        private sealed class MoveEvent
        {
            internal double Start;
            internal float Duration;
            internal Ease Ease;
            internal int StartFloor;
            internal int EndFloor;
            internal int GapLength;
            internal bool HasPosition;
            internal Vector2 Position;
            internal bool HasRotation;
            internal float Rotation;
            internal bool HasScale;
            internal Vector2 Scale;
            internal bool HasOpacity;
            internal float Opacity;
        }

        private sealed class FloorDefault
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Scale;
            internal float Opacity;
        }

        private readonly List<MoveEvent> events = new List<MoveEvent>();

        /// <summary>
        /// 能影响每一格的事件，在 Analyze 里预先算好。
        ///
        /// 求值那一趟以前要走遍全部 2356 个事件、对 1133 个格各来一遍
        /// ——每帧约 270 万次迭代，每次还带一次范围判断和一个
        /// 取模——实测约 8ms，压过了整个预览。
        /// 按格分桶把它变成只走一遍该格真正拥有的事件。
        /// 表项保持起始时间顺序，所以内层循环照样可以
        /// 一遇到还没开始的事件就停。
        /// </summary>
        private List<MoveEvent>[] eventsByFloor = new List<MoveEvent>[0];
        private FloorDefault[] defaults = new FloorDefault[0];
        private AnimateTrackPreviewDomain animateTrack;

        // ── 视口分块 ──────────────────────────────────────────────────────
        //
        // 只给「相机当前看得见的块」里的格写状态，看不见的格一次都不碰。
        // 这不是优化，它直接决定了画面：没被碰过的格保持原生烘焙的值。
        //
        // 这一点在这张谱上至关重要。原生的 ApplyEventsToFloors 根本不应用
        // MoveTrack 的透明度（opacity 只在 PositionTrack 的 case 下处理），
        // 所以没被碰过的格是 opacity=1（全亮）。而谱面开场的 f0 事件
        // （range 1..1131, opacity 0, duration 0）会把整条轨道压成全透明。
        //
        // 全量求值 → 整条轨道透明（我们改之前的样子，也就是「比原版暗」）。
        // 分块求值 → 只把看得见的那几块压透明，其余保持全亮，相机移过去
        //             时那些块才被求值、才「突然」变样。
        //
        // 只碰可见块，是唯一能同时保住「原版观感」和「不整条轨道被压透明」
        // 的做法。
        private const float ChunkSize = 16f;

        // 可见块向外扩一圈再判定。按每格的影响范围算边界更精确，
        // 但那要为每格算包围盒；统一外扩一圈效果一样，代价小得多。
        private const int ChunkMargin = 1;

        private Dictionary<long, List<int>> chunkFloors;
        private readonly List<int> visibleFloors = new List<int>();

        /// <summary>
        /// 这一帧算好、但还没写下去的格状态。
        ///
        /// 为什么不直接写：**游戏的原生组件跑在求值器之后**，会把
        /// floor.transform 重置回烘焙值。实测同一帧里，求值刚写完是
        /// rot=45 / scale=0,2，到帧末读回来已经是 rot=0 / scale=1——
        /// 也就是玩家看到的一直是原始姿态，MoveTrack 等于没生效。
        ///
        /// 所以改成求值只算、入队，帧末在 LateUpdate 里再写。
        /// 这和粒子发射器图标、编辑器引导线是同一个修法。
        /// </summary>
        internal struct PendingWrite
        {
            internal scrFloor Floor;
            // 要写哪几个通道。MoveTrack 四个都写；AnimateTrack 只写它
            // 真正驱动的那些——它从不碰的通道绝不能被写回，否则会盖掉
            // MoveTrack 设的值（有张谱面用 MoveTrack(opacity 0) 藏后面的格，
            // 结果被全局消失动画把不透明度改回了基线）。
            internal bool WritePosition;
            internal bool WriteRotation;
            internal bool WriteScale;
            internal bool WriteOpacity;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Scale;
            internal float Opacity;
        }

        private readonly List<PendingWrite> pendingWrites = new List<PendingWrite>();

        /// <summary>
        /// 交一笔延迟写入。AnimateTrack 用它排在自己那些格上，
        /// 排在 MoveTrack 之后，于是在帧末下发时仍然赢过 MoveTrack——
        /// 与两者原来直写时的先后关系一致。
        /// </summary>
        internal void QueueWrite(PendingWrite write)
        {
            pendingWrites.Add(write);
        }

        private static long ChunkKey(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        public string Name
        {
            get { return "MoveTrack"; }
        }

        /// <summary>
        /// AnimateTrack 也从基线重置出发写同一批格属性（position/rotation/scale/
        /// opacity）。把它做成独立域，会让两者
        /// 互相覆盖，因为本域每次采样都从起始
        /// 缩放重算，抹掉了动画状态。把它的片段
        /// 并进同一趟，就保证每格只有一个写入者、只有一种顺序。
        /// </summary>
        internal void AttachAnimateTrack(AnimateTrackPreviewDomain domain)
        {
            animateTrack = domain;
            // 反向引用：AnimateTrack 的写入要经本域的延迟队列，
            // 排在 MoveTrack 之后，才能在帧末下发时保持「它胜出」。
            domain.AttachOwner(this);
        }

        public void Analyze(PreviewTimelineContext context)
        {
            events.Clear();
            if (animateTrack != null)
            {
                animateTrack.Analyze(context);
            }
            defaults = new FloorDefault[context.Floors.Count];
            for (int i = 0; i < context.Floors.Count; i++)
            {
                PreviewFloorSnapshot floor = context.Floors[i];
                defaults[i] = new FloorDefault
                {
                    Position = floor.StartPosition,
                    Rotation = Quaternion.Euler(floor.StartRotation),
                    Scale = floor.StartScale,
                    Opacity = floor.BaseOpacity
                };
            }

            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.MoveTrack);
            for (int i = 0; i < source.Count; i++)
            {
                PreviewEventSnapshot levelEvent = source[i];
                int startFloor;
                int endFloor;
                ResolveRange(context, levelEvent, out startFloor, out endFloor);
                Vector2 position = context.GetValue(levelEvent, "positionOffset", Vector2.zero);
                Vector2 scale = context.GetValue(levelEvent, "scale", new Vector2(100f, 100f));
                events.Add(new MoveEvent
                {
                    Start = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    StartFloor = startFloor,
                    EndFloor = endFloor,
                    GapLength = Mathf.Max(0, context.GetValue(levelEvent, "gapLength", 0)),
                    HasPosition = context.IsPropertyEnabled(levelEvent, "positionOffset"),
                    Position = position,
                    HasRotation = context.IsPropertyEnabled(levelEvent, "rotationOffset"),
                    Rotation = context.GetValue(levelEvent, "rotationOffset", 0f),
                    HasScale = context.IsPropertyEnabled(levelEvent, "scale"),
                    Scale = scale,
                    HasOpacity = context.IsPropertyEnabled(levelEvent, "opacity"),
                    Opacity = context.GetValue(levelEvent, "opacity", 100f) / 100f
                });
            }

            PreviewStableSort.Sort(events, (left, right) => left.Start.CompareTo(right.Start));
            BuildFloorBuckets(context.Floors.Count);
            BuildChunkIndex(context);
            LogEventTable(context);
            Main.Logger.Log("[StaticPreview/MoveTrack] analyzed " + events.Count + " events.");
        }

        /// <summary>
        /// 每个事件只登记到它真正覆盖的格上，并遵守它的
        /// gap stride。没有事件的格拿到的是空列表而不是 null，所以
        /// 求值循环不需要额外判断。
        /// </summary>
        private void BuildFloorBuckets(int floorCount)
        {
            eventsByFloor = new List<MoveEvent>[floorCount];
            for (int i = 0; i < floorCount; i++)
            {
                eventsByFloor[i] = new List<MoveEvent>();
            }

            for (int i = 0; i < events.Count; i++)
            {
                MoveEvent value = events[i];
                int stride = Mathf.Max(1, value.GapLength + 1);
                int first = Mathf.Clamp(value.StartFloor, 0, floorCount - 1);
                int last = Mathf.Clamp(value.EndFloor, 0, floorCount - 1);
                for (int floorIndex = first; floorIndex <= last; floorIndex += stride)
                {
                    eventsByFloor[floorIndex].Add(value);
                }
            }

            int bound = 0;
            for (int i = 0; i < floorCount; i++)
            {
                bound += eventsByFloor[i].Count;
            }
            Main.Logger.Log("[StaticPreview/MoveTrack] floor bindings=" + bound + ".");
        }

        /// <summary>
        /// 只读。打印每个 MoveTrack 事件解出的结果，这样「隐藏开关
        /// 没作用」这种反馈能拿解析出的范围和 opacity 去核对，
        /// 而不是靠猜测谱面是怎么写的。
        /// </summary>
        private void LogEventTable(PreviewTimelineContext context)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            for (int i = 0; i < events.Count && i < 40; i++)
            {
                MoveEvent e = events[i];
                bool reachesEnd = e.EndFloor >= context.Floors.Count - 2;
                if (!e.HasOpacity && !reachesEnd)
                {
                    continue;
                }
                Main.Logger.Log("[MoveTrackEvent] #" + i
                    + " start=" + e.Start.ToString("0.00")
                    + " dur=" + e.Duration.ToString("0.00")
                    + " floors=" + e.StartFloor + ".." + e.EndFloor
                    + " gap=" + e.GapLength
                    + " pos=" + (e.HasPosition ? e.Position.x + "," + e.Position.y : "-")
                    + " rot=" + (e.HasRotation ? e.Rotation.ToString("0") : "-")
                    + " scale=" + (e.HasScale ? e.Scale.x + "," + e.Scale.y : "-")
                    + " opacity=" + (e.HasOpacity ? e.Opacity.ToString("0.00") : "-")
                    + ".");
            }

            // 烤入的基线决定任何事件跑之前格长什么样，
            // 所以「前面几格看不见」直接读它就能定性。
            int[] probes = { 0, 1, 50, 90, 182, 183, 184, 200 };
            System.Text.StringBuilder baseLine = new System.Text.StringBuilder("[MoveTrackBaseline]");
            for (int i = 0; i < probes.Length; i++)
            {
                int index = probes[i];
                if (index >= defaults.Length || defaults[index] == null)
                {
                    continue;
                }
                baseLine.Append(" f").Append(index)
                    .Append("=").Append(defaults[index].Opacity.ToString("0.000"))
                    .Append("/live=")
                    .Append(context.GetFloorBinding(index) == null
                        ? "-"
                        : context.GetFloorBinding(index).opacity.ToString("0.000"));
            }
            Main.Logger.Log(baseLine.ToString());
        }

        /// <summary>
        /// 把一格放回烤入状态，只写真正漂移过的通道。
        /// 对没有 MoveTrack 事件覆盖的格调用它，那些格
        /// 可能已被别的域做过动画。
        /// </summary>
        private static void RestoreBaseline(scrFloor floor, FloorDefault baseline)
        {
            Vector3 wantedPosition = new Vector3(
                baseline.Position.x, baseline.Position.y, floor.transform.position.z);
            if ((floor.transform.position - wantedPosition).sqrMagnitude > 0.000001f)
            {
                floor.transform.position = wantedPosition;
            }

            Quaternion wantedRotation = Quaternion.Euler(0f, 0f, baseline.Rotation.eulerAngles.z);
            if (Quaternion.Angle(floor.transform.rotation, wantedRotation) > 0.02f)
            {
                floor.transform.rotation = wantedRotation;
            }

            // z 分量不动，跟各域写缩放的方式一致。
            Vector3 wantedScale = new Vector3(
                baseline.Scale.x, baseline.Scale.y, floor.transform.localScale.z);
            if ((floor.transform.localScale - wantedScale).sqrMagnitude > 0.000001f)
            {
                floor.transform.localScale = wantedScale;
            }

            if (Mathf.Abs(floor.opacity - baseline.Opacity) > 0.002f)
            {
                floor.SetOpacity(baseline.Opacity);
            }
        }

        /// <summary>
        /// 按 16 世界单位一块，把每一格登记到它所在（以及它移动范围可及）的块上。
        /// 求值时只取出可见的那几块，其余格一律不碰——理由见 ChunkSize 那段说明。
        /// </summary>
        private void BuildChunkIndex(PreviewTimelineContext context)
        {
            if (chunkFloors == null)
            {
                chunkFloors = new Dictionary<long, List<int>>();
            }
            chunkFloors.Clear();

            int count = context.Floors.Count;
            for (int floorIndex = 0; floorIndex < count; floorIndex++)
            {
                PreviewFloorSnapshot snapshot = context.GetFloor(floorIndex);
                if (snapshot == null)
                {
                    continue;
                }

                // 一格的注册范围 = 它自己的位置，加上它身上所有
                // MoveTrack 的位移。不这么做的话，一个把格移出原位置的
                // 事件会跑到没登记的块上，那块一旦进入视野就没人管它。
                float minX = snapshot.StartPosition.x;
                float maxX = snapshot.StartPosition.x;
                float minY = snapshot.StartPosition.y;
                float maxY = snapshot.StartPosition.y;

                List<MoveEvent> events = floorIndex < eventsByFloor.Length
                    ? eventsByFloor[floorIndex]
                    : null;
                if (events != null)
                {
                    for (int i = 0; i < events.Count; i++)
                    {
                        MoveEvent value = events[i];
                        if (!value.HasPosition)
                        {
                            continue;
                        }
                        // null 解出来是 NaN，意思是「这个轴别动」，跳过。
                        if (!float.IsNaN(value.Position.x))
                        {
                            float dx = value.Position.x * context.TileSize;
                            if (dx < 0f) { minX += dx; } else { maxX += dx; }
                        }
                        if (!float.IsNaN(value.Position.y))
                        {
                            float dy = value.Position.y * context.TileSize;
                            if (dy < 0f) { minY += dy; } else { maxY += dy; }
                        }
                    }
                }

                RegisterRange(minX, minY, maxX, maxY, floorIndex);
            }
        }

        private void RegisterRange(float minX, float minY, float maxX, float maxY, int floorIndex)
        {
            int x0 = Mathf.FloorToInt(minX / ChunkSize);
            int x1 = Mathf.FloorToInt(maxX / ChunkSize);
            int y0 = Mathf.FloorToInt(minY / ChunkSize);
            int y1 = Mathf.FloorToInt(maxY / ChunkSize);
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    long key = ChunkKey(x, y);
                    List<int> bucket;
                    if (!chunkFloors.TryGetValue(key, out bucket))
                    {
                        bucket = new List<int>();
                        chunkFloors.Add(key, bucket);
                    }
                    bucket.Add(floorIndex);
                }
            }
        }

        /// <summary>
        /// 收集相机当前看得见的那些块里的格。取不到相机时返回 false，
        /// 调用方退化成全量求值——宁可慢一点，也不能漏掉该处理的东西。
        /// </summary>
        private bool CollectVisibleFloors()
        {
            visibleFloors.Clear();
            if (chunkFloors == null || chunkFloors.Count == 0)
            {
                return false;
            }

            scrCamera rig = scrCamera.instance;
            Camera view = rig == null ? null : rig.camobj;
            if (view == null || !view.gameObject.activeInHierarchy)
            {
                return false;
            }

            Vector3 center = view.transform.position;
            float halfHeight = view.orthographicSize;
            // 竖屏/窄视口时水平方向可能比竖直方向更宽，取两者的大值，
            // 宁可多算一块也不漏。
            float halfWidth = Mathf.Max(halfHeight * view.aspect, halfHeight);

            int x0 = Mathf.FloorToInt((center.x - halfWidth) / ChunkSize) - ChunkMargin;
            int x1 = Mathf.FloorToInt((center.x + halfWidth) / ChunkSize) + ChunkMargin;
            int y0 = Mathf.FloorToInt((center.y - halfHeight) / ChunkSize) - ChunkMargin;
            int y1 = Mathf.FloorToInt((center.y + halfHeight) / ChunkSize) + ChunkMargin;

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    List<int> bucket;
                    if (chunkFloors.TryGetValue(ChunkKey(x, y), out bucket))
                    {
                        visibleFloors.AddRange(bucket);
                    }
                }
            }
            return true;
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            // 上一帧的队列正常情况下已经被 ApplyPendingWrites 消费掉了；
            // 这里再清一次是防它没跑（比如工作台中途被关掉）。
            pendingWrites.Clear();
            bool chunked = CollectVisibleFloors();
            if (chunked)
            {
                for (int i = 0; i < visibleFloors.Count; i++)
                {
                    EvaluateFloor(frame, visibleFloors[i]);
                }
            }
            else
            {
                // 没有分块索引或取不到相机：退回全量，别让画面缺格。
                for (int floorIndex = 0; floorIndex < frame.Context.Floors.Count; floorIndex++)
                {
                    EvaluateFloor(frame, floorIndex);
                }
            }

            // 之后才跑，好让出现/消失动画对它涉及的格有最后发言权，
            // 这也和原生顺序一致：那里
            // ffxFloorAppearPlus/DisappearPlus 的 tween 是在
            // MoveTrack 的之后创建的。
            if (animateTrack != null)
            {
                animateTrack.Evaluate(frame);
            }
        }

        private void EvaluateFloor(PreviewEvaluationFrame frame, int floorIndex)
        {
            scrFloor floor = frame.Context.GetFloorBinding(floorIndex);
            FloorDefault baseline = defaults[floorIndex];
            if (floor == null || baseline == null)
            {
                return;
            }

            List<MoveEvent> floorEvents = floorIndex < eventsByFloor.Length
                ? eventsByFloor[floorIndex]
                : null;
            if (floorEvents == null || floorEvents.Count == 0)
            {
                // 没有事件拥有这一格，但它仍然得被维持在烤入
                // 状态：跑在本域之后的另一个域（全局轨道
                // 消失动画）会在播放头经过时淡化并移动每一格，
                // 所以不还原的话，没有一个自己的 MoveTrack 事件的格
                // 会在整个会话余下时间里一直保持淡出和位移。
                RestoreBaseline(floor, baseline);
                return;
            }

            Vector3 position = baseline.Position;
            float rotation = baseline.Rotation.eulerAngles.z;
            Vector2 scale = new Vector2(baseline.Scale.x, baseline.Scale.y) * 100f;
            float opacity = baseline.Opacity * 100f;

            for (int eventIndex = 0; eventIndex < floorEvents.Count; eventIndex++)
            {
                MoveEvent value = floorEvents[eventIndex];
                // 桶是按起始时间排的，所以后面的事件也都还没开始。
                if (value.Start > frame.Time)
                {
                    break;
                }

                float ratio = value.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((frame.Time - (float)value.Start) / value.Duration);
                float eased = PreviewEase.EasedValue(0f, 1f, ratio, value.Ease);
                // 谱面里的 null 解出来是 float.NaN，在原生效果里意思是
                // 「这个轴别动」。把 NaN 折进向量会
                // 污染格的 transform，再传播到
                // 行星和跟随相机，把画面弄黑。
                if (value.HasPosition)
                {
                    Vector3 target = baseline.Position;
                    if (!float.IsNaN(value.Position.x))
                    {
                        target.x += value.Position.x * frame.Context.TileSize;
                    }
                    if (!float.IsNaN(value.Position.y))
                    {
                        target.y += value.Position.y * frame.Context.TileSize;
                    }
                    position = Vector3.LerpUnclamped(position, target, eased);
                }
                if (value.HasRotation)
                {
                    rotation = Mathf.LerpUnclamped(rotation, baseline.Rotation.eulerAngles.z + value.Rotation, eased);
                }
                if (value.HasScale)
                {
                    Vector2 target = new Vector2(
                        float.IsNaN(value.Scale.x) ? scale.x : value.Scale.x,
                        float.IsNaN(value.Scale.y) ? scale.y : value.Scale.y);
                    scale = Vector2.LerpUnclamped(scale, target, eased);
                }
                if (value.HasOpacity)
                {
                    opacity = Mathf.LerpUnclamped(opacity, value.Opacity * 100f, eased);
                }
            }

            // 只入队，不写。真正的写入在 ApplyPendingWrites（LateUpdate），
            // 因为原生组件会在本帧稍后把这些值重置回烘焙状态。
            Vector3 wantedPosition = new Vector3(position.x, position.y, floor.transform.position.z);
            Quaternion wantedRotation = Quaternion.Euler(0f, 0f, rotation);
            Vector3 wantedScale = new Vector3(scale.x / 100f, scale.y / 100f, baseline.Scale.z);
            float wantedOpacity = Mathf.Clamp01(opacity / 100f);

            pendingWrites.Add(new PendingWrite
            {
                Floor = floor,
                WritePosition = true,
                WriteRotation = true,
                WriteScale = true,
                WriteOpacity = true,
                Position = wantedPosition,
                Rotation = wantedRotation,
                Scale = wantedScale,
                Opacity = wantedOpacity
            });
        }

        /// <summary>
        /// 把这一帧算好的格状态真正写下去。由
        /// PreviewEvaluationEngine.TickEditorScaffoldingLate 在 LateUpdate 里调用。
        ///
        /// 必须放在 LateUpdate：游戏自己的组件在 Update 阶段跑，
        /// 会把 floor.transform 重置回烘焙值。LateUpdate 保证在所有 Update
        /// 之后、渲染之前，那才是这一帧画面真正读到 transform 的时刻。
        ///
        /// 每一步都带变化检测，所以原生要是没覆盖，这里就是一次空转——
        /// 不会为了保险而无条件写几百个 transform。
        /// </summary>
        internal void ApplyPendingWrites()
        {
            if (pendingWrites.Count == 0)
            {
                return;
            }

            for (int i = 0; i < pendingWrites.Count; i++)
            {
                PendingWrite write = pendingWrites[i];
                scrFloor floor = write.Floor;
                if (floor == null)
                {
                    continue;
                }

                if (write.WritePosition)
                {
                    if ((floor.transform.position - write.Position).sqrMagnitude > 0.000001f)
                    {
                        floor.transform.position = write.Position;
                    }
                    // 游戏的 ffxMoveFloorPlus 用 PositionX/PositionY，
                    // 别的特效可能用整体的 Position，一并接管。
                    PreviewEase.TakeOverChannel(floor, TweenType.Position);
                    PreviewEase.TakeOverChannel(floor, TweenType.PositionX);
                    PreviewEase.TakeOverChannel(floor, TweenType.PositionY);
                }

                if (write.WriteRotation)
                {
                    if (Quaternion.Angle(floor.transform.rotation, write.Rotation) > 0.02f)
                    {
                        floor.transform.rotation = write.Rotation;
                    }

                    // **必须同步影子状态。** 游戏的旋转不是直接写在 transform 上的：
                    // tween 写 floor.tweenRot，再由它的 OnUpdate 每帧
                    // 推给 transform.eulerAngles。只改 transform 的话，
                    // 那个驻留的 tween 下一帧就把旧值推回来——
                    // 这正是「MoveTrack 看起来从没生效」的根因。
                    floor.tweenRot = write.Rotation.eulerAngles;
                    PreviewEase.TakeOverChannel(floor, TweenType.Rotation);
                }

                if (write.WriteScale)
                {
                    if ((floor.transform.localScale - write.Scale).sqrMagnitude > 0.000001f)
                    {
                        floor.transform.localScale = write.Scale;
                    }
                    PreviewEase.TakeOverChannel(floor, TweenType.Scale);
                    PreviewEase.TakeOverChannel(floor, TweenType.ScaleX);
                    PreviewEase.TakeOverChannel(floor, TweenType.ScaleY);
                }

                if (write.WriteOpacity)
                {
                    if (Mathf.Abs(floor.opacity - write.Opacity) > 0.002f)
                    {
                        floor.SetOpacity(write.Opacity);
                    }
                    PreviewEase.TakeOverChannel(floor, TweenType.Opacity);
                }
            }

            pendingWrites.Clear();
        }

        public void EndSession()
        {
            events.Clear();
            defaults = new FloorDefault[0];
            if (animateTrack != null)
            {
                animateTrack.EndSession();
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
                // 老谱面可能省略 tile 对。保留事件在它自己的格上。
            }
        }
    }
}
