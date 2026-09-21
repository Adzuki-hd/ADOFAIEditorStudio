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
            internal bool AppliedUseMaskingDepth;
            internal int AppliedMaskingFrontDepth;
            internal int AppliedMaskingBackDepth;

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

        public string Name
        {
            get { return "DecorationCore"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            states.Clear();
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || manager.allDecorations == null)
            {
                return;
            }

            Dictionary<string, List<MoveEvent>> taggedEvents = BuildTaggedEvents(context);
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
                    Position = decoration.pivotPosVec,
                    Baseline = decoration.startPos,
                    Pivot = decoration.pivotOffsetVec,
                    Rotation = decoration.rotAngle,
                    Scale = decoration.scaleVec,
                    Color = decoration.color,
                    Opacity = decoration.opacity,
                    Depth = 0,
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
                    HasDepthState = false,
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
                    Masking = ReadMaskingType(decoration as scrVisualDecoration)
                        ?? (decoration is scrVisualDecoration maskSource && maskSource.isMask()
                            ? MaskingType.Mask
                            : MaskingType.None),
                    // 从作者写的值播种，跟 Masking 从组件播种的方式一致。
                    // SetMaskingDepth 从这些值重新推导 SpriteMask 的排序范围，
                    // 所以起始值错了就会裁剪错的那一段，直到某个事件
                    // 碰巧又把它重申一遍。
                    UseMaskingDepth = decoration.sourceLevelEvent.GetBool("useMaskingDepth"),
                    MaskingFrontDepth = decoration.sourceLevelEvent.GetInt("maskingFrontDepth"),
                    MaskingBackDepth = decoration.sourceLevelEvent.GetInt("maskingBackDepth")
                };

                // 作者写的图是每次求值重放的起点，所以
                // 向后拖动和向前拖动落在同一个 sprite 上。
                state.DefaultImage = state.AppliedImage = decoration.sourceLevelEvent.GetString("decorationImage");

                string tag = decoration.sourceLevelEvent.GetString("tag");
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
                Vector2 parallaxOffset = state.ParallaxOffset;
                bool visible = state.Visible;
                bool hasDepthState = state.HasDepthState;
                MaskingType masking = state.Masking;
                bool useMaskingDepth = state.UseMaskingDepth;
                int maskingFrontDepth = state.MaskingFrontDepth;
                int maskingBackDepth = state.MaskingBackDepth;
                string imageFilename = state.DefaultImage;

                for (int eventIndex = 0; eventIndex < state.Events.Count; eventIndex++)
                {
                    MoveEvent value = state.Events[eventIndex];
                    if (value.Start > frame.Time)
                    {
                        break;
                    }

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
                    if (value.HasDepth && eased >= 1f)
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
                bool skipTransparentWrites = cullTransparent && opacity <= 0.001f;

                // 不透明度无条件写入，放在下面那块之外。
                //
                // 它是 transparent 剔除所依据的值，所以把它留在块里会让
                // 那个剔除变成单向的：一个变透明的装饰物从此被跳过，
                // 再也淡不回来，因为唯一能把它不透明度重新抬起来的
                // 那次写入也会被跳过。
                decoration.SetOpacity(Mathf.Clamp01(opacity));

                if (!skipHiddenWrites && !skipTransparentWrites)
                {
                    decoration.SetPosition(position, pivot);
                    decoration.SetRotation(rotation);
                    decoration.SetScale(scale);
                    decoration.SetColor(color);
                    decoration.SetParallax(parallax, placement);
                    decoration.parallaxOffset = parallaxOffset;
                    if (decoration.parallax != null)
                    {
                        decoration.parallax.SetTrans();
                    }
                }

                if (hasDepthState)
                {
                    decoration.SetDepth(depth);
                }

                if (skipHiddenWrites)
                {
                    continue;
                }

                decoration.SetVisible(visible && !decoration.forceHide);

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
                if (decoration is scrVisualDecoration visualForMask)
                {
                    MaskingType? current = ReadMaskingType(visualForMask);
                    bool needsUpdate = current.HasValue
                        ? current.Value != masking
                        : visualForMask.isMask() != (masking == MaskingType.Mask);
                    if (needsUpdate)
                    {
                        visualForMask.SetMaskingType(masking);
                    }

                    // 遮罩深度决定 SpriteMask 的排序范围，所以是它让一个
                    // 与之配对的 VisibleOutsideMask 装饰物在周围的 Mask 装饰物
                    // 换段时消失。原生按顺序跑这两个重载：
                    //
                    //   if (useMaskingDepthUsed) dec.SetMaskingDepth(targetUseMaskingDepth);
                    //   if (frontUsed || backUsed)
                    //       dec.SetMaskingDepth(frontUsed ? front : (int?)null,
                    //                           backUsed ? back : (int?)null);
                    //
                    // SetMaskingDepth 会改写排序层 id 和排序号，所以只在
                    // 值真正变化时才跑——不像 SetMaskingType，原生对后者
                    // 也是每次开始都调，而本域也用同样的方式做了守卫。
                    bool depthChanged = !state.MaskingDepthApplied
                        || state.AppliedUseMaskingDepth != useMaskingDepth
                        || state.AppliedMaskingFrontDepth != maskingFrontDepth
                        || state.AppliedMaskingBackDepth != maskingBackDepth;
                    if (depthChanged)
                    {
                        visualForMask.SetMaskingDepth(useMaskingDepth);
                        visualForMask.SetMaskingDepth(maskingFrontDepth, maskingBackDepth);
                        state.MaskingDepthApplied = true;
                        state.AppliedUseMaskingDepth = useMaskingDepth;
                        state.AppliedMaskingFrontDepth = maskingFrontDepth;
                        state.AppliedMaskingBackDepth = maskingBackDepth;
                    }
                }

                // 每帧重试直到成功：sprite 可能还在加载。
                if (!string.Equals(state.AppliedImage, imageFilename, StringComparison.Ordinal))
                {
                    if (ApplyImage(decoration, imageFilename))
                    {
                        state.AppliedImage = imageFilename;
                    }
                }
            }
        }

        /// <summary>
        /// 装饰物当前的遮罩类型，直接从组件上读。
        ///
        /// `maskingType` 是私有的，没有 getter，而公开的表面（isMask）把
        /// 类型折叠成一个 bit，分不出 VisibleInsideMask 和 None。反射
        /// 结果做了缓存，因为这段每个装饰物每帧都要跑一次。
        /// </summary>
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

        private static FieldInfo maskingTypeField;

        // 每次 Evaluate 读一次，而不是每个装饰物读一次。
        private static bool hiddenCulling;
        private static bool cullTransparent;

        /// <summary>
        /// 换掉装饰物的 sprite，对齐 ffxMoveDecorationsPlus：粒子
        /// 装饰物设材质纹理，可视装饰物设 sprite。名字在 image holder 里
        /// 没有条目就清空 sprite，这正是原生对缺 key
        /// 的处理。
        /// </summary>
        /// <summary>
        /// 换 sprite 并报告到底换成了没有。
        ///
        /// 返回值很要紧：还没加载完的 sprite 在 customSprites 里就是
        /// 不存在的，而调用方只在成功时才记「已应用」。不管成功与否都
        /// 标记已应用，会让加载慢的图永远不被重试，装饰物
        /// 就只剩颜色、什么都显示不出来。在 Factory 谱面上 p4.1.png 是一张 9 MB、
        /// 5332x3000 的纹理，会话打开那一帧还没就绪，
        /// 之后整个预览里它都渲染成一块灰板。
        /// </summary>
        private static bool ApplyImage(scrDecoration decoration, string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return false;
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

        public void EndSession()
        {            states.Clear();
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

                string tag = levelEvent.GetString("tag");
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
