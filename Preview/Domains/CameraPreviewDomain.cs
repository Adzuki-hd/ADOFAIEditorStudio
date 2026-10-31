using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 预览相机的绝对时间求值。
    ///
    /// 原生相机是一台状态机，不是谱面的函数：每次踩到格，
    /// scrCamera.UpdateFollowCam 都会设置
    ///
    ///     frompos = 当前相机位置
    ///     topos   = 球的位置 + 偏移
    ///     timer   = 0
    ///
    /// 然后 scrCamera.Update 推进 pos = Lerp(frompos, topos, timer / camspeed)，
    /// 持续 camspeed 秒。当 camspeed = 2 * floorDuration 时，相机永远
    /// 在一个目标上用不完时间，下一个就来了，所以它始终落后
    /// 球大约两格。只用当前格去求值（一种局部
    /// 近似）会让画面一跳一跳地推进：拖动定位时球的变换本身就是
    /// 阶梯状的，朝着它做局部 lerp 只是把那些阶梯照搬出来。
    ///
    /// 这个域改为在 Analyze 里从第 0 格起把递推正向重放一遍，
    /// 并记下每个格边界处的相机位置。之后 Evaluate
    /// 在当前格内部采样，复杂度 O(1)，且只取决于谱面
    /// 和绝对时间。
    ///
    /// MoveCamera 通过原生效果用的同几条通道叠加在上层：
    /// 位置、旋转和缩放各自保留自己的上一个目标，并从
    /// 事件开始那一刻的值开始插值。
    /// </summary>
    internal sealed class CameraPreviewDomain : IPreviewDomain
    {
        private sealed class CameraKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal CamMovementType Mode;

            /// <summary>
            /// 这个事件到底有没有写 relativeTo。缺失意味着「继续沿用
            /// 已经生效的模式」，而不是「退回谱面默认值」，正是这个
            /// 区别决定了只带缩放的事件允不允许移动
            /// 相机。
            /// </summary>
            internal bool ModeUsed;

            internal bool HasPosition;
            internal Vector2 Position;
            internal bool HasRotation;
            internal float Rotation;
            internal bool HasZoom;
            internal float Zoom;
            internal int Floor;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<CameraKeyframe> keyframes = new List<CameraKeyframe>();

        // 预计算好的跟随状态，每个格边界一条。
        private Vector2[] cameraAtFloorEntry = new Vector2[0];
        // 每一格起步时的 zoom 基准。平时等于谱面的 zoom；freeroam 出场会把
        // 它设成 1（原生 MoveCameraToTile 明确传了 zoom: 1f），之后一直沿用。
        private float[] zoomBaseAtFloor = new float[0];
        private Vector2[] ballAtFloorEntry = new Vector2[0];
        // 长按格上相机跟随目标的**起点**和**终点**（两端都是球的位置）。
        // 非长按格两个都等于 ballAtFloorEntry，于是目标不随时间变，
        // 和以前完全一样。见 EvaluateFollow。
        private Vector2[] holdStartAtFloor = new Vector2[0];
        private Vector2[] holdEndAtFloor = new Vector2[0];
        private bool[] holdFloor = new bool[0];
        private Vector2[] floorPosition = new Vector2[0];
        private float[] floorEntryTime = new float[0];
        private float[] floorCamspeed = new float[0];
        // 这一格开始时球的跟随是否在跑，对应
        // scrCamera.followMode。由这一格或之前最后一个 MoveCamera 模式设置：
        // Player 打开它，Tile/Global 调 SetToFreeMode 关掉它，LastPosition
        // 保持原样。
        private bool[] followModeAtFloor = new bool[0];
        private PreviewTimelineContext probeContext;

        // 1045 那种「掉帧」演出：谱面在同一批 tag 里把相机甩到 10000 格外
        // （`[null, 10000]`），**同时**打开 HallOfMirrors。
        //
        // 原生那边镜厅的副作用是 Bgcamstatic 的 clearFlags 从 Color 改成 Depth，
        // 也就是「没有任何相机擦颜色」—— 主相机看不到东西时，屏幕留住上一帧。
        // 作者的 `fps` 标签（224 次 × 0.125 拍）要的就是这个：画面每次定住 0.06 秒，
        // 看起来像掉帧，不是黑屏。
        //
        // 预览必须每帧清屏（随机访问 + ImGui 都要干净缓冲），照抄就变成黑闪。
        // 所以这里换个等价做法：**远端帧整个换成上一张正常帧的时间重算**，
        // 求值器重算出来的画面与那张正常帧完全一致 —— 等价于「定格」，
        // 但不需要残留帧缓冲，也不会有旧内容的糊影。
        private const float HoldDistance = 500f;
        private const float HoldWindowSeconds = 0.5f;

        /// <summary>
        /// 当前引擎正在用的实例。求值引擎在建好帧时间之后、跑各域之前调用
        /// <see cref="ResolveFrameHold"/> —— 定格必须发生在**所有域**之前，
        /// 只把相机冻住的话，同一帧里的滤镜 / 装饰物透明度照样会变，还是会闪。
        /// </summary>
        internal static CameraPreviewDomain Active;

        private CamMovementType defaultMode;
        private Vector2 defaultPosition;
        private float defaultRotation;
        private float defaultZoom;
        private float tileSize = 1f;
        private int floorCount;

        /// <summary>
        /// 谱面是不是老格式的相对定位（`settings.version &lt; 11` 会自动置位，
        /// 也可以显式写 `legacyCamRelativeTo`）。
        ///
        /// **一条事件不写 `relativeTo` 时，两支的行为完全不同**，原生在
        /// ffxCameraPlus.StartEffect 里分岔：
        ///
        ///     if (legacyRelativeTo &amp;&amp; !movementTypeUsed) {
        ///         Vector2 p = camParent.position;
        ///         finalPos = positionUsed ? (targetPos + p) : p;
        ///     } else {
        ///         switch (movementTypeUsed ? movementType : lastUsedMovementType) { ... }
        ///     }
        ///
        /// 也就是说：**老格式把事件的位置当成「相对相机当前位置的偏移」，
        /// 而且完全不碰跟随模式**；新格式才是「继承上一个用过的模式」。
        ///
        /// 本域一直实现的是**新格式那一支**（注释里那套"缺失的
        /// relativeTo 是继承已生效的模式"，是为了修 M3 谱第 270/274/277 格
        /// 相机归零的问题，在非老格式谱上是对的）。
        ///
        /// 注意 legacy 那一支的公式和 `LastPosition` 分支**逐字相同**
        /// （都是 `camParent.position` 加上偏移），而且同样不改跟随模式。
        /// 所以这里不去新写一段逻辑，只在需要时把模式替换成 LastPosition，
        /// 复用本域早就调好的那条路径。见 EvaluateChannels 里的 resolveMode。
        /// </summary>
        private bool legacyRelativeTo;

        // 镜像事件链的工作值，纯粹是为了让 LogCameraCompare 能报出来。
        // 它们是 EvaluateChannels 里的局部变量；LastPosition 事件是针对
        // 被携带的目标解析的，所以相机落到世界原点时，看一眼这些值就能
        // 分辨出是携带值被清零，还是原点解析错了。
        private Vector2 diagOrigin;
        private Vector2 diagTarget;
        private Vector2 diagCarry;
        private Vector2 diagWorld;
        // 跟随轨道自己在当前格的数值，好让 LogCameraCompare 把它们
        // 摆到 CameraPathRecorder 捕获的原生 topos/frompos 旁边。跟随的
        // 推进是 ratio = floor duration / camspeed，而这个比值太大
        // 正是求值出来的相机跑在原生相机前面的原因。
        private Vector2 diagFollowCam;
        private Vector2 diagFollowBall;
        private float diagFollowSpeed;
        private float diagFollowDuration;
        // diagFollowDuration 背后的原始入场时刻，这样某个格长与
        // 原生跟随隐含的时长对不上时，可以追到那份快照上，而不是
        // 追到那次除法上。
        private float diagEntryTime;
        private float diagPrevEntryTime;
        private CamMovementType diagMode;
        private bool diagAnyPositionEvent;
        // 当前生效的旋转和缩放事件自己的数值。一个比原版更早到达
        // 目标的 tween，看起来会像原点错了、时长错了或缓动错了，
        // 只有这些值能把它们区分开。
        private float diagRotOrigin;
        private float diagRotTarget;
        private float diagRotStart;
        private float diagRotDuration;
        private Ease diagRotEase;
        private float diagZoomOrigin;
        private float diagZoomTarget;
        private float diagZoomStart;
        private float diagZoomDuration;
        private Ease diagZoomEase;
        // 当前生效的位置事件自己的数值，在它们离开作用域之前抓下来。
        // 没有任何事件开始、目标却变了，说明事件链捡起了一个它
        // 本不该捡的事件，只有这些值能说明是哪一个。
        private Vector2 diagPosOrigin;
        private Vector2 diagPosTarget;
        private float diagPosStart;
        private float diagPosDuration;
        private Ease diagPosEase;
        private int diagPosFloor;
        private int diagActiveFloor = -1;

        // 手动平移视图，累积自求值器没有做出的相机变化。
        // 每次新会话开始也会重置，好让新预览从居中打开。
        private static Vector2 userPan;
        private static Vector2 lastWrittenLocal;
        private static bool lastWrittenLocalValid;
        private static Vector2 lastLoggedPan;

        /// <summary>
        /// 置上后，检测到手动平移的那一帧就把平移丢掉，视图就停在
        /// 谱面把它放在的地方。这相当于缩放锁定，后者每帧把
        /// camUserSizeMultiplier 强制回 1——相机本身没有一个「用户位置」
        /// 字段，所以这个锁定改施加到累积的平移上。
        ///
        /// 之所以是 static，是因为平移累积量也是：新会话必须继承当前的锁定
        /// 状态，而不是把它重置。
        /// </summary>
        internal static bool PositionLocked
        {
            get { return positionLocked; }
            set
            {
                positionLocked = value;
                if (value)
                {
                    userPan = Vector2.zero;
                }
            }
        }

        private static bool positionLocked;

        /// <summary>
        /// 丢弃任何累积的手动平移，把视图还给谱面自己的取景。
        /// 锁定每帧都在隐式做同一件事；这个是它的一次性形式。
        /// </summary>
        internal static void ResetUserPan()
        {
            userPan = Vector2.zero;
        }

        // 稳定检测：原生效果会对上一个 tween 调 Kill(complete: true)，
        // 强制它先到终值，再开始下一个。只记录某个通道
        // 发生跳变的那些帧，就能看出这个求值器是否复现了那次
        // 稳定、幅度是多大。
        private float lastLoggedZoom = float.NaN;
        /// <summary>
        /// CameraCmp 的采样节流。
        ///
        /// 这条日志曾经每帧都打，占掉整个 Player.log 的 80%。它要回答的
        /// 是「求值器算出的相机位置和场景实际的对不对得上」，抽样看就够；
        /// 每帧都看既没有更多信息，又把真正有用的日志淹了。
        /// </summary>
        private const int CameraCompareIntervalFrames = 30;
        private int lastCameraCompareFrame = -10000;
        private Vector2 lastLoggedPosition = new Vector2(float.NaN, float.NaN);
        private float lastLoggedRotation = float.NaN;

        public string Name
        {
            get { return "Camera"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            tileSize = context.TileSize;

            // 新会话从没有平移的状态开始，所以视图打开时就在谱面说的位置。
            userPan = Vector2.zero;
            lastWrittenLocalValid = false;

            LevelData levelData = scnGame.instance == null ? null : scnGame.instance.levelData;
            defaultMode = levelData == null ? CamMovementType.Player : levelData.camRelativeTo;
            // 和原生赋值那一行同源：ffxCameraPlus.legacyRelativeTo = levelData.legacyCamRelativeTo
            legacyRelativeTo = levelData != null && levelData.legacyCamRelativeTo;
            defaultPosition = levelData == null ? Vector2.zero : levelData.camPosition;
            defaultRotation = levelData == null ? 0f : levelData.camRotation;
            float rawZoom = levelData == null ? 100f : levelData.camZoom;
            // scrCamera.UpdateSize 在 zoomSize == 0 时会禁用渲染相机，以前
            // 那会把视口弄成全黑。缺失的关卡值绝不能被读成零。
            defaultZoom = rawZoom <= 0.001f ? 1f : rawZoom / 100f;

            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.MoveCamera);
            for (int i = 0; i < source.Count; i++)
            {
                PreviewEventSnapshot levelEvent = source[i];
                keyframes.Add(new CameraKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Mode = context.GetValue(levelEvent, "relativeTo", defaultMode),
                    ModeUsed = context.IsPropertyEnabled(levelEvent, "relativeTo"),
                    // 原生 Decode 一定会读一个位置：
                    //   targetPos = RDUtils.GetRandomVector2(evnt, "position") * tileSize
                    // GetRandomVector2 对缺失的键返回 (0,0)，对数组里的 null
                    // 返回 NaN，所以每个 MoveCamera 都会解出一个位置，
                    // 下面那道逐事件门看到的就是 NaN 轴，而不是缺失的属性。
                    // 对一个省略了该键的事件报「没有位置」，会在 Factory 谱面上
                    // 整条跳过位置通道，而那张谱面第 135 格和
                    // 157-159 格写了缩放或旋转，位置则走默认。
                    HasPosition = context.HasPropertyKey(levelEvent, "position"),
                    Position = context.GetValue(levelEvent, "position", Vector2.zero),
                    // 与 ffxCameraPlus.Decode 完全一致：
                    //   rotationUsed = !evnt.disabled["rotation"];
                    //   targetRot    = RDUtils.GetRandomFloat(evnt, "rotation");
                    // GetRandomFloat 对缺失的键返回 0，而这道门只看
                    // disabled 标志——所以省略 rotation 的事件仍然会
                    // 转相机，转回零。要求该键必须存在，会让
                    // 旋转停在最后一个值上：在 M3 谱面上第 270、274
                    // 和 277 格省略了 rotation，原生相机回到了 0，而这边
                    // 一直卡在 -6.377。
                    HasRotation = context.HasPropertyKey(levelEvent, "rotation"),
                    Rotation = context.GetValue(levelEvent, "rotation", 0f),
                    HasZoom = context.IsPropertyEnabled(levelEvent, "zoom"),
                    Zoom = context.GetValue(levelEvent, "zoom", defaultZoom * 100f) / 100f,
                    Floor = levelEvent.Floor
                });

            }
            PreviewStableSort.Sort(keyframes, (left, right) => left.StartTime.CompareTo(right.StartTime));

            // 跟随递推的帧长。原生每帧用 Time.deltaTime 推进它的跟随
            // 计时器，而谱面的理想格间距没有算落地本身发生的那一帧，
            // 所以递推必须把它补回来。那一帧的大小就是游戏实际跑的帧长
            // ——在 M3 谱面上量到每格缺 ~0.0059s，当时游戏跑在 ~167fps。
            //
            // 但**不能读 TIME.SMOOTHDELTATIME 的瞬时值**。这个值会被烘进
            // 整条跟随轨道：它大一点，每格的推进就靠前一点；大到某个程度
            // 比值直接夹到 1，相机当场贴到球上——每走一格"卡"一下、
            // 平滑跟随整个消失。而重建往往正好发生在最慢的一帧
            // （刚拖完时间轴、整条轨道重算），于是这个缺陷看时机出现，
            // 也就是"有时"。
            //
            // 改用持续跟踪的帧长估计：它在每一帧的求值里缓慢跟随真实帧率，
            // 对单帧尖峰不敏感，并且夹在一个物理上合理的区间里
            // （游戏不会长期跑得比 240fps 还快、也不会比 60fps 还慢）。
            BuildFollowTrack(context, StableFrameSeconds);
            // 引擎会在跑各域之前问这个实例「这一帧要不要定格」。
            Active = this;
            Main.Logger.Log("[StaticPreview/Camera] analyzed " + keyframes.Count
                + " events, " + floorCount + " floors.");
        }

        // 持续跟踪的帧长。初值取 1/144：它只是"第一条轨道还没攒到数据时"
        // 的占位，下一次重建（拖动之后必然发生）就用上真实估计了。
        private float observedFrameSeconds = 1f / 144f;

        /// <summary>
        /// 稳定的帧长估计。每帧缓慢跟随真实帧率，单帧尖峰影响很小。
        /// </summary>
        private float StableFrameSeconds
        {
            get { return Mathf.Clamp(observedFrameSeconds, 1f / 240f, 1f / 60f); }
        }

        /// <summary>
        /// 逐帧更新帧长估计。放在求值里而不是重建里：重建本身可能
        /// 正好落在慢帧上，那正是要避免的取样点。
        /// </summary>
        private void TrackFrameSeconds()
        {
            float delta = Time.smoothDeltaTime;
            // 丢掉明显不合理的样本（暂停后恢复、场景切换那一帧）。
            if (delta > 1f / 400f && delta < 1f / 30f)
            {
                observedFrameSeconds = Mathf.Lerp(observedFrameSeconds, delta, 0.05f);
            }
        }

        /// <summary>
        /// 把原生跟随递推从第 0 格向前重放一遍：
        ///
        /// 用格锚点（而不是原始 transform）能让结果成为谱面的纯函数，
        /// 而把比值夹取到 1，可以让短格在 camspeed 小于格长时
        /// 仍然追得上。
        /// </summary>
        private void BuildFollowTrack(PreviewTimelineContext context, float frameSeconds)
        {

            floorCount = context.FloorCount;
            cameraAtFloorEntry = new Vector2[floorCount];
            zoomBaseAtFloor = new float[floorCount];
            ballAtFloorEntry = new Vector2[floorCount];
            holdStartAtFloor = new Vector2[floorCount];
            holdEndAtFloor = new Vector2[floorCount];
            holdFloor = new bool[floorCount];
            floorPosition = new Vector2[floorCount];
            floorEntryTime = new float[floorCount];
            floorCamspeed = new float[floorCount];
            followModeAtFloor = new bool[floorCount];

            // 哪些格的跟随在跑，来自事件的模式。在递推之前先建好，
            // 因为递推在每次 off->on 转换处都要重新播种。
            CamMovementType runningMode = defaultMode;
            for (int i = 0; i < floorCount; i++)
            {
                for (int k = 0; k < keyframes.Count; k++)
                {
                    CameraKeyframe keyframe = keyframes[k];
                    if (keyframe.Floor != i)
                    {
                        continue;
                    }
                    if (keyframe.ModeUsed)
                    {
                        runningMode = keyframe.Mode;
                    }
                }
                followModeAtFloor[i] = runningMode == CamMovementType.Player;
            }

            Vector2 camera = Vector2.zero;
            // 每一格起步时用哪个 zoom 当基准。平时就是谱面的 zoom，但 freeroam
            // 出场会把 zoom 设成 1，而且那之后一直不变——所以它得跟着递推走。
            float zoomBase = defaultZoom;
            for (int i = 0; i < floorCount; i++)
            {
                PreviewFloorSnapshot floor = context.GetFloor(i);

                // 两个位置必须分开：
                //
                //   floorPosition —— 格本身在哪（startPos + offsetPos）。
                //     相机 Tile 模式锚在它上面；自由段之后跟随重新播种
                //     也取它，因为那段结束时相机停在格位置上。
                //
                //   ballAtFloorEntry —— 球在哪。stickToFloor 关掉的格上，
                //     轨道被推走了而球还留在布局位，两者差一个 offsetPos。
                //     跟随追的是**球**：用格的位置会让相机跟着轨道偏出去，
                //     第 7 格 offset=(1,-1) 时正好是往右偏一格。
                Vector2 tileAnchor = floor == null
                    ? Vector2.zero
                    : new Vector2(floor.StartPosition.x, floor.StartPosition.y);
                Vector2 ballAnchor = floor == null
                    ? Vector2.zero
                    : new Vector2(floor.BallPosition.x, floor.BallPosition.y);
                float entryTime = floor == null ? 0f : Convert.ToSingle(floor.EntryTime);

                ballAtFloorEntry[i] = ballAnchor;
                floorPosition[i] = tileAnchor;
                floorEntryTime[i] = entryTime;

                // 长按格上球会沿一条滑移线走完整格，相机得跟着走。
                // 见 EvaluateFollow 里那段说明。
                holdFloor[i] = floor != null && floor.HoldLength > -1;
                if (holdFloor[i])
                {
                    holdStartAtFloor[i] = ToVector2(context.ResolveBallCentre(i));
                    holdEndAtFloor[i] = ResolveHoldEnd(context, i, ballAnchor);
                }
                else
                {
                    holdStartAtFloor[i] = ballAnchor;
                    holdEndAtFloor[i] = ballAnchor;
                }

                float camspeed = GetCameraSpeed(context, floor);
                floorCamspeed[i] = camspeed;

                if (i == 0)
                {
                    // 起点取球的位置：跟追随的本来就是球。
                    camera = ballAnchor;
                }
                else if (followModeAtFloor[i] && !followModeAtFloor[i - 1])
                {
                    // 自由段之后的一个 Player 事件会调 UpdateFollowCam(force: true)，
                    // 它会用相机那一刻所处的位置重新播种 frompos。自由段
                    // 把相机停在格位置上了（Tile 把 camParent 设为
                    // floorPos，Global 设为原点），所以轨道采纳的是那个位置，
                    // 而不是继续漂移。在 NYA 谱面上量到：162-163 格是
                    // Tile，把相机留在 126.26，而 164 格上的 Player 事件报的
                    // frompos 是 126.26——而一条直穿过去的轨道只到
                    // 124.76，差一格，让之后每一格都短暂落后。
                    camera = floorPosition[i - 1];
                }
                else
                {
                    float floorDuration = entryTime - floorEntryTime[i - 1];
                    if (floorDuration < 0f)
                    {
                        floorDuration = 0f;
                    }

                    // 原生跟随累积的是真实帧时长：
                    //
                    //   timer += Time.deltaTime;  pos = Lerp(frompos, topos, timer / camspeed)
                    //
                    // 所以一格的进度是实际流逝的墙上时间，而不是
                    // 谱面的理想间距。两者每格差一帧，因为
                    // 落地发生在某一帧的末尾，而理想间距是从
                    // 入场算到入场、中间没有帧。在 M3 谱面上拟合原生录制
                    // 自己的 frompos/topos，显示每一格都短了几乎
                    // 正好一帧——第 250 格需要 0.1150s 而实测 0.1090s，
                    // 252 格需要 0.0179 而实测 0.0120，如此类推，60fps 下稳定地差 ~0.006s。
                    // 那个系统性的亏空就是这台相机此前每次
                    // 尝试都残留的 ~0.07 偏移。
                    floorDuration += frameSeconds;

                    // 分母必须是「被推进的那一格」的速度，也就是第 i-1 格。
                    //
                    // 这个 lerp 走的是第 i-1 格的跟随：从它在 i-1 入口的位置，
                    // 朝 i-1 格的球锚点推进。用第 i 格的速度做分母，
                    // 在 BPM 恒定时两者相同、看不出问题；一旦
                    // 第 i 格上有 SetSpeed，分母就中途换了值，进度被整体缩放。
                    //
                    // 在 Factory 谱面上量到：第 26 格 BPM 翻倍，
                    // camspeed 从 0.923 掉到 0.462，于是这一格的入口位置按
                    // 两倍的进度算出，相机看起来被「提前结算」到目标。
                    float advanceSpeed = floorCamspeed[i - 1];
                    float ratio = advanceSpeed <= 0.0001f
                        ? 1f
                        : Mathf.Clamp01(floorDuration / advanceSpeed);
                    // 必须走和逐帧求值同一个采样函数。
                    //
                    // 以前这里直接朝 ballAtFloorEntry[i-1] 推进，也就是
                    // "球进这一格时的位置"。长按格上那个目标已经改成时变的
                    // 滑移线了，两处不一致的话，递推算出的"这一格结束时
                    // 相机在哪"会停在格心附近，而求值那边已经跟着球滑出去
                    // 5 格——落地下一格的那一帧相机就猛地跳回来。
                    camera = SampleFollow(i - 1, floorDuration, ratio);
                }

                // freeroam 出场。原生那条是 scrController.MoveCameraToTile
                // （29072）：建一个 Tile 模式的 ffxCameraPlus，把相机直接带到
                // **下一格**，而且 `zoom` 参数传的是 `1f`——不是"保持当前"，
                // 是明确设成 1。谱面默认 zoom 222 的关卡，出场后就变成 100。
                //
                // 出场发生在**上一格的末尾**，所以它决定的是**这一格**的起步
                // 位置和 zoom 基准——判据要用 i-1，不是 i。写成 i 会把入口那格
                // 自己的入口位置也改掉，表现就是"进自由段先跳一下、镜头被重置"。
                //
                // 只改逐帧求值不改这里的递推，就会反过来在落地下一格时跳回来
                // （和长按滑移那次是同一个毛病）。两头都要算。
                scrFloor previousBinding = i > 0 ? context.GetFloorBinding(i - 1) : null;
                if (previousBinding != null && previousBinding.freeroam
                    && previousBinding.moveCamAtFreeroamEnd && previousBinding.nextfloor != null)
                {
                    camera = new Vector2(
                        previousBinding.nextfloor.transform.position.x,
                        previousBinding.nextfloor.transform.position.y);
                    zoomBase = 1f;
                }

                cameraAtFloorEntry[i] = camera;
                zoomBaseAtFloor[i] = zoomBase;
            }
        }


        public void Evaluate(PreviewEvaluationFrame frame)
        {
            scrCamera camera = scrCamera.instance;
            if (camera == null || floorCount == 0)
            {
                return;
            }

            PreviewTimelineContext context = frame.Context;
            TrackFrameSeconds();
            int floorIndex = Mathf.Clamp(context.FindFloorAtOrBefore(frame.Time),
                0, floorCount - 1);

            // 原生模型里两份相机贡献是相加的：
            // camParent (MoveCamera) + camera.localPosition (球的跟随)。
            Vector2 follow = EvaluateFollow(floorIndex, frame.Time);

            // freeroam 出场运镜。原生 MoveCameraToTile 建的是一个 **Tile 模式**的
            // ffxCameraPlus——相机贴着那格平移到下一格，不是继续跟球。所以这里
            // 直接把跟随分量整个换掉，而不是在它上面再叠一条。
            //
            // 起点/终点/进度由 FreeRoamPreviewDomain 算好（它先跑），用的是原生
            // 那条 `duration = crotchet/speed * outTime * 0.5 / pitch` 加 outEase。
            if (FreeRoamPreviewDomain.ExitCameraActive)
            {
                follow = Vector2.LerpUnclamped(
                    FreeRoamPreviewDomain.ExitCameraFrom,
                    FreeRoamPreviewDomain.ExitCameraTo,
                    FreeRoamPreviewDomain.ExitCameraProgress);
            }

            Vector2 resolved = follow;

            diagFollowCam = cameraAtFloorEntry[floorIndex];
            diagFollowBall = ballAtFloorEntry[floorIndex];
            diagFollowSpeed = floorCamspeed[floorIndex];
            diagFollowDuration = floorIndex <= 0
                ? 0f
                : floorEntryTime[floorIndex] - floorEntryTime[floorIndex - 1];
            diagEntryTime = floorEntryTime[floorIndex];
            diagPrevEntryTime = floorIndex <= 0 ? 0f : floorEntryTime[floorIndex - 1];

            float rotation = defaultRotation;
            // zoom 的基准跟着递推走：freeroam 出场把它设成 1 之后就一直沿用。
            float baseZoom = zoomBaseAtFloor.Length > floorIndex
                ? zoomBaseAtFloor[floorIndex]
                : defaultZoom;
            float zoom = baseZoom;
            probeContext = context;
            // 走带缓存的版本：这一趟的入参和 `ResolveFrameHold` 那趟一样时
            // 直接拿它的结果，不再把事件链从头回放一遍（见 EvaluateChannelsForReuse）。
            EvaluateChannelsForReuse(floorIndex, frame.Time, follow,
                ref resolved, ref rotation, ref zoom);

            // freeroam 出场期间 zoom 从基准平滑到 1（原生的 ffxCameraPlus 带
            // zoom 目标，用的同一条 duration 和 outEase）。
            if (FreeRoamPreviewDomain.ExitCameraActive)
            {
                zoom = Mathf.LerpUnclamped(baseZoom, 1f, FreeRoamPreviewDomain.ExitCameraProgress);
            }

            if (float.IsNaN(resolved.x) || float.IsNaN(resolved.y)
                || float.IsInfinity(resolved.x) || float.IsInfinity(resolved.y))
            {
                return;
            }
            if (zoom <= 0.001f || float.IsNaN(zoom) || float.IsInfinity(zoom))
            {
                zoom = defaultZoom > 0.001f ? defaultZoom : 1f;
            }
            if (float.IsNaN(rotation) || float.IsInfinity(rotation))
            {
                rotation = defaultRotation;
            }

            ApplyCamera(camera, context, resolved, rotation, zoom);
            LogChannelJump(frame.Time, resolved, rotation, zoom);
            LogCameraCompare(frame.Time, floorIndex, resolved, rotation, zoom);
            // 轨道探针（设置 → 开发者里手动开）：相机应用之后再采样，
            // 这样屏幕坐标和视锥结果都是这一帧的真实值。
            TrackProbe.Tick(frame, camera);
        }

        /// <summary>
        /// 求值引擎的帧时间闸门：谱面把相机甩到极远时，这一帧整个按
        /// **上一张正常帧的时间**重算。
        ///
        /// 为什么放在引擎层而不是相机域：同一次 `fps` 循环里除了 MoveCamera，
        /// 还有 SetFilter / Bloom / MoveDecorations 透明度一起翻。只冻相机，
        /// 这些照样会在同一帧变，画面仍然是闪的。
        /// </summary>
        internal float ResolveFrameHold(PreviewTimelineContext context, float time)
        {
            // 这里**不做** `hasFarTargets` 预筛：预筛只是省一点开销，一旦它
            // 判错（第一次就是因为 `[null,10000]` 的 NaN 分量让模长变成 NaN，
            // 整段功能静默失效），表现就是"改了等于没改"。每帧多走一次
            // 通道求值的代价远小于一次误判。
            if (context == null || floorCount == 0)
            {
                return time;
            }

            if (!IsCameraFarAt(context, time))
            {
                return time;
            }

            // 定格到**哪一张**：最近一条"位置不是远端"的关键帧的起始时刻。
            //
            // 这里不能用"上一次采样到的正常帧"。预览一帧要 25–30ms，比这段
            // 谱面的正常窗口（5° = 17.5ms）还长，靠采样会整轮漏掉 —— 漏一轮就
            // 卡一下，这正是「稳定掉帧一会突然卡一会」的来源。改成按关键帧
            // 自己排的时间来定，跟帧率无关，每一轮的时长都是恒定的。
            float anchor = FindHoldAnchorTime(time);
            if (float.IsNaN(anchor) || time - anchor > HoldWindowSeconds)
            {
                return time;
            }

            return anchor;
        }

        /// <summary>
        /// 找出 `time` 之前最近一条**位置不是远端**的相机关键帧的时刻。
        ///
        /// 判据只看关键帧自己写的 `position`（逐轴、容忍 NaN 分量），不看解算后的
        /// 世界位置 —— 这一步每帧都要跑，不能对每个候选再做一次全量通道求值。
        /// </summary>
        private float FindHoldAnchorTime(float time)
        {
            int index = -1;
            for (int i = 0; i < keyframes.Count; i++)
            {
                if (keyframes[i].StartTime > time)
                {
                    break;
                }
                index = i;
            }
            if (index < 0)
            {
                return float.NaN;
            }

            for (int i = index; i >= 0 && index - i <= 40; i--)
            {
                CameraKeyframe keyframe = keyframes[i];
                if (time - keyframe.StartTime > HoldWindowSeconds)
                {
                    break;
                }

                // 只看**这一条真的写了的轴**。`position: [null, null]` 那种
                // （只改旋转 / 缩放的事件）一个轴都没写，它的相机位置是继承来的，
                // 拿它当锚点会把锚定在远端状态上 —— 结果还是黑。
                Vector2 probe = keyframe.Position;
                bool wroteAxis = false;
                bool farAxis = false;
                if (!float.IsNaN(probe.x))
                {
                    wroteAxis = true;
                    farAxis |= Mathf.Abs(probe.x) > HoldDistance;
                }
                if (!float.IsNaN(probe.y))
                {
                    wroteAxis = true;
                    farAxis |= Mathf.Abs(probe.y) > HoldDistance;
                }

                if (wroteAxis && !farAxis)
                {
                    return (float)keyframe.StartTime;
                }
            }

            return float.NaN;
        }

        /// <summary>
        /// 这个时间点的相机是不是被谱面甩到了极远（`position: [null, 10000]`
        /// 那一类）。纯计算，不写任何状态 —— 引擎每帧都要问一次。
        /// </summary>
        private bool IsCameraFarAt(PreviewTimelineContext context, float time)
        {
            int floorIndex = Mathf.Clamp(context.FindFloorAtOrBefore(time), 0, floorCount - 1);
            Vector2 follow = EvaluateFollow(floorIndex, time);
            if (FreeRoamPreviewDomain.ExitCameraActive)
            {
                follow = Vector2.LerpUnclamped(
                    FreeRoamPreviewDomain.ExitCameraFrom,
                    FreeRoamPreviewDomain.ExitCameraTo,
                    FreeRoamPreviewDomain.ExitCameraProgress);
            }

            Vector2 position = follow;
            float rotation = defaultRotation;
            float zoom = zoomBaseAtFloor.Length > floorIndex
                ? zoomBaseAtFloor[floorIndex]
                : defaultZoom;
            probeContext = context;
            // 这一趟是**缓存来源**：同帧稍后 `Evaluate` 会用同样的入参再问一次，
            // 那时直接取这里算好的结果。
            EvaluateChannelsForReuse(floorIndex, time, follow,
                ref position, ref rotation, ref zoom);

            if (float.IsNaN(position.x) || float.IsNaN(position.y)
                || float.IsInfinity(position.x) || float.IsInfinity(position.y))
            {
                return false;
            }

            return (position - follow).magnitude > HoldDistance;
        }

        /// <summary>
        /// 这台求值器解析出来的结果与场景相机实际落点的并排追踪，
        /// 外加原始输入。直接读那个差值，
        /// 比推理一个值属于哪个坐标空间要好。
        /// </summary>
        private void LogCameraCompare(float time, int floorIndex,
            Vector2 resolved, float rotation, float zoom)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            if (Time.frameCount - lastCameraCompareFrame < CameraCompareIntervalFrames)
            {
                return;
            }
            lastCameraCompareFrame = Time.frameCount;

            scrCamera camera = scrCamera.instance;
            if (camera == null)
            {
                return;
            }

            // 这一次求值只服务于打印，但打印里确实要它（follow= 那一项），
            // 所以留着。节流到 30 帧一次之后，它的代价也跟着降到 1/30。
            Vector2 follow = EvaluateFollow(floorIndex, time);
            Vector2 actual = camera.transform.position;
            Vector2 local = camera.transform.localPosition;
            Vector2 ball = GetBallPosition(Vector2.zero);
            Vector2 floorPos = floorPosition[Mathf.Clamp(floorIndex, 0, floorCount - 1)];

            Main.Logger.Log("[CameraCmp] t=" + time.ToString("0.000")
                + " f=" + floorIndex
                + " eval=" + resolved.x.ToString("0.00") + "," + resolved.y.ToString("0.00")
                + " actual=" + actual.x.ToString("0.00") + "," + actual.y.ToString("0.00")
                + " local=" + local.x.ToString("0.00") + "," + local.y.ToString("0.00")
                + " ball=" + ball.x.ToString("0.00") + "," + ball.y.ToString("0.00")
                + " floorPos=" + floorPos.x.ToString("0.00") + "," + floorPos.y.ToString("0.00")
                + " follow=" + follow.x.ToString("0.00") + "," + follow.y.ToString("0.00")
                + " shake=" + camera.shake.x.ToString("0.00") + "," + camera.shake.y.ToString("0.00")
                + " rot=" + rotation.ToString("0.0")
                + " zoom=" + zoom.ToString("0.000")
                // 事件链自己的数值。LastPosition 是针对被携带的
                // 目标解析的，所以当一个时长 0 的事件把相机扔到世界原点时，
                // 问题就是那一刻 eventCarry 是不是本来就已是零。
                + " origin=" + diagOrigin.x.ToString("0.00") + "," + diagOrigin.y.ToString("0.00")
                + " target=" + diagTarget.x.ToString("0.00") + "," + diagTarget.y.ToString("0.00")
                + " carry=" + diagCarry.x.ToString("0.00") + "," + diagCarry.y.ToString("0.00")
                + " world=" + diagWorld.x.ToString("0.00") + "," + diagWorld.y.ToString("0.00")
               
                + " mode=" + diagMode
                + " posEv=" + (diagAnyPositionEvent ? 1 : 0)
                + " pO=" + diagPosOrigin.x.ToString("0.00") + "," + diagPosOrigin.y.ToString("0.00")
                + " pT=" + diagPosTarget.x.ToString("0.00") + "," + diagPosTarget.y.ToString("0.00")
                + " pS=" + diagPosStart.ToString("0.000")
                + " pD=" + diagPosDuration.ToString("0.000")
                + " pE=" + diagPosEase
                + " pF=" + diagPosFloor
                + " rO=" + diagRotOrigin.ToString("0.00")
                + " rT=" + diagRotTarget.ToString("0.00")
                + " rS=" + diagRotStart.ToString("0.000")
                + " rD=" + diagRotDuration.ToString("0.000")
                + " rE=" + diagRotEase
                + " rT01=" + (diagRotDuration <= 0.0001f
                    ? "-"
                    : ((time - diagRotStart) / diagRotDuration).ToString("0.000"))
                // 旋转的 PreviewEase.EasedValue 原始输出，好让求值器的
                // 曲线能直接和原生那条比，而不用手工重新推导。
                // rT01 是喂进去的时间比例；rEv 是 DOTween 为它返回的值。
                + " rEv=" + (diagRotDuration <= 0.0001f
                    ? "-"
                    : PreviewEase.EasedValue(0f, 1f,
                        Mathf.Clamp01((time - diagRotStart) / diagRotDuration),
                        diagRotEase).ToString("0.0000"))
                + " zO=" + diagZoomOrigin.ToString("0.000")
                + " zT=" + diagZoomTarget.ToString("0.000")
                + " zS=" + diagZoomStart.ToString("0.000")
                + " zD=" + diagZoomDuration.ToString("0.000")
                + " fCam=" + diagFollowCam.x.ToString("0.00") + "," + diagFollowCam.y.ToString("0.00")
                + " fBall=" + diagFollowBall.x.ToString("0.00") + "," + diagFollowBall.y.ToString("0.00")
                + " fSpd=" + diagFollowSpeed.ToString("0.000")
                + " fDur=" + diagFollowDuration.ToString("0.000")
                + " fT=" + diagEntryTime.ToString("0.0000")
                + " fPT=" + diagPrevEntryTime.ToString("0.0000")
                + " fRatio=" + (diagFollowSpeed <= 0.0001f
                    ? "1.000"
                    : Mathf.Clamp01(diagFollowDuration / diagFollowSpeed).ToString("0.000"))
                + ".");
        }

        /// <summary>
        /// 只记录某个通道一步走得很远的那些帧。一次稳定
        /// （对上一个 tween 调 Kill(complete: true)）在这里表现为一次大跳变，
        /// 于是那个读起来像「大了 4 倍」的幅度就变成了一个可以和
        /// 事件时长对比的数字。
        /// </summary>
        private void LogChannelJump(float time, Vector2 position, float rotation, float zoom)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            float zoomDelta = float.IsNaN(lastLoggedZoom) ? 0f : Mathf.Abs(zoom - lastLoggedZoom);
            float positionDelta = float.IsNaN(lastLoggedPosition.x)
                ? 0f
                : Vector2.Distance(position, lastLoggedPosition);
            float rotationDelta = float.IsNaN(lastLoggedRotation)
                ? 0f
                : Mathf.Abs(rotation - lastLoggedRotation);

            lastLoggedZoom = zoom;
            lastLoggedPosition = position;
            lastLoggedRotation = rotation;

            if (zoomDelta < 0.05f && positionDelta < 0.5f && rotationDelta < 2f)
            {
                return;
            }

            Main.Logger.Log("[CameraJump] t=" + time.ToString("0.000")
                + " pos=" + position.x.ToString("0.00") + "," + position.y.ToString("0.00")
                + " dPos=" + positionDelta.ToString("0.00")
                + " rot=" + rotation.ToString("0.0")
                + " dRot=" + rotationDelta.ToString("0.0")
                + " zoom=" + zoom.ToString("0.000")
                + " dZoom=" + zoomDelta.ToString("0.000") + ".");
        }

        public void EndSession()
        {
            keyframes.Clear();
            userPan = Vector2.zero;
            lastWrittenLocalValid = false;
            cameraAtFloorEntry = new Vector2[0];
            ballAtFloorEntry = new Vector2[0];
            floorPosition = new Vector2[0];
            floorEntryTime = new float[0];
            floorCamspeed = new float[0];
            floorCount = 0;
        }

        /// <summary>
        /// 某个时刻播放头落在哪一格。二分 floorEntryTime，和
        /// PreviewTimelineContext.FindFloorAtOrBefore 同一套判据，
        /// 只是这里用已经预计算的表，省掉一次 context 依赖。
        /// </summary>
        private int FloorAtTime(float time)
        {
            if (floorCount == 0)
            {
                return 0;
            }
            int low = 0;
            int high = floorCount;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (floorEntryTime[middle] <= time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return Mathf.Clamp(low - 1, 0, floorCount - 1);
        }

        /// <summary>
        /// 在当前格内部采样预计算的跟随轨道。时间形式，给逐帧求值用。
        /// 实现只有一份，见 SampleFollow——递推那边也必须走它，
        /// 否则两处对"这一格结束时相机在哪"的答案会不一样。
        /// </summary>
        private Vector2 EvaluateFollow(int floorIndex, float time)
        {
            if (floorIndex >= floorCount)
            {
                return Vector2.zero;
            }

            return SampleFollow(floorIndex, time - floorEntryTime[floorIndex], float.NaN);
        }

        /// <summary>
        /// 跟随采样的唯一实现。
        ///
        /// ratioOverride 为 NaN 时按 elapsed / camspeed 算；递推那边
        /// 传它自己算好的比例（它要把落地那一帧的时长补上，见
        /// BuildFollowTrack 里的说明），否则两条式子会不一致。
        /// </summary>
        private Vector2 SampleFollow(int floorIndex, float elapsed, float ratioOverride)
        {
            Vector2 origin = cameraAtFloorEntry[floorIndex];
            if (elapsed <= 0f)
            {
                return origin;
            }

            Vector2 target = FollowTargetAt(floorIndex, elapsed);

            float ratio = ratioOverride;
            if (float.IsNaN(ratio))
            {
                float camspeed = floorCamspeed[floorIndex];
                ratio = camspeed <= 0.0001f ? 1f : Mathf.Clamp01(elapsed / camspeed);
            }

            return Vector2.LerpUnclamped(origin, target, ratio);
        }

        /// <summary>
        /// 这一格在 elapsed 时刻的跟随目标。
        ///
        /// **长按格上它是时变的。** 原生 scrCamera.Update：
        ///
        ///   pos = Vector2.Lerp(frompos, topos + offset + holdOffset, timer / camspeed);
        ///
        /// 其中 holdOffset 就是行星的 holdOffsetPos——scrPlanet.Update 的
        /// 长按分支里每帧 `camy.SetHoldOffset(holdOffsetPos)`。topos 是落地
        /// 那刻球的位置，加上这条滑移，正好是球此刻所在。所以相机是
        /// **跟着球一起沿长按线滑**的。
        ///
        /// 以前这里恒用 ballAtFloorEntry——球进这一格时的位置。长按的
        /// 滑移线长 5 格（`测试1` 第 25 格），于是整段里相机原地不动、
        /// 球已经滑出画面。
        ///
        /// 非长按格 holdStart == holdEnd == ballAtFloorEntry，
        /// 两条式子退化成同一条，行为不变。
        /// </summary>
        private Vector2 FollowTargetAt(int floorIndex, float elapsed)
        {
            if (!holdFloor[floorIndex])
            {
                return ballAtFloorEntry[floorIndex];
            }

            // 用和行星域同一条线性进度：滑移和角度共用它，
            // 不然会出现"球在滑、相机按另一条曲线追"。
            float duration = floorIndex + 1 < floorCount
                ? floorEntryTime[floorIndex + 1] - floorEntryTime[floorIndex]
                : 0f;
            float progress = duration <= 0.0001f ? 1f : Mathf.Clamp01(elapsed / duration);
            return Vector2.LerpUnclamped(
                holdStartAtFloor[floorIndex], holdEndAtFloor[floorIndex], progress);
        }

        /// <summary>
        /// 长按格上球滑到哪里。
        ///
        /// 原生 scrPlanet.Update 的长按分支：
        ///
        ///   targetPosition = currfloor.nextfloor.transform.position
        ///                    - tileSize * nextfloor.radiusScale
        ///                      * ClockwiseAngleToVector(currfloor.exitangle);
        ///
        /// 也就是"下一格的入口边缘"。和 PlanetPreviewDomain 里那条滑移
        /// 是同一个式子——改一边记得改另一边。
        /// </summary>
        private static Vector2 ResolveHoldEnd(
            PreviewTimelineContext context, int floorIndex, Vector2 fallback)
        {
            PreviewFloorSnapshot floor = context.GetFloor(floorIndex);
            PreviewFloorSnapshot next = context.GetFloor(floorIndex + 1);
            if (floor == null || next == null)
            {
                return fallback;
            }

            Vector3 centre = context.ResolveBallCentre(floorIndex + 1);
            double exitAngle = floor.ExitAngle;
            float edge = context.TileSize * next.RadiusScale;
            return new Vector2(
                centre.x - (float)Math.Sin(exitAngle) * edge,
                centre.y - (float)Math.Cos(exitAngle) * edge);
        }

        private static Vector2 ToVector2(Vector3 value)
        {
            return new Vector2(value.x, value.y);
        }

        /// <summary>
        /// 应用 MoveCamera。
        ///
        /// 原生模型里两份贡献是相加的：
        ///
        ///   world = camParent.position        (MoveCamera 事件)
        ///         + camera.transform.localPosition  (球的跟随)
        ///
        /// 所以事件永远不会替换跟随的那份偏移。把事件当成
        /// 整个相机位置，会在事件一触发的瞬间丢掉跟随分量，
        /// 读起来就是突然贴到事件目标上。
        ///
        /// 每个通道都从事件开始时它实际持有的值重新起步，
        /// 对应 StartEffect 会杀掉上一个 tween 而不让它跑完
        /// it.
        /// </summary>
        private void EvaluateChannels(int floorIndex, float time,
            Vector2 follow, ref Vector2 position, ref float rotation, ref float zoom)
        {
            CamMovementType mode = defaultMode;
            rotation = defaultRotation;
            // zoom **不**在这里重置成 defaultZoom：调用方已经把"这一格的 zoom
            // 基准"传进来了（freeroam 出场之后那个基准是 1，不是谱面的 zoom）。
            // 事件链就从它开始接。

            // 这个通道在 camParent 空间里累积，ResolveWorld
            // 产出的目标也在这个空间。用世界空间的跟随位置
            // （很大的坐标）给它播种，而目标却是很小的父空间偏移，
            // 会让每次插值都横穿整个世界，读起来是
            // 一次巨大的瞬移——幅度是真实事件位移的好几倍。
            Vector2 eventCarry = Vector2.zero;
            // 并行的携带值，持有上一个事件结束后的相机世界位置。
            // LastPosition 针对它解析；eventCarry 必须保留它自己的
            // 含义，因为上面的连续性数学要为 Player 目标把跟随加回来，
            // 那里放一个世界值会把跟随算两次。
            Vector2 eventWorld = Vector2.zero;
            // 对应 scrCamera.followMode：在 Tile/Global 事件释放相机之前一直为 true。
            bool followMode = true;
            bool anyPositionEvent = false;

            // 位置通道的逐轴 tween 状态。
            //
            // 原生效果把 moveXTween 和 moveYTween 跑成两个独立的 tween，
            // 只杀掉事件实际写过的那个轴：
            //
            //   if (!float.IsNaN(finalPos.x) || movementTypeUsed) { moveXTween?.Kill(complete: true); }
            //   if (!float.IsNaN(finalPos.y) || movementTypeUsed) { moveYTween?.Kill(complete: true); }
            //
            // 所以一个只移动 Y 的事件会让飞行中的 X tween 继续跑到
            // 它自己的完成点，按它自己的时钟。把两者合并成一对向量，
            // 会在下一个事件开始的瞬间结束那个 tween，于是该轴瞬移
            // 到携带值上。在 Digital Warrior 谱面上，第 435 格正处在
            // 第 419 格事件 X 位移的中途（约从 100.4 朝 94.28 走），
            // 这时第 436 格的纯 Y 事件来了，相机在一帧里跳掉了剩下的 6.1。
            float activeXOrigin = eventCarry.x;
            float activeXTarget = eventCarry.x;
            float activeXStart = float.NegativeInfinity;
            float activeXDuration = 0f;
            Ease activeXEase = Ease.Linear;

            float activeYOrigin = eventCarry.y;
            float activeYTarget = eventCarry.y;
            float activeYStart = float.NegativeInfinity;
            float activeYDuration = 0f;
            Ease activeYEase = Ease.Linear;

            // 合成后的 tween 状态，报给诊断日志。两条轴共用同一个
            // 模式，因为切换模式的事件会把两者都重启。
            Vector2 activeOrigin = eventCarry;
            Vector2 activeTarget = eventCarry;
            float activeStart = float.NegativeInfinity;
            float activeDuration = 0f;
            Ease activeEase = Ease.Linear;

            // 当前位置事件正在用的合成模式，它决定
            // 实时跟随要不要叠在它上面。
            CamMovementType activeMode = defaultMode;

            float rotationOrigin = rotation;
            float rotationTarget = rotation;
            float rotationStart = float.NegativeInfinity;
            float rotationDuration = 0f;
            Ease rotationEase = Ease.Linear;

            float zoomOrigin = zoom;
            float zoomTarget = zoom;
            float zoomStart = float.NegativeInfinity;
            float zoomDuration = 0f;
            Ease zoomEase = Ease.Linear;

            for (int i = 0; i < keyframes.Count; i++)
            {
                CameraKeyframe keyframe = keyframes[i];
                if (keyframe.StartTime > time)
                {
                    break;
                }

                // 原生 StartEffect 这样解析模式：
                //   camMovementType = movementTypeUsed ? movementType : cam.lastUsedMovementType
                // 所以没写 relativeTo 的事件是继承已经生效的
                // 模式，而不是退回谱面默认值。把缺失的值当成
                // 「Player」，会让一个只带缩放的事件（position [null,null]、
                // 没有 relativeTo）在过渡中途把相机从 Tile 模式切走，
                // 让视图掉到球的跟随位置，然后才缓回来。
                CamMovementType modeBeforeEvent = mode;
                if (keyframe.ModeUsed)
                {
                    mode = keyframe.Mode;
                }

                bool positionUsed = keyframe.HasPosition
                    && (!float.IsNaN(keyframe.Position.x) || !float.IsNaN(keyframe.Position.y));

                // 只要事件写了模式，原生就会消耗掉那个移动类型，
                // 这正是「relativeTo: Player」不带位置数据也能把
                // 相机切回跟随球的原因。这里要求必须有位置数据，
                // 会让那次切换悄悄不发生。
                if (positionUsed || keyframe.ModeUsed)
                {
                    // 原生 Decode 为每个事件都重新赋 targetPos：
                    //
                    //   targetPos = RDUtils.GetRandomVector2(evnt, "position") * tileSize
                    //
                    // 所以这个偏移是*本*事件写下的，绝不是从
                    // 更早的事件继承的。把它跨事件累积，会让
                    // 第 75 格事件的 "position": [40, 4] 漏进第 157 格事件的
                    // "position": [null, 5]，把一个 NaN 的 X 变成 101.64 + 40 * tileSize，
                    // 把相机横着甩出去，而原生那条 tween 根本没碰过
                    // X。
                    Vector2 offset = defaultPosition;
                    // 两个轴一视同仁：数组里的 null（读出来是 NaN）
                    // 一律保留。NaN 轴在下面会走到「不驱动」那一支，
                    // 目标由携带值给出，而不是被清成 0。
                    offset.x = keyframe.Position.x;
                    // 数组里的 null（= NaN）一律**保留**，不因为事件
                    // 写了 relativeTo 就把它清成 0。
                    //
                    // 这里原本有一句「ModeUsed 时 offset 清零」，理由是
                    // 原生那段
                    //     if (movementTypeUsed) { if (IsNaN(vector.y)) vector.y = 0; }
                    // 实测下来这个读法是错的：Phantigma 第 396 格是
                    // `position [-4, null]` + `relativeTo Tile`，而原版的相机
                    // Y 从 85.19 一路升到 **96.63** —— 那是第 382 格
                    // `[null, 9]` 那条 tween 的目标（格子 87.63 + 偏移 9）。
                    // 如果清零，目标就变成 87.63，正好丢掉那 9，
                    // 相机也整个偏掉。
                    //
                    // 保住 NaN 之后，这一格 Y 轴的目标由「携带值」给出
                    // （见下面的逐轴重启），起点用该时刻的实时位置，
                    // 于是 85.08 → 96.63，与原版一致。
                    offset.y = keyframe.Position.y;

                    // 上面那两条「一律保留 NaN」说的是**模式没换**的情况。
                    // 模式**换掉**时原生走的是另一句：
                    //
                    //   if (movementTypeUsed) {
                    //       if (IsNaN(vector.x)) vector.x = 0f;
                    //       if (IsNaN(vector.y)) vector.y = 0f;
                    //   }
                    //
                    // 也就是 null 分量被当成 **0** 去驱动。差别只出现在
                    // `relativeTo: Player` + `position: [null, null]` 这种
                    // 「交回跟随球」的写法上：Player 模式下目标就是 0 偏移，
                    // 相机回到球本身。冻在残差上则整段保留自由运镜留下的偏移。
                    //
                    // 本谱 528 格就是这一条（实测）：518 格 `LastPosition [0,0]`
                    // 之后相机停在 141.15,-232.44，球已爬到 -229.44，残差 (0,-3)
                    // ≈ 两个格距；528 写 `relativeTo: Player` + `[null,null]` 交回
                    // 跟随，原生借此清零，而这里冻住了残差，于是 528~535 整段
                    // 比谱面低 3 个单位，到 535 的 `[3,0]` 才拉回来。
                    //
                    // 放进来的门与原生的守卫同源：守卫条件是「模式与
                    // lastUsedMovementType 相同」，命中时原生把 movementTypeUsed
                    // 清掉、这一格根本不驱动位置（NaN 也就不归零），
                    // 所以只在**模式真的换了**时归零。Phantigma 第 396 格
                    // （Tile→Tile，`[-4, null]`）因此不受影响，仍是沿用携带值。
                    // Global 不在守卫的排除名单里之外，它单独放行。
                    // LastPosition 系列归零与沿用等价（0 + 残差 = 残差）。
                    bool nanAsZero = keyframe.ModeUsed && keyframe.HasPosition
                        && (mode != modeBeforeEvent || mode == CamMovementType.Global);
                    if (nanAsZero)
                    {
                        if (float.IsNaN(offset.x))
                        {
                            offset.x = 0f;
                        }
                        if (float.IsNaN(offset.y))
                        {
                            offset.y = 0f;
                        }
                    }

                    // 一个事件的起点必须让相机留在它原本所在的地方，
                    // 因为原生 tween 是从 camParent.position 起跑的——而它
                    // 刚刚被停在相机的当前世界位置上。改为从上一个事件的
                    // 偏移起步，会让一次模式切换从世界原点飞过来：
                    // 在第 39 格进入 Tile 会从 (0,0) 开始，
                    // 走到该格的位置，读起来就是视图停在很下面
                    // 再往上漂。
                    //
                    // 所以原点的选取要保证合成输出在这次切换
                    // 前后保持连续，无论模式往哪个方向切。
                    // 连续性原点要用「事件触发那一刻播放头所在的格」的跟随值，
                    // 不能用事件自己的格号。
                    //
                    // 平时两者相同，所以差别看不出来；但带负 angleOffset 的
                    // 事件会提前整整一圈生效，此时事件格号远在当前格之后。
                    // EvaluateFollow(未来格, 过去的时刻) 里 elapsed < 0，会直接
                    // 返回那个未来格的 cameraAtFloorEntry——一个和当前跟随值
                    // 无关的数。拿它去算 continuityOrigin，交接点就对不上，
                    // 表现为一帧内瞬移零点几个单位。
                    //
                    // 在 Factory 谱面上量到：第 333 格那条
                    // relativeTo Player + angleOffset -360 的事件在第 329 格
                    // 生效，followAtStart 取到第 333 格的 127.83，而运行时
                    // 跟随是 128.67，差 0.84；改成本格之后交接值精确连续。
                    Vector2 followAtStart = EvaluateFollow(
                        FloorAtTime((float)keyframe.StartTime), (float)keyframe.StartTime);

                    // 哪些合成输出要在事件自己的分量之上带上球的跟随。
                    // Player 事件贡献一个偏移，跟随就叠在
                    // 上面；Tile 和 Global 会调 SetToFreeMode，那会把 localPosition 清零，
                    // 于是没有东西叠上去；LastPosition 既不改 camParent 也不改
                    // followMode，所以本来就在跑的跟随会在它下面
                    // 继续跑。在 NYA 第 167 格量到——两个零偏移的 LastPosition 事件——
                    // 原生视图一直跟着球走，而这个只为 Player 加跟随的
                    // 域把相机冻在了原地。
                    // **本事件真正采用的位置语义。**
                    //
                    // 老格式谱 + 事件没写 relativeTo 时，原生走「相对
                    // camParent 当前位置叠加偏移」，公式和 LastPosition 分支
                    // 逐字相同、并且**同样不改跟随模式**。所以这里替换成
                    // LastPosition，而不是去改 mode —— mode 还要继续充当
                    // lastUsedMovementType 的角色：原生在 legacy 那一支里
                    // 不会更新 lastUsedMovementType，本域靠 mode 跟踪它，
                    // 把它改掉会把后面事件的继承基准弄错。
                    CamMovementType resolveMode = legacyRelativeTo && !keyframe.ModeUsed
                        ? CamMovementType.LastPosition
                        : mode;

                    bool nextFollow = resolveMode == CamMovementType.Player
                        ? true
                        : (resolveMode == CamMovementType.Tile || resolveMode == CamMovementType.Global
                            ? false
                            : followMode);

                    bool previousCarries = CarriesFollow(activeMode, followMode);
                    bool nextCarries = CarriesFollow(resolveMode, nextFollow);

                    Vector2 previousOutput = previousCarries
                        ? followAtStart + eventCarry
                        : eventCarry;

                    // 交接用的原点：上一条事件还在**缓动中途**时，取它
                    // 在这一刻的实际值；没有 tween 在跑时，沿用携带值。
                    //
                    // 为什么要实时值：本谱 f382 那条 MoveCamera 长 18.857 秒
                    // （页面里是 duration 32），到 f396 才走了 3%，相机在
                    // 85.07，而目标是 96.63。拿目标当新事件的起点会把相机
                    // 一帧内拽过去——表现就是「走一格突然被结算」。
                    //
                    // 为什么要有回落：早段还没有任何位置事件时通道是
                    // 初始值，无条件采样会把那个占位值当成真实值用。
                    float handoffX = LiveChannelValue(
                        activeXOrigin, activeXTarget, activeXStart, activeXDuration,
                        activeXEase, eventCarry.x, (float)keyframe.StartTime);
                    float handoffY = LiveChannelValue(
                        activeYOrigin, activeYTarget, activeYStart, activeYDuration,
                        activeYEase, eventCarry.y, (float)keyframe.StartTime);
                    Vector2 handoffOutput = (previousCarries ? followAtStart : Vector2.zero)
                        + new Vector2(handoffX, handoffY);

                    // 事件分量的原点：取那个能让合成输出
                    // 跨过切换仍然连续的值。CarriesFollow 在分量上是仿射的，
                    // 系数为 1，所以差别只在跟随项。
                    Vector2 continuityOrigin = handoffOutput - (nextCarries
                        ? followAtStart
                        : Vector2.zero);

                    // 一个在跟随的 LastPosition 事件所基于的值是上一个
                    // camParent，而不是合成后的相机位置。原生是实时读的：
                    //
                    //   case LastPosition:
                    //       Vector2 vector4 = camParent.position;
                    //       if (positionUsed) finalPos = vector + vector4;
                    //
                    // 而且到那时上一个 tween 已经被 Kill(complete: true)
                    // 强制到终点了，所以 camParent 持有的是上一个事件的*目标*。
                    // 改用事件前的携带值，会把再上一个事件的目标交给
                    // 这个事件：在 Factory 谱面上第 157 格的 Tile 事件把
                    // camParent 停在 109.36,7.82，后面那个 LastPosition 事件必须
                    // 在*那个*值上加 [0, 3.0]（原生到 110.82）；用携带值 101.64,6.32
                    // 会把相机送回 101.64,9.32，整个停顿期间都停在
                    // 原位置左边一格。
                    eventWorld = previousCarries
                        ? previousOutput - followAtStart
                        : previousOutput;

                    followMode = nextFollow;

                    CamMovementType previousMode = activeMode;
                    activeMode = resolveMode;
                    Vector2 resolved = ResolveWorld(resolveMode, offset, keyframe.Floor,
                        ballAtFloorEntry[Mathf.Clamp(keyframe.Floor, 0, floorCount - 1)],
                        eventWorld);

                    // 一根轴什么时候被重新指向：**事件真的给了值**，
                    // 或者**模式换了**（换模式时原生会把 camParent 从相机
                    // 当前位置重新播种，两根轴都要重建）。
                    //
                    // 关键是 `keyframe.ModeUsed` 单独**不构成**重启理由。
                    // 实测 Phantigma 第 396 格：事件带 relativeTo=Tile
                    // （与第 382 格相同）而 position 是 [-4, null]。原版的
                    // Y 一路平滑走到 96.63 —— 也就是第 382 格那条事件的目标
                    // （格子 87.63 + 偏移 9）—— 在 t=21.27 到达后才停住。
                    // 也就是说第 396 格**根本没有重启 Y**。
                    //
                    // 之前这里写的是 `!IsNaN(y) || ModeUsed`，ModeUsed 一来
                    // 就无条件重启 Y，于是目标被重指向「格子位置」87.63，
                    // 第 382 格留下的 +9 偏移被丢掉，相机位置就错了。

                    // 逐轴重启，对应原生那几道门。原生只杀掉
                    // finalPos 为实数的那个轴：
                    //
                    //   if (!float.IsNaN(finalPos.x) || movementTypeUsed) { moveXTween?.Kill(complete: true); }
                    //
                    // 被驱动的轴会被杀到终点再重新指向，所以它从
                    // 连续性原点重启。被保持的轴则原封不动地保留
                    // 正在跑的 tween——原点、目标、起点和缓动全都不变——
                    // 只是按自己的时钟继续走。改为从它此刻持有的值
                    // 重启，就产生了那次单帧瞬移：在
                    // Digital Warrior 谱面上第 435 格正处在第 419 格事件的
                    // X 位移中途（100.4 朝 94.28 走），这时第 436 格的纯 Y 事件来了，
                    // 相机在一帧里跳掉了剩下的 6.1。
                    // 只有**事件真的给了值**的那根轴才重启。
                    //
                    // 原来这里还挂着 `|| keyframe.ModeUsed`，于是一个
                    // 只写 X、带 relativeTo 的事件会把 Y 也重启，而 Y 的
                    // NaN 又被下面那条替换成「起点自己」，Y 就成了一个
                    // 原点到目标的空 tween —— 相机 Y 彻底不动。
                    //
                    // 实测 Phantigma 第 396 格：事件是 [-4, null] + relativeTo=Tile。
                    // 原版的 Y 从 85.19 一路升到 96.63（第 382 格 [null,9]
                    // 那条 tween 的目标），21.27 秒才到 —— 也就是说第 396 格
                    // **没有触碰 Y**。带 ModeUsed 的判据每次都重启它，
                    // 相机就钉在原地，位置整个偏掉 9。
                    // 一根 NaN 轴要不要被「定住」，取决于**模式有没有换**，
                    // 而不是事件有没有写 relativeTo。
                    //
                    // 逐事件实测（Phantigma 382/396 两格，形状都是 [X, null] + Tile）：
                    //   f382  模式 Player -> Tile（换了）→ 必须定住 Y，
                    //         否则 carry 会衰减成 0，下一格的起点跟着变 0
                    //         （相机从 83 掉到 0 再爬）
                    //   f396  模式 Tile -> Tile（没换）→ **不能**动 Y，
                    //         否则正在跑的 Y（目标 96.63）被冻在 85.08
                    // 两次的差别只有模式是否变化这一条。
                    bool modeChanged = mode != previousMode;
                    bool forceAxis = keyframe.ModeUsed && modeChanged;

                                        if (!float.IsNaN(resolved.x) || forceAxis)
                    {
                        activeXOrigin = continuityOrigin.x;
                        activeXTarget = float.IsNaN(resolved.x) ? continuityOrigin.x : resolved.x;
                        activeXStart = (float)keyframe.StartTime;
                        activeXDuration = keyframe.Duration;
                        activeXEase = keyframe.Ease;
                    }
                    if (!float.IsNaN(resolved.y) || forceAxis)
                    {
                        activeYOrigin = continuityOrigin.y;
                        activeYTarget = float.IsNaN(resolved.y) ? continuityOrigin.y : resolved.y;
                        activeYStart = (float)keyframe.StartTime;
                        activeYDuration = keyframe.Duration;
                        activeYEase = keyframe.Ease;
                    }

                    // 合成出来的这一对驱动携带值和诊断日志。每条轴
                    // 按自己的时钟解析，从更早事件带过来的轴
                    // 报的是那个事件的开始，而不是这一个的。
                    activeTarget = new Vector2(activeXTarget, activeYTarget);
                    activeOrigin = new Vector2(activeXOrigin, activeYOrigin);
                    activeStart = Mathf.Max(activeXStart, activeYStart);
                    activeDuration = activeXStart >= activeYStart ? activeXDuration : activeYDuration;
                    activeEase = activeXStart >= activeYStart ? activeXEase : activeYEase;
                    diagActiveFloor = keyframe.Floor;
                    anyPositionEvent = true;
                }

                // 旋转通道。原生 ffxCameraPlus 那条 tween 是：
                //
                //   rotationTween = DOTween.To(() => vfx.camAngle, x => vfx.camAngle = x,
                //                              targetRot + num, duration);
                //
                // 而 `num` **只有一种情况非 0**：
                //
                //   case CamMovementType.LastPosition:   num = vfx.camAngle;   // 相对当前角度
                //   case LastPositionNoRotation:         num 保持 0             // 绝对
                //   Player / Tile / Global:              num 保持 0             // 绝对
                //
                // 也就是说 **`relativeTo: LastPosition` 的 rotation 是相对量**，
                // 其余全是绝对量。以前这里一律按绝对处理，于是每一个
                // 「LastPosition + rotation 0」的落点都会把角度**瞬移回 0**。
                //
                // 这张谱 68 格之后正是一串这样的对子：
                //   (LastPosition, 0 拍, rotation 0, zoom 160) + (Player, 1 拍, rotation 7/20/27/34…)
                // 原版的角度是 7 → 20 → 27 → 34 → 41 → 48 一路小步往上走；
                // 求值器每一拍都先掉回 0、再一帧冲到 34/41/48 —— 幅度大了一倍多，
                // 还多出一记回甩，看起来就是「组合运镜和原版不一样、幅度很大」。
                //
                // 另外原生那道门是
                //   `if (rotationUsed || (movementTypeUsed && movementTypeIsLastPosition))`
                // ——显式写了 LastPosition 的事件**即使没有 rotation** 也会重启旋转
                // tween，目标就是「当前角度 + 0」，等于把飞在半路的旋转就地钉住。
                // LastPositionNoRotation 同样重启，但 num 是 0，所以是钉到绝对 0。
                bool rotationValueUsable = keyframe.HasRotation
                    && !float.IsNaN(keyframe.Rotation)
                    && !float.IsInfinity(keyframe.Rotation);
                bool lastPositionRotation = mode == CamMovementType.LastPosition;
                bool lastPositionRestart = lastPositionRotation
                    || mode == CamMovementType.LastPositionNoRotation;

                if (rotationValueUsable || lastPositionRestart)
                {
                    rotationOrigin = rotation;
                    if (!rotationValueUsable)
                    {
                        // 没写 rotation 的 LastPosition 系事件：LastPosition 钉在
                        // 当前角度，LastPositionNoRotation 钉到绝对 0（num=0）。
                        rotationTarget = lastPositionRotation ? rotation : 0f;
                    }
                    else
                    {
                        rotationTarget = lastPositionRotation
                            ? rotation + keyframe.Rotation
                            : keyframe.Rotation;
                    }

                    rotationStart = (float)keyframe.StartTime;
                    rotationDuration = keyframe.Duration;
                    rotationEase = keyframe.Ease;
                }

                // zoom 0 是有意义的，不是缺失值：scrCamera.UpdateSize
                // 在 zoomSize == 0 时会禁用渲染相机，而那正是
                // 谱面在重新拉近之前用的「缩到没有」过渡。
                // 在这里拒绝它会让那个过渡悄悄消失。只剩
                // 非有限值的保护，因为谱面里的 null 会解码成 NaN。
                if (keyframe.HasZoom && !float.IsNaN(keyframe.Zoom)
                    && !float.IsInfinity(keyframe.Zoom))
                {
                    zoomOrigin = zoom;
                    zoomTarget = keyframe.Zoom;
                    zoomStart = (float)keyframe.StartTime;
                    zoomDuration = keyframe.Duration;
                    zoomEase = keyframe.Ease;
                }

                // 原生 StartEffect 会对上一个 tween 调 Kill(complete: true)，
                // 强制它在下一个 tween 开始前先到*终*值。所以
                // 被携带的值必须是上一个目标，而不是缓动
                // 碰巧走到的那个值。改为从中途的缓动位置插值，
                // 会产生和游戏不一样的位移距离，这才让
                // 那次稳定看起来比原版大好几倍。
                //
                // eventCarry 保留它原来的含义——上一个目标，用它自己模式
                // 所用的空间——因为上面的连续性数学为 Player 模式把 previousOutput
                // 建成 follow + eventCarry。往那里放世界位置会把
                // 跟随算两次，让每个 Player 格都从错的地方起步。
                //
                // eventWorld 是独立的世界空间携带值，只有 LastPosition 会消费它。
                // 它是上一个事件解析出的目标，在上面那个
                // LastPosition 分支仍然看得见的地方赋值；它为什么该用事件前的值，
                // 见那处赋值。
                //
                // 跟随是在事件的*末尾*采样的。Player 相机会在它的事件运行期间
                // 持续前进，所以在起点采样会让携带值冻在
                // 落后好几格的地方（在 NYA 谱面上量到：卡在 126.26，而相机已经
                // 到了 128.10）。
                eventCarry = activeTarget;
                rotation = rotationTarget;
                zoom = zoomTarget;
                diagWorld = eventWorld;
            }

            // 跟随偏移只在 Player 模式下生效。原生 Tile/Global/
            // LastPosition 都会调 cam.SetToFreeMode()，那会把 followMode 设为 false
            // 并把相机的 localPosition 停在零，于是相机完全
            // 落在 camParent 的值上。在这些模式之上再加跟随，会
            // 把视图抬高整整一个跟随向量（量到：第 57 格求值出
            // 17.22,43.29，而不是跟随的 5.55,20.59）。
            // 在事件链的工作值离开作用域之前，为诊断日志镜像它们；
            // 为什么有这个，见字段声明处。
            diagOrigin = activeOrigin;
            diagTarget = activeTarget;
            diagCarry = eventCarry;
            diagMode = activeMode;
            diagAnyPositionEvent = anyPositionEvent;

            if (anyPositionEvent)
            {
                // 每条轴按最后一次重启它的那个事件的时钟采样，所以
                // 还在从更早事件走过来的轴保留自己的进度。
                Vector2 eventComponent = new Vector2(
                    Sample(activeXOrigin, activeXTarget, activeXStart, activeXDuration, activeXEase, time),
                    Sample(activeYOrigin, activeYTarget, activeYStart, activeYDuration, activeYEase, time));
                position = CarriesFollow(activeMode, followMode)
                    ? follow + eventComponent
                    : eventComponent;
            }
            diagPosOrigin = activeOrigin;
            diagPosTarget = activeTarget;
            diagPosStart = activeStart;
            diagPosDuration = activeDuration;
            diagPosEase = activeEase;
            diagPosFloor = diagActiveFloor;
            diagRotOrigin = rotationOrigin;
            diagRotTarget = rotationTarget;
            diagRotStart = rotationStart;
            diagRotDuration = rotationDuration;
            diagRotEase = rotationEase;
            diagZoomOrigin = zoomOrigin;
            diagZoomTarget = zoomTarget;
            diagZoomStart = zoomStart;
            diagZoomDuration = zoomDuration;
            diagZoomEase = zoomEase;

            rotation = Sample(rotationOrigin, rotationTarget, rotationStart,
                rotationDuration, rotationEase, time);
            zoom = Sample(zoomOrigin, zoomTarget, zoomStart, zoomDuration,
                zoomEase, time);
        }

        // ── 同帧去重复算 ────────────────────────────────────────────
        //
        // 每一帧会调用 `EvaluateChannels` **两次**：
        //   1. `ResolveFrameHold`（引擎层，域循环之前）问「相机是不是被甩远了，
        //      要不要定格」，它内部走一遍完整的事件链；
        //   2. 紧接着相机域的 `Evaluate` 又从头走一遍，入参完全相同。
        //
        // `EvaluateChannels` 是纯计算（只读关键帧和上下文，只写 ref 出参，
        // 不改任何字段），所以同帧同入参的第二次调用可以直接复用第一次的结果。
        //
        // 为什么值得：这条链从 t=0 回放到播放头，成本随位置**线性增长** ——
        // 实测 `Cam.Hold` 从谱面开头的 0.00ms 涨到 240 秒处的 0.10ms。
        // 而 165Hz 的整帧预算只有 6.06ms，这一项在动画段等于白占 1.7%。

        private int reuseFrame = -1;
        private int reuseFloorIndex = -1;
        private float reuseTime;
        private Vector2 reuseFollow;
        private bool reuseFreeRoam;
        private Vector2 reuseResolved;
        private float reuseRotation;
        private float reuseZoom;

        /// <summary>
        /// 同帧同入参就复用上一趟的结果，否则真算并把它记下来。
        /// 键里必须带上 `FreeRoam` 状态：出场运镜会**替换**跟随分量，
        /// 两次调用之间它若翻了，输入就不再相同。
        /// </summary>
        private void EvaluateChannelsForReuse(int floorIndex, float time,
            Vector2 follow, ref Vector2 position, ref float rotation, ref float zoom)
        {
            bool freeRoam = FreeRoamPreviewDomain.ExitCameraActive;
            if (reuseFrame == Time.frameCount
                && reuseFloorIndex == floorIndex
                && reuseTime.Equals(time)
                && reuseFollow == follow
                && reuseFreeRoam == freeRoam)
            {
                position = reuseResolved;
                rotation = reuseRotation;
                zoom = reuseZoom;
                return;
            }

            EvaluateChannels(floorIndex, time, follow, ref position, ref rotation, ref zoom);

            reuseFrame = Time.frameCount;
            reuseFloorIndex = floorIndex;
            reuseTime = time;
            reuseFollow = follow;
            reuseFreeRoam = freeRoam;
            reuseResolved = position;
            reuseRotation = rotation;
            reuseZoom = zoom;
        }

        /// <summary>
        /// 把事件目标解析成它的 camParent 空间值，对应
        /// ffxCameraPlus.StartEffect 里的那个 switch。
        ///
        /// Player 模式只返回偏移：球的位置是通过跟随通道
        /// 到达相机的，由调用方加上。把球
        /// 也在这里折进去，会在事件一触发的瞬间把相机按到某个静态格锚点上，
        /// 丢掉平滑后的跟随，视图就瞬移了。
        /// </summary>
        private Vector2 ResolveWorld(CamMovementType mode, Vector2 offset, int floorIndex,
            Vector2 ballPosition, Vector2 previousWorld)
        {
            // 原生 switch 用普通运算符相加各分量，所以一个 NaN 偏移
            // 轴只会传播到该轴的 finalPos 上，随后逐轴的 tween
            // 门就跳过它。Vector2 运算保持了这种逐轴行为；
            // 调用方再用被保持的值替换掉幸存下来的 NaN。
            switch (mode)
            {
                case CamMovementType.Tile:
                    // 原生用的是 floorPos——事件所在那一格
                    // 的位置——而不是球。球是绕着格转的，所以拿它做锚点
                    // 会把相机甩得很远（量到：第 39 格飞得很高）。
                    return AddPerAxis(floorPosition[Mathf.Clamp(floorIndex, 0, floorCount - 1)],
                        offset * tileSize);
                case CamMovementType.Global:
                    return offset * tileSize;
                case CamMovementType.LastPosition:
                case CamMovementType.LastPositionNoRotation:
                    return AddPerAxis(previousWorld, offset * tileSize);
                default:
                    return offset * tileSize;
            }
        }

        /// <summary>
        /// 一根通道在某个时刻的实时值；**没有 tween 在跑时回落到携带值**。
        ///
        /// `start == NegativeInfinity` 或 `duration &lt;= 0` 表示这个通道
        /// 还没有被任何事件驱动过，此时 origin/target 都是初始占位值，
        /// 直接采样会把它当成真实值用。
        /// </summary>
        private static float LiveChannelValue(float origin, float target, float start,
            float duration, Ease ease, float carry, float time)
        {
            if (float.IsNegativeInfinity(start) || duration <= 0f || time <= start)
            {
                return carry;
            }
            return Sample(origin, target, start, duration, ease, time);
        }

        private static Vector2 AddPerAxis(Vector2 left, Vector2 right)
        {
            return new Vector2(left.x + right.x, left.y + right.y);
        }

        /// <summary>
        /// 球的跟随要不要叠在事件自己的分量之上，对应
        /// scrCamera 如何把 camParent.position 与 camera.transform.localPosition 合成。
        ///
        /// Player 事件贡献一个偏移，所以跟随总是叠在上面。Tile 和
        /// Global 把相机停住并调 SetToFreeMode，那会把 localPosition 清零，于是
        /// 没有东西可加。LastPosition 既不停住也不释放：跟随本来就在跑，
        /// 就在事件的 tween 下面继续跑，两者相加。
        /// </summary>
        private static bool CarriesFollow(CamMovementType mode, bool followMode)
        {
            return mode == CamMovementType.Player
                || ((mode == CamMovementType.LastPosition
                     || mode == CamMovementType.LastPositionNoRotation) && followMode);
        }

        private static Vector2 GetBallPosition(Vector2 fallback)
        {
            scrPlayerManager manager = scrPlayerManager.instance;
            if (manager == null || manager.players == null)
            {
                return fallback;
            }

            for (int i = 0; i < manager.players.Length; i++)
            {
                scrPlayer player = manager.players[i];
                if (player != null && player.planetarySystem != null
                    && player.planetarySystem.chosenPlanet != null)
                {
                    Vector3 position = player.planetarySystem.chosenPlanet.transform.position;
                    return new Vector2(position.x, position.y);
                }
            }

            return fallback;
        }

        /// <summary>
        /// 对应 scrCamera.Update 里自定义关卡速度的 camspeed：该格 BPM 下的
        /// 两拍，再乘歌曲音高。
        /// </summary>
        private static float GetCameraSpeed(PreviewTimelineContext context, PreviewFloorSnapshot floor)
        {
            float speed = floor == null || floor.Speed <= 0f ? 1f : floor.Speed;
            float pitch = 1f;
            scrConductor conductor = scrConductor.instance;
            if (conductor != null && conductor.song != null && conductor.song.pitch > 0.001f)
            {
                pitch = conductor.song.pitch;
            }
            float bpm = Mathf.Max(0.001f, context.BaseBpm * speed * pitch);
            return 120f / bpm;
        }

        private static Vector2 Sample(Vector2 origin, Vector2 target,
            float start, float duration, Ease ease, float time)
        {
            // 事件的窗口一过，通道就正好停在目标上。这就是
            // DOTween 完成时做的事——它用终值本身去调 setter，
            // 缓动只塑造中间那段路径。反过来让缓动决定
            // 终值，会让振荡型家族停在半路：在 M3 谱面上
            // 第 268 格的 InFlash 旋转终值是 0.089，继续用
            // 缓动结果会把相机停在 -6.377，而游戏到了 0.000。
            if (time >= start + duration)
            {
                return target;
            }
            if (time < start || duration <= 0f)
            {
                return origin;
            }
            if (time == start)
            {
                // DOTween 的第一次更新落在起点之后一步，而不是原点，
                // 所以这个值永远不会等于 tween 的起点。这里返回 origin 对
                // 事件开始的那一帧仍然是对的；这个分支存在只是为了让
                // 比值不进那次除法。
                return origin;
            }

            float ratio = Mathf.Clamp01((time - start) / duration);
            return Vector2.LerpUnclamped(origin, target, PreviewEase.EasedValue(0f, 1f, ratio, ease));
        }

        private static float Sample(float origin, float target,
            float start, float duration, Ease ease, float time)
        {
            // 规则同 Vector2 重载：窗口一过就停在目标上。
            if (time >= start + duration)
            {
                return target;
            }
            if (time <= start || duration <= 0f)
            {
                return origin;
            }

            float ratio = Mathf.Clamp01((time - start) / duration);
            return PreviewEase.EasedValue(origin, target, ratio, ease);
        }

        private static void ApplyCamera(scrCamera camera, PreviewTimelineContext context,
            Vector2 world, float rotation, float zoom)
        {
            // world = camParent.position + camera.transform.localPosition。
            //
            // 读 camParent 的值再减掉它并不安全：它每种模式
            // 装的东西不一样，拖动定位时也没有任何东西让它保持同步。
            // 把它停在原点，把整个世界位置放进相机的
            // localPosition，这样和是一样的，却不依赖之前的状态。
            Transform camParent = context != null && context.Editor != null
                && context.Editor.customLevel != null
                ? context.Editor.customLevel.camParent
                : camera.transform.parent;
            if (camParent != null)
            {
                Vector3 parentPosition = camParent.position;
                camParent.position = new Vector3(0f, 0f, parentPosition.z);
            }

            Vector3 local = camera.transform.localPosition;

            // 求值器每帧都重写相机，所以手动拖动会被立刻
            // 覆盖。反过来，凡是求值器没有做出的 transform 变化，
            // 都被当成用户在平移视图，并被
            // 延续下去。用这种方式移动相机，是区分
            // 常量偏移（装饰物单纯放错了位置）和
            // 视差速率错误（相机移动时装饰物相对轨道滑动）
            // 最快的办法。
            Vector2 pan = userPan;
            if (lastWrittenLocalValid)
            {
                pan += new Vector2(local.x - lastWrittenLocal.x, local.y - lastWrittenLocal.y);
            }

            // 锁定时检测到的移动会被丢掉而不是累积，这样只要相机
            // 持续被拽走，视图就停在谱面自己的取景上。
            // 每帧清掉它也会撤销在锁定打开之前
            // 已经施加的那次平移。
            userPan = positionLocked ? Vector2.zero : pan;
            pan = userPan;

            Vector3 written = new Vector3(
                world.x + camera.shake.x + pan.x,
                world.y + camera.shake.y + pan.y,
                local.z);
            camera.transform.localPosition = written;
            lastWrittenLocal = new Vector2(written.x, written.y);
            lastWrittenLocalValid = true;

            if (Main.Settings != null && Main.Settings.VerboseDiagnostics
                && (pan - lastLoggedPan).sqrMagnitude > 1f)
            {
                lastLoggedPan = pan;
                Main.Logger.Log("[CameraPan] userPan=" + pan.x.ToString("0.0") + ","
                    + pan.y.ToString("0.0") + ".");
            }
            // MoveCamera 的旋转属于 scrVfxPlus.camAngle，不直接属于
            // 相机 transform。写旋转的是它的 setter：
            //
            //   set { _camAngle = value; cam.transform.rotation = Quaternion.Euler(0, 0, value); }
            //
            // 只写 transform 会让 scrVfxPlus.camAngle 在整个
            // 预览里一直是零，于是任何读那个字段的东西都看不到旋转。在
            // M3 谱面第 250 格量到：原生 camAngle 在这一格内从 6.994 走到 5.382，
            // 而这边一直是平的 0.000。
            scrVfxPlus vfx = scrVfxPlus.instance;
            if (vfx != null)
            {
                vfx.camAngle = rotation;
            }
            else
            {
                camera.transform.rotation = Quaternion.Euler(0f, 0f, rotation);
            }

            camera.followMode = true;
            camera.topos = world;
            camera.frompos = world;
            camera.pos = world;
            camera.timer = Mathf.Max(camera.timer, camera.camspeed);

            camera.zoomSize = zoom;
            camera.UpdateSize();

            // 这里故意不重新启用。UpdateSize 会在 zoomSize
            // 为 0 时禁用 camobj，那正是有意的「缩走」过渡；
            // 强行把它打开就破坏了这个效果。
        }
    }
}
