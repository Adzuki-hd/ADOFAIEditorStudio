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
    internal sealed class MoveTrackPreviewDomain : IPreviewDomain, IHistoryResettable
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

        // 本帧被排了位置写入的格。AnimateTrack 每格都要问一次
        // 「这一格的位置有没有人写」，线性扫队列会退化成 O(格数²)。
        private readonly HashSet<scrFloor> positionWriteFloors = new HashSet<scrFloor>();

        /// <summary>
        /// 当前**不在烘焙状态**的格。它们必须每帧被求值，哪怕被摄像机甩到
        /// 可见区块之外——否则播放头往回跳时没人把它们写回去。
        /// 见 Evaluate 里那段说明。
        /// </summary>
        private readonly HashSet<int> dirtyFloors = new HashSet<int>();
        /// <summary>求值顺序的临时集合（可见 ∪ 脏），复用以免每帧分配。</summary>
        private readonly HashSet<int> evaluationScratch = new HashSet<int>();

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
            // 会话重建：脏格集作废，全部按烘焙状态重新起步。
            dirtyFloors.Clear();
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
                // 必须用带 NaN 语义的读法，不能用通用 GetValue<Vector2>。
                //
                // 后者对 List<object> 遇到数组里的 null 会抛异常、然后**整份**
                // 回落到 fallback，于是 `positionOffset: [-1, null]` 变成 (0,0)——
                // 那一格的轨道就完全不动（Dream Flower 第 174 格就是这个）。
                //
                // 原生里数组里的 null 含义是「这个轴别动」，下面消费的地方
                // （写 target 那几行）早就按轴判 float.IsNaN 了，缺的只是
                // 在这里把 NaN 读进来。相机域踩过同一个坑，注释见
                // PreviewEventSnapshot.GetVector2WithNaN。
                Vector2 position = context.GetVector2WithNaN(levelEvent, "positionOffset", Vector2.zero);
                Vector2 scale = context.GetVector2WithNaN(levelEvent, "scale", new Vector2(100f, 100f));
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
            LogOpacityEvents();
            Main.Logger.Log("[StaticPreview/MoveTrack] analyzed " + events.Count + " events.");
        }

        /// <summary>
        /// 只读：把「会把轨道淡掉」的 MoveTrack 事件连同它解出的范围
        /// 全部打出来。轨道探针打开时跑一次，用来回答
        /// 「到底哪条事件把 214 格之后的轨道藏了」。
        /// </summary>
        private void LogOpacityEvents()
        {
            if (Main.Settings == null || !Main.Settings.TrackProbe)
            {
                return;
            }

            int logged = 0;
            for (int i = 0; i < events.Count; i++)
            {
                MoveEvent e = events[i];
                if (!e.HasOpacity || e.Opacity > 0.99f)
                {
                    continue;
                }
                logged++;
                if (logged > 24)
                {
                    break;
                }
                Main.Logger.Log("[MoveTrackOpacity] #" + i
                    + " start=" + e.Start.ToString("0.00")
                    + " dur=" + e.Duration.ToString("0.00")
                    + " floors=" + e.StartFloor + ".." + e.EndFloor
                    + " gap=" + e.GapLength
                    + " opacity=" + e.Opacity.ToString("0.00")
                    + ".");
            }
            Main.Logger.Log("[MoveTrackOpacity] hidden-event count=" + logged + ".");
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

            // **不恢复 opacity。**
            //
            // 不透明度是 AnimateTrack 域的通道：全局 trackDisappearAnimation
            // 正是靠它把每一格淡出的（原版给每一格都建
            // ffxFloorDisappearPlus，没有 MoveTrack 事件的格同样会淡）。
            //
            // 这里再写一次基线会把它抵消掉，而且和 scrFloor.LateUpdate
            // 抢同一个 LateUpdate 时段——实测 China E：淡出的格在播放头
            // 走过之后又整片亮回来，就是这个顺序竞争。
            //
            // 原先的注释说「不还原的话格会一直保持淡出」，把正确行为
            // 当成了 bug；本域只该恢复它自己管的位姿通道。
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
            positionWriteFloors.Clear();

            // **播放头跳转时把全部格子标脏一帧。**
            //
            // 由引擎在检测到跳转时通过 IHistoryResettable 通知（见
            // ResetHistory）。脏格集有个自举缺陷：它是由 EvaluateFloor 填的，
            // 而**被跳过的格子从来没被求值过**——所以它们连"变脏"的机会都没有。
            // 跳转事件本身把全部格子塞进去，才不依赖任何单格的求值结果。
            // 实测跳转那一帧会因此打出 changed=2003/2003。

            bool chunked = CollectVisibleFloors();
            if (chunked)
            {
                // **可见区块 ∪ 脏格集。**
                //
                // 只求值可见区块的话，被摄像机甩到区块外的格子**再也不会被
                // 求值**，于是永远停在上一次写入的状态。实测 Offshore（2003 格）
                // 的覆盖只有 135~350 格：播放头瞬间跳到远处时中间的区块全被跳过，
                // 那些格的位置/透明度/颜色就都停在被播放过的样子——而位置、
                // 透明度、颜色分属三个域却一起失效，正说明是"没人求值"。
                //
                // 慢拖之所以正常：相机一格格挪过去，每个区块都轮到。
                // 正常播放之所以正常：它没有瞬间跳转，唯一的瞬间动作
                // （回开头）还是带动画的。
                //
                // 脏格集 = 当前不在烘焙状态的格。它必然只有正在动的那一带
                // （实测远小于总格数），所以不牺牲这条优化本身。
                evaluationScratch.Clear();
                for (int i = 0; i < visibleFloors.Count; i++)
                {
                    evaluationScratch.Add(visibleFloors[i]);
                }

                if (dirtyFloors.Count > 0)
                {
                    foreach (int dirty in dirtyFloors)
                    {
                        evaluationScratch.Add(dirty);
                    }
                }

                foreach (int floorIndex in evaluationScratch)
                {
                    EvaluateFloor(frame, floorIndex);
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
                dirtyFloors.Remove(floorIndex);
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
                // 「这个轴别动」。**target 的初值必须是当前运行值 position，
                // 不能是 baseline**——这是 native 的逐轴语义：
                // ffxMoveFloorPlus.StartEffect 里对 NaN 的轴**根本不建 tween**
                // （if (!float.IsNaN(vector.x)) ...），那个轴保留上一条
                // tween 留下的值。
                //
                // 取 baseline 的后果：一条 positionOffset:[null,null] 的事件
                // （作者常用来只改透明度/缩放/旋转）会把同一桶里前面事件
                // 刚推开的位移整条抹回去。duration 为 0 时 eased 恒为 1，
                // 抹得干干净净——Dream Flower 第 390 格那条 range=0..389 的
                // [null,null] 就是它：实测 t=120.8 一到，365 那一组的位移
                // 集体弹回烘焙位置，看起来就是「球没粘在移动着的轨道上」。
                //
                // 反过来，作者**写了值**的轴仍然是绝对目标
                // （原生 target.startPos + positionOffset），不是增量。
                //
                // 整条属性被禁用的（HasPosition 为假）跳过：原生这时
                // positionUsed=false，一个轴的 tween 都不建，位置原样不动。
                //
                // 同一套逐轴语义在装饰物域和相机域早就写对了
                // （DecorationCorePreviewDomain 的 target 从 position 播种、
                // CameraPreviewDomain 的「NaN 轴保留携带值」），
                // 只有这里一直从 baseline 播种。
                if (value.HasPosition)
                {
                    Vector3 target = position;
                    if (!float.IsNaN(value.Position.x))
                    {
                        target.x = baseline.Position.x + value.Position.x * frame.Context.TileSize;
                    }
                    if (!float.IsNaN(value.Position.y))
                    {
                        target.y = baseline.Position.y + value.Position.y * frame.Context.TileSize;
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
            positionWriteFloors.Add(floor);

            // **这一格偏离烘焙状态了吗？** 偏离的进"脏格集"，于是每帧都会被
            // 求值——哪怕被摄像机甩到可见区块之外。播放头往回跳时（瞬间点击、
            // 极快拖动），只有这样它才有人把它写回去。回到烘焙状态就移出，
            // 所以这个集合的大小只跟"当前正在动的那一带"成正比。
            bool deviated = (wantedPosition - baseline.Position).sqrMagnitude > 0.000001f
                || Quaternion.Angle(
                    wantedRotation,
                    Quaternion.Euler(0f, 0f, baseline.Rotation.eulerAngles.z)) > 0.05f
                || (wantedScale - baseline.Scale).sqrMagnitude > 0.000001f
                || Mathf.Abs(wantedOpacity - baseline.Opacity) > 0.002f;
            if (deviated)
            {
                dirtyFloors.Add(floorIndex);
            }
            else
            {
                dirtyFloors.Remove(floorIndex);
            }
        }

        /// <summary>
        /// 本帧已经算好、但还没写进格子的位置。
        ///
        /// **同帧内要问「这一格现在在哪」，必须走这里，不能读
        /// transform.position。** 写入排在 LateUpdate（原因见 ApplyPendingWrites），
        /// 而球和相机是在求值阶段跑的，比写入早——读 transform 拿到的是
        /// **上一帧**的位置。轨道在动的时候，那一帧的差就是
        /// 「球没粘在轨道上」看起来的样子。
        /// </summary>
        internal bool TryGetPendingPosition(scrFloor floor, out Vector3 position)
        {
            if (floor != null)
            {
                for (int i = 0; i < pendingWrites.Count; i++)
                {
                    PendingWrite write = pendingWrites[i];
                    if (ReferenceEquals(write.Floor, floor))
                    {
                        position = write.Position;
                        return true;
                    }
                }
            }

            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// 这一帧是否已经给这一格排了写入（**任何通道**）。
        ///
        /// 两个调用方，判据是同一个，只是用途不同：
        ///   · AnimateTrack 判断"跑完的 appear 段还要不要继续主张位置"——
        ///     原生那条 appear tween 跑完就释放了通道，后启动的 MoveTrack
        ///     才接得了手；求值器里两域都每帧写，谁后写谁赢，所以跑完的段
        ///     必须主动让位，否则它会把 MoveTrack 的位移抹回烘焙值。
        ///   · AnimateTrack 判断"这一格的不透明度有没有别人在管"——有的话
        ///     本域就不该替它写回 1，否则会把 `MoveTrack(opacity 0)` 那种
        ///     隐藏又给撤销了。
        /// </summary>
        internal bool HasPendingWrite(scrFloor floor)
        {
            return floor != null && positionWriteFloors.Contains(floor);
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
            if (pendingWrites.Count == 0)            {
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

        /// <summary>
        /// 播放头跳转：把全部格子标脏一帧，让它们下一帧全部重算一次。
        /// 见 Evaluate 里那段说明——脏格集自己没法覆盖"从来没被求值过"的格。
        /// </summary>
        public void ResetHistory()
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                dirtyFloors.Add(i);
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
