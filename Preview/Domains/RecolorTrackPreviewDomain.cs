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
    internal sealed class RecolorTrackPreviewDomain : IPreviewDomain, IHistoryResettable
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

        /// <summary>
        /// 每格**在被我们接管之前**的 baked 颜色状态，回退到"这一格还没有任何
        /// recolor 事件"时用来还原。
        ///
        /// 不还原的话：播放走过 575 格的 RecolorTrack 之后往回拖，求值器对
        /// "当前时刻之前没有事件"的格直接跳过 —— 而我们上一帧已经把
        /// `specialColor*` / `glowMultiplier` 改成事件那一套了，`scrFloor.Update`
        /// 会继续按那套算，格就永远停在事件的颜色上（实测症状就是
        /// "经过 575 格再退回 575 之前，轨道颜色不还原"）。
        /// </summary>
        private TrackColorType[] bakedSpecialType = new TrackColorType[0];
        private Color[] bakedSpecialColor1 = new Color[0];
        private Color[] bakedSpecialColor2 = new Color[0];
        private float[] bakedSpecialAnimDuration = new float[0];
        private float[] bakedSpecialTimeOffset = new float[0];
        /// <summary>格子在加载时被赋予的轨道样式（`initialTrackStyle`）。</summary>
        private TrackStyle[] bakedStyle = new TrackStyle[0];
        /// <summary>
        /// 接管前格子渲染器上的颜色（`floorRenderer.color` 的快照）。
        ///
        /// **只给 EndSession 一处用**，和 <see cref="floorBaseColor"/> 不是一回事：
        /// floorBaseColor 是谱面设置里的基础轨道色（我们做 Lerp 的起点），
        /// 这一份是"游戏当时真正画在屏幕上的那个色"。
        ///
        /// 为什么结束时要它：我们把类型改成了 Single，游戏的 Update 就不再
        /// 碰这一格的颜色了。对 baked 类型本来就是 Single/Stripes 的格，
        /// 还原类型之后**没有任何东西会去重写颜色** —— 就这样放着的话，
        /// 轨道会停在我们最后一帧写的预览色上（比如编辑器里看起来还是特效色，
        /// 实际谱面底色是别的）。所以那种格要连颜色一起退回快照。
        /// 反过来 baked 类型是会动的（Glow/Blink/…），Update 下一帧自己就会
        /// 重算，不能碰，碰了反而闪一下。
        /// </summary>
        private Color[] bakedRendererColor = new Color[0];
        /// <summary>接管前格子的辉度（`glowMultiplier` 的快照），和
        /// <see cref="bakedRendererColor"/> 同一处用处、同一个时刻抓。</summary>
        private float[] bakedRendererGlow = new float[0];
        /// <summary>这一格被我们写过没有。没写过就别碰它 —— 游戏自己管得好好的。</summary>
        private bool[] touched = new bool[0];

        // ── 临时探针：把这一域每帧那 1.96ms 拆开 ──────────────────────
        //
        // 已知事实：工作台播放态 `RecolorTrack=1.96ms`，是模组各域里最大的一个，
        // 而它每帧要走的格数（`floorsWithEvents.Count`）和工作量分布都还没量过。
        // 光看代码判断不了这 1.96ms 花在「二分找事件 + 算颜色」还是
        // 「写回格子的那 6 个字段」上 —— 猜是不能写的，所以三段各打一个点：
        //   Recolor.Calc  = 读绑定 + 二分 + GenerateColor + Lerp
        //   Recolor.Take  = TakeOverGlow（杀游戏的辉度 tween）
        //   Recolor.Write = SetColor + glowMultiplier + specialColor 五件套
        // 再配上几个计数，就能算出「每条路径各有多少格」，而不是只看总和。
        private long probeCalcTicks;
        private long probeTakeTicks;
        private long probeWriteTicks;
        private static int probeFloors;
        private static int probeWrites;
        private static int probeHits;
        private static int probeRestores;
        private static int probeStyles;
        private static int probeCulled;

        // ── 视野裁剪：屏幕外的格不必每帧重算重写 ──────────────────────
        //
        // 实测 `Recolor.Wr=2620`、`Recolor.N=2620`（这张谱 2620 格全被 recolor
        // 事件覆盖），而同一时刻画面里真正看得见的只有两百多格
        // （MoveTrack 那边同一台相机量出来 `ScW.Visible≈265`）。等于每帧有
        // 九成的材质写入是写给看不见的格的。
        //
        // 颜色是 (格号, 时间) 的**纯函数**，没有历史依赖（不像 MoveTrack 的
        // 位置是相对上一帧的增量）——所以屏幕外的格跳过求值，相机一移过去
        // 它在同一帧就被重算重写了。
        //
        // 唯一留下的痕迹是「类型被改成了 Single」（见 typeClaimed）：被裁掉的格
        // 不会每帧被写颜色，可它 baked 的动画类型还得停掉，否则游戏自己会继续
        // 画。这确实是一份状态，所以还原路径必须认 typeClaimed 而不只是 touched
        // （见 RestoreBakedState / EndSession）。
        //
        // 判据取「相机中心 + 屏幕矩形的外接圆」：圆包住整个屏幕矩形，
        // 再多放一圈余量盖住格子本身的长度。宁可多写一圈，
        // 也不能漏掉该处理的格。
        private Vector3 viewCenter;
        private float viewRadiusSq;
        private bool viewCull;
        /// <summary>外接圆半径的余量倍数。格子的 transform 在格首，格身还会
        /// 往下一格的方向延伸，所以比屏幕多一圈才安全。</summary>
        private const float ViewCullMargin = 1.35f;
        /// <summary>
        /// 外接圆之外再补的**绝对**长度（世界单位）。
        ///
        /// `ViewCullMargin` 是比例余量，它随视野一起缩放；而格身长度是固定的。
        /// 放大到极近时外接圆半径只剩一两个单位，按比例留出来的余量会小于
        /// 一个格身的长度 —— 那时「格首在圆外、格身还探进画面」的格子会被裁掉，
        /// 表现为屏幕边缘少一格颜色。所以再补一个固定长度（比一个格身略长）。
        /// </summary>
        private const float ViewCullPadUnits = 1.2f;

        /// <summary>
        /// 每格的世界坐标缓存，只给「在不在屏幕里」这一个判断用。
        ///
        /// **为什么要缓存**：`floor.transform` 和 `Transform.position` 都是
        /// IL2CPP 的原生调用，一次大约一百多纳秒。实测裁剪之后
        /// `Recolor.Calc` 反而从 0.70ms 涨到 0.91ms —— 因为剩下那 2500 个
        /// 被裁掉的格虽然不写颜色了，却仍然每格读一次世界坐标，光读位置
        /// 就比原来的整段计算还贵。
        ///
        /// **为什么不每帧全刷**：格子的位置只有在有 MoveTrack 之类在动它
        /// 的时候才变。所以照 `片` 等分，每帧只刷一片，每格最迟
        /// `PositionCacheSlices` 帧刷新一次，代价摊平成原来的 1/`PositionCacheSlices`。
        /// 判据本身还带着 20% 余量，位置晚几十毫秒也不会把一个该写的格判到
        /// 屏幕外去。
        ///
        /// **为什么从 4 提到 16**（2026-10-31）：4 片意味着每帧仍要读
        /// 2600/4 ≈ 655 格的世界坐标，那是 0.1~0.2ms —— 比视野裁剪本身
        /// 省不下来的那部分还大。16 片降到 164 格，滞后从 4 帧变成 16 帧
        /// （165Hz 下约 0.1 秒）。这个滞后只在「某格刚被移进视野」时才有感，
        /// 而那时它晚 0.1 秒上色，屏幕上完全看不出来；移出视野的格反而
        /// 更晚才被裁掉，只是多写几帧，无害。
        /// </summary>
        private Vector2[] cachedFloorPosition = new Vector2[0];
        /// <summary>
        /// 每帧刷新的位置片数（必须是 2 的幂，取片用 `frameCount &amp; (N-1)`）。
        /// 16 片 = 每格每 16 帧刷一次。
        /// </summary>
        private const int PositionCacheSlices = 16;
        /// <summary>
        /// 下一次刷新必须全刷一遍。
        ///
        /// 分片刷新的前提是「位置变化很慢」。播放头一跳（<see cref="ResetHistory"/>）
        /// 之后这个前提就不成立了 —— 从跳到落地的这几帧，缓存里装的是跳转前的
        /// 位置，而判据拿的是跳转后的相机。虽然分片刷新会在
        /// `PositionCacheSlices` 帧内自己修正，
        /// 但跳转本来就是每次的必经动作，这里顺手全刷一次（约 2600 次原生读，
        /// 一次性 ~1ms）比让裁剪判错十几帧划算。
        /// </summary>
        private bool positionCacheStale = true;

        /// <summary>按格号缓存的格对象，给 EndSession 还原用（那边拿不到求值帧）。</summary>
        private scrFloor[] boundFloors = new scrFloor[0];

        /// <summary>
        /// 这一格的颜色类型被我们改成了 Single（游戏因此不再自己驱动它的颜色）。
        ///
        /// 和 <see cref="touched"/> 分开记：touched 的格连颜色都归我们写过，
        /// 结束时要把颜色一起还原；只改了类型的格（屏幕外、我们没写颜色）
        /// 不该去碰它的颜色，只把类型换回去。
        /// </summary>
        private bool[] typeClaimed = new bool[0];

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
            bakedSpecialType = new TrackColorType[count];
            bakedSpecialColor1 = new Color[count];
            bakedSpecialColor2 = new Color[count];
            bakedSpecialAnimDuration = new float[count];
            bakedSpecialTimeOffset = new float[count];
            bakedStyle = new TrackStyle[count];
            bakedRendererColor = new Color[count];
            bakedRendererGlow = new float[count];
            touched = new bool[count];
            typeClaimed = new bool[count];
            // 位置缓存与格对象表一起在这里建：两者都按格号对齐，
            // 而这里是唯一一次遍历全部格的地方。
            cachedFloorPosition = new Vector2[count];
            boundFloors = new scrFloor[count];
            for (int i = 0; i < count; i++)
            {
                scrFloor floor = context.GetFloorBinding(i);
                boundFloors[i] = floor;
                // 基线取谱面设置，而不是 floorRenderer.color 的快照。
                //
                // 快照取决于 Analyze 跑的那一刻游戏有没有把轨道色烤进格子里，
                // 没烤到就抓到白色/默认值，之后 Lerp(base, target, eased) 在
                // eased < 1 的区间就会整体偏亮——而且随刷新时机时好时坏。
                // 谱面设置是确定的，不受时机影响。
                floorBaseColor[i] = defaultTrackColor;
                floorBaseGlow[i] = defaultGlowIntensity / 100f;

                // **这四个字段是求值器自己会改写的**，所以要留一份原件。
                // 拿不到格对象时留默认值 —— 那种格本来也不会被求值（见 Evaluate
                // 里的 null 判断），还原分支同样碰不到它。
                if (floor == null)
                {
                    continue;
                }
                bakedSpecialType[i] = floor.specialColorType;
                bakedSpecialColor1[i] = floor.specialColor1;
                bakedSpecialColor2[i] = floor.specialColor2;
                bakedSpecialAnimDuration[i] = floor.specialAnimDuration;
                bakedSpecialTimeOffset[i] = floor.specialTimeOffset;
                bakedStyle[i] = floor.initialTrackStyle;
                // 渲染器可能是空的（还没 Awake 完的格），GetColor 在那种情况下
                // 会打一条错误日志再返回品红 —— 先判空。
                if (floor.floorRenderer != null)
                {
                    bakedRendererColor[i] = floor.floorRenderer.color;
                }
                bakedRendererGlow[i] = floor.glowMultiplier;
                Vector3 position = floor.transform.position;
                cachedFloorPosition[i] = new Vector2(position.x, position.y);
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
            // **诊断关着就别付探针税。**
            //
            // 下面每格要取 6 次 `Stopwatch.GetTimestamp()`（约 20ns/次），
            // 本谱 `floorsWithEvents` 约 1100 格，一帧就是 6600 次 ≈ 0.13ms，
            // 而诊断默认是关的 —— 这笔钱一直是白付的。
            // 缓存成局部布尔后，关着时每格只剩一次（可预测的）分支，
            // 开着时行为和原来一模一样。
            bool probe = PreviewDiagnostics.Enabled;

            // 【临时探针】每帧清零。计数是「本帧走了哪些路径」的分布，
            // 时间累加到一个字段里，最后一次送进诊断。
            probeFloors = floorsWithEvents.Count;
            probeWrites = 0;
            probeHits = 0;
            probeRestores = 0;
            probeStyles = 0;
            probeCulled = 0;
            probeCalcTicks = 0L;
            probeTakeTicks = 0L;
            probeWriteTicks = 0L;

            RefreshViewBounds();
            if (viewCull)
            {
                RefreshPositionSlice(frame);
            }

            // 只管真正被某个事件覆盖的格。走遍全部 1133 个格，会让
            // 这一帧对绝大多数根本没有 recolor 事件的格调用 FindLastAffectingEvent，
            // 而每次调用都只是返回 null。
            for (int listIndex = 0; listIndex < floorsWithEvents.Count; listIndex++)
            {
                long probeStart = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                int floorIndex = floorsWithEvents[listIndex];
                scrFloor floor = frame.Context.GetFloorBinding(floorIndex);
                if (floor == null || floor.floorRenderer == null)
                {
                    continue;
                }

                // 屏幕外的格直接跳过：颜色是纯函数，跳过不留状态，
                // 相机移过去的那一帧就会补上。取不到相机时 viewCull 为
                // false，退化成全量——宁可慢，不能漏。
                //
                // 坐标走缓存（见 cachedFloorPosition），不在这里现读：
                // 原生读一次位置比这一段其余计算加起来还贵。
                //
                // 缓存长度和格数对不上（理论上只会在换谱面那一瞬间发生）
                // 就**不裁**：宁可多写几帧，也不能拿一个不存在的坐标把格
                // 判到屏幕外，让它永远停在旧颜色上。
                if (viewCull && floorIndex < cachedFloorPosition.Length)
                {
                    Vector2 position = cachedFloorPosition[floorIndex];
                    float dx = position.x - viewCenter.x;
                    float dy = position.y - viewCenter.y;
                    if (dx * dx + dy * dy > viewRadiusSq)
                    {
                        // **被裁掉的格也要冻住它自己的颜色动画。**
                        //
                        // 见下面写回段的长注释：我们改走
                        // `specialColorType = Single` 之后，游戏就不会再每帧
                        // 为这一格重算颜色了。这一步对看得见的格是省一次
                        // 重复写入，对看不见的格则是「把 2620 次材质写入
                        // 整个从游戏自己的 Update 里拿掉」——那才是这轮
                        // 真正省下来的东西。它只写一个枚举字段，很便宜。
                        //
                        // 它不写颜色，所以进 typeClaimed 而不是 touched：
                        // 结束时要还原的只是「类型」，不该去动颜色。
                        floor.specialColorType = TrackColorType.Single;
                        if (floorIndex < typeClaimed.Length)
                        {
                            typeClaimed[floorIndex] = true;
                        }
                        probeCulled++;
                        if (probe)
                        {
                            probeCalcTicks += System.Diagnostics.Stopwatch.GetTimestamp() - probeStart;
                        }
                        continue;
                    }
                }

                RecolorEvent current = FindLastAffectingEvent(floorIndex, frame.Time);
                if (current == null)
                {
                    // **当前时刻之前没有事件 = 这一格应该回到 baked 状态。**
                    //
                    // 以前这里直接跳过，理由是"这一格每帧自己驱动颜色"——
                    // 那在**从没被我们写过的格**上成立（Glow/Pulse 的 baked 动画
                    // 由 scrFloor.Update 自己跑）。可一旦我们接管过这一格，
                    // `specialColor*` / `glowMultiplier` 已经被事件那套覆盖了，
                    // Update 会继续按那套算 —— 于是把播放头拖回事件之前，
                    // 格还停在事件的颜色上（"经过 575 格再退回 575 之前不还原"）。
                    //
                    // 没被我们碰过的格仍然不碰：还原成它自己才是对的。
                    probeRestores++;
                    RestoreBakedState(floorIndex, floor);
                    if (probe)
                    {
                        probeCalcTicks += System.Diagnostics.Stopwatch.GetTimestamp() - probeStart;
                    }
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
                    probeStyles++;
                    floor.SetTrackStyle(current.Style, false, null);
                }

                float glow = Mathf.LerpUnclamped(
                    floorBaseGlow[Mathf.Clamp(floorIndex, 0, floorBaseGlow.Length - 1)],
                    current.GlowIntensity / 100f,
                    eased);

                // 【临时探针】第一段（读绑定 + 二分 + 算颜色）到这里结束。
                long probeTake = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (probe)
                {
                    probeCalcTicks += probeTake - probeStart;
                }

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
                // 【临时探针】第二段（接管辉度 tween）到这里结束。
                long probeWrite = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (probe)
                {
                    probeTakeTicks += probeWrite - probeTake;
                }

                // 只有「颜色不随时间变」的类型才允许跳过重复写入。
                //
                // 判据从「Single / Stripes」放宽成「不是那几种会动的」：
                // GenerateColor 里只有 Glow/Blink/Switch/Rainbow 会用到时间，
                // 加上交给游戏驱动、每帧都在变的 Volume，其余（Single、Stripes）
                // 都只是返回事件里的常量色，时间一动值也不动，缓存是精确的。
                bool cacheSafe = !styleChanged && !IsAnimatedColor(current.ColorType);
                if (cacheSafe
                    && floorIndex < appliedValid.Length
                    && appliedValid[floorIndex]
                    && appliedColor[floorIndex] == color
                    && appliedGlow[floorIndex] == glow)
                {
                    probeHits++;
                    if (probe)
                    {
                        probeWriteTicks += System.Diagnostics.Stopwatch.GetTimestamp() - probeWrite;
                    }
                    continue;
                }

                probeWrites++;
                // 从这一刻起这一格归我们管 —— 之后回退到事件之前要还原。
                if (floorIndex < touched.Length)
                {
                    touched[floorIndex] = true;
                }
                if (floorIndex < typeClaimed.Length)
                {
                    typeClaimed[floorIndex] = true;
                }
                floor.SetColor(color);
                floor.glowMultiplier = glow;


                // **把这一格的颜色动画关掉，颜色只由我们写。**
                //
                // 以前这里的做法正好相反：把事件的类型/两色/时长/相位原样
                // 写给 scrFloor，让 `scrFloor.Update` 每帧按同一套参数
                // 重算出和我们一样的结果。理由是「只调 SetColor 的话，
                // 下一帧就被 Update 那句重算覆盖掉，Glow/Rainbow 型看起来
                // 完全没生效」。
                //
                // 那个理由只说明「不能只写输出」，并没有要求「必须让
                // Update 跟着一起写」。写成 Single 更划算：
                //
                //   · scrFloor.Update 里的颜色那段是
                //     `if (type != Single || type != Stripes) switch (type)`，
                //     而那个判断恒为真；真正决定做不做事的只有 switch，
                //     而 switch 里**没有** Single/Stripes 的分支。
                //     所以 type = Single 就等于告诉游戏「这一格别管颜色」。
                //   · 于是 2620 格每帧那 2620 次 `material.SetColor`
                //     从游戏自己的 Update 里彻底消失（本轮之前它们一直
                //     在跑，和我们写的完全重复），材质也不再被每帧标脏。
                //   · 顺带修掉一个一直存在的隐患：以前 Update 会按**上一帧**
                //     的相位重算，把我们算好的缓动值（eased < 1 的那段）
                //     覆盖成终值。现在它不碰，缓动才真的生效。
                //
                // 代价是：预览结束（EndSession）或这一格回退到事件之前时，
                // 必须把类型还原成它自己的，否则轨道会停在最后一帧的颜色上
                // 不再自己动。两处都已经还原（见 RestoreBakedState /
                // EndSession），baked 快照就是为这个留的。
                //
                // **Volume 是例外，必须留给游戏自己驱动。**
                // 它的原版实现是 `Color.Lerp(c1, c2, vfxPlus.vTrackerFloat.output)`
                // —— 那个 output 是音频频谱驱动的，我们算不出来，GenerateColor
                // 对 Volume 只能返回一个常量色。冻成 Single 就等于把音频律动
                // 整个抹平成静止色。所以这里原样写回事件的五个输入，
                // 让 Update 继续按老办法算（这一格每帧的写入开销也就还在，
                // 但 Volume 谱面很少，不值得为它冒视觉错误的风险）。
                if (current.ColorType == TrackColorType.Volume)
                {
                    floor.specialColorType = current.ColorType;
                    floor.specialColor1 = current.TrackColor;
                    floor.specialColor2 = current.SecondaryTrackColor;
                    floor.specialAnimDuration = Mathf.Max(0.0001f, current.AnimationDuration);
                    floor.specialTimeOffset = frame.Time + PulsePhaseOffset(floorIndex, current)
                        - Time.time;
                }
                else
                {
                    floor.specialColorType = TrackColorType.Single;
                }

                if (cacheSafe && floorIndex < appliedValid.Length)
                {
                    appliedColor[floorIndex] = color;
                    appliedGlow[floorIndex] = glow;
                    appliedValid[floorIndex] = true;
                }
                else if (floorIndex < appliedValid.Length)
                {
                    // **会动的颜色一旦写过，缓存就必须作废。**
                    // 缓存里存的是「上一次写进去的值」，前提是那个值一直没变。
                    // 走过一段 Glow 之后，格上现在是某个中间色，而缓存里还留着
                    // 更早那个常量色；等播放头再走到一个算出同样常量色的段，
                    // 缓存命中、跳过写入，格就冻在 Glow 的最后一帧上。
                    // 以前游戏自己的 Update 会把它盖回去，现在我们把 Update
                    // 关了，这条错就没人兜了。
                    appliedValid[floorIndex] = false;
                }
                // 【临时探针】第三段（写回格子）到这里结束。
                if (probe)
                {
                    probeWriteTicks += System.Diagnostics.Stopwatch.GetTimestamp() - probeWrite;
                }
            }

            // 【临时探针】把三段耗时和四个计数送进诊断。
            //
            // 三段加起来会比 `RecolorTrack` 本身略大：每次取时间戳大约 20ns，
            // 每格取 6 次、按一千多格算就是 0.13ms 的探针税。
            // 现在整段计时都被 `probe` 门控住了，**诊断关着时这笔税是 0**；
            // 只有开着诊断看比例时才付。看比例，别看绝对值。
            double probeScale = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            PreviewDiagnostics.RecordDomain("Recolor.Calc", probeCalcTicks * probeScale);
            PreviewDiagnostics.RecordDomain("Recolor.Take", probeTakeTicks * probeScale);
            PreviewDiagnostics.RecordDomain("Recolor.Write", probeWriteTicks * probeScale);
            PreviewDiagnostics.RecordFlag("Recolor.N", probeFloors);
            PreviewDiagnostics.RecordFlag("Recolor.Wr", probeWrites);
            PreviewDiagnostics.RecordFlag("Recolor.Ht", probeHits);
            PreviewDiagnostics.RecordFlag("Recolor.Rs", probeRestores);
            PreviewDiagnostics.RecordFlag("Recolor.Sk", probeStyles);
            PreviewDiagnostics.RecordFlag("Recolor.Cull", probeCulled);
        }

        /// <summary>
        /// 每帧只刷四分之一格的世界坐标（见 <see cref="cachedFloorPosition"/>）。
        /// 用 <c>Time.frameCount</c> 选片，保证四片轮流轮到，
        /// 任一格最迟 4 帧就会被刷新一次。
        /// </summary>
        private void RefreshPositionSlice(PreviewEvaluationFrame frame)
        {
            int count = frame.Context.Floors.Count;
            if (cachedFloorPosition.Length != count)
            {
                return;
            }
            int slice = Time.frameCount & (PositionCacheSlices - 1);
            int step = PositionCacheSlices;
            if (positionCacheStale)
            {
                // 跳转后第一帧全刷（见 positionCacheStale）。
                slice = 0;
                step = 1;
                positionCacheStale = false;
            }
            for (int i = slice; i < count; i += step)
            {
                scrFloor floor = frame.Context.GetFloorBinding(i);
                if (floor == null)
                {
                    continue;
                }
                Vector3 position = floor.transform.position;
                cachedFloorPosition[i] = new Vector2(position.x, position.y);
            }
        }

        /// <summary>
        /// 每帧刷新「屏幕范围」：取渲染世界的那台相机（<c>scrCamera.camobj</c>，
        /// 预览开着 render texture 时世界也是它画的，只是输出到了 RT），
        /// 用正交尺寸 + 宽高比算出屏幕矩形的外接圆。
        ///
        /// 取不到相机、或者相机不是正交（没有 orthographicSize 的意义）时
        /// 就关掉裁剪——全量求值只是慢，不会画错。
        /// </summary>
        private void RefreshViewBounds()
        {
            viewCull = false;
            scrCamera rig = scrCamera.instance;
            Camera view = rig == null ? null : rig.camobj;
            if (view == null || !view.gameObject.activeInHierarchy || !view.orthographic)
            {
                return;
            }

            Vector3 center = view.transform.position;
            float halfHeight = view.orthographicSize;
            // 窄视口时横向可能比纵向更宽，取大值，宁可比屏幕大。
            float halfWidth = Mathf.Max(halfHeight * view.aspect, halfHeight);
            float radius = Mathf.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight)
                * ViewCullMargin + ViewCullPadUnits;
            viewCenter = center;
            viewRadiusSq = radius * radius;
            viewCull = true;
        }

        /// <summary>
        /// 把一格还原成"我们接管之前"的样子。只有被写过（<see cref="touched"/>）
        /// 的格才动 —— 没写过的格本来就是这个状态，去写反而会打断它的 baked 动画。
        /// </summary>
        /// <summary>
        /// 把一格还原成"我们接管之前"的样子。
        ///
        /// **两种接管都要认**：<see cref="touched"/>（连颜色一起写过）和
        /// <see cref="typeClaimed"/>（只把类型改成了 Single）。只判 touched 的话，
        /// 「在屏幕外被裁掉、之后跳回来、此刻还没有生效事件」的格会一直卡在
        /// Single 上 —— 它本来该按 baked 的动画类型继续变色，却被冻住了。
        /// 裁剪是每帧都在做的事，所以这条路径用户很容易走到（拖动播放头即可）。
        ///
        /// 颜色只在 touched 时退：typeClaimed-only 的格我们没写颜色，去写反而
        /// 打断它自己的 baked 动画。
        /// </summary>
        private void RestoreBakedState(int floorIndex, scrFloor floor)
        {
            bool wasTouched = floorIndex >= 0 && floorIndex < touched.Length && touched[floorIndex];
            bool wasClaimed = floorIndex >= 0 && floorIndex < typeClaimed.Length
                && typeClaimed[floorIndex];
            if (!wasTouched && !wasClaimed)
            {
                return;
            }
            if (wasTouched)
            {
                touched[floorIndex] = false;
            }
            if (wasClaimed)
            {
                typeClaimed[floorIndex] = false;
            }

            RestoreBakedColourFields(floorIndex, floor);
            if (!wasTouched)
            {
                // 颜色从来不是我们写的，退回类型就够了。
                return;
            }
            floor.SetColor(floorBaseColor[floorIndex]);
            floor.glowMultiplier = floorBaseGlow[floorIndex];

            // 样式也归我们改过（SetTrackStyle），一并退回这一格 baked 的样式。
            // 只有真的推过样式才走 —— SetTrackStyle 要写十来个材质属性。
            if (floorIndex < appliedStyle.Length && appliedStyle[floorIndex] >= 0
                && floorIndex < bakedStyle.Length)
            {
                TrackStyle style = bakedStyle[floorIndex];
                appliedStyle[floorIndex] = (int)style;
                floor.SetTrackStyle(style, false, null);
            }

            // 缓存跟着作废：下一次走到事件上必须重新写一遍。
            if (floorIndex < appliedValid.Length)
            {
                appliedValid[floorIndex] = false;
            }
        }

        /// <summary>
        /// 只把「颜色动画的五个输入」还原成加载时的样子。
        /// 不碰颜色本身 —— 调用方决定要不要连颜色一起退（见 RestoreBakedState /
        /// EndSession）。
        /// </summary>
        private void RestoreBakedColourFields(int floorIndex, scrFloor floor)
        {
            if (floorIndex < 0 || floorIndex >= bakedSpecialType.Length)
            {
                return;
            }
            floor.specialColorType = bakedSpecialType[floorIndex];
            floor.specialColor1 = bakedSpecialColor1[floorIndex];
            floor.specialColor2 = bakedSpecialColor2[floorIndex];
            floor.specialAnimDuration = bakedSpecialAnimDuration[floorIndex];
            floor.specialTimeOffset = bakedSpecialTimeOffset[floorIndex];
        }

        public void EndSession()
        {
            events.Clear();
            appliedValid = new bool[0];

            // **预览结束时把自己改过的格还原回去。**
            //
            // 求值期间我们把格子的 `specialColorType` 写成了 Single（见写回段），
            // 等于让游戏别再自己驱动这一格的颜色。预览一停，如果就这么放着，
            // 轨道会停在最后一帧的颜色上不动 —— 而原版播放时它本该继续
            // 按谱面的颜色动画走。所以这里把快照换回去。
            //
            // **只退那五个「颜色动画输入」；颜色本身分情况。**
            //
            // 对 baked 类型会动的格（Glow/Blink/Switch/Rainbow/Volume）：
            // 类型一还回去，`scrFloor.Update` 下一帧就拿着还原好的参数自己重算，
            // 所以这里不能碰颜色 —— 碰了会让整条轨道闪一下。
            //
            // 对 baked 类型是 Single/Stripes 的格：Update 那段 switch 里压根
            // 没有它们的分支，还原类型之后**没有任何东西会重写颜色**。就这样
            // 放着，格会永远停在我们最后写的预览色上（谱面底色是别的色时
            // 一眼可见）。所以这几种要连颜色和辉度一起退回接管前的快照。
            //
            // 用 boundFloors 而不是 floorsWithEvents 去取格对象：这里没有
            // 求值帧，拿不到 context；boundFloors 是 Analyze 时按格号存下的
            // 同一批引用。
            for (int i = 0; i < boundFloors.Length; i++)
            {
                scrFloor floor = boundFloors[i];
                if (floor == null)
                {
                    continue;
                }
                bool claimed = (i < touched.Length && touched[i])
                    || (i < typeClaimed.Length && typeClaimed[i]);
                if (!claimed)
                {
                    continue;
                }
                RestoreBakedColourFields(i, floor);
                if (i < bakedSpecialType.Length
                    && !IsAnimatedColor(bakedSpecialType[i])
                    && i < bakedRendererColor.Length
                    && floor.floorRenderer != null)
                {
                    floor.SetColor(bakedRendererColor[i]);
                    if (i < bakedRendererGlow.Length)
                    {
                        floor.glowMultiplier = bakedRendererGlow[i];
                    }
                }
            }
            touched = new bool[0];
            typeClaimed = new bool[0];
        }

        /// <summary>
        /// 播放头跳转：把「已经应用过」的缓存全部作废，强制下一帧重写一遍
        /// 颜色与辉度。
        ///
        /// 那个缓存的前提是**播放头连续移动**——格子的颜色如果被游戏自己
        /// 的机制（或另一次求值）改过，而我们这边还记着"已应用"，就会一路
        /// 跳过写入，格停在一个不对的颜色上。跳转正是这种情况最容易发生的
        /// 时刻，而在此之前没有任何一处收到通知。
        /// </summary>
        public void ResetHistory()
        {
            for (int i = 0; i < appliedValid.Length; i++)
            {
                appliedValid[i] = false;
            }
            // 位置缓存也跟着作废（见 positionCacheStale）。
            positionCacheStale = true;
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

        /// <summary>
        /// 这个颜色类型会不会随时间变。Glow/Blink/Switch/Rainbow 在
        /// <see cref="GenerateColor"/> 里用到了时间；Volume 的时间源是音频频谱，
        /// 我们算不出来（见写回段），但它同样每帧在变 —— 所以这五种都不许走
        /// 「值没变就跳过写入」的缓存。其余（Single / Stripes）只返回事件里的
        /// 常量色，缓存是精确的。
        /// </summary>
        private static bool IsAnimatedColor(TrackColorType type)
        {
            return type == TrackColorType.Glow
                || type == TrackColorType.Blink
                || type == TrackColorType.Switch
                || type == TrackColorType.Rainbow
                || type == TrackColorType.Volume;
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
                    // **线性斜坡**，不是方波。
                    // 原版 scrFloor.Update 的 Blink 分支是
                    // `Lerp(c1, c2, DOVirtual.EasedValue(0,1, num % num2 / num2, Linear))`，
                    // 也就是一个 0→1 的锯齿：每个周期从第一色缓缓推到第二色，
                    // 到周期尾端再跳回第一色。
                    // 这里原来写的是半周期方波，那是把 Blink 和 Switch 搞反了。
                    // 以前游戏自己的 Update 会盖掉我们的值，错了也看不出来；
                    // 现在我们把 Update 关了（类型写成 Single），我们算的
                    // 就是最终结果，必须和原版逐帧一致。
                    return Color.Lerp(value.TrackColor, value.SecondaryTrackColor,
                        ((phase % 1f) + 1f) % 1f);
                case TrackColorType.Switch:
                    // **半周期硬切**，不是三角波。
                    // 原版 scrFloor.Update 的 Switch 分支是
                    // `num % num2 < num2 / 2 ? specialColor1 : specialColor2`，
                    // 前半周期第一色、后半周期第二色，中间没有过渡。
                    // 这里原来写的是三角波（线性推过去再推回来），
                    // 同样是被游戏 Update 盖住了才一直没暴露。
                    return ((phase % 1f) + 1f) % 1f < 0.5f
                        ? value.TrackColor : value.SecondaryTrackColor;
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
