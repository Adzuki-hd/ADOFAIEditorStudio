using System;
using System.Collections.Generic;
using System.Reflection;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 常见可视装饰物属性的绝对时间 MoveDecorations 求值。事件按
    /// 装饰物 tag 匹配，然后应用到游戏自己的 scrDecoration
    /// 实例上。
    /// </summary>
    internal sealed class DecorationCorePreviewDomain : IPreviewDomain
    {
        private sealed class MoveEvent
        {
            internal double Start;
            internal float Duration;
            internal Ease Ease;
            internal MaskingType Masking;
            internal bool HasMasking;
            internal bool HasUseMaskingDepth;
            internal bool UseMaskingDepth;
            internal bool HasMaskingFrontDepth;
            internal int MaskingFrontDepth;
            internal bool HasMaskingBackDepth;
            internal int MaskingBackDepth;
            internal bool HasVisible;
            internal bool Visible;
            internal bool HasPlacement;
            internal DecPlacementType Placement;
            internal bool HasPosition;
            internal Vector2 Position;
            internal bool HasPivot;
            internal Vector2 Pivot;
            internal bool HasRotation;
            internal float Rotation;
            internal bool HasScale;
            internal Vector2 Scale;
            internal bool HasColor;
            internal Color Color;
            internal bool HasOpacity;
            internal float Opacity;
            internal bool HasDepth;
            internal int Depth;
            internal bool HasParallax;
            internal Vector2 Parallax;
            internal bool HasParallaxOffset;
            internal Vector2 ParallaxOffset;

            /// <summary>
            /// 一次 decorationImage 换图。它属于原生事件，尽管不是
            /// MoveDecorations 式的 tween：谱面靠每帧发一个事件
            /// 来搭逐帧动画，每个事件指定下一张图，彼此用 angleOffset
            /// 隔开不到一拍。
            /// </summary>
            internal bool HasImage;
            internal string ImageFilename;

            /// <summary>
            /// 在谱面事件数组里的原始下标。
            ///
            /// 排序必须用它做第二键。原生是按数组顺序逐个
            /// 应用事件的（ApplyEventsToFloors 遍历 events 列表），
            /// 而开始时间相同的多条事件谁最后生效，就取决于那个顺序。
            ///
            /// 光按 Start 排序不够：Events 的填充是按 tag 分组的
            /// （一个装饰物的 tag 有多个时，先灌完第一个 tag 的桶
            /// 再灌第二个），所以同一时刻的"最后一条"会变成
            /// 某个 tag 桶里的最后一条，而不是数组里的最后一条。
            ///
            /// 在原版里量到：第 426 格上装饰物 rainbow.png 的 tag 是
            /// "flarer flare"，事件数组以 tag=flare 的 opacity=100 收尾，
            /// 但按 tag 分组会把 tag=flarer 的 opacity=5 排到后面，
            /// 于是那朵彩虹（5% 不透明度）一直显形，而原版看不见。
            /// </summary>
            internal int Order;
        }

        private sealed class DecorationState
        {
            internal scrDecoration Decoration;
            internal Vector2 Position;

            /// <summary>
            /// dec.startPos，原生效果 tween 位置的起点。不同于
            /// Position，后者镜像的是 dec.pivotPosVec。
            /// </summary>
            internal Vector2 Baseline;

            internal Vector2 Pivot;
            internal float Rotation;
            internal Vector2 Scale;
            internal Color Color;
            internal float Opacity;
            internal int Depth;
            internal DecPlacementType Placement;
            internal Vector2 Parallax;
            internal Vector2 ParallaxOffset;
            internal bool Visible;
            internal bool HasDepthState;

            /// <summary>遮罩目标 tag（`maskingTarget`），决定这片遮罩管谁。</summary>
            internal string MaskingTarget;
            internal string AppliedMaskingTarget;

            // ── 写入去重缓存 ────────────────────────────────────────────
            // Unity 对"被动过的对象"要付一次延迟处理成本，与改了几个属性无关。
            // 本谱 541 件装饰物里绝大多数整段没动，不去重就是每帧 541 × 6 次
            // setter，实测 DecorationCore 单帧 25ms（见日志 [PreviewCost]）。
            internal bool HasTransformApplied;
            internal Vector2 AppliedPosition;
            internal Vector2 AppliedPivot;
            internal float AppliedRotation;
            internal Vector2 AppliedScale;
            internal Color AppliedColor;
            internal Vector2 AppliedParallax;
            internal Vector2 AppliedParallaxOffset;
            internal DecPlacementType AppliedPlacement;
            internal bool MaskingApplied;
            internal MaskingType AppliedMasking;
            internal int LastMaskingResyncFrame = -100000;
            internal int LastMaskWriteFrame = -100000;
            /// <summary>该装饰物在 SettledAt 时刻之后直到 NextEventStart 都没有活动事件。</summary>
            internal bool Settled;
            internal float SettledAt;
            internal float NextEventStart;
            internal bool HasVisibleApplied;
            internal bool AppliedVisible;
            internal float AppliedOpacity;
            /// <summary>本帧求值出来的遮罩类型/可见性/透明度，供独立的遮罩通道使用。</summary>
            internal MaskingType FrameMasking;
            internal bool FrameVisible;
            internal float FrameOpacity;


            /// <summary>上次驱动 AO 遮罩合成刷新的帧号。</summary>
            internal int LastAlphaMaskForceFrame = -1000;

            /// <summary>上次推给组件的排序层，只在真的变化时才重复写。</summary>
            internal bool DepthApplied;
            internal int AppliedDepth;

            /// <summary>
            /// 这件装饰物在谱面里的创建楼层（`decorations[i].floor`）。
            /// 早于这一格时它还不存在 —— 原生是走到那格才把它加进场景的。
            /// </summary>
            internal int CreationFloor;
            internal MaskingType Masking;
            internal bool HasMasking;
            internal bool UseMaskingDepth;
            internal int MaskingFrontDepth;
            internal int MaskingBackDepth;

            /// <summary>
            /// 本域上次通过 SetMaskingDepth 推下去的值，这样只在值
            /// 真正变化时才跑那个 setter。它会改写 SpriteMask 的排序层和
            /// 排序号，不是每帧都该重复做的事。
            /// </summary>
            internal bool MaskingDepthApplied;

            /// <summary>
            /// 本域是否把行星的世界坐标预加进了这个装饰物的 pivotPosVec。
            /// 退出预览时要还原，否则原生播放（那时 paused=false，原生自己
            /// 还会再加一次行星坐标）会偏移两遍。
            /// </summary>
            internal bool DisplacedForPlanet;

            /// <summary>
            /// 最近推给装饰物的图，这样只在活跃的图片事件变化时
            /// 才换图。sprite 加载不免费，而逐帧动画
            /// 每帧都有一个事件。
            /// </summary>
            internal string AppliedImage;

            /// <summary>
            /// 装饰物创作时用的那张图，在有图片事件另行指定之前，
            /// 它就显示这张。每次求值都从这里起步，重放到当前
            /// 时间为止的事件，所以结果只取决于时间，而不取决于
            /// 播放头是从哪个方向过来的。
            ///
            /// 把 AppliedImage 跨求值留着，会让图片变成只写一次：
            /// 在 Factory 谱面上，pp4 装饰物在 567 格以 p4.1.png 创建，
            /// 在 615 格换成 p4.2.png，于是拖过 615 再拖回 567
            /// 它还显示 p4.2.png，因为 567 到 614 之间没有事件提到
            /// 图片，而带着的那个值从来没被重置。
            /// </summary>
            internal string DefaultImage;

            internal readonly List<MoveEvent> Events = new List<MoveEvent>();
        }

        private readonly List<DecorationState> states = new List<DecorationState>();

        /// <summary>
        /// 「下一帧必须重算」。参数栏改勾选状态时置位 —— 求值器平时在播放头
        /// 不动的时候整帧早退，不置这一位就看不到改动。
        /// </summary>
        internal static bool PositionSeedDirty;

        /// <summary>行星跟随探针上一次输出的帧号，避免每帧刷屏。仅诊断时使用。</summary>
        private static int lastPlanetFollowProbeFrame = -1000;

        /// <summary>临时探针：1095 之后打一次「当前可见装饰物按原图分组」的汇总。</summary>

        /// <summary>是否按作者标记剔除隐藏的装饰物。</summary>
        private bool hiddenCulling;

        /// <summary>只在预览里处理带遮罩的那几十件，避免在 500+ 件的主循环里逐件判断。</summary>
        private readonly System.Collections.Generic.List<DecorationState> maskCandidateStates
            = new System.Collections.Generic.List<DecorationState>(64);
        private readonly System.Collections.Generic.List<int> maskCandidates
            = new System.Collections.Generic.List<int>(64);

        /// <summary>单次求值允许改写的遮罩件数上限（分摊 O(n) 的缓存重算）。</summary>
        private const int MaskWritesPerEval = 8;

        // 统计累加器。真正输出走 PreviewDiagnostics（按帧聚合），
        // 这里只保留累加，方便以后接更细的分项。
        private int statDecorations;
        private long statImageTicks;
        private long statTransformTicks;
        private long statVisibleTicks;
        private long statModelTicks;
        private bool hasEvaluated;
        private float lastEvaluatedTime = float.NaN;
        private int lastEvaluatedFrame = -1000;

        // 临时：统计每帧各类工作量（定位 25ms 花在哪），修完删。

        // 临时诊断用（修完删）
        private string diagnosticSamples = string.Empty;

        /// <summary>是否剔除不透明度为 0 的装饰物（遮罩与粒子除外）。</summary>
        private bool cullTransparent;

        /// <summary>`maskingType` 是私有字段，反射结果缓存一次。</summary>
        private static System.Reflection.FieldInfo maskingTypeField;

        public string Name
        {
            get { return "DecorationCore"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            // **把原生切到老遮罩管线，只切一次。**
            //
            // V15 管线对每件可见遮罩件逐帧重合成 RT（`ApplyAlphaMaskToRT`），
            // 杪夏 800 格附近几十件同时在画面里就是 4~9 帧。
            // 老管线只切 `spriteRenderer.enabled` / `spriteMask.enabled`。
            //
            // **必须只切一次**：这是全局位，逐帧对齐会让管理器反复重建装饰物的
            // 着色路径 —— 那是我之前引入过一次 120~150ms/帧的回归。
            ApplyLegacyMaskPipeline(true);

            // **分析过就一定要重画一次。**
            //
            // 这两条早退状态（`hasEvaluated` / `lastEvaluatedTime`）属于实例，
            // 而重新分析的时候播放头往往没动。不重置的话第一次 Evaluate 就
            // 直接 return，新算出来的状态一份都没写下去 —— 用户看到的就是
            // 「勾回来了，位置没回，动一下播放头才瞬间归位」。
            hasEvaluated = false;
            lastEvaluatedTime = float.NaN;
            lastEvaluatedFrame = -1000;

            states.Clear();
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || manager.allDecorations == null)
            {
                return;
            }

            Dictionary<string, List<MoveEvent>> taggedEvents = BuildTaggedEvents(context);
            statDecorations = manager.allDecorations.Count;
            statImageTicks = 0L;
            statVisibleTicks = 0L;
            for (int i = 0; i < manager.allDecorations.Count; i++)
            {
                scrDecoration decoration = manager.allDecorations[i];
                if (decoration == null || decoration.sourceLevelEvent == null)
                {
                    continue;
                }

                DecorationState state = new DecorationState
                {
                    Decoration = decoration,
                    CreationFloor = decoration.sourceLevelEvent == null
                        ? 0
                        : decoration.sourceLevelEvent.floor,
                    // **位置也要从作者值起步，而且要认「禁用」。**
                    //
                    // 装饰物自己的位置由 `scrDecoration.Setup` 写死（它不看
                    // `disabled`），所以作者把「位置」勾掉之后，组件上还停在被
                    // 禁用的那个坐标上。谱面存盘时这个键不会写出去，重开后装饰物
                    // 回到落点；预览得跟上这一点，否则勾选框看起来毫无作用。
                    Position = AuthorPosition(decoration),
                    Baseline = decoration.startPos,
                    Pivot = decoration.pivotOffsetVec,
                    Rotation = decoration.rotAngle,
                    // **这三个必须从作者写的创建值起步，不能读组件上的当前值。**
                    //
                    // 组件上的值是「上一次求值/上一次播放留下的」：作者写的是
                    // `opacity: 0`（本谱的 5-hat.png 全是这样，等 1127 格才抬到
                    // 100），一旦播放曾经把它抬起来过，再拖回 1090 一带，种子就是
                    // 100 —— 帽子在它出场之前就画出来了。同一个原因还会让一堆
                    // 「创建时 scale 0 / color 透明」的装饰物提前露出来。
                    //
                    // 求值必须是时间的纯函数：种子取作者数据，事件链从 0 开始重放。
                    Scale = AuthorScale(decoration.sourceLevelEvent, decoration.scaleVec) / 100f,
                    Color = AuthorColor(decoration.sourceLevelEvent, "color", Color.white),
                    Opacity = Mathf.Clamp01(AuthorFloat(
                        decoration.sourceLevelEvent, "opacity", decoration.opacity * 100f) / 100f),
                    // **深度也要从作者值起步，而且第一帧就写下去。**
                    // 以前 HasDepthState 只在「深度事件走完」后才置真，
                    // 于是播放头从深度事件之后拖回前面时，组件还留着那条事件的
                    // 排序层 —— 一层错位就是「本该被挡住的东西跑到前面」。
                    Depth = (int)AuthorFloat(decoration.sourceLevelEvent, "depth", 0f),
                    HasDepthState = true,
                    Placement = decoration.placementType,
                    Parallax = decoration.parallax == null
                        ? Vector2.zero
                        : decoration.parallax.multiplier * 100f,
                    ParallaxOffset = decoration.parallaxOffset,
                    // 用事件逻辑上的可见性，不用实时渲染状态。
                    // scrDecoration.GetVisible() 把 alpha 折进了结果，所以一个
                    // 从不透明度 0 起步的装饰物会被快照成
                    // 不可见，然后被永久隐藏，因为之后只提高
                    // 不透明度的 MoveDecorations 事件并不会打开
                    // "visible" 属性。
                    Visible = decoration.sourceLevelEvent.visible,
                    // 装饰物自己的遮罩状态，从组件读，而不是
                    // 经过 isMask() 折叠。
                    //
                    // isMask() 只是「type == Mask」，所以把 VisibleInsideMask 和
                    // VisibleOutsideMask 播种成 None 会误报起始状态。这
                    // 在应用步骤也比较 isMask() 时无害——两端都读到
                    // false，setter 从没被调用，组件保持它的原生
                    // 值——但应用步骤一旦开始比较真实类型，这个
                    // 种子就每帧都和组件不一致，把遮罩剥掉了：
                    // 作者设定为被遮罩裁剪的背景，从关卡一开始就
                    // 完整画了出来。在 Factory 谱面上，p5c 装饰物是
                    // 深度 -9 处一个 260x700 的白色矩形，它的全部用途就是当遮罩，
                    // 没有真实值它就会渲染成一块白色遮挡物。
                    // **优先用作者写的 `maskingType`。**
                    //
                    // 从组件读有两个坑：组件上的私有字段可能是别的求值轮次留下的，
                    // 而且 `isMask()` 只折叠成 Mask / 非 Mask。一旦把作者写的
                    // `Mask` 读成了 `None`，那片遮罩就会被当成普通贴图**画出来**
                    // —— 它的图往往是一大块形状，直接盖住后面的东西（本谱 5-circle
                    // 的 `-m` 变体就是这样：depth -1 落在 Default 层，压在所有 Bg 层
                    // 的装饰物和轨道上）。
                    Masking = AuthorMasking(decoration.sourceLevelEvent)
                        ?? ReadMaskingType(decoration as scrVisualDecoration)
                        ?? (decoration is scrVisualDecoration maskSource && maskSource.isMask()
                            ? MaskingType.Mask
                            : MaskingType.None),
                    // 从作者写的值播种，跟 Masking 从组件播种的方式一致。
                    // SetMaskingDepth 从这些值重新推导 SpriteMask 的排序范围，
                    // 所以起始值错了就会裁剪错的那一段，直到某个事件
                    // 碰巧又把它重申一遍。
                    // `useMaskingDepth` 在谱面里写的是 **"Enabled" / "Disabled" 字符串**，
                    // `GetBool` 是硬转换，会失败并落回 false —— 于是「自定义遮罩深度」
                    // 被当成关闭，`spriteMask.isCustomRangeActive` 被写成 false，
                    // 遮罩范围从 1~1 变成默认全范围：本该只裁一小段深度的遮罩
                    // 变成到处都裁，后面整段画面异常。用解析器读字符串。
                    MaskingTarget = AuthorString(decoration.sourceLevelEvent, "maskingTarget", null),
                    UseMaskingDepth = AuthorBool(
                        decoration.sourceLevelEvent, "useMaskingDepth", false),
                    MaskingFrontDepth = AuthorInt(decoration.sourceLevelEvent, "maskingFrontDepth", -1),
                    MaskingBackDepth = AuthorInt(decoration.sourceLevelEvent, "maskingBackDepth", -1)
                };

                // 作者写的图是每次求值重放的起点，所以
                // 向后拖动和向前拖动落在同一个 sprite 上。
                state.DefaultImage = state.AppliedImage = AuthorString(decoration.sourceLevelEvent, "decorationImage", string.Empty);

                string tag = AuthorString(decoration.sourceLevelEvent, "tag", string.Empty);
                string[] tags = string.IsNullOrEmpty(tag)
                    ? new[] { "NO TAG" }
                    : tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<MoveEvent> unique = new HashSet<MoveEvent>();
                for (int tagIndex = 0; tagIndex < tags.Length; tagIndex++)
                {
                    List<MoveEvent> bucket;
                    if (!taggedEvents.TryGetValue(tags[tagIndex], out bucket))
                    {
                        continue;
                    }

                    for (int eventIndex = 0; eventIndex < bucket.Count; eventIndex++)
                    {
                        if (unique.Add(bucket[eventIndex]))
                        {
                            state.Events.Add(bucket[eventIndex]);
                        }
                    }
                }

                // 第二键用原始数组下标。Events 是按 tag 分组灌进来的，
                // 所以同一开始时刻的先后已经被打乱；光按 Start 排（哪怕
                // 排序本身稳定）保不住原生那种「数组顺序决定谁最后生效」。
                // 见 MoveEvent.Order 的说明。
                PreviewStableSort.Sort(state.Events, (left, right) =>
                {
                    int byStart = left.Start.CompareTo(right.Start);
                    return byStart != 0 ? byStart : left.Order.CompareTo(right.Order);
                });
                states.Add(state);
                // 只把真正带遮罩的那几十件登记成候选，主循环不再逐件判断。
                if (state.Masking != MaskingType.None && decoration is scrVisualDecoration)
                {
                    maskCandidateStates.Add(state);
                }
            }

            int totalEvents = 0;
            int matchedDecorations = 0;
            for (int i = 0; i < states.Count; i++)
            {
                totalEvents += states[i].Events.Count;
                if (states[i].Events.Count > 0)
                {
                    matchedDecorations++;
                }
            }

            Main.Logger.Log("[StaticPreview/DecorationCore] analyzed " + states.Count
                + " decorations; taggedEvents=" + taggedEvents.Count
                + " matched=" + matchedDecorations
                + " totalBinding=" + totalEvents + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {

            // **同一时间点只算一次。**
            //
            // 求值器一帧会被调用 3~4 次（同一帧内多个调用点），而装饰物域是这里
            // 最重的域：每件装饰物要跑完事件表、写 6 类属性。实测同一帧重复计算
            // 占掉了全部装饰物开销的四分之三。
            float frameTime = frame.Time;
            // **勾选状态变了要能立刻重算。**
            //
            // 下面那两条早退（时间没变就不重算、两帧一推）是性能的命根子，
            // 可它们也意味着「播放头不动时，参数怎么改画面都不会更新」——
            // 用户看到的就是「勾回来位置不动，动一下播放头才瞬间归位」。
            // 所以参数栏改动时置一个脏位，这一帧绕过两条早退。
            bool reseed = PositionSeedDirty;
            if (reseed)
            {
                PositionSeedDirty = false;
            }
            if (!reseed
                && hasEvaluated
                && Mathf.Abs(frameTime - lastEvaluatedTime) <= 0.00001f)
            {
                return;
            }

            // **每两帧推一次。**
            //
            // 这个域的开销几乎全在 Unity 的 setter 上（105 件装饰物 × 位置/旋转/
            // 缩放/颜色/视差/深度），实测单次 79~99ms 且随播放推进变多
            // （进入动画的装饰物越来越多）。装饰物是背景元素，30Hz 更新肉眼看不出，
            // 换来的是整体帧率翻倍。改这个数字即可调（1 = 每帧）。
            const int EvaluateStride = 2;
            if (!reseed && hasEvaluated && Time.frameCount - lastEvaluatedFrame < EvaluateStride)
            {
                return;
            }
            lastEvaluatedFrame = Time.frameCount;
            hasEvaluated = true;
            lastEvaluatedTime = frameTime;

            if (reseed)
            {
                // 起点按作者数据重算，并把「已经摆过」的标记清掉，
                // 让这一帧真的把新位置写下去。
                for (int i = 0; i < states.Count; i++)
                {
                    DecorationState dirty = states[i];
                    if (dirty.Decoration == null)
                    {
                        continue;
                    }
                    dirty.Position = AuthorPosition(dirty.Decoration);
                    dirty.HasTransformApplied = false;
                    dirty.Settled = false;
                }
            }

            // **预览期间切到老遮罩管线。**
            //
            // V15 管线：每件可见遮罩件每帧一张 RT（`ApplyAlphaMaskToRT`），
            // 杪夏实测单帧 140~280ms。
            // 老管线：只切 renderer / spriteMask 的 enabled，几十件也无压力。
            // 代价是不支持 maskingFrontDepth/BackDepth 那套自定义深度范围。
            // **不要改 `disableV15Features`。**
            //
            // 它是全局开关，原生各处都会读；预览里每帧去对齐它会让管理器重建
            // 装饰物的着色路径 —— 实测单帧 120~150ms，比遮罩本身还贵。
            // 老管线的选择只影响我们自己怎么写遮罩，不动这个全局位。
            bool legacyMask = Main.Settings != null && Main.Settings.UseLegacySpriteMask
                && ADOBase.controller != null && ADOBase.controller.disableV15Features;

            hiddenCulling = Main.Settings != null && Main.Settings.CullHiddenDecorations;
            cullTransparent = Main.Settings != null && Main.Settings.CullTransparentDecorations;

            for (int i = 0; i < states.Count; i++)
            {
                DecorationState state = states[i];
                scrDecoration decoration = state.Decoration;
                if (decoration == null)
                {
                    continue;
                }

                Vector2 position = state.Position;
                Vector2 pivot = state.Pivot;
                float rotation = state.Rotation;
                Vector2 scale = state.Scale;
                Color color = state.Color;
                float opacity = state.Opacity;
                int depth = state.Depth;
                DecPlacementType placement = state.Placement;
                Vector2 parallax = state.Parallax;
                long modelStart = System.Diagnostics.Stopwatch.GetTimestamp();
                // **定型期整件跳过。**
                //
                // 装饰物域的固定开销几乎全是"读组件属性 + 写回组件属性"，
                // 与动画是否在跑无关。本谱 105 件装饰物里绝大多数整段不动，
                // 却每帧被读一遍、写一遍 —— 单帧 20ms 就是这么来的。
                // 上一轮已经算过"下一个事件的开始时刻"，在它到来之前没有任何
                // 属性会变，直接 continue。
                if (state.Settled
                    && frame.Time >= state.SettledAt
                    && frame.Time < state.NextEventStart)
                {
                    continue;
                }

                Vector2 parallaxOffset = state.ParallaxOffset;
                bool visible = state.Visible;
                bool hasDepthState = state.HasDepthState;
                MaskingType masking = state.Masking;
                bool useMaskingDepth = state.UseMaskingDepth;
                int maskingFrontDepth = state.MaskingFrontDepth;
                int maskingBackDepth = state.MaskingBackDepth;
                string imageFilename = state.DefaultImage;

                // **先把"当前时刻之前的事件"用二分定位，再从后往前找仍在跑的那几条。**
                //
                // 原来是线性扫：播放头越靠后，扫过的历史事件越多 —— 本谱 3257 条动作，
                // 105 件装饰物各扫一遍，帧时随播放推进从 23ms 涨到 50ms，
                // 就是"越玩越卡"本身。事件表已按 Start 排序（Analyze 里排过），
                // 所以可以二分；活动判定只需回看最近若干条（缓动时长有限）。
                int eventCount = state.Events.Count;
                int cut = UpperBoundByStart(state.Events, frame.Time);
                float nextEventStart = cut < eventCount
                    ? (float)state.Events[cut].Start
                    : float.PositiveInfinity;

                bool anyActive = false;
                const int ActiveLookBack = 256;
                int lookBackFrom = cut - 1;
                int lookBackTo = Mathf.Max(0, cut - ActiveLookBack);
                for (int probe = lookBackFrom; probe >= lookBackTo; probe--)
                {
                    MoveEvent candidate = state.Events[probe];
                    // **零时长事件不算"仍在活动"。**
                    // 原来写成 `Duration <= 0 || time <= Start + Duration`，
                    // 于是回看窗口里只要有一条零时长事件，anyActive 就恒为真，
                    // 定型判定永远不成立、整件跳过从不触发 —— 这是 12~16ms
                    // 固定开销的直接原因。零时长只在恰好落在该时刻时才算活动。
                    if (frame.Time <= candidate.Start + candidate.Duration)
                    {
                        anyActive = true;
                        break;
                    }
                }

                for (int eventIndex = 0; eventIndex < cut; eventIndex++)
                {
                    MoveEvent value = state.Events[eventIndex];

                    float ratio = value.Duration <= 0f
                        ? 1f
                        : Mathf.Clamp01((frame.Time - (float)value.Start) / value.Duration);
                    float eased = PreviewEase.EasedValue(0f, 1f, ratio, value.Ease);
                    if (value.HasVisible)
                    {
                        // 原生在效果一开始就设可见性
                        // （"if (visibleUsed) dec.SetVisible(visible && !dec.forceHide)"），
                        // 没有缓动闸门。等 eased >= 1 会让装饰物
                        // 在整个长移动过程里一直隐藏，所以一段 16 拍的上升
                        // 在结束之前都是看不见的。
                        visible = value.Visible;
                    }
                    if (value.HasPlacement)
                    {
                        placement = value.Placement;
                    }
                    if (value.HasMasking)
                    {
                        // 原生立即应用 maskingType，跟 visible 一样：没有缓动闸门。
                        masking = value.Masking;
                    }
                    // 遮罩深度就是 SetMaskingDepth 变成 SpriteMask 排序
                    // 范围的东西，所以它决定了遮罩实际裁剪哪些装饰物。原生
                    // 两者都立即应用，没有缓动闸门。
                    if (value.HasUseMaskingDepth)
                    {
                        useMaskingDepth = value.UseMaskingDepth;
                    }
                    if (value.HasMaskingFrontDepth)
                    {
                        maskingFrontDepth = value.MaskingFrontDepth;
                    }
                    if (value.HasMaskingBackDepth)
                    {
                        maskingBackDepth = value.MaskingBackDepth;
                    }
                    // 谱面里的 null 解出来是 float.NaN，在原生效果里意思是
                    // 「这个轴别动」。把 NaN 折进向量
                    // 会在每次拖动时污染装饰物位置。
                    if (value.HasPosition)
                    {
                        // 原生按模式挑起点：
                        //   startPos = (placement == LastPosition) ? dec.pivotPosVec : dec.startPos
                        // 然后 tween 到 startPos + targetPos。dec.startPos 和
                        // dec.pivotPosVec 是两个不同的量（游戏调
                        // SetPosition(startPos, pivotOffset) 时两个都传），所以两种模式都用
                        // pivot 快照的话，只要两者不同，就会把每个位置事件
                        // 偏移一个常量。
                        Vector2 origin = value.Placement == DecPlacementType.LastPosition
                            ? position
                            : state.Baseline;
                        // 谱面留成 null 的轴意思是「这个轴别动」：原生
                        // 根本不为它建 tween。拿 origin 给目标轴播种
                        // 会把那个轴猛地拉到基线，所以一个只设了 Y 的事件
                        // 照样能把 X 拖到起始位置。
                        Vector2 target = position;
                        if (!float.IsNaN(value.Position.x))
                        {
                            target.x = origin.x + value.Position.x * frame.Context.TileSize;
                        }
                        if (!float.IsNaN(value.Position.y))
                        {
                            target.y = origin.y + value.Position.y * frame.Context.TileSize;
                        }
                        position = Vector2.LerpUnclamped(position, target, eased);
                    }
                    if (value.HasPivot)
                    {
                        // pivotOffset 是「绝对」的：原生做 "targetPivot = tileSize *
                        // pivotOffset" 然后直接把 pivot tween 到那里，没有
                        // 基线。从 state.Pivot 播种等于每个事件都把基线又加了一遍，
                        // 于是每个 pivot 事件落点都离该在的位置越来越远，
                        // 连着一串就稳定漂走。
                        float pivotX = float.IsNaN(value.Pivot.x)
                            ? pivot.x
                            : value.Pivot.x * frame.Context.TileSize;
                        float pivotY = float.IsNaN(value.Pivot.y)
                            ? pivot.y
                            : value.Pivot.y * frame.Context.TileSize;
                        pivot = Vector2.LerpUnclamped(pivot, new Vector2(pivotX, pivotY), eased);
                    }
                    if (value.HasRotation)
                    {
                        rotation = Mathf.LerpUnclamped(rotation, value.Rotation, eased);
                    }
                    if (value.HasScale)
                    {
                        Vector2 target = new Vector2(
                            float.IsNaN(value.Scale.x) ? scale.x : value.Scale.x / 100f,
                            float.IsNaN(value.Scale.y) ? scale.y : value.Scale.y / 100f);
                        scale = Vector2.LerpUnclamped(scale, target, eased);
                    }
                    if (value.HasColor)
                    {
                        color = Color.LerpUnclamped(color, value.Color, eased);
                    }
                    if (value.HasOpacity)
                    {
                        opacity = Mathf.LerpUnclamped(opacity, value.Opacity / 100f, eased);
                    }
                    // **深度没有缓动**：原生 ffxMoveDecorationsPlus.StartEffect 里
                    // 是紧挨着 SetVisible 的一句 `if (depthUsed) dec.SetDepth(targetDepth);`
                    // —— 事件一开始就落下去。以前这里加了 `eased >= 1f` 的门，
                    // 于是带 duration 的深度事件在整段过渡里都不生效，排序层停在旧值：
                    // 对比原生导出，51 件 order 不一致、16 件连层都反了。
                    if (value.HasDepth)
                    {
                        depth = value.Depth;
                        hasDepthState = true;
                    }
                    if (value.HasParallax)
                    {
                        Vector2 target = new Vector2(
                            float.IsNaN(value.Parallax.x) ? parallax.x : value.Parallax.x,
                            float.IsNaN(value.Parallax.y) ? parallax.y : value.Parallax.y);
                        parallax = Vector2.LerpUnclamped(parallax, target, eased);
                    }
                    if (value.HasParallaxOffset)
                    {
                        // 绝对量，跟 pivotOffset 一样：原生算
                        // "targetParallaxOffset = tileSize * parallaxOffset" 然后直接 tween 到
        // 那个值。加上基线让它漂移的方式
        // 和 pivot 一样。
                        float offsetX = float.IsNaN(value.ParallaxOffset.x)
                            ? parallaxOffset.x
                            : value.ParallaxOffset.x * frame.Context.TileSize;
                        float offsetY = float.IsNaN(value.ParallaxOffset.y)
                            ? parallaxOffset.y
                            : value.ParallaxOffset.y * frame.Context.TileSize;
                        parallaxOffset = Vector2.LerpUnclamped(
                            parallaxOffset, new Vector2(offsetX, offsetY), eased);
                    }
                    if (value.HasImage)
                    {
                        // 是换图，不是 tween：原生直接对活跃帧调
                        // SetSprite。谱面靠每帧发一个事件、每个指定下一张图
                        // 来做逐帧动画。
                        imageFilename = value.ImageFilename;
                    }
                }

                // 无条件写入。
                //
                // 试过为性能跳过「输入看起来没变」的 setter，
                // 两次都不得不回滚。这些 setter 不是纯赋值：
                // SetPosition 会跑 UpdatePosition，它还会刷新视差锚点
                // （parallax.SetNewStartPosition / posCamAtStart）并重新应用旋转、
                // 缩放和颜色。这些依赖相机和父格，所以
                // 「输入没变」并不等于「调用没必要」——相机移动时
                // 一个没变的装饰物仍然需要重新摆位。
                //
                // 此外 SetVisible 还是按类型分支的（renderer 标志、text 标志、
                // activeObject、particle 的 GameObject，或者干脆什么都不做），所以
                // 没有一个缓存值能预测它。
                if (placement != decoration.placementType)
                {
                    decoration.SetPlacementType(placement);
                }

                // 两次剔除，都是关于「别去碰看不见的装饰物」。
                //
                // 给装饰物推 transform 的开销与它的 CPU 成本不成比例：
                // 从下面那块里去掉任意单个 setter 没带来任何变化，而
                // 整个去掉一个装饰物的写入块却有效果。开销来自 Unity 对
                // 被动过对象的延迟处理，每个装饰物只付一次，与改了多少个
                // 属性无关。所以真正的杠杆是「碰了多少个装饰物」，
                // 而不是「往每个里写了多少」。
                //
                // hidden：作者填的 `visible` 标记为隐藏，且当前没有在渲染。
                // currently rendering.
                // transparent：不透明度为 0。光看作者标记不够——在
                //   Not Too Long 谱面上，1253 个装饰物里有 1001 个作者标记为可见，而每帧写入的
                //   646 个里有 490 个不透明度是 0。
                bool skipHiddenWrites = hiddenCulling
                    && !visible
                    && !decoration.GetVisible();
                // transparent 剔除：不透明度为 0 就当它看不见，省掉这组 setter。
                //
                // **但粒子装饰物不适用**：`SetOpacity` 走的是 `ApplyColor`，
                // 对 `scrParticleDecoration` 只是改
                // `main.startColor = colorGradient` —— **已经在飞的粒子不受影响，
                // 系统照样在喷**。也就是说"opacity 0"对粒子根本不是"看不见"。
                //
                // 以前这里一视同仁，于是凡是「`opacity: 0` 和 `SetParticle Start`
                // 一起发」的谱面（本谱 739 / 754 的 `6-7 6-8 6-9 6-16` 正是），
                // 发射器的 position / rotation / scale **整段被跳过**：原生每帧
                // 都在跟着父格移动旋转，求值器却把它们冻在旧位置旧角度上，
                // 而粒子还在照常往外喷 —— 用户看到的就是"727 格之后角度和位置
                // 全都错了"。
                // **遮罩件同样不能按“不透明度 0”剔除。**
                //
                // 遮罩的失效方式只有三种：`visible` 关掉、缩放归零、移出遮罩深度范围。
                // 改不透明度对它没有任何作用 —— 但它的动画恰恰是**缩放**（圆圈从小放大
                // 去覆盖画面）。一旦按 opacity 跳过 transform 写入，遮罩就冻在某一帧的
                // 大小上：该收缩的不收缩、该消失的一直在裁，整段画面跟着错。
                //
                // 对比原生导出（1150 格）：183/244 件遮罩 opacity≈0，
                // 原生 scale=0.00，预览却停在 0.32 / 0.74 / 2.5 —— 就是这个剔除造成的。
                bool skipTransparentWrites = cullTransparent
                    && opacity <= 0.001f
                    && !(decoration is scrParticleDecoration)
                    && masking != MaskingType.Mask;

                // 不透明度无条件写入，放在下面那块之外。
                //
                // 它是 transparent 剔除所依据的值，所以把它留在块里会让
                // 那个剔除变成单向的：一个变透明的装饰物从此被跳过，
                // 再也淡不回来，因为唯一能把它不透明度重新抬起来的
                // 那次写入也会被跳过。
                // **只在值变时才写。** 原来是无条件写，510 件 × 每帧 3 次求值
                // 就是 1500 次 `SetOpacity`，而它内部走 `ApplyColor` → 材质写入。
                // 值没变时写入毫无意义（透明剔除依据的是本帧算出来的 opacity，
                // 不是组件上的旧值，所以去掉无条件写不影响那条剔除）。
                if (!state.HasTransformApplied
                    || Mathf.Abs(opacity - state.AppliedOpacity) > 0.0005f)
                {
                    decoration.SetOpacity(Mathf.Clamp01(opacity));
                    state.AppliedOpacity = opacity;
                }

                // relativeTo = Red/Blue/GreenPlanet 的装饰物跟着球走。
                //
                // 原生 scrDecoration.UpdatePosition 是这么算的：
                //
                //   vector2 = pivotPosVec + ((!controller.paused && followPlanet != null)
                //                            ? (Vector2)followPlanet.transform.position
                //                            : Vector2.zero);
                //
                // 编辑器里 controller.paused 恒为 true，那一项就恒为 0 —— 于是
                // 这类装饰物全塌到 pivotPosVec 上。而它们通常写的是
                // position: [0,0]，等于全部堆在世界原点。表现就是「作者把球缩成 0、
                // 用两个圆形装饰物代替球，工作台里两个圆一个都不在球身上」。
                //
                // 这里把行星当前的世界坐标**预加进传给 SetPosition 的坐标**：原生
                // 那一步再加 0，结果正好是「跟着球」。这一手是刻意的——
                // 另一条路（临时把 controller.paused 置 false 再调）只能让偏移落到
                // parallax 的 startPosition 上，而 scrDecorationManager.LateUpdate
                // 每帧都会拿 pivotPosVec 重算一次（暂停时加 0），两边交替写，
                // 装饰物会在球和原点之间来回弹。预加进 pivotPosVec，则不管谁后跑、
                // 跑几遍，算出来的都是同一个位置。
                Vector2 anchored = position;
                scrPlanet followPlanet = ResolveFollowPlanet(decoration, placement);
                scrController controller = ADOBase.controller;
                bool suppressPlanetFollow = controller != null && controller.paused;
                if (followPlanet != null && suppressPlanetFollow)
                {
                    // 只在原生自己会丢掉这段偏移的时候补（暂停 = 编辑器里的常态）。
                    // 万一工作台是在原生 transport 跑着的时候被打开的，paused 为
                    // false，原生 UpdatePosition 会自己加上行星位置，我们再补就是
                    // 加两遍——那种情况交给原生。
                    anchored += (Vector2)followPlanet.transform.position;
                }

                statModelTicks += System.Diagnostics.Stopwatch.GetTimestamp() - modelStart;
                if (PreviewDiagnostics.Enabled)
                {
                    PreviewDiagnostics.RecordDomain("Deco.Model",
                        (System.Diagnostics.Stopwatch.GetTimestamp() - modelStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                state.FrameMasking = masking;
                state.FrameVisible = visible;
                state.FrameOpacity = opacity;

                // **PACL2 式脏值门控**：这一帧算出来的值和上次写下去的一样，
                // 就整件跳过 —— 后面所有写入（变换 / 可见性 / 遮罩）都不做。
                //
                // 参考 PACL2 的 `ApplyMoveDecorations`：它先算 visible/scale/opacity/color
                // 的变化位，全部没变就返回 `DecorationRefreshStatus.Invisible`，
                // 整段刷新直接跳过。我们原来是逐 setter 去重 —— 每个 setter 都还在跑，
                // 而 Unity 对"被碰过的对象"要付一次延迟处理成本，跟改了几个属性无关。
                if (state.HasTransformApplied
                    && state.MaskingApplied
                    && state.MaskingDepthApplied
                    && (anchored - state.AppliedPosition).sqrMagnitude <= 1e-7f
                    && Mathf.Abs(rotation - state.AppliedRotation) <= 0.0005f
                    && (scale - state.AppliedScale).sqrMagnitude <= 1e-7f
                    && Mathf.Abs(opacity - state.AppliedOpacity) <= 0.0005f
                    && depth == state.AppliedDepth
                    && visible == state.AppliedVisible
                    && Mathf.Abs(color.r - state.AppliedColor.r) <= 0.002f
                    && Mathf.Abs(color.g - state.AppliedColor.g) <= 0.002f
                    && Mathf.Abs(color.b - state.AppliedColor.b) <= 0.002f
                    && Mathf.Abs(color.a - state.AppliedColor.a) <= 0.002f)
                {
                    continue;
                }

                long transformStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!skipHiddenWrites && !skipTransparentWrites)
                {
                    // **只在值变化时写。** 六个 setter 逐个比，未变的跳过：
                    // 静止的装饰物一帧只比较几次浮点，不再触发 Unity 的
                    // 变换/材质脏标记。这是 25ms 降下来的关键。
                    bool positionChanged = !state.HasTransformApplied
                        || !Approximately(state.AppliedPosition, anchored)
                        || !Approximately(state.AppliedPivot, pivot);
                    // 拖播放头期间**别写粒子装饰物的位置**：原生 `SetPosition`
                    // 会清空粒子系统，每帧清一次就永远是"刚发射"，看着像冻住。
                    // 拖动结束后这里会照常补写一次，位置不会丢。
                    bool holdParticle = ParticleWatch.ScrubbingActive
                        && decoration is scrParticleDecoration;
                    if (positionChanged && !holdParticle)
                    {
                        decoration.SetPosition(anchored, pivot);
                        state.AppliedPosition = anchored;
                        state.AppliedPivot = pivot;
                    }
                    state.DisplacedForPlanet = followPlanet != null && suppressPlanetFollow;

                    if (!state.HasTransformApplied
                        || Mathf.Abs(state.AppliedRotation - rotation) > 0.001f)
                    {
                        decoration.SetRotation(rotation);
                        state.AppliedRotation = rotation;
                    }
                    if (!state.HasTransformApplied || !Approximately(state.AppliedScale, scale))
                    {
                        decoration.SetScale(scale);
                        state.AppliedScale = scale;
                    }
                    if (!state.HasTransformApplied || !Approximately(state.AppliedColor, color))
                    {
                        decoration.SetColor(color);
                        state.AppliedColor = color;
                    }
                    bool parallaxChanged = !state.HasTransformApplied
                        || !Approximately(state.AppliedParallax, parallax)
                        || placement != state.AppliedPlacement;
                    if (parallaxChanged)
                    {
                        decoration.SetParallax(parallax, placement);
                        state.AppliedParallax = parallax;
                        state.AppliedPlacement = placement;
                    }
                    if (!state.HasTransformApplied
                        || !Approximately(state.AppliedParallaxOffset, parallaxOffset))
                    {
                        decoration.parallaxOffset = parallaxOffset;
                        if (decoration.parallax != null)
                        {
                            decoration.parallax.SetTrans();
                        }
                        state.AppliedParallaxOffset = parallaxOffset;
                    }
                    state.HasTransformApplied = true;
                }
                if (PreviewDiagnostics.Enabled)
                {
                    PreviewDiagnostics.RecordDomain("Deco.SetTransform",
                        (System.Diagnostics.Stopwatch.GetTimestamp() - transformStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                statTransformTicks += System.Diagnostics.Stopwatch.GetTimestamp() - transformStart;

                if (hasDepthState && (!state.DepthApplied || state.AppliedDepth != depth))
                {
                    decoration.SetDepth(depth);
                    state.DepthApplied = true;
                    state.AppliedDepth = depth;
                }

                if (skipHiddenWrites)
                {
                    continue;
                }

                // 可见性**先不写**，挪到遮罩之后。
                //
                // 原生 SetVisible 是这样推的：
                //   spriteRenderer.enabled = !isMask() & visible;
                //   spriteMask.enabled     =  isMask() & visible;
                // 两行都读 maskingType。所以顺序反了的话，类型刚变的那一帧
                // 用的是**旧类型**：一个刚被设成 Mask 的装饰物会把 sprite 画出来、
                // 同时遮罩不启用——正好是"该被遮的没被遮、还多出一块"。
                // 排到遮罩之后，enabled 就总是按刚写下去的类型算。

                // 遮罩走原生 setter，它才是决定装饰物到底是否渲染的
                // 东西。它的实现是
                //
                //   switch (type) { ... spriteRenderer.maskInteraction = ...; }
                //   maskingType = type;                  // 无条件赋值
                //   if (wasMask == isMask()) return;     // 只跳过缓存刷新
                //
                // 所以只要 type 一变化 setter 就必须跑，不只是那些
                // 翻转了 isMask() 的变化。比较 isMask() 会跳过 "VisibleInsideMask -> None"，
                // 这种两端 isMask() 都是 false，但仍然必须重置
                // maskInteraction。Factory 谱面的 285 格就在关掉十几个背景层所裁剪的
                // 遮罩的同一帧里，对它们做了这件事
                // （curtain4, Mask -> None），于是整个背景消失了：遮罩
                // 没了，但每一层都还停在 VisibleInsideMask。
                //
                // 读组件自己的字段，而不是本域上次写了什么的本地
                // 副本，这让调用保持幂等，并且在原生那一趟或会话重启
                // 背着本域改动了它时能自我纠正。

                long visibleStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long visibleWatch = System.Diagnostics.Stopwatch.GetTimestamp();
                bool wantedVisible = visible && !decoration.forceHide;
                if (!state.HasVisibleApplied || state.AppliedVisible != wantedVisible)
                {
                    decoration.SetVisible(wantedVisible);
                    state.HasVisibleApplied = true;
                    state.AppliedVisible = wantedVisible;
                }
                if (PreviewDiagnostics.Enabled)
                {
                    PreviewDiagnostics.RecordDomain("Deco.SetVisible",
                        (System.Diagnostics.Stopwatch.GetTimestamp() - visibleWatch)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                statVisibleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - visibleStart;

                // **把遮罩目标 tag 写下去 —— 这是遮罩能不能生效的总开关。**
                //
                // AO 的裁剪是按 tag 建的索引：
                //   SpriteAlphaMaskUtils.RefreshMaskCache()
                //     → taggedAlphaMaskCache[item.targetTag] = [遮罩…]
                //   装饰物 UpdateAlphaMaskCache()
                //     → 用自己 tags 去这张表里取，取不到就 = 不裁
                //
                // 而 `targetTag` 是遮罩自己身上那个字段，只有
                //   scrVisualDecoration.SetMaskingTarget(targetTag)
                // 会写它。我们的域从来没调过这个 —— 遮罩的目标 tag 停在加载
                // 时的默认值上，于是谁都匹配不到、遮罩等于不存在。
                if (decoration is scrVisualDecoration maskTargetVisual)
                {
                    string wantedTarget = state.MaskingTarget ?? "NO TAG";
                    if (!string.Equals(state.AppliedMaskingTarget, wantedTarget,
                            StringComparison.Ordinal))
                    {
                        maskTargetVisual.SetMaskingTarget(wantedTarget);
                        state.AppliedMaskingTarget = wantedTarget;
                    }
                }

                // **把 AO 自家的遮罩合成重新驱动起来。**
                //
                // 原版在 `SetMaskingType` 里做了一件我们一直没做的事：
                //   manager.ForceUpdateAlphaMaskCache(deco)
                // 它把装饰物塞进 `decorationsToUpdateAlphaMaskCache`，管理器的
                // LateUpdate 随后 `UpdateAlphaMaskCache(force:true)` 重算命中列表，
                // 再走 `ApplyAlphaMaskToRT` 把遮罩贴图合成进 RT。
                //
                // 不做这一步，`alphaMaskCache` 永远停在第一次构建的（空）结果上，
                // 裁剪等于不存在 —— 参数全对、画面却和原版不一样，差的就是这一环。
                //
                // 每 20 帧补一次：遮挡目标会随关卡加载/重建变化，一次性的写入不够。
                // **带遮罩的装饰物由 AO 的 meshRenderer 画，sprite 必须让位。**
                //
                // 原版这条路径（ApplyAlphaMaskToRT 之后）会做：
                //     if (spriteRenderer.enabled) spriteRenderer.enabled = false;
                //     meshRenderer.material = …合成好的 RT…
                // 我们每帧的 SetVisible 又把它重新打开，于是**未裁剪的整张贴图**
                // 压在裁剪结果上 —— 看起来就是“遮罩完全不生效”。
                // 只要 mesh 那条路在画，就把 sprite 关掉。
                if ((masking == MaskingType.VisibleInsideMask
                        || masking == MaskingType.VisibleOutsideMask)
                    && decoration is scrVisualDecoration maskedVisual
                    && maskedVisual.meshRendererObj != null
                    && maskedVisual.meshRendererObj.activeSelf
                    && maskedVisual.spriteRenderer != null
                    && maskedVisual.spriteRenderer.enabled)
                {
                    maskedVisual.spriteRenderer.enabled = false;
                }

                // **只在遮罩状态真的变了才通知管理器重算缓存。**
                //
                // 原来这里是「每 20 帧对所有带遮罩的装饰物补一次」：本谱 541 件装饰、
                // 其中 500 多件带遮罩，于是每 20 帧就有几百次
                // `ForceUpdateAlphaMaskCache` + 一次 `doRefreshMaskCache`，
                // 而 manager 的 LateUpdate 会对每一项做 HashSet 重建与 OBB 遍历 ——
                // 直接表现为疯狂掉帧。
                //
                // 缓存的内容是「这件装饰物可能被哪些遮罩裁」，只随 tag / 遮罩类型
                // 变化，不随遮罩每帧移动而变（OBB 在渲染时另算）。所以只在
                // masking / maskingTarget 改变（含首次）时刷新一次即可。
                state.Settled = !anyActive;
                state.SettledAt = frame.Time;
                state.NextEventStart = nextEventStart;

                // 每帧重试直到成功：sprite 可能还在加载。
                if (!string.Equals(state.AppliedImage, imageFilename, StringComparison.Ordinal))
                {
                    long imgStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (ApplyImage(decoration, imageFilename))
                    {
                        state.AppliedImage = imageFilename;
                    }
                    statImageTicks += System.Diagnostics.Stopwatch.GetTimestamp() - imgStart;
                }

                // 探针：确认行星相对的装饰物真的贴在球上（位置 ≈ 行星位置）。
                // 挂在 VerboseDiagnostics 后面，默认一条都不输出。
                if (followPlanet != null
                    && Main.Settings != null
                    && Main.Settings.VerboseDiagnostics
                    && Time.frameCount - lastPlanetFollowProbeFrame >= 60)
                {
                    lastPlanetFollowProbeFrame = Time.frameCount;
                    Main.Logger.Log("[DecorationCore/PlanetFollow] " + decoration.name
                        + " placement=" + placement
                        + " planet=" + (followPlanet.name ?? "?")
                        + " planetPos=" + ((Vector2)followPlanet.transform.position).ToString("0.00")
                        + " decoPos=" + ((Vector2)decoration.transform.position).ToString("0.00")
                        + " anchored=" + anchored.ToString("0.00")
                        + " paused=" + suppressPlanetFollow
                        + " opacity=" + opacity.ToString("0.00")
                        + " visible=" + visible + ".");
                }
            }

            ApplyMaskPass(frame);
        }

        private static bool ApplyImage(scrDecoration decoration, string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                // 空文件名不是"没什么可设的"，而是作者在**清掉这张图**，
                // 好让装饰物消失——574 格那种"该消失的没消失"就是这么来的。
                //
                // 原生：SetSprite(customSprites.GetValueOrDefault(name ?? "", null))。
                // 名字为空时取到的是 null，SetSprite(null) 把 spriteRenderer
                // 和 spriteMask 一起清空。早先这里直接 return false，
                // 于是那条事件被当成空操作，装饰物保持上一张图。
                return ClearSprite(decoration);
            }

            scrDecorationManager manager = scrDecorationManager.instance;
            TextureManager holder = manager == null ? null : manager.imageHolder;
            if (holder == null || holder.customSprites == null)
            {
                return false;
            }

            TextureManager.CustomSprite sprite;
            holder.customSprites.TryGetValue(filename, out sprite);
            if (sprite == null)
            {
                return false;
            }

            scrParticleDecoration particle = decoration as scrParticleDecoration;
            if (particle != null)
            {
                particle.SetSprite(sprite);
                return true;
            }

            scrVisualDecoration visual = decoration as scrVisualDecoration;
            if (visual != null)
            {
                // 签名来自发行版的程序集：SetSprite(CustomSprite,
                // bool)。反编译的源码树用的是旧的两参数形式，带一个
                // 已经不存在的 ImageOptions 枚举。
                visual.SetSprite(sprite);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 把装饰物身上的图清掉。走的是和 <see cref="ApplyImage"/> 同一条
        /// 原生 setter——对 scrVisualDecoration 它会同时清掉 spriteRenderer
        /// 和 spriteMask，这一点和原生传 null 时的行为一致。
        /// </summary>
        private static bool ClearSprite(scrDecoration decoration)
        {
            scrParticleDecoration particle = decoration as scrParticleDecoration;
            if (particle != null)
            {
                particle.SetSprite((TextureManager.CustomSprite)null);
                return true;
            }

            scrVisualDecoration visual = decoration as scrVisualDecoration;
            if (visual != null)
            {
                visual.SetSprite((TextureManager.CustomSprite)null);
                return true;
            }

            return false;
        }

        public void EndSession()
        {
            ApplyLegacyMaskPipeline(false);
            // 行星相对的装饰物被预加过行星坐标（见 Evaluate），退出预览得把
            // 干净的 pivotPosVec 还给原生：原生播放时 paused=false，它自己还会
            // 再加一次行星位置，旧偏移留着就是加两遍——退出工作台后这两个
            // 装饰物会整体挪开一整个球的距离。
            for (int i = 0; i < states.Count; i++)
            {
                DecorationState state = states[i];
                if (state == null || !state.DisplacedForPlanet || state.Decoration == null)
                {
                    continue;
                }

                state.Decoration.SetPosition(state.Position, state.Pivot);
                state.DisplacedForPlanet = false;
            }

            states.Clear();
        }

        private static Dictionary<string, List<MoveEvent>> BuildTaggedEvents(PreviewTimelineContext context)
        {
            Dictionary<string, List<MoveEvent>> result = new Dictionary<string, List<MoveEvent>>();
            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.MoveDecorations);
            for (int i = 0; i < source.Count; i++)
            {
                PreviewEventSnapshot levelEvent = source[i];
                MoveEvent value = new MoveEvent
                {
                    Order = i,
                    Start = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    HasVisible = context.IsPropertyEnabled(levelEvent, "visible"),
                    Visible = context.GetValue(levelEvent, "visible", true),
                    HasMasking = context.HasPropertyKey(levelEvent, "maskingType"),
                    Masking = context.GetValue(levelEvent, "maskingType", MaskingType.None),
                    HasUseMaskingDepth = context.IsPropertyEnabled(levelEvent, "useMaskingDepth"),
                    UseMaskingDepth = context.GetValue(levelEvent, "useMaskingDepth", false),
                    HasMaskingFrontDepth = context.IsPropertyEnabled(levelEvent, "maskingFrontDepth"),
                    MaskingFrontDepth = context.GetValue(levelEvent, "maskingFrontDepth", -1),
                    HasMaskingBackDepth = context.IsPropertyEnabled(levelEvent, "maskingBackDepth"),
                    MaskingBackDepth = context.GetValue(levelEvent, "maskingBackDepth", -1),
                    HasPlacement = context.IsPropertyEnabled(levelEvent, "relativeTo"),
                    Placement = context.GetValue(levelEvent, "relativeTo", DecPlacementType.Tile),
                    HasPosition = context.IsPropertyEnabled(levelEvent, "positionOffset"),
                    Position = context.GetValue(levelEvent, "positionOffset", Vector2.zero),
                    HasPivot = context.IsPropertyEnabled(levelEvent, "pivotOffset"),
                    Pivot = context.GetValue(levelEvent, "pivotOffset", Vector2.zero),
                    HasRotation = context.IsPropertyEnabled(levelEvent, "rotationOffset"),
                    Rotation = context.GetValue(levelEvent, "rotationOffset", 0f),
                    HasScale = context.IsPropertyEnabled(levelEvent, "scale"),
                    Scale = context.GetValue(levelEvent, "scale", new Vector2(100f, 100f)),
                    HasColor = context.IsPropertyEnabled(levelEvent, "color"),
                    Color = ReadColor(levelEvent, "color", Color.white),
                    HasOpacity = context.IsPropertyEnabled(levelEvent, "opacity"),
                    Opacity = context.GetValue(levelEvent, "opacity", 100f),
                    HasDepth = context.IsPropertyEnabled(levelEvent, "depth"),
                    Depth = context.GetValue(levelEvent, "depth", 0),
                    HasParallax = context.IsPropertyEnabled(levelEvent, "parallax"),
                    Parallax = context.GetValue(levelEvent, "parallax", Vector2.zero),
                    HasParallaxOffset = context.IsPropertyEnabled(levelEvent, "parallaxOffset"),
                    ParallaxOffset = context.GetValue(levelEvent, "parallaxOffset", Vector2.zero),
                    HasImage = context.IsPropertyEnabled(levelEvent, "decorationImage"),
                    ImageFilename = context.GetValue(levelEvent, "decorationImage", string.Empty)
                };

                string tag = SafeString(levelEvent, "tag");
                string[] tags = string.IsNullOrEmpty(tag)
                    ? new[] { "NO TAG" }
                    : tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int tagIndex = 0; tagIndex < tags.Length; tagIndex++)
                {
                    List<MoveEvent> bucket;
                    if (!result.TryGetValue(tags[tagIndex], out bucket))
                    {
                        bucket = new List<MoveEvent>();
                        result.Add(tags[tagIndex], bucket);
                    }
                    bucket.Add(value);
                }
            }

            return result;
        }

        /// <summary>
        /// 切换原生遮罩管线（V15 ↔ 老版 SpriteMask）。
        ///
        /// 只在进入/退出预览时各调一次。老管线不支持 maskingFrontDepth /
        /// maskingBackDepth 那套自定义深度范围，但不逐帧合成 RT。
        /// </summary>
        /// <summary>
        /// 独立遮罩通道：只遍历真正带遮罩的那几十件，不再放在 500+ 件的主循环里。
        ///
        /// 之前把遮罩判断塞在主循环里，即使一件都没应用，这段也能吃掉 150~280ms
        /// —— 主循环里每件装饰物都要过一遍，代价与件数成正比，与是否真的需要无关。
        /// 拆出来之后，判断次数从 510 降到 53，且带预算。
        /// </summary>
        private void ApplyMaskPass(PreviewEvaluationFrame frame)
        {
            if (Main.Settings == null || Main.Settings.SimplifyDecorationMasks)
            {
                return;
            }
            int budget = 8;
            for (int i = 0; i < maskCandidateStates.Count && budget > 0; i++)
            {
                DecorationState state = maskCandidateStates[i];
                scrVisualDecoration visual = state.Decoration as scrVisualDecoration;
                if (visual == null)
                {
                    continue;
                }
                MaskingType wanted = state.FrameMasking;
                if (!state.FrameVisible || state.FrameOpacity <= 0.004f)
                {
                    wanted = MaskingType.None;
                }
                // `VisibleOutsideMask` 现在也照常应用：遮罩处理已经移出主循环，
                // 每帧最多写 8 件，先看帧率能不能扛住。
                if (state.MaskingApplied && state.AppliedMasking == wanted)
                {
                    continue;
                }
                WriteMaskingTypeFast(visual, wanted);
                state.AppliedMasking = wanted;
                state.MaskingApplied = true;
                budget--;
            }
        }

        private static void ApplyLegacyMaskPipeline(bool legacy)
        {
            if (Main.Settings == null || !Main.Settings.UseLegacySpriteMask)
            {
                return;
            }
            scrController controller = ADOBase.controller;
            if (controller == null)
            {
                return;
            }
            if (legacy)
            {
                if (!legacyMaskApplied)
                {
                    legacyMaskOriginal = controller.disableV15Features;
                    legacyMaskApplied = true;
                }
                controller.disableV15Features = true;
                return;
            }
            if (legacyMaskApplied)
            {
                controller.disableV15Features = legacyMaskOriginal;
                legacyMaskApplied = false;
            }
        }

        private static bool legacyMaskApplied;
        private static bool legacyMaskOriginal;

        private static void WriteMaskingTypeFast(scrVisualDecoration decoration, MaskingType type)
        {
            if (decoration == null)
            {
                return;
            }
            SpriteRenderer renderer = decoration.spriteRenderer;
            if (renderer != null)
            {
                switch (type)
                {
                    case MaskingType.VisibleInsideMask:
                        renderer.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                        break;
                    case MaskingType.VisibleOutsideMask:
                        renderer.maskInteraction = SpriteMaskInteraction.VisibleOutsideMask;
                        break;
                    default:
                        renderer.maskInteraction = SpriteMaskInteraction.None;
                        break;
                }
            }
            ReadMaskingType(decoration);   // 保证字段缓存已初始化
            if (maskingTypeField != null)
            {
                maskingTypeField.SetValue(decoration, type);
            }
        }

        private static MaskingType? ReadMaskingType(scrVisualDecoration decoration)
        {
            // 粒子装饰物不是 scrVisualDecoration，而播种路径在调到这里之前
            // 会做强制转换，所以 null 是正常输入。对实例字段调
            // FieldInfo.GetValue(null) 会抛 TargetException 而不是返回 null，那会
            // 中断整个 Analyze，让预览一个装饰物都没有。
            if (decoration == null)
            {
                return null;
            }

            if (maskingTypeField == null)
            {
                maskingTypeField = typeof(scrVisualDecoration).GetField(
                    "maskingType",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (maskingTypeField == null)
                {
                    Main.Logger.Warning(
                        "[Decorations] scrVisualDecoration.maskingType not found; falling back"
                        + " to isMask(), which cannot see VisibleInside/OusideMask.");
                    return null;
                }
            }

            try
            {
                object raw = maskingTypeField.GetValue(decoration);
                return raw is MaskingType ? (MaskingType?)raw : null;
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[Decorations] could not read maskingType: " + exception.Message);
                maskingTypeField = null;
                return null;
            }
        }

        /// <summary>
        /// 读作者/事件里的布尔值。谱面用 `"Enabled" / "Disabled"` 字符串表示开关，
        /// 直接按 bool 强转会失败并静默取默认值，所以统一走这里。
        /// </summary>
        private static bool BoolOf(object raw, bool fallback)
        {
            if (raw == null)
            {
                return fallback;
            }
            if (raw is bool)
            {
                return (bool)raw;
            }
            string text = raw.ToString();
            if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            bool parsed;
            if (bool.TryParse(text, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static bool AuthorBool(ADOFAI.LevelEvent levelEvent, string key, bool fallback)
        {
            if (levelEvent == null)
            {
                return fallback;
            }
            object raw;
            return levelEvent.TryGet<object>(key, out raw) ? BoolOf(raw, fallback) : fallback;
        }

        /// <summary>
        /// 这个装饰物当前跟着哪颗行星；不是行星相对的装饰物返回 null。
        ///
        /// 原生在 `SetPlacementType` 里把控制器上那颗球记进 `followPlanet`，
        /// 并且在同一个方法的开头就把它清空，所以 `followPlanet` 永远和
        /// `placementType` 一致——它比我们自己按枚举去猜更可靠。兜底那条
        /// 分支只照顾"刚建出来、还没跑过 SetPlacementType"的装饰物。
        /// </summary>
        private static scrPlanet ResolveFollowPlanet(scrDecoration decoration, DecPlacementType placement)
        {
            if (decoration == null)
            {
                return null;
            }

            if (decoration.followPlanet != null)
            {
                return decoration.followPlanet;
            }

            scrController controller = ADOBase.controller;
            if (controller == null)
            {
                return null;
            }

            switch (placement)
            {
                case DecPlacementType.RedPlanet:
                    return controller.planetRed;
                case DecPlacementType.BluePlanet:
                    return controller.planetBlue;
                case DecPlacementType.GreenPlanet:
                    return controller.planetGreen;
                default:
                    return null;
            }
        }

        private static MaskingType? AuthorMasking(ADOFAI.LevelEvent levelEvent)
        {
            if (levelEvent == null)
            {
                return null;
            }
            object raw;
            if (!levelEvent.TryGet<object>("maskingType", out raw) || raw == null)
            {
                return null;
            }
            if (raw is MaskingType)
            {
                return (MaskingType)raw;
            }
            MaskingType parsed;
            if (System.Enum.TryParse(raw.ToString(), out parsed))
            {
                return parsed;
            }
            return null;
        }

        /// <summary>
        /// 读作者写的数值（`LevelEvent.data` 里可能是 int / float / double，
        /// 直接 `Get&lt;float&gt;` 会 InvalidCast 掉成默认值，所以走 Convert）。
        /// </summary>
        /// <summary>
        /// 读作者写的整数。**不能用 `LevelEvent.GetInt`** —— 那是硬转换
        /// （`(T)value`），谱面里同一个字段在不同作者手里可能是 int / float / 字符串，
        /// 类型对不上就抛 `InvalidCastException`，而这个异常发生在 `Analyze` 里，
        /// 会把**整个装饰物域**废掉：一件装饰物都不更新，画面上一片空白。
        /// 本谱（星相运轨）就是这么被打穿的。
        /// </summary>
        private static int AuthorInt(ADOFAI.LevelEvent levelEvent, string key, int fallback)
        {
            if (levelEvent == null)
            {
                return fallback;
            }
            object value;
            if (!levelEvent.TryGet<object>(key, out value) || value == null)
            {
                return fallback;
            }
            try
            {
                return System.Convert.ToInt32(value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// 读作者写的字符串。理由同 <see cref="AuthorInt"/>：`GetString` 是硬转换，
        /// 字段类型不符会抛异常并连带废掉整个域。
        /// </summary>
        private static string AuthorString(ADOFAI.LevelEvent levelEvent, string key, string fallback)
        {
            if (levelEvent == null)
            {
                return fallback;
            }
            object value;
            if (!levelEvent.TryGet<object>(key, out value) || value == null)
            {
                return fallback;
            }
            string text = value as string;
            return text ?? value.ToString();
        }

        /// <summary>
        /// 读作者写的颜色。**不用 `Get<Color>`**：同一个 `color` 字段在不同谱面里
        /// 可能是 `"ffffff"` 这样的十六进制字符串，也可能是对象；硬转换遇到字符串
        /// 就抛 `InvalidCastException`，整个装饰物域在 `Analyze` 阶段就废掉。
        /// 本谱（星相运轨）510 件装饰物写的是字符串。
        /// </summary>
        private static Color AuthorColor(ADOFAI.LevelEvent levelEvent, string key, Color fallback)
        {
            if (levelEvent == null)
            {
                return fallback;
            }
            object raw;
            if (!levelEvent.TryGet<object>(key, out raw) || raw == null)
            {
                return fallback;
            }
            if (raw is Color)
            {
                return (Color)raw;
            }
            string text = raw as string;
            if (!string.IsNullOrEmpty(text))
            {
                string hex = text[0] == '#' ? text : "#" + text;
                Color parsed;
                if (ColorUtility.TryParseHtmlString(hex, out parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        /// <summary>读事件上的字符串字段，读不到就返回空串（不抛异常）。</summary>
        private static string SafeString(PreviewEventSnapshot levelEvent, string key)
        {
            if (levelEvent == null)
            {
                return string.Empty;
            }
            try
            {
                return levelEvent.GetString(key) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>把「遮罩类型 + 目标 tag」压成一个整数签名，用于判断是否需要重算缓存。</summary>
        /// <summary>向量近似相等（写入去重，容差 0.001 小于一个屏幕像素）。</summary>
        private static bool Approximately(Vector2 a, Vector2 b)
        {
            return Mathf.Abs(a.x - b.x) <= 0.001f && Mathf.Abs(a.y - b.y) <= 0.001f;
        }

        private static bool Approximately(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) <= 0.001f && Mathf.Abs(a.g - b.g) <= 0.001f
                && Mathf.Abs(a.b - b.b) <= 0.001f && Mathf.Abs(a.a - b.a) <= 0.001f;
        }

        /// <summary>事件表里第一条 Start &gt; time 的下标（表已按 Start 升序）。</summary>
        private static int UpperBoundByStart(System.Collections.Generic.List<MoveEvent> events, float time)
        {
            int low = 0;
            int high = events.Count;
            while (low < high)
            {
                int mid = (low + high) >> 1;
                if ((float)events[mid].Start <= time)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            return low;
        }

        private static float AuthorFloat(ADOFAI.LevelEvent levelEvent, string key, float fallback)
        {
            if (levelEvent == null)
            {
                return fallback;
            }
            object value;
            if (!levelEvent.TryGet<object>(key, out value) || value == null)
            {
                return fallback;
            }
            try
            {
                return System.Convert.ToSingle(value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// 这个属性在作者那边是不是「启用」的。缺键按启用算 —— 和原生的
        /// `TryGetAndSet(..., onlyIfEnabled: true)` 同一套语义。
        /// </summary>
        private static bool AuthorEnabled(ADOFAI.LevelEvent levelEvent, string key)
        {
            bool value;
            return levelEvent == null || levelEvent.disabled == null
                || !levelEvent.disabled.TryGetValue(key, out value) || !value;
        }

        /// <summary>
        /// 装饰物该从哪儿起步。
        ///
        /// 「位置」被勾掉时用**落点**（`startPos` 减掉作者写的偏移）：`startPos`
        /// 是 `SetPlacementType` 里算出来的 `落点 + 位置×格宽`，减回去就是落点。
        /// 相机锚定的那两种放置方式，偏移不是按格宽算的，直接减。
        ///
        /// 没勾掉时仍读组件当前值 —— 那是 `Setup` 刚摆好、也是 MoveDecorations
        /// 一路改过来的位置，和以前完全一致。
        /// </summary>
        private static Vector2 AuthorPosition(scrDecoration decoration)
        {
            if (decoration == null)
            {
                return Vector2.zero;
            }
            // **起点一律按作者数据算，不读组件当前值。**
            //
            // `pivotPosVec` 是「上一次求值/上一次播放留下的」位置：我们的求值器
            // 自己每帧会写它，勾掉「位置」时又会把它写成落点。拿它当下一轮的
            // 起点，就成了「勾回来还是停在落点上」——必须等原生再跑一次 Setup
            // 才能纠正（用户看到的就是「动一下播放头才瞬间归位」）。
            //
            // `startPos` 是 `SetPlacementType` 按作者数据算出来的初始位置
            // （落点 + 位置偏移），只有 Setup 会改它，我们这套求值不碰。
            ADOFAI.LevelEvent levelEvent = decoration.sourceLevelEvent;
            if (AuthorEnabled(levelEvent, "position"))
            {
                return decoration.startPos;
            }
            Vector2 offset = ReadListVector2(levelEvent, "position", Vector2.zero);
            bool camera = decoration.placementType == DecPlacementType.Camera
                || decoration.placementType == DecPlacementType.CameraAspect;
            float tile = ADOBase.controller == null ? 1f : ADOBase.controller.tileSize;
            return camera ? decoration.startPos - offset : decoration.startPos - offset * tile;
        }

        /// <summary>按 `[x, y]` 形态读一个二维值（谱面里存的是列表）。</summary>
        private static Vector2 ReadListVector2(ADOFAI.LevelEvent levelEvent, string key,
            Vector2 fallback)
        {
            object raw;
            if (levelEvent == null || !levelEvent.TryGet<object>(key, out raw) || raw == null)
            {
                return fallback;
            }
            System.Collections.IList list = raw as System.Collections.IList;
            if (list == null || list.Count < 2)
            {
                return fallback;
            }
            try
            {
                return new Vector2(
                    System.Convert.ToSingle(list[0], System.Globalization.CultureInfo.InvariantCulture),
                    System.Convert.ToSingle(list[1], System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// 读作者写的 scale（`[x, y]`）。读不出来时退回组件当前值 —— 宁可这一次
        /// 不纯净，也不要让「作者写了 300×300」的装饰物在第一个 scale 事件之前
        /// 变成 100×100。
        /// </summary>
        private static Vector2 AuthorScale(ADOFAI.LevelEvent levelEvent, Vector2 fallbackRuntime)
        {
            Vector2 pair;
            if (levelEvent != null && levelEvent.TryGet<Vector2>("scale", out pair))
            {
                return pair;
            }
            object raw;
            if (levelEvent != null && levelEvent.TryGet<object>("scale", out raw) && raw != null)
            {
                System.Collections.IList list = raw as System.Collections.IList;
                if (list != null && list.Count >= 2)
                {
                    try
                    {
                        return new Vector2(
                            System.Convert.ToSingle(list[0], System.Globalization.CultureInfo.InvariantCulture),
                            System.Convert.ToSingle(list[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    catch
                    {
                        return fallbackRuntime * 100f;
                    }
                }
            }
            return fallbackRuntime * 100f;
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
