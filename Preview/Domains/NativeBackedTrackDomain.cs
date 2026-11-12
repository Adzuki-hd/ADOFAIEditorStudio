using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;
using ADOFAIEditorStudio.Preview.Timeline;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 新的轨道求值域：把 MoveTrack 事件和翻译出来的出现/消失片段
    /// 合进同一张时间表，每格六个通道各自求值。
    ///
    /// **它取代 <c>MoveTrackPreviewDomain</c> + <c>AnimateTrackPreviewDomain</c>。**
    /// 那两个域写同一批通道，靠「谁该让位」的规则协调；那套规则没有单一
    /// 判据，反复调反复破。这里只有一张表，后启动的段覆盖先启动的，
    /// 是排序的自然结果。
    ///
    /// ## 求值
    ///
    /// Analyze 时把每格建成一个 <see cref="FloorTrackState"/>，
    /// 把属于它的所有片段按通道塞进对应的时间轴。
    /// Evaluate 时对每格调一次 `Evaluate(t)` 拿姿态，再写进场景。
    ///
    /// **没有跨帧状态。** 不读自己上一帧写了什么，不做增量推进，
    /// 不用「值没变就不写」那种缓存。给定时间就是给定画面——
    /// 这是能把拖动做对的前提。
    ///
    /// ## 可见性
    ///
    /// 沿用旧域的做法：只对「可见 ∪ 脏 ∪ 错峰片」求值，不是全谱每帧扫。
    /// 完全求值会在几千格的谱上每帧吃满主线程。
    /// </summary>
    internal sealed class NativeBackedTrackDomain : IPreviewDomain
    {
        public string Name
        {
            get { return "NativeBackedTrack"; }
        }

        private FloorTrackState[] floors = new FloorTrackState[0];
        private PreviewTimelineContext context;

        /// <summary>这一格本帧是否被求值过，供诊断用。</summary>
        private int evaluatedCount;

        // 本帧算好、还没写进场景的姿态。见 Evaluate 里的说明。
        private scrFloor[] pendingFloors = new scrFloor[0];
        private FloorPose[] pendingPoses = new FloorPose[0];
        private bool pendingValid;

        public void Analyze(PreviewTimelineContext source)
        {
            context = source;
            int count = source.Floors.Count;
            floors = new FloorTrackState[count];
            pendingFloors = new scrFloor[count];
            pendingPoses = new FloorPose[count];
            pendingValid = false;

            for (int i = 0; i < count; i++)
            {
                PreviewFloorSnapshot snapshot = source.GetFloor(i);
                FloorTrackState state = new FloorTrackState();
                if (snapshot != null)
                {
                    state.SetBaseline(
                        snapshot.StartPosition.x,
                        snapshot.StartPosition.y,
                        snapshot.StartRotation.z,
                        snapshot.StartScale.x * 100f,
                        snapshot.StartScale.y * 100f,
                        snapshot.BaseOpacity);
                }
                floors[i] = state;
            }

            BuildMoveTrackFragments(source);
            BuildAnimateTrackFragments(source);

            for (int i = 0; i < count; i++)
            {
                floors[i].Sort();
            }

            Main.Logger.Log("[NativeTrack] analyzed " + count + " floors.");
        }

        /// <summary>把每条 MoveTrack 事件拆成片段塞进它覆盖的格。</summary>
        private void BuildMoveTrackFragments(PreviewTimelineContext source)
        {
            IList<PreviewEventSnapshot> events = source.GetEvents(LevelEventType.MoveTrack);
            int count = source.Floors.Count;
            int fragmentCount = 0;

            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                int startFloor;
                int endFloor;
                ResolveRange(source, levelEvent, out startFloor, out endFloor);

                double start = source.GetEventStartTime(levelEvent);
                float duration = source.GetEventDuration(levelEvent);

                // 用带 NaN 语义的读法：数组里的 null 表示「这个轴别动」。
                Vector2 position = source.GetVector2WithNaN(levelEvent, "positionOffset", Vector2.zero);
                Vector2 scale = source.GetVector2WithNaN(levelEvent, "scale", new Vector2(100f, 100f));

                bool hasPosition = source.IsPropertyEnabled(levelEvent, "positionOffset");
                bool hasRotation = source.IsPropertyEnabled(levelEvent, "rotationOffset");
                bool hasScale = source.IsPropertyEnabled(levelEvent, "scale");
                bool hasOpacity = source.IsPropertyEnabled(levelEvent, "opacity");

                float rotation = source.GetValue(levelEvent, "rotationOffset", 0f);
                float opacity = source.GetValue(levelEvent, "opacity", 100f) / 100f;
                int gap = Mathf.Max(0, source.GetValue(levelEvent, "gapLength", 0));
                int stride = Mathf.Max(1, gap + 1);

                for (int floorIndex = startFloor; floorIndex <= endFloor; floorIndex += stride)
                {
                    if (floorIndex < 0 || floorIndex >= count)
                    {
                        break;
                    }

                    FloorTrackState state = floors[floorIndex];
                    PreviewFloorSnapshot snapshot = source.GetFloor(floorIndex);
                    float baseX = snapshot == null ? 0f : snapshot.StartPosition.x;
                    float baseY = snapshot == null ? 0f : snapshot.StartPosition.y;

                    // **位置偏移要乘 tileSize。**
                    //
                    // 谱面里的 `positionOffset: [2, 1]` 是「差几个格子」，
                    // 换算成世界坐标得乘格子尺寸。原生
                    // `ffxMoveFloorPlus.Decode` 就是 `targetPos = tileSize * vector`，
                    // 旧域也用 `frame.Context.TileSize`。
                    //
                    // 漏掉这一乘，所有带位移动画的轨道幅度都会差一个数量级
                    // （tileSize 默认约 10.3），而旋转和缩放不受影响——
                    // 表现就是「旋转缩放的动画看着对，但位置整体跟不上」。
                    float tileSize = source.TileSize;

                    // 每个轴单独加：NaN 的轴不加事件，就等于「这个通道不动」。
                    if (hasPosition && !float.IsNaN(position.x))
                    {
                        state.PositionX.Add(start, duration,
                            baseX + position.x * tileSize, ToEase(source, levelEvent));
                    }
                    if (hasPosition && !float.IsNaN(position.y))
                    {
                        state.PositionY.Add(start, duration,
                            baseY + position.y * tileSize, ToEase(source, levelEvent));
                    }
                    if (hasRotation)
                    {
                        float baseRotation = snapshot == null ? 0f : snapshot.StartRotation.z;
                        state.Rotation.Add(start, duration, baseRotation + rotation, ToEase(source, levelEvent));
                    }
                    if (hasScale && !float.IsNaN(scale.x))
                    {
                        state.ScaleX.Add(start, duration, scale.x, ToEase(source, levelEvent));
                    }
                    if (hasScale && !float.IsNaN(scale.y))
                    {
                        state.ScaleY.Add(start, duration, scale.y, ToEase(source, levelEvent));
                    }
                    if (hasOpacity)
                    {
                        state.Opacity.Add(start, duration, opacity, ToEase(source, levelEvent));
                    }
                    fragmentCount++;
                }
            }

            Main.Logger.Log("[NativeTrack] move fragments=" + fragmentCount
                + " events=" + events.Count + ".");
        }

        /// <summary>把出现 / 消失动画翻译成片段，塞进同一批时间轴。</summary>
        private void BuildAnimateTrackFragments(PreviewTimelineContext source)
        {
            int count = source.Floors.Count;
            AnimateTrackTranslator translator = new AnimateTrackTranslator();
            translator.SetFloorCount(count);

            for (int i = 0; i < count; i++)
            {
                PreviewFloorSnapshot snapshot = source.GetFloor(i);
                if (snapshot == null)
                {
                    continue;
                }
                translator.SetFloor(
                    i,
                    snapshot.EntryTime,
                    GetCrotchet(source, i),
                    snapshot.StartPosition.x,
                    snapshot.StartPosition.y,
                    snapshot.StartRotation.z,
                    snapshot.StartScale.x * 100f,
                    snapshot.StartScale.y * 100f,
                    snapshot.BaseOpacity);
            }

            // 全局设置：谱面可以在 settings 里声明默认动画，一个事件都没有。
            TrackAnimationType globalAppear = TrackAnimationType.None;
            TrackAnimationType2 globalDisappear = TrackAnimationType2.None;
            float globalAhead = 0f;
            float globalBehind = 0f;

            scnGame game = scnGame.instance;
            if (game == null && ADOBase.editor != null)
            {
                game = ADOBase.editor.customLevel;
            }
            LevelData levelData = game == null ? null : game.levelData;
            if (levelData != null)
            {
                globalAppear = levelData.trackAnimation;
                globalDisappear = levelData.trackDisappearAnimation;
                globalAhead = levelData.trackBeatsAhead;
                globalBehind = levelData.trackBeatsBehind;
            }

            translator.SetGlobalSettings(
                ToKind(globalAppear), globalAhead,
                ToKind(globalDisappear), globalBehind);

            IList<PreviewEventSnapshot> events = source.GetEvents(LevelEventType.AnimateTrack);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                TrackAnimationType appear = source.GetValue(levelEvent, "trackAnimation", TrackAnimationType.None);
                TrackAnimationType2 disappear = source.GetValue(levelEvent, "trackDisappearAnimation", TrackAnimationType2.None);
                float ahead = source.GetValue(levelEvent, "beatsAhead", 0f);
                float behind = source.GetValue(levelEvent, "beatsBehind", 0f);
                translator.AddEvent(levelEvent.Floor, ToKind(appear), ahead, ToKind(disappear), behind);
            }

            List<MoveTrackFragment> fragments = translator.Translate();
            int applied = 0;
            for (int i = 0; i < fragments.Count; i++)
            {
                MoveTrackFragment fragment = fragments[i];
                if (fragment.Floor < 0 || fragment.Floor >= count)
                {
                    continue;
                }

                FloorTrackState state = floors[fragment.Floor];
                Func<float, float> ease = LinearEase;
                if (fragment.HasX) { state.PositionX.Add(fragment.Start, fragment.Duration, fragment.X, ease); }
                if (fragment.HasY) { state.PositionY.Add(fragment.Start, fragment.Duration, fragment.Y, ease); }
                if (fragment.HasRotation) { state.Rotation.Add(fragment.Start, fragment.Duration, fragment.Rotation, ease); }
                if (fragment.HasScaleX) { state.ScaleX.Add(fragment.Start, fragment.Duration, fragment.ScaleX, ease); }
                if (fragment.HasScaleY) { state.ScaleY.Add(fragment.Start, fragment.Duration, fragment.ScaleY, ease); }
                if (fragment.HasOpacity) { state.Opacity.Add(fragment.Start, fragment.Duration, fragment.Opacity, ease); }
                applied++;
            }

            Main.Logger.Log("[NativeTrack] animate fragments=" + applied
                + " events=" + events.Count + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            evaluatedCount = 0;
            int count = floors.Length;
            if (count == 0)
            {
                pendingValid = false;
                return;
            }

            // **只算不写。**
            //
            // 游戏的原生组件跑在求值器**之后**，会把格的 transform 重置回
            // 烘焙值。在 Evaluate 里直接写，写完就被覆盖回去——画面上看到的
            // 是上一次的值，于是动画整体慢一拍。
            //
            // 所以这里只把结果存下来，由 <see cref="ApplyPending"/> 在
            // LateUpdate 里统一写。旧的 MoveTrack 域走的是同一条路
            // （理由见 PreviewEvaluationEngine.TickEditorScaffoldingLate
            // 里那段注释），这也是那个域一直没这个延迟的原因。
            for (int i = 0; i < count; i++)
            {
                scrFloor binding = frame.Context.GetFloorBinding(i);
                if (binding == null)
                {
                    continue;
                }

                pendingFloors[i] = binding;
                pendingPoses[i] = floors[i].Evaluate(frame.Time);
                evaluatedCount++;
            }

            pendingValid = true;
        }

        /// <summary>
        /// 本帧算好、但还没写进场景的这一格位置。
        ///
        /// **同帧内要问「这一格现在在哪」，必须走这里，不能读 transform.position。**
        /// 写入排在 LateUpdate（原因见 Evaluate 的注释），而球、相机、freeroam
        /// 都在求值阶段跑，比写入早——直接读 transform 拿到的是**上一帧**的位置。
        /// 轨道在动的时候，那一帧的差就是「球没粘在轨道上」「运镜偏一格」。
        ///
        /// 和旧的 MoveTrackPreviewDomain.TryGetPendingPosition 同签名，
        /// 由 PreviewEvaluationEngine.FloorPositionThisFrame 分派。
        /// </summary>
        internal bool TryGetPendingPosition(scrFloor floor, out Vector3 position)
        {
            position = Vector3.zero;
            if (!pendingValid || floor == null)
            {
                return false;
            }

            for (int i = 0; i < evaluatedCount; i++)
            {
                if (!ReferenceEquals(pendingFloors[i], floor))
                {
                    continue;
                }
                FloorPose pose = pendingPoses[i];
                position = new Vector3(pose.X, pose.Y, floor.transform.position.z);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 把这一趟算好的姿态真正写进场景。由
        /// <see cref="PreviewEvaluationEngine.TickEditorScaffoldingLate"/>
        /// 在 LateUpdate 里调用。
        /// </summary>
        internal void ApplyPending()
        {
            if (!pendingValid)
            {
                return;
            }
            pendingValid = false;

            bool wrote = false;
            for (int i = 0; i < evaluatedCount; i++)
            {
                scrFloor binding = pendingFloors[i];
                if (binding == null)
                {
                    continue;
                }
                Apply(binding, pendingPoses[i]);
                wrote = true;
            }

            if (wrote)
            {
                PreviewEvaluationEngine.NoteTransformWritten();
            }
        }

        private static void Apply(scrFloor floor, FloorPose pose)
        {
            Transform transform = floor.transform;
            Vector3 position = transform.position;
            if (!Approximately(position.x, pose.X) || !Approximately(position.y, pose.Y))
            {
                transform.position = new Vector3(pose.X, pose.Y, position.z);
            }

            if (!Approximately(transform.eulerAngles.z, pose.Rotation))
            {
                transform.rotation = Quaternion.Euler(0f, 0f, pose.Rotation);
            }

            Vector3 scale = transform.localScale;
            float wantedX = pose.ScaleX / 100f;
            float wantedY = pose.ScaleY / 100f;
            if (!Approximately(scale.x, wantedX) || !Approximately(scale.y, wantedY))
            {
                transform.localScale = new Vector3(wantedX, wantedY, scale.z);
            }

            float opacity = Mathf.Clamp01(pose.Opacity);
            if (!Approximately(floor.opacity, opacity))
            {
                floor.SetOpacity(opacity);
            }
        }

        private static bool Approximately(float a, float b)
        {
            return Mathf.Abs(a - b) <= 0.0001f;
        }

        public void EndSession()
        {
            floors = new FloorTrackState[0];
            context = null;
        }

        // ── 辅助 ──────────────────────────────────────────────────────

        private static readonly Func<float, float> LinearEase = value => value;

        private static Func<float, float> ToEase(PreviewTimelineContext source, PreviewEventSnapshot levelEvent)
        {
            Ease ease = source.GetEase(levelEvent);
            return value => PreviewEase.EasedValue(0f, 1f, value, ease);
        }

        private static float GetCrotchet(PreviewTimelineContext source, int floorIndex)
        {
            PreviewFloorSnapshot snapshot = source.GetFloor(floorIndex);
            if (snapshot == null)
            {
                return 0.5f;
            }
            float speed = snapshot.Speed <= 0f ? 1f : snapshot.Speed;
            float bpm = Mathf.Max(0.001f, source.BaseBpm * speed);
            return 60f / bpm;
        }

        private static void ResolveRange(PreviewTimelineContext source,
            PreviewEventSnapshot levelEvent, out int startFloor, out int endFloor)
        {
            startFloor = Mathf.Clamp(levelEvent.Floor, 0, source.Floors.Count - 1);
            endFloor = startFloor;
            try
            {
                Tuple<int, TileRelativeTo> start = levelEvent.GetTile("startTile");
                Tuple<int, TileRelativeTo> end = levelEvent.GetTile("endTile");
                startFloor = Mathf.Clamp(source.ResolveTile(start, levelEvent.Floor),
                    0, source.Floors.Count - 1);
                endFloor = Mathf.Clamp(source.ResolveTile(end, levelEvent.Floor),
                    0, source.Floors.Count - 1);
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

        /// <summary>游戏的出现动画枚举 → 翻译器用的枚举。数值顺序一致，直接转。</summary>
        private static AnimateTrackKind ToKind(TrackAnimationType type)
        {
            return (AnimateTrackKind)(int)type;
        }

        private static AnimateTrackKind ToKind(TrackAnimationType2 type)
        {
            return (AnimateTrackKind)(int)type;
        }
    }
}
