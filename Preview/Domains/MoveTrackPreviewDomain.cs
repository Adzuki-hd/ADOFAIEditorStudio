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
            /// <summary>
            /// 这一格在谱面里的序号。合成之后要拿它更新 `dirtyFloors` ——
            /// 那个集合按**格号**存（不是格对象），而合成表的键是格对象。
            /// 两个域各自知道自己扫到的是第几格，顺手带过来，省一次反查。
            /// </summary>
            internal int FloorIndex;
            // 要写哪几个通道。MoveTrack 四个都写；AnimateTrack 只写它
            // 真正驱动的那些——它从不碰的通道绝不能被写回，否则会盖掉
            // MoveTrack 设的值（有张谱面用 MoveTrack(opacity 0) 藏后面的格，
            // 结果被全局消失动画把不透明度改回了基线）。
            internal bool WritePosition;
            internal bool WriteRotation;
            internal bool WriteScale;
            internal bool WriteOpacity;
            internal Vector3 Position;
            /// <summary>
            /// 目标旋转的 **z 欧拉角**（本域只写纯 Z 轴旋转）。
            /// 存 float 而不是 Quaternion：帧末比较写不写时只比一个角度，
            /// 不必构造四元数再算 acos。
            /// </summary>
            internal float RotationZ;
            internal Vector3 Scale;
            internal float Opacity;

            /// <summary>
            /// 这笔写入是**只为防漂移**排进来的（目标值和上次一样，
            /// 只是轮到了它那一槽强制重排）。
            ///
            /// 这种写入在 `ApplyPendingWrites` 里本该什么都不做 —— 目标值
            /// 早就在 Transform 上了。可它现在仍要读三次 `floor.transform`
            /// （position / eulerAngles / localScale），每次都是一趟
            /// native interop，只为了比较出"相等"。排查实测：稳态播放时
            /// `ScW.Queued` 恒为 88（= 总格数 / 30 的错峰片），而
            /// `ScW.Pos/Rot/Scale` 恒为 0 —— 也就是说这 88 笔**全部**是
            /// 这种白排，`Sc.Write` 那 0.11~0.13ms 全花在三次读回上。
            ///
            /// 当这笔写入下辖的每个通道都已经被我们接管（见 `floorTakeOverMask`），
            /// 读回来的值必然是目标值：接管把原生 tween 从 `floor.moveTweens`
            /// 里 Kill 掉了，`scrFloor.LateUpdate` 只读 position 不写，
            /// `scrFloor.Update` 只写 `floorRenderer.color`。所以这三次读
            /// 可以整段跳过，只留透明度那一笔（`floor.opacity` 是普通
            /// 托管字段，读它零 native 开销）。
            /// </summary>
            internal bool ForceOnly;

            /// <summary>
            /// 两笔写入要表达的目标值是不是同一个。
            ///
            /// 只比**值**，不比 `Floor` 引用之外的身份信息 —— 调用方保证是同一格。
            ///
            /// 容差刻意取得比 `ApplyPendingWrites` 里的写入阈值**松一倍**
            /// （写入是 sqr&gt;1e-6 / 0.02° / 0.002，这里用 4e-6 / 0.04° / 0.004）。
            /// 为什么必须更松：两者相等的话，一个正好卡在阈值附近抖动的值会
            /// **每帧都被判定为"变了"而重新入队，又在写入时被判为"没变"而跳过**
            /// —— 那就是白排一趟，优化等于没做。
            ///
            /// 松一倍仍然远小于任何真实动画的每帧变化量（1 秒完成的位移在 165fps
            /// 下每帧走完全程的 0.6‰），所以不会吃掉真正该写的那一帧。
            /// </summary>
            internal bool SameTarget(PendingWrite other)
            {
                if (WritePosition != other.WritePosition
                    || WriteRotation != other.WriteRotation
                    || WriteScale != other.WriteScale
                    || WriteOpacity != other.WriteOpacity)
                {
                    return false;
                }
                if ((Position - other.Position).sqrMagnitude > 0.000004f)
                {
                    return false;
                }
                if (Mathf.Abs(Mathf.DeltaAngle(RotationZ, other.RotationZ)) > 0.04f)
                {
                    return false;
                }
                if ((Scale - other.Scale).sqrMagnitude > 0.000004f)
                {
                    return false;
                }
                return Mathf.Abs(Opacity - other.Opacity) <= 0.004f;
            }
        }

        /// <summary>
        /// 每一格每隔这么多帧无条件重排一次，兜住「原生在我们写完之后又改了
        /// Transform」那种情况。165fps 下约 0.18 秒。
        /// 见 `lastQueuedTargets` 的完整说明。
        ///
        /// **按格错峰，不是全表一起重排**：重排日由 `ForceRewriteDue` 按格的
        /// 哈希取模决定。全表一起重排的话，每 30 帧就有一帧要重新排 4000 多笔
        /// （≈2ms）——那正是这个优化要消灭的开销，只是从每帧挪到了隔 30 帧。
        /// 错峰之后每帧只处理 1/30，摊平到约 0.07ms，而且任意一刻任何一格的
        /// 上次重排都不超过这个间隔。
        /// </summary>
        private const int ForceRewriteIntervalFrames = 30;

        private readonly List<PendingWrite> pendingWrites = new List<PendingWrite>();

        /// <summary>
        /// **本帧合成后的格状态：每格至多一条。**
        ///
        /// 原来 MoveTrack 和 AnimateTrack 各自往队列里塞一条，同一格可能排两笔
        /// （MoveTrack 全部通道 + AnimateTrack 它碰的那几个），靠入队先后来表达
        /// 「后写者胜」。那样没法判"这一格这一帧到底要不要写"——两笔互相覆盖，
        /// 逐笔比较必然是错的。
        ///
        /// 所以先合成：MoveTrack 打底（它四个通道都写），AnimateTrack 把它真正
        /// 驱动的通道覆盖上去。这一格这一帧的最终目标就确定了，比较才有了意义。
        ///
        /// 合成与原来"先后写两条"**逐通道等价**：ApplyPendingWrites 里每笔写入
        /// 独立判断、独立写通道，AnimateTrack 的 WriteXxx 为假的通道它本来就不碰。
        /// </summary>
        private readonly Dictionary<scrFloor, PendingWrite> frameTargets
            = new Dictionary<scrFloor, PendingWrite>();

        // 本帧被排了位置写入的格。AnimateTrack 每格都要问一次
        // 「这一格的位置有没有人写」，线性扫队列会退化成 O(格数²)。
        private readonly HashSet<scrFloor> positionWriteFloors = new HashSet<scrFloor>();

        /// <summary>
        /// 当前**不在烘焙状态**的格。它们必须每帧被求值，哪怕被摄像机甩到
        /// 可见区块之外——否则播放头往回跳时没人把它们写回去。
        /// 见 Evaluate 里那段说明。
        /// </summary>
        private readonly HashSet<int> dirtyFloors = new HashSet<int>();
        /// <summary>求值顺序的临时集合（可见 ∪ 脏 ∪ 错峰片），复用以免每帧分配。</summary>
        private readonly HashSet<int> evaluationScratch = new HashSet<int>();

        /// <summary>
        /// 这一帧是不是全谱都求值了（取不到相机时分块失效，退回全量）。
        /// AnimateTrack 要据此决定是跟着本帧的格集合走，还是自己全扫一遍。
        /// </summary>
        private bool evaluatedAllFloors;

        /// <summary>
        /// 本帧求值过的格号集合。**AnimateTrack 按它扫，不再自己全谱扫一遍。**
        ///
        /// 两个域必须同进同退：只让一个域跳过某一格的话，合成表里那一格就只剩
        /// 另一个域的值，帧末写下去等于把出现/消失动画撤销了。
        /// </summary>
        internal HashSet<int> EvaluatedFloors
        {
            get { return evaluationScratch; }
        }

        /// <summary>这一帧是不是全谱都求值了。见 `evaluatedAllFloors`。</summary>
        internal bool AllFloorsEvaluatedThisFrame
        {
            get { return evaluatedAllFloors; }
        }

        /// <summary>
        /// **上一次真正排进队列的每格目标值。** 用来判断「这一帧还值不值得再排一次」。
        ///
        /// 为什么需要：实测一帧排 4299 笔待写，而真正写进 Transform 的是 **0 笔**
        /// —— 也就是说 99.6% 的队列项在 `ApplyPendingWrites` 里逐项比较完之后
        /// 什么都没做，光这一趟就吃掉 2ms/帧（占 `Overlay.L` 的 99%）。
        ///
        /// 产生空转的原因不是求值错了，而是 `dirtyFloors` 的语义：它记的是
        /// 「偏离烘焙基线」的格，于是一个在 t=10 就被挪走、之后一直停在原地的
        /// 格会永远留在集合里，每帧重新求值、重新入队、再被判定为「不用写」。
        ///
        /// 判据放在**入队时**而不是出队时：入队时目标值就在手上，
        /// 且不用碰 `transform`；出队时必须已经读过 Transform 才能比。
        ///
        /// **但"上一帧排过同样的值"不等于"Transform 上已经是这个值"** ——
        /// 原生组件有可能在上一帧的 LateUpdate 之后把它改掉。所以这条优化
        /// 配了一条强制刷新：见 `ForceRewriteDue`。
        ///
        /// 表的大小由谱面格数封顶（只增不减，但每格最多一条，且 Analyze /
        /// 会话结束会整个清掉），不随播放时长增长。
        /// </summary>
        private readonly Dictionary<scrFloor, PendingWrite> lastQueuedTargets
            = new Dictionary<scrFloor, PendingWrite>();

        /// <summary>
        /// 这一格这一帧轮不轮得到强制重排。
        ///
        /// 把重排日按格错开：第 i 格在 `帧号 % 30 == i % 30` 那一帧轮到。
        /// 每帧只有一个槽到期，于是全表每 30 帧被完整重排一遍，但每一帧
        /// 只多排 1/30 的格——全表一起重排的话，每 30 帧就有一帧要重新排
        /// 4000 多笔（≈2ms），那正是这个优化要消灭的开销，只是从"每帧"
        /// 挪到了"隔 30 帧"。
        ///
        /// **用格号而不是对象哈希**：Evaluate 里的错峰求值片用的是
        /// `格号 % 30 == 帧号 % 30`，两边必须对齐——否则轮到的格那一帧
        /// 根本没被求值，也就不在合成表里，强制重排永远不会发生。
        /// </summary>
        private static bool ForceRewriteDue(int floorIndex)
        {
            return (Time.frameCount - floorIndex) % ForceRewriteIntervalFrames == 0;
        }

        /// <summary>
        /// 交一笔延迟写入。AnimateTrack 用它把**自己驱动的通道**覆盖到
        /// 本帧的合成目标上（MoveTrack 已经打好了底）。
        ///
        /// 它不再自己往队列里排——同一格这一帧只应该有**一条**目标，
        /// 否则「值有没有变」无从判断。见 `frameTargets` 的说明。
        /// </summary>
        internal void QueueWrite(PendingWrite write)
        {
            scrFloor floor = write.Floor;
            if (floor == null)
            {
                return;
            }

            PendingWrite merged;
            if (frameTargets.TryGetValue(floor, out merged))
            {
                // MoveTrack 这一帧已经给这格定了底，只覆盖它管的通道。
                // **不能整条替换**：AnimateTrack 从不碰的通道（比如只驱动
                // 透明度的那一族）留着 MoveTrack 的值，替换会把它抹回 0/基线。
                if (write.WritePosition) { merged.WritePosition = true; merged.Position = write.Position; }
                if (write.WriteRotation) { merged.WriteRotation = true; merged.RotationZ = write.RotationZ; }
                if (write.WriteScale) { merged.WriteScale = true; merged.Scale = write.Scale; }
                if (write.WriteOpacity) { merged.WriteOpacity = true; merged.Opacity = write.Opacity; }
            }
            else
            {
                merged = write;
            }

            frameTargets[floor] = merged;
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
            // 去重缓存同样作废：格子对象可能换了，旧的「上次排过什么」不再可信。
            lastQueuedTargets.Clear();
            frameTargets.Clear();
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
            // 只有真写了才登记「物理世界需要重新同步」（见 NoteTransformWritten）。
            bool wrote = false;

            Vector3 wantedPosition = new Vector3(
                baseline.Position.x, baseline.Position.y, floor.transform.position.z);
            if ((floor.transform.position - wantedPosition).sqrMagnitude > 0.000001f)
            {
                floor.transform.position = wantedPosition;
                wrote = true;
            }

            Quaternion wantedRotation = Quaternion.Euler(0f, 0f, baseline.Rotation.eulerAngles.z);
            if (Quaternion.Angle(floor.transform.rotation, wantedRotation) > 0.02f)
            {
                floor.transform.rotation = wantedRotation;
                wrote = true;
            }

            // z 分量不动，跟各域写缩放的方式一致。
            Vector3 wantedScale = new Vector3(
                baseline.Scale.x, baseline.Scale.y, floor.transform.localScale.z);
            if ((floor.transform.localScale - wantedScale).sqrMagnitude > 0.000001f)
            {
                floor.transform.localScale = wantedScale;
                wrote = true;
            }

            if (wrote)
            {
                PreviewEvaluationEngine.NoteTransformWritten();
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
            frameTargets.Clear();

            // **播放头跳转时把全部格子标脏一帧。**
            //
            // 由引擎在检测到跳转时通过 IHistoryResettable 通知（见
            // ResetHistory）。脏格集有个自举缺陷：它是由 EvaluateFloor 填的，
            // 而**被跳过的格子从来没被求值过**——所以它们连"变脏"的机会都没有。
            // 跳转事件本身把全部格子塞进去，才不依赖任何单格的求值结果。
            // 实测跳转那一帧会因此打出 changed=2003/2003。

            bool chunked = CollectVisibleFloors();
            // 【临时探针】求值循环与帧末合成分开计时：这两段加起来才是
            // 引擎里那一笔 `域 MoveTrack=…`，不拆开就不知道该砍哪一段。
            bool probe = PreviewDiagnostics.Enabled;
            long probeStart = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            int evaluatedFloors = 0;
            evaluatedAllFloors = !chunked;
            if (chunked)
            {
                // **可见区块 ∪ 脏格集 ∪ 错峰片。**
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
                // **脏格集的判据 2026-10-30 改过**，见 EvaluateFloor 末尾：
                // 从「偏离烘焙基线」改成「目标值相对上一帧变了」。
                // 旧判据的本意是保底，实测却把 2046/2619 格钉成永久脏格，
                // 每帧全谱重算（`MT.Loop=1.70ms` 里绝大部分是它），
                // 而这一帧真正需要重写的只有 0~2 笔（`ScW.Queued`）。
                //
                // 错峰片：每帧把 `格号 % 30 == 帧号 % 30` 的那一片也带上。
                // 屏幕外、目标值也没变的格会因此每 30 帧被完整重算一次，
                // 于是"屏幕外停着不动的格永远没人看"这件事有了上界（约 0.18 秒），
                // 代价是每帧多算 1/30 的格。
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

                int totalFloors = frame.Context.Floors.Count;
                int staleSlot = Time.frameCount % ForceRewriteIntervalFrames;
                for (int floorIndex = staleSlot; floorIndex < totalFloors; floorIndex += ForceRewriteIntervalFrames)
                {
                    evaluationScratch.Add(floorIndex);
                }

                foreach (int floorIndex in evaluationScratch)
                {
                    EvaluateFloor(frame, floorIndex);
                }
                evaluatedFloors = evaluationScratch.Count;
            }
            else
            {
                // 没有分块索引或取不到相机：退回全量，别让画面缺格。
                int count = frame.Context.Floors.Count;
                for (int floorIndex = 0; floorIndex < count; floorIndex++)
                {
                    EvaluateFloor(frame, floorIndex);
                }
                evaluatedFloors = count;
            }

            if (probe)
            {
                RecordSince("MT.Loop", probeStart);
                probeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            // 之后才跑，好让出现/消失动画对它涉及的格有最后发言权，
            // 这也和原生顺序一致：那里
            // ffxFloorAppearPlus/DisappearPlus 的 tween 是在
            // MoveTrack 的之后创建的。
            if (animateTrack != null)
            {
                animateTrack.Evaluate(frame);
            }

            if (probe)
            {
                RecordSince("AT.Eval", probeStart);
                probeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            // 两域都投完票了，现在才谈得上「这一格这一帧的最终目标是什么」。
            FlushFrameTargets();

            if (probe)
            {
                RecordSince("MT.Flush", probeStart);
                PreviewDiagnostics.RecordFlag("ScW.Eval", evaluatedFloors);
                // MoveTrack 这一帧拥有（有 MoveTrack 事件）的格数。它和
                // ScW.Targets 的差额就是「只被 AnimateTrack 碰到的格」——
                // 全局出现/消失动画会把每一格都扫一遍，那才是合成表总是满的
                // 真正原因。
                PreviewDiagnostics.RecordFlag("ScW.Own", positionWriteFloors.Count);
                ProbeNativeTweens(frame);
            }
        }

        /// <summary>
        /// 【临时探针】数一遍全谱还活着多少个原生 tween。
        ///
        /// 用途：判断"整帧里那 9~10ms 的原生开销"里有多少是 2619 个格子上
        /// 还挂着的 DOTween 组件每帧在跑。`PreviewEase.TakeOverChannel`
        /// 会把接管过的通道从 `floor.moveTweens` 里摘掉，而我们现在只对
        /// **求值过的格**（可见 ∪ 脏 ∪ 错峰片）做接管——屏幕外的格那些
        /// 原生 tween 就一直在跑。这个数字决定那条实不实。
        ///
        /// 每 120 帧数一次（全谱走一遍是 2619 次字典访问，不必每帧做）。
        /// </summary>
        private void ProbeNativeTweens(PreviewEvaluationFrame frame)
        {
            if (Time.frameCount - lastTweenProbeFrame < 120)
            {
                return;
            }
            lastTweenProbeFrame = Time.frameCount;

            int total = 0;
            int floorsWithTweens = 0;
            int playing = 0;
            int active = 0;
            int count = frame.Context.Floors.Count;
            for (int i = 0; i < count; i++)
            {
                scrFloor floor = frame.Context.GetFloorBinding(i);
                if (floor == null || floor.moveTweens == null || floor.moveTweens.Count == 0)
                {
                    continue;
                }
                total += floor.moveTweens.Count;
                floorsWithTweens++;
                foreach (KeyValuePair<TweenType, Tween> pair in floor.moveTweens)
                {
                    Tween tween = pair.Value;
                    if (tween == null)
                    {
                        continue;
                    }
                    // `active` = 还没被 Kill；`IsPlaying` = 真的在时间线上跑。
                    // 前者留着不花每帧的钱，后者才要 DOTween 每帧推值。
                    if (tween.active) { active++; }
                    // **必须先判 `active` 再问 `IsPlaying()`。**
                    //
                    // DOTween 的 `IsPlaying()` 对已经被 Kill 的 tween 会走
                    // `Debugger.LogInvalidTween`，每次同步写一行
                    // "This Tween has been killed and is now invalid"。
                    // 而重建时 `PrepareFloorTrackColours` 会给每一格造一条
                    // duration=0、当帧就 autoKill 的 tween 并留在
                    // `floor.moveTweens` 里——2620 格就是 2620 条死 tween。
                    //
                    // 探针每 120 帧扫一遍，于是每遍刷 2620 行警告：实测某次
                    // 会话日志 793,860 行、占全文 98%，全是这一行写出来的。
                    // 那不只是刷屏，还是同步的 Console.WriteLine。
                    if (tween.active && tween.IsPlaying()) { playing++; }
                }
            }
            PreviewDiagnostics.RecordFlag("SC.Tweens", total);
            PreviewDiagnostics.RecordFlag("SC.TwFloor", floorsWithTweens);
            PreviewDiagnostics.RecordFlag("SC.TwAct", active);
            PreviewDiagnostics.RecordFlag("SC.TwPlay", playing);
            // DOTween 全局正在播放的 tween 总数——这才是每帧真正被推进的数量。
            PreviewDiagnostics.RecordFlag("SC.DtPlay", DOTween.TotalPlayingTweens());
        }

        private int lastTweenProbeFrame = -1000;

        /// <summary>【临时探针】记一段耗时，配合 Evaluate 里的分段计时。</summary>
        private static void RecordSince(string name, long startTimestamp)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            PreviewDiagnostics.RecordPart(name,
                (now - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>
        /// 把本帧的合成目标转成待写队列：**只有值真的变了才排**，
        /// 同时维护 `dirtyFloors`。
        ///
        /// 必须在两域都求值完之后调用（见 Evaluate 末尾）。这一步之前
        /// `pendingWrites` 是空的，而球的圆心、运镜都靠
        /// `TryGetPendingPosition` 读它——所以这一步不能拖到 LateUpdate，
        /// 必须留在 MoveTrack 求值的最后一行：引擎里 FreeRoam / Planet / Camera
        /// 都排在 MoveTrack 之后、`ApplyPendingWrites` 之前。
        ///
        /// **`dirtyFloors` 的判据 2026-10-30 换成「相对上一帧有没有变」。**
        ///
        /// 旧判据是「偏离烘焙基线」，记的是**静止状态**而不是**变化**：
        /// 一张谱里被 MoveTrack 永久挪开的格会一直"偏离基线"，实测 2046/2619 格
        /// 被钉成永久脏格，每帧全谱重算（`MT.Loop=1.70ms` 主要是它），
        /// 而同一时刻真正需要重写的只有 0~2 笔（`ScW.Queued`）——
        /// 说明那 2046 格重算出来的结果和上一帧一模一样。
        ///
        /// 判据必须放在这里而不是 EvaluateFloor：只有合成之后才知道这一格
        /// 这一帧到底要变成什么。在 MoveTrack 那边判的话，"透明度被
        /// AnimateTrack 覆盖"的格会拿 MoveTrack 自己的透明度和合成值比，
        /// 每次都判成变了，脏格集又会涨回去。
        /// </summary>
        private void FlushFrameTargets()
        {
            foreach (KeyValuePair<scrFloor, PendingWrite> pair in frameTargets)
            {
                PendingWrite wanted = pair.Value;

                PendingWrite previous;
                bool changed = !lastQueuedTargets.TryGetValue(pair.Key, out previous)
                    || !previous.SameTarget(wanted);

                // 动画跑着的格每帧都变 → 一直脏 → 一直求值，行为不变；
                // 停在终态的格变一次就不再脏，屏幕外的就不再每帧重算。
                if (changed)
                {
                    dirtyFloors.Add(wanted.FloorIndex);
                }
                else
                {
                    dirtyFloors.Remove(wanted.FloorIndex);
                }

                if (!changed)
                {
                    // 目标值和上次排的完全一样：Transform 上早就是这个值了，
                    // 再排一遍只会在 ApplyPendingWrites 里白跑一趟。
                    // （上一轮的 4299 笔待写里 99.6% 是这种。）
                    // 只有轮到强制重排那一槽的格才放行，见 ForceRewriteDue。
                    if (!ForceRewriteDue(wanted.FloorIndex))
                    {
                        continue;
                    }

                    // 放行的这批是**纯防漂移**写入：目标值没变过。
                    // 打上标记，让 ApplyPendingWrites 跳过一次都不需要的
                    // Transform 读回（见 PendingWrite.ForceOnly 的说明）。
                    wanted.ForceOnly = true;
                }

                pendingWrites.Add(wanted);
                lastQueuedTargets[pair.Key] = wanted;
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
                // 这一格本帧不参与写入，把它从去重缓存里摘掉：
                // 它此后若被别的路径重新主张，那是全新的值，不该被旧记录挡掉。
                lastQueuedTargets.Remove(floor);
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
            Vector3 wantedScale = new Vector3(scale.x / 100f, scale.y / 100f, baseline.Scale.z);
            float wantedOpacity = Mathf.Clamp01(opacity / 100f);

            // 用 default 起手：这段是逐字段赋值，而结构体新增字段后
            // 每处构造点都必须把它也赋上，否则编译器会报「使用了未赋值的
            // 局部变量」。从 default 起手，新字段自动落到零值，
            // 以后再增字段也不会漏。
            PendingWrite wanted = default(PendingWrite);
            wanted.Floor = floor;
            wanted.FloorIndex = floorIndex;
            wanted.WritePosition = true;
            wanted.WriteRotation = true;
            wanted.WriteScale = true;
            wanted.WriteOpacity = true;
            wanted.Position = wantedPosition;
            wanted.RotationZ = rotation;
            wanted.Scale = wantedScale;
            wanted.Opacity = wantedOpacity;

            // **只登记本帧目标，不排队。**
            //
            // 队列要等 AnimateTrack 也投完票再统一生成（见 FlushFrameTargets）：
            // 同一格同一帧只能有一条最终目标，"这一格要不要写"才有唯一答案。
            // 上一版在这里直接比较入队，结果同一格被两域各排一笔、互相覆盖，
            // 比较永远不可能命中，优化等于没做。
            frameTargets[floor] = wanted;
            positionWriteFloors.Add(floor);

            // `dirtyFloors` 的维护挪到 FlushFrameTargets 里做 —— 判据要用
            // **合成之后**的值（含 AnimateTrack 覆盖的通道），在这里判会把
            // "被 AnimateTrack 覆盖了透明度"的格永远判成变了。
            // 见 FlushFrameTargets 里那段说明。
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
                // 队列空也要打诊断：去重之后这会是常态，
                // 不打就会一直看着上一帧的旧数字，误以为队列还是 4000 多笔。
                RecordWriteFlags(0, 0, 0, 0);
                return;
            }

            // **通道接管位掩码：同一个通道摘过一次就不用再摘。**
            //
            // `TakeOverChannel` 只是把游戏原生 tween 从 `floor.moveTweens`
            // 里杀掉并移除 —— 摘过之后词典里就没有了，之后再查必然落空。
            // 可我们每帧对每格调 4 次（Position / PositionX / PositionY /
            // Rotation），4000+ 笔就是 1.6 万次空转的 `TryGetValue`。
            //
            // 位掩码记住「这一格已经摘过哪些通道」，摘过的直接跳过。
            // 只省字典查询，不改变任何写入行为 —— 这是本次优化里
            // 唯一不影响轨道的部分。
            // 诊断：队列长度 vs 真正写下去的笔数。
            // 两者之差就是「变化检测」挡掉的空转——如果队列很长而实际写入很少，
            // 2ms 花在遍历和比较上；如果两者接近，那就是真在写。
            int writePositionCount = 0;
            int writeRotationCount = 0;
            int writeOpacityCount = 0;
            int writeScaleCount = 0;
            for (int i = 0; i < pendingWrites.Count; i++)
            {
                PendingWrite write = pendingWrites[i];
                scrFloor floor = write.Floor;
                if (floor == null)
                {
                    continue;
                }

                int taken = floorTakeOverMask.TryGetValue(floor, out int mask) ? mask : 0;

                // 这笔是不是「纯防漂移」写入，且它下辖的每个姿态通道都已经
                // 在我们手里？是的话，Transform 上必定已经是目标值 ——
                // 三次读回可以整段跳过（见 PendingWrite.ForceOnly）。
                // 透明度不参与这个判断：`floor.opacity` 是普通托管字段，
                // 读它不花钱，而且它确实可能被别处动过，必须照常比较。
                bool skipPoseReads = write.ForceOnly;
                if (skipPoseReads)
                {
                    int poseMask = 0;
                    if (write.WritePosition)
                    {
                        poseMask |= TakeOverPositionMask;
                    }
                    if (write.WriteRotation)
                    {
                        poseMask |= TakeOverRotationMask;
                    }
                    if (write.WriteScale)
                    {
                        poseMask |= TakeOverScaleMask;
                    }
                    // poseMask == 0 表示这笔只写透明度，那就没什么可跳的。
                    if (poseMask == 0 || (taken & poseMask) != poseMask)
                    {
                        skipPoseReads = false;
                    }
                }

                if (write.WritePosition)
                {
                    if (!skipPoseReads
                        && (floor.transform.position - write.Position).sqrMagnitude > 0.000001f)
                    {
                        floor.transform.position = write.Position;
                        writePositionCount++;
                    }
                    // 游戏的 ffxMoveFloorPlus 用 PositionX/PositionY，
                    // 别的特效可能用整体的 Position，一并接管。
                    if ((taken & TakeOverPositionMask) != TakeOverPositionMask)
                    {
                        PreviewEase.TakeOverChannel(floor, TweenType.Position);
                        PreviewEase.TakeOverChannel(floor, TweenType.PositionX);
                        PreviewEase.TakeOverChannel(floor, TweenType.PositionY);
                        taken |= TakeOverPositionMask;
                    }
                }

                if (write.WriteRotation)
                {
                    // 纯 Z 轴旋转：目标角入队时已经算好，这里只在真要写时构造四元数。
                    //
                    // **2026-10-30：比较方式从 `Quaternion.Angle` 换成 `DeltaAngle`。**
                    // 两者判的是同一件事（绕 Z 转了多少度），但 `Quaternion.Angle`
                    // 内部要构造四元数再做一次 acos —— 而这里每帧要跑 4000 多笔，
                    // 其中 99.6% 的结果是「不需要写」。A3 那轮已经在别处把
                    // `Quaternion.Angle` 换成 `DeltaAngle`，这里是最后一处遗留。
                    // 读 transform.eulerAngles.z 与目标 z 角直接比，语义等价、零 acos。
                    if (!skipPoseReads)
                    {
                        float currentZ = floor.transform.eulerAngles.z;
                        if (Mathf.Abs(Mathf.DeltaAngle(currentZ, write.RotationZ)) > 0.02f)
                        {
                            Quaternion wantedRotation = Quaternion.Euler(0f, 0f, write.RotationZ);
                            floor.transform.rotation = wantedRotation;
                            floor.tweenRot = wantedRotation.eulerAngles;
                            writeRotationCount++;
                        }
                    }

                    // **必须同步影子状态。**
                    //
                    // 游戏的旋转不是直接写在 transform 上的：tween 写
                    // floor.tweenRot，再由它的 OnUpdate 每帧推给
                    // transform.eulerAngles。只改 transform 的话，那个驻留的
                    // tween 下一帧就把旧值推回来 —— 这正是「MoveTrack 看起来
                    // 从没生效」的根因。
                    //
                    // 上面分支里已经写过一次；这里再兜一次，覆盖「角度没变但
                    // 原生 tween 改了影子值」的情况。纯内存写，不进 Unity。
                    floor.tweenRot = new Vector3(0f, 0f, write.RotationZ);

                    if ((taken & TakeOverRotationMask) == 0)
                    {
                        PreviewEase.TakeOverChannel(floor, TweenType.Rotation);
                        taken |= TakeOverRotationMask;
                    }
                }

                if (write.WriteScale)
                {
                    if (!skipPoseReads
                        && (floor.transform.localScale - write.Scale).sqrMagnitude > 0.000001f)
                    {
                        floor.transform.localScale = write.Scale;
                        writeScaleCount++;
                    }
                    if ((taken & TakeOverScaleMask) != TakeOverScaleMask)
                    {
                        PreviewEase.TakeOverChannel(floor, TweenType.Scale);
                        PreviewEase.TakeOverChannel(floor, TweenType.ScaleX);
                        PreviewEase.TakeOverChannel(floor, TweenType.ScaleY);
                        taken |= TakeOverScaleMask;
                    }
                }

                if (write.WriteOpacity)
                {
                    if (Mathf.Abs(floor.opacity - write.Opacity) > 0.002f)
                    {
                        floor.SetOpacity(write.Opacity);
                        writeOpacityCount++;
                    }
                    if ((taken & TakeOverOpacityMask) == 0)
                    {
                        PreviewEase.TakeOverChannel(floor, TweenType.Opacity);
                        taken |= TakeOverOpacityMask;
                    }
                }

                floorTakeOverMask[floor] = taken;
            }

            // 位姿通道只要真落进过 Transform，就登记一次物理同步需求。
            // opacity 走 SpriteRenderer，和碰撞体无关，不算。
            if (writePositionCount + writeRotationCount + writeScaleCount > 0)
            {
                PreviewEvaluationEngine.NoteTransformWritten();
            }

            RecordWriteFlags(writePositionCount, writeRotationCount, writeScaleCount, writeOpacityCount);
            pendingWrites.Clear();
        }

        /// <summary>
        /// 落诊断计数。这些数字是判断「去重到底有没有生效」的唯一依据：
        ///   · `ScW.Targets` —— 本帧求值了多少格（合成的目标条数）
        ///   · `ScW.Queued`  —— 去重之后真正排进队列的笔数
        ///   · `ScW.Pos/Rot/Scale/Opac` —— 队列里最终真的写进 Transform 的笔数
        /// 正常情况下应当看到 `Targets ≈ 4000`、`Queued` 只有几十，静止时接近 0。
        /// </summary>
        private void RecordWriteFlags(int pos, int rot, int scale, int opac)
        {
            if (!PreviewDiagnostics.Enabled)
            {
                return;
            }
            PreviewDiagnostics.RecordFlag("ScW.Targets", frameTargets.Count);
            PreviewDiagnostics.RecordFlag("ScW.Queued", pendingWrites.Count);
            PreviewDiagnostics.RecordFlag("ScW.Dirty", dirtyFloors.Count);
            PreviewDiagnostics.RecordFlag("ScW.Visible", visibleFloors.Count);
            PreviewDiagnostics.RecordFlag("ScW.Pos", pos);
            PreviewDiagnostics.RecordFlag("ScW.Rot", rot);
            PreviewDiagnostics.RecordFlag("ScW.Scale", scale);
            PreviewDiagnostics.RecordFlag("ScW.Opac", opac);
        }

        // 通道接管位掩码：每个 bit 代表「这一格的某个通道已经从原生 tween 手里摘过」。
        private const int TakeOverPositionMask = 1;
        private const int TakeOverRotationMask = 2;
        private const int TakeOverScaleMask = 4;
        private const int TakeOverOpacityMask = 8;

        /// <summary>
        /// 每格已接管的通道（位掩码）。见 ApplyPendingWrites 里的说明。
        /// 会话重建时清空 —— 新会话的格子上可能有新的原生 tween。
        /// </summary>
        private readonly Dictionary<scrFloor, int> floorTakeOverMask
            = new Dictionary<scrFloor, int>();

        public void EndSession()
        {
            events.Clear();
            defaults = new FloorDefault[0];
            floorTakeOverMask.Clear();
            lastQueuedTargets.Clear();
            frameTargets.Clear();
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
            // **去重缓存必须一起作废。**
            //
            // 跳转是最需要真正重写的场景：原生可能在对面的位置上把格挪到了
            // 完全不同的地方，而"上一帧我们排过什么"在这里没有任何参考价值 ——
            // 留着它会让跳转那一帧被去重掉，表现为"拖回去轨道不还原"。
            lastQueuedTargets.Clear();
            frameTargets.Clear();

            // **接管掩码一起作废。**
            //
            // `PendingWrite.ForceOnly` 靠这个掩码判断「这格的姿态通道在我们手里，
            // Transform 上必然就是目标值，读回可以跳过」。跳转之后这个前提不成立：
            // 原生可能重新给格挂上了 tween（换格引用、重新烤谱都会），
            // 那时 Transform 上的值不再等于我们记的目标值。
            // 清掉掩码让跳转后第一帧重新走一遍完整的「读回 → 比较 → 写 → 接管」，
            // 代价只有一帧，之后掩码会被重新填回来。
            // 严格说只有 `changed == false` 走强制重排的那批依赖它，而那些格
            // 本来就不需要还原；但「宁可多写一帧」比「万一漏掉一次还原」便宜得多。
            floorTakeOverMask.Clear();
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
