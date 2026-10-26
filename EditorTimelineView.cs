using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAI;
using ADOFAIEditorStudio.Preview.Domains;
using ADOFAIEditorStudio.Ui;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    internal sealed class EditorTimelineView : MonoBehaviour, IScrollHandler, IPointerDownHandler, IPointerUpHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private enum TimelineLane
        {
            Camera,
            Visual,
            Decoration,
            Audio,
            Gameplay,
            Other,
            /// <summary>
            /// 轨道类事件。**追加在最后**，不插在中间：LaneBaseHues、
            /// LaneIconEventTypes、laneVisible 这些表都是按下标对齐的，
            /// 插在中间会把后面每一条的下标都推走。
            /// </summary>
            Track
        }

        private sealed class TimelineItem
        {
            internal LevelEvent Event;
            internal TimelineLane Lane;
            internal float StartTime;
            internal float Duration;
            internal int FloorId;
            internal int SourceOrder;

            /// <summary>
            /// 事件身份：**类型 : 格 : 起点时间 : 参数哈希**（见 `EnsureEventKeys`）。
            /// 工程文件就是按它存行号的。
            ///
            /// 要等全部事件到齐、时间也定下来才算得出，所以在 `EnsureEventKeys`
            /// 里补算，`AddTimelineItem` 那会儿还轮不上。**算过就不再变** ——
            /// `TimelineItem` 只在重新分析谱面时整个重建（换一批新对象），
            /// 所以缓存安全。
            /// </summary>
            internal string Key;

            /// <summary>
            /// 下面三个是「算一次就够」的派生值。
            ///
            /// 它们只取决于事件自己的参数，而 `TimelineItem` 只在重新分析谱面时
            /// 重建（同 `Key` 那段的理由），所以在这里存一份，别放进每帧的
            /// `CollectBlocks` 里现算 —— 那三个函数每帧每块都要查一遍字典
            /// （`duration` 属性 / 图标表 / `ease`），4237 个块就是每帧两万多次
            /// 字符串哈希，实测占了「构建块视图」2.6ms 的大头。
            /// </summary>
            internal bool Resizable;
            internal Sprite Icon;
            internal Ease Ease;

            internal bool IsDecoration;
            internal bool Active;
            internal string Name;
            internal Color BlockColor;
            // 紧挨着块存着，而不是绘制时从块推导：两者在调色板上各占一端，
            // 块是浅的、标签是深的，靠一方推导另一方，
            // 会随调色板整体反转而失效。
            internal Color LabelColor;
            internal int DisplayRow;
        }

        private sealed class TimelineMarker
        {
            internal GameObject GameObject;
            internal RectTransform Rect;
            internal Image Background;
            internal EaseCurveImage Curve;
            /// <summary>上一次绑的曲线数组。同一个数组不重复 SetCurve。</summary>
            internal float[] CurveValues;
            internal Image Icon;
            internal TextMeshProUGUI Label;
            // 精确选中时点亮的白色描边。常驻对象，只切换显隐，
            // 尺寸由 BindMarker 按当前行高写——ALT+滚轮改行高后
            // 下一帧就跟上。
            internal Image Outline;
            // 拖动交互。挂在块上，随池复用，所以创建一次就够。
            internal TimelineBlockDrag Drag;
            internal TimelineItem Item;
            /// <summary>当前绑定的块 ID（-1 = 没绑）。回收时用它摘掉旧映射。</summary>
            internal int BoundBlockId = -1;
        }

        private struct RowInterval
        {
            internal float StartTime;
            internal float EndTime;
            internal TimelineItem Item;
        }

        /// <summary>
        /// 事件块的直接操作：左键拖块身改开始时间，瞄准左右边缘拖拽改时长。
        ///
        /// 做成 EditorTimelineView 的嵌套类，是为了能直接用
        /// TimelineMarker / TimelineItem —— 两者都是这个类的私有嵌套类型，
        /// 放到外面就得把可见性放宽，为了一个交互细节不值得。
        ///
        /// **只实现拖动相关的接口，不实现 IPointerDownHandler。**
        /// marker 身上已经有一个 Button，它的 OnPointerDown 会 eventData.Use()。
        /// Unity 派发 pointerDown 时，即使父级有处理器也会照常执行
        /// （ExecuteHierarchy 会在第一个命中对象上把该接口的所有组件跑完），
        /// 所以加一个自己的 IPointerDownHandler 并不能拦住
        /// EditorTimelineView.OnPointerDown，只会多一处要协调的状态。
        /// 而 IBeginDragHandler 是沿父链查找的：marker 自己实现了，
        /// 视图的 OnBeginDrag 就不会被调用，这正是我们要的切入点。
        /// 代价是按下点落在块上时视图什么也收不到，所以中键要由这里
        /// 显式转交回去（见 OnBeginDrag 开头那段）。
        /// </summary>
        private sealed class TimelineBlockDrag : MonoBehaviour,
            IPointerMoveHandler, IPointerExitHandler,
            IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private enum Mode
            {
                None,
                Move,
                ResizeLeft,
                ResizeRight
            }

            /// <summary>边缘多宽算「贴边」，够宽才好瞄准。</summary>
            private const float EdgeZone = 6f;
            /// <summary>拖出这个像素数才算拖动，避免点击时手抖被当成拖动。</summary>
            private const float DragThreshold = 3f;

            private EditorTimelineView view;
            private TimelineMarker marker;

            private Mode mode = Mode.None;
            private bool dragging;
            private float originalStartTime;
            private float originalDuration;

            internal void Initialize(EditorTimelineView owner, TimelineMarker target)
            {
                view = owner;
                marker = target;
            }

            // ---------------------------------------------------------- 指针形状

            public void OnPointerMove(PointerEventData eventData)
            {
                if (view == null || marker == null || marker.Item == null || dragging)
                {
                    return;
                }

                bool onLeftEdge;
                bool onRightEdge;
                DetectEdges(eventData.position, out onLeftEdge, out onRightEdge);
                view.ApplyBlockDragCursor(onLeftEdge, onRightEdge);
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (!dragging)
                {
                    view.RestoreCursor();
                }
            }

            // ---------------------------------------------------------- 拖动

            public void OnBeginDrag(PointerEventData eventData)
            {
                // 诊断：进这一帧时按没按 Ctrl、哪个键。用来对齐 OnEndDrag 那条。
                if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
                {
                    Main.Logger.Log("[TimelineDrag] marker.OnBeginDrag button=" + eventData.button
                        + " ctrl=" + MarqueeModifierHeld
                        + " marqueeActive=" + (view != null && view.MarqueeActive));
                }

                mode = Mode.None;
                dragging = false;

                // **按着 Ctrl 时不拖块** —— 那是在框选。
                //
                // 而且必须在这里把框选**接手过来**。块身上挂着 Button，而
                // Button 自己就实现了 IPointerDownHandler，从块上按下时事件
                // 被它吃掉 —— 视图的 OnPointerDown / OnDrag / OnPointerUp
                // 一个都收不到，那条"视图自己拉框"的路压根没开始。
                // 所以三个回合都得由这里转交（见下面 OnDrag / OnEndDrag）。
                if (MarqueeModifierHeld)
                {
                    if (view != null)
                    {
                        view.MarqueeFromMarkerBegin(eventData);
                    }
                    return;
                }

                // 中键不是拖块的，是平移时间轴的。这里必须转交出去。
                //
                // 事件系统是按「按下点」去找拖拽处理者的（GetEventHandler 从
                // pointerPress 沿父链往上找第一个实现 IDragHandler 的物体），
                // 所以中键落在块身上时找到的是 marker，视图自己的 OnBeginDrag
                // 根本不会被调用——它的中键平移也就无从触发，表现为
                // 「在块上按中键拖不动」。落在空白处时链上只有视图，
                // 于是照常平移，这正是不对称的来源。
                //
                // 借用视图那条现成的路径，不自己另写一套平移逻辑。
                // 放在 marker 判空之前：平移跟这块还在不在没关系。
                if (eventData.button == PointerEventData.InputButton.Middle)
                {
                    // 块身上挂着 Button，按下点又在它身上，所以事件系统
                    // 认为这也可能是一次点击——拖动结束后会把 click
                    // 派发给它，平移一次就顺手选中一个事件，参数栏跟着跳。
                    // 拖过就算平移，点击在此作废。
                    eventData.eligibleForClick = false;
                    if (view != null)
                    {
                        view.OnBeginDrag(eventData);
                    }
                    return;
                }

                if (view == null || marker == null || marker.Item == null)
                {
                    return;
                }

                if (eventData.button != PointerEventData.InputButton.Left)
                {
                    return;
                }

                TimelineItem item = marker.Item;
                if (item.Event == null)
                {
                    return;
                }

                // **只读：块不能拖。** 拖动改的是事件的时间和时长，都是写谱面。
                // 拦在这里而不是 Drag 里：一旦进入拖动状态，光标、预览位置、
                // 松手提交都要走完整条链，中途拦反而容易留下半截状态。
                // `AllowEdit` 会顺手弹一句"只读模式不能修改谱面"。
                if (!view.CanEditChart())
                {
                    return;
                }

                // 用 pressPosition 判边缘，不用 position：OnPointerMove 不保证
                // 在 OnBeginDrag 之前跑过，而 pressPosition 一定是按下的那一点。
                bool onLeftEdge;
                bool onRightEdge;
                DetectEdges(eventData.pressPosition, out onLeftEdge, out onRightEdge);

                // 没有时长的事件不给拉长——它只有一个时间点，没有长度可改。
                if (!view.CanResizeItem(item))
                {
                    onLeftEdge = false;
                    onRightEdge = false;
                }

                if (onLeftEdge)
                {
                    mode = Mode.ResizeLeft;
                }
                else if (onRightEdge)
                {
                    mode = Mode.ResizeRight;
                }
                else
                {
                    mode = Mode.Move;
                }

                originalStartTime = item.StartTime;
                originalDuration = item.Duration;

                dragging = true;
                view.BeginBlockDrag(marker, mode != Mode.Move);
                view.BeginBlockDragPointer(eventData.pressPosition, marker);
                eventData.Use();
            }

            public void OnDrag(PointerEventData eventData)
            {
                // **框选优先，而且用 `MarqueeActive` 判、不用 Ctrl 键。**
                //
                // 框是在 OnBeginDrag 里开起来的（那时按着 Ctrl），但拖到一半
                // 松开 Ctrl 是常事 —— 按 Ctrl 判会让框在手指底下消失，
                // 然后掉进下面那套块拖动逻辑（而 dragging 还是 false），
                // 表现就是"拖到一半框没了"。
                if (view != null && view.MarqueeActive)
                {
                    view.MarqueeFromMarkerDrag(eventData);
                    return;
                }

                // 中键平移期间 OnBeginDrag 把事件交给了视图，这里跟着转交，
                // 否则中键会走进下面按左键写的那套块拖动逻辑。
                if (eventData.button == PointerEventData.InputButton.Middle)
                {
                    if (view != null)
                    {
                        view.OnDrag(eventData);
                    }
                    return;
                }

                if (!dragging || view == null || marker == null)
                {
                    return;
                }

                // 块要跟着光标走（两个轴都跟），所以每一帧都把光标位置记下来，
                // 由 RefreshGeometry 摆块时用。
                view.UpdateBlockDragPointer(eventData.position);

                // 块可能已经被池回收给别人了（RefreshData 重建了 TimelineItem）。
                // 这时手上这个 item 是孤儿，继续改它没有意义。
                if (marker.Item == null)
                {
                    Cancel();
                    return;
                }

                // **用 Canvas 本地坐标算位移，不能用屏幕坐标。**
                //
                // eventData.position 是屏幕像素，而 pixelsPerSecond 是 Canvas
                // 本地像素/秒，两者之间差一个 Canvas 缩放（这个工程里是 1.5）。
                // 拿屏幕位移去除，块每一帧都会多走 scale 倍 —— 拖得越远漂得越
                // 厉害（实测鼠标走 314px、块走了 464px，刚好 1.48 倍）。
                // 纵向一直没这个毛病，正是因为它本来就用 dragPointerLocal。
                float deltaX = view.dragPointerLocal.x - view.dragPressPointerLocal.x;
                if (mode == Mode.Move && Mathf.Abs(deltaX) < DragThreshold)
                {
                    return;
                }

                float deltaTime = deltaX / Mathf.Max(1f, view.pixelsPerSecond);

                switch (mode)
                {
                    case Mode.Move:
                    {
                        // 拖块身：按整拍移动、保住格内偏移，时长不变。
                        float start = view.SnapBlockTimeKeepingPhase(
                            originalStartTime + deltaTime, originalStartTime);
                        view.PreviewBlockDrag(marker, start, originalDuration);
                        break;
                    }
                    case Mode.ResizeRight:
                    {
                        // 右边缘：终点吸附到格边界，规则和拖块身一致
                        // （设置项开时吸附，按 Alt 临时反转，都走 SnapBlockTime）。
                        //
                        // 唯一要防的是「吸回起点」：原始时长比半格还短时，
                        // 最近的边界就是起点自己，时长会塌成 0、块缩到最小
                        // 宽度，看起来像拉不动。所以吸附结果没前进就退回
                        // 未吸附的值——宁可自由伸缩，也不让它塌。
                        float rawEnd = originalStartTime + originalDuration + deltaTime;
                        float end = view.SnapBlockTime(rawEnd);
                        if (end <= originalStartTime)
                        {
                            end = Mathf.Max(originalStartTime, rawEnd);
                        }
                        view.PreviewBlockDrag(marker, originalStartTime, end - originalStartTime);
                        break;
                    }
                    case Mode.ResizeLeft:
                    {
                        // 左边缘：**右端钉住不动**，起点吸附到格边界。
                        // 同样防「吸过右端」——那样时长会变成负数。
                        float end = originalStartTime + originalDuration;
                        float start = view.SnapBlockTime(originalStartTime + deltaTime);
                        if (start >= end)
                        {
                            start = Mathf.Clamp(originalStartTime + deltaTime, 0f, end);
                        }
                        view.PreviewBlockDrag(marker, start, Mathf.Max(0f, end - start));
                        break;
                    }
                }
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                // 诊断：这一条最要紧 —— 松手那一刻 dragging 与 marqueeActive
                // 是不是同时为真。若同时为真，下面第一条分支会直接 return，
                // EndBlockDrag 永远走不到，blockDragActive 就永久卡住。
                if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
                {
                    Main.Logger.Log("[TimelineDrag] marker.OnEndDrag button=" + eventData.button
                        + " dragging=" + dragging
                        + " marqueeActive=" + (view != null && view.MarqueeActive));
                }

                // 从块上起步的框选：收尾也在这里（见 OnBeginDrag 的说明）。
                if (view != null && view.MarqueeActive)
                {
                    view.MarqueeFromMarkerEnd(eventData);
                    return;
                }

                // 中键的收尾也归视图：居中浏览模式下它在 OnEndDrag 里
                // 关掉自己开的那次 scrub，漏掉就会一直挂着，
                // 之后的定位提交全被静默拒绝。
                if (eventData.button == PointerEventData.InputButton.Middle)
                {
                    if (view != null)
                    {
                        view.OnEndDrag(eventData);
                    }
                    return;
                }

                if (!dragging)
                {
                    return;
                }

                dragging = false;
                view.EndBlockDrag(marker, originalStartTime, originalDuration);
                view.RestoreCursor();
                eventData.Use();
            }

            private void Cancel()
            {
                dragging = false;
                mode = Mode.None;
                if (view != null)
                {
                    view.CancelBlockDrag();
                    view.RestoreCursor();
                }
            }

            /// <summary>
            /// 按按下点落在块内的位置判定是不是瞄准了边缘。
            /// 用的是「相对块左边缘的像素」，所以块被裁剪（左端滑出视口）时
            /// 判定依然对——裁剪会一并改掉 rect 的宽度，两边同步偏移。
            /// </summary>
            private void DetectEdges(Vector2 screenPosition, out bool onLeftEdge, out bool onRightEdge)
            {
                onLeftEdge = false;
                onRightEdge = false;

                if (marker == null || marker.Rect == null)
                {
                    return;
                }

                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        marker.Rect, screenPosition, null, out local))
                {
                    return;
                }

                float width = marker.Rect.rect.width;
                float x = local.x + width * marker.Rect.pivot.x;
                onLeftEdge = x <= EdgeZone;
                onRightEdge = x >= width - EdgeZone;
            }
        }

        /// <summary>
        /// 时间轴里一个事件类型的分类框。行不再等高：每个分类框
        /// 以一行表头开头，后面才是它的事件行，所以纵向位置
        /// 来自 layoutRowY 表，而不是用行号乘以
        /// 一个固定高度。
        /// </summary>
        private sealed class TimelineSection
        {
            internal int TypeKey;
            internal string Title;
            internal Sprite Icon;
            internal bool Collapsed;

            // 行坐标，用于索引 layoutRowSection / layoutRowY。
            internal int HeaderRow;
            internal int EventRowCount;
            internal int ShownRows;

            // 本分类框的行在 layoutRows 里的起始下标。
            internal int EventRowBandStart;

            // 本分类框自己的滚动位置，单位是行。每个分类框是一个固定
            // 高度的窗口，一次只显示几行；在其中一个里滚动只移动
            // 该分类框的内容，所以要看更靠后的分类框，不必先滚过
            // 前面每个分类框的所有行。
            internal float RowScrollOffset;


            internal int HiddenRows
            {
                get { return EventRowCount - ShownRows; }
            }
        }

        private sealed class SectionHeaderView
        {
            internal GameObject GameObject;
            internal RectTransform Rect;
            internal Image Background;
            internal Image Icon;
            internal TextMeshProUGUI Label;
            internal Button Button;
        }

        private sealed class CollapsedBadge
        {
            internal RectTransform Rect;
            internal Image Arrow;
            internal TextMeshProUGUI Label;
        }

        internal const int LaneCount = 7;
        /// <summary>
        /// 块对象池上限。
        ///
        /// **必须远大于"一屏最多能同时显示的块数"。** 之前是 320：密集谱上
        /// 一屏能过几百个块，池子一满循环就 `break`，后面的块拿不到标记 ——
        /// 表现是"块看不见、也点不中"，但拖别的块过去照样会判重叠变红
        /// （重叠判定看的是数据，不是标记）。
        /// </summary>
        private const int MarkerPoolSize = 1600;
        private const int MajorTickPoolSize = 32;
        private const int FloorLinePoolSize = 160;
        private const int RowBackgroundPoolSize = 32;
        private const int SectionHeaderPoolSize = 96;
        private const int CollapsedBadgePoolSize = 320;
        // 起始时间相差在这个范围内的两个事件，算作同一列（用于提示标记）。
        private const float ColumnGroupSeconds = 0.02f;
        private static Color ChevronColor { get { return UiTheme.Current.TextSecondary; } }
        private readonly List<float> hintTimes = new List<float>();
        private readonly List<int> hintCounts = new List<int>();
        // 工具栏占两行，给后续要加的小按钮留位置。
        // 第一行放时间轴自己的控件（筛选、缩放、播放模式…），
        // 第二行预留给后续功能。主体从两行之下开始。
        private const float ToolRowHeight = 34f;
        private const float ToolRowGap = 2f;
        // 工具区高度不是常量：放置事件面板的按钮放不下时会多占一行，
        // 这里跟着变高，时间轴可视区相应变矮。见 RefreshEventPalette。
        private float headerHeight = 42f + ToolRowHeight + ToolRowGap;
        private const float GutterWidth = 12f;
        /// <summary>
        /// 左端让给装饰物列表的宽度。
        ///
        /// 时间轴内容（标尺、事件画布、顶部那排按钮、事件按钮）全部整体
        /// 右移这么多，左边腾出来的竖条放装饰物列表。腾出来之后事件按钮
        /// 一行排不下，会自己多占一行，工具区跟着变高——那是想要的：
        /// 竖条高一点，列表就能多显示几行。
        /// </summary>
        private const float DecoPaneWidth = 290f;
        /// <summary>竖条与时间轴内容之间的留缝。没有它两块会贴在一起。</summary>
        private const float DecoPaneGap = 14f;
        /// <summary>
        /// 时间轴内容整体右移的量。顶部那排按钮、标题、事件按钮、
        /// 视口都用它定位——改竖条宽度时只要改这一处，不会漏。
        ///
        /// 现在是 0：装饰物竖条已经搬到左栏，时间轴不再需要给它让位，
        /// 内容直接铺满整块面板的宽度。保留这个常量而不是删掉它的引用，
        /// 是因为上面那句话依然成立——将来若再要往左边塞东西，
        /// 改这一处就够了。
        /// </summary>
        private const float ContentShift = 0f;
        /// <summary>
        /// 竖条顶端离面板顶的距离。
        ///
        /// 它**不**从 headerHeight 起——headerHeight 是事件按钮占了几行之后
        /// 才定下来的，跟着它走的话，竖条顶端会随调色板行数上下跳，
        /// 而且一开始就比「时间轴」标题低一大截。竖条是整块面板的一个
        /// 兄弟区域，顶端贴面板顶、底端铺到面板下沿，跟调色板占几行无关。
        /// </summary>
        private const float DecoPaneTop = 8f;
        internal const float RulerHeight = 28f;
        // 播放头：青色发光，由三层叠出来（外辉光 / 内辉光 / 实心芯线）。
        private const float PlayheadCoreWidth = 2f;
        private const float PlayheadGlowInnerWidth = 6f;
        // 辉光只比芯线宽一点点。铺太开会把播放头读成一条粗带子，
        // 而不是「一条细线带着一层薄光」。
        private const float PlayheadGlowWidth = 8f;
        private const float PlayheadHandleWidth = 11f;
        private static Color PlayheadColor { get { return UiTheme.Current.ActionPrimary; } }
        private static Color PlayheadGlowInnerColor { get { return WithAlpha(UiTheme.Current.ActionPrimary, 0.35f); } }
        private static Color PlayheadGlowOuterColor { get { return WithAlpha(UiTheme.Current.ActionPrimary, 0.14f); } }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
        private const float DefaultRowHeight = 30f;
        private const float MinimumRowHeight = 24f;
        private const float MaximumRowHeight = 48f;
        private const float RowHeightStep = 3f;
        private const float RowVerticalPadding = 3f;
        // 图层模式的单一 section 用一个不会与事件类型冲突的键。
        private const int LayerStackTypeKey = int.MinValue;
        private const float SectionHeaderHeight = 24f;
        private const float SectionGap = 5f;
        // 一个分类框窗口里完整可见的行数。窗口按这个值再加
        // 一小截来定尺寸，让下一行露出一丝——正是这一丝让眼睛知道
        // 下面还有更多，不用去读「+N」。
        private const float SectionVisibleRows = 4f;
        private const float SectionSliverRows = 0.5f;
        private const float BadgeWidth = 68f;
        private const float BadgeHeight = 7f;
        // 默认缩放下可见跨度是十二秒，让分类框打开时显示的是一段
        // 事件而不是整张谱面——时间轴是用来处理某一段的，
        // 全局概览交给标尺和滚动条。缩放读数的基准就是
        // 这个值，所以默认视图显示 100%。
        // **缩放的三档（默认 / 上限 / 下限）全部按谱面 BPM 算** —— 见下面那组属性。
        // 旧的是写死的 192 / 96 / 640：快谱缩到最长也看不清一拍，
        // 慢谱缩到最挤又挤成一片。现在三档都换算成「一拍占多少像素」，
        // 于是不同 BPM 的谱看起来是一致的。

        /// <summary>
        /// 默认缩放下，一拍占多少像素。**也是「100%」的基准。**
        /// 96 这个数让 BPM 120 的谱算出来正好是旧的固定值 192，
        /// 老谱子的手感不变。
        /// </summary>
        private const float DefaultPixelsPerBeat = 96f;

        /// <summary>
        /// 缩放到最长时，一拍占多少像素。
        /// 取 320 是为了让 BPM 120 时正好等于旧的固定上限 640。
        /// </summary>
        private const float MaxPixelsPerBeat = 320f;

        /// <summary>最挤能缩到上限的几分之一。</summary>
        private const float MinZoomRatio = 0.25f;

        /// <summary>
        /// 谱面的基础 BPM。读不到就退回 120（最常见的值）。
        ///
        /// **只取 `levelData.bpm`，不乘每格的 `speed`** —— 变速段不该改变
        /// 整张谱的缩放范围。
        /// </summary>
        private static float ChartBpm
        {
            get
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null || editor.levelData == null)
                {
                    return 120f;
                }
                return Mathf.Max(1f, editor.levelData.bpm);
            }
        }

        /// <summary>打开谱面时的默认缩放，也是缩放读数里「100%」的基准。</summary>
        private static float DefaultPixelsPerSecond
        {
            get { return DefaultPixelsPerBeat * ChartBpm / 60f; }
        }

        /// <summary>缩放上限：让「每拍像素」固定，所以快谱的上限更大。</summary>
        /// <summary>
        /// 缩放上限：视口里最少显示 `MinVisibleSeconds` 秒。
        /// 用"看到多少秒"定，而不是拿 BPM 推像素 —— 换谱面/BPM 变化时范围不会飘。
        /// </summary>
        private const float MinVisibleSeconds = 1.2f;

private static float MaximumPixelsPerSecond
        {
            get { return MaxPixelsPerBeat * ChartBpm / 60f; }
        }


        /// <summary>
        /// 缩放下限，取上限的一个固定比例。
        ///
        /// 块宽另有 `BlockMinWidth`（64 像素）兜底，所以这里管的不是「块别太窄」，
        /// 而是「整条轴别挤成一片」—— 再小下去块虽然不会窄过 64 像素，
        /// 但彼此贴到一起，名字和缓速曲线都读不出来。
        /// </summary>
        /// <summary>
        /// 缩放下限：视口里最多显示 `MaxVisibleSeconds` 秒。
        /// 再缩下去一块就要占好几秒，同行必然叠住。
        /// </summary>
        private const float MaxVisibleSeconds = 45f;

private static float MinimumPixelsPerSecond
        {
            get { return MaximumPixelsPerSecond * MinZoomRatio; }
        }

        /// <summary>视口宽度（布局未就绪时退回设计宽度，避免除零）。</summary>
        private float EffectiveBodyWidth()
        {
            return bodyWidth > 1f ? bodyWidth : 1890f;
        }

        private const float DataRefreshInterval = 0.5f;
        private const float SelectionRefreshInterval = 0.12f;
        private const float EventGap = 3f;
        internal const float MinimumPointEventWidth = 64f;
        internal const float MarkerIconLeft = 6f;
        internal const float MarkerIconGap = 5f;
        internal const float ResizeHandleHeight = 9f;

        // 与工作台的调色板保持一致；时间轴是块大面积的表面，两者之间任何
        // 偏差都会读成「贴了另一款程序的界面」。
        // 与工作台的灰阶一致：以 #151515 为基准的中性灰，不带任何色偏。
        // 时间轴是屏幕上最大的表面，所以与工作台其余部分的任何偏差
        // 都会读成来自另一款程序的面板。
        private static readonly Color PanelAlt = Palette.PanelAlt;
        private static readonly Color ButtonNormal = Palette.ButtonNormal;
        private static readonly Color ButtonHover = Palette.ButtonHover;
        private static readonly Color Selected = Palette.Selected;
        // 主体卡片：比面板亮，略带透明。
        // 比面板（0.082）高一步，合成后约 0.134——足够让主体读成
        // 独立表面而不是一个洞，又足够柔和，不会和面板抢眼。
        // 0.196 是第一次尝试，结果读成白色；现在行条纹近乎透明，
        // 所以这个值就是卡片的最终观感，而不是被覆盖层盖住后露出的底色。
        // 纯平背景：时间轴视口不再铺一层 90% 的凸起面，
        // 露出的就是工作台那一层底色（与「参数」栏一致）。
        private static Color TimelineCard { get { return new Color(0f, 0f, 0f, 0f); } }
        // 标尺条铺在卡片上，颜色刻意更深，好让时间标签
        // 有自己的底带。
        // 标尺条同样不再铺底色，只保留刻度线与数字。
        /// <summary>
        /// 标尺那一条的底色。**必须不透明。**
        ///
        /// 之前给的是全透明（后来改成 3% 白），依赖模块底色透上来；但标尺
        /// 这一段实际没有东西垫底，结果是游戏画面直接透过来 —— 齿标在、
        /// 背后是空的。这里直接铺模块那一档 #0B0B0B。
        /// </summary>
        private static Color RulerStrip { get { return new Color(0.043f, 0.043f, 0.043f, 1f); } }
        // 分类框窗口的填充色：不透明，且与卡片同值，让各窗口读成
        // 一整块表面，而不是一排瓷砖。
        private static Color WindowFill { get { return UiTheme.Current.SurfacePanel; } }
        // 标尺线和格线是参考线，不是内容：淡到能待在事件
        // 块下面，又不至于和它们抢注意力。
        // 标尺刻度和格线。从 0.16 的 alpha 提上来，那个值在面板上直接消失了，
        // 但仍远低于面板边框，让时间轴仍读成内容而不是
        // 又一层边框。
        private static Color GridLineColor { get { return WithAlpha(Color.white, 0.10f); } }
        private static readonly Color TextPrimary = Palette.TextPrimary;
        private static readonly Color TextSecondary = Palette.TextSecondary;
        private static readonly Color TextMuted = Palette.TextMuted;
        // 每条泳道一个色相，颜色一眼就能告诉你事件属于哪个家族。
        private static readonly float[] LaneBaseHues = { 0.53f, 0.72f, 0.42f, 0.94f, 0.10f, 0.58f, 0.26f };
        // 同一泳道里每个事件按自身 id 在色相上错开一点的幅度。
        private const float BlockHueJitter = 0.10f;
        // 事件块的亮度（相对亮度，不是 HSV 的 V）。
        //
        // 0.45 落在「不深不浅」那一段：对分类框底色约 7:1，六个家族彼此
        // 又在同一条亮度线上，所以谁也不会比谁更抢眼，但每个色相都还
        // 认得出来。低了整片读成灰，高了就刺眼。
        // 0.45 → 0.32：整片事件块原来偏亮，长时间看刺眼。
        // 压暗后色相仍分得开（六个家族还在同一条亮度线上），
        // 但要跟时间轴底色拉开层次就靠块上文字与曲线，不再靠块本身的亮度。
        private const float BlockBrightness = 0.32f;
        // 块上文字（以及缓速曲线、选中描边）的亮度。压到接近黑，
        // 对块本身约 7:1，小块上的小字也清楚。
        private const float LabelBrightness = 0.020f;
        // 块填充允许的最大饱和度。黄绿这一类色相本身就亮，不设上限的话
        // 它们会被推到接近纯色，反而比别的家族跳。到顶之后就靠降 V 收。
        private const float BlockTintMaxSaturation = 0.85f;
        // 求文字亮度时用的参考饱和度。文字偏灰一点更像墨，
        // 而且比高饱和的深色读起来舒服。
        private const float LabelReferenceSaturation = 0.80f;
        // 反解亮度的二分次数。20 次已经远超 8 位色深的分辨力，
        // 而且只在事件数据变化时算，不进每帧路径。
        private const int LuminanceSearchSteps = 20;

        private readonly List<TimelineItem>[] laneItems = new List<TimelineItem>[LaneCount];
        private readonly List<TimelineItem> layoutItems = new List<TimelineItem>();
        private readonly List<List<RowInterval>> layoutRows = new List<List<RowInterval>>();
        private readonly List<TimelineMarker> markerPool = new List<TimelineMarker>();
        /// <summary>
        /// 块稳定 ID → 画它的那个对象。
        ///
        /// **不能按遍历顺序轮流绑池子。** `BlockViews` 的顺序不是稳定的，
        /// 每帧同一个池位可能换成另一块，曲线就会来回跳变（用户看到的
        /// 「缓速反复镜像」其实就是这个）。按 ID 绑，对象就认死一块。
        /// </summary>
        /// <summary>事件块那一层（在标尺下、播放头上）。</summary>
        private RectTransform blockLayer;
        /// <summary>格线层（在块下面）。</summary>
        private RectTransform gridLayer;
        private EditorStudioOverlay owner;
        private RectTransform root;
        private RectTransform viewport;

        /// <summary>下一次重建时要不要重排（载入 / 换谱 / 固定排版）。</summary>
        private bool repackOnNextRebuild = true;
        /// <summary>防抖：到这个时刻执行一次重排（0 = 不排）。</summary>
        private float repackDueTime;
        private readonly HashSet<int> activeBlockIds = new HashSet<int>();
        private readonly List<TimelineSection> layoutSections = new List<TimelineSection>();
        private readonly List<int> layoutRowSection = new List<int>();
        private readonly List<float> layoutRowY = new List<float>();
        private readonly List<SectionHeaderView> sectionHeaderPool = new List<SectionHeaderView>();
        private readonly List<CollapsedBadge> collapsedBadgePool = new List<CollapsedBadge>();
        private readonly List<TextMeshProUGUI> majorTickLabels = new List<TextMeshProUGUI>();
        // 每个刻度标签上一次写入的文本，用来跳过没变的 TMP 赋值。
        private readonly List<string> majorTickLabelText = new List<string>();
        private readonly List<Image> floorLines = new List<Image>();
        private readonly List<RectTransform> rowBackgrounds = new List<RectTransform>();
            internal RectTransform Content;

        private readonly Dictionary<int, TimelineMarker> markerByBlockId
            = new Dictionary<int, TimelineMarker>();
        /// <summary>
        /// 上一帧收起来的块对象，下一帧优先复用。
        ///
        /// **没有这张表就只能一直新建**：滚一下视口，进来的块都要新对象，
        /// 撞到池上限后循环 `break`，后面的块全没有实体（数据还在，拖过去
        /// 照样判重叠变红）—— 用户看到的"稍微一动就消失一大片"就是这个。
        /// </summary>
        private readonly List<TimelineMarker> freeMarkers = new List<TimelineMarker>();



        private RectTransform markerLayer;
        private RectTransform sectionHeaderLayer;
        private RectTransform badgeLayer;
        private RectTransform rulerBackdrop;
        private RectTransform playhead;
        // 播放头的三个可见子节点。必须自己存着——它们的高度是按
        // 构建时的 bodyHeight 定死的，而那一帧 root 的尺寸往往还是
        // 占位值。只改父节点的高度是没用的：父节点自己没有像素，
        // 线看上去永远停在最初那个偏矮的高度上（表现就是"被切了一半"）。
        private Image playheadGlow;
        private Image playheadCore;
        private Image playheadHandle;
        private TextMeshProUGUI summaryText;
        private TextMeshProUGUI timeText;
        private TextMeshProUGUI zoomText;
        private TMP_InputField jumpField;
        private TMP_FontAsset font;
        private Sprite roundedSprite;
        private EditorDecorationList decoPane;
        private Texture2D roundedTexture;
        // 选中事件用的空心环形 sprite。做成九宫格切片，拉伸时四角保持
        // 圆角、四边不糊——和 roundedSprite 一样的做法，只是中间挖空。
        private Sprite outlineSprite;
        private Texture2D outlineTexture;
        private Button viewLockButton;
        /// <summary>「点轨道跟随」与「定位到播放头」两颗按钮（图标自绘）。</summary>
        private Button locateButton;
        private Button locatePlayheadButton;
        /// <summary>标题行里的车道筛选按钮与图标（原来在预览工具栏上）。</summary>
        /// <summary>
        /// 播放时视图怎么跟随。
        /// </summary>
        private enum TimelineFollowMode
        {
            /// <summary>播放头自由走，视图归用户手动滚。</summary>
            FreePlayhead,
            /// <summary>播放头钉在时间轴正中，内容从它下面滑过。</summary>
            CenteredPlayhead
        }

        private TimelineFollowMode followMode = TimelineFollowMode.FreePlayhead;

        internal bool IsCenteredPlayhead
        {
            get { return followMode == TimelineFollowMode.CenteredPlayhead; }
        }


        // 固定播放头下中键横拖 = 扫时间：记开始时间与累计量。
        private float centerBrowseStartTime;
        private float centerBrowseAccum;
        private bool centerBrowseScrubbing;

        /// <summary>
        /// 固定播放头下视图滑向"播放头在正中"的速度（每秒指数逼近比例）。
        ///
        /// **缓动而不是每帧硬摆**：硬摆时一停播放、一切模式就"啪"地跳一下；
        /// 缓动之后这些时刻都是滑过去的，停止播放也是平滑收尾。
        /// </summary>
        private const float CenterLerpSpeed = 10f;

        /// <summary>手动横滚之后暂停回位的时长（秒），避免边滚边被拽。</summary>
        private float centeredHoldUntil;

        /// <summary>
        /// 固定播放头下 Shift+滚轮 = 扫时间。
        ///
        /// 和自由模式的滚动用**同一套"欠账 + 指数逼近"**（`ScrollSmoothSpeed`），
        /// 所以手感、滑停时长完全一致 —— 之前这里固定按 5 秒/秒 追，明显比
        /// 自由模式慢一大截。
        /// </summary>
        private float centeredScrubPending;
        private bool centeredScrubActive;

        /// <summary>「定位到播放头」的滑动目标与开关（滑过去而不是瞬移）。</summary>
        private float viewGlideTarget;
        private bool viewGliding;

        /// <summary>
        /// 滚轮平滑：**只追赶，不留惯性**。
        ///
        /// 滚轮把"还没走完的距离"记进 `*ScrollPending`，每帧按指数逼近把它花掉
        /// （`1 - e^(-22·dt)`），花完就停。
        ///
        /// 早先那套是"给一个初速度再衰减"，在触控板上会出事：系统自己会连发
        /// 一串动量事件，每个都再加一次速度，叠起来松手后能滑好几秒。
        /// 改成"欠账式"之后，输入停 → 欠账有限 → 0.2 秒内停稳，不会有残余。
        /// </summary>
        private const float ScrollSmoothSpeed = 22f;
        private float verticalScrollPending;
        private float horizontalScrollPending;

        /// <summary>切换「自由 / 固定播放头」。</summary>
        internal void ToggleFollowMode()
        {
            followMode = followMode == TimelineFollowMode.FreePlayhead
                ? TimelineFollowMode.CenteredPlayhead
                : TimelineFollowMode.FreePlayhead;
            // 切模式时把可能还开着的"扫时间"会话收掉，否则 scrub 状态挂住。
            if (centeredScrubActive)
            {
                centeredScrubActive = false;
                SubmitPendingScrub(true);
                EditorPlaybackBridge.EndTimelineScrub();
                ParticleWatch.ResumeAll();
            }
            visualsDirty = true;
            if (Main.Overlay != null)
            {
                Main.Overlay.SetTransientStatus(followMode == TimelineFollowMode.CenteredPlayhead
                    ? "固定播放头：开（内容从中间滑过）"
                    : "固定播放头：关（视图归你手动滚）");
            }
        }

        /// <summary>居中模式下视图该在的位置（让播放头落在视口正中）。</summary>
        private float CenteredViewTarget()
        {
            float visible = Mathf.Max(1f, bodyWidth) / Mathf.Max(1f, pixelsPerSecond);
            return PlayheadSeconds - visible * 0.5f;
        }

        private readonly List<Button> laneButtons = new List<Button>();
        private readonly List<Image> laneIcons = new List<Image>();
        private Image locateIcon;
        private Image locatePlayheadIcon;
        private Sprite locateSprite;
        private Sprite locatePlayheadSprite;
        private Button followModeButton;
        private Button selectionFollowButton;
        private Button snapDragButton;
        private Sprite followModeSprite;
        private Sprite selectionFollowSprite;
        private Sprite snapDragSprite;
        private Sprite viewLockSprite;
        /// <summary>跳转按钮那张「文档 + 放大镜」图标，只生成一次。</summary>
        private static Sprite jumpIconSprite;
        // 筛选改成多选：每个车道一个可见标志，而不是一个单选值。
        // 按钮挂在预览面板的工具栏上（见 EditorStudioOverlay.BuildPreview），
        // 由它调用 ToggleLane / SetAllLanes，状态仍然由时间轴持有。
        private readonly bool[] laneVisible = new bool[LaneCount];

        /// <summary>时间轴的排列方式。</summary>
        private enum TimelineLayoutMode
        {
            /// <summary>按事件类型分组的分类卷帘：每个类型一个固定高度的可滚动盒子。</summary>
            SectionRoll,
            /// <summary>每个类型一个平铺的图层，纵向全部展开、不裁剪。</summary>
            LayerStack
        }

        /// <summary>
        /// 排布方式。**分类卷帘那一档已经删掉了** —— 按分类各自开一个固定高度的
        /// 滚动窗口、能折叠、折叠了还要画徽标，那一整套在一个"看谱面"的工具里
        /// 是鸡肋：真要找某个分类，直接滚过去就行。
        ///
        /// 字段留着而不是彻底删掉，是因为下面还有一批只被卷帘走到的分支
        /// （窗口上限、折叠、SectionWindow 池子）要跟着一起清 —— 那一步和
        /// 车道区重写一起做，免得现在两头改。
        /// </summary>
        private TimelineLayoutMode layoutMode = TimelineLayoutMode.LayerStack;

        internal bool IsSectionRollLayout
        {
            get { return layoutMode == TimelineLayoutMode.SectionRoll; }
        }

        /// <summary>
        /// 一个分类框的表头行占多高。分类卷帘每个类型有一个表头（名字、
        /// 图标、折叠箭头）；<b>平铺模式不分类，也就没有表头</b>，所以是 0。
        /// 表头行在那个模式下仍然占行表里的一个槽位，只是高度为零 ——
        /// 这样 HeaderRow 这个下标对谁都还成立，不用给两套索引。
        /// </summary>
        private float HeaderRowHeight
        {
            get { return layoutMode == TimelineLayoutMode.SectionRoll ? SectionHeaderHeight : 0f; }
        }

        private readonly List<Button> laneFilterButtons = new List<Button>();
        private readonly List<Image> laneFilterIcons = new List<Image>();
        private bool laneFilterStateInitialized;
        private LevelEvent selectedEvent;
        private float bodyWidth;
        private float bodyHeight;
        private float pixelsPerSecond = DefaultPixelsPerSecond;
        /// <summary>
        /// 打开谱面后，第一次排版完成时把横向缩放拉到最大（= Ctrl+上滚轮到顶）。
        ///
        /// **为什么不在"打开"那一刻直接设**：那一刻 `ChartBpm` 可能还是上一张谱
        /// 残留的值，而缩放上限 = `MaxPixelsPerBeat × BPM ÷ 60` —— 用错 BPM 算出来的
        /// 值，等真实 BPM 到了就落在合法区间外面，用户一滚轮反而会往下跳。
        /// 所以先记一笔，等排版完成（那时 BPM 已经对了）再设。
        /// </summary>
        private bool pendingZoomToMaximum;
        private float viewStartTime;
        private float totalDuration;
        private float navigationDuration;
        private float nextDataRefresh;
        private float nextSelectionRefresh;
        private float manualNavigationUntil;
        private float lastRootHeight;
        private float lastRootWidth;
        private float pendingScrubTime;
        private float lastSubmittedScrubTime = float.NaN;
        private float nextScrubSubmitTime;
        private float verticalScrollOffset;
        private float rowHeight;
        private int dataFingerprint;
        private int selectionFingerprint;
        private bool dataFingerprintValid;
        private bool viewInitialized;
        private bool visualsDirty = true;
        private bool layoutDirty = true;


        // 只滚动分类框时用它，避免为一格滚动重建整个布局。
        // 布局本身没变，变的只是各行在窗口里的纵向偏移。
        private bool middleDragging;
        private bool scrubbing;
        // 拖动时指针相对视口的横向位置，用来判断要不要边缘自动滚动。
        private float scrubPointerX;
        private bool scrubPointerValid;
        private bool scrubPending;
        private bool blockingPointerDrag;
        private static bool anyPointerCapture;

        internal static bool IsAnyPointerCapture
        {
            get { return anyPointerCapture; }
        }

        /// <summary>
        /// 当前选择集的只读视图，供参数面板跟随。
        /// 面板轮询它而不是订阅回调：分辨率一变，工作台就会
        /// 拆除重建，订阅会在身后留下
        /// 悬空的监听者。轮询本来也是本文件对游戏编辑器
        /// 自身选择集的做法，走 ComputeSelectionFingerprint。
        /// </summary>
        internal LevelEvent SelectedEvent
        {
            get { return selectedEvent; }
        }

        /// <summary>
        /// 清掉时间轴自己的选中。游戏侧的 floor / decoration 选中由调用方
        /// 负责（见 EditorPlaybackBridge 的清除选中热键）——本视图的
        /// 高亮大部分是从那边轮询出来的，两边都清才是真的「没选中」。
        /// </summary>
        internal void ClearSelection()
        {
            if (selectedEvent == null && multiSelection.Count == 0)
            {
                return;
            }

            selectedEvent = null;
            multiSelection.Clear();
            visualsDirty = true;
        }

        /// <summary>
        /// 点空白处：清掉**全部**选中 —— 时间轴的多选、主选中，以及原生那份
        /// 装饰物选中。
        ///
        /// 装饰物必须一起清。它们在时间轴里同样是块（框选、Ctrl 点都能选进来），
        /// 只清前两样会留下一个还高亮着的装饰物 —— 「清除所有选中」就只做了
        /// 一半，而且那个高亮还指着已经不在面板里的东西。
        ///
        /// 这和 <see cref="ClearSelection"/> 是两件事：那个只清时间轴自己的
        /// 两份状态，用在"工作台收起"这类场合；这个是用户主动取消选择。
        /// </summary>
        private void ClearAllSelection()
        {
            scnEditor editor = scnEditor.instance;
            bool hadDecorations = false;
            if (editor != null && editor.selectedDecorations != null
                && editor.selectedDecorations.Count > 0)
            {
                editor.selectedDecorations.Clear();
                hadDecorations = true;
            }

            if (selectedEvent == null && multiSelection.Count == 0 && !hadDecorations)
            {
                return;
            }

            selectedEvent = null;
            multiSelection.Clear();
            visualsDirty = true;

            if (owner != null)
            {
                owner.SetSceneSelectionStatus(string.Empty);
            }
        }

        // ---------------------------------------------------------------- 多选与框选

        /// <summary>
        /// 时间轴自己的**多选**集合。
        ///
        /// `selectedEvent` 仍然是"主选中"—— 参数栏显示的那一个、同步给原生
        /// 编辑器的也是那一个。这个集合是它旁边的补充，两者都算"选中"
        /// （见 IsTimelineItemSelected）。
        ///
        /// **装饰物不走这里**：原生编辑器自己就有一份 `selectedDecorations`
        /// 列表，装饰物天然多选，本视图只负责把它读出来判断。
        /// </summary>
        private readonly HashSet<LevelEvent> multiSelection = new HashSet<LevelEvent>();

        private bool marqueeActive;
        private Vector2 marqueeStart;
        private Vector2 marqueeCurrent;
        private RectTransform marqueeBand;

        private static readonly Color MarqueeFill = new Color(0.45f, 0.75f, 1f, 0.16f);
        private static readonly Color MarqueeBorder = new Color(0.45f, 0.75f, 1f, 0.55f);

        /// <summary>
        /// 框选键（Ctrl）是不是按着。
        ///
        /// 按着的时候块自己的拖动要让位 —— 否则从块上按下再拖会变成"移动这个块"，
        /// 而不是拉框。见 marker.OnBeginDrag 和 ActivateMarker。
        /// </summary>
        internal static bool MarqueeModifierHeld
        {
            get
            {
                return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            }
        }

        /// <summary>当前多选里的条目（框选 / Ctrl+点击选的）。拖动、删除、复制都读它。</summary>
        internal ICollection<LevelEvent> MultiSelection
        {
            get { return multiSelection; }
        }

        /// <summary>
        /// 视图局部坐标 → **以视口左上角为原点**的坐标（x 向右、y 向下）。
        ///
        /// 统一成这一套是因为代码里其它地方（SetTopLeftRect、TryGetViewportPoint）
        /// 都是这个约定；而 `ScreenPointToLocalPointInRectangle` 给的是以
        /// **轴心**为原点的坐标，轴心在哪不确定，所以这里用 rect 的边界换算，
        /// 和轴心无关。
        /// </summary>
        private Vector2 ToViewportTopLeft(Vector2 local)
        {
            Rect rect = viewport.rect;
            return new Vector2(local.x - rect.xMin, rect.yMax - local.y);
        }

        /// <summary>
        /// <see cref="TryGetViewportPoint(Vector2, out float, out float)"/> 的
        /// Vector2 版本，只为了框选时写起来短一点。**坐标约定必须和那边一致**
        /// （左上角为原点、y 向下），否则框的位置会和判断用的位置对不上。
        /// </summary>
        private bool TryGetViewportPoint(Vector2 screenPosition, out Vector2 point)
        {
            float x;
            float y;
            if (!TryGetViewportPoint(screenPosition, out x, out y))
            {
                point = Vector2.zero;
                return false;
            }

            point = new Vector2(x, y);
            return true;
        }

        /// <summary>拉框中。块的拖动处理要看它决定是转交还是自己走。</summary>
        internal bool MarqueeActive
        {
            get { return marqueeActive; }
        }

        // ---------------------------------------------------------- 从块上起步的框选
        //
        // 块身上挂着 Button，而 Button 自己实现了 IPointerDownHandler /
        // IPointerUpHandler，从块上按下时事件全被它吃掉 —— 视图的
        // OnPointerDown / OnDrag / OnPointerUp 一个都收不到。所以"Ctrl 从块上
        // 拖出框"这件事必须由块把三个回合分头转交过来（见
        // TimelineMarker.OnBeginDrag / OnDrag / OnEndDrag）。
        //
        // 三个转发都不复用视图的 OnXxx：那些方法里还挂着标尺、播放头、
        // 中键平移等一大堆判断，从块上进来时全都不适用，硬塞进去只会
        // 让每条判断都要多一个"我是从哪来的"分支。

        /// <summary>从块上按下 Ctrl：在这里把框开起来。返回有没有成功开始。</summary>
        private bool MarqueeFromMarkerBegin(PointerEventData eventData)
        {
            float x;
            float y;
            if (!TryGetViewportPoint(eventData.position, out x, out y))
            {
                return false;
            }

            BeginMarquee(new Vector2(x, y));
            eventData.Use();
            return true;
        }

        /// <summary>框选中拖动：更新框的另一角。</summary>
        private void MarqueeFromMarkerDrag(PointerEventData eventData)
        {
            if (!marqueeActive)
            {
                return;
            }

            float x;
            float y;
            if (TryGetViewportPoint(eventData.position, out x, out y))
            {
                marqueeCurrent = new Vector2(x, y);
                UpdateMarqueeBand();
            }
            eventData.Use();
        }

        /// <summary>
        /// 框选收尾。
        ///
        /// 松手那一帧要用**最终位置**再更新一次，否则"拉出去又收回来"
        /// 会按上一帧的框去选 —— 和视图那条路（见 OnPointerUp）一致。
        /// </summary>
        private void MarqueeFromMarkerEnd(PointerEventData eventData)
        {
            if (!marqueeActive)
            {
                return;
            }

            float x;
            float y;
            if (TryGetViewportPoint(eventData.position, out x, out y))
            {
                marqueeCurrent = new Vector2(x, y);
            }
            FinishMarquee();
            blockingPointerDrag = false;
            anyPointerCapture = false;
            eventData.Use();
        }

        private void BeginMarquee(Vector2 point,
            [System.Runtime.CompilerServices.CallerMemberName] string from = "")
        {
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[TimelineDrag] BeginMarquee from=" + from);
            }
            marqueeActive = true;
            marqueeStart = point;
            marqueeCurrent = point;
            EnsureMarqueeBand();
            UpdateMarqueeBand();
        }

        private void EnsureMarqueeBand()
        {
            if (marqueeBand != null || viewport == null)
            {
                return;
            }

            GameObject band = new GameObject("MarqueeBand", typeof(RectTransform));
            band.transform.SetParent(viewport, false);
            marqueeBand = band.GetComponent<RectTransform>();
            // 左上角锚点 + 左上角轴心，和 SetTopLeftRect 一套；这样
            // anchoredPosition 就是"距视口左上角的距离"。
            marqueeBand.anchorMin = new Vector2(0f, 1f);
            marqueeBand.anchorMax = new Vector2(0f, 1f);
            marqueeBand.pivot = new Vector2(0f, 1f);

            Image fill = band.AddComponent<Image>();
            // 用现成的两张：实心圆角做底、空心圆角做边 —— 都是本视图
            // 已有的 sprite，不必为框选再生成一套。
            if (roundedSprite != null)
            {
                fill.sprite = roundedSprite;
                fill.type = Image.Type.Sliced;
            }
            fill.color = MarqueeFill;
            fill.raycastTarget = false;

            GameObject borderObject = new GameObject("Border", typeof(RectTransform));
            borderObject.transform.SetParent(band.transform, false);
            RectTransform border = borderObject.GetComponent<RectTransform>();
            border.anchorMin = Vector2.zero;
            border.anchorMax = Vector2.one;
            border.offsetMin = Vector2.zero;
            border.offsetMax = Vector2.zero;
            Image borderImage = borderObject.AddComponent<Image>();
            if (outlineSprite != null)
            {
                borderImage.sprite = outlineSprite;
                borderImage.type = Image.Type.Sliced;
            }
            borderImage.color = MarqueeBorder;
            borderImage.raycastTarget = false;

            band.SetActive(false);
        }

        private void UpdateMarqueeBand()
        {
            if (marqueeBand == null)
            {
                return;
            }

            Vector2 min = Vector2.Min(marqueeStart, marqueeCurrent);
            Vector2 max = Vector2.Max(marqueeStart, marqueeCurrent);
            SetTopLeft(marqueeBand, min.x, min.y, max.x - min.x, max.y - min.y);
            if (!marqueeBand.gameObject.activeSelf)
            {
                marqueeBand.gameObject.SetActive(true);
            }
            // 拉出来的框必须在所有块之上，否则扫过时会被块盖住。
            marqueeBand.SetAsLastSibling();
        }

        private Rect MarqueeViewportRect()
        {
            Vector2 min = Vector2.Min(marqueeStart, marqueeCurrent);
            Vector2 max = Vector2.Max(marqueeStart, marqueeCurrent);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private void FinishMarquee(
            [System.Runtime.CompilerServices.CallerMemberName] string from = "")
        {
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[TimelineDrag] FinishMarquee from=" + from
                    + " active=" + marqueeActive);
            }
            if (!marqueeActive)
            {
                return;
            }
            marqueeActive = false;
            if (marqueeBand != null)
            {
                marqueeBand.gameObject.SetActive(false);
            }

            Rect band = MarqueeViewportRect();
            // 拉出的框太小 = 只是按了一下，不是框选 —— 交给"点一下切换"那条路。
            if (band.width < 4f && band.height < 4f)
            {
                ToggleSelectionAt(marqueeStart);
                return;
            }

            ApplyMarqueeSelection(band);
        }

        /// <summary>
        /// 按住 Ctrl 点一下**空白处**：光标下没有块就清空多选。
        /// 点在块上的那条路不在这里 —— 见 <see cref="ToggleSelectionOf"/>。
        /// </summary>
        private void ToggleSelectionAt(Vector2 point)
        {
            ToggleSelectionOf(FindItemAtViewportPoint(point));
        }

        /// <summary>
        /// 把一个块**切换**进出多选；传 null（点在空白处）则清空多选。
        ///
        /// 两条路都汇到这里：
        ///   · 视图那条 —— Ctrl 从**空白处**按下、几乎没动就松手，框太小，
        ///     当成"点了一下"（见 <see cref="FinishMarquee"/>）。
        ///   · 块那条（**主路**）—— 直接 Ctrl 点在块身上，走块自己的
        ///     `Button.onClick`（见 <see cref="ActivateMarker"/>）。
        ///
        /// 块那条才是主路：**块身上挂着 Button，而 Button 自己就实现了
        /// IPointerDownHandler / IPointerUpHandler**，点在块上时事件根本到不了
        /// 视图，视图的框选路径整个不参与。
        /// </summary>
        private void ToggleSelectionOf(TimelineItem hit)
        {
            if (hit == null || hit.Event == null)
            {
                multiSelection.Clear();
                visualsDirty = true;
                return;
            }

            // **第一次 Ctrl+点击要把主选中那一个也收进来。**
            //
            // 不然「普通点一下 A，再 Ctrl 点 B、C」得到的是 {B, C} ——
            // A 还高亮着却不在这一批里，删除、复制、批量改参全都会漏掉它。
            // 明明看着选上了，实际没算数。
            //
            // 加选最常见的起手就是「先点一个，再 Ctrl 加」，所以第一次
            // Ctrl+点击的时候先把当前主选中并进来，后续各次就都是纯切换了。
            if (multiSelection.Count == 0 && selectedEvent != null
                && !selectedEvent.IsDecoration)
            {
                multiSelection.Add(selectedEvent);
            }

            if (!multiSelection.Add(hit.Event))
            {
                multiSelection.Remove(hit.Event);

                // 反选到空：连主选中一起清掉。
                //
                // 不清的话，对一个单独选中的块 Ctrl 点两下会「第一下没反应、
                // 第二下也没反应」—— 因为它每次都被并回来又拿掉。清掉之后
                // Ctrl 点两下就是彻底的取消选中。
                if (multiSelection.Count == 0)
                {
                    selectedEvent = null;
                    if (owner != null)
                    {
                        owner.SetSceneSelectionStatus(string.Empty);
                    }
                }
            }

            visualsDirty = true;
        }

        /// <summary>框选：把与框相交的块全部放进多选。</summary>
        private void ApplyMarqueeSelection(Rect band)
        {
            multiSelection.Clear();
            for (int i = 0; i < markerPool.Count; i++)
            {
                TimelineMarker marker = markerPool[i];
                if (marker == null || marker.Item == null || marker.Item.Event == null
                    || !marker.Rect.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (MarkerViewportRect(marker).Overlaps(band))
                {
                    multiSelection.Add(marker.Item.Event);
                }
            }

            visualsDirty = true;
            if (owner != null)
            {
                owner.SetSceneSelectionStatus(multiSelection.Count == 0
                    ? string.Empty
                    : "框选 " + multiSelection.Count + " 个事件");
            }
        }

        /// <summary>一个块在视口左上角坐标系里的矩形。</summary>
        private Rect MarkerViewportRect(TimelineMarker marker)
        {
            Vector3[] corners = new Vector3[4];
            marker.Rect.GetWorldCorners(corners);

            Vector2 one = ToViewportTopLeft(viewport.InverseTransformPoint(corners[0]));
            Vector2 two = ToViewportTopLeft(viewport.InverseTransformPoint(corners[2]));
            return Rect.MinMaxRect(
                Mathf.Min(one.x, two.x), Mathf.Min(one.y, two.y),
                Mathf.Max(one.x, two.x), Mathf.Max(one.y, two.y));
        }

        private TimelineItem FindItemAtViewportPoint(Vector2 point)
        {
            TimelineItem best = null;
            // 后摆的块画在上面，所以从后往前找第一个命中的。
            for (int i = markerPool.Count - 1; i >= 0; i--)
            {
                TimelineMarker marker = markerPool[i];
                if (marker == null || marker.Item == null
                    || !marker.Rect.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (MarkerViewportRect(marker).Contains(point))
                {
                    best = marker.Item;
                    break;
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- 删除 / 复制 / 粘贴

        /// <summary>
        /// 当前选中的**事件**（多选优先，否则主选中的那一个）。装饰物不在内。
        ///
        /// 复制 / 剪切用它（装饰物走另一条复制路径）。
        /// **删除不能用它** —— 见 <see cref="DeleteSelection"/> 里那段：框选出来的
        /// 装饰物不在这份里，会被静默漏掉。参数栏也用不滤装饰物的
        /// <see cref="PanelSelection"/>。
        /// </summary>
        internal List<LevelEvent> CollectSelection()
        {
            List<LevelEvent> targets = new List<LevelEvent>();
            if (multiSelection.Count > 0)
            {
                foreach (LevelEvent levelEvent in multiSelection)
                {
                    if (levelEvent != null && !levelEvent.IsDecoration)
                    {
                        targets.Add(levelEvent);
                    }
                }
                return targets;
            }

            if (selectedEvent != null && !selectedEvent.IsDecoration)
            {
                targets.Add(selectedEvent);
            }
            return targets;
        }

        /// <summary>
        /// 参数栏用的选集：**多选优先，否则主选中那一个**。
        ///
        /// 和 <see cref="CollectSelection"/> 是同一条判断，差别只有一个：
        /// **这里不排除装饰物**。删除/复制把它们交给
        /// `editor.selectedDecorations` 单独处理，而参数栏要给装饰物显示
        /// 图片预览 —— 在这儿把装饰物滤掉，面板就会空着。
        ///
        /// 参数栏必须走这个而不是 `TimelineSelection`：那个只认主选中，
        /// Ctrl 只选了一个、或反选到只剩一个的时候它还是 null，
        /// 面板会整个空掉，看着像"选中了却没反应"。
        /// </summary>
        internal List<LevelEvent> PanelSelection()
        {
            List<LevelEvent> targets = new List<LevelEvent>();
            if (multiSelection.Count > 0)
            {
                foreach (LevelEvent levelEvent in multiSelection)
                {
                    if (levelEvent != null)
                    {
                        targets.Add(levelEvent);
                    }
                }
                return targets;
            }

            if (selectedEvent != null)
            {
                targets.Add(selectedEvent);
            }
            return targets;
        }

        /// <summary>
        /// 这一帧参数栏该看谁（带装饰物那份）。
        ///
        /// <see cref="PanelSelection"/> 只看时间轴自己的两套选中（多选 / 主选中），
        /// 而**左栏那个装饰物列表点一行走的是游戏那一侧**——
        /// `editor.SelectDecoration`，它一个字都不碰时间轴的主选中。
        /// 于是"从左栏选一个装饰物，参数栏没反应"。
        ///
        /// 合并规则取"谁更像最新一次明确的动作"：
        ///   · 时间轴多选有东西 → 听它（那是框选/批量）
        ///   · 否则游戏那一侧有装饰物选中 → 听它
        ///   · 再否则 → 主选中那一个
        ///
        /// **必须在游戏那份非空时优先于主选中**：从时间轴上点一个普通事件之后
        /// 主选中会更新，但装饰物那份不会自己消失 —— 反过来优先的话，
        /// 参数栏会一直停在那个装饰物上。
        /// </summary>
        internal List<LevelEvent> PanelSelectionWithDecorations()
        {
            List<LevelEvent> targets = new List<LevelEvent>();
            if (multiSelection.Count > 0)
            {
                return PanelSelection();
            }

            scnEditor editor = scnEditor.instance;
            if (editor != null && editor.selectedDecorations != null
                && editor.selectedDecorations.Count > 0)
            {
                for (int i = 0; i < editor.selectedDecorations.Count; i++)
                {
                    if (editor.selectedDecorations[i] != null)
                    {
                        targets.Add(editor.selectedDecorations[i]);
                    }
                }
                if (targets.Count > 0)
                {
                    return targets;
                }
            }

            return PanelSelection();
        }

        /// <summary>
        /// 删掉选中的东西，记成**一条**撤销记录。
        ///
        /// 选中来自两处：时间轴的多选（事件）、以及**画面里选中的**
        /// （原生编辑器的 `selectedDecorations`）。用户按一次 Delete 就是
        /// 一次操作，所以两边合成一条命令 —— 撤销按一次就全回来。
        /// </summary>
        internal bool DeleteSelection(out string status)
        {
            status = null;

            // **只读模式：删除也是改谱面，拦住。**
            //
            // 顺带说一声 —— 同"加事件"那条：工作台每次打开都回到只读（那个状态
            // 没存进设置），静默拦住会让人以为"按钮坏了"。
            // 剪切（`CutSelection`）走的就是这个入口，一并挡住。
            if (owner != null && !owner.AllowEdit())
            {
                // 把原因回传（**文案取自同一处**）：热键处理要靠"有没有原因"决定
                // 吃不吃掉这个键，见 `EditorPlaybackBridge` 里那段。
                // 提示那边 `AllowEdit` 已经发过了。
                status = EditorStudioOverlay.ReadOnlyEditMessage;
                return false;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.events == null)
            {
                return false;
            }

            // **删除要用不滤装饰物的那份选集。**
            //
            // `CollectSelection()` 会把装饰物（`AddDecoration` / `AddText`）整个滤掉，
            // 指望它们由游戏那侧的 `selectedDecorations` 单独交上来。可那份只有
            // "在时间轴上直接点一下"才会被 `SyncSceneSelection` 填上 ——
            // **框选和 Ctrl 点选都不填**，于是那些装饰物块看着选中了，按 Delete
            // 却一个都不删。
            //
            // `RemoveSelection` 本来就会按"目标实际躺在 events 还是 decorations 里"
            // 分类，所以这里直接把全都交给它，重复的它自己会去重。
            List<LevelEvent> targets = PanelSelection();

            List<LevelEvent> decorations = new List<LevelEvent>();
            if (editor.selectedDecorations != null)
            {
                for (int i = 0; i < editor.selectedDecorations.Count; i++)
                {
                    if (editor.selectedDecorations[i] != null)
                    {
                        decorations.Add(editor.selectedDecorations[i]);
                    }
                }
            }

            if (targets.Count == 0 && decorations.Count == 0)
            {
                return false;
            }

            int removed;
            if (!EditorCommandHistory.RemoveSelection(editor, targets, decorations, out removed))
            {
                return false;
            }

            // 装饰物已经不在谱面里了，原生那份选中还指着它们 —— 不清掉
            // 面板会拿着一批死引用继续显示。
            if (editor.selectedDecorations != null)
            {
                editor.selectedDecorations.Clear();
            }
            multiSelection.Clear();
            selectedEvent = null;
            visualsDirty = true;
            status = "已删除 " + removed + " 项";
            return true;
        }

        internal int CopySelection()
        {
            return EditorTimelineClipboard.Copy(CollectSelection());
        }

        /// <summary>剪切 = 复制 + 删除。复制没成功就不删 —— 否则东西没了还没得粘。</summary>
        internal bool CutSelection(out string status)
        {
            status = null;
            int copied = CopySelection();
            if (copied == 0)
            {
                return false;
            }

            string deleteStatus;
            if (!DeleteSelection(out deleteStatus))
            {
                return false;
            }

            status = "已剪切 " + copied + " 个事件";
            return true;
        }

        /// <summary>
        /// 把剪贴板粘到**播放头所在格**。
        ///
        /// 落点用播放头而不是鼠标：位置是可预期的，而鼠标在拖动/滚动时
        /// 会飘。事件表用 `seqID` 当格号（原生 AddEventAtSelected 就是
        /// 拿 `selectedFloors[0].seqID` 当 floor 的）。
        /// </summary>
        internal bool PasteAtPlayhead(out string status)
        {
            status = null;

            // 只读模式：粘贴会往谱面里加事件，拦住（原因回传，见删除那条的说明）。
            if (owner != null && !owner.AllowEdit())
            {
                status = EditorStudioOverlay.ReadOnlyEditMessage;
                return false;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.events == null || editor.floors == null
                || editor.floors.Count == 0 || !EditorTimelineClipboard.HasContent)
            {
                return false;
            }

            int floorIndex = Mathf.Clamp(
                LowerBoundFloors(editor.floors,
                    EditorPlaybackBridge.ReadSnapshot().CurrentTime) - 1,
                0, editor.floors.Count - 1);
            int seq = editor.floors[floorIndex].seqID;

            int firstIndex = editor.events.Count;
            List<LevelEvent> created = EditorTimelineClipboard.Paste(editor, seq);
            if (created.Count == 0)
            {
                return false;
            }

            EditorCommandHistory.RecordEventCreates(editor, created, firstIndex);

            // 粘完就选中它们 —— 用户要能立刻看见粘到了哪、接着改。
            multiSelection.Clear();
            for (int i = 0; i < created.Count; i++)
            {
                multiSelection.Add(created[i]);
            }
            selectedEvent = created[0];
            visualsDirty = true;

            status = "已粘贴 " + created.Count + " 个事件到第 " + seq + " 格";
            return true;
        }

        internal void Initialize(RectTransform timelineRoot, TMP_FontAsset timelineFont, EditorStudioOverlay timelineOwner)
        {
            owner = timelineOwner;
            root = timelineRoot;
            if (!laneFilterStateInitialized)
            {
                laneFilterStateInitialized = true;
                for (int i = 0; i < LaneCount; i++)
                {
                    laneVisible[i] = true;
                }
            }
            font = timelineFont;
            rowHeight = Mathf.Clamp(
                Main.Settings == null ? DefaultRowHeight : Main.Settings.TimelineRowHeight,
                MinimumRowHeight,
                MaximumRowHeight
            );
            if (Main.Settings != null)
            {
                Main.Settings.TimelineRowHeight = rowHeight;
            }
            for (int i = 0; i < laneItems.Length; i++)
            {
                laneItems[i] = new List<TimelineItem>();
            }

            roundedSprite = CreateRoundedSprite();
            outlineSprite = CreateOutlineSprite();
            BuildUi();
            viewInitialized = true;
            // 第一张谱排完版后，横向缩放直接给到最大（见 `pendingZoomToMaximum`）。
            pendingZoomToMaximum = true;
            UpdateZoomText();
            RefreshData(true);
        }

        private void OnDestroy()
        {
            anyPointerCapture = false;
            // 装饰物列表的批量隐藏/锁定只改运行时对象，销毁前必须让它收回去
            // （它的 OnDestroy 会处理，但那个组件的 GameObject 是 root 的子节点，
            // 顺序上不保证先于这里）。
            decoPane = null;
            EditorPlaybackBridge.CancelTimelineScrub();
            if (roundedSprite != null)
            {
                Destroy(roundedSprite);
            }
            if (roundedTexture != null)
            {
                Destroy(roundedTexture);
            }
            if (outlineSprite != null)
            {
                Destroy(outlineSprite);
            }
            if (outlineTexture != null)
            {
                Destroy(outlineTexture);
            }

            // 拖动指针交给 Cursor 之后，Unity 不持有它；不在这里销毁
            // 就是每次重建工作台泄漏一张纹理。它挂在静态字段上，
            // 所以销毁后要把引用也清掉，否则下次会去用一个已销毁的对象。
            if (resizeCursorTexture != null)
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                Destroy(resizeCursorTexture);
                resizeCursorTexture = null;
                cursorIsResize = false;
            }
        }

        private void OnDisable()
        {
            scrubbing = false;
            middleDragging = false;
            scrubPending = false;
            blockingPointerDrag = false;
            anyPointerCapture = false;
            EditorPlaybackBridge.CancelTimelineScrub();
        }

        internal bool ContainsScreenPoint(Vector2 screenPosition)
        {
            return root != null
                && gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(root, screenPosition, null);
        }

        internal bool IsCapturingPointer
        {
            get { return scrubbing || middleDragging || blockingPointerDrag; }
        }

        internal void Relayout()
        {
            if (root == null || viewport == null)
            {
                return;
            }

            float rootWidth = Mathf.Max(800f, root.rect.width);
            float rootHeight = Mathf.Max(220f, root.rect.height);
            // 宽度也得重算。放置事件面板按可用宽度决定按钮大小、以及
            // 要不要多占一行，而 UI 刚建好那一帧 root 的宽度往往还不是
            // 最终值。只认高度变化的话，那次偏差会一直留着，直到用户
            // 缩放窗口高度才被纠正——表现出来就是面板「要点一下才出来」。
            lastRootWidth = rootWidth;
            lastRootHeight = rootHeight;
            bodyWidth = rootWidth - (GutterWidth + ContentShift) - 10f;
            bodyHeight = rootHeight - headerHeight - 8f;

            SetTopLeft(viewport, GutterWidth + ContentShift, headerHeight, bodyWidth, bodyHeight);

            SetTopLeft(markerLayer, 0f, 0f, bodyWidth, bodyHeight);
            SetTopLeft(rulerBackdrop, 0f, 0f, bodyWidth, RulerHeight);

            // 工具区高度可能因为面板换行而变过，按钮要重新摆。
            RefreshEventPalette();

            ClampViewStart();
            ClampVerticalScroll();
            visualsDirty = true;
        }

        /// <summary>
        /// 面板每帧自检尺寸。以前只比高度，宽度变了不重排——而 UI 第一次
        /// 构建时 root 的宽度常常还是默认值，于是放置事件面板按错的可用
        /// 宽度算了一遍按钮大小就再没人管，看着像「不点一下不显示」。
        /// </summary>
        private void RelayoutIfResized()
        {
            float width = Mathf.Max(800f, root.rect.width);
            float height = Mathf.Max(220f, root.rect.height);
            if (!Mathf.Approximately(lastRootWidth, width)
                || !Mathf.Approximately(lastRootHeight, height))
            {
                Relayout();
            }
        }

        private void Update()
        {
            if (root == null || viewport == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            // 供预览层判断"现在是拖动定位"：那期间不写粒子装饰物的位置，
            // 否则原生 SetPosition 每帧清一次粒子，看着就是冻住。
            ParticleWatch.ScrubbingActive = scrubbing || centerBrowseScrubbing
                || centeredScrubActive;
            RelayoutIfResized();
            DiagCheckStuckBlockDrag();
            HandleKeyboardShortcuts();
            RefreshFeatureButtons();
            RefreshLaneButtons();
            // **块数据每帧重建。**
            CollectBlocks();
            UpdateBlocks();
            UpdateRuler();

            // 拖播放头期间每帧把粒子放起来：定位会重建装饰物，
            // 只靠"提交后放一次"仍有空档（用户反馈会卡住）。
            if (scrubbing)
            {
                ParticleWatch.ResumeAll();
            }
            TickEdgeAutoScroll();
            // 拖块贴住视口上下边缘时自动纵向滚动（横向那套在 TickEdgeAutoScroll）。
            TickDragVerticalAutoScroll();
            if (scrubPending && now >= nextScrubSubmitTime)
            {
                SubmitPendingScrub(false);
            }
            if (now >= nextDataRefresh)
            {
                RefreshData(false);
                nextDataRefresh = now + DataRefreshInterval;
                if (decoPane != null)
                {
                    decoPane.MarkDataDirty();
                }
            }
            if (now >= nextSelectionRefresh)
            {
                int fingerprint = ComputeSelectionFingerprint();
                if (fingerprint != selectionFingerprint)
                {
                    selectionFingerprint = fingerprint;
                    visualsDirty = true;
                }
                if (decoPane != null)
                {
                    decoPane.MarkSelectionDirty();
                }
                nextSelectionRefresh = now + SelectionRefreshInterval;
            }

            EditorPlaybackSnapshot snapshot = EditorPlaybackBridge.ReadSnapshot();
            totalDuration = Mathf.Max(totalDuration, snapshot.Duration);
            float visibleDuration = bodyWidth / Mathf.Max(1f, pixelsPerSecond);
            navigationDuration = Mathf.Max(navigationDuration, totalDuration, snapshot.CurrentTime);
            if (snapshot.State == EditorPlaybackState.Playing)
            {
                navigationDuration = Mathf.Max(navigationDuration, snapshot.CurrentTime + visibleDuration);
            }

            // 固定播放头下的 Shift+滚轮：**和自由模式同一套欠账式平滑**
            // （同样的 `ScrollSmoothSpeed`），所以速度和滑停时长一致。
            if (centeredScrubActive)
            {
                if (Mathf.Abs(centeredScrubPending) > 0.0002f)
                {
                    float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * ScrollSmoothSpeed);
                    float move = centeredScrubPending * k;
                    float scrubNow = snapshot.CurrentTime;
                    float scrubNext = Mathf.Clamp(scrubNow + move, 0f,
                        Mathf.Max(0f, navigationDuration));
                    if (Mathf.Abs(scrubNext - (scrubNow + move)) > 0.00001f)
                    {
                        // 撞到谱面头/尾：欠账作废，别继续"想"走。
                        centeredScrubPending = 0f;
                    }
                    else
                    {
                        centeredScrubPending -= move;
                        if (Mathf.Abs(centeredScrubPending) < 0.0002f)
                        {
                            centeredScrubPending = 0f;
                        }
                    }
                    pendingScrubTime = scrubNext;
                    scrubPending = true;
                    nextScrubSubmitTime = 0f;
                }
                else
                {
                    centeredScrubActive = false;
                    SubmitPendingScrub(true);
                    EditorPlaybackBridge.EndTimelineScrub();
                    ParticleWatch.ResumeAll();
                }
            }

            // 固定播放头：**播放中每帧精确摆正，其它时候缓动滑回。**
            //
            // 为什么分两种：一直用缓动的话，播放时视图会恒定落后一点点
            // （实测 533px/s 下落后约 53px，看起来就是"没固定在中间"）；
            // 一直硬摆的话，切换模式 / 手动横滚回位会"啪"地跳。
            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                float centeredTarget = CenteredViewTarget();
                // 三种情况必须**精确摆正**（不能缓动）：
                //   · 正在播放；
                //   · Shift+滚轮扫时间；
                //   · 中键横拖扫时间（`centerBrowseScrubbing`）。
                // 后两者播放头是每帧被推着走的，缓动跟不上 —— 表现出来就是
                // "播放头先左右飘、松手后再慢慢回到中间"。
                bool playing = snapshot.State == EditorPlaybackState.Playing
                    || centeredScrubActive
                    || centerBrowseScrubbing;
                if (playing)
                {
                    if (Mathf.Abs(viewStartTime - centeredTarget) > 0.0001f)
                    {
                        viewStartTime = centeredTarget;
                        ClampCenteredViewStart();
                        visualsDirty = true;
                    }
                }
                else if (Time.unscaledTime >= centeredHoldUntil
                    && Mathf.Abs(viewStartTime - centeredTarget) > 0.0005f)
                {
                    float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * CenterLerpSpeed);
                    viewStartTime = Mathf.Lerp(viewStartTime, centeredTarget, k);
                    ClampCenteredViewStart();
                    visualsDirty = true;
                }
                // 固定播放头自己不产生横向欠账：位置由上面每帧算死。
                horizontalScrollPending = 0f;
            }

            // 「定位到播放头」的滑动（自由模式用；固定模式由上面的缓动代劳）。
            if (viewGliding)
            {
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * CenterLerpSpeed);
                viewStartTime = Mathf.Lerp(viewStartTime, viewGlideTarget, k);
                ClampViewStart();
                visualsDirty = true;
                if (Mathf.Abs(viewStartTime - viewGlideTarget) < 0.002f)
                {
                    viewStartTime = viewGlideTarget;
                    ClampViewStart();
                    viewGliding = false;
                }
            }

            ApplyScrollSmoothing();

            if (visualsDirty)
            {
                RefreshGeometry();
            }

            UpdatePlayhead(snapshot);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!ContainsScreenPoint(eventData.position) || Mathf.Approximately(eventData.scrollDelta.y, 0f))
            {
                return;
            }

            // **拖动事件块期间，滚轮一概不参与滚动。**
            //
            // 拖动中滚一下滚轮，分节卷帘会把行挪走，而被拖的块是按"光标 − 抓取偏移"
            // 摆的、不跟着挪 —— 于是它当场和轨道错位，看着像"拖动被打断了"。
            // 这里把滚轮整个让给拖动（Ctrl 缩放、Alt 行高那几个也一并停掉，
            // 免得拖到一半整个视图跳一下）。
            if (blockDragActive)
            {
                return;
            }

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            ScrollBy(eventData.scrollDelta.y, control, alt, shift);
            eventData.Use();
        }

        /// <summary>
        /// 滚轮的核心。**和 OnScroll 是同一份实现**，只是不经过 uGUI 的
        /// `PointerEventData` —— ImGui 接管之后滚轮是它给的。
        ///
        /// 原来这里还有一支"在分类框盒子里滚"的分支，随分类卷帘模式一起删了。
        /// </summary>
        /// <summary>
        /// 拖动中「贴边自动横滚」用：把一个像素位移加进视图起点。
        ///
        /// **刻意不走 `PanBy`** —— 那个在居中模式下会把横向平移翻译成
        /// scrubbing（改当前时间）。这里只是想看看更远的地方，
        /// 不该顺手把谱面的时间也改了。
        /// </summary>
        /// <summary>uGUI 那条路正在拖块吗（探针用）。</summary>
        /// <summary>当前能不能改谱面（只读模式返回 false，并弹统一提示）。</summary>
        internal bool CanEditChart()
        {
            return owner == null || owner.AllowEdit();
        }

        internal bool BlockDragActive
        {
            get { return blockDragActive; }
        }

        /// <summary>
        /// 当前纵向滚动偏移（像素）。
        ///
        /// 拖动中要读它：行号是按「鼠标位移」算的，而滚动会把整块画布挪走 ——
        /// 不把这个偏移补偿掉，鼠标原地不动、块也会越滚越偏。
        /// </summary>
        internal float VerticalScrollOffset
        {
            get { return verticalScrollOffset; }
        }

        /// <summary>纵向最多能滚多少（0 = 一屏放得下、没得滚）。探针用。</summary>
        internal float MaxVerticalScroll
        {
            get { return GetMaximumVerticalScroll(); }
        }

        /// <summary>
        /// 被拖那一批按下时的**时间**（取第一个 = 鼠标真正抓着的那块）。
        ///
        /// 拖动中在屏幕上竖一条线标出它：横向是按格吸附的，块和鼠标会一段一段
        /// 错开，光看块本身分不清"离开原来的时间多远了"。
        /// </summary>
        internal float DraggedOriginTime
        {
            get
            {
                for (int i = 0; i < imBlockDragOrigins.Count; i++)
                {
                    if (imBlockDragOrigins[i].Item != null)
                    {
                        return imBlockDragOrigins[i].Start;
                    }
                }
                return 0f;
            }
        }

        /// <summary>
        /// 被拖那一批按下时的行号（取第一个 = 鼠标真正抓着的那块）。
        ///
        /// 拖动时拿它和"鼠标现在指在第几行"作差 —— 比按鼠标位移算稳，
        /// 因为滚动、缩放都会改视口，位移那个基准会跟着飘。
        /// </summary>
        internal int DraggedOriginRow
        {
            get
            {
                for (int i = 0; i < imBlockDragOrigins.Count; i++)
                {
                    if (imBlockDragOrigins[i].Item != null)
                    {
                        return imBlockDragOrigins[i].Row;
                    }
                }
                return 0;
            }
        }

        internal void ScrollBy(float wheelDelta, bool control, bool alt, bool shift)
        {
            if (Mathf.Approximately(wheelDelta, 0f))
            {
                return;
            }

            // **拖动中只放开纵向滚动。**
            //
            // 长事件往右铺得远，拖的时候本来就要上下找行，一律禁掉太难用。
            // 但缩放 / 行高 / 横向那三条**不能放开** —— 它们会改坐标的基准
            // （每秒多少像素、行多高、视图起点），而拖动中的行号是拿
            // 「起点 + 位移」现算的，基准一动，鼠标下的那块就跳。
            // 纵向滚动只挪视口、不参与行号换算，所以它是安全的那个。
            if (blockDragActive)
            {
                if (control || alt || shift)
                {
                    return;
                }
                AddVerticalScrollDelta(-wheelDelta * rowHeight * 1.7f);
                return;
            }

            if (control)
            {
                ZoomAt(wheelDelta > 0f ? 1.25f : 0.8f, bodyWidth * 0.5f);
            }
            else if (alt)
            {
                AdjustRowHeight(wheelDelta > 0f ? 1f : -1f);
            }
            else if (shift)
            {
                // 只有按住 Shift 才横滚。固定播放头下也滚 —— 先把手动位置
                // 交给用户，停手一会儿再缓动滑回中间（不让每帧的缓动抢手）。
                float pixels = wheelDelta * 150f;
                if (followMode == TimelineFollowMode.CenteredPlayhead)
                {
                    // **固定播放头下横滚 = 挪时间**，视图不挪 —— 播放头始终钉在正中。
                    // 只把"还要走多少秒"记进欠账，推进交给 Update 里的指数逼近，
                    // 参数和自由模式的滚动同一个 `ScrollSmoothSpeed`。
                    if (!centeredScrubActive)
                    {
                        centeredScrubPending = 0f;
                        centeredScrubActive = true;
                        string scrubStatus;
                        EditorPlaybackBridge.TryBeginTimelineScrub(out scrubStatus);
                    }
                    centeredScrubPending -= pixels / Mathf.Max(1f, pixelsPerSecond);
                    MarkManualNavigation();
                }
                else
                {
                    AddHorizontalScrollDelta(-pixels);
                }
            }
            else
            {
                // **普通滚轮一律纵向滚动。**
                //
                // 原来这里多了一条 `|| GetMaximumVerticalScroll() <= 0.01f`：
                // 纵向没有可滚空间时改滚横向。但主体改 uGUI 之后，纵向可滚
                // 高度一度算成 0，于是普通滚轮全被当成横滚 —— 表现就是
                // 「往上滚，画面往左跑」。纵向没得滚时就应该什么都不动。
                AddVerticalScrollDelta(-wheelDelta * rowHeight * 1.7f);
            }

            visualsDirty = true;
        }

        /// <summary>
        /// 纵向滚轮：**只记欠账，不产生惯性**。
        ///
        /// 滚一格记一笔"还要走多少像素"，由 `ApplyScrollSmoothing` 按指数逼近
        /// 花掉；花完即停。这样既不顿（不是一格一跳），也不会像惯性那样松手
        /// 之后还滑几秒（触控板会连发动量事件，速度叠起来停不下来）。
        /// </summary>
        private void AddVerticalScrollDelta(float pixels)
        {
            verticalScrollPending += pixels;
            visualsDirty = true;
        }

        /// <summary>横向滚轮：同上，单位是像素，落到 `viewStartTime` 时再换成秒。</summary>
        private void AddHorizontalScrollDelta(float pixels)
        {
            viewGliding = false;
            horizontalScrollPending += pixels;
            MarkManualNavigation();
            visualsDirty = true;
        }

        /// <summary>
        /// 每帧把"欠账"按指数逼近花掉：`move = pending · (1 − e^(−22·dt))`。
        ///
        /// 到点即停（剩余不足 0.01 直接抹零），所以**松手就是停**，
        /// 不会像惯性那样还往前滑。撞到边界也立刻清零，免得贴着边抖。
        /// </summary>
        private void ApplyScrollSmoothing()
        {
            float delta = Time.unscaledDeltaTime;
            if (delta <= 0f)
            {
                return;
            }
            float k = 1f - Mathf.Exp(-delta * ScrollSmoothSpeed);
            bool dirty = false;

            if (Mathf.Abs(verticalScrollPending) > 0.01f)
            {
                float move = verticalScrollPending * k;
                float before = verticalScrollOffset;
                verticalScrollOffset += move;
                ClampVerticalScroll();
                verticalScrollPending -= move;
                if (Mathf.Abs(before - verticalScrollOffset) < 0.001f)
                {
                    // 顶到边界：欠账作废，别让它一直"想"滚。
                    verticalScrollPending = 0f;
                }
                if (Mathf.Abs(verticalScrollPending) < 0.01f)
                {
                    verticalScrollPending = 0f;
                }
                dirty = true;
            }

            if (Mathf.Abs(horizontalScrollPending) > 0.01f)
            {
                float move = horizontalScrollPending * k;
                float before = viewStartTime;
                viewStartTime += move / Mathf.Max(1f, pixelsPerSecond);
                ClampViewStart();
                horizontalScrollPending -= move;
                if (Mathf.Abs(before - viewStartTime) < 0.000001f)
                {
                    horizontalScrollPending = 0f;
                }
                if (Mathf.Abs(horizontalScrollPending) < 0.01f)
                {
                    horizontalScrollPending = 0f;
                }
                dirty = true;
            }

            if (dirty)
            {
                visualsDirty = true;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if ((eventData.button != PointerEventData.InputButton.Left
                    && eventData.button != PointerEventData.InputButton.Middle)
                || scrubbing)
            {
                return;
            }

            blockingPointerDrag = true;
            anyPointerCapture = true;
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            float x;
            float y;
            if (!TryGetViewportPoint(eventData.position, out x, out y))
            {
                return;
            }

            // **Ctrl + 左键 = 框选。** 放在标尺/播放头判断之前：按住 Ctrl 时
            // 整块区域都归框选用。
            //
            // 注意这里只在**从空白处**按下时才走得到。块身上挂着 Button，
            // 而 Button 自己实现了 IPointerDownHandler —— 从块上按下时事件被
            // 它吃掉，视图这条路不参与。块上按 Ctrl 的情形是"点击切换多选"，
            // 在 ActivateMarker 里处理（见 ToggleSelectionOf 的说明）。
            if (MarqueeModifierHeld)
            {
                BeginMarquee(new Vector2(x, y));
                eventData.Use();
                return;
            }

            EditorPlaybackSnapshot snapshot = EditorPlaybackBridge.ReadSnapshot();
            float playheadX = (snapshot.CurrentTime - viewStartTime) * pixelsPerSecond;
            bool rulerHit = y >= 0f && y <= RulerHeight;
            bool playheadHit = playheadX >= 0f && playheadX <= bodyWidth && Mathf.Abs(x - playheadX) <= 7f;
            if (!rulerHit && !playheadHit)
            {
                // **点空白 = 清除全部选中。**
                //
                // 这里原来什么都不做，于是选了一堆之后想全清掉，只能去点
                // 另一个块（那是"替换选择"、还留着那一个），或者去别处找。
                // 点在空处取消是最基本的习惯。
                //
                // 中键和 Ctrl 都已经在上面各自出去了（中键平移、Ctrl 框选），
                // 走到这里的只剩"不带修饰键的左键点在空处"。
                ClearAllSelection();
                return;
            }

            string status;
            if (!EditorPlaybackBridge.TryBeginTimelineScrub(out status))
            {
                return;
            }

            scrubbing = true;
            lastSubmittedScrubTime = float.NaN;
            scrubPointerX = x;
            scrubPointerValid = true;
            QueueScrubAtX(x);
            eventData.Use();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left
                && eventData.button != PointerEventData.InputButton.Middle)
            {
                return;
            }

            // 框选收尾。松手那一帧要用最终位置，所以先更新再做判定 ——
            // 否则"拉出去又收回来"会按上一帧的框去选。
            if (marqueeActive && eventData.button == PointerEventData.InputButton.Left)
            {
                Vector2 marqueePoint;
                if (TryGetViewportPoint(eventData.position, out marqueePoint))
                {
                    marqueeCurrent = marqueePoint;
                }
                FinishMarquee();
                blockingPointerDrag = false;
                anyPointerCapture = false;
                eventData.Use();
                return;
            }

            blockingPointerDrag = false;
            if (eventData.button == PointerEventData.InputButton.Middle)
            {
                middleDragging = false;
                anyPointerCapture = scrubbing;
                return;
            }
            anyPointerCapture = scrubbing || middleDragging;
            if (!scrubbing)
            {
                return;
            }

            float x;
            float ignoredY;
            if (TryGetViewportPoint(eventData.position, out x, out ignoredY))
            {
                QueueScrubAtX(x);
            }
            FinishScrub();
            eventData.Use();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (scrubbing && eventData.button == PointerEventData.InputButton.Left)
            {
                eventData.Use();
                return;
            }
            middleDragging = eventData.button == PointerEventData.InputButton.Middle && ContainsScreenPoint(eventData.position);

            // **固定播放头下中键横拖 = 扫时间**，不是平移视图。
            // 线钉在屏幕上、内容从它下面过，所以"抓住内容往右拖"等价于
            // "把播放头挪到更早的时间"——和剪辑软件拖时间轴同一种手感。
            // 必须真的进一次 scrub 状态：`TryScrubTimelineToTime` 开头就检查
            // `timelineScrubActive`，不进状态的话每次提交都被静默拒绝（拖不动）。
            if (middleDragging && followMode == TimelineFollowMode.CenteredPlayhead)
            {
                string browseStatus;
                if (EditorPlaybackBridge.TryBeginTimelineScrub(out browseStatus))
                {
                    centerBrowseScrubbing = true;
                    centerBrowseStartTime = EditorPlaybackBridge.ReadSnapshot().CurrentTime;
                    centerBrowseAccum = 0f;
                }
            }

            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                anyPointerCapture = true;
                MarkManualNavigation();
                eventData.Use();
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            // 框选优先：拉框中时其它拖动都不参与。
            if (marqueeActive && eventData.button == PointerEventData.InputButton.Left)
            {
                Vector2 marqueePoint;
                if (TryGetViewportPoint(eventData.position, out marqueePoint))
                {
                    marqueeCurrent = marqueePoint;
                    UpdateMarqueeBand();
                }
                eventData.Use();
                return;
            }

            if (scrubbing && eventData.button == PointerEventData.InputButton.Left)
            {
                float x;
                float ignoredY;
                if (TryGetViewportPoint(eventData.position, out x, out ignoredY))
                {
                    scrubPointerX = x;
                    scrubPointerValid = true;
                    QueueScrubAtX(x);
                }
                eventData.Use();
                return;
            }
            if (!middleDragging)
            {
                return;
            }

            if (centerBrowseScrubbing)
            {
                // 固定播放头：横拖改时间（走 scrub 提交，预览画面才跟着走）
                centerBrowseAccum -= eventData.delta.x / Mathf.Max(1f, pixelsPerSecond);
                float browseTime = Mathf.Clamp(
                    centerBrowseStartTime + centerBrowseAccum,
                    0f,
                    Mathf.Max(0f, navigationDuration));
                pendingScrubTime = browseTime;
                scrubPending = true;
                nextScrubSubmitTime = 0f;
            }
            else
            {
                viewGliding = false;
                viewStartTime -= eventData.delta.x / Mathf.Max(1f, pixelsPerSecond);
                ClampViewStart();
            }
            verticalScrollOffset += eventData.delta.y;
            ClampVerticalScroll();
            MarkManualNavigation();
            visualsDirty = true;
            eventData.Use();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (scrubbing && eventData.button == PointerEventData.InputButton.Left)
            {
                blockingPointerDrag = false;
                scrubPointerValid = false;
                FinishScrub();
                ParticleWatch.ResumeAll();
                anyPointerCapture = middleDragging;
                eventData.Use();
                return;
            }
            if (middleDragging)
            {
                eventData.Use();
            }
            if (centerBrowseScrubbing)
            {
                // 固定播放头下中键扫时间：这里收尾，提交最后一次定位并退出
                // scrub 状态 —— 否则 `timelineScrubActive` 一直挂着，
                // 后续所有定位提交都会被静默拒绝。
                centerBrowseScrubbing = false;
                SubmitPendingScrub(true);
                EditorPlaybackBridge.EndTimelineScrub();
                ParticleWatch.ResumeAll();
            }
            middleDragging = false;
            blockingPointerDrag = false;
            anyPointerCapture = false;
        }

        private void HandleKeyboardShortcuts()
        {
            if (!ContainsScreenPoint(Input.mousePosition)
                || owner == null || owner.IsTextInputFocused())
            {
                return;
            }

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool plus = Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
            bool minus = Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
            bool zero = Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0);
            bool saveKey = Input.GetKeyDown(KeyCode.S);

            // **单独的排版保存：Ctrl+Alt+S**（Ctrl+S 已经同时存谱面 + 排版了，
            // 这里留一个只写 `.aesprj` 的入口，方便单独验证排版）。
            //
            // 挂 `Ctrl+Shift+S` 是不行的：反编译 `scnEditor` 里注册的是
            // `Ctrl+Shift+S → SaveLevelAs`（另存谱面），按下去会同时触发两件事。
            if (control && alt && saveKey)
            {
                SaveProjectInteractive();
                return;
            }

            if (control && zero)
            {
                SetRowHeight(DefaultRowHeight, true);
                ResetHorizontalView();
            }
            else if (control && plus)
            {
                ZoomAt(1.25f, GetPointerAnchorX(Input.mousePosition));
            }
            else if (control && minus)
            {
                ZoomAt(0.8f, GetPointerAnchorX(Input.mousePosition));
            }
            else if (alt && plus)
            {
                AdjustRowHeight(1f);
            }
            else if (alt && minus)
            {
                AdjustRowHeight(-1f);
            }
        }

        private void AdjustRowHeight(float direction)
        {
            SetRowHeight(rowHeight + direction * RowHeightStep, true);
        }

        private void SetRowHeight(float value, bool persist)
        {
            float requested = Mathf.Clamp(value, MinimumRowHeight, MaximumRowHeight);
            bool changed = false;
            if (!Mathf.Approximately(requested, rowHeight))
            {
                // 行位置存在行表里，而行表是按行高
                // 构建的，所以放大行必须重跑布局。只把
                // 视觉标脏，会用旧间距画更高的块，
                // 让每个分类框都压到下面那个身上。
                //
                // **比例的分母要用"现在的内容高度"。** 原来用的是
                // `layoutTotalHeight` —— 那是旧排版模型的字段，重建那一段删掉之后
                // 就再也没被赋过值、恒为 0，于是比例恒为 0、每按一次 Alt+滚轮
                // 滚动位置都被拽回顶部（人看起来就是"调行高把视图弹飞了"）。
                float contentBefore = BlockContentHeight;
                float scrollRatio = contentBefore <= 0f
                    ? 0f
                    : Mathf.Clamp01(verticalScrollOffset / contentBefore);
                rowHeight = requested;
                changed = true;
                layoutDirty = true;
                visualsDirty = true;

                // 让光标下始终是谱面的同一处，而不是保持同一个
                // 行号——行号已经不再对应固定位置了。
                verticalScrollOffset = scrollRatio * BlockContentHeight;
            // 改行高时位置是重算出来的，把滚轮的欠账清掉，否则两边抢。
            verticalScrollPending = 0f;
                if (Main.Settings != null)
                {
                    Main.Settings.TimelineRowHeight = rowHeight;
                }
            }

            if (persist && changed)
            {
                Main.SaveSettings();
            }
        }

        private float GetPointerAnchorX(Vector2 screenPosition)
        {
            float x;
            float ignoredY;
            return TryGetViewportPoint(screenPosition, out x, out ignoredY)
                ? x
                : bodyWidth * 0.5f;
        }

        private bool TryGetViewportPoint(Vector2 screenPosition, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (viewport == null)
            {
                return false;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, screenPosition, null, out local))
            {
                return false;
            }

            x = Mathf.Clamp(local.x + viewport.rect.width * viewport.pivot.x, 0f, bodyWidth);
            y = -local.y + viewport.rect.height * (1f - viewport.pivot.y);
            return true;
        }

        /// <summary>边缘多宽算「贴边」。</summary>
        private const float EdgeScrollZone = 56f;
        /// <summary>顶到最边上时的最大滚动速度（像素/秒）。</summary>
        private const float EdgeScrollMaxSpeed = 1400f;

        /// <summary>
        /// 拖动播放头时，指针贴近视口左右边缘就让时间轴朝那个方向持续滚动。
        ///
        /// 很多剪辑软件都有这个：把播放头拖到边上不用松手，视图自己往前走，
        /// 挪到很远的时刻不必反复「拖—松—滚—再拖」。
        ///
        /// 速度按指针进入边缘区的深度平方递增：刚进去是微调，顶到边上才最快。
        /// 否则一碰边就全速，很难停在想要的位置。
        ///
        /// 只在「自由播放头」+ 拖播放头时生效：固定播放头模式下视图是
        /// 播放头的从属量，再叠一层自动滚动会互相打架。
        /// </summary>
        private void TickEdgeAutoScroll()
        {
            if (!scrubbing || !scrubPointerValid
                || followMode == TimelineFollowMode.CenteredPlayhead)
            {
                return;
            }

            float direction = 0f;
            if (scrubPointerX < EdgeScrollZone)
            {
                direction = -1f;
            }
            else if (scrubPointerX > bodyWidth - EdgeScrollZone)
            {
                direction = 1f;
            }
            if (direction == 0f)
            {
                return;
            }

            // 0（刚进边缘区）→ 1（顶到最边）
            float depth = direction < 0f
                ? 1f - Mathf.Clamp01(scrubPointerX / EdgeScrollZone)
                : Mathf.Clamp01((scrubPointerX - (bodyWidth - EdgeScrollZone)) / EdgeScrollZone);
            float speed = EdgeScrollMaxSpeed * depth * depth;

            float before = viewStartTime;
            viewStartTime += direction * speed * Time.unscaledDeltaTime / Mathf.Max(1f, pixelsPerSecond);
            ClampViewStart();
            if (!Mathf.Approximately(before, viewStartTime))
            {
                // 视图挪了，播放头也要跟上，否则它会停在后面不动。
                QueueScrubAtX(scrubPointerX);
                MarkManualNavigation();
                visualsDirty = true;
            }
        }

        private void QueueScrubAtX(float x)
        {
            pendingScrubTime = Mathf.Clamp(
                viewStartTime + Mathf.Clamp(x, 0f, bodyWidth) / Mathf.Max(1f, pixelsPerSecond),
                0f,
                Mathf.Max(0f, navigationDuration)
            );
            scrubPending = true;
            manualNavigationUntil = float.PositiveInfinity;
        }

        private void SubmitPendingScrub(bool force)
        {
            if (!scrubPending && !force)
            {
                return;
            }

            if (!float.IsNaN(lastSubmittedScrubTime)
                && Mathf.Abs(lastSubmittedScrubTime - pendingScrubTime) < 0.001f)
            {
                scrubPending = false;
                return;
            }

            string ignored;
            if (EditorPlaybackBridge.TryScrubTimelineToTime(pendingScrubTime, out ignored))
            {
                lastSubmittedScrubTime = pendingScrubTime;
                // 每个渲染帧的 Update 只提交一次。播放桥会做一次
                // 只含视觉的精确采样，所以指针慢速移动也能保持顺滑。
                nextScrubSubmitTime = Time.unscaledTime;
                // 每次定位后立刻把粒子放起来：拖动过程中也会被清掉，
                // 只在松手时恢复的话，看起来就是「拖着的时候粒子停了」。
                ParticleWatch.ResumeAll();
            }
            scrubPending = false;
        }

        private void FinishScrub()
        {
            SubmitPendingScrub(true);
            scrubbing = false;
            scrubPending = false;
            blockingPointerDrag = false;
            anyPointerCapture = middleDragging;
            manualNavigationUntil = Time.unscaledTime + 2.4f;
            EditorPlaybackBridge.EndTimelineScrub();
        }

        /// <summary>标题行的时间读数（"00:00.000 / 00:00.000"）。</summary>
        internal string TimeReadout        {
            get { return timeText == null ? null : timeText.text; }
        }

        /// <summary>标题行中段那条摘要（事件数、当前格之类，每秒被覆写一次）。</summary>
        internal string SummaryReadout
        {
            get { return summaryText == null ? null : summaryText.text; }
        }

        /// <summary>缩放读数的当前文字（"100%"）。</summary>
        internal string ZoomReadout
        {
            get { return zoomText == null ? null : zoomText.text; }
        }

        internal void GoToStart() { ReturnToStart(); }

        // ------------------------------------------------- ImGui 版车道区的口子
        //
        // 只摊平**静态层**（分类表头 + 每一行），事件块那层留给后面的堆叠方案。

        /// <summary>
        /// 内容空间 → 视口内 Y 的那个偏移，**行内所有元素都用它**。
        ///
        /// 位置是 `RulerHeight + 内容Y − 滚动偏移`：标尺占着视口顶部那一条，
        /// 所以内容空间的 Y 是从它下面量起的。少了标尺那一项，整层会高一个
        /// 标尺高度、压在标尺上（uGUI 那边踩过这个坑，注释里记着）。
        /// </summary>
        internal float ContentYOffset
        {
            get { return RulerHeight - verticalScrollOffset; }
        }

        internal float RowHeightPixels { get { return rowHeight; } }

        internal float SectionHeaderPixels { get { return SectionHeaderHeight; } }

        // -------------------------------------------- 自由画布的口子
        //
        // **这是一套新模型，和上面的排版模型（layoutRows / layoutRowY）不是一回事。**
        // 那一套是"按格分组 + 自动打包成行"，是旧时间轴的骨架，ImGui 这边不读它。
        // 这一套是玩家自己摆的自由画布：每块有自己的行和时间，
        // **垂直顺序就是执行优先级**（ADOFAI 从上往下执行事件）。
        //
        // 现在只活在这次会话里 —— 落盘（.aes）和写回 .adofai 都还没做。

        private sealed class BlockPlacement
        {
            internal TimelineItem Item;
            internal int Row;
            internal float Time;
            /// <summary>
            /// 拖动中临时用的时长（NaN = 用 item 自己的）。
            ///
            /// 拉长/缩短时画布要立刻跟着变，而 item.Duration 是提交之后才更新的，
            /// 所以拖动期间先记在这儿。松手就清掉。
            /// </summary>
            internal float PreviewDuration = float.NaN;
        }

        /// <summary>ImGui 那边读的一个块（摊平出来，不暴露 TimelineItem）。</summary>
        internal struct BlockView
        {
            /// <summary>
            /// 稳定 ID = `blockPlacements` 里的下标。**拖动一律靠它定位**，
            /// 不靠行号 —— 行号会随着别的块被拖走而变。
            /// </summary>
            internal int Id;
            internal int Row;

            /// <summary>
            /// 这一块能不能拉时长。**不能拉的就不要给左右边缘区** ——
            /// 给了的话按在边上会走"改时长"，而它根本没有 `duration` 可改，
            /// 结果是什么都不发生（表现就是"这块拖不动"）。
            /// 最小宽度 64 的块，边缘区就占掉 12，将近五分之一是死区。
            /// </summary>
            internal bool Resizable;
            internal float Time;
            internal float Duration;
            internal string Name;
            internal Color BlockColor;
            internal Color LabelColor;
            internal bool Active;
            internal bool Selected;
            /// <summary>事件类型图标（白色剪影）。没有就是 null。</summary>
            internal Sprite Icon;
            /// <summary>
            /// 缓速曲线的采样值（0..1）。时长为 0 的事件没有缓速可言，是 null。
            ///
            /// **数组是按缓速类型缓存的**（见 GetEaseCurveValues），
            /// 每帧取一次不会分配 —— 否则几百个块每帧各分一个数组，
            /// GC 会被拖垮。
            /// </summary>
            internal float[] Curve;
            /// <summary>
            /// 这个块对应的那个事件。**撤销命令靠它定位**，不靠 Id ——
            /// Id 是位置表里的下标，谱面一重载整张表就重建了，
            /// 拿旧下标回去会挪错块。
            /// </summary>
            internal LevelEvent Event;
        }

        private readonly List<BlockPlacement> blockPlacements = new List<BlockPlacement>();
        private readonly List<BlockView> blockViews = new List<BlockView>();

        /// <summary>
        /// 刚**消失**的事件记着它最后一次待的行号，撤销回来时按这张表领回去。
        ///
        /// 不记的话：删除 → 下一次刷新位置表里那一条被丢掉 → 撤销把**同一个**
        /// `LevelEvent` 放回来，但它对现在的表来说是个"新块"，前缀也查不到，
        /// 只能领 `++maxRow`，也就是掉到最下面 —— 用户看到的就是
        /// "撤回后没回到之前的位置"。
        ///
        /// 按**事件对象**记（引用比较），因为撤销插回来的就是被删的那个对象；
        /// 换个对象的一般是换谱面 / 原生撤销，那种走前缀兜底，不该套这张表。
        /// </summary>
        private readonly Dictionary<LevelEvent, int> removedRows =
            new Dictionary<LevelEvent, int>(ReferenceComparer<LevelEvent>.Instance);

        /// <summary>画布上摆着的块，**按行升序**（上面的先画，下面的盖住它）。</summary>
        internal List<BlockView> BlockViews { get { return blockViews; } }

        /// <summary>画布上一共有多少行（拖到更下面的行就是新建行）。</summary>
        internal int BlockRowCount { get; private set; }

        /// <summary>
        /// 自由画布的几何：行距、块高、块最小宽度。
        ///
        /// **放在模型这边而不是渲染那边** —— 滚动上限要用行距算，两边各存一份
        /// 迟早对不上（一边改了另一边没改，表现是滚不到底）。
        /// 块比行距高，所以相邻两块是**叠着**的，这就是"一层层往下盖"。
        /// </summary>
        /// <summary>块高至少留这么多，行距调到最小时也得看得见。</summary>
        internal const float BlockMinHeight = 14f;
        /// <summary>
        /// 块的最小宽度。**就是旧模型那个数**（`MinimumPointEventWidth`）——
        /// 0 时长的事件要窄成一条竖线，既看不见也点不着。
        ///
        /// 试过收窄到 26，结果整条时间轴看着变碎了，所以恢复。
        /// 0 时长的事件不再特殊对待，只是**不画缓速曲线**（没缓速可言）。
        /// </summary>
        internal const float BlockMinWidth = MinimumPointEventWidth;
        /// <summary>块与块之间留的缝。上下靠 `BlockStride − BlockHeight`，左右靠它。</summary>
        internal const float BlockGap = 4f;

        /// <summary>
        /// 画布的行距。**就是 `rowHeight`**，不是另写一个常量。
        ///
        /// 这样 Alt+± / Ctrl+0 那几个改行高的快捷键、以及存在设置里的
        /// `TimelineRowHeight` 都直接作用到画布上 —— 我一开始把行距写死成常量，
        /// 结果那几个快捷键全成了空转（按了没反应，看着像"快捷键失效"）。
        /// </summary>
        internal float BlockStridePixels { get { return rowHeight; } }

        /// <summary>块高 = 行距 − 缝。缝固定，所以行距一变块跟着变。</summary>
        internal float BlockHeightPixels
        {
            get { return Mathf.Max(BlockMinHeight, rowHeight - BlockGap); }
        }

        /// <summary>画布末尾固定留出的空行数 —— 给「往下拖」留落点。</summary>
        private const int ExtraRowsBelow = 10;

        /// <summary>
        /// 自由画布的内容总高。
        ///
        /// **末尾固定多留 `ExtraRowsBelow` 行**（10 行）。不留的话这个高度正好等于
        /// "整张谱里堆得最高的那一处"—— 滚动到底时最后一行紧贴面板下沿，
        /// 想把它往更下面拖根本没有落点（用户的原话就是"按最高的那一格定高度"）。
        ///
        /// 这些空行不占内存、也不画任何东西，纯粹是给拖拽留地方。
        /// </summary>
        internal float BlockContentHeight
        {
            get
            {
                return (Mathf.Max(0, BlockRowCount) + ExtraRowsBelow) * BlockStridePixels
                    + BlockStridePixels;
            }
        }

        /// <summary>垂直滚动位置（正数向下）。</summary>
        internal float ScrollOffsetY { get { return verticalScrollOffset; } }

        /// <summary>
        /// 没摆过就自动排一次：**按文件顺序，一个块一行**。
        ///
        /// 为什么不按时间打包成紧凑的几行：那样垂直顺序就不等于文件顺序了，
        /// 而"从上往下 = 执行优先级"整个方案的前提就是这一条。
        ///
        /// **只在块数对不上时重排**（第一次进、或者换了谱面）。玩家摆过的位置
        /// 不能因为切个车道、折叠一下就被冲掉。
        /// </summary>
        /// <summary>上次自动排列时用的是哪张谱面。**换谱面才重排，清单重建只重新绑定。**</summary>
        private object placementsChart;

        /// <summary>
        /// 当前工程里存的行号（事件身份 → 行号）。**null = 这张谱还没有工程**，
        /// 那就整张走自动装箱。
        ///
        /// 只在初次排列（`AssignInitialRows`）里被读一次：有记录的块直接归位，
        /// 没记录的（新加的事件、名册对不上的）才轮到贪心装箱。
        /// </summary>
        private Dictionary<string, int> projectRows;

        /// <summary>
        /// 上一次认的谱面路径。用来判断「换谱面了没有」—— 见 `EnsureBlockPlacements`。
        /// </summary>
        private string projectChartPath;

        /// <summary>
        /// 布局有没有**没写进工程**的改动。
        ///
        /// 只算布局（块的行号），不算谱面参数 —— 改透明度、改颜色那些属于
        /// 谱面本身，归游戏自己的保存管，不该混进这个提示里。
        /// </summary>
        private bool projectDirty;

        /// <summary>有没有未保存的布局改动（关工作台 / 切回只读时问它）。</summary>
        internal bool ProjectDirty
        {
            get { return projectDirty; }
        }

        /// <summary>
        /// 下一次 `EnsureBlockPlacements` **强制整张重排**，不许走"按事件搬旧位置"。
        ///
        /// 「重置时间轴排版」用它：那条路上谱面路径没有变，而判据是"按路径认
        /// 还是不是同一张谱"，光清 `projectRows` 判不出来 —— 会走重绑、
        /// 等于什么都没重置（实测症状：重置完位置原样不动）。
        /// </summary>
        private bool forcePlacementRebuild;

        /// <summary>
        /// 上次**自动**排列时用的 BPM。
        ///
        /// 用来发现「排得太早、参数还没定下来」：打开谱面的头几帧里
        /// `levelData.bpm` 还是上一张谱的值，按它算出来的占位时长是错的
        /// （见 `EnsureBlockPlacements` 里那段说明）。只对**还没有工程**的谱面
        /// 生效 —— 有工程就说明位置是玩家的，一律不碰。
        /// </summary>
        private float autoArrangedBpm;

        /// <summary>自动排列跑过几次。用来抓"每帧重排把拖动冲掉"那类问题。</summary>
        private int placementArranges;

        /// <summary>
        /// 从工程文件加载位置表。**换谱面（或者第一次打开）时调一次。**
        /// 找不到工程就把 `projectRows` 清成 null —— 整张自动装箱，
        /// 这正是「只读模式打开一张没做过的谱」的正常路径。
        /// </summary>
        internal void LoadProjectRows(string chartPath)
        {
            projectRows = null;

            // **不管有没有工程，都要先把「认过的谱面」清掉。**
            //
            // 这里原来是"载入成功才清 placementsChart"，于是换到一张**没有工程**
            // 的新谱面时：两个 early return 之一直接出去、标记还留着旧的，
            // `EnsureBlockPlacements` 就以为"还是同一张谱"，走按事件重绑那条路 ——
            // 新谱的事件在旧表里一个都找不到，全被当成新事件逐个 `++maxRow`，
            // 行号一路涨到几百。表现就是**换谱之后事件全没了**（全排到下面去了）。
            placementsChart = null;

            if (string.IsNullOrEmpty(chartPath))
            {
                return;
            }

            string path = EditorProjectFile.FindProjectFile(chartPath);
            EditorProjectFile.Project project;
            if (!EditorProjectFile.TryLoad(path, out project))
            {
                // **这张谱还没有工程 —— 首次打开，直接排一次干净的。**
                //
                // 位置是玩家的资产，但那要等**第一次存下来**之后才算数。没有工程
                // 的时候，手上这份位置表要么是空的、要么是上一次会话的残留，照搬
                // 毫无意义 —— 而"新谱一打开块就叠在一起、非得手动重置排版才好"
                // 就是从这儿来的。
                //
                // 这里**显式**置一次重排标记，不再指望 `sameChart` 恰好为 false
                // 那个副作用把流程带到初次排列上去：那是隐式的，中间任何一环
                // 变了都会悄悄失效。
                forcePlacementRebuild = true;
                return;
            }
            projectRows = project.Rows;
            Main.Logger.Log("[工程] 载入 " + project.Rows.Count + " 条位置记录：" + path);
        }

        /// <summary>
        /// 把当前布局写进工程文件。返回空串表示成功，否则是失败原因（给弹窗用）。
        /// 写之前先确保位置表是最新的 —— 拖动过但还没重排的那种情况。
        /// </summary>
        internal string SaveProjectRows(string projectPath, string chartName)
        {
            EnsureBlockPlacements();

            // **存之前把身份键重算一遍。**
            //
            // 键里带着"起点时间"和"参数哈希"，而拖动改的正是这两样。用的是
            // 上一次 `RefreshData` 留下的旧键就会把行号存到旧身份下 —— 重开谱面
            // 时键对不上，位置就"自己没了"。
            List<TimelineItem> items = new List<TimelineItem>(blockPlacements.Count);
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement != null && placement.Item != null)
                {
                    items.Add(placement.Item);
                }
            }
            EnsureEventKeys(items);

            var project = new EditorProjectFile.Project { Chart = chartName };
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement == null || placement.Item == null
                    || string.IsNullOrEmpty(placement.Item.Key))
                {
                    continue;
                }
                project.Rows[placement.Item.Key] = placement.Row;
            }
            string error = EditorProjectFile.Save(projectPath, project);
            if (string.IsNullOrEmpty(error))
            {
                // 存下去了，「未保存」的标记跟着清掉。
                projectDirty = false;
                Main.Logger.Log("[工程] 保存 " + project.Rows.Count + " 条位置记录：" + projectPath);
            }
            return error;
        }

        /// <summary>
        /// 保存工程（开发期挂在 `Ctrl+Shift+S` 上）。
        /// 路径按约定算：`<谱面目录>/<谱面名>_AES/<谱面名>.aesprj`。
        ///
        /// 现在只写一条日志 —— 保存成功的提示、未保存的拦截，那些跟菜单栏一起做。
        /// </summary>
        private void SaveProjectInteractive()
        {
            string chartPath = EditorProjectSession.GetCurrentLevelPath();
            if (string.IsNullOrEmpty(chartPath))
            {
                Main.Logger.Warning("[工程] 拿不到谱面路径，没保存。");
                return;
            }
            string directory = EditorProjectFile.GetProjectDirectory(chartPath);
            string fileName = System.IO.Path.GetFileNameWithoutExtension(chartPath)
                + EditorProjectFile.FileExtension;
            string projectPath = System.IO.Path.Combine(directory, fileName);
            string error = SaveProjectRows(projectPath, System.IO.Path.GetFileName(chartPath));
            if (!string.IsNullOrEmpty(error))
            {
                Main.Logger.Warning("[工程] 保存失败：" + error);
            }
        }

        /// <summary>
        /// 丢掉工程里存的位置，强制整张**重新自动装箱**。
        /// 「工程 → 重置时间轴排版」用它。
        ///
        /// 两个字段都要清：`projectRows` 是那份表，`placementsChart` 是
        /// 「已经认过谱面了」的标记 —— 只清前者的话，下一次
        /// `EnsureBlockPlacements` 会认为"还是同一张谱"、只按事件重绑旧位置，
        /// 等于什么都没发生。
        /// </summary>
        internal void ResetPlacementsToAuto()
        {
            projectRows = null;
            placementsChart = null;
            // **必须强制走重排。**
            //
            // 判据是「按路径认还是不是同一张谱」，而重置排版时路径根本没变 ——
            // 不清这个标志的话会走 `RebindBlockPlacements`（按事件搬旧位置），
            // 等于什么都没重置，清掉 `projectRows` 也白搭。
            forcePlacementRebuild = true;
            blockPlacements.Clear();
            // 整张重排，之前删掉那些事件的行号对新布局没有意义。
            removedRows.Clear();
            EnsureBlockPlacements();
            visualsDirty = true;
            // 整张重排过了，这就是一份没存过的新布局。
            projectDirty = true;
        }

        private void EnsureBlockPlacements()
        {
            // **谱面换了就重新认工程。**
            //
            // 这个方法是每帧都跑的，所以这里只能比路径、不能每次读盘。
            // 路径变了（打开了别的谱面）才去工程目录找一次 —— 找到就把位置表装进来，
            // 找不到就是「这张谱还没做过工程」，整张自动装箱。
            //
            // **空路径不算"变了"**：`GetCurrentLevelPath()` 在场景切换的中间态
            // 会返回 null（它自己的注释就写着这事）。一旦让 null 覆盖掉记下的路径，
            // 下一帧拿到真路径时又会被判成"换谱面了" → `placementsChart` 清掉 →
            // 整张重新自动装箱，**玩家摆好的位置（含刚撤销回去的）当场没了**。
            // 实测症状就是「拖到第 6 行、撤销回到第 1 行，一眨眼又跑到第 2 行」。
            string chartPath = EditorProjectSession.GetCurrentLevelPath();
            bool sameChart;
            if (string.IsNullOrEmpty(chartPath))
            {
                // 场景切换的中间态拿不到路径 —— **当作还是同一张谱**，
                // 别趁这一下重排（那会把玩家摆好的位置冲掉）。
                sameChart = true;
            }
            else
            {
                sameChart = string.Equals(projectChartPath, chartPath,
                    StringComparison.OrdinalIgnoreCase);
                if (!sameChart)
                {
                    projectChartPath = chartPath;
                    LoadProjectRows(chartPath);

                    // **换谱面了：旧谱的位置表对新谱没有意义。**
                    //
                    // 不清掉的话，下面那句「blockPlacements.Count > 0 就走重绑」
                    // 会让新谱的事件去套旧谱的行号；套不上的一律 `++maxRow`
                    // 补到最下面 —— 表现就是「一换谱面排版就乱」。
                    //
                    // 清掉之后，下面「还没排过版就先确认清单是最新的」那道检查
                    // 才会生效：新谱的事件清单还没刷进来时先不排，等刷好再排。
                    blockPlacements.Clear();
                    // 换谱面了：上一张谱里删掉那些事件的行号不能套到新谱上。
                    removedRows.Clear();

                    // 换了一张谱：排完版后横向缩放重新给到最大。
                    pendingZoomToMaximum = true;
                }
            }

            // **清单取自全部车道，不按车道可见性过滤。**
            //
            // 原来取的是 `layoutItems`，而它是"只拼可见车道"的。于是关掉一个车道，
            // 那些事件就从清单里消失 —— `RebindBlockPlacements` 找不到它们，把行号
            // 丢了；再打开时按 `++maxRow` 补到最底下。玩家摆好的位置当场作废。
            //
            // 位置是玩家的资产（自动排列只在初次加载谱面跑一次），所以清单必须完整。
            // "看不见"改在 `CollectBlocks` 里滤 —— 那只是画不画的问题。
            List<TimelineItem> ordered = new List<TimelineItem>();
            for (int lane = 0; lane < LaneCount; lane++)
            {
                ordered.AddRange(laneItems[lane]);
            }
            // 上面是按车道拼的，不是文件序 —— 必须按 SourceOrder 重排。
            ordered.Sort(delegate(TimelineItem a, TimelineItem b)
            {
                return a.SourceOrder.CompareTo(b.SourceOrder);
            });

            // 身份要在**按时间重排之前**算好 —— 再往下 `ordered` 就被按时间排序了。
            EnsureEventKeys(ordered);

            // **工程认不出来，就当没有工程。**
            //
            // 工程的键是「类型:格:时间:参数」，而时间/参数这两段跟着代码版本走
            // （早期构建只存了「类型:格:序号」三段）。格式一变，旧文件**一个键都
            // 对不上** —— 玩家的摆放被静默丢掉，界面上只表现为"排版不对"。
            //
            // 更糟的是 `projectRows != null` 会让下面那条
            // 「还没有工程时，BPM 变了就重排一次」的防护失效：第一次装箱要是读到
            // 了上一张谱残留的 BPM，排错之后就**再也不会自己纠正**。
            //
            // 判据取「一条都对不上」：只要还有一条能对上，就说明这份工程是有用的，
            // 玩家的位置照旧保留（哪怕谱面改过、只剩一部分能认）。
            // 真的一条都对不上时，它本来也提供不了任何位置。
            if (projectRows != null && projectRows.Count > 0)
            {
                bool anyProjectRowMatched = false;
                for (int i = 0; i < ordered.Count; i++)
                {
                    TimelineItem candidate = ordered[i];
                    if (candidate != null && !string.IsNullOrEmpty(candidate.Key)
                        && projectRows.ContainsKey(candidate.Key))
                    {
                        anyProjectRowMatched = true;
                        break;
                    }
                }
                if (!anyProjectRowMatched)
                {
                    Main.Logger.Log("[工程] 位置记录一条都对不上（" + projectRows.Count
                        + " 条）：" + projectChartPath + " —— 按没有工程处理，重新自动排版");
                    projectRows = null;
                    if (owner != null)
                    {
                        owner.SetTransientStatus("工程的位置记录与当前版本不兼容，已重新自动排版", 5f);
                    }
                }
            }

            scnEditor editor = scnEditor.instance;
            object chart = editor == null ? null : editor.levelData;

            // **清单必须和谱面对得上，否则不许排版。**
            //
            // 实测（大谱首次打开）：自动排版拿的是上一次 `RefreshData` 留下的清单 ——
            // 那张谱实际有 2826 个事件，清单里只有 357 个（打开过程中事件是边填边长的，
            // 每 0.5 秒才刷一次）。剩下的两千来个后来才被刷新进来，走
            // `RebindBlockPlacements` 的「新事件」分支被 `++maxRow` 补到最下面 ——
            // 用户看到的就是「首次打开排版有问题」。
            //
            // 所以：**还没有位置表**（= 这张谱还没排过版）时，先比一次指纹；
            // 对不上就先刷新、这一帧先不排，下一帧拿新清单再来。
            // 只有在"第一次排"这一步做，正常拖动 / 编辑时每帧都会走原来的分支。
            if (blockPlacements.Count == 0 && editor != null)
            {
                int currentFingerprint = ComputeDataFingerprint(editor);
                if (!dataFingerprintValid || currentFingerprint != dataFingerprint)
                {
                    RefreshData(true);
                    return;
                }
            }

            // **数量对得上、而且还是同一张谱面 —— 就只重新绑定，不重排。**
            //
            // 这里踩过大坑：原来判的是"手上这个块还在清单里"，而时间轴的清单
            // 在**每次重新分析谱面时都会整个重建**（TimelineItem 换成一批新对象）。
            // 于是那个判据每帧为假 → 每帧重排 → **玩家刚拖的块当场被抹平**，
            // 表现就是"拖不动"。（清单重建和"换谱面"是两回事，分开判。）
            // **同一张谱面：只重新绑定，绝不重排。**
            //
            // 判据不能看数量。实测日志里数量在 395/396 之间来回跳（事件集合
            // 每帧重建时偶尔差一个），那时按数量判就会**每跳一次重排一次** ——
            // 玩家摆好的布局被反复冲掉，还白跑几百次。
            // 现在不管数量变没变，都按事件把旧位置搬过去。
            // **按路径认「还是不是同一张谱」，不按 `levelData` 对象。**
            //
            // 撤销会把 `levelData` 换成新对象（它恢复的就是整套谱面状态），
            // 按对象认的话每次撤销都被判成"换谱面"→ 下面整张重新装箱，
            // 玩家摆好的位置**连同刚撤销回去的那个**当场被冲掉。
            // 实测症状：拖到第 6 行、撤销回到第 1 行，一眨眼又跑到第 2 行
            // （第 2 行正是自动装箱会给它的位置）。
            // **排得太早、BPM 还没定下来的那一次，要跟着重排。**
            //
            // 装箱的「占位时长」= max(最小块宽, 事件时长) 换算成秒，而换算要除以 BPM
            // —— 也就是占位固定从 0.8 拍起步。可打开谱面的头几帧里 `levelData.bpm`
            // 还是**上一张谱残留的值**：实测这张谱真实 BPM 123.333，首次装箱读到的是
            // 160。占位算错，行数和排布就跟着错 —— 同一张谱、同样的块和时间，
            // 实测一次 90 行、一次 93 行，样子完全不同。
            //
            // 而且它**排完就再也不会自己纠正**，只能靠玩家手动重置排版 —— 那正是
            // 「首次进去排版乱、重置一下就对了」的由来（玩家以为两条路不同，其实是
            // 同一条路、只是喂进去的 BPM 一个有对一个错）。
            //
            // 所以：只要**还没有工程**（布局还是我们自动生成的、没被玩家的位置覆盖
            // 过），发现 BPM 变了就重排一次。**有工程之后一律不碰** —— 位置是玩家的
            // 资产，好坏都听文件里的（见 `LoadProjectRows` 那段）。
            if (projectRows == null && autoArrangedBpm > 0f
                && Mathf.Abs(ChartBpm - autoArrangedBpm) > 0.5f)
            {
                forcePlacementRebuild = true;
            }

            if (!forcePlacementRebuild && sameChart && blockPlacements.Count > 0)
            {
                RebindBlockPlacements(ordered);
                return;
            }
            // 强制重排是一次性的：这次用掉就清掉，别让它一直生效
            // （那样每帧都会重排，玩家拖好的位置会被反复冲掉）。
            forcePlacementRebuild = false;
            repackOnNextRebuild = true;

            placementsChart = chart;
            blockPlacements.Clear();

            // **初次排列：按时间装箱，每一块都贴到最上面那个放得下的行。**
            //
            // 目标是**一屏能看全整张谱**，而且**不留空**：
            //
            //   · 原来是一块一行 —— 400 个块就是 400 行，一屏只看得见十来个。
            //   · 中间试过"按类别分段"（装饰物一段、运镜一段…），但那和"不留空"
            //     是互斥的：某一类如果只在谱面开头出现，它那一段到中后段就整片
            //     空着，而别的类的块明明可以挪上来，却被类压在下面十几行。
            //   · 现在只按时间装箱：时间上不重叠的块共用一个行，每个块都放进
            //     **第一个此刻空着的行**。纵向高度 = 同时重叠的事件数。
            //
            // 只影响**初次**排列：玩家自己拖过的位置由 `RebindBlockPlacements`
            // 按事件搬过去，一个都不动。
            ordered.Sort(CompareInitialPlacement);
            AssignInitialRows(ordered);
            placementArranges++;
            // 记下这次用的是哪个 BPM —— 它要是和真实值不一样（谱面还没加载完就读了），
            // 下一帧会被上面那段补一次重排。见 `autoArrangedBpm` 的说明。
            autoArrangedBpm = ChartBpm;

            // 排版完成 = BPM 已经是这张谱的真实值，这时才能安全地把缩放拉到最大。
            // **只动横向（X），行高（Y）保持用户自己的设置。**
            if (pendingZoomToMaximum)
            {
                pendingZoomToMaximum = false;
                pixelsPerSecond = MaximumPixelsPerSecond;
                viewStartTime = 0f;
                ClampViewStart();
                UpdateZoomText();
                visualsDirty = true;
            }
        }

        /// <summary>
        /// 把排好序的清单摆成"每个行一串在时间上不相撞的块"。
        ///
        /// 贪心装箱（first fit）：清单已按**时间升序**排好，所以每个块只要找
        /// 第一个"已占用到的时间 ≤ 它的起点"的行放进去就行；**都放不下**才新开一行。
        /// 于是每一块都贴在"最上面那个此刻放得下的行"上，纵向高度 = 谱面里同时
        /// 重叠的事件数 —— 不会出现"上面空着一大片、块却压在下面十几行"。
        ///
        /// 零长事件也要占地方，**而且占多少要按"画出来多宽"算**（见下）。
        /// </summary>
        /// <returns>用掉的行数 —— 打日志用（想一屏看全，就靠这个数衡量）。</returns>
        private int AssignInitialRows(List<TimelineItem> ordered)
        {
            // 每个行"已经占到哪个时间"。行号就是下标 —— 从 0 开始，
            // 所以"找第一个放得下的"天然就是"贴到最上面"。
            var rowEnds = new List<float>();

            // 工程行号也按「一个身份只认领一次」用 —— 见下面读取行号那段的说明。
            var claimedProjectKeys = new HashSet<string>();

            // **占位时间按画出来的宽度换算，不能用一个名义的常数。**
            //
            // 画的时候块有个最小宽度（`BlockMinWidth`，64 像素），零时长的事件
            // 也画这么宽。所以两个相距 0.1 秒的点事件要是放进同一行，画出来就是
            // 两个 64 像素的块叠在一起 —— 用户看到的就是"很多块重叠了"。
            //
            // **按最小缩放（最挤的情况）算占位**，不是按当前缩放。
            //
            // 这样之后不管用户怎么缩，块之间的间距只会更大、不会更小 ——
            // 永远不会叠。所以「缩放时重排」那条路就不需要了，
            // 玩家摆好的位置从头到尾都是他自己的。
            // 代价是行数按最坏情况给（缩放下限越大，这个数越小）。
            float speed = MinimumPixelsPerSecond;

            for (int i = 0; i < ordered.Count; i++)
            {
                TimelineItem item = ordered[i];
                float start = item.StartTime;

                // 画出来的宽度（像素）再换回秒，末尾补一道 `BlockGap` 的缝，
                // 免得两个正好贴着的块画出来连成一条。
                float drawnPixels = Mathf.Max(BlockMinWidth, item.Duration * speed - BlockGap);
                float end = start + (drawnPixels + BlockGap) / speed;

                int row = -1;

                // **工程里存了这一块的行号就用它。** 位置是玩家的资产 ——
                // 只有他没摆过的（新加的事件、或者名册对不上的）才轮到自动装箱。
                //
                // （这里曾经试过"行号会撞就退到 first-fit"的避让，撤掉了：它没能消掉
                // 工程里已有的重叠，还把行数从 300 撑到 764、被自动保存写回工程，
                // 越修越坏。工程要是坏的，正路是让用户按「重置排版」重建。）
                //
                // **同一个身份只让认领一次**（同 `RebindBlockPlacements` 里那条）：
                // 工程里存着两个一模一样的事件时，两条记录的身份键相同，照搬会叠在
                // 同一行。第二个起改走下面的 first-fit。
                int saved = -1;
                bool savedUsable = projectRows != null && !string.IsNullOrEmpty(item.Key)
                    && claimedProjectKeys.Add(item.Key)
                    && projectRows.TryGetValue(item.Key, out saved) && saved >= 0;
                if (savedUsable)
                {
                    row = saved;
                    // 把这一行的「已占到哪」推到这一块之后，后面那些没存行号的块
                    // 才不会撞进同一行。和已有值取 Max，是为了不把那一行更晚的占用拉回来。
                    while (rowEnds.Count <= row)
                    {
                        rowEnds.Add(float.NegativeInfinity);
                    }
                    rowEnds[row] = Mathf.Max(rowEnds[row], end);
                }
                else
                {
                    for (int r = 0; r < rowEnds.Count; r++)
                    {
                        if (rowEnds[r] <= start)
                        {
                            row = r;
                            rowEnds[r] = end;
                            break;
                        }
                    }
                    if (row < 0)
                    {
                        row = rowEnds.Count;
                        rowEnds.Add(end);
                    }
                }

                blockPlacements.Add(new BlockPlacement
                {
                    Item = item,
                    Row = row,
                    Time = item.StartTime
                });
            }

            return rowEnds.Count;
        }

        /// <summary>
        /// 同一张谱面里事件集合变了（加了一个、删了一个、或者清单被整个重建）：
        /// **按事件把旧的行/时间搬过去，玩家摆过的一个都不动。**
        ///
        /// 新出现的排到末尾（用文件顺序当行号），消失的直接丢掉。
        /// 清单顺序仍然按 `SourceOrder` 排 —— 这样和"一块一行"的初始排列一致，
        /// 索引也就稳定，拖动中的 Id 不会指到别的块上。
        /// </summary>
        private void RebindBlockPlacements(List<TimelineItem> ordered)
        {
            // **一模一样就一个字都别动。**
            //
            // 这个方法每一帧都会被 `EnsureBlockPlacements` 调到（谱面没变也调）。
            // 重建一次就有两个副作用，而且都不报错、只表现为"手感不对"：
            //   · `PreviewDuration` **没被带过去**（那是新对象，默认 NaN）——
            //     拉动块两端时画布没有实时预览，松手才跳；
            //   · `Id` 是位置表里的下标，重建等于把 ID 重发一遍。顺序本来
            //     是确定的，但 `List.Sort` 对**相同的 `SourceOrder` 不保证稳定**，
            //     一有并列就可能逐帧抖，`Id` 跟着指到别的块上。
            // 没变就早退，这两个问题一起消失。
            if (SamePlacementOrder(ordered))
            {
                return;
            }

            // **按事件身份（`Key`）搬位置，不是按 `LevelEvent` 对象。**
            //
            // 这里是"再打开事件全没了"的根因：谱面一重新分析，`LevelEvent` 就换成
            // 一批新对象，按对象匹配**全部对不上**，于是每个块都被当成"新事件"、
            // 逐个 `++maxRow` —— 行号一路涨到几百，块全被排到下面很远的行上。
            // 而 `Ctrl+Shift+S` 存的就是这份被撑坏的表。
            var times = new Dictionary<string, float>();
            // **旧行号再按「位置」存一份**，给下面"键对不上"时兜底。
            //
            // 为什么要它：键里含参数（为了分开"同格、同类型、同时刻"的多个事件，
            // 比如同一格上叠着的几个滤镜），而**改参数会让键变** —— 只按键找的话，
            // 改一个透明度那一块就"找不到"、被补到最下面，看着就是"改个参数块就跑了"。
            // 按位置兜底之后它保持原样，那个矛盾就不存在了。
            // 旧表的行号按**身份键的前三段**（类型:格:时间）分组排队。
            //
            // **为什么不按键、也不按下标：**
            //   · 只按键 —— 键的第四段是参数，一改参数键就变，"找不到"就被补到
            //     最下面（用户看到的是"改个透明度块就跑了"）；
            //   · 只按下标 —— 清单长度一变（加了事件）下标就整体错位，新加的块会
            //     套用别人的行号、挤在同一行。实测就是这么叠的：
            //     「行0 时间 3.84 vs 3.84 身份相同=否」——身份不同却撞在一行。
            //
            // 按**前三段**排队，三种情况一次都照顾到：
            //   · 改过参数的块，前三段没变 → 照样认得出，位置保住；
            //   · 真·新事件的前缀在旧表里没有 → 补到最下面；
            //   · 一模一样的那对（前三段当然也一样）→ 一人领一个旧行号，
            //     旧表里只有一个的话，第二个自然去补 → 不会再叠。
            var oldRowsByPrefix = new Dictionary<string, Queue<int>>();

            // **再按事件对象存一份行号**，给「身份键的前三段也被改掉」的情况兜底。
            //
            // 键的前三段是 `类型 : 格 : 起点时间`，而**横向拖动改的正是后两样**。
            // 松手提交后谱面指纹变化、下一次轮询重建 `TimelineItem`，重算出来的
            // 键就再也对不上旧表 —— 于是走到下面那句 `row = ++maxRow`，
            // 块当场掉到最下面一行。用户看到的就是「拖完 Y 轴突然往下跳很多」。
            //
            // 增量刷新时事件对象是**同一批**（`RefreshData` 只是重新遍历
            // `editor.events`），所以按引用能精确认回来。只有撤销 / 换谱面那种
            // 整体替换才会认不出，那时照旧补到最下面。
            var oldRowsByEvent = new Dictionary<LevelEvent, int>(ReferenceComparer<LevelEvent>.Instance);
            int maxRow = -1;
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement == null || placement.Item == null)
                {
                    continue;
                }
                // 事件对象 → 旧行号。键可能是空的（还没算出身份），
                // 但对象本身照样能用来认「这是同一个事件」。
                if (placement.Item.Event != null)
                {
                    oldRowsByEvent[placement.Item.Event] = placement.Row;
                }
                string oldKey = placement.Item.Key;
                if (string.IsNullOrEmpty(oldKey))
                {
                    continue;
                }
                times[oldKey] = placement.Time;

                string prefix = KeyPrefix(oldKey);
                Queue<int> queue;
                if (!oldRowsByPrefix.TryGetValue(prefix, out queue))
                {
                    queue = new Queue<int>();
                    oldRowsByPrefix[prefix] = queue;
                }
                queue.Enqueue(placement.Row);

                if (placement.Row > maxRow)
                {
                    maxRow = placement.Row;
                }
            }

            blockPlacements.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                TimelineItem item = ordered[i];
                int row = -1;
                float time = item.StartTime;

                // **先按事件对象认。** 同一个对象就是同一个事件，比身份键更准：
                // 键里带着格和时间，而拖动 / 改格刚好会改掉它们（见上面那段说明）。
                if (item.Event != null)
                {
                    int oldRow;
                    if (oldRowsByEvent.TryGetValue(item.Event, out oldRow))
                    {
                        row = oldRow;
                        // 一个事件只领一次，领完就摘掉。
                        oldRowsByEvent.Remove(item.Event);
                    }
                }

                if (row < 0 && !string.IsNullOrEmpty(item.Key))
                {
                    Queue<int> queue;
                    if (oldRowsByPrefix.TryGetValue(KeyPrefix(item.Key), out queue)
                        && queue.Count > 0)
                    {
                        row = queue.Dequeue();
                        // 时间只认**同一个身份**记下的那个；前缀对上但身份变了
                        // （改过参数）就用它这次算出来的起点。
                        float savedTime;
                        if (times.TryGetValue(item.Key, out savedTime))
                        {
                            time = savedTime;
                        }
                    }
                }

                if (row < 0 && item.Event != null)
                {
                    // **刚才被删掉、现在又回来了的事件。**
                    //
                    // 撤销插回的就是当初删掉的那个对象（见 `RemoveEvents`），
                    // 所以能按引用认出来，把它原来的行号还给它。
                    // 放在前缀兜底之后 —— 前缀更准（它要求身份一致），
                    // 只有前缀也查不到才轮到这里。
                    int restoredRow;
                    if (removedRows.TryGetValue(item.Event, out restoredRow))
                    {
                        row = restoredRow;
                        removedRows.Remove(item.Event);
                        if (row > maxRow)
                        {
                            maxRow = row;
                        }
                    }
                }

                if (row < 0)
                {
                    // 前缀在旧表里没有（真·新事件），或者那一组已经分完了：
                    // 补到最下面一行。
                    row = ++maxRow;
                }

                blockPlacements.Add(new BlockPlacement { Item = item, Row = row, Time = time });
            }

            // **只在载入 / 换谱时排一次。**
            //
            // 原来这里还带着「固定排版模式下每次改动都重排」，而重排是
            // O(块数 × 行数) 的全量装箱（这张谱 3400+ 块、上百行），
            // 一次就要几百毫秒 —— 拖动/缩放时每次重建都跑，直接把主线程卡死。
            // 等块表真的有内容再排：第一次重建常常发生在事件清单还没刷进来时，
            // 那一次重排是空跑，标记却被清掉 —— 首次进谱面就用了老规则。
            if (repackOnNextRebuild && BlockTableLooksComplete())
            {
                repackOnNextRebuild = false;
                RepackRowsByTypePriority();
            }


            // **这一轮没被任何块领走的旧行号 = 刚刚消失的事件。**
            //
            // 记起来给撤销用。上限只是防长期不关工作台时无限攒 ——
            // 真到了这一步，说明删掉的量已经远超一次撤销能覆盖的范围，
            // 丢掉最旧的那批行号用户也看不出来。
            foreach (KeyValuePair<LevelEvent, int> leftover in oldRowsByEvent)
            {
                RememberRemovedRow(leftover.Key, leftover.Value);
            }
        }

        /// <summary>记下"这个事件消失前待在第几行"。见 <see cref="removedRows"/>。</summary>
        private void RememberRemovedRow(LevelEvent levelEvent, int row)
        {
            if (levelEvent == null)
            {
                return;
            }
            if (removedRows.Count >= 16384)
            {
                removedRows.Clear();
            }
            removedRows[levelEvent] = row;
        }

        /// <summary>
        /// 身份键的「前三段」：`类型:格:时间`。
        ///
        /// 第四段是参数哈希 —— 改参数会让整个键变掉，但前三段不变，所以拿它认
        /// 「还是不是同一个块」（见 `RebindBlockPlacements` 里那段说明）。
        /// 前三段里不会有冒号（两个整数 + 一个定点小数），数到第三个冒号就够了。
        /// </summary>
        private static string KeyPrefix(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }
            int seen = 0;
            for (int i = 0; i < key.Length; i++)
            {
                if (key[i] != ':')
                {
                    continue;
                }
                seen++;
                if (seen == 3)
                {
                    return key.Substring(0, i);
                }
            }
            return key;
        }

        /// <summary>
        /// 按**引用**比较的字典键 —— 给「用事件对象反查旧行号」用。
        ///
        /// .NET Framework 4.8 没有 `ReferenceEqualityComparer`（那是 .NET 5 才有的），
        /// 而默认的 `Equals` 万一被 `LevelEvent` 重写过，就会把两个不同的事件
        /// 当成同一个 —— 那正好是这里最不能出的事（两块会被塞进同一行）。
        /// </summary>
        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T a, T b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(T value)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }

        /// <summary>
        /// 清单和位置表是不是**一一对应**（同一批对象、同一个顺序）。
        ///
        /// 比的是**对象引用**而不是个数：谱面一重新分析，`TimelineItem` 就是
        /// 一批新对象了（那正是"要重新绑定"的信号），而平时逐帧跑过来的是
        /// 同一批对象。
        /// </summary>
        private bool SamePlacementOrder(List<TimelineItem> ordered)
        {
            if (ordered == null || ordered.Count != blockPlacements.Count)
            {
                return false;
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(blockPlacements[i].Item, ordered[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 初次排列的比较：**按起点时间**，起点相同的按类别，最后用文件序兜底。
        ///
        /// 顺序必须是**时间序** —— 装箱是贪心的，乱序会让它白白多开好几行。
        /// </summary>
        private static int CompareInitialPlacement(TimelineItem a, TimelineItem b)
        {
            int byTime = a.StartTime.CompareTo(b.StartTime);
            if (byTime != 0)
            {
                return byTime;
            }
            int byLane = LaneDisplayOrder(a.Lane).CompareTo(LaneDisplayOrder(b.Lane));
            return byLane != 0 ? byLane : a.SourceOrder.CompareTo(b.SourceOrder);
        }

        /// <summary>
        /// 类别在时间轴上的先后（**只用来给"起点相同"的块定序**）。
        ///
        /// 这里曾经靠它把画布**分段**（装饰物一段、运镜一段…）。分段后来去掉了：
        /// 某一类的事件如果只在谱面开头出现过，它那一段到中后段就整片空着，
        /// 而别的类的块明明可以挪上来却被类压在下面 —— 用户圈出来的正是那两片空。
        /// **"严格分段"和"不留空"是互斥的**，选了后者。
        ///
        /// **这张表和 `TimelineLane` 的数值仍然故意解耦**：那个枚举的数值顺序
        /// 是喂给 `LaneBaseHues` / `LaneIconEventTypes` / `laneVisible` 那几张
        /// **按下标对齐**的表的，动一下会连带把颜色和图标错位。
        /// </summary>
        private static int LaneDisplayOrder(TimelineLane lane)
        {
            switch (lane)
            {
                case TimelineLane.Decoration: return 0;
                case TimelineLane.Camera: return 1;
                case TimelineLane.Visual: return 2;
                case TimelineLane.Track: return 3;
                case TimelineLane.Audio: return 4;
                case TimelineLane.Gameplay: return 5;
                default: return 6;
            }
        }

        /// <summary>摊平给 ImGui 画。按行升序排，因为画的时候是"一层层往下盖"。</summary>
        internal void CollectBlocks()
        {
            EnsureBlockPlacements();
            blockViews.Clear();
            int rows = 0;
            // **提到循环外。** `scnEditor.instance` 是属性，每访问一次都要查一遍，
            // 而它在一帧里不会变 —— 4237 个块就是白查 4237 次。`imBlockDrag` 同理
            // （循环里没有改它的地方）。
            scnEditor chartEditor = scnEditor.instance;
            // 两条拖动路径都要算「正在拖」：`imBlockDrag` 是 ImGui 时代那条，
            // `blockDragActive` 是现在 uGUI 标记块走的这条。少判一个，拖动期间
            // 位置表就会被谱面（还没提交的旧值）覆盖回去，块不跟手。
            bool liveTimeBase = !imBlockDrag && !blockDragActive && chartEditor != null
                && chartEditor.floors != null && chartEditor.floors.Count > 0;
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                TimelineItem item = placement.Item;

                // **车道被关掉的块只是不画，位置表里原样留着。**
                // 滤在这里而不是滤在清单里，切换车道才不会再动玩家的摆放。
                if (item != null && !LaneIsVisible(item.Lane))
                {
                    continue;
                }

                // **不在拖动时，横向位置直接问谱面。**
                //
                // 横向的真相就是 `floor` + `angleOffset`（拖动提交写的是它们，
                // 见 CommitBlockDrag）。这里必须**现算**，不能抄 `item.StartTime`：
                // 那个字段是上一次重新分析谱面时的快照，而 `RefreshData` 由脏标记
                // 驱动、下一帧才跑（有时干脆不跑）。撤销却是**同一帧**把谱面改回去的
                // —— 抄快照的话，行号（纵向）当场复位，下一帧横向又按着过期快照弹回
                // 拖动后的位置，等真刷新了才跳回来。表现就是"先复位 Y 再复位 X"，
                // 而且刷不刷得动看运气，有时 X 就再也不回来了。
                //
                // 现算没有这个竞态，而且和拖动提交用的是**同一个函数**
                // （CommitBlockDrag 也是读 GetEventStartTime 回填 item 的），
                // 块和参数栏的数值因此严丝合缝，不会一个在这个位置、一个写另一个值。
                //
                // 拖动中例外：那时位置表就是"手底下这一份"，必须听它的。
                bool liveTime = liveTimeBase && item != null && item.Event != null;
                if (liveTime)
                {
                    // 写回位置表，不只是给这一帧画 —— `TryGetBlockPlacement` 也是
                    // 从这里读的（松手记录撤销要用它），留着过期值就会记错。
                    placement.Time = GetEventStartTime(chartEditor, item.Event);
                }

                if (placement.Row + 1 > rows)
                {
                    rows = placement.Row + 1;
                }
                blockViews.Add(new BlockView
                {
                    Id = i,
                    Row = placement.Row,
                    // 这三个改用 item 上现成的（建 item 时就算好了，见那边的说明）——
                    // 原来是每帧每块现查字典，4237 个块就是每帧两万多次字符串哈希。
                    Resizable = item.Resizable,
                    Time = placement.Time,
                    Duration = float.IsNaN(placement.PreviewDuration)
                        // 时长同理：拖右边缘改的就是 `duration`，撤销时同一类过期。
                        ? (liveTime ? GetEventDuration(chartEditor, item.Event) : item.Duration)
                        : placement.PreviewDuration,
                    Name = item.Name,
                    BlockColor = item.BlockColor,
                    LabelColor = item.LabelColor,
                    Active = item.Active,
                    Selected = IsBlockSelected(item),
                    Event = item.Event,
                    Icon = item.Icon,
                    Curve = item.Event != null && item.Duration > 0.01f
                        ? GetEaseCurveValues(item.Ease)
                        : null
                });
            }
            BlockRowCount = rows;





            // **可用横向范围必须覆盖到最靠后的块。**
            //
            // `navigationDuration` 原来只取谱面时长 / 播放位置，遇到"块的时间
            // 超出谱面时长"（长事件、末尾的装饰物事件等）就会被算小 —— 于是
            // 横向滚动在某个位置被卡住，后面的块**永远滚不到**，看起来就是
            // "第 N 格之后的事件块全消失了"。
            float latestBlockTime = 0f;
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement == null)
                {
                    continue;
                }
                float end = placement.Time + Mathf.Max(0f, placement.Item.Duration);
                if (end > latestBlockTime)
                {
                    latestBlockTime = end;
                }
            }
            if (latestBlockTime > 0f)
            {
                navigationDuration = Mathf.Max(navigationDuration, latestBlockTime + 2f);
            }
            // **这里原来要把 blockViews 按（行号, Id）排一遍，去掉了。**
            //
            // 排序唯一的用途，是让绘制那边能「画到屏幕底下就 break」提前收工。
            // 代价却是每帧对 4237 个 struct 跑一整趟内省排序 —— 实测 1.4ms，而且
            // `blockPlacements` 是按事件源序存的、不是按行，所以顺序每帧都是乱的，
            // "缓存顺序"那条路走不通，只能每次真排。
            //
            // 改成让绘制那边遍历到底、靠它本来就有的两个 `continue` 跳过看不见的
            // 块：多空转 4000 次约 0.2ms，比排序便宜一个数量级。
            //
            // **顺序从此没有意义 —— 谁都不能再假设 `BlockViews` 是按行号有序的。**
        }

        // -------------------------------- ImGui 版块拖动：**这条是改谱面的**

        /// <summary>拖动方式。</summary>
        internal enum BlockDragMode
        {
            Move,
            ResizeLeft,
            ResizeRight
        }

        private bool imBlockDrag;
        private BlockDragMode imBlockDragMode;
        private DragOrigin imBlockDragOrigin;
        private float imBlockDragPressTime;
        /// <summary>
        /// 一起拖的那一批（[0] 是被拖的那个）。
        ///
        /// **只在"整体移动"时组队**：拉长/缩短是"改这一个的时长"，
        /// 把一批的时长一起改掉不会有人想要。而且被拖的那个必须**在选中里**
        /// 才组队 —— 否则拖一个没选中的块会把旁边选中的一批也带走。
        /// </summary>
        private readonly List<DragOrigin> imBlockDragOrigins = new List<DragOrigin>();

        internal bool ImBlockDragActive { get { return imBlockDrag; } }

        /// <summary>边缘多宽算「贴边」—— 和旧版同一个数，够宽才好瞄准。</summary>
        internal const float BlockEdgeZone = 6f;

        /// <summary>
        /// 拖动时「回到原位」的吸附半径（像素），和屏幕上那条竖线配套。
        /// 比格吸附优先：看得见原点的线，就该吸得回原点。
        /// </summary>
        private const float OriginSnapPixels = 14f;

        /// <summary>
        /// 开始拖一个块。
        ///
        /// **横向拖和拖边缘是"改谱面"**：它们走的是和 uGUI 那版**同一套**
        /// `CaptureDragOrigin`（抓原值）/ `SnapBlockTime`（吸附）/ `CommitBlockDrag`
        /// （写回 + 撤销 + 联机上报）—— 那套是踩出来的，不重写一份。
        /// 纵向（行号）是布局，不归这里管。
        /// </summary>
        internal bool BeginBlockDrag(LevelEvent target, BlockDragMode mode, float pointerTime)
        {
            imBlockDrag = false;

            // 只读模式：块拖不动 —— 拖块会改谱面里的时间（提示统一由 AllowEdit 发）。
            if (owner != null && !owner.AllowEdit())
            {
                return false;
            }

            if (target == null)
            {
                return false;
            }
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return false;
            }
            TimelineItem item = FindItemForEvent(target);
            if (item == null || !IsEventStillLive(editor, target))
            {
                return false;
            }
            imBlockDragMode = mode;
            imBlockDragOrigin = CaptureDragOrigin(item);
            imBlockDragOrigins.Clear();
            imBlockDragOrigins.Add(imBlockDragOrigin);
            if (mode == BlockDragMode.Move && multiSelection.Count > 1
                && multiSelection.Contains(target))
            {
                foreach (LevelEvent selected in multiSelection)
                {
                    if (selected == null || ReferenceEquals(selected, target))
                    {
                        continue;
                    }
                    TimelineItem other = FindItemForEvent(selected);
                    if (other != null)
                    {
                        imBlockDragOrigins.Add(CaptureDragOrigin(other));
                    }
                }
            }
            imBlockDragPressTime = pointerTime;
            imBlockDrag = true;
            return true;
        }

        /// <summary>拖到 `pointerTime` 这个时间点（秒）。吸附和"吸回原地"的防护照抄旧版。</summary>
        internal void DragBlockTo(float pointerTime)
        {
            if (!imBlockDrag)
            {
                return;
            }
            TimelineItem item = imBlockDragOrigin.Item;
            if (item == null)
            {
                return;
            }

            float delta = pointerTime - imBlockDragPressTime;
            float start = imBlockDragOrigin.Start;
            float duration = imBlockDragOrigin.StartDuration;

            switch (imBlockDragMode)
            {
                case BlockDragMode.Move:
                    // **先看有没有回到原位。** 屏幕上那条竖线标的就是它 ——
                    // 看得见就该吸得上。
                    //
                    // 只按格边界吸的话，拖回来会停在"最近的格"，未必是出发那一格：
                    // 块的起点本来就不一定压在格边界上（角度偏移、变速都会让它偏），
                    // 于是"拖回原来的地方"变成了"拖回附近的某个格"。
                    if (Mathf.Abs(start + delta - imBlockDragOrigin.Start)
                        <= OriginSnapPixels / Mathf.Max(1f, pixelsPerSecond))
                    {
                        start = imBlockDragOrigin.Start;
                    }
                    else
                    {
                        start = SnapBlockTime(start + delta);
                    }
                    break;
                case BlockDragMode.ResizeRight:
                {
                    // 防「吸回起点」：原时长比半格还短时，最近的边界就是起点自己，
                    // 时长会塌成 0、块缩到最小宽度，看起来像拉不动。宁可自由伸缩。
                    float rawEnd = start + duration + delta;
                    float end = SnapBlockTime(rawEnd);
                    if (end <= start)
                    {
                        end = Mathf.Max(start, rawEnd);
                    }
                    duration = Mathf.Max(0f, end - start);
                    break;
                }
                case BlockDragMode.ResizeLeft:
                {
                    // 右端钉住不动。同样防「吸过右端」——那样时长会变成负数。
                    float end = start + duration;
                    float newStart = SnapBlockTime(start + delta);
                    if (newStart >= end)
                    {
                        newStart = Mathf.Clamp(start + delta, 0f, end);
                    }
                    duration = Mathf.Max(0f, end - newStart);
                    start = newStart;
                    break;
                }
            }

            // **预览直接写在位置表上** —— 画布每帧现取，写进去就等于立刻跟手。
            // 旧版是写 marker 的 Rect，这边没有 marker。
            BlockPlacement placement = FindPlacementForEvent(item.Event);
            if (placement != null)
            {
                placement.Time = start;
                placement.PreviewDuration = duration;
            }

            // 整批跟着走，位移以被拖的那个为准（旧版同一套）。
            // 跟随者只挪**位置**，时长不动。
            if (imBlockDragOrigins.Count > 1
                && ReferenceEquals(imBlockDragOrigins[0].Item, item))
            {
                float shift = start - imBlockDragOrigin.Start;
                for (int i = 1; i < imBlockDragOrigins.Count; i++)
                {
                    DragOrigin follower = imBlockDragOrigins[i];
                    if (follower.Item == null || follower.Item.Event == null)
                    {
                        continue;
                    }
                    BlockPlacement followerPlacement = FindPlacementForEvent(follower.Item.Event);
                    if (followerPlacement != null)
                    {
                        followerPlacement.Time = follower.Start + shift;
                    }
                }
            }
        }

        /// <summary>松手：还原原值（让提交那边能判"真的改了没有"）→ 写回谱面。</summary>
        internal void EndBlockDrag()
        {
            if (!imBlockDrag)
            {
                return;
            }
            imBlockDrag = false;

            TimelineItem item = imBlockDragOrigin.Item;
            scnEditor editor = scnEditor.instance;
            if (item == null || item.Event == null || editor == null)
            {
                visualsDirty = true;
                return;
            }

            BlockPlacement placement = FindPlacementForEvent(item.Event);
            float finalStart = placement != null ? placement.Time : item.StartTime;
            float finalDuration = placement != null && !float.IsNaN(placement.PreviewDuration)
                ? placement.PreviewDuration
                : item.Duration;

            // 先还原成原值：CommitBlockDrag 要用**真实的 old** 判断有没有真的改动。
            item.StartTime = imBlockDragOrigin.Start;
            item.Duration = imBlockDragOrigin.StartDuration;

            CommitBlockDrag(editor, item, finalStart, finalDuration,
                imBlockDragOrigin.Floor, imBlockDragOrigin.AngleOffset,
                imBlockDragOrigin.DataDuration, imBlockDragOrigin.HadDuration);

            // 提交之后 item 上的时间是**真实生效的**那个（没改动时就是原值），
            // 位置表跟着它走，免得和参数栏显示的值对不上。
            if (placement != null)
            {
                placement.PreviewDuration = float.NaN;
                placement.Time = item.StartTime;
            }

            // 跟随者：和整批同一个时间位移，但**只改 angleOffset、不动 floor**
            // （见 CommitFollowerDrag 的说明）。每项用**它自己的最终时间**算位移 ——
            // 吸附之后各自对到了不同的格边界，位移不再相同。
            for (int i = 1; i < imBlockDragOrigins.Count; i++)
            {
                DragOrigin follower = imBlockDragOrigins[i];
                if (follower.Item == null || follower.Item.Event == null)
                {
                    continue;
                }
                BlockPlacement followerPlacement = FindPlacementForEvent(follower.Item.Event);
                float finalFollowerStart = followerPlacement != null
                    ? followerPlacement.Time
                    : follower.Item.StartTime;
                follower.Item.StartTime = follower.Start;
                follower.Item.Duration = follower.StartDuration;
                CommitFollowerDrag(editor, follower, finalFollowerStart - follower.Start);
                if (followerPlacement != null)
                {
                    followerPlacement.Time = follower.Item.StartTime;
                }
            }
            imBlockDragOrigins.Clear();
        }

        private BlockPlacement FindPlacementForEvent(LevelEvent target)
        {
            if (target == null)
            {
                return null;
            }
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement.Item != null && ReferenceEquals(placement.Item.Event, target))
                {
                    return placement;
                }
            }
            return null;
        }

        /// <summary>
        /// 横向平移：把视图往右挪 dx 像素（ImGui 那边拖空白处用的）。
        ///
        /// 和滚轮那支走同一套收尾（夹边界 + 标记手动导航）——
        /// 不标的话播放头会自动跟随，手动平移当场被拽回去。
        /// </summary>
        internal void PanBy(float dxPixels)
        {
            if (Mathf.Approximately(dxPixels, 0f))
            {
                return;
            }
            viewStartTime += dxPixels / Mathf.Max(1f, pixelsPerSecond);
            ClampViewStart();
            MarkManualNavigation();
            visualsDirty = true;
        }

        /// <summary>画布上选中的块数（排查框选用）。</summary>
        internal int SelectedBlockCount { get { return multiSelection.Count; } }

        /// <summary>当前播放头的位置（秒）。</summary>
        internal float PlayheadSeconds
        {
            get { return Mathf.Max(0f, EditorPlaybackBridge.ReadSnapshot().CurrentTime); }
        }

        /// <summary>
        /// 框选：把与这个框（**视口坐标，像素**）相交的块全选进多选。
        ///
        /// **直接算画布上的块**，不走旧的那套 marker 框选 —— 那套依赖 uGUI
        /// 的矩形和激活状态，而画布的块在另一套坐标里，借不过来。
        /// </summary>
        internal void SelectBlocksInRect(float left, float top, float right, float bottom)
        {
            SelectBlocksInRect(left, top, right, bottom, false);
        }

        /// <summary>
        /// 框选的完整版本：`additive` 为真时把框到的块**并进**已有的多选，
        /// 而不是先清空。
        ///
        /// Ctrl 是起框键，所以"加选框"只能借 Shift 区分 —— 见
        /// `EditorImGuiTimelineBody.BeginMarquee`。不分开的话，第二次框选
        /// 会把第一次那一批整个清掉（用户看到的就是"框完第二段，第一段没了"）。
        /// </summary>
        internal void SelectBlocksInRect(float left, float top, float right, float bottom,
            bool additive)
        {
            EnsureBlockPlacements();
            float speed = Mathf.Max(1f, pixelsPerSecond);
            if (!additive)
            {
                multiSelection.Clear();
            }
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement.Item == null || placement.Item.Event == null)
                {
                    continue;
                }
                float blockLeft = (placement.Time - viewStartTime) * speed;
                float blockRight = blockLeft + Mathf.Max(BlockMinWidth,
                    placement.Item.Duration * speed);
                if (blockRight < left || blockLeft > right)
                {
                    continue;
                }
                float blockTop = RulerHeight + placement.Row * BlockStridePixels - verticalScrollOffset;
                if (blockTop + BlockHeightPixels < top || blockTop > bottom)
                {
                    continue;
                }
                multiSelection.Add(placement.Item.Event);
            }

            // **把主选中那一个也并进来。**
            //
            // 框选只往 `multiSelection` 里塞，主选中那一个不在里面 —— 于是
            // "先点一个、再 Ctrl 框选几个"会得到"看着选中了 5 个、实际只有 4 个"：
            // 那个块还高亮着（高亮走的是 `IsBlockSelected`，主选中也算），
            // 但批量改参 / 启用停用 / 删除 / 复制都不带它。
            // 第一次 Ctrl+点击那条路早就为同一个原因做了这件事
            // （见 ToggleSelectionOf），框选这条路一直漏着。
            if (selectedEvent != null && !selectedEvent.IsDecoration)
            {
                multiSelection.Add(selectedEvent);
            }

            visualsDirty = true;
        }

        /// <summary>这个块在不在选中集里。选中状态归时间轴所有，ImGui 只是读。</summary>
        /// <summary>
        /// 按画布 ID 选中一个块。`additive` 为真就是 Ctrl 加选。
        ///
        /// **复用现有的选中语义**（`ToggleSelectionOf`）—— 那里面"第一次 Ctrl
        /// 点击要把主选中并进来"之类的边界条件是踩出来的，不值得推第二遍。
        ///
        /// 参数栏每 0.12 秒问一次 `PanelSelection()`，所以只改这两个字段就够，
        /// 不用额外通知谁。
        /// </summary>
        internal void SelectBlock(int id, bool additive)
        {
            if (id < 0 || id >= blockPlacements.Count)
            {
                return;
            }
            TimelineItem item = blockPlacements[id].Item;
            if (item == null || item.Event == null)
            {
                return;
            }
            if (additive)
            {
                ToggleSelectionOf(item);
                return;
            }

            // **已经在选中里就不要替换选中。**
            //
            // 多选之后想整批拖动，第一步就是按在其中一个上 —— 那一下要是
            // 把选中换成"只有它一个"，随后的拖动就只剩一个块了
            // （表现是"框选了几个、拖其中一个却只有它动"）。
            // 想收窄成单选，点别处或者点另一个块都行，这是编辑器里的常规做法。
            if (multiSelection.Contains(item.Event)
                || ReferenceEquals(selectedEvent, item.Event))
            {
                return;
            }

            selectedEvent = item.Event;
            multiSelection.Clear();
            // **游戏那一侧的选择也要跟上。**（装饰物和格子各有一份，
            // 见 SyncSceneSelection —— ImGui 点块这条路上原来漏了这一步。）
            SyncSceneSelection(item);
            visualsDirty = true;
        }

        /// <summary>
        /// 画布上这一块算不算"选中"。
        ///
        /// **直接复用 `IsTimelineItemSelected`。** 这两个判据曾经各写一份，于是
        /// 画布这一份漏了 `IsDecorationSelected`：从左栏那个装饰物列表选一个，
        /// 时间轴上对应的块**不高亮**。一份判据、一个地方改。
        /// </summary>
        private bool IsBlockSelected(TimelineItem item)
        {
            return IsTimelineItemSelected(item);
        }

        /// <summary>标尺上的一条刻度（格号）。</summary>
        internal struct RulerTick
        {
            /// <summary>视口内的 X。纵坐标固定是标尺那一条，不用给。</summary>
            internal float X;
            internal string Label;
        }

        /// <summary>一条格线（从标尺下沿一直落到视口底部）。</summary>
        internal struct RulerLine
        {
            internal float X;
        }

        private readonly List<RulerTick> rulerTickMarks = new List<RulerTick>();
        private readonly List<RulerLine> rulerLines = new List<RulerLine>();

        internal List<RulerTick> RulerTicks { get { return rulerTickMarks; } }
        internal List<RulerLine> RulerLines { get { return rulerLines; } }

        /// <summary>标尺那一屏的左右边界（时间轴单位：秒）。</summary>
        internal float ViewStartSeconds { get { return viewStartTime; } }

        internal float PixelsPerSecond { get { return Mathf.Max(1f, pixelsPerSecond); } }

        internal float BodyWidthPixels { get { return bodyWidth; } }

        internal float BodyHeightPixels { get { return bodyHeight; } }

        /// <summary>
        /// 摊平这一屏的标尺：刻度（格号）和格线。
        ///
        /// **数学是从 RefreshGrid 原样搬过来的，一个字没改** —— 刻度按「格子」走
        /// 而不是按时间均分（变速谱里格子之间的时长不相等，按时间均分就对不上
        /// 格线）；格宽用「可视区里的格数」均摊而不是全谱总格数（全谱平均在放大
        /// 之后会严重低估格宽，刻度糊成一片）。这两条都是踩出来的，别再推一遍。
        /// </summary>
        // 标尺缓存：视口（起点 / 宽 / 缩放 / 总格数）没变就不重算。
        private double rulerCacheViewStart = double.NaN;
        private float rulerCacheBodyWidth = -1f;
        private float rulerCachePixelsPerSecond = -1f;
        private int rulerCacheFloorCount = -1;

        internal void CollectRuler()
        {
            // **每帧重算标尺是白花的。**
            // 它按可视格数循环两遍并给每个刻度 `seqID.ToString()`，
            // 一屏两三百格就是几百次分配 —— 实测标尺占 3ms/帧。
            // 视口没动时直接用上一次的结果。
            scnEditor rulerEditor = scnEditor.instance;
            int rulerFloorCount = (rulerEditor != null && rulerEditor.floors != null)
                ? rulerEditor.floors.Count : 0;
            if (rulerCacheViewStart == viewStartTime
                && rulerCacheBodyWidth == bodyWidth
                && rulerCachePixelsPerSecond == pixelsPerSecond
                && rulerCacheFloorCount == rulerFloorCount)
            {
                return;
            }
            rulerCacheViewStart = viewStartTime;
            rulerCacheBodyWidth = bodyWidth;
            rulerCachePixelsPerSecond = pixelsPerSecond;
            rulerCacheFloorCount = rulerFloorCount;

            rulerTickMarks.Clear();
            rulerLines.Clear();

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return;
            }

            float viewEnd = viewStartTime + bodyWidth / Mathf.Max(1f, pixelsPerSecond);

            int firstVisible = LowerBoundFloors(editor.floors, viewStartTime);
            int lastVisible = LowerBoundFloors(editor.floors, viewEnd);
            int visibleFloors = Mathf.Max(1, lastVisible - firstVisible + 1);
            float pixelsPerFloor = bodyWidth / (float)visibleFloors;
            int step = Mathf.Max(1, Mathf.RoundToInt(84f / Mathf.Max(1f, pixelsPerFloor)));

            for (int f = firstVisible; f < editor.floors.Count; f++)
            {
                scrFloor floor = editor.floors[f];
                float floorTime = Convert.ToSingle(floor.entryTime);
                if (floorTime > viewEnd)
                {
                    break;
                }
                if (f % step != 0)
                {
                    continue;
                }
                rulerTickMarks.Add(new RulerTick
                {
                    X = (floorTime - viewStartTime) * pixelsPerSecond,
                    Label = floor.seqID.ToString()
                });
            }

            // 格线比刻度密得多：**挤到 5 像素以内就不画**，否则放大后会糊成一片灰。
            int floorIndex = LowerBoundFloors(editor.floors, viewStartTime);
            float lastX = float.NegativeInfinity;
            while (floorIndex < editor.floors.Count)
            {
                scrFloor floor = editor.floors[floorIndex++];
                float floorTime = Convert.ToSingle(floor.entryTime);
                if (floorTime > viewEnd)
                {
                    break;
                }
                float x = (floorTime - viewStartTime) * pixelsPerSecond;
                if (x - lastX < 5f)
                {
                    continue;
                }
                rulerLines.Add(new RulerLine { X = x });
                lastX = x;
            }
        }

        private void BuildUi()
        {
            float rootWidth = Mathf.Max(800f, root.rect.width);
            float rootHeight = Mathf.Max(220f, root.rect.height);
            bodyWidth = rootWidth - (GutterWidth + ContentShift) - 10f;
            bodyHeight = rootHeight - headerHeight - 8f;
            lastRootHeight = rootHeight;

            // 标题条实心底衬。高度取整个标题行（42）而不是别的面板那 28——
            // 这一行右边还要并排放缩放读数和几个开关，底衬得把它们一起罩住，
            // 才读成"一条横带"而不是"标题后面垫了块布"。
            CreateRoundedImage("HeadingBand", root, 0f, 0f, rootWidth, 42f, Palette.SurfaceBand);

            // 标题和它右边那排模式按钮整体右移，给左侧的装饰物列表让位。
            // 标题用第二强调色（金），和"能点的"紫色分开——四个面板的标题
            // 因此连成一条金色的信息层；左边那条实心竖条是这层共有的标记。
            // 条高 16、标题行 30 且从 7 起，所以条放在 14 才是居中的。
            const float barX = 18f;
            CreateImage("HeadingBar", root, barX + ContentShift, 14f,
                EditorStudioOverlay.HeadingBarWidth, EditorStudioOverlay.HeadingBarHeight,
                Palette.AccentSecondary);
            CreateText("Heading", root,
                barX + EditorStudioOverlay.HeadingBarWidth + EditorStudioOverlay.HeadingBarGap + ContentShift,
                7f - EditorStudioOverlay.HeadingTextLift, 78f, 30f, "时间轴", UiType.Title, Palette.AccentSecondary,
                TextAlignmentOptions.MidlineLeft, true);

            // 车道筛选按钮原来在这里，是一排文字按钮（全部/镜头/视效/…）。
            // 现在改成原生图标、无文字、多选，挂在预览面板的工具栏上——
            // 也就是预览画面上方那条，离它们控制的东西更近。

            followModeButton = CreateButton(
                "FollowMode", root, 136f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { ToggleFollowMode(); ApplyFollowModeIcon(); });
            ApplyFollowModeIcon();

            // 点事件时要不要把画面和时间轴一起挪过去。默认关，
            // 因为点一下通常只是想改参数，画面跟着跳会打断正在看的那段。
            selectionFollowButton = CreateButton(
                "SelectionFollow", root, 172f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { ToggleSelectionFollow(); ApplySelectionFollowIcon(); });
            ApplySelectionFollowIcon();

            // 拖动事件块时是否吸附到格边界。
            snapDragButton = CreateButton(
                "SnapDrag", root, 208f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { ToggleSnapDrag(); });
            ApplySnapDragIcon();

            // 图标按钮没有文字，靠悬停提示说明自己是谁。
            UiHoverTip.Attach(followModeButton,
                "固定播放头：开 = 播放头钉在正中、内容从中间滑过；关 = 视图归你手动滚");
            // 和旁边那颗「点轨道跟随」（准星）区分开：这一颗画的是
            // 「指针指着一个事件块」，一眼能看出触发对象是**事件块**。
            UiHoverTip.Attach(selectionFollowButton,
                "点事件跟随：在时间轴上点事件块，画面 1 秒内加速走过去");
            UiHoverTip.Attach(snapDragButton, "拖动吸附：拖块时吸附到格边界");

            // **「定位」「翻页」两颗模式按钮。**
            //
            // ImGui 那版把它们画在四个图标开关后面（也是 32×29 的方钮，只是
            // 里面写字）。迁回 uGUI 时漏了这两颗，现在补回来。
            locateButton = CreateButton(
                "LocateOnClick", root, 248f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { ToggleLocateOnTrackClick(); RefreshFeatureButtons(); });
            HideButtonLabel(locateButton);
            locateIcon = CreateImage("LocateIcon", locateButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, TextMuted);
            locateIcon.raycastTarget = false;

            // 「定位到播放头」：把视图横向滚到播放头位置（一次性的动作）。
            // 原来这颗是「自动翻页」开关，功能已取消 —— 视图不再自己跟着播放头跑。
            locatePlayheadButton = CreateButton(
                "LocatePlayhead", root, 284f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { LocateToPlayhead(); RefreshFeatureButtons(); });
            HideButtonLabel(locatePlayheadButton);
            locatePlayheadIcon = CreateImage("PlayheadIcon",
                locatePlayheadButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, TextPrimary);
            locatePlayheadIcon.raycastTarget = false;

            // **排版模式那颗按钮已搬进「设置窗口 → 常规设置」。**
            //
            // 时间轴标题行只留「跟随 / 吸附 / 定位」这类操作，模式开关归到设置里。
            // 快捷键 Ctrl+L 仍然保留（见 EditorPlaybackBridge），不占按钮位置。

            RefreshFeatureButtons();
            UiHoverTip.Attach(locateButton,
                "点轨道跟随：在预览里点轨道，画面 1 秒内加速走过去（Ctrl+J）");
            UiHoverTip.Attach(locatePlayheadButton,
                "定位到播放头：把时间轴横向滚到播放头位置（Ctrl+K）");

            BuildEventPalette(root);

            // **右侧那一串从右往左排**：缩放 +/− → 回到开头 → 缩放读数 →
            // 车道筛选。各自按右边距硬算会互相压（ImGui 那边也踩过）。
            const float stepSize = 28f;
            const float laneSize = 26f;
            const float laneGap = 4f;
            int laneCount = LaneCount;
            float lanesWidth = laneCount * laneSize + (laneCount - 1) * laneGap;

            float zoomInX = rootWidth - 16f - stepSize;
            float zoomOutX = zoomInX - 4f - stepSize;
            float rewindX = zoomOutX - 16f - 32f;
            float zoomTextX = rewindX - 12f - 52f;
            float lanesX = zoomTextX - 20f - lanesWidth;

            zoomText = CreateText("Zoom", root, zoomTextX, 8f, 52f, 28f, "100%", 11f, TextMuted, TextAlignmentOptions.Center, false);

            Button rewindButton = CreateIconButton("GoToStart", root, rewindX, 32f,
                CreateRewindSprite(), TextSecondary, ReturnToStart, "回到开头");
            rewindButton.name = "GoToStart";

            CreateIconButton("ZoomOut", root, zoomOutX, stepSize,
                CreateMinusSprite(), TextSecondary,
                delegate { ZoomAt(0.8f, bodyWidth * 0.5f); }, "缩小");
            CreateIconButton("ZoomIn", root, zoomInX, stepSize,
                CreatePlusSprite(), TextSecondary,
                delegate { ZoomAt(1.25f, bodyWidth * 0.5f); }, "放大");

            // 车道筛选：从预览工具栏搬回时间轴第一行（ImGui 版就在这里）。
            laneButtons.Clear();
            laneIcons.Clear();
            for (int lane = 0; lane < laneCount; lane++)
            {
                int index = lane;
                RectTransform host = CreateRect("LaneFilter" + lane, root,
                    lanesX + lane * (laneSize + laneGap), 7f, laneSize, 29f);
                Image hit = host.gameObject.AddComponent<Image>();
                hit.color = Color.clear;
                hit.raycastTarget = true;
                Button laneButton = host.gameObject.AddComponent<Button>();
                laneButton.targetGraphic = hit;
                laneButton.transition = Selectable.Transition.None;
                laneButton.onClick.AddListener(delegate { ToggleLane(index); });
                UiHoverTip.Attach(laneButton, "筛选：显示 / 隐藏这一类");

                Sprite icon = GetLaneIcon(lane);
                Image iconImage = CreateImage("Icon", host, 4f, 6f, 18f, 16f,
                    IsLaneVisible(lane) ? TextPrimary : TextMuted);
                iconImage.sprite = icon;
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
                laneButtons.Add(laneButton);
                laneIcons.Add(iconImage);
            }

            // 视图锁：一个按钮同时管相机位置和缩放。
            //
            // 就放在筛选按钮之后，摘要列从它之后开始。这排条的
            // 右端已经排满了——时间读数右对齐，
            // 最后几位数字一直顶到播放控件——所以早先尝试把锁放在
            // 那里，结果被「00:00.000」盖住了。
            // 和前面几颗模式开关排在一起（原来钉在 550，孤零零挂在中间）。
            viewLockButton = CreateButton(
                "ViewLock",
                root,
                322f + ContentShift,
                7f,
                32f,
                29f,
                string.Empty,
                delegate { ToggleViewLock(); }
            );
            ApplyViewLockIcon();
            UiHoverTip.Attach(viewLockButton, "视图锁：锁住预览的相机位置和缩放");

            // 这个设置会持久化，所以启动时必须告知求值器——
            // 否则会出现一开始实际未锁、按钮却已显示为锁定状态的情况。
            if (Main.Settings != null)
            {
                EditorPlaybackBridge.ApplyPreviewPositionLock(Main.Settings.LockPreviewPosition);
            }

            // 时间读数摆在**整条时间轴的正中**（用户要求）。原来贴右缘，
            // 和缩放/筛选挤成一串；居中之后它是这一行的视觉锚点。
            const float timeWidth = 240f;
            float timeX = Mathf.Max(200f, (rootWidth - timeWidth) * 0.5f);
            timeText = CreateText("Time", root, timeX, 8f, timeWidth, 28f,
                "00:00.000 / 00:00.000", 13f, Palette.AccentSecondary,
                TextAlignmentOptions.Center, false);

            // 摘要放到读数左边那段空档里，右对齐贴住读数。
            // 起点让开视图锁（322 + 32）那一格。
            float summaryLeft = 364f + ContentShift;
            summaryText = CreateText("Summary", root, summaryLeft, 8f,
                Mathf.Max(60f, timeX - 12f - summaryLeft), 28f, string.Empty, 11f,
                TextMuted, TextAlignmentOptions.MidlineRight, false);
            summaryText.overflowMode = TextOverflowModes.Ellipsis;

            // **跳转栏：输入格号 + 点击跳过去。**
            //
            // 放在时间读数右边那段空档里（左侧 1/3 落点、滑过去），不挤占
            // 摘要和右侧的缩放/车道筛选。窄窗口里塞不下就整条不建 ——
            // 让它压住时间读数或车道筛选比没有更糟。
            const float jumpWidth = 128f;
            float jumpRoom = lanesX - 12f - (timeX + timeWidth + 16f);
            if (jumpRoom >= jumpWidth)
            {
                float jumpX = Mathf.Min(
                    timeX + timeWidth + 24f,
                    lanesX - 12f - jumpWidth);
                BuildJumpBar(root, jumpX);
            }

            // 时间轴主体是浮在面板上的一张卡片，不是面板上的凹槽。它曾经用 PanelAlt
            // ——比面板暗——那会把它压到表面之下；而设计稿要求
            // 模块的内容区读成一张浮起的卡片，所以改用较亮的取值和
            // 圆角 sprite。略带透明，免得一大块平板矩形读成
            // 面板上凿的洞。
            //
            // 它的尺寸已经通过 Relayout 跟随面板高度，所以缩放手柄
            // 这里不需要额外处理。
            Image viewportImage = CreateImage("Viewport", root, GutterWidth + ContentShift, headerHeight, bodyWidth, bodyHeight, TimelineCard);
            viewportImage.raycastTarget = true;
            if (roundedSprite != null)
            {
                viewportImage.sprite = roundedSprite;
                viewportImage.type = Image.Type.Sliced;
            }
            viewport = viewportImage.rectTransform;
            viewport.gameObject.AddComponent<RectMask2D>();
            // 格线要压在块**下面**，所以单独一层、先建。
            gridLayer = CreateRect("GridLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            // 事件块层：夹在标尺上沿与视口之间，播放头在它上面。
            //
            // **不要再给它加 RectMask2D。** 视口自己已经有遮罩了；这一层
            // 建的时候按当时的 bodyHeight 定尺寸，面板拉高之后它不跟着变 ——
            // 多出来那一段就被它自己裁掉（用户看到「下面又被剪了」）。
            blockLayer = CreateRect("BlockLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            markerLayer = CreateRect("MarkerLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            // 这些层必须在填充各自的池之前就存在，否则
            // 表头会被挂到场景根节点而不是画布下，导致
            // 完全渲染不出来。
            sectionHeaderLayer = CreateRect("SectionHeaderLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            badgeLayer = CreateRect("CollapsedBadgeLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            // 网格线是时间块层的独立同级节点。让可交互的事件块
            // 压在这些线上方，同时把播放头留在
            // 最顶层的可视元素。
            markerLayer.SetAsLastSibling();
            sectionHeaderLayer.SetAsLastSibling();
            badgeLayer.SetAsLastSibling();

            Image rulerImage = CreateImage("RulerBackdrop", viewport, 0f, 0f, bodyWidth, RulerHeight, RulerStrip);
            rulerImage.raycastTarget = true;
            rulerBackdrop = rulerImage.rectTransform;
            CreateImage(
                "RulerBottomBorder",
                rulerBackdrop,
                0f,
                RulerHeight - 1f,
                bodyWidth,
                1f,
                WithAlpha(Color.white, 0.08f)
            ).raycastTarget = false;
            for (int i = 0; i < majorTickLabels.Count; i++)
            {
                majorTickLabels[i].rectTransform.SetAsLastSibling();
            }

            // 播放头做成青色发光。
            //
            // 辉光是一张横向 alpha 渐变的贴图，不是几层硬边矩形——硬边的
            // 矩形叠出来，最外那条会有清晰边界，看着就是「光被剪掉了」。
            // 渐变贴图让亮度从中心向外平滑衰减，才读得出光晕。
            //
            // 芯线单独画在最上层，保持 2px 锐利：有锐利的芯、有散开的光，
            // 才像一支发光的指示线，而不是一团糊。
            //
            // 辉光层是 playhead 的子节点，拖动时整体一起走，不必另外同步位置。
            playhead = CreateRect("Playhead", viewport, 0f, 0f, PlayheadCoreWidth, bodyHeight);
            playheadGlow = CreateImage("PlayheadGlow", playhead,
                -(PlayheadGlowWidth - PlayheadCoreWidth) * 0.5f, 0f,
                PlayheadGlowWidth, bodyHeight, Color.white);
            playheadGlow.sprite = GetPlayheadGlowSprite();
            playheadGlow.type = Image.Type.Simple;
            playheadGlow.color = PlayheadColor;
            playheadGlow.raycastTarget = false;
            playheadCore = CreateImage("PlayheadCore", playhead,
                0f, 0f, PlayheadCoreWidth, bodyHeight, PlayheadColor);
            playheadCore.raycastTarget = false;
            // 顶部的手柄也跟着发光色走。
            playheadHandle = CreateRoundedImage("PlayheadHandle", playhead,
                -(PlayheadHandleWidth - PlayheadCoreWidth) * 0.5f, 0f,
                PlayheadHandleWidth, 9f, PlayheadColor);
            playheadHandle.raycastTarget = false;

            Image resizeHandleImage = CreateImage(
                "ResizeHandle",
                root,
                0f,
                0f,
                rootWidth,
                ResizeHandleHeight,
                new Color(0f, 0f, 0f, 0.001f)
            );
            CreateRoundedImage(
                "Grip",
                resizeHandleImage.rectTransform,
                rootWidth * 0.5f - 24f,
                2f,
                48f,
                3f,
                UiTheme.Current.BorderStrong
            ).raycastTarget = false;
            TimelineResizeHandle resizeHandle = resizeHandleImage.gameObject.AddComponent<TimelineResizeHandle>();
            resizeHandle.Initialize(owner, root);
            resizeHandleImage.transform.SetAsLastSibling();

            UpdateFilterVisuals();
        }

        /// <summary>
        /// 标题行右侧的跳转栏：输入格号 + 一颗「跳过去」按钮。
        ///
        /// 长谱面里想精确回到某一格，拖动播放头是在几千格、几千像素里找一个
        /// 点；输入格号再点一下才是稳的。回车和按钮走同一条路。
        /// </summary>
        private void BuildJumpBar(RectTransform root, float x)
        {
            const float fieldWidth = 92f;
            const float fieldHeight = 29f;
            const float gap = 4f;
            const float buttonWidth = 32f;

            RectTransform host = CreateRect("JumpField", root, x, 7f, fieldWidth, fieldHeight);
            Image background = host.gameObject.AddComponent<Image>();
            background.color = Palette.FieldWell;
            // 输入框自己要能接住点击 —— 标题条那块底衬铺满整行，
            // 不显式要射线的话，点下去只会落到后面那层底衬上。
            background.raycastTarget = true;

            TextMeshProUGUI text = CreateStretchedInputText("Text", host, 8f,
                string.Empty, 12.5f, Palette.TextPrimary);
            TextMeshProUGUI placeholder = CreateStretchedInputText("Placeholder", host, 8f,
                "格号", 12.5f, Palette.TextMuted);

            TMP_InputField field = host.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.contentType = TMP_InputField.ContentType.IntegerNumber;
            field.characterLimit = 5;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.richText = false;
            field.customCaretColor = true;
            field.caretColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            field.caretWidth = 2;
            field.caretBlinkRate = 0.85f;
            field.textViewport = host;
            field.textComponent = text;
            field.placeholder = placeholder;
            // 打错字按回车只是回退到原值，不要把框清空。
            field.restoreOriginalTextOnEscape = true;
            field.onSubmit.AddListener(delegate { JumpToFloorFromInput(); });
            jumpField = field;

            // 图标按钮和别的工具按钮同一规格（32 宽、29 高、图标内缩 4/6），
            // 这样这一排读起来是一条线，而不是中间突然插了个陌生的控件。
            CreateIconButton(
                "JumpToFloor",
                root,
                x + fieldWidth + gap,
                buttonWidth,
                GetJumpIconSprite(),
                Palette.TextSecondary,
                JumpToFloorFromInput,
                "跳转到指定格（输入后按回车也行）");
        }

        /// <summary>
        /// 输入框里的文字：横向铺满父级、上下留 1 像素，左边留出内边距。
        ///
        /// TMP_InputField 会按自己的 caret 位置去摆 textComponent，所以这层
        /// 必须是「拉伸锚点」而不是固定尺寸 —— 否则光标和文字会各走各的。
        /// </summary>
        private TextMeshProUGUI CreateStretchedInputText(string name, RectTransform parent,
            float padding, string value, float size, Color color)
        {
            RectTransform rect = CreateRect(name, parent, 0f, 0f, 0f, 0f);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(padding, 1f);
            rect.offsetMax = new Vector2(-padding, -1f);

            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        /// <summary>
        /// 从输入框读格号并跳过去。空着或者不是数字就只提示，不乱跳。
        /// </summary>
        internal void JumpToFloorFromInput()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return;
            }

            string raw = jumpField == null || jumpField.text == null
                ? string.Empty
                : jumpField.text.Trim();
            int wanted;
            if (!int.TryParse(raw, out wanted))
            {
                if (Main.Overlay != null)
                {
                    Main.Overlay.SetTransientStatus("请输入要跳转的格号");
                }
                if (jumpField != null)
                {
                    // 回到输入状态，省得用户再点一次。
                    jumpField.ActivateInputField();
                    jumpField.Select();
                }
                return;
            }

            JumpToFloor(wanted);
        }

        /// <summary>
        /// 跳到指定格：选中它、让预览播放头走过去、再把时间轴滑到那一格。
        ///
        /// 三件事得一起做 —— 只滑时间轴不动播放头，画面还停在原处；
        /// 只动播放头不滑时间轴，落点在视口外，等于没看见。
        /// </summary>
        internal void JumpToFloor(int floorId)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return;
            }

            // 超出范围就夹到两端：用户输入 99999 时应该落在最后一格，
            // 而不是什么都不发生。
            int clamped = Mathf.Clamp(floorId, 0, editor.floors.Count - 1);
            scrFloor target = editor.floors[clamped];
            if (target == null)
            {
                return;
            }

            // 和「从面板加事件」同一条路：只把选中挪过去，别让原版
            // SelectFloor 的跟随逻辑再动一次镜头（cameraJump:false）。
            EditorPlaybackBridge.MarkNextFloorSelect(
                EditorPlaybackBridge.FloorSelectPlayheadSync);
            editor.SelectFloor(target, cameraJump: false);

            // 播放头加速走过去。预览会话没起来时它返回 false ——
            // 那种情况下时间轴照样滑，至少能让用户看到那一格。
            EditorPlaybackBridge.JumpPreviewToFloor(target);

            if (viewInitialized && pixelsPerSecond > 0f)
            {
                if (followMode == TimelineFollowMode.CenteredPlayhead)
                {
                    // 固定播放头下不需要滑动，放开手动让位即可。
                    centeredHoldUntil = 0f;
                }
                else
                {
                    // 和「定位到播放头」用同一个落点：视口左侧 1/3，
                    // 跳过去之后右边还留着大部分可看的空间。
                    float targetTime = (float)target.entryTime;
                    viewGlideTarget = targetTime - bodyWidth * 0.34f
                        / Mathf.Max(1f, pixelsPerSecond);
                    viewGliding = true;
                }
                MarkManualNavigation();
                visualsDirty = true;
            }

            if (jumpField != null)
            {
                // 夹过之后就写回夹过的值，用户一眼能看出实际跳到了哪。
                jumpField.SetTextWithoutNotify(clamped.ToString());
            }
            if (Main.Overlay != null)
            {
                Main.Overlay.SetTransientStatus("已跳转到第 " + clamped + " 格");
            }
        }

        /// <summary>跳转按钮的图标，只生成一次。</summary>
        private static Sprite GetJumpIconSprite()
        {
            if (jumpIconSprite != null)
            {
                return jumpIconSprite;
            }
            jumpIconSprite = CreatePinIconSprite(false);
            return jumpIconSprite;
        }

        /// <summary>
        /// 「定位到播放头」的图标：同一枚定位针，但针尖下面多一条短横线
        /// （落点/基线）。整根针相应缩到 88%，给那条线腾出位置 ——
        /// 两个按钮挨着，靠“有没有基线”一眼区分，比换色更稳。
        /// </summary>
        private static Sprite CreateLocatePlayheadIconSprite()
        {
            return CreatePinIconSprite(true);
        }

        /// <summary>
        /// 定位针图标：**map pin**（中间一个圆环）。
        ///
        /// 直接按用户给的 SVG 描：1024×1024 坐标系里四个子路径 ——
        /// 定位针外沿、内沿、圆环外圈、圆环内圈 —— 采样成多边形后用
        /// 奇偶规则填充，等价于 SVG 的填充结果。外框和圆环都是实心带，
        /// 不是描边，所以水滴的尖端是尖的、圆环宽度处处一致。
        ///
        /// `withBaseline` 为真时再加一条针尖下方的横线（定位到播放头用）。
        /// </summary>
        private static Sprite CreatePinIconSprite(bool withBaseline)
        {
            const int size = 24;
            const int samples = 4;
            const int big = size * samples;

            // 三次贝塞尔采样。步数按 96×96 的采样网格定：24 步在
            // 最大的一圈上每步约 7 个采样像素，肉眼已经看不出多边形。
            void AddCubic(List<Vector2> points, Vector2 start, Vector2 control1,
                Vector2 control2, Vector2 end)
            {
                const int steps = 24;
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float u = 1f - t;
                    points.Add(u * u * u * start
                        + 3f * u * u * t * control1
                        + 3f * u * t * t * control2
                        + t * t * t * end);
                }
            }

            void AddCircle(List<Vector2> points, float centerX, float centerY, float radius)
            {
                const int steps = 64;
                for (int i = 0; i < steps; i++)
                {
                    float angle = i * Mathf.PI * 2f / steps;
                    points.Add(new Vector2(centerX + Mathf.Cos(angle) * radius,
                        centerY + Mathf.Sin(angle) * radius));
                }
            }

            // ---- 子路径 1：定位针外沿（尖端朝下）----
            var pinOuter = new List<Vector2>(96);
            pinOuter.Add(new Vector2(512f, 0f));
            AddCubic(pinOuter, new Vector2(512f, 0f),
                new Vector2(744.374857f, 0f), new Vector2(928.914286f, 195.072f),
                new Vector2(928.914286f, 427.008f));
            AddCubic(pinOuter, new Vector2(928.914286f, 427.008f),
                new Vector2(928.914286f, 656.530286f), new Vector2(761.417143f, 883.2f),
                new Vector2(537.526857f, 1009.6f));
            pinOuter.Add(new Vector2(512f, 1024f)); // 针尖
            pinOuter.Add(new Vector2(486.473143f, 1009.6f));
            AddCubic(pinOuter, new Vector2(486.473143f, 1009.6f),
                new Vector2(262.582857f, 883.2f), new Vector2(95.085714f, 656.530286f),
                new Vector2(95.085714f, 427.008f));
            AddCubic(pinOuter, new Vector2(95.085714f, 427.008f),
                new Vector2(95.085714f, 195.072f), new Vector2(279.625143f, 0f),
                new Vector2(512f, 0f));

            // ---- 子路径 2：定位针内沿（外沿内缩约 101.7）----
            var pinInner = new List<Vector2>(96);
            var innerStart = new Vector2(512f, 101.668571f);
            pinInner.Add(innerStart);
            AddCubic(pinInner, innerStart,
                new Vector2(340.699429f, 101.668571f), new Vector2(198f, 246.930285f),
                new Vector2(198f, 427.008f));
            AddCubic(pinInner, new Vector2(198f, 427.008f),
                new Vector2(198f, 601.673143f), new Vector2(323.659429f, 790.235429f),
                new Vector2(512f, 906.313143f));
            AddCubic(pinInner, new Vector2(512f, 906.313143f),
                new Vector2(700.342857f, 790.235429f), new Vector2(826f, 601.673143f),
                new Vector2(826f, 427.008f));
            AddCubic(pinInner, new Vector2(826f, 427.008f),
                new Vector2(826f, 246.857143f), new Vector2(683.300571f, 101.668571f),
                new Vector2(512f, 101.668571f));

            // ---- 子路径 3 / 4：中间圆环的外圈与内圈 ----
            // 圆心在定位针头部中心，半径 177.81 / 76.215。
            var ringOuter = new List<Vector2>(64);
            var ringInner = new List<Vector2>(64);
            AddCircle(ringOuter, 512f, 420.425f, 177.81f);
            AddCircle(ringInner, 512f, 420.425f, 76.215f);

            List<Vector2>[] contours = { pinOuter, pinInner, ringOuter, ringInner };

            // 「定位到播放头」那版：整根针缩到 88%、往下挪一点，
            // 针尖停在横线上方而不是压住它。这里只算缩放参数 ——
            // 采样时做逆变换，省得把两百多个顶点再复制一遍。
            float pinScale = withBaseline ? 0.88f : 1f;
            float pinTop = withBaseline ? 12.8f : 0f;

            // 奇偶规则：落在这几个子路径里的次数是奇数就填。
            // 针身带 = 外沿里 + 内沿外（1 次）；圆环带 = 外沿 + 内沿 +
            // 圆环外圈（3 次）；圆环的孔 = 4 次，不填。
            bool Inside(List<Vector2> polygon, float x, float y)
            {
                bool inside = false;
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    Vector2 a = polygon[i];
                    Vector2 b = polygon[j];
                    if ((a.y > y) != (b.y > y))
                    {
                        float t = (y - a.y) / (b.y - a.y);
                        if (x < a.x + t * (b.x - a.x))
                        {
                            inside = !inside;
                        }
                    }
                }
                return inside;
            }

            bool[] on = new bool[big * big];
            const float scale = 1024f / big;
            for (int y = 0; y < big; y++)
            {
                // 纹理第 0 行是底边，而 SVG 的 y 轴朝下 —— 采样时翻一下，
                // 针尖才会落在图标底部。
                float sy = 1024f - (y + 0.5f) * scale;
                for (int x = 0; x < big; x++)
                {
                    float sx = (x + 0.5f) * scale;
                    // 逆变换回“未缩放的 1024 空间”再测子路径。
                    float px = 512f + (sx - 512f) / pinScale;
                    float py = (sy - pinTop) / pinScale;
                    bool filled = false;
                    for (int c = 0; c < contours.Length; c++)
                    {
                        if (Inside(contours[c], px, py))
                        {
                            filled = !filled;
                        }
                    }
                    // 横线不参与缩放，直接在真实坐标里测；它和针身
                    // 不重叠，所以异或不会抵消。
                    if (withBaseline && sx >= 280f && sx <= 744f
                        && sy >= 954f && sy <= 998f)
                    {
                        filled = !filled;
                    }
                    on[y * big + x] = filled;
                }
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            Color32[] pixels = new Color32[size * size];
            const int perPixel = samples * samples;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        int row = (y * samples + sy) * big + x * samples;
                        for (int sx = 0; sx < samples; sx++)
                        {
                            if (on[row + sx])
                            {
                                hits++;
                            }
                        }
                    }
                    byte alpha = (byte)Mathf.RoundToInt(255f * hits / perPixel);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            texture.filterMode = FilterMode.Bilinear;
            return Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// 左侧那块竖条：装饰物列表。
        ///
        /// 它挂在 root 上、而不是 viewport 里——viewport 上有 RectMask2D，
        /// 会把画到它边界外的东西裁掉，而竖条本来就落在 viewport 左边。
        /// 背景用 TimelineCard，跟事件画布同一档，两块读起来是同一层表面。
        ///
        /// 高度按整块面板算，跟事件按钮占几行无关——见 DecoPaneTop。
        /// </summary>
        /// <summary>
        /// 接上外部建好的装饰物竖条。
        ///
        /// 它原本由本视图建在自己的面板里（贴着左边一条竖的），现在搬到
        /// 左栏、和媒体网格上下分栏——时间轴因此腾出了整宽。
        ///
        /// 但**通知关系不能断**：数据什么时候变、选中什么时候变，只有本视图
        /// 知道（它持有事件表），所以竖条由宿主建、建好后注入回来。
        /// </summary>
        internal void AttachDecorationPane(EditorDecorationList pane)
        {
            decoPane = pane;
        }

        /// <summary>竖条的最小高度。再矮就没法放搜索框和一行列表了。</summary>
        private const float RowPaneMinimumHeight = 140f;

        private void RefreshData(bool force)
        {
            // 拖动期间绝不能重建 TimelineItem。
            //
            // 拖动中的操作对象是 marker.Item，而这个方法会把所有 item
            // 换成新造的一批，手上的引用当场变成孤儿——后续 OnDrag 改的是
            // 一个已经不在任何行里的对象，表现为「拖到一半块就不动了」。
            // 松手提交后 dataFingerprint 会变，紧接着的那次轮询
            // 自然会重建，所以这里直接跳过不会漏掉刷新。
            if (blockDragActive && !force)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            int fingerprint = ComputeDataFingerprint(editor);
            if (!force && dataFingerprintValid && fingerprint == dataFingerprint)
            {
                return;
            }

            // 保住纵向滚动位置。
            //
            // 下面那句 verticalScrollOffset = 0f 是给「换了一张谱面」
            // 准备的，但这个方法也会被「同一次编辑的增量刷新」调用
            // （比如刚加了一个事件）。两者都清零，会让用户每加一个
            // 事件，时间轴就跳回顶部——他正在看下面那些行的时候尤其烦。
            float keptScroll = verticalScrollOffset;

            dataFingerprint = fingerprint;
            dataFingerprintValid = true;
            for (int i = 0; i < LaneCount; i++)
            {
                laneItems[i].Clear();
            }
            layoutItems.Clear();
            verticalScrollOffset = 0f;

            totalDuration = 0f;
            navigationDuration = 0f;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                // 空谱面也把滚动位置装回去，否则退出到这里时它会
                // 停在零，而 keptScroll 白留一份。
                verticalScrollOffset = keptScroll;
                layoutDirty = true;
                visualsDirty = true;
                return;
            }

            int order = 0;
            foreach (LevelEvent levelEvent in editor.events)
            {
                AddTimelineItem(editor, levelEvent, false, order++);
            }
            foreach (LevelEvent decoration in editor.decorations)
            {
                AddTimelineItem(editor, decoration, true, order++);
            }
            for (int i = 0; i < LaneCount; i++)
            {
                laneItems[i].Sort(CompareTimelineItems);
            }

            totalDuration = Mathf.Max(totalDuration, Convert.ToSingle(editor.floors[editor.floors.Count - 1].entryTime));
            navigationDuration = totalDuration;
            if (selectedEvent != null && !ContainsEventReference(selectedEvent))
            {
                selectedEvent = null;
            }

            // 多选里也要清掉**已经不在谱面里**的条目 —— 撤销 / 联机覆盖都会
            // 把事件对象换掉，留着旧引用会让"选中 N 个"一直报着已删除的东西。
            if (multiSelection.Count > 0)
            {
                multiSelection.RemoveWhere(delegate (LevelEvent candidate)
                {
                    return candidate == null || !ContainsEventReference(candidate);
                });
            }

            // 装回刚才保住的滚动位置，再按新的内容高度夹一次——
            // 谱面变短时它会把值收进合法范围。
            verticalScrollOffset = keptScroll;
            ClampViewStart();
            ClampVerticalScroll();
            UpdateSummary();
            layoutDirty = true;
            visualsDirty = true;

            // 车道图标是在预览面板建按钮时取的，而那时谱面数据可能还没进来。
            // 数据一刷新就让那边再取一次：GetLaneIcon 的回退分支依赖事件已就绪。
            if (owner != null)
            {
                owner.RefreshLaneFilterIcons();
                // 放置事件面板的图标同样依赖谱面数据，一并刷新。
                RefreshEventPalette();
            }
        }

        // ==== 以下方法从备份原样拼回（英文注释）====

        private void AddTimelineItem(scnEditor editor, LevelEvent levelEvent, bool isDecoration, int order)
        {
            if (levelEvent == null)
            {
                return;
            }

            TimelineLane lane;
            if (!TryClassify(levelEvent.eventType, isDecoration, out lane))
            {
                return;
            }
            float startTime = GetEventStartTime(editor, levelEvent);
            float duration = GetEventDuration(editor, levelEvent);
            var item = new TimelineItem
            {
                Event = levelEvent,
                Lane = lane,
                StartTime = startTime,
                Duration = duration,
                FloorId = levelEvent.floor,
                SourceOrder = order,
                IsDecoration = isDecoration,
                Active = levelEvent.active,
                Name = GetTimelineItemName(levelEvent, lane),
                BlockColor = GetEventTypeColor(levelEvent.eventType, lane),
                LabelColor = GetEventLabelColor(levelEvent.eventType, lane),
                // 这三个每帧每块都要用 —— 在这里算一次，别留到 CollectBlocks。
                Resizable = GetCanResize(levelEvent),
                Icon = GetEventIcon(levelEvent),
                Ease = GetTimelineEase(levelEvent)
            };
            laneItems[(int)lane].Add(item);
            totalDuration = Mathf.Max(totalDuration, startTime + duration);
        }

        /// <summary>
        /// 这个事件能不能拉时长 —— 判据和 `CanResizeItem` 完全一样，只是按事件问。
        /// 建 `TimelineItem` 那一步要用这个答案，那时 item 还没造出来。
        /// </summary>
        private static bool GetCanResize(LevelEvent levelEvent)
        {
            if (levelEvent == null || levelEvent.info == null)
            {
                return false;
            }
            Dictionary<string, ADOFAI.PropertyInfo> properties = levelEvent.info.propertiesInfo;
            return properties != null && properties.ContainsKey("duration");
        }

        /// <summary>
        /// 给每个事件补算身份（`Key`）：**类型 + 格 + 同类型同格里的序号**。
        ///
        /// **为什么序号要按「同类型同格」分组数**：只用「类型 + 格」的话，
        /// 同一格上的两个 MoveCamera 会算出同一个身份、在工程表里互相覆盖，
        /// 布局就串了。加了组内序号才分得开。
        ///
        /// **组内按文件序数**（调用方给的清单已经按 `SourceOrder` 排好），
        /// 所以稳定性来自「在谱面文件里的先后」，而不是会被外部编辑搅乱的数组下标。
        ///
        /// 什么会改这个身份：在 AES **之外**往同一格插/删同类型事件 ——
        /// 那之后的序号整体挪一位，就是约定的「名册对不上」。
        /// 改参数、改别的格、加别的类型，都不动它。
        /// </summary>
        private void EnsureEventKeys(List<TimelineItem> ordered)
        {
            if (ordered == null)
            {
                return;
            }

            // **身份 = `类型 : 格 : 起点时间 : 参数`**（不碰谱面，不写任何字段）。
            //
            // 为什么这个组合在这一版是稳的：
            //   · **改参数**（透明度、颜色、缓速…）→ 键确实会变，但重绑那边
            //     有"按位置兜底"（见 `RebindBlockPlacements`），改完块**留在原地**；
            //   · **拖动** → `DragBlockTo` 只写 `placement`（画布预览），
            //     事件数据要等 `EndBlockDrag` 才提交 —— 拖动**全程**键不变；
            //     但**提交之后**格和时间都变了，键跟着变，所以重绑那边还留了一手
            //     「按事件对象认」（见 `RebindBlockPlacements` 里的 `oldRowsByEvent`），
            //     否则拖完块会掉到最下面一行；
            //   · **撤销 / 重做** → 事件回到哪个状态，键就是那个状态的键；
            //   · **加 / 删事件** → 只有那一个的键消失，别的照旧。
            //
            // **第四段（参数）是不能省的**：同一格上可以叠着好几个同类型、
            // 同时刻的事件 —— 最常见的是一格挂三个 SetFilter。前三段对它们
            // 完全一样，不靠参数就分不开，保存时互相覆盖、重绑时全挤回同一行。
            // 实测症状：「三个滤镜叠在一起，手动分开又自动叠回去」。
            for (int i = 0; i < ordered.Count; i++)
            {
                TimelineItem item = ordered[i];
                if (item == null)
                {
                    continue;
                }
                if (item.Event == null)
                {
                    item.Key = null;
                    continue;
                }
                // **算过一次就别再算。**
                //
                // 这个方法挂在每帧都会跑的 `EnsureBlockPlacements` 上，而 `item`
                // 只在**重新分析谱面**时才重建（见那边的注释：换一批新对象），
                // 不是每帧新造的 —— 所以重复计算纯属浪费。偏偏它一点都不便宜：
                // 每个事件都要取一遍参数字典、把 `键=值` 拼成字符串、**再对这些
                // 字符串排序**、最后 join 起来。
                //
                // 大规模实测（2286 个块）：`CollectBlocks` 一共 37.5ms，而真正的
                // 绘制只占 0.1ms —— 钱基本都花在这儿。加上这一句，它只在必要时算。
                if (item.Key != null)
                {
                    continue;
                }
                item.Key = (int)item.Event.eventType + ":" + item.FloorId
                    + ":" + item.StartTime.ToString("F4")
                    + ":" + HashEventData(item.Event);
            }
        }

        /// <summary>
        /// 把一个事件的参数拼成一个可用于比较的字符串。
        ///
        /// **按键排序之后再拼**：字典的遍历顺序不保证，不排序的话同一个事件
        /// 两次算出来的串可能不同，键就会自己抖。
        ///
        /// 只用来**区分同一批里的事件**，不要求它完美 —— 值里的复合类型
        /// （数组之类）`ToString()` 出来是一样的，那种情况下两个事件会被当成
        /// 同一个键、互相覆盖。实测那不影响：位置一样、它们本来就该在同一行。
        /// </summary>
        private static string HashEventData(LevelEvent levelEvent)
        {
            Dictionary<string, object> data = levelEvent.GetData();
            if (data == null || data.Count == 0)
            {
                return "";
            }
            var parts = new List<string>(data.Count);
            foreach (KeyValuePair<string, object> pair in data)
            {
                parts.Add(pair.Key + "="
                    + (pair.Value == null ? "null" : pair.Value.ToString()));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        private void RebuildDynamicRows()
        {
            layoutItems.Clear();
            for (int lane = 0; lane < LaneCount; lane++)
            {
                if (LaneIsVisible((TimelineLane)lane))
                {
                    layoutItems.AddRange(laneItems[lane]);
                }
            }
            layoutItems.Sort(CompareTimelineItemsForLayout);

            // Each event type gets its own band of rows, so a row never mixes colours
            // and same-length blocks of one type line up. Types are walked in the order
            // they first appear in the chart, which keeps the bands stable as events are
            // edited instead of reshuffling with the time.
            Dictionary<int, TimelineSection> collapsedByType = SnapshotCollapsedSections();
            Dictionary<int, float> scrollByType = SnapshotRowScroll();
            layoutSections.Clear();
            layoutRows.Clear();
            float minimumDuration = (MinimumPointEventWidth + EventGap)
                / Mathf.Max(1f, pixelsPerSecond);
            // 图层模式的排法是「所有事件按时间重叠全局装箱」，不按类型分组：
            // 每个事件贪心放进第一个放得下的行，行高固定。所以这一模式
            // 只产出一个 section，把全部事件丢进去一起装箱。
            //
            // 分类收窄靠预览工具栏上那排车道图标。
            var bucketsByType = new Dictionary<int, List<TimelineItem>>();
            var orderedTypes = new List<int>();
            if (layoutMode == TimelineLayoutMode.LayerStack)
            {
                bucketsByType[LayerStackTypeKey] = new List<TimelineItem>(layoutItems);
                orderedTypes.Add(LayerStackTypeKey);
            }
            else
                // 卷帘模式：每个事件类型一个桶，桶内按类型独立装箱。
                {
                for (int scanIndex = 0; scanIndex < layoutItems.Count; scanIndex++)
                {
                    int scanKey = GetLayoutTypeKey(layoutItems[scanIndex]);
                    List<TimelineItem> existingBucket;
                    if (!bucketsByType.TryGetValue(scanKey, out existingBucket))
                    {
                        existingBucket = new List<TimelineItem>();
                        bucketsByType.Add(scanKey, existingBucket);
                        orderedTypes.Add(scanKey);
                    }
                    existingBucket.Add(layoutItems[scanIndex]);
                }
            }

            for (int typeIndex = 0; typeIndex < orderedTypes.Count; typeIndex++)
            {
                int typeKey = orderedTypes[typeIndex];
                int bandStart = layoutRows.Count;
                List<TimelineItem> bucket = bucketsByType[typeKey];
                for (int itemIndex = 0; itemIndex < bucket.Count; itemIndex++)
                {
                    TimelineItem item = bucket[itemIndex];

                    float layoutDuration = Mathf.Max(item.Duration, minimumDuration);
                    var interval = new RowInterval
                    {
                        StartTime = item.StartTime,
                        EndTime = item.StartTime + layoutDuration,
                        Item = item
                    };

                    // Only this type's rows are candidates, and they are scanned in
                    // order, so a long block claims an upper row and everything it
                    // overlaps is pushed below it.
                    int rowIndex = bandStart;
                    int insertionIndex = 0;
                    for (; rowIndex < layoutRows.Count; rowIndex++)
                    {
                        if (TryFindRowInsertion(layoutRows[rowIndex], interval, out insertionIndex))
                        {
                            break;
                        }
                    }
                    if (rowIndex == layoutRows.Count)
                    {
                        layoutRows.Add(new List<RowInterval>());
                        insertionIndex = 0;
                    }

                    item.DisplayRow = rowIndex;
                    layoutRows[rowIndex].Insert(insertionIndex, interval);
                }

                bool layerStack = layoutMode == TimelineLayoutMode.LayerStack;
                var section = new TimelineSection
                {
                    TypeKey = typeKey,
                    // 图层模式没有「某个类型」可言——它是全部事件混装，
                    // 所以不该套用事件类型的名字和图标。
                    Title = layerStack ? "全部事件" : GetEventName((LevelEventType)typeKey),
                    Icon = layerStack ? null : GetTypeIcon((LevelEventType)typeKey),
                    EventRowBandStart = bandStart,
                    EventRowCount = layoutRows.Count - bandStart
                };
                TimelineSection collapsed;
                if (collapsedByType.TryGetValue(typeKey, out collapsed))
                {
                    section.Collapsed = collapsed.Collapsed;
                }
                // 平铺模式没有表头，也就没有东西能把一个折叠的盒子展开 ——
                // 万一留着旧状态，整条会被折成一行又没法救。所以强制展开。
                if (layerStack)
                {
                    section.Collapsed = false;
                }
                float keptScroll;
                if (scrollByType.TryGetValue(typeKey, out keptScroll))
                {
                    section.RowScrollOffset = keptScroll;
                }
                // 层堆叠模式没有框在滚，每个分类整个展开。残留的滚动偏移
                // 必须清掉：那个模式的布局不减去它（行直接从框顶往下排），
                // 而标记的位置仍然减——两侧不一致会让整框的行整体上移。
                if (layoutMode != TimelineLayoutMode.SectionRoll)
                {
                    section.RowScrollOffset = 0f;
                }
                section.ShownRows = section.Collapsed
                    ? Mathf.Min(1, section.EventRowCount)
                    : section.EventRowCount;
                layoutSections.Add(section);
            }

            // 这里原来还有一次 `BuildRowTable()` —— 它把 marker 池、分类框池、
            // 表头池、行底衬池、标尺池全建出来。现在时间轴是 ImGui 画的，
            // 那些池子一个都不用了，连创建一起删了。
            layoutItems.Sort(CompareTimelineItems);

            layoutDirty = false;
            ClampVerticalScroll();
            UpdateSummary();
        }

        private static int GetLayoutTypeKey(TimelineItem item)
        {
            return (int)item.Event.eventType;
        }

        /// <summary>
        /// Captures which bands were collapsed before the rows are recomputed. The
        /// sections themselves are rebuilt from scratch each pass, so the state has to
        /// survive by type rather than by object identity.
        /// </summary>
        /// <summary>
        /// Each band's own scroll, keyed by type.
        ///
        /// Needed for the same reason the collapsed set is: RebuildDynamicRows clears
        /// layoutSections and builds new ones, so a scroll kept only on the old objects is lost
        /// the moment anything marks the layout dirty -- which is exactly what scrolling does,
        /// so the band snapped back to its first row on the next frame.
        /// </summary>

        /// <summary>
        /// Captures which bands were collapsed before the rows are recomputed. The
        /// sections themselves are rebuilt from scratch each pass, so the state has to
        /// survive by type rather than by object identity.
        /// </summary>
        /// <summary>
        /// Each band's own scroll, keyed by type.
        ///
        /// Needed for the same reason the collapsed set is: RebuildDynamicRows clears
        /// layoutSections and builds new ones, so a scroll kept only on the old objects is lost
        /// the moment anything marks the layout dirty -- which is exactly what scrolling does,
        /// so the band snapped back to its first row on the next frame.
        /// </summary>
        private Dictionary<int, float> SnapshotRowScroll()
        {
            var scroll = new Dictionary<int, float>();
            for (int i = 0; i < layoutSections.Count; i++)
            {
                TimelineSection section = layoutSections[i];
                if (section != null && section.RowScrollOffset > 0.01f)
                {
                    scroll[section.TypeKey] = section.RowScrollOffset;
                }
            }
            return scroll;
        }

        private Dictionary<int, TimelineSection> SnapshotCollapsedSections()
        {
            var collapsed = new Dictionary<int, TimelineSection>();
            for (int i = 0; i < layoutSections.Count; i++)
            {
                TimelineSection section = layoutSections[i];
                if (section != null && section.Collapsed)
                {
                    collapsed[section.TypeKey] = section;
                }
            }
            return collapsed;
        }
        /// <summary>
        /// The editor icon for an event type, straight from the sprite table the game
        /// fills when the editor opens. Also used for the band header.
        /// </summary>

        /// <summary>
        /// The editor icon for an event type, straight from the sprite table the game
        /// fills when the editor opens. Also used for the band header.
        /// </summary>
        private static Sprite GetTypeIcon(LevelEventType eventType)
        {
            if (GCS.levelEventIcons == null)
            {
                return null;
            }

            Sprite icon;
            return GCS.levelEventIcons.TryGetValue(eventType, out icon) ? icon : null;
        }

        /// <summary>
        /// The band a row belongs to, by the full row table (headers included). Only
        /// meaningful for rows that belong to a band; a row a collapsed band hides
        /// still resolves, which keeps a stale DisplayRow harmless.
        /// </summary>

        private static bool TryFindRowInsertion(
            List<RowInterval> intervals,
            RowInterval candidate,
            out int insertionIndex)
        {
            int low = 0;
            int high = intervals.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (intervals[middle].StartTime < candidate.StartTime)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            insertionIndex = low;
            bool overlapsPrevious = insertionIndex > 0
                && intervals[insertionIndex - 1].EndTime > candidate.StartTime + 0.0001f;
            bool overlapsNext = insertionIndex < intervals.Count
                && intervals[insertionIndex].StartTime < candidate.EndTime - 0.0001f;
            return !overlapsPrevious && !overlapsNext;
        }

        private void RefreshGeometry()
        {
            visualsDirty = false;
            if (!viewInitialized || bodyWidth <= 1f)
            {
                return;
            }

            if (layoutDirty)
            {
                RebuildDynamicRows();
            }
            // **这里是旧排版模型的"摆位"那一段，已经整段删掉了。**
            //
            // 原来它每帧按分类框把 320 个 marker、分类框、表头、行底衬、
            // 折叠徽标一个个摆到 RectTransform 上。现在时间轴是 ImGui 画的
            // （见 EditorImGuiTimelineBody），这一段全成了白算。
            //
            // 上面那几行**保留**：`RebuildDynamicRows` 会重建 `layoutItems`
            // （有哪些事件），而画布那边正是拿它当数据源。行装箱、layoutRows /
            // layoutRowY 那些留到第二阶段再清。
        }
        /// <summary>
        /// Content-space Y of the last row a band shows, in the band's current state.
        /// </summary>

        /// <summary>
        /// How many of a band's visible rows start below the viewport. Counted from the
        /// far end so a band with thousands of rows does not walk all of them.
        /// </summary>

        /// <summary>
        /// The editor icon the game uses for an event type. The table is filled by
        /// scnEditor when the editor opens, so it is normally present; a missing entry
        /// just means the block is laid out without an icon.
        /// </summary>

        /// <summary>
        /// The editor icon the game uses for an event type. The table is filled by
        /// scnEditor when the editor opens, so it is normally present; a missing entry
        /// just means the block is laid out without an icon.
        /// </summary>
        private static Sprite GetEventIcon(LevelEvent levelEvent)
        {
            if (levelEvent == null || GCS.levelEventIcons == null)
            {
                return null;
            }

            Sprite icon;
            return GCS.levelEventIcons.TryGetValue(levelEvent.eventType, out icon) ? icon : null;
        }

        /// <summary>
        /// Reads the ease a timeline item animates with. Returns Linear when the
        /// property is absent or disabled, matching how the evaluator treats an
        /// unspecified ease.
        /// </summary>

        /// <summary>
        /// Reads the ease a timeline item animates with. Returns Linear when the
        /// property is absent or disabled, matching how the evaluator treats an
        /// unspecified ease.
        /// </summary>
        internal static Ease GetTimelineEase(LevelEvent levelEvent)
        {
            if (levelEvent == null || levelEvent.GetData() == null
                || !levelEvent.GetData().ContainsKey("ease"))
            {
                return Ease.Linear;
            }

            bool disabled;
            if (levelEvent.disabled != null
                && levelEvent.disabled.TryGetValue("ease", out disabled) && disabled)
            {
                return Ease.Linear;
            }

            object value;
            if (!levelEvent.GetData().TryGetValue("ease", out value) || value == null)
            {
                return Ease.Linear;
            }

            if (value is Ease)
            {
                return (Ease)value;
            }

            try
            {
                return (Ease)Enum.Parse(typeof(Ease), value.ToString(), true);
            }
            catch
            {
                return Ease.Linear;
            }
        }

        // Curvature of each ease, sampled once over the normalised domain. The view
        // mirrors the game's own EaseGraphRenderer: sample DOVirtual.EasedValue, then
        // normalise by the peak so overshooting eases such as Back and Elastic fit
        // inside the block instead of being clipped.
        private static readonly Dictionary<Ease, float[]> EaseCurveValues = new Dictionary<Ease, float[]>();

        // The curve is drawn as a line strip of this many segments. Block widths vary
        // by a factor of ~30, so a fixed segment count would leave the widest blocks
        // visibly faceted; 128 keeps a 1800px block under 7px per segment.
        private const int EaseCurveSamples = 128;

        // Stroke width in UI pixels, and how far the curve stays clear of the block
        // outline. Linear and Quad rest flat against the top and bottom edges, so
        // without the inset the stroke merges into the block border.
        // Feathering spreads the stroke's apparent weight, so the solid core is set a
        // little wider than a plain hairline to end up reading as the same thickness.
        // 1.4 → 2.4：块变小/变暗之后 1.4 的线几乎看不见。
        /// <summary>块高低于这个值就不画缓速曲线（扁到没有可读形状）。</summary>
        private const float EaseCurveHideBelowHeight = 26f;

        internal const float EaseCurveThickness = 2.4f;
        internal const float EaseCurveMarginX = 3f;
        internal const float EaseCurveMarginY = 2f;

        // Width of the feathered edge drawn outside the stroke, in UI pixels. A hard
        // edged strip stair-steps once a steep ease is squeezed into a block only ~18
        // pixels tall; fading the outermost band to transparent lets those steps blend
        // into each other. The strip is then cut back by the whole feathered width so
        // even the faint outer edge stays inside its rect.
        private const float EaseCurveFeather = 0.75f;

        // Radius of the dot capping each end of the curve, and the width of its feathered
        // rim. The dot needs its own, thinner feather: the stroke's is proportionally
        // large enough that reusing it would leave a core barely wider than the stroke
        // itself, and the dot would read as a blunt line end rather than a marker.
        // 2.2 → 3.4：端点圆点原来和线同宽，视觉上读不出"这里有端点"。
        internal const float EaseCurveDotRadius = 3.4f;
        private const float EaseCurveDotFeather = 0.6f;

        /// <summary>
        /// 把所有缓速类型的采样点先算出来。
        ///
        /// 每种 `Ease` 第一次被画到时要现算 `EaseCurveSamples`（128）个采样点，
        /// 每个都是一次 `DOVirtual.EasedValue`。一张用了好几种缓速的谱，
        /// 这些计算全挤在进时间轴的头几帧里 —— 用户看到的就是
        /// "事件块上的缓速曲线加载很慢"。
        ///
        /// 放这里给加载页调（F8 打开工作台时），逐帧摊不摊开都无所谓：
        /// 全部算一遍也就几十次 × 128，加载页本来就压着最短 0.35 秒。
        /// </summary>
        internal static void PrecomputeEaseCurves()
        {
            System.Array all = System.Enum.GetValues(typeof(Ease));
            for (int i = 0; i < all.Length; i++)
            {
                try
                {
                    GetEaseCurveValues((Ease)all.GetValue(i));
                }
                catch
                {
                    // 个别占位档（`Unset` / `INTERNAL_*`）算不出来不影响别的，跳过。
                }
            }
        }

        private static float[] GetEaseCurveValues(Ease ease)
        {
            float[] cached;
            if (EaseCurveValues.TryGetValue(ease, out cached))
            {
                return cached;
            }

            float[] values = new float[EaseCurveSamples];
            float peak = 0f;
            for (int i = 0; i < EaseCurveSamples; i++)
            {
                float ratio = (float)i / (EaseCurveSamples - 1);
                float value = DOVirtual.EasedValue(0f, 1f, ratio, ease);
                values[i] = value;
                if (value > peak)
                {
                    peak = value;
                }
            }

            if (peak > 1f)
            {
                float scale = 1f / peak;
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] *= scale;
                }
            }

            EaseCurveValues[ease] = values;
            return values;
        }
        /// <summary>
        /// An Image whose geometry is a curve instead of a quad. Subclassing is what
        /// makes this reliable: CanvasUpdateRegistry rebuilds every graphic each time it
        /// is marked dirty, so a mesh pushed straight onto the canvas renderer would be
        /// overwritten by the next layout or filter pass.
        /// </summary>
        private sealed class EaseCurveImage : Image
        {
            /// <summary>当前块高对应的缩放系数（OnPopulateMesh 里算，画端点球用）。</summary>
            private float _dotScale = 1f;
            private float[] _values;

            internal void SetCurve(float[] values)
            {
                _values = values;
                // 换了数据必须标脏，否则网格还是上一条曲线的样子。
                SetVerticesDirty();
            }

            protected override void OnPopulateMesh(VertexHelper vh)
            {
                vh.Clear();
                if (_values == null || _values.Length < 2)
                {
                    return;
                }

                Rect rect = GetPixelAdjustedRect();
                int segments = _values.Length - 1;

                // Two nested strips make up the stroke: a solid core of the requested
                // thickness, and a feathered band around it that fades to transparent.
                // The end dots reach further out than the stroke, so the whole mesh is
                // inset by their radius as well and they still sit inside the block.
                // **粗细/留白跟着块高自适应。**
                //
                // 原来线宽 2.4、羽化 0.75、端点半径 3.4 都是写死的，
                // 三者加起来 inset≈5.35；行高被 ALT+滚轮压到最扁时曲线框只剩
                // 十几像素，上下各让掉 5 像素之后有效高度接近 0 ——
                // 表现就是「所有曲线都拉成一条直线」。
                // 现在按短边等比缩放，并把 inset 压到短边的 28% 以内。
                float shortSide = Mathf.Min(rect.width, rect.height);
                float scale = Mathf.Clamp(shortSide / 42f, 0.45f, 1f);
                _dotScale = scale;
                // **线宽给下限**：最扁一档按比例只剩不到 1px，看起来像断线。
                float thickness = Mathf.Max(1.2f, EaseCurveThickness * scale);
                float inner = thickness * 0.5f;
                float outer = inner + Mathf.Max(0.4f, EaseCurveFeather * scale);
                float inset = Mathf.Min(outer + EaseCurveDotRadius * scale,
                    shortSide * 0.28f);
                float left = rect.xMin + inset;
                float right = rect.xMax - inset;
                float width = right - left;
                float bottom = rect.yMin + inset;
                float height = Mathf.Max(0.01f, rect.height - inset * 2f);

                Color fade = new Color(color.r, color.g, color.b, 0f);
                var vertex = UIVertex.simpleVert;
                float previousX = left;
                float previousY = bottom;
                float startX = left;
                float startY = bottom;
                float endX = left;
                float endY = bottom;
                for (int i = 0; i <= segments; i++)
                {
                    float x = left + width * i / segments;
                    float y = bottom + height * Mathf.Clamp01(_values[i]);
                    if (i == 0)
                    {
                        startX = x;
                        startY = y;
                    }
                    if (i == segments)
                    {
                        endX = x;
                        endY = y;
                    }

                    // Scale the normal by the half thickness so each quad between two
                    // stations spans the stroke's exact width, instead of inheriting
                    // the angle of the segment it belongs to.
                    float normalX = 0f;
                    float normalY = 1f;
                    if (i > 0)
                    {
                        float tangentX = x - previousX;
                        float tangentY = y - previousY;
                        float length = Mathf.Sqrt(tangentX * tangentX + tangentY * tangentY);
                        if (length > 0.0001f)
                        {
                            normalX = -tangentY / length;
                            normalY = tangentX / length;
                        }
                    }

                    vertex.color = fade;
                    vertex.position = new Vector3(x + normalX * outer, y + normalY * outer, 0f);
                    vh.AddVert(vertex);
                    vertex.color = color;
                    vertex.position = new Vector3(x + normalX * inner, y + normalY * inner, 0f);
                    vh.AddVert(vertex);
                    vertex.position = new Vector3(x - normalX * inner, y - normalY * inner, 0f);
                    vh.AddVert(vertex);
                    vertex.color = fade;
                    vertex.position = new Vector3(x - normalX * outer, y - normalY * outer, 0f);
                    vh.AddVert(vertex);

                    previousX = x;
                    previousY = y;
                }

                // Each station contributes four vertices in the order outer+, inner+,
                // inner-, outer-, so one segment is the solid core between the two
                // inner edges plus one feathered band on each side.
                for (int i = 0; i < segments; i++)
                {
                    int current = i * 4;
                    int next = current + 4;
                    vh.AddTriangle(current, next, current + 1);
                    vh.AddTriangle(current + 1, next, next + 1);
                    vh.AddTriangle(current + 1, next + 1, current + 2);
                    vh.AddTriangle(current + 2, next + 1, next + 2);
                    vh.AddTriangle(current + 2, next + 2, current + 3);
                    vh.AddTriangle(current + 3, next + 2, next + 3);
                }

                AddDot(vh, startX, startY);
                AddDot(vh, endX, endY);
            }

            /// <summary>
            /// Caps an end of the curve with a small filled disc, built from a centre
            /// vertex ringed by a feathered rim so it antialiases like the stroke does.
            /// </summary>
            private void AddDot(VertexHelper vh, float centreX, float centreY)
            {
                const int wedges = 12;
                int centre = vh.currentVertCount;
                var vertex = UIVertex.simpleVert;
                vertex.color = color;
                vertex.position = new Vector3(centreX, centreY, 0f);
                vh.AddVert(vertex);

                // **端点球也跟着块高缩。** 下限 1.6px，最扁时仍看得见一个点。
                float radius = Mathf.Max(1.6f, EaseCurveDotRadius * _dotScale);
                float feather = Mathf.Max(0.35f, EaseCurveDotFeather * _dotScale);
                float inner = Mathf.Max(0.6f, radius - feather);
                Color fade = new Color(color.r, color.g, color.b, 0f);
                for (int i = 0; i < wedges; i++)
                {
                    float angle = Mathf.PI * 2f * i / wedges;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);

                    vertex.color = color;
                    vertex.position = new Vector3(
                        centreX + cos * inner, centreY + sin * inner, 0f);
                    vh.AddVert(vertex);

                    vertex.color = fade;
                    vertex.position = new Vector3(
                        centreX + cos * radius,
                        centreY + sin * radius,
                        0f);
                    vh.AddVert(vertex);
                }

                for (int i = 0; i < wedges; i++)
                {
                    int innerCurrent = centre + 1 + i * 2;
                    int outerCurrent = innerCurrent + 1;
                    int innerNext = centre + 1 + (i + 1) % wedges * 2;
                    int outerNext = innerNext + 1;
                    vh.AddTriangle(centre, innerCurrent, innerNext);
                    vh.AddTriangle(innerCurrent, outerCurrent, outerNext);
                    vh.AddTriangle(innerCurrent, outerNext, innerNext);
                }
            }
        }
        // ---------------------------------------------------------------- 块拖动
        //
        // 拖动分两段：拖的时候只改内存里的 TimelineItem（视觉上块跟着橡皮筋
        // 走），松手时才算一次真实编辑——写回 angleOffset / duration / floor，
        // 记一步撤销，并重排布局。
        //
        // 为什么拖动中不写数据：RefreshData 每 0.5 秒轮询一次，而它的指纹
        // 已经把 angleOffset 和 duration 算进去了。拖动中一旦写数据，
        // 下一次轮询就会重建 TimelineItem，把手上这个 marker.Item 变成孤儿，
        // 后续 OnDrag 改的就是一个没人看的对象。
        //
        // 为什么拖动中不置 layoutDirty：RebuildDynamicRows 是贪心装箱，
        // 途中重排会让块在手指底下跳行。只让它在原行内伸缩，松手再整理。

        private bool blockDragActive;

        /// <summary>
        /// 诊断用：这一轮"卡住"是否已经报过。
        ///
        /// 只在 VerboseDiagnostics 下有意义。卡住会持续很多帧，不设这个会把日志刷爆。
        /// </summary>
        private bool diagStuckLogged;

        /// <summary>诊断用：跟随者越界报过几条（限流，免得刷爆日志）。</summary>
        private int diagClampLogged;

        /// <summary>诊断用：拖动中的 marker 被回收循环拦下过几次（限流）。</summary>

        /// <summary>
        /// 诊断：抓「块拖动收尾没走到」。
        ///
        /// 正常路径是松手 → marker.OnEndDrag → EndBlockDrag / CancelBlockDrag 把
        /// blockDragActive 清掉。而 marker 故意不实现 IPointerUpHandler，退出拖动
        /// 只有 OnEndDrag 这一条路；OnEndDrag 里又有个「框选优先」的分支会直接
        /// return。所以只要有一次没走到收尾，这个标志就永久挂着：
        /// 块按 dragPointerLocal 停在空中（RefreshGeometry 的分支），
        /// 滚轮被 OnScroll 挡掉，RefreshData 也被跳过（撤回后画面不动）。
        ///
        /// 这里不做任何修复，只把当时的完整状态打出来 —— 先看清是哪条分支漏的。
        /// </summary>
        private void DiagCheckStuckBlockDrag()
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            if (blockDragActive && !Input.GetMouseButton(0))
            {
                if (diagStuckLogged)
                {
                    return;
                }
                diagStuckLogged = true;
                Main.Logger.Log("[TimelineDrag] 卡住：左键已松开但 blockDragActive 还在"
                    + " marqueeActive=" + marqueeActive
                    + " dragPointerValid=" + dragPointerValid
                    + " members=" + dragOrigins.Count
                    + " item=" + (blockDragItem == null ? "null" : blockDragItem.Name));
            }
            else if (!blockDragActive)
            {
                diagStuckLogged = false;
            }
        }

        /// <summary>
        /// 正在拖的那个 item。
        ///
        /// 用它把拖动中的块**提到最上层**：块同一行里还排着别的块，横向滑动时
        /// 会钻到它们后面，看起来像"拖没了"（松手重排后就又出现了）。
        /// 每帧摆块时都要重提一次 —— marker 是**池化**的，同一个 marker 对象
        /// 下一帧可能被分配给别的 item，只在开始拖时提一次会提错人。
        /// </summary>
        private TimelineItem blockDragItem;

        /// <summary>
        /// 正在拖的那个 marker 组件。
        ///
        /// 用途只有一个：在 RefreshGeometry 末尾的回收循环里放行它。
        /// 被拖的块滑出视口时会被跳过分配（那两处 continue），落进回收循环
        /// 就会 SetActive(false)；GameObject 一 inactive，Unity 就派发不到
        /// OnEndDrag（GetComponents 拿不到组件），drag 状态永久卡住。
        /// </summary>
        private TimelineMarker blockDragMarker;

        /// <summary>
        /// 拖动中块要跟的光标位置（`markerLayer` 的左上角原点坐标），
        /// 以及"抓取偏移" —— 按下时块的左上角相对光标的位移。
        ///
        /// 有了抓取偏移，块才是"被拎着走"而不是"左上角吸到光标上"。
        /// </summary>
        private Vector2 dragPointerLocal;
        private Vector2 dragGrabOffset;
        private bool dragPointerValid;

        /// <summary>
        /// 按下时光标的位置。跟随者的**垂直**位移就按它算：
        /// 被拖的那个自由跟光标，跟随者跟同一个垂直偏移 —— 只跟水平的话，
        /// 整组会散成一条斜线（实测就是这个报告）。
        /// </summary>
        private Vector2 dragPressPointerLocal;

        /// <summary>
        /// 拖动开始时，**每个参与拖动的事件**的快照。
        ///
        /// 多选一起拖时每个事件的原始值都不同，而 `blockDragOriginal*` 那几个
        /// 字段只装得下被拖的那一个。所以整批的原始值收在这里，提交时逐个
        /// 传给 CommitBlockDrag。
        ///
        /// **第 0 个永远是被拖的那一个** —— 整批的位移以它为基准算。
        /// </summary>
        private struct DragOrigin
        {
            internal TimelineItem Item;
            internal float Start;

            /// <summary>拖动前 item 上的时长 —— **秒**。只用来说明"要保住多长"。</summary>
            internal float StartDuration;

            /// <summary>
            /// 拖动前**事件数据里的** `duration` —— **拍数**。
            ///
            /// 撤销要还原的是它。这两个千万不能混：数据里存的是拍数，
            /// 而 item 上的 Duration 是秒。拿秒当拍数写回撤销记录，
            /// 撤销之后那个事件的时长就彻底错了（实测：块的长度变了）。
            /// </summary>
            internal float DataDuration;

            internal int Floor;
            internal float AngleOffset;
            internal bool HadDuration;

            /// <summary>
            /// 拖动前块在第几行（纵向）。整批一起拖时，跟随者要按**同一个行位移**
            /// 走，所以每一项都得记下自己的起点。
            /// </summary>
            internal int Row;
        }

        private readonly List<DragOrigin> dragOrigins = new List<DragOrigin>();

        /// <summary>屏幕点 → `markerLayer` 的左上角原点坐标（x 向右、y 向下）。</summary>
        private Vector2 ToMarkerLayerTopLeft(Vector2 screenPoint)
        {
            if (markerLayer == null)
            {
                return Vector2.zero;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    markerLayer, screenPoint, null, out local))
            {
                return Vector2.zero;
            }

            // 用 rect 的边界换算，和轴心在哪无关。
            Rect rect = markerLayer.rect;
            return new Vector2(local.x - rect.xMin, rect.yMax - local.y);
        }

        /// <summary>某个块的**左上角**在 markerLayer 左上角坐标系里的位置。</summary>
        private Vector2 MarkerLayerTopLeftOf(RectTransform rect)
        {
            if (markerLayer == null || rect == null)
            {
                return Vector2.zero;
            }

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            // GetWorldCorners 的顺序：0=左下 1=左上 2=右上 3=右下。
            Vector3 local = markerLayer.InverseTransformPoint(corners[1]);
            Rect layer = markerLayer.rect;
            return new Vector2(local.x - layer.xMin, layer.yMax - local.y);
        }

        /// <summary>
        /// 拖动开始：记下抓取偏移。有了它，块才是"被拎着走"，
        /// 而不是左上角吸到光标上。
        /// </summary>
        private void BeginBlockDragPointer(Vector2 screenPoint, TimelineMarker marker)
        {
            if (marker == null || marker.Rect == null)
            {
                dragPointerValid = false;
                return;
            }

            dragPointerLocal = ToMarkerLayerTopLeft(screenPoint);
            dragPressPointerLocal = dragPointerLocal;
            dragGrabOffset = dragPointerLocal - MarkerLayerTopLeftOf(marker.Rect);
            dragPointerValid = true;
        }

        private void UpdateBlockDragPointer(Vector2 screenPoint)
        {
            if (!blockDragActive)
            {
                return;
            }

            dragPointerLocal = ToMarkerLayerTopLeft(screenPoint);
            dragPointerValid = true;

            // **纵向也跟手。**
            //
            // 纵轴不是"改数据"而是"改行号"：这里的行号就是执行优先级
            // （ADOFAI 从上往下执行事件），所以拖动时直接把人挪到目标行、
            // 其余块顺势上下让位 —— 松手不需要再补一次提交。
            //
            // 拉长/缩短不动行：那是改时长，纵向跟着跑会顺手把行号也改了。
            if (blockDragResizing || blockDragItem == null)
            {
                return;
            }

            ApplyBlockDragRowFromPointer();
        }

        /// <summary>
        /// 按当前指针位置算目标行并整批挪过去。
        ///
        /// 单独拆出来是因为**贴边自动滚动**时指针没动、但内容在滚 —— 那种情况
        /// 不会触发 `OnDrag`，得每帧自己再算一次，否则块停在原地、视口越滚越远。
        /// </summary>
        private void ApplyBlockDragRowFromPointer()
        {
            if (!blockDragActive || blockDragResizing || blockDragItem == null
                || !dragPointerValid)
            {
                return;
            }
            float stride = Mathf.Max(1f, BlockStridePixels);
            float desiredTop = dragPointerLocal.y - dragGrabOffset.y;
            int row = Mathf.Max(0, Mathf.RoundToInt((desiredTop - ContentTop()) / stride));
            if (blockDragRow >= 0 && row == blockDragRow)
            {
                return;
            }
            blockDragRow = row;

            // **整批一起挪。** 位移以被拖那个为基准（和横向组拖同一套心智）：
            // 抓手落到第几行，其余按各自原来的行号加上同一个位移。
            int deltaRows = dragOrigins.Count > 0 ? row - dragOrigins[0].Row : 0;
            MoveDragGroupRows(deltaRows);
        }

        /// <summary>
        /// 拖块时贴到视口上/下边缘 → 自动纵向滚动。
        ///
        /// 和横向贴边滚动同一套手感：越往边上顶越快（深度平方），松手即停。
        /// 滚完立刻按指针重算目标行，块才跟着一起往下走。
        /// </summary>
        private void TickDragVerticalAutoScroll()
        {
            if (!blockDragActive || blockDragResizing || !dragPointerValid)
            {
                return;
            }
            float zone = EdgeScrollZone;
            float height = Mathf.Max(1f, bodyHeight);
            float y = dragPointerLocal.y;
            float direction = 0f;
            if (y < zone)
            {
                direction = -(1f - Mathf.Clamp01(y / zone));
            }
            else if (y > height - zone)
            {
                direction = 1f - Mathf.Clamp01((height - y) / zone);
            }
            if (Mathf.Abs(direction) < 0.02f)
            {
                return;
            }
            float depth = Mathf.Abs(direction);
            verticalScrollOffset += direction * 900f * depth * depth * Time.unscaledDeltaTime;
            ClampVerticalScroll();
            visualsDirty = true;
            ApplyBlockDragRowFromPointer();
        }

        /// <summary>
        /// 把排版模式拨到指定值；固定时立刻重排一次。
        ///
        /// 设置窗口的两个分段按钮要能**直接指定**（而不是只能翻转），
        /// 所以真正的活在这里，`ToggleFixedTimelineLayout` 只是包一层。
        /// </summary>
        internal void SetFixedTimelineLayout(bool fixedLayout)
        {
            if (Main.Settings == null || Main.Settings.FixedTimelineLayout == fixedLayout)
            {
                return;
            }
            Main.Settings.FixedTimelineLayout = fixedLayout;
            Main.SaveSettings();
            if (fixedLayout)
            {
                RepackRowsByTypePriority();
                visualsDirty = true;
            }
            if (Main.Overlay != null)
            {
                Main.Overlay.SetTransientStatus(fixedLayout
                    ? "排版：固定（按类型自动分层，Ctrl+L）"
                    : "排版：自由（行号归你，Ctrl+L）");
            }
        }

        /// <summary>切换「固定排版 / 自由排版」（Ctrl+L 走这里）。</summary>
        internal void ToggleFixedTimelineLayout()
        {
            if (Main.Settings == null)
            {
                return;
            }
            SetFixedTimelineLayout(!Main.Settings.FixedTimelineLayout);
        }

        /// <summary>当前是不是「固定排版」（类型优先级自动排）。</summary>
        private static bool FixedTimelineLayout
        {
            get { return Main.Settings != null && Main.Settings.FixedTimelineLayout; }
        }

        /// <summary>
        /// 事件类型的排版优先级（越小越靠上）。
        ///
        /// 用**名字**而不是枚举成员：这样写错一个名字只是排到最后，
        /// 不会编译不过；以后游戏加新事件类型也自动落到"其它"里。
        /// </summary>
        private static readonly string[] LayoutPriorityNames =
        {
            "MoveDecorations",
            "MoveCamera",
            "SetFilter",
            "SetFilterAdvanced",
            "MoveTrack",
            "SetSpeed",
            "Twirl",
            "SetHitsound",
            "Pause",
            "RecolorTrack",
            "AnimateTrack",
            "Bloom",
            "ScreenTile",
            "RepeatEvents",
            "Flash",
            "SetConditionalEvents",
        };

        private static int LayoutPriorityOf(LevelEvent levelEvent)
        {
            if (levelEvent == null)
            {
                return LayoutPriorityNames.Length;
            }
            string name = levelEvent.eventType.ToString();
            for (int i = 0; i < LayoutPriorityNames.Length; i++)
            {
                if (string.Equals(LayoutPriorityNames[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return LayoutPriorityNames.Length;
        }

        /// <summary>
        /// 块表是否已经把这一轮事件全建好了。
        ///
        /// 首次打开时 `EnsureBlockPlacements` 会先被调一次（事件清单还没刷完），
        /// 那时块表只有一部分；如果这时就重排，排的是残缺数据，之后再进就
        /// 走老规则 —— 表现就是"首次排版和手动重置的结果不一样"。
        /// </summary>
        private bool BlockTableLooksComplete()
        {
            if (blockPlacements.Count == 0)
            {
                return false;
            }
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return true;
            }
            int expected = 0;
            if (editor.events != null)
            {
                expected += editor.events.Count;
            }
            if (editor.decorations != null)
            {
                expected += editor.decorations.Count;
            }
            // 一块对应一个事件；留一点余量（有些事件不上时间轴）。
            return expected == 0 || blockPlacements.Count >= expected - 1;
        }


        /// <summary>
        /// 按类型优先级重排行号（固定排版）。
        ///
        /// 和旧模型一样**允许同排共用**：同类型里时间不重叠的块会挤进同一行，
        /// 所以 1400+ 个块仍然只占几十行。差别只在"谁先挑行"——
        /// 优先级高的类型先挑，于是自然形成自上而下的分带。
        /// </summary>
        /// <summary>
        /// 按事件类型优先级重排行号（保留给「固定排版」开关用）。
        ///
        /// 每类从上往下分带，同类内按时间先后贪心装箱：能塞进第一排就塞，
        /// 塞不下才新开一排。
        /// </summary>
        internal void RepackRowsByTypePriority()
        {
            if (blockPlacements.Count == 0)
            {
                return;
            }
            List<BlockPlacement> ordered = new List<BlockPlacement>(blockPlacements.Count);
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement != null && placement.Item != null)
                {
                    ordered.Add(placement);
                }
            }
            ordered.Sort(delegate (BlockPlacement a, BlockPlacement b)
            {
                int priorityA = LayoutPriorityOf(a.Item.Event);
                int priorityB = LayoutPriorityOf(b.Item.Event);
                if (priorityA != priorityB)
                {
                    return priorityA.CompareTo(priorityB);
                }
                int byTime = a.Time.CompareTo(b.Time);
                return byTime != 0 ? byTime : a.Item.FloorId.CompareTo(b.Item.FloorId);
            });

            // **全局首次适配 + 行内二分。**
            //
            // 每一行维护"按起点排好的区间表"，插入时二分找位置，只比较相邻两段
            // —— 原来每块都线性扫整行，3400 块 × 上百行要几百毫秒，只能挂在载入
            // 时；现在单次约几毫秒，可以跟着缩放做（防抖后重排）。
            List<List<float>> rowStarts = new List<List<float>>();
            List<List<float>> rowEnds = new List<List<float>>();
            // 占位要把**块间距**也算进去：绘制时块宽是
            // `max(BlockMinWidth, 时长×pps − BlockGap)`，两块贴边时若只按
            // 最小块宽占位，视觉上还会蹭在一起（最小缩放档最明显）。
            float perSecond = Mathf.Max(1f, pixelsPerSecond);
            float minSpan = (BlockMinWidth + BlockGap) / perSecond;

            for (int i = 0; i < ordered.Count; i++)
            {
                BlockPlacement placement = ordered[i];
                float blockStart = placement.Time;
                float blockEnd = blockStart + Mathf.Max(minSpan,
                    placement.Item.Duration + BlockGap / perSecond);

                int row = -1;
                for (int r = 0; r < rowStarts.Count; r++)
                {
                    List<float> starts = rowStarts[r];
                    List<float> ends = rowEnds[r];
                    int at = LowerBound(starts, blockStart);
                    bool clash = (at > 0 && ends[at - 1] > blockStart)
                        || (at < starts.Count && starts[at] < blockEnd);
                    if (!clash)
                    {
                        starts.Insert(at, blockStart);
                        ends.Insert(at, blockEnd);
                        row = r;
                        break;
                    }
                }
                if (row < 0)
                {
                    rowStarts.Add(new List<float> { blockStart });
                    rowEnds.Add(new List<float> { blockEnd });
                    row = rowStarts.Count - 1;
                }
                placement.Row = row;
            }
            visualsDirty = true;
        }

        /// <summary>`list` 里第一个 >= value 的下标（二分）。</summary>
        private static int LowerBound(List<float> list, float value)
        {
            int low = 0;
            int high = list.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (list[mid] < value)
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

        private const int BlocksPerRowTarget = 26;

        /// <summary>单个类型最多分几行 —— 再密也不至于把整条时间轴摊成几十行。</summary>
        private const int MaxRowsPerType = 8;

        private static bool RowFits(List<Vector2> intervals, float start, float end)
        {
            for (int i = 0; i < intervals.Count; i++)
            {
                Vector2 interval = intervals[i];
                if (start < interval.y && interval.x < end)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 把整批选中的块按同一行位移挪过去。
        ///
        /// **行是共享的** —— 1468 个块只占 0~73 行（时间不重叠的块同排一行），
        /// 所以这里只改这一组的行号，**绝不动别人的**。
        /// （早先按"一块一行"整体重排过一次，把 1468 个块摊成 1468 行，
        ///   全滚到视口外，看起来就是"块全没了"。）
        ///
        /// 上下界：不小于 0，不越过现有最后一行 —— 拖出很远的话块会掉到视口外，
        /// 用户既看不见也拖不回来。
        /// </summary>
        private void MoveDragGroupRows(int deltaRows)
        {
            if (dragOrigins.Count == 0)
            {
                return;
            }
            int maxRow = 0;
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                if (blockPlacements[i] != null)
                {
                    maxRow = Mathf.Max(maxRow, blockPlacements[i].Row);
                }
            }
            for (int i = 0; i < dragOrigins.Count; i++)
            {
                DragOrigin origin = dragOrigins[i];
                if (origin.Item == null)
                {
                    continue;
                }
                BlockPlacement placement = FindPlacementForEvent(origin.Item.Event);
                if (placement == null)
                {
                    continue;
                }
                // **下界 0，上界放宽到「最后一行 + 末尾预留行」。**
                //
                // 原来夹在 `maxRow`（当前最靠下的那块），于是只有两行时往第三行
                // 拖不动 —— 手感上就是"最多拖到现有层数"。末尾本来就预留了
                // `ExtraRowsBelow` 行空白（内容高度也按它算），块落进去既看得见
                // 也滚得回来。
                int upperRow = maxRow + ExtraRowsBelow;
                int row = Mathf.Clamp(origin.Row + deltaRows, 0, upperRow);
                if (placement.Row != row)
                {
                    placement.Row = row;
                    visualsDirty = true;
                    // **行号是工程数据。** 不置这个位，Ctrl+S 不会写、关工作台
                    // 也不会提示保存 —— 重开谱面时位置就"自己没了"。
                    projectDirty = true;
                }
            }
        }


        /// <summary>
        /// 这个块按它**当前的行号和时间**，会不会跟别的块撞上。
        ///
        /// 判据和画的时候一致：**同一行 + 画出来的像素区间相交**。不能拿"名义
        /// 时长"比 —— 块有最小宽度（`BlockMinWidth`），两个点事件时间上并不重叠、
        /// 画出来却是叠着的。
        ///
        /// 拖动中拿它做「这一下能不能放下」的判据，也用来决定要不要描红。
        /// 只查这一块（不两两比），代价 O(块数)。
        /// </summary>
        internal bool BlockOverlapsOthers(LevelEvent target)
        {
            BlockPlacement self = FindPlacementForEvent(target);
            if (self == null || self.Item == null)
            {
                return false;
            }
            float speed = Mathf.Max(1f, pixelsPerSecond);
            float selfLeft = self.Time * speed;
            float selfWidth = Mathf.Max(BlockMinWidth, self.Item.Duration * speed - BlockGap);
            float selfRight = selfLeft + selfWidth;

            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement other = blockPlacements[i];
                if (other == self || other == null || other.Item == null
                    || other.Row != self.Row)
                {
                    continue;
                }
                float otherLeft = other.Time * speed;
                float otherWidth = Mathf.Max(BlockMinWidth,
                    other.Item.Duration * speed - BlockGap);
                if (selfLeft < otherLeft + otherWidth && otherLeft < selfRight)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 松手前检查：这批块里只要有一块和别的块叠着，就整批**作废**并返回 true。
        ///
        /// 为什么要作废而不是放任：块的**最小宽度**让两个时间上并不重叠的事件
        /// 画出来也会叠；叠在一起之后用户既看不出谁压着谁，撤销也会像"跳错地方"。
        ///
        /// **必须在记录行号撤销之前调** —— 退回之后每块的行号都等于起点行，
        /// 那一步自然会认为"一个都没动"、不记撤销。
        /// </summary>
        internal bool RevertDragIfOverlapping()
        {
            bool overlapping = false;
            for (int i = 0; i < dragOrigins.Count; i++)
            {
                DragOrigin origin = dragOrigins[i];
                if (origin.Item == null || origin.Item.Event == null)
                {
                    continue;
                }
                if (BlockOverlapsOthers(origin.Item.Event))
                {
                    overlapping = true;
                    break;
                }
            }
            if (!overlapping)
            {
                return false;
            }

            for (int i = 0; i < dragOrigins.Count; i++)
            {
                DragOrigin origin = dragOrigins[i];
                if (origin.Item == null || origin.Item.Event == null)
                {
                    continue;
                }
                BlockPlacement placement = FindPlacementForEvent(origin.Item.Event);
                if (placement != null)
                {
                    placement.Row = origin.Row;
                    // 横向要退在 `placement.Time` 上：拖动中的预览就写在那儿，
                    // 而 `EndBlockDrag` 也是读它去提交的 —— 退错字段等于没退。
                    placement.Time = origin.Start;
                    placement.PreviewDuration = float.NaN;
                }
                origin.Item.StartTime = origin.Start;
                origin.Item.Duration = origin.StartDuration;
            }

            // 起点表清掉：横纵都不再提交。
            dragOrigins.Clear();
            visualsDirty = true;
            return true;
        }

        /// <summary>
        /// 收掉「固定播放头下滚轮扫时间」留下的 scrub 会话。
        ///
        /// 滚动时我们开了一次 scrub 来挪播放头；会话没关之前求值器仍处于
        /// "拖动定位"，空格播放起不来。按空格前先调这里。
        /// </summary>
        internal void CancelScrollScrub()
        {
            if (!centeredScrubActive)
            {
                return;
            }
            centeredScrubActive = false;
            centeredScrubPending = 0f;
            SubmitPendingScrub(true);
            EditorPlaybackBridge.EndTimelineScrub();
            ParticleWatch.ResumeAll();
        }

        /// <summary>某个事件当前在第几行；找不到这个块返回 -1。</summary>
        internal int BlockRowOf(LevelEvent target)
        {
            BlockPlacement placement = FindPlacementForEvent(target);
            return placement == null ? -1 : placement.Row;
        }

        /// <summary>
        /// 直接把一批块放回指定行（撤销 / 重做用）。
        ///
        /// **不做让位。** 撤销要复原的是"当时那一份布局"，逐块做插入让位
        /// 会把其它块再推一遍，结果和撤销前的样子对不上。
        /// </summary>
        internal void ApplyBlockRows(LevelEvent[] targets, int[] rows)
        {
            if (targets == null || rows == null)
            {
                return;
            }
            for (int i = 0; i < targets.Length && i < rows.Length; i++)
            {
                BlockPlacement placement = FindPlacementForEvent(targets[i]);
                if (placement != null)
                {
                    int row = Mathf.Max(0, rows[i]);
                    if (placement.Row != row)
                    {
                        placement.Row = row;
                        projectDirty = true;
                    }
                }
            }
            visualsDirty = true;
        }

        /// <summary>
        /// 拖动结束时把"行号怎么变的"记进撤销栈。
        ///
        /// **记的是整张行表**（拖动开始时的快照 vs 现在的值），不是只记本组：
        /// 整组插入会让其它块顺势上/下移一行，只把自己的行号记回去的话，
        /// 撤销后那些被让位的块会留下空档，布局和拖动前对不上。
        ///
        /// 只有真的变了才记 —— 纯横向拖动不该多出一条撤销记录。
        /// </summary>
        private void RecordRowMoveIfChanged()
        {
            if (dragOrigins.Count == 0)
            {
                return;
            }
            // **按事件记，不按下标记。** 拖动期间位置表可能被重绑（`RefreshData`），
            // 下标和快照对不上就会把行号记到别的块头上 —— 撤销之后位置自然不对。
            // 只记本组：现在纵向移动只改这一组的行号，别人的一行都没动。
            List<LevelEvent> targets = new List<LevelEvent>(dragOrigins.Count);
            List<int> fromRows = new List<int>(dragOrigins.Count);
            List<int> toRows = new List<int>(dragOrigins.Count);
            for (int i = 0; i < dragOrigins.Count; i++)
            {
                TimelineItem item = dragOrigins[i].Item;
                if (item == null || item.Event == null)
                {
                    continue;
                }
                int now = BlockRowOf(item.Event);
                if (now < 0 || now == dragOrigins[i].Row)
                {
                    continue;
                }
                targets.Add(item.Event);
                fromRows.Add(dragOrigins[i].Row);
                toRows.Add(now);
            }
            if (targets.Count == 0)
            {
                return;
            }
            EditorCommandHistory.RecordBlockRows(this,
                targets.ToArray(), fromRows.ToArray(), toRows.ToArray());
        }

        /// <summary>把整张行表恢复到快照（取消拖动时用）。</summary>
        private void RestoreRowSnapshot()
        {
            if (dragRowSnapshot == null || dragRowSnapshot.Length != blockPlacements.Count)
            {
                return;
            }
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                if (blockPlacements[i] != null)
                {
                    blockPlacements[i].Row = Mathf.Max(0, dragRowSnapshot[i]);
                }
            }
            visualsDirty = true;
        }

        /// <summary>内容区（标尺以下）在标记层坐标系里的顶端。</summary>
        private float ContentTop()
        {
            return RulerHeight - verticalScrollOffset;
        }

        private DragOrigin CaptureDragOrigin(TimelineItem item)
        {
            // 纵向的起点从位置表读 —— 它是"最终摆在哪"的事实来源，
            // 和横向的 `Start` 一样，都属于拖动要还原的那一份。
            int row = 0;
            BlockPlacement placement = item == null ? null : FindPlacementForEvent(item.Event);
            if (placement != null)
            {
                row = placement.Row;
            }
            return new DragOrigin
            {
                Item = item,
                Row = row,
                // **起点要现算，不能抄 `item.StartTime`。**
                //
                // `StartTime` 是上次重新分析谱面时的快照，而块**画出来**的位置
                // 是每帧现算的（见 `CollectBlocks` 那条注释）—— 两者会差一截，
                // 于是拖动时那条标原位的竖线画在过期位置、看着像"跑到别处"。
                // 这里必须和画的时候用同一个来源。
                Start = GetEventStartTime(scnEditor.instance, item.Event),
                StartDuration = item.Duration,
                // 和原来的单例字段 blockDragOriginalDuration 取的是同一个东西：
                // **事件数据里的拍数**，撤销要还原的就是它。
                DataDuration = GetEnabledFloat(item.Event, "duration"),
                Floor = item.FloorId,
                AngleOffset = GetEnabledFloat(item.Event, "angleOffset"),
                HadDuration = CanResizeItem(item)
            };
        }

        private TimelineItem FindItemForEvent(LevelEvent target)
        {
            if (target == null)
            {
                return null;
            }

            for (int r = 0; r < layoutRows.Count; r++)
            {
                List<RowInterval> intervals = layoutRows[r];
                for (int i = 0; i < intervals.Count; i++)
                {
                    TimelineItem candidate = intervals[i].Item;
                    if (candidate != null && ReferenceEquals(candidate.Event, target))
                    {
                        return candidate;
                    }
                }
            }
            return null;
        }

        /// <summary>拖动中的块描红：和别人的像素区间叠上了，松手会作废。</summary>
        private static readonly Color DragOverlapColor = new Color(0.92f, 0.26f, 0.28f, 1f);

        /// <summary>这次拖动涉及的块（描边和重叠判定要按它认人）。</summary>
        private readonly HashSet<LevelEvent> dragMembers = new HashSet<LevelEvent>();

        /// <summary>这次拖动是不是在拉边缘（拉边缘不挪行）。</summary>
        private bool blockDragResizing;
        /// <summary>拖动中当前落到的行号；-1 = 还没算过。</summary>
        private int blockDragRow = -1;

        /// <summary>
        /// 拖动时钉在「原位置」的那条竖线。
        ///
        /// 横向是按格吸附的，拖动时块和鼠标会一段一段错开，光看块本身分不清
        /// 离原处多远了；这条线就是那个原点，拖回它附近会吸回去。松手即隐藏。
        /// </summary>
        private RectTransform dragOriginGuide;

        /// <summary>原位竖线的颜色（浅蓝，半透明，不抢事件块的眼）。</summary>
        private static readonly Color DragOriginGuideColor = new Color(0.62f, 0.78f, 0.95f, 0.55f);
        /// <summary>
        /// 拖动开始时的**整张行表**（按 `blockPlacements` 下标）。撤销和取消都靠它
        /// 精确复原：整组插入会让其它块跟着上下移，只记本组不够。
        /// </summary>
        private int[] dragRowSnapshot;

        // 拖动开始时的原始值，松手时用来做「有没有真的改动」的判断
        // 和撤销的 old 值。
        private int blockDragOriginalFloor;
        private float blockDragOriginalAngleOffset;
        private float blockDragOriginalDuration;
        private bool blockDragHadDuration;

        /// <summary>没有时长的事件不给拉长：它只有一个时间点，没有长度可改。</summary>
        private bool CanResizeItem(TimelineItem item)
        {
            if (item == null || item.Event == null || item.Event.info == null)
            {
                return false;
            }

            // 事件必须声明了 duration 这个属性。往未声明的键上写值，
            // 游戏的解析器可能直接忽略，用户会看到「拖了但没反应」。
            Dictionary<string, ADOFAI.PropertyInfo> properties = item.Event.info.propertiesInfo;
            return properties != null && properties.ContainsKey("duration");
        }

        private void BeginBlockDrag(TimelineMarker marker, bool resizing)
        {
            TimelineItem item = marker == null ? null : marker.Item;
            if (item == null || item.Event == null)
            {
                return;
            }

            // 拖块也取锁。这是最容易和别人撞上的操作——两个人拖同一个块，
            // 谁先松手谁说了算，另一个人的努力白费，而且双方都不知道。

            blockDragActive = true;
            blockDragItem = item;
            blockDragMarker = marker;
            // 拉长/缩短不参与纵向挪行（见 UpdateBlockDragPointer）。
            blockDragResizing = resizing;
            blockDragRow = -1;
            EnsureBlockPlacements();
            dragRowSnapshot = new int[blockPlacements.Count];
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                dragRowSnapshot[i] = blockPlacements[i] == null ? 0 : blockPlacements[i].Row;
            }
            blockDragOriginalFloor = item.FloorId;
            blockDragOriginalAngleOffset = GetEnabledFloat(item.Event, "angleOffset");
            blockDragOriginalDuration = GetEnabledFloat(item.Event, "duration");
            blockDragHadDuration = CanResizeItem(item);

            // 多选一起拖的名单。
            //
            // **只在移动时组队**：拉长 / 缩短是"改这一个的时长"，把一批的
            // 时长一起改掉不会有人想要。而且被拖的那个必须**在选中里**
            // 才组队 —— 否则拖一个没被选中的块会把旁边选中的一批也带走。
            diagClampLogged = 0;
            dragOrigins.Clear();
            dragOrigins.Add(CaptureDragOrigin(item));
            if (!resizing && multiSelection.Count > 1 && multiSelection.Contains(item.Event))
            {
                foreach (LevelEvent selected in multiSelection)
                {
                    if (selected == null || ReferenceEquals(selected, item.Event))
                    {
                        continue;
                    }

                    TimelineItem other = FindItemForEvent(selected);
                    if (other != null)
                    {
                        dragOrigins.Add(CaptureDragOrigin(other));
                    }
                }
            }

            // 描边和重叠判定按这份名单认人。
            dragMembers.Clear();
            for (int i = 0; i < dragOrigins.Count; i++)
            {
                TimelineItem member = dragOrigins[i].Item;
                if (member != null && member.Event != null)
                {
                    dragMembers.Add(member.Event);
                }
            }

            // 拖动期间必须让 RefreshData 站住：它会重建 TimelineItem，
            // 而重建之后手上这个引用就指着孤儿了。
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[TimelineDrag] begin " + item.Name
                    + " mode=" + (resizing ? "resize" : "move")
                    + " floor=" + item.FloorId
                    + " start=" + item.StartTime.ToString("0.000")
                    + " dur=" + item.Duration.ToString("0.000"));
            }
        }

        /// <summary>
        /// 拖动中的视觉预览。只改内存里的 TimelineItem，不碰谱面数据。
        /// </summary>
        private void PreviewBlockDrag(TimelineMarker marker, float startTime, float duration)
        {
            TimelineItem item = marker == null ? null : marker.Item;
            if (item == null)
            {
                return;
            }

            float newStart = Mathf.Max(0f, startTime);
            float newDuration = Mathf.Max(0f, duration);

            item.StartTime = newStart;
            item.Duration = newDuration;

            // **同时写进位置表。**
            //
            // 画布摆块读的是 `placement.Time`，不是 `item.StartTime`；而拖动期间
            // `RefreshGeometry` 又会让位置表“听位置表自己的”（见 `liveTimeBase`）。
            // 只改 item 的话，位置表里还是拖动前那一份 —— 表现就是**块不跟手，
            // 松手提交之后才落到新位置**。
            BlockPlacement preview = FindPlacementForEvent(item.Event);
            if (preview != null)
            {
                preview.Time = newStart;
                preview.PreviewDuration = newDuration;
            }

            // **整批跟着走**，位移以被拖的那个（dragOrigins[0]）为准。
            //
            // 只在拖动中改时间、不动 layoutRows，所以整批留在原来的行里 ——
            // 松手才重排（否则块会在手指底下跳行）。
            if (dragOrigins.Count > 1 && ReferenceEquals(dragOrigins[0].Item, item))
            {
                // **跟随者：时间同步，但只改 angleOffset、不动 floor。**
                //
                // 两条要求要同时成立：
                //   · 整组在**时间**上同步（各移同样的秒数）。只按"格"移的话，
                //     一格的长度可能远大于这次的位移 —— 实测被拖的移了 0.48 秒，
                //     跟随者却跳了整整一格 1.92 秒（因为被拖那个正好停在
                //     格边界上，0.48 秒就让它跨了一格）。整组当场散开。
                //   · 每项**留在原来那一格**。跨格重新锚定会把作者写的偏移
                //     改写成另一个数字（实测 532° 变成 82°）。
                //
                // 兼顾的办法：位移全部折算进 `angleOffset`，允许它超出 360
                // （`532` 这种值本来就在谱面里存在）。于是时间步调一致、
                // 格归属不变。
                // **整组按同一个"吸附后的位移"走。**
                //
                // 关键是这个位移来自**被拖那个吸附之后**的位置，跟随者不再各自
                // 吸附。各自吸附会把它们**各自的格内偏移抹平** —— 一个偏移很大的
                // 项会被直接吸到最近的格边界上、落到第 0 格（实测就是这个）。
                //
                // 这也正是"组拖"的常规做法：**抓手吸附到格，其余保持相对位置**。
                // 于是既吸上了格，作者写的偏移又一个字不动。
                float delta = newStart - dragOrigins[0].Start;
                for (int i = 1; i < dragOrigins.Count; i++)
                {
                    DragOrigin origin = dragOrigins[i];
                    if (origin.Item == null)
                    {
                        continue;
                    }

                    float wanted = origin.Start + delta;

                    // 诊断：跟随者越界。拖到左边缘时 delta 固定、各自被钳到 0
                    // （组内间距丢失），拖到右边缘时被推出谱面末尾（滑出视口、
                    // RefreshGeometry 里 continue 掉，不再跟随）。
                    if (Main.Settings != null && Main.Settings.VerboseDiagnostics
                        && diagClampLogged < 8)
                    {
                        float lastFloor = -1f;
                        scnEditor diagnosticEditor = scnEditor.instance;
                        if (diagnosticEditor != null && diagnosticEditor.floors != null
                            && diagnosticEditor.floors.Count > 0)
                        {
                            lastFloor = Convert.ToSingle(
                                diagnosticEditor.floors[diagnosticEditor.floors.Count - 1].entryTime);
                        }
                        if (wanted < 0f || (lastFloor >= 0f && wanted > lastFloor))
                        {
                            diagClampLogged++;
                            Main.Logger.Log("[TimelineDrag] 跟随者越界 " + origin.Item.Name
                                + " start=" + origin.Start.ToString("0.000")
                                + " delta=" + delta.ToString("0.000")
                                + " wanted=" + wanted.ToString("0.000")
                                + " 允许区间=[0," + lastFloor.ToString("0.000") + "]"
                                + " members=" + dragOrigins.Count);
                        }
                    }

                    origin.Item.StartTime = Mathf.Max(0f, wanted);
                    // 跟随者也一样：位置表不跟着改，整组就只有被拖那个在动。
                    BlockPlacement followerPlacement = FindPlacementForEvent(origin.Item.Event);
                    if (followerPlacement != null)
                    {
                        followerPlacement.Time = origin.Item.StartTime;
                    }
                }
            }

            // visualsDirty 会在下一帧走 RefreshGeometry 重新摆这一块，
            // 但不碰 layoutRows，所以它留在原来的行里。
            visualsDirty = true;
            UpdateSummary();
        }

        /// <summary>
        /// 松手：把拖动结果转换成一次真实编辑并提交。
        /// </summary>
        private void EndBlockDrag(TimelineMarker marker, float originalStart, float originalDuration)
        {
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[TimelineDrag] EndBlockDrag active=" + blockDragActive
                    + " members=" + dragOrigins.Count);
            }
            if (!blockDragActive)
            {
                return;
            }
            blockDragActive = false;
            blockDragItem = null;
            blockDragMarker = null;
            dragPointerValid = false;
            blockDragResizing = false;
            blockDragRow = -1;

            // **叠上了就整批作废**（描红已经提示过）。放在记录行号撤销之前 ——
            // 退回之后行号等于起点行，那一步自然会认为"一个都没动"。
            if (RevertDragIfOverlapping())
            {
                dragRowSnapshot = null;
                dragMembers.Clear();
                return;
            }

            TimelineItem item = marker == null ? null : marker.Item;
            scnEditor editor = scnEditor.instance;
            if (item == null || item.Event == null || editor == null
                || editor.floors == null || editor.floors.Count == 0)
            {
                visualsDirty = true;
                return;
            }

            // 整批的**最终**时间先抓下来 —— 下面会把 item 全部还原成原值，
            // 而 CommitBlockDrag 要用真实的 old 值判断"有没有真的改动"。
            List<float> finalStarts = new List<float>(dragOrigins.Count);
            for (int i = 0; i < dragOrigins.Count; i++)
            {
                TimelineItem target = dragOrigins[i].Item;
                finalStarts.Add(target == null ? 0f : target.StartTime);
            }

            float finalStart = dragOrigins.Count > 0 ? finalStarts[0] : originalStart;

            // 被拖的那个的**最终秒数**（拉长/缩短改的就是它），必须在还原之前抓。
            float finalDuration = dragOrigins.Count > 0 && dragOrigins[0].HadDuration
                ? Mathf.Max(0f, item.Duration)
                : (dragOrigins.Count > 0 ? dragOrigins[0].StartDuration : originalDuration);

            for (int i = 0; i < dragOrigins.Count; i++)
            {
                TimelineItem target = dragOrigins[i].Item;
                if (target != null)
                {
                    target.StartTime = dragOrigins[i].Start;
                    target.Duration = dragOrigins[i].StartDuration;
                }
            }

            // 整批记成**一条**撤销记录 —— 见 EditorCommandHistory.BeginBatch。
            // 放 try/finally 里：中途抛异常会让攒下的命令永远不入栈，
            // 那样东西改了却撤不回来。
            EditorCommandHistory.BeginBatch(
                "拖动 " + Mathf.Max(1, dragOrigins.Count) + " 个事件");
            try
            {
                if (dragOrigins.Count > 0)
                {
                    // 被拖的那个排在最前，它带着时长的变化；**它仍然走"时间"
                    // 那条路**（单拖的行为不变，拖到哪一格就挂到哪一格）。
                    CommitBlockDrag(editor, item, finalStart, finalDuration,
                        dragOrigins[0].Floor, dragOrigins[0].AngleOffset,
                        dragOrigins[0].DataDuration, dragOrigins[0].HadDuration);

                    // 跟随者：和整批同一个**时间**位移，但只改 `angleOffset`、
                    // 不动 `floor`。见 CommitFollowerDrag。
                    for (int i = 1; i < dragOrigins.Count; i++)
                    {
                        // **每项用它自己的最终时间算位移**，不用被拖那个的 ——
                        // 吸附之后各自对到了不同的格边界，位移不再相同。
                        // 用被拖那个的位移，写进数据的值会和画面上看到的位置对不上。
                        CommitFollowerDrag(editor, dragOrigins[i],
                            finalStarts[i] - dragOrigins[i].Start);
                    }
                }
                else
                {
                    CommitBlockDrag(editor, item, finalStart, finalDuration,
                        blockDragOriginalFloor, blockDragOriginalAngleOffset,
                        blockDragOriginalDuration, blockDragHadDuration);
                }

                // **行号也记进同一批。** 放在 try 里才和上面的时间改动合成一条
                // 撤销记录 —— 一次 Ctrl+Z 把横向和纵向一起退回，块不会只回一半。
                RecordRowMoveIfChanged();
            }
            finally
            {
                EditorCommandHistory.EndBatch();
                // 松手之后位置表的预览时长作废：下一帧按谱面重新算
                // （提交时可能被钳过，留着预览值会让块比数据长一截）。
                ClearDragPreview(dragOrigins);
                dragRowSnapshot = null;
                dragMembers.Clear();
                dragOrigins.Clear();
            }
        }

        /// <summary>把这一批块的拖动预览清掉（只清时长，时间下一帧会重算）。</summary>
        private void ClearDragPreview(List<DragOrigin> origins)
        {
            if (origins == null)
            {
                return;
            }
            for (int i = 0; i < origins.Count; i++)
            {
                TimelineItem target = origins[i].Item;
                if (target == null)
                {
                    continue;
                }
                BlockPlacement placement = FindPlacementForEvent(target.Event);
                if (placement != null)
                {
                    placement.PreviewDuration = float.NaN;
                }
            }
        }

        /// <summary>拖动被中断（块被回收等），丢弃这次操作。</summary>
        private void CancelBlockDrag()
        {
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[TimelineDrag] CancelBlockDrag active=" + blockDragActive
                    + " members=" + dragOrigins.Count);
            }
            blockDragActive = false;
            blockDragItem = null;
            blockDragMarker = null;
            dragPointerValid = false;
            blockDragResizing = false;
            blockDragRow = -1;
            // 取消也要把预览清掉，否则位置表会一直停在半路那个位置。
            ClearDragPreview(dragOrigins);
            // 纵向是拖动中实时改的，取消得把整张行表退回快照。
            RestoreRowSnapshot();
            dragRowSnapshot = null;
            dragMembers.Clear();
            dragOrigins.Clear();
            visualsDirty = true;
        }

        /// <summary>
        /// 提交一次拖动。
        ///
        /// **原始值由调用方传进来**，不读本视图那几个 `blockDragOriginal*` 字段 ——
        /// 多选一起拖时每个事件的原始值都不一样，那几个字段只装得下被拖的那一个。
        /// </summary>
        private void CommitBlockDrag(
            scnEditor editor,
            TimelineItem item,
            float finalStart,
            float finalDuration,
            int oldFloor,
            float oldAngleOffset,
            float oldDuration,
            bool hadDuration)
        {
            LevelEvent levelEvent = item.Event;
            if (levelEvent == null || !IsEventStillLive(editor, levelEvent))
            {
                visualsDirty = true;
                return;
            }

            // 反解出新的 floor 与 angleOffset。
            int newFloor;
            float newAngleOffset;
            ResolveFloorAndAngleOffset(editor, levelEvent, finalStart, out newFloor, out newAngleOffset);

            bool floorChanged = newFloor != oldFloor;
            bool angleChanged = !Mathf.Approximately(newAngleOffset, oldAngleOffset);

            float newDurationValue = hadDuration
                ? ConvertDurationToData(editor, newFloor, finalDuration)
                : oldDuration;
            bool durationChanged = hadDuration
                && !Mathf.Approximately(newDurationValue, oldDuration);

            if (!floorChanged && !angleChanged && !durationChanged)
            {
                // 原地按下去又原样放开，不占一步撤销。
                visualsDirty = true;
                layoutDirty = true;
                return;
            }

            var keys = new List<string>(3);
            var oldValues = new List<object>(3);
            var newValues = new List<object>(3);
            var oldDisabled = new List<bool>(3);
            var newDisabled = new List<bool>(3);

            AddKey(levelEvent, "floor", keys, oldValues, newValues, oldDisabled, newDisabled,
                oldFloor, newFloor, floorChanged);
            AddKey(levelEvent, "angleOffset", keys, oldValues, newValues, oldDisabled, newDisabled,
                oldAngleOffset, newAngleOffset, angleChanged);

            // 用参数而不是本视图那个字段：多选一起拖时，只有被拖的那个
            // 带时长变化，其余的是 hadDuration=false。
            if (hadDuration)
            {
                AddKey(levelEvent, "duration", keys, oldValues, newValues, oldDisabled, newDisabled,
                    oldDuration, newDurationValue, durationChanged);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.Equals(key, "floor", StringComparison.Ordinal))
                {
                    levelEvent.floor = Convert.ToInt32(newValues[i]);
                }
                else
                {
                    levelEvent[key] = newValues[i];
                }

                // 属性被 disable 时读取一律按 0 算（GetEnabledFloat）。
                // 不清掉的话，刚写进去的值会被当成没写，块瞬间跳回格首。
                if (levelEvent.disabled != null && levelEvent.disabled.ContainsKey(key))
                {
                    levelEvent.disabled[key] = false;
                    newDisabled[i] = false;
                }
            }

            if (levelEvent.info != null && levelEvent.info.isDecoration)
            {
                editor.UpdateDecorationObject(levelEvent);
            }

            // 写完之后才把 item 同步到**落点**的等价时间。
            //
            // 不能直接沿用 finalStart：它和写进 floor/angleOffset 再读回来的
            // 时间会差一点点（角度有 360 度取整）。用读数回填，块和参数栏
            // 的数值就严丝合缝，不会出现"块在这个位置、参数栏写另一个值"。
            //
            // 这一步必须在置 layoutDirty 之前：重排是拿 item.StartTime
            // 装箱的，用还原后的旧值排，块会先跳回旧位置待半秒，
            // 等下一轮轮询才纠正。
            item.FloorId = newFloor;
            item.StartTime = GetEventStartTime(editor, levelEvent);
            item.Duration = GetEventDuration(editor, levelEvent);

            EditorPlaybackBridge.RequestEvaluatorRebuild("timeline-drag");
            EditorCommandHistory.RecordBlockDrag(
                editor,
                levelEvent,
                keys.ToArray(),
                oldValues.ToArray(),
                newValues.ToArray(),
                oldDisabled.ToArray(),
                newDisabled.ToArray());

            // 松手才重排：拖动中不排，否则块会在手指底下跳行。
            layoutDirty = true;
            visualsDirty = true;
            UpdateSummary();

            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[TimelineDrag] commit " + item.Name
                    + " floor " + oldFloor + "->" + newFloor
                    + " angle " + oldAngleOffset.ToString("0.###") + "->" + newAngleOffset.ToString("0.###")
                    + (hadDuration
                        ? " dur " + oldDuration.ToString("0.###") + "->" + newDurationValue.ToString("0.###")
                        : ""));
            }
        }

        /// <summary>
        /// 批量拖动里**跟随者**的提交：按**时间**跟整批同步，但**只改
        /// `angleOffset`、不动 `floor`**。
        ///
        /// 位移折算成角度：格内时间与角度是 `1 拍 = 180°` 的关系，
        /// 所以 Δ度 = Δ秒 × bpm × 3，其中 bpm 取**它自己那一格**的。
        ///
        /// 只写这一个键，撤销记录里也就只有它 —— 撤回去时精确还原，
        /// 而 `floor` 从头到尾没被动过。
        /// </summary>
        private bool CommitFollowerDrag(scnEditor editor, DragOrigin origin, float deltaSeconds)
        {
            TimelineItem item = origin.Item;
            LevelEvent levelEvent = item == null ? null : item.Event;
            if (levelEvent == null || !IsEventStillLive(editor, levelEvent))
            {
                return false;
            }

            item.FloorId = origin.Floor;

            int floorIndex = Mathf.Clamp(origin.Floor, 0, editor.floors.Count - 1);
            scrFloor floor = editor.floors[floorIndex];
            float bpm = Mathf.Max(0.001f, editor.levelData.bpm * floor.speed);
            float newAngle = origin.AngleOffset + deltaSeconds * bpm * 3f;

            bool changed = !Mathf.Approximately(newAngle, origin.AngleOffset);
            bool oldDisabled = IsDisabledKey(levelEvent, "angleOffset");

            if (changed)
            {
                levelEvent["angleOffset"] = newAngle;
                if (levelEvent.disabled != null && levelEvent.disabled.ContainsKey("angleOffset"))
                {
                    levelEvent.disabled["angleOffset"] = false;
                }
            }

            // item 的时间用**数据**回填，块的位置和面板的数字才是同一个值。
            item.StartTime = GetEventStartTime(editor, levelEvent);
            item.Duration = GetEventDuration(editor, levelEvent);

            if (changed)
            {
                EditorCommandHistory.RecordBlockDrag(
                    editor,
                    levelEvent,
                    new[] { "angleOffset" },
                    new object[] { origin.AngleOffset },
                    new object[] { newAngle },
                    new[] { oldDisabled },
                    new[] { false });
            }
            return changed;
        }

        private static void AddKey(
            LevelEvent levelEvent,
            string key,
            List<string> keys,
            List<object> oldValues,
            List<object> newValues,
            List<bool> oldDisabled,
            List<bool> newDisabled,
            object oldValue,
            object newValue,
            bool changed)
        {
            if (!changed)
            {
                return;
            }

            keys.Add(key);
            oldValues.Add(oldValue);
            newValues.Add(newValue);
            oldDisabled.Add(IsDisabledKey(levelEvent, key));
            // 这里写 false 是有意的：提交时会把 disabled 清掉，
            // 所以重做之后它也应该是不禁用的状态。
            newDisabled.Add(false);
        }

        private static bool IsDisabledKey(LevelEvent levelEvent, string key)
        {
            bool disabled;
            return levelEvent.disabled != null
                && levelEvent.disabled.TryGetValue(key, out disabled)
                && disabled;
        }

        /// <summary>
        /// 把一个绝对时间反解成「哪一格 + 格内角度偏移」。
        ///
        /// 这是 <see cref="GetEventStartTime"/> 的逆运算，必须和它保持
        /// 同一套公式（同一份 bpm、同一套 entryTime），否则拖完松手
        /// 块会轻微跳一下——反解出来的值再正着算一遍对不上原时间。
        ///
        /// 跨格是允许的：目标时间落到哪一格，事件就挂到哪一格上，
        /// 这也是「拖到另一格」这个操作的实现方式。
        /// </summary>
        private static void ResolveFloorAndAngleOffset(
            scnEditor editor, LevelEvent levelEvent, float time, out int floorId, out float angleOffset)
        {
            int last = editor.floors.Count - 1;
            floorId = Mathf.Clamp(levelEvent.floor, 0, last);

            // 先确定落在哪一格：格的进入时间单调递增，二分即可。
            int low = 0;
            int high = editor.floors.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Convert.ToSingle(editor.floors[middle].entryTime) <= time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            floorId = Mathf.Clamp(low - 1, 0, last);

            scrFloor floor = editor.floors[floorId];
            float bpm = Mathf.Max(0.001f, editor.levelData.bpm * floor.speed);
            float secondsIntoFloor = time - Convert.ToSingle(floor.entryTime);
            angleOffset = secondsIntoFloor * bpm * 180f / 60f;

            // 角度是周期量：一格最多转 360 度。夹进 [0, 360) 免得写出
            // 一个越界的值（游戏能读，但和原生编辑器里显示的对不上）。
            angleOffset = Mathf.Repeat(angleOffset, 360f);
        }

        /// <summary>把一个时长（秒）反解成 duration 字段的值（拍）。</summary>
        private static float ConvertDurationToData(scnEditor editor, int floorId, float seconds)
        {
            if (seconds <= 0f)
            {
                return 0f;
            }
            int clamped = Mathf.Clamp(floorId, 0, editor.floors.Count - 1);
            scrFloor floor = editor.floors[clamped];
            float bpm = Mathf.Max(0.001f, editor.levelData.bpm * floor.speed);
            return Mathf.Max(0f, seconds * bpm / 60f);
        }

        /// <summary>
        /// 吸附：把时间对到**最近的格的边界**上。
        ///
        /// 不是对"以 t=0 为原点的拍网格"取整。这两者只有在每格都是
        /// 180 度、且中途没有变速时才恰好重合——360 度的格、SetSpeed
        /// 都会让拍网格和真实格边界错开。用户看的是时间轴上那些
        /// 格子线和格号刻度，所以吸附必须对着它们来。
        ///
        /// 做法：二分找出前后相邻的两格，看目标时间离哪一格的进入
        /// 时刻更近。原版取格号也是对 FloorsTime 做二分，区别只是
        /// 我们同时看左右两边，好判断"更近的是哪边"。
        /// </summary>
        private float SnapBlockTime(float time)
        {
            if (!SnapDragEnabled)
            {
                return Mathf.Max(0f, time);
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return Mathf.Max(0f, time);
            }

            List<scrFloor> floors = editor.floors;
            int index = LowerBoundFloors(floors, time);
            if (index <= 0)
            {
                return 0f;
            }
            if (index >= floors.Count)
            {
                // 拖到末格之后：吸附到最后一个格边界，不让事件掉出谱面。
                return Convert.ToSingle(floors[floors.Count - 1].entryTime);
            }

            float before = Convert.ToSingle(floors[index - 1].entryTime);
            float after = Convert.ToSingle(floors[index].entryTime);
            return (time - before) <= (after - time) ? before : after;
        }

        /// <summary>
        /// 带格内偏移的块怎么吸。
        ///
        /// 普通块吸到格边界就对了；但块的起点未必压在格上（`angleOffset` 写的，
        /// 或者变速把它推偏了）。这类块如果也吸到格边界，**偏移会被抹平** ——
        /// 手指还没动，块就先横跳半拍。
        ///
        /// 所以按「整拍移动、保住格内相位」来：先把时间减掉相位、吸附到格边界，
        /// 再把相位加回去。于是块只在整拍之间跳，作者写的偏移一个字不动。
        /// </summary>
        internal float SnapBlockTimeKeepingPhase(float time, float referenceStart)
        {
            if (!SnapDragEnabled)
            {
                return Mathf.Max(0f, time);
            }
            float perSecond = Mathf.Max(1f, pixelsPerSecond);

            // **1) 原位优先。** 拖回出发的地方要能精确吸住 —— 屏幕上那条竖线
            // 标的就是它，看得见就该吸得上。
            if (Mathf.Abs(time - referenceStart) <= OriginSnapPixels / perSecond)
            {
                return referenceStart;
            }

            float baseTime = FloorTimeAtOrBefore(referenceStart);
            float phase = Mathf.Max(0f, referenceStart - baseTime);
            float plain = SnapBlockTime(time);
            if (phase <= 0.001f)
            {
                // 没偏移的块：只有格边界这一条线。
                return plain;
            }

            // **2) 两条线都算一遍，取更近的那条：**
            //    · 格边界（整拍）
            //    · 格边界 + 这块自己的格内偏移（保住作者的偏移）
            float phased = Mathf.Max(0f, SnapBlockTime(Mathf.Max(0f, time - phase)) + phase);
            return Mathf.Abs(time - phased) < Mathf.Abs(time - plain) ? phased : plain;
        }

        /// <summary>`time` 之前（含）最近那一格的 entryTime；没有则 0。</summary>
        private float FloorTimeAtOrBefore(float time)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return 0f;
            }
            List<scrFloor> floors = editor.floors;
            int index = LowerBoundFloors(floors, time);
            if (index <= 0)
            {
                return 0f;
            }
            if (index > floors.Count)
            {
                index = floors.Count;
            }
            return Convert.ToSingle(floors[index - 1].entryTime);
        }

        /// <summary>吸附开关：设置项，拖的时候按住 Alt 临时反转。</summary>
        private bool SnapDragEnabled
        {
            get
            {
                bool snap = Main.Settings == null || Main.Settings.SnapTimelineDrag;
                if (IsAltHeld())
                {
                    return !snap;
                }
                return snap;
            }
        }

        /// <summary>
        /// Alt 按住没有。**直接问 Unity，不问 `io.KeyAlt`。**
        ///
        /// 和 `EditorImGuiTimelineBody.CtrlHeld` 是同一条教训：实测
        /// `io.KeyCtrl` 一直是 false（桥那边 LeftCtrl/RightCtrl 都喂了也无效），
        /// Alt 同一个机制、同一个下场 —— Alt+滚轮调行高就是这么哑掉的。
        /// 项目里其余地方一律读 `Input.GetKey`，这里也照那个来。
        ///
        /// **两个入口共用这一个**（`SnapDragEnabled` 的临时反转、滚轮调行高），
        /// 免得一处修好一处还哑着。
        /// </summary>
        internal static bool IsAltHeld()
        {
            return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        }

        // ---------------------------------------------------------- 拖动指针

        private static Texture2D resizeCursorTexture;

        /// <summary>
        /// 左右箭头指针。Unity 没有内置的 resize 指针，
        /// 而 Cursor.SetCursor 只吃自定义 Texture2D，所以自己画一张。
        ///
        /// 画成左右两个三角 + 中间一根横杠，边缘做一像素羽化——
        /// 硬边的斜线在大屏上锯齿很明显。
        /// </summary>
        internal static Texture2D GetResizeCursorTexture()
        {
            if (resizeCursorTexture != null)
            {
                return resizeCursorTexture;
            }

            const int size = 24;
            const float halfStroke = 1.6f;
            var pixels = new Color32[size * size];
            var clear = new Color32(0, 0, 0, 0);

            float mid = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    // 中间那根横杠。
                    float bar = Mathf.Max(
                        Mathf.Abs(py - mid) - 1.1f,
                        Mathf.Abs(px - mid) - (size * 0.22f));
                    float barAlpha = Mathf.Clamp01(0.5f - bar);

                    // 左右两个箭头（三角形），用「点到三段线段的距离」描边，
                    // 这样和项目里其它自绘图标是同一套画法。
                    float left = DistanceToSegment(px, py, 2.5f, mid, 8f, mid - 5.5f);
                    left = Mathf.Min(left, DistanceToSegment(px, py, 2.5f, mid, 8f, mid + 5.5f));
                    left = Mathf.Min(left, DistanceToSegment(px, py, 2.5f, mid, 8f, mid));

                    float right = DistanceToSegment(px, py, size - 2.5f, mid, size - 8f, mid - 5.5f);
                    right = Mathf.Min(right, DistanceToSegment(px, py, size - 2.5f, mid, size - 8f, mid + 5.5f));
                    right = Mathf.Min(right, DistanceToSegment(px, py, size - 2.5f, mid, size - 8f, mid));

                    float alpha = Mathf.Max(barAlpha, Mathf.Clamp01(halfStroke - Mathf.Min(left, right) + 0.5f));
                    if (alpha <= 0f)
                    {
                        pixels[y * size + x] = clear;
                        continue;
                    }

                    // 深色描边 + 白色主体：指针会落在浅色的事件块上，
                    // 纯白的箭头在白块上会看不见。
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            resizeCursorTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            resizeCursorTexture.SetPixels32(pixels);
            // 第二个参数是 makeNoLongerReadable，必须是 false。
            // 置 true 会把纹理的 CPU 端丢掉，而 Cursor.SetCursor 恰恰
            // 要求纹理 CPU 可读——否则每次调用都静默失败，日志里只留
            // 一句 "the specified texture was not CPU accessible"，
            // 表现就是「鼠标贴到边缘光标不变」。
            resizeCursorTexture.Apply(false, false);
            return resizeCursorTexture;
        }

        private bool cursorIsResize;

        /// <summary>鼠标悬停在块的左/右边缘时切换成左右箭头。</summary>
        /// <summary>
        /// 悬停在块的左/右边缘时切换成左右箭头。**ImGui 那边也调这个** ——
        /// 换指针这件事只有一份实现（连"纹理必须 CPU 可读"那个坑都在里面）。
        /// </summary>
        internal void ApplyBlockDragCursor(bool onLeftEdge, bool onRightEdge)
        {
            bool wantResize = onLeftEdge || onRightEdge;
            if (wantResize == cursorIsResize)
            {
                return;
            }
            if (wantResize)
            {
                Texture2D texture = GetResizeCursorTexture();
                if (texture != null)
                {
                    Cursor.SetCursor(texture, new Vector2(texture.width * 0.5f, texture.height * 0.5f),
                        CursorMode.Auto);
                    cursorIsResize = true;
                }
            }
            else
            {
                RestoreCursor();
            }
        }

        private void RestoreCursor()
        {
            if (!cursorIsResize)
            {
                return;
            }
            cursorIsResize = false;
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        /// <summary>事件还在编辑器的事件表里。原生撤销会整体换掉数据，旧引用会失效。</summary>
        private static bool IsEventStillLive(scnEditor editor, LevelEvent levelEvent)
        {
            if (editor == null || levelEvent == null)
            {
                return false;
            }
            if (editor.events != null)
            {
                for (int i = 0; i < editor.events.Count; i++)
                {
                    if (ReferenceEquals(editor.events[i], levelEvent))
                    {
                        return true;
                    }
                }
            }
            if (editor.decorations != null)
            {
                for (int i = 0; i < editor.decorations.Count; i++)
                {
                    if (ReferenceEquals(editor.decorations[i], levelEvent))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 只有「这一个事件本身」被选中才算。
        ///
        /// 以前这里还带一条「它所在的格被选中也算」，而 ActivateMarker 点一个
        /// 事件时会调 SelectFloor 选中整格，于是同一行上的每个事件都被判成
        /// 选中、一起描边，看不出点的是哪一个。格那一档降级到
        /// IsTimelineItemInSelectedFloor，只给轻微提亮。
        /// </summary>
        private bool IsTimelineItemSelected(TimelineItem item)
        {
            if (item == null)
            {
                return false;
            }
            return ReferenceEquals(item.Event, selectedEvent)
                || multiSelection.Contains(item.Event)
                || IsDecorationSelected(item);
        }

        /// <summary>
        /// 只是「所在格被选中」，不是「这个事件本身被选中」。
        /// 只给一点轻微提亮，白色描边留给精确选中的那一个。
        /// </summary>
        private bool IsTimelineItemInSelectedFloor(TimelineItem item)
        {
            if (item == null || item.IsDecoration)
            {
                return false;
            }

            scnEditor editor = scnEditor.instance;
            return editor != null
                && editor.floors != null
                && item.FloorId >= 0
                && item.FloorId < editor.floors.Count
                && editor.selectedFloors != null
                && editor.selectedFloors.Contains(editor.floors[item.FloorId]);
        }
        /// <summary>
        /// 把播放头的三个可见子节点拉满当前的内容高度。
        ///
        /// 它们是构建时按当时的 bodyHeight 建的，而那一帧 root 的尺寸
        /// 往往还不是最终值（面板刚建出来），所以启动后线会一直停在
        /// 那个偏矮的高度上，看着像被切掉一截。父节点每帧都在改尺寸，
        /// 但父节点自己没有像素——真正要改的是这三层。
        ///
        /// 横向偏移和纵向起点都沿用构建时的取值（起点 0，也就是从
        /// 视口最顶端、标尺之上开始），这里只更新高度。
        /// </summary>
        private void SyncPlayheadHeight()
        {
            if (playhead == null)
            {
                return;
            }

            if (playheadGlow != null)
            {
                SetTopLeft(playheadGlow.rectTransform,
                    -(PlayheadGlowWidth - PlayheadCoreWidth) * 0.5f, 0f,
                    PlayheadGlowWidth, bodyHeight);
            }
            if (playheadCore != null)
            {
                SetTopLeft(playheadCore.rectTransform, 0f, 0f, PlayheadCoreWidth, bodyHeight);
            }
            if (playheadHandle != null)
            {
                SetTopLeft(playheadHandle.rectTransform,
                    -(PlayheadHandleWidth - PlayheadCoreWidth) * 0.5f, 0f,
                    PlayheadHandleWidth, 9f);
            }
        }

        private void UpdatePlayhead(EditorPlaybackSnapshot snapshot)
        {
            // **时间读数先更新，别受下面那个提前返回影响。**
            //
            // ImGui 版接管之后这个方法在开头就 `return` 了（uGUI 那条播放头不摆，
            // 免得和 ImGui 画的叠成两条）—— 而 `timeText` 的赋值原来落在它后面，
            // 于是读数永远停在初始的 "00:00.000 / 00:00.000"，一动不动。
            //
            // 这个文本对象现在只是**数据的载体**（ImGui 那边读它的 `.text` 去画），
            // 和"摆不摆 uGUI 播放头"是两件不相干的事。
            float current = Mathf.Max(0f, snapshot.CurrentTime);
            timeText.text = FormatTime(current) + " / "
                + FormatTime(Mathf.Max(totalDuration, snapshot.Duration, current));

            float x = (current - viewStartTime) * pixelsPerSecond;
            bool inside = viewInitialized && x >= 0f && x <= bodyWidth;
            playhead.gameObject.SetActive(inside);
            if (inside)
            {
                SetTopLeft(playhead, x, 0f, 2f, bodyHeight);
                SyncPlayheadHeight();
            }
        }

        /// <summary>
        /// Toggle one lane's visibility. Called by the icon buttons on the
        /// preview toolbar. Turning every lane off is left alone -- that is the
        /// user's own choice, not a state to correct.
        /// </summary>
        internal void ToggleLane(int lane)
        {
            if (lane < 0 || lane >= LaneCount)
            {
                return;
            }
            laneVisible[lane] = !laneVisible[lane];
            ApplyLaneFilterChange();
        }

        /// <summary>Show every lane. Used by the "all" button on the toolbar.</summary>
        internal void SetAllLanes(bool visible)
        {
            for (int i = 0; i < LaneCount; i++)
            {
                laneVisible[i] = visible;
            }
            ApplyLaneFilterChange();
        }

        internal bool IsLaneVisible(int lane)
        {
            return lane >= 0 && lane < LaneCount && laneVisible[lane];
        }

        /// <summary>
        /// The native icon to stand for each lane. GCS.levelEventIcons is keyed
        /// by event type and has no notion of a lane, so each lane is represented
        /// by one event type that belongs to it.
        /// </summary>
        internal static readonly LevelEventType[] LaneIconEventTypes =
        {
            LevelEventType.MoveCamera,
            LevelEventType.SetFilter,
            LevelEventType.AddDecoration,
            LevelEventType.PlaySound,
            LevelEventType.SetSpeed,
            LevelEventType.EditorComment,
            LevelEventType.MoveTrack
        };

        /// <summary>
        /// A lane's representative icon. The preferred type is tried first;
        /// GCS.levelEventIcons does not cover every type, so it falls back to
        /// the first event that actually occurs in this lane and has an icon.
        /// That fallback only works once a chart is loaded, which is why the
        /// toolbar buttons are refreshed again from RefreshData.
        /// </summary>
        internal Sprite GetLaneIcon(int lane)
        {
            if (lane < 0 || lane >= LaneIconEventTypes.Length)
            {
                return null;
            }

            Sprite preferred = GetTypeIcon(LaneIconEventTypes[lane]);
            if (preferred != null)
            {
                return preferred;
            }

            List<TimelineItem> items = laneItems[lane];
            for (int i = 0; i < items.Count; i++)
            {
                Sprite fallback = GetTypeIcon(items[i].Event.eventType);
                if (fallback != null)
                {
                    return fallback;
                }
            }
            return null;
        }

        /// <summary>Applies a visibility change and syncs the toolbar icons.</summary>
        private void ApplyLaneFilterChange()
        {
            verticalScrollOffset = 0f;
            layoutDirty = true;
            visualsDirty = true;
            UpdateFilterVisuals();
        }

        /// <summary>Registers a toolbar icon so visibility changes can tint it.</summary>
        internal void RegisterLaneFilterButton(int lane, Image icon)
        {
            while (laneFilterIcons.Count <= lane)
            {
                laneFilterIcons.Add(null);
            }
            laneFilterIcons[lane] = icon;
            if (icon != null)
            {
                icon.color = laneVisible[lane] ? Color.white : new Color(1f, 1f, 1f, 0.28f);
            }
        }

        /// <summary>Syncs the toolbar lane icons with the current visibility.</summary>
        private void UpdateFilterVisuals()
        {
            for (int i = 0; i < laneFilterIcons.Count && i < LaneCount; i++)
            {
                if (laneFilterIcons[i] != null)
                {
                    laneFilterIcons[i].color = laneVisible[i]
                        ? Color.white
                        : new Color(1f, 1f, 1f, 0.28f);
                }
            }
        }

        private void UpdateSummary()
        {
            if (summaryText == null)
            {
                return;
            }
            if (selectedEvent != null)
            {
                summaryText.text = GetEventName(selectedEvent.eventType) + "  ·  第 " + selectedEvent.floor + " 格  ·  " + FormatTime(FindEventTime(selectedEvent));
                return;
            }
            summaryText.text = GetFilteredItemCount() + " 个事件"
                + "  ·  " + layoutSections.Count + " 类";
        }

        private void ActivateMarker(TimelineMarker marker)
        {
            TimelineItem item = marker == null ? null : marker.Item;
            scnEditor editor = scnEditor.instance;
            if (item == null || editor == null)
            {
                return;
            }

            // **Ctrl + 点击 = 切换多选。这条是主路，不是兜底。**
            //
            // 块身上挂着 Button，而 Button **自己就实现了 IPointerDownHandler /
            // IPointerUpHandler**。事件系统是从按下点沿父链找第一个实现者的，
            // 所以点在块上时，按下和松开都被这个 Button 吃掉了 —— 视图那条
            // 「Ctrl 从块上按下就开框选」的路根本收不到事件。于是"Ctrl 点块"
            // 唯一能走到的地方就是这里；原先这里直接 return，表现就是
            // 一点反应都没有。
            if (MarqueeModifierHeld)
            {
                ToggleSelectionOf(item);
                return;
            }

            // 普通点击 = **替换**选择，所以先把多选清掉。
            //
            // 不清的话，多选建好之后点别的块会得到一种各说各话的状态：
            // 主选中是刚点的那个（高亮在它身上），而参数栏「多选优先」
            // 画的还是旧的那一批，删除也只删旧的那一批。
            multiSelection.Clear();

            selectedEvent = item.Event;
            SyncSceneSelection(item);
            UpdateSummary();
            visualsDirty = true;
        }

        /// <summary>
        /// 把游戏那一侧的选择跟到刚点的这个块上。
        ///
        /// **装饰物和格子的选中都不在 `selectedEvent` 那一套里** —— 原生编辑器
        /// 自己各有一份（`editor.selectedDecorations`，以及 `SelectFloor` 管的
        /// 那份）。只设 `selectedEvent` 的话，画面里的高亮、游戏自己那套刷新、
        /// 以及靠 `ComputeSelectionFingerprint` 认人的地方都走不到它。
        ///
        /// 这段原来长在 `ActivateMarker` 里。ImGui 接管之后点块改走 `SelectBlock`，
        /// 只搬了"记下主选中"那一半，这一半漏了 —— 表现就是
        /// **"在时间轴上点装饰物，参数栏/画面没反应"**。
        /// </summary>
        private void SyncSceneSelection(TimelineItem item)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || item == null || item.Event == null)
            {
                return;
            }

            if (item.IsDecoration)
            {
                editor.SelectDecoration(
                    item.Event,
                    jumpToDecoration: false,
                    showPanel: false,
                    ignoreDeselection: false,
                    ignoreAdjustRect: true
                );
                return;
            }

            if (editor.floors != null && editor.floors.Count > 0)
            {
                int floorId = Mathf.Clamp(item.FloorId, 0, editor.floors.Count - 1);
                // **先把装饰物那份清掉。** 两份选中是彼此独立的，点一个普通事件
                // 不清的话，参数栏会一直显示上一次选中的那个装饰物
                // （`PanelSelection` 现在会去看 `selectedDecorations`）。
                editor.DeselectAllDecorations();
                // 标来源：这是"点事件块"引起的选中同步，只听「选中跟随」，
                // 不要被「定位」当成点轨道去做 1 秒加速走位。
                EditorPlaybackBridge.MarkNextFloorSelect(
                    EditorPlaybackBridge.FloorSelectTimelineEvent);
                editor.SelectFloor(editor.floors[floorId], cameraJump: false);
            }
        }

        private void ReturnToStart()
        {
            viewStartTime = 0f;
            MarkManualNavigation();
            visualsDirty = true;

            string status;
            if (!EditorPlaybackBridge.TryReturnToStart(out status)
                && owner != null
                && !string.IsNullOrEmpty(status))
            {
                owner.SetSceneSelectionStatus(status);
            }
        }

        private void ResetHorizontalView()
        {
            pixelsPerSecond = DefaultPixelsPerSecond;
            viewStartTime = 0f;
            viewInitialized = true;
            verticalScrollOffset = 0f;
            layoutDirty = true;
            MarkManualNavigation();
            UpdateZoomText();
            visualsDirty = true;
        }

        private void ZoomAt(float factor, float anchorX)
        {
            if (!viewInitialized)
            {
                ResetHorizontalView();
                return;
            }
            float oldPixels = pixelsPerSecond;
            float requestedPixels = Mathf.Clamp(
                oldPixels * factor,
                MinimumPixelsPerSecond,
                MaximumPixelsPerSecond
            );
            if (Mathf.Approximately(requestedPixels, oldPixels))
            {
                return;
            }
            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                // 固定播放头下锚点就是播放头：改完比例后**当场**把视图摆回居中。
                //
                // 只改比例、等下一帧居中接手会晚一帧 —— 连续滚轮缩小时每帧都落后
                // 一帧的量，看起来就是"边缩边漂"（放大时恰好因对称而不明显）。
                pixelsPerSecond = requestedPixels;
                viewStartTime = CenteredViewTarget();
                ClampCenteredViewStart();
            }
            else
            {
                float anchorTime = viewStartTime + anchorX / oldPixels;
                pixelsPerSecond = requestedPixels;
                viewStartTime = anchorTime - anchorX / pixelsPerSecond;
                ClampViewStart();
            }
            // **缩放后要重排行号。**
            //
            // 块有最小像素宽度，缩小时"名义不重叠"的两个块在屏幕上照样会叠在
            // 一起。固定排版下重排一次，让间距按新的像素尺度重新分配。
            if (FixedTimelineLayout)
            {
                RepackRowsByTypePriority();
            }
            layoutDirty = true;
            MarkManualNavigation();
            UpdateZoomText();
            visualsDirty = true;
        }

        private void UpdateZoomText()
        {
            if (zoomText != null)
            {
                zoomText.text = Mathf.RoundToInt(pixelsPerSecond / DefaultPixelsPerSecond * 100f) + "%";
            }
        }

        private void ClampViewStart()
        {
            // 固定播放头的范围不一样（允许负起点），统一走它自己那套 ——
            // 否则缩放/拖动里调一次这个、每帧居中又调一次那个，两套边界
            // 互相打架，0 秒附近就会来回跳。
            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                ClampCenteredViewStart();
                return;
            }
            float maximumStart = Mathf.Max(0f, navigationDuration - bodyWidth / Mathf.Max(1f, pixelsPerSecond));
            viewStartTime = Mathf.Clamp(viewStartTime, 0f, maximumStart);
        }

        /// <summary>
        /// 固定播放头专用的夹取：**允许起点为负**。
        ///
        /// 播放头在 0 秒时，要让它落在正中，视图起点必须是 `-可见时长 × 0.5`
        /// （谱面左边留白）。普通夹取会把起点按住 0，于是 0 秒时播放头顶在左边缘。
        /// </summary>
        private void ClampCenteredViewStart()
        {
            float visible = bodyWidth / Mathf.Max(1f, pixelsPerSecond);
            float maximumStart = Mathf.Max(0f, navigationDuration - visible);
            viewStartTime = Mathf.Clamp(viewStartTime, -visible * 0.5f, maximumStart);
        }

        private float GetMaximumVerticalScroll()
        {
            float visibleHeight = Mathf.Max(0f, bodyHeight - RulerHeight);
            // **接管之后按新画布的内容高算。** 旧模型是按格分组打包的几行，
            // 新画布是一块一行、高得多 —— 还用旧的算就会"滚不到底"，
            // 后面那些块永远出不来。
            // **块画布现在**永远是 uGUI 那套「一块一行」的模型（ImGui 主体已经
            // 不再绘制），所以内容高一律取 `BlockContentHeight`。
            // 还在按旧的 `layoutTotalHeight` 算的话，纵向可滚高度会算成 0 ——
            // 表现就是「普通滚轮完全不响应」（或者被当成横滚）。
            float contentHeight = BlockContentHeight;
            return Mathf.Max(0f, contentHeight - visibleHeight);
        }

        private void ClampVerticalScroll()
        {
            verticalScrollOffset = Mathf.Clamp(verticalScrollOffset, 0f, GetMaximumVerticalScroll());
        }

        private void MarkManualNavigation()
        {
            manualNavigationUntil = Time.unscaledTime + 1.6f;
        }

        private bool LaneIsVisible(TimelineLane lane)
        {
            return laneVisible[(int)lane];
        }

        private int GetFilteredItemCount()
        {
            int total = 0;
            for (int i = 0; i < LaneCount; i++)
            {
                if (laneVisible[i])
                {
                    total += laneItems[i].Count;
                }
            }
            return total;
        }

        private bool IsDecorationSelected(TimelineItem item)
        {
            scnEditor editor = scnEditor.instance;
            return item.IsDecoration
                && editor != null
                && editor.selectedDecorations != null
                && editor.selectedDecorations.Contains(item.Event);
        }

        private int ComputeSelectionFingerprint()
        {
            unchecked
            {
                int hash = selectedEvent == null ? 17 : RuntimeHelpers.GetHashCode(selectedEvent);
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                {
                    return hash;
                }
                if (editor.selectedFloors != null)
                {
                    foreach (scrFloor floor in editor.selectedFloors)
                    {
                        hash = hash * 31 + (floor == null ? 0 : floor.GetInstanceID());
                    }
                }
                if (editor.selectedDecorations != null)
                {
                    foreach (LevelEvent decoration in editor.selectedDecorations)
                    {
                        hash = hash * 31 + (decoration == null ? 0 : RuntimeHelpers.GetHashCode(decoration));
                    }
                }
                return hash;
            }
        }

        private static int ComputeDataFingerprint(scnEditor editor)
        {
            if (editor == null)
            {
                return 0;
            }
            unchecked
            {
                int hash = editor.GetInstanceID();
                hash = HashCollection(hash, editor.events);
                hash = HashCollection(hash, editor.decorations);
                if (editor.floors != null)
                {
                    hash = hash * 31 + editor.floors.Count;
                    foreach (scrFloor floor in editor.floors)
                    {
                        hash = hash * 31 + (floor == null ? 0 : floor.GetInstanceID());
                        hash = hash * 31 + (floor == null ? 0 : Convert.ToSingle(floor.entryTime).GetHashCode());
                    }
                }
                return hash;
            }
        }

        private static int HashCollection(int hash, IEnumerable<LevelEvent> events)
        {
            unchecked
            {
                int count = 0;
                foreach (LevelEvent levelEvent in events)
                {
                    count++;
                    if (levelEvent == null)
                    {
                        hash *= 31;
                        continue;
                    }
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(levelEvent);
                    hash = hash * 31 + levelEvent.floor;
                    hash = hash * 31 + (int)levelEvent.eventType;
                    hash = hash * 31 + levelEvent.active.GetHashCode();
                    hash = hash * 31 + GetEnabledFloat(levelEvent, "angleOffset").GetHashCode();
                    hash = hash * 31 + GetEnabledFloat(levelEvent, "duration").GetHashCode();
                    // The ease drives the curve drawn inside the block, so changing it
                    // must invalidate the layout. Without this the fingerprint stayed
                    // the same and the curve kept showing the previous easing.
                    hash = hash * 31 + (int)GetTimelineEase(levelEvent);
                    string tag;
                    if (levelEvent.TryGet<string>("tag", out tag) && tag != null)
                    {
                        hash = hash * 31 + StringComparer.Ordinal.GetHashCode(tag);
                    }
                }
                return hash * 31 + count;
            }
        }

        private bool ContainsEventReference(LevelEvent target)
        {
            for (int lane = 0; lane < LaneCount; lane++)
            {
                foreach (TimelineItem item in laneItems[lane])
                {
                    if (ReferenceEquals(item.Event, target))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private float FindEventTime(LevelEvent target)
        {
            for (int lane = 0; lane < LaneCount; lane++)
            {
                foreach (TimelineItem item in laneItems[lane])
                {
                    if (ReferenceEquals(item.Event, target))
                    {
                        return item.StartTime;
                    }
                }
            }
            return 0f;
        }

        private static int CompareTimelineItems(TimelineItem left, TimelineItem right)
        {
            int result = left.StartTime.CompareTo(right.StartTime);
            if (result != 0)
            {
                return result;
            }
            result = left.Duration.CompareTo(right.Duration);
            return result != 0 ? result : left.SourceOrder.CompareTo(right.SourceOrder);
        }

        private static int CompareTimelineItemsForLayout(TimelineItem left, TimelineItem right)
        {
            int result = right.Duration.CompareTo(left.Duration);
            if (result != 0)
            {
                return result;
            }
            result = left.StartTime.CompareTo(right.StartTime);
            return result != 0 ? result : left.SourceOrder.CompareTo(right.SourceOrder);
        }

        private static int LowerBoundFloors(List<scrFloor> floors, float time)
        {
            int low = 0;
            int high = floors.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Convert.ToSingle(floors[middle].entryTime) < time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        private static float GetEventStartTime(scnEditor editor, LevelEvent levelEvent)
        {
            int floorId = Mathf.Clamp(levelEvent.floor, 0, editor.floors.Count - 1);
            scrFloor floor = editor.floors[floorId];
            float bpm = Mathf.Max(0.001f, editor.levelData.bpm * floor.speed);
            float angleOffset = GetEnabledFloat(levelEvent, "angleOffset");
            return Mathf.Max(0f, Convert.ToSingle(floor.entryTime) + angleOffset / 180f * 60f / bpm);
        }

        private static float GetEventDuration(scnEditor editor, LevelEvent levelEvent)
        {
            float duration = Mathf.Max(0f, GetEnabledFloat(levelEvent, "duration"));
            if (duration <= 0f)
            {
                return 0f;
            }
            int floorId = Mathf.Clamp(levelEvent.floor, 0, editor.floors.Count - 1);
            scrFloor floor = editor.floors[floorId];
            float bpm = Mathf.Max(0.001f, editor.levelData.bpm * floor.speed);
            return duration * 60f / bpm;
        }

        private static float GetEnabledFloat(LevelEvent levelEvent, string key)
        {
            bool disabled;
            if (levelEvent.disabled != null
                && levelEvent.disabled.TryGetValue(key, out disabled)
                && disabled)
            {
                return 0f;
            }
            float value;
            return levelEvent.TryGet<float>(key, out value) ? value : 0f;
        }

        private static string GetTimelineItemName(LevelEvent levelEvent, TimelineLane lane)
        {
            if (lane == TimelineLane.Decoration)
            {
                string tag;
                if (levelEvent.TryGet<string>("tag", out tag) && !string.IsNullOrWhiteSpace(tag))
                {
                    return NormalizeTimelineLabel(tag);
                }
            }

            // **滤镜块用滤镜名当名字，而且必须是原生那套中文名。**
            //
            // 全叫「滤镜」的话，一排滤镜块长得一模一样，只能靠点开参数栏才知道
            // 哪个是哪个 —— 而这恰恰是排布时最需要一眼看出来的信息。
            //
            // 名字走 `EditorPropertyPanel.DisplayEnumName`：它内部是
            // `RDString.GetEnumValue(...)`，和参数栏下拉框、原生属性面板同一条
            // 本地化路径，所以中文名一定和原版一模一样（不会出现我们自造的译名）。
            if (levelEvent.eventType == LevelEventType.SetFilter)
            {
                ADOFAI.PropertyInfo filterInfo;
                if (levelEvent.info != null && levelEvent.info.propertiesInfo != null
                    && levelEvent.info.propertiesInfo.TryGetValue("filter", out filterInfo))
                {
                    string raw = EditorPropertyPanel.ReadEnumName(levelEvent, filterInfo);
                    string localized = EditorPropertyPanel.DisplayEnumName(
                        levelEvent, filterInfo, raw);
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        return NormalizeTimelineLabel(localized);
                    }
                }
            }

            // **高级滤镜的值是类名字符串**，走不了枚举那条路 ——
            // 用参数栏下拉的同一套映射（去前缀 → RDString → 拆驼峰兜底），
            // 保证块名、下拉、原生面板三处显示的是同一个中文名。
            if (levelEvent.eventType == LevelEventType.SetFilterAdvanced)
            {
                string rawFilter;
                if (levelEvent.TryGet<string>("filter", out rawFilter)
                    && !string.IsNullOrWhiteSpace(rawFilter))
                {
                    string localized = UiInspectorPane.FilterDisplayFor(rawFilter);
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        return NormalizeTimelineLabel(localized);
                    }
                }
            }

            return GetEventName(levelEvent.eventType);
        }

        private static string NormalizeTimelineLabel(string value)
        {
            return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>
        /// 事件块的填充色：泳道色相，固定感知亮度。
        ///
        /// 亮度是解出来的，不是写死的。HSV 的 V 不是感知亮度，
        /// 同一个 V 下蓝紫这类色相天生比黄绿暗得多——旧配色把饱和度压到
        /// 极低、V 拉到 0.93，结果六个家族的填充最大通道都是 237、
        /// 最小都是 157，只有中间通道在变，看着就是同一片灰白。
        ///
        /// 做法是：能提饱和就提饱和，提到该色相能达到目标亮度的上限为止
        /// （黄绿先撞到这个上限），剩下的靠 V 收。于是每个家族都落在
        /// 同一条亮度线上，色相各自又保住了。
        ///
        /// 泳道筛选按钮用的是同一组色相，所以一个分类框的颜色
        /// 和打开它的那个按钮是对得上的。
        /// </summary>
        private static Color GetEventTypeColor(LevelEventType eventType, TimelineLane lane)
        {
            float hue = GetEventHue(eventType, lane);
            // **事件块保留彩色**：块的色相是用户区分事件类型的唯一可靠线索，
            // 全灰阶会让 MoveTrack / MoveDecorations 这类近邻类型混成一片。
            // 界面（面板、文字、控件）仍是黑灰白，只有时间轴事件块例外。
            float saturation = BlockTintMaxSaturation;
            if (Luminance(Color.HSVToRGB(hue, saturation, 1f)) < BlockBrightness)
            {
                saturation = SaturationForLuminance(hue, BlockBrightness);
            }

            Color color = Color.HSVToRGB(
                hue,
                saturation,
                ValueForLuminance(hue, saturation, BlockBrightness)
            );
            color.a = 1f;
            return color;
        }

        /// <summary>
        /// 块上文字的颜色：同色相的深色。缓速曲线和选中描边也用这个。
        ///
        /// 也不写死一个深色，同样反解出来。深色比浅色更受色相影响，
        /// 固定值会让某几个家族的字明显比别的浅——旧的 v=0.34 就是
        /// 这样，紫色那族一直偏淡。
        /// </summary>
        private static Color GetEventLabelColor(LevelEventType eventType, TimelineLane lane)
        {
            float hue = GetEventHue(eventType, lane);
            // 块上文字跟着块的色相走（同色相的深色），否则文字与块脱节。
            Color color = Color.HSVToRGB(
                hue,
                LabelReferenceSaturation,
                ValueForLuminance(hue, LabelReferenceSaturation, LabelBrightness)
            );
            color.a = 1f;
            return color;
        }

        /// <summary>
        /// 泳道色相，按事件自身 id 再错开一点。黄金分割步长让相邻 id
        /// 在色相上离得很远，列表里挨着的两个事件不会撞成几乎一样的颜色；
        /// 错开幅度又足够小，一眼还是能看出它属于哪个家族。
        /// </summary>
        private static float GetEventHue(LevelEventType eventType, TimelineLane lane)
        {
            float variation = Mathf.Repeat(Mathf.Abs((int)eventType) * 0.6180339f, 1f);
            return Mathf.Repeat(LaneBaseHues[(int)lane] + (variation - 0.5f) * BlockHueJitter, 1f);
        }

        /// <summary>
        /// 在给定饱和度下，二分出使 HSV 颜色相对亮度等于 target 的 V。
        ///
        /// 亮度随 V 单调递增，所以二分必定收敛。用迭代而不是解析解，
        /// 是因为相对亮度里夹了一段 sRGB 的幂函数，反解出来
        /// 一样是近似值，还更难读。
        /// </summary>
        private static float ValueForLuminance(float hue, float saturation, float target)
        {
            float low = 0f;
            float high = 1f;
            for (int i = 0; i < LuminanceSearchSteps; i++)
            {
                float mid = (low + high) * 0.5f;
                if (Luminance(Color.HSVToRGB(hue, saturation, mid)) < target)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            return (low + high) * 0.5f;
        }

        /// <summary>
        /// 使 HSV(hue, s, 1) 的相对亮度正好等于 target 的饱和度。
        ///
        /// 亮度随饱和度单调递减，同样二分。求出来的值可能超过上限，
        /// 那说明该色相本身够亮、不该再提饱和（黄绿一类），
        /// 夹到上限是调用方的事。
        /// </summary>
        private static float SaturationForLuminance(float hue, float target)
        {
            float low = 0f;
            float high = 1f;
            for (int i = 0; i < LuminanceSearchSteps; i++)
            {
                float mid = (low + high) * 0.5f;
                if (Luminance(Color.HSVToRGB(hue, mid, 1f)) > target)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            return (low + high) * 0.5f;
        }

        /// <summary>
        /// sRGB 相对亮度（WCAG 那个定义）。用它而不是 RGB 均值来给
        /// 配色排序深浅：均值会把蓝色算得比实际亮得多，六个家族排不齐。
        /// </summary>
        private static float Luminance(Color color)
        {
            return 0.2126f * Linearize(color.r)
                + 0.7152f * Linearize(color.g)
                + 0.0722f * Linearize(color.b);
        }

        private static float Linearize(float channel)
        {
            return channel <= 0.04045f
                ? channel / 12.92f
                : Mathf.Pow((channel + 0.055f) / 1.055f, 2.4f);
        }

        private static bool TryClassify(LevelEventType eventType, bool isDecoration, out TimelineLane lane)
        {
            lane = TimelineLane.Other;
            if (isDecoration)
            {
                lane = TimelineLane.Decoration;
                return true;
            }
            switch (eventType)
            {
                case LevelEventType.SetSpeed:
                case LevelEventType.Twirl:
                case LevelEventType.Pause:
                case LevelEventType.Hold:
                case LevelEventType.Multitap:
                case LevelEventType.Hide:
                case LevelEventType.ScaleMargin:
                case LevelEventType.ScaleRadius:
                case LevelEventType.TileDimensions:
                case LevelEventType.SetFloorIcon:
                    return false;

                // 轨道类事件以前整条被排除在时间轴之外。它们不播任何东西，
                // 改的是轨道本身——移动、变色、出现消失。做谱时
                // 「这一格轨道动了」和「这一格有个滤镜」一样值得看得见。
                case LevelEventType.MoveTrack:
                case LevelEventType.PositionTrack:
                case LevelEventType.ChangeTrack:
                case LevelEventType.ColorTrack:
                case LevelEventType.AnimateTrack:
                case LevelEventType.RecolorTrack:
                    lane = TimelineLane.Track;
                    return true;

                case LevelEventType.MoveCamera:
                case LevelEventType.ShakeScreen:
                    lane = TimelineLane.Camera;
                    return true;

                case LevelEventType.CustomBackground:
                case LevelEventType.Flash:
                case LevelEventType.SetFilter:
                case LevelEventType.SetFilterAdvanced:
                case LevelEventType.Bloom:
                case LevelEventType.SetPlanetRotation:
                case LevelEventType.ScalePlanets:
                case LevelEventType.SetFrameRate:
                case LevelEventType.ScreenTile:
                case LevelEventType.ScreenScroll:
                case LevelEventType.HallOfMirrors:
                    lane = TimelineLane.Visual;
                    return true;

                case LevelEventType.AddDecoration:
                case LevelEventType.AddText:
                case LevelEventType.SetText:
                case LevelEventType.MoveDecorations:
                case LevelEventType.AddObject:
                case LevelEventType.SetObject:
                case LevelEventType.SetDefaultText:
                case LevelEventType.AddParticle:
                case LevelEventType.SetParticle:
                case LevelEventType.EmitParticle:
                    lane = TimelineLane.Decoration;
                    return true;

                case LevelEventType.SetHitsound:
                case LevelEventType.SetHoldSound:
                case LevelEventType.PlaySound:
                    lane = TimelineLane.Audio;
                    return true;

                case LevelEventType.Checkpoint:
                case LevelEventType.MultiPlanet:
                case LevelEventType.FreeRoam:
                case LevelEventType.FreeRoamTwirl:
                case LevelEventType.FreeRoamRemove:
                case LevelEventType.FreeRoamWarning:
                case LevelEventType.AutoPlayTiles:
                case LevelEventType.KillPlayer:
                    lane = TimelineLane.Gameplay;
                    return true;

                default:
                    lane = TimelineLane.Other;
                    return true;
            }
        }

        private static string GetEventName(LevelEventType eventType)
        {
            string key = "editor." + eventType;
            try
            {
                string localized = RDString.Get(key);
                if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, key, StringComparison.Ordinal))
                {
                    return localized;
                }
            }
            catch
            {
                // Custom or newly introduced events may not have a localization key.
            }
            return eventType.ToString();
        }

        private static string FormatTime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            int minutes = Mathf.FloorToInt(seconds / 60f);
            float remainder = seconds - minutes * 60f;
            return minutes.ToString("00") + ":" + remainder.ToString("00.000");
        }

        private static readonly System.Text.StringBuilder RulerTimeBuilder =
            new System.Text.StringBuilder(8);

        private Image CreateImage(string name, RectTransform parent, float x, float y, float width, float height, Color color)
        {
            RectTransform rect = CreateRect(name, parent, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private Image CreateRoundedImage(string name, RectTransform parent, float x, float y, float width, float height, Color color)
        {
            Image image = CreateImage(name, parent, x, y, width, height, color);
            image.sprite = roundedSprite;
            image.type = Image.Type.Sliced;
            return image;
        }

        private readonly Dictionary<long, Texture2D> chevronTextures = new Dictionary<long, Texture2D>();
        private readonly Dictionary<long, Sprite> chevronSprites = new Dictionary<long, Sprite>();

        /// <summary>
        /// A downward chevron drawn into a texture: two strokes meeting at the bottom centre.
        /// Drawn rather than typed, because the workbench font has no arrow glyph.
        ///
        /// The stroke is measured against the hint's real width in pixels, so it keeps the same
        /// drawn thickness however far the timeline is zoomed. Baking one sprite would make the
        /// stroke thin out at high zoom and fatten at low zoom, because the sprite is stretched
        /// across a fixed-time hint rather than a fixed-width one.
        /// </summary>

        /// <summary>
        /// A downward chevron drawn into a texture: two strokes meeting at the bottom centre.
        /// Drawn rather than typed, because the workbench font has no arrow glyph.
        ///
        /// The stroke is measured against the hint's real width in pixels, so it keeps the same
        /// drawn thickness however far the timeline is zoomed. Baking one sprite would make the
        /// stroke thin out at high zoom and fatten at low zoom, because the sprite is stretched
        /// across a fixed-time hint rather than a fixed-width one.
        /// </summary>
        private Sprite GetChevronSprite(float hintPixels, float hintHeight)
        {
            int width = Mathf.Clamp(Mathf.RoundToInt(hintPixels), 8, 128);
            int height = Mathf.Clamp(Mathf.RoundToInt(hintHeight), 3, 64);
            // 2px stroke, no matter the size. Scaled by the box the arrow has been given instead
            // it would ride the aspect: two source pixels on the diagonal cover 2*sqrt(2) of
            // vertical, so a thin box fattens the stroke and a tall one starves it.
            const float halfStroke = 1f;
            long key = (long)width * 10000L + height;

            Sprite cached;
            if (chevronSprites.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = "ADOFAIEditorStudio.Chevron",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float d1 = DistanceToSegment(px, py, 0f, height * 0.80f, width * 0.5f, height * 0.20f);
                    float d2 = DistanceToSegment(px, py, width, height * 0.80f, width * 0.5f, height * 0.20f);
                    float d = Mathf.Min(d1, d2);
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(halfStroke - d + 0.5f) * 255f);
                    pixels[y * width + x] = new Color32(255, 255, 255, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;

            chevronTextures[key] = texture;
            chevronSprites[key] = sprite;
            return sprite;
        }

        private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax;
            float vy = by - ay;
            float wx = px - ax;
            float wy = py - ay;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 0.0001f ? 0f : Mathf.Clamp01((wx * vx + wy * vy) / len2);
            float dx = wx - vx * t;
            float dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private Sprite CreateRoundedSprite()
        {
            const int size = 32;
            const float radius = 7f;
            roundedTexture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "ADOFAIEditorStudio.RoundedRect",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float nearestX = Mathf.Clamp(px, radius, size - radius);
                    float nearestY = Mathf.Clamp(py, radius, size - radius);
                    float distance = Vector2.Distance(new Vector2(px, py), new Vector2(nearestX, nearestY));
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(radius + 0.5f - distance) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            roundedTexture.SetPixels32(pixels);
            roundedTexture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                roundedTexture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(8f, 8f, 8f, 8f)
            );
            sprite.name = "ADOFAIEditorStudio.RoundedRect";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>
        /// 空心圆角矩形，用来给选中的事件描一圈白边。
        ///
        /// 和 CreateRoundedSprite 同一套距离场算法，区别只在内侧：
        /// 距离落在 [radius - thickness, radius] 之间的像素才不透明，
        /// 中间挖空。这样它就是一条固定粗细的环，拉伸到任意块宽都不会
        /// 把边框拉粗——块宽由事件时长决定，能相差几十倍。
        ///
        /// 九宫格边界取 8，和圆角半径 7 对齐，于是只有四角和四边的
        /// 一小段被拉伸，圆角始终是圆的。
        /// </summary>
        private Sprite CreateOutlineSprite()
        {
            const int size = 32;
            const float radius = 7f;
            const float thickness = 2.4f;
            outlineTexture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "ADOFAIEditorStudio.OutlineRect",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float nearestX = Mathf.Clamp(px, radius, size - radius);
                    float nearestY = Mathf.Clamp(py, radius, size - radius);
                    float distance = Vector2.Distance(new Vector2(px, py), new Vector2(nearestX, nearestY));
                    // 只有贴着边缘那一圈带宽度的像素保留，其余全透明。
                    float coverage = Mathf.Min(radius + 0.5f - distance, distance - (radius - thickness));
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(coverage) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            outlineTexture.SetPixels32(pixels);
            outlineTexture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                outlineTexture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(8f, 8f, 8f, 8f)
            );
            sprite.name = "ADOFAIEditorStudio.OutlineRect";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>
        /// Toggles both view locks together.
        ///
        /// They are exposed as one control because the useful states are "the preview shows what
        /// the chart says" and "let me look around", and having position and zoom disagree left
        /// an ambiguous middle state that only produced confusion.
        /// </summary>

        /// <summary>
        /// Toggles both view locks together.
        ///
        /// They are exposed as one control because the useful states are "the preview shows what
        /// the chart says" and "let me look around", and having position and zoom disagree left
        /// an ambiguous middle state that only produced confusion.
        /// </summary>
        /// <summary>
        /// 切换「选中事件时是否跟随跳转」。
        ///
        /// 关闭时点事件只选中、不挪画面，适合连续改一批事件的参数；
        /// 打开时点哪跳哪，适合顺着谱面逐个检查。
        /// </summary>
        internal void ToggleSelectionFollow()
        {
            if (Main.Settings == null)
            {
                return;
            }

            Main.Settings.FollowSelectionOnClick = !Main.Settings.FollowSelectionOnClick;
            Main.SaveSettings();
        }

        /// <summary>
        /// 定位模式：预览画面里点轨道，播放头立刻跳到那一格。
        ///
        /// 开关只改一个设置项 —— 真正的跳转挂在 `SelectFloor` 补丁那条路上
        /// （点轨道 → 原生 `SelectFloor` → `EditorPlaybackBridge.OnFloorSelected`
        /// → `FollowSelectedFloor(force: 定位模式)`），所以这里没有第二套拾取逻辑。
        /// </summary>
        internal void ToggleLocateOnTrackClick()
        {
            if (Main.Settings == null)
            {
                return;
            }

            Main.Settings.LocateOnTrackClick = !Main.Settings.LocateOnTrackClick;
            Main.SaveSettings();
            if (Main.Overlay != null)
            {
                Main.Overlay.SetTransientStatus(Main.Settings.LocateOnTrackClick
                    ? "定位：开（点轨道跳画面，Ctrl+J）"
                    : "定位：关（Ctrl+J）");
            }
        }

        /// <summary>
        /// 把视图横向滚到播放头位置（一次性动作，取代原来的「自动翻页」开关）。
        ///
        /// 落点放在视口左侧 1/3 处 —— 这样"跳过去"之后，播放头右侧还留着
        /// 大部分可看的空间。
        /// </summary>
        internal void LocateToPlayhead()
        {
            if (!viewInitialized || pixelsPerSecond <= 0f)
            {
                return;
            }
            float current = PlayheadSeconds;
            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                // 固定播放头下本来就一直在摆正，按一下只要放开"手动让位"即可。
                centeredHoldUntil = 0f;
                visualsDirty = true;
            }
            else
            {
                // **滑过去，不是瞬移** —— 瞬移在长谱面上很容易丢失方位感。
                viewGlideTarget = current - bodyWidth * 0.34f
                    / Mathf.Max(1f, pixelsPerSecond);
                viewGliding = true;
                MarkManualNavigation();
                visualsDirty = true;
            }
            if (Main.Overlay != null)
            {
                Main.Overlay.SetTransientStatus(
                    "已定位到播放头 " + current.ToString("0.000") + "s");
            }
        }

        /// <summary>
        /// 吸附开关的图标：两块一样大的圆角方块中间夹一条对齐线。
        ///
        /// 开：两块**吸在一起**，中间那条线正好落在它们的接缝上，方块染强调色。
        /// 关：两块**分开**，中间留出明显的缝。
        /// 两态的差别是"缝的有无"，比换个符号更容易一眼认出来。
        /// </summary>
        private static Sprite CreateSnapDragSprite(bool on)
        {
            const int w = 32;
            const int h = 28;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var px = new Color32[w * h];

            const float cx = 16f;
            const float cy = 14f;

            void FillRoundedRect(float left, float top, float right, float bottom, float radius)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float pxc = x + 0.5f;
                        float pyc = y + 0.5f;
                        // 到圆角矩形边界的距离：把采样点夹进"核心矩形"
                        // 之后剩下的偏移，就是到最近圆角中心的距离。
                        float qx = Mathf.Clamp(pxc, left + radius, right - radius);
                        float qy = Mathf.Clamp(pyc, top + radius, bottom - radius);
                        float dx = pxc - qx;
                        float dy = pyc - qy;
                        float a = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                        if (a <= 0f)
                        {
                            continue;
                        }
                        int o = y * w + x;
                        byte v = (byte)Mathf.RoundToInt(a * 255f);
                        if (v > px[o].a)
                        {
                            px[o] = new Color32(255, 255, 255, v);
                        }
                    }
                }
            }

            void FillDisc(float cx0, float cy0, float radius)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float dx = x + 0.5f - cx0;
                        float dy = y + 0.5f - cy0;
                        float a = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                        if (a <= 0f)
                        {
                            continue;
                        }
                        int o = y * w + x;
                        byte v = (byte)Mathf.RoundToInt(a * 255f);
                        if (v > px[o].a)
                        {
                            px[o] = new Color32(255, 255, 255, v);
                        }
                    }
                }
            }

            // 两块方块。开态两块贴在一起（缝隙 1px，视觉上就是一条接缝线），
            // 关态拉开 7px。方块本身够大够圆，缩到 18px 显示时不会糊成一坨。
            const float gapOpen = 1.5f;
            const float gapClosed = 7f;
            float gap = on ? gapOpen : gapClosed;
            const float boxW = 8.5f;
            const float boxH = 15f;

            FillRoundedRect(cx - gap - boxW, cy - boxH * 0.5f, cx - gap, cy + boxH * 0.5f, 2.6f);
            FillRoundedRect(cx + gap, cy - boxH * 0.5f, cx + gap + boxW, cy + boxH * 0.5f, 2.6f);

            // 对齐线。开态它落在接缝上（就是那条缝本身），关态则穿在两块
            // 中间的空隙里——两种情况下它都在"本该吸合的位置"，所以
            // 用户看的是方块有没有贴上去。
            FillDisc(cx, cy, 1.7f);
            FillRoundedRect(cx - 0.9f, 2f, cx + 0.9f, cy - 3f, 0.9f);
            FillRoundedRect(cx - 0.9f, cy + 3f, cx + 0.9f, h - 2f, 0.9f);

            tex.SetPixels32(px);
            tex.Apply(false, true);
            Sprite sprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, w, h),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private void ApplySnapDragIcon()
        {
            if (snapDragButton == null)
            {
                return;
            }
            TextMeshProUGUI label = snapDragButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
            Transform existing = snapDragButton.transform.Find("SnapDragIcon");
            if (existing != null)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
            }
            if (snapDragSprite != null)
            {
                UnityEngine.Object.Destroy(snapDragSprite);
            }

            bool on = Main.Settings == null || Main.Settings.SnapTimelineDrag;
            snapDragSprite = CreateSnapDragSprite(on);
            Image icon = CreateImage("SnapDragIcon", snapDragButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, on ? Palette.Accent : TextMuted);
            icon.sprite = snapDragSprite;
            icon.raycastTarget = false;
        }

        private void ToggleSnapDrag()
        {
            if (Main.Settings == null)
            {
                return;
            }
            Main.Settings.SnapTimelineDrag = !Main.Settings.SnapTimelineDrag;
            ApplySnapDragIcon();
            // 时间轴自己没有状态栏，摘要列每秒都会被 UpdateSummary 覆写，
            // 所以借工作台页眉那行读数报一下开关状态。
            if (owner != null)
            {
                owner.SetSceneSelectionStatus(
                    Main.Settings.SnapTimelineDrag ? "拖动吸附：开" : "拖动吸附：关");
            }
        }

        /// <summary>图标按钮：清掉文字、塞一张自绘图标，并挂上悬停提示。</summary>
        private Button CreateIconButton(string name, RectTransform parent, float x,
            float width, Sprite sprite, Color tint, System.Action action, string tip)
        {
            Button button = CreateButton(name, parent, x, 7f, width, 29f,
                string.Empty, delegate { action(); });
            HideButtonLabel(button);
            Image icon = CreateImage("Icon", button.GetComponent<RectTransform>(),
                4f, 6f, width - 8f, 16f, tint);
            icon.sprite = sprite;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            UiHoverTip.Attach(button, tip);
            return button;
        }

        /// <summary>车道筛选按钮的图标/颜色每帧对齐一次（切车道、数据刷新都会变）。</summary>
        private void RefreshLaneButtons()
        {
            for (int i = 0; i < laneButtons.Count && i < laneIcons.Count; i++)
            {
                Image icon = laneIcons[i];
                if (icon == null)
                {
                    continue;
                }
                Color wanted = IsLaneVisible(i) ? TextPrimary : TextMuted;
                if (icon.color != wanted)
                {
                    icon.color = wanted;
                }
                Sprite sprite = GetLaneIcon(i);
                if (sprite != null && icon.sprite != sprite)
                {
                    icon.sprite = sprite;
                }
            }
        }

        /// <summary>回到开头：一条竖线 + 向左的三角。</summary>
        private static Sprite CreateRewindSprite()
        {
            const int w = 24;
            const int h = 22;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] px = new Color32[w * h];
            Color32 on = new Color32(255, 255, 255, 255);
            Color32 off = new Color32(0, 0, 0, 0);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = off;
            }

            int midY = h / 2;
            for (int y = 4; y < h - 4; y++)
            {
                for (int x = 4; x <= 5; x++)
                {
                    px[y * w + x] = on;
                }
            }
            for (int i = 0; i <= 7; i++)
            {
                for (int t = -i; t <= i; t++)
                {
                    int x = 16 - i;
                    int y = midY + t;
                    if (x >= 0 && x < w && y >= 0 && y < h)
                    {
                        px[y * w + x] = on;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>缩小：一条横杠。</summary>
        private static Sprite CreateMinusSprite()
        {
            const int w = 24;
            const int h = 22;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] px = new Color32[w * h];
            Color32 on = new Color32(255, 255, 255, 255);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(0, 0, 0, 0);
            }
            int midY = h / 2;
            for (int x = 5; x <= 18; x++)
            {
                px[(midY - 1) * w + x] = on;
                px[midY * w + x] = on;
            }
            tex.SetPixels32(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>放大：一个加号。</summary>
        private static Sprite CreatePlusSprite()
        {
            const int w = 24;
            const int h = 22;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] px = new Color32[w * h];
            Color32 on = new Color32(255, 255, 255, 255);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(0, 0, 0, 0);
            }
            int midY = h / 2;
            int midX = w / 2;
            for (int x = 5; x <= 18; x++)
            {
                px[(midY - 1) * w + x] = on;
                px[midY * w + x] = on;
            }
            for (int y = 4; y <= h - 5; y++)
            {
                px[y * w + (midX - 1)] = on;
                px[y * w + midX] = on;
            }
            tex.SetPixels32(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// 刷新那两颗按钮的图标：左边那颗是开关（开 = 强调色、关 = 弱化色），
        /// 右边那颗是「定位到播放头」的一次性动作（恒为主文字色）。
        /// 键盘（Ctrl+J / Ctrl+K）也能触发，所以放在 Update 里跑。
        /// </summary>
        private void RefreshFeatureButtons()
        {
            bool locate = Main.Settings != null && Main.Settings.LocateOnTrackClick;

            if (locateIcon != null)
            {
                if (locateSprite == null)
                {
                    locateSprite = CreateLocateSprite();
                }
                if (locateIcon.sprite != locateSprite)
                {
                    locateIcon.sprite = locateSprite;
                }
                Color wanted = locate ? Palette.Accent : Palette.TextMuted;
                if (locateIcon.color != wanted)
                {
                    locateIcon.color = wanted;
                }
            }

            if (locatePlayheadIcon != null)
            {
                if (locatePlayheadSprite == null)
                {
                    // 同一枚定位针 + 针尖下一条横线：和跳转按钮成对，
                    // 但一眼能看出这颗是“定位到播放头”。
                    locatePlayheadSprite = CreateLocatePlayheadIconSprite();
                }
                if (locatePlayheadIcon.sprite != locatePlayheadSprite)
                {
                    locatePlayheadIcon.sprite = locatePlayheadSprite;
                }
                // 一次性动作，没有开/关状态 —— 恒为主文字色。
                if (locatePlayheadIcon.color != TextPrimary)
                {
                    locatePlayheadIcon.color = TextPrimary;
                }
            }
        }

        private static void HideButtonLabel(Button button)
        {
            if (button == null)
            {
                return;
            }
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
        }

        /// <summary>定位图标：一个靶环 + 中心点 + 四个刻度。</summary>
        private static Sprite CreateLocateSprite()
        {
            const int w = 24;
            const int h = 22;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] px = new Color32[w * h];
            Color32 on = new Color32(255, 255, 255, 255);
            Color32 off = new Color32(0, 0, 0, 0);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = off;
            }

            float cx = (w - 1) * 0.5f;
            float cy = (h - 1) * 0.5f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    // 环：7±1
                    if (Mathf.Abs(dist - 7f) <= 1f)
                    {
                        px[y * w + x] = on;
                    }
                    // 中心点
                    if (dist <= 1.6f)
                    {
                        px[y * w + x] = on;
                    }
                    // 四个刻度
                    if ((Mathf.Abs(dx) <= 0.6f && Mathf.Abs(dy) >= 9f)
                        || (Mathf.Abs(dy) <= 0.6f && Mathf.Abs(dx) >= 9f))
                    {
                        px[y * w + x] = on;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// 「点事件跟随」的图标：**一个事件块（横条）+ 指着它的指针**。
        ///
        /// 原来的图标是"准星 + 四角括弧"，和旁边那颗「点轨道跟随」（真准星）
        /// 几乎一样，两颗按钮并排时根本分不出谁是谁。现在画的是触发对象本身：
        /// 指针尖头**贴着**横条 = 开，**留一道缝** = 关。
        /// </summary>
        private static Sprite CreateEventFollowSprite(bool follow)
        {
            const int w = 32;
            const int h = 28;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] px = new Color32[w * h];

            void Plot(int x, int y, float alpha)
            {
                if (x < 0 || y < 0 || x >= w || y >= h || alpha <= 0f)
                {
                    return;
                }
                int index = y * w + x;
                byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                if (value > px[index].a)
                {
                    px[index] = new Color32(255, 255, 255, value);
                }
            }

            // 事件块：一条圆角横条
            const float left = 5f;
            const float right = 27f;
            const float top = 4f;
            const float bottom = 12f;
            const float corner = 2.5f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float cx = x + 0.5f;
                    float cy = y + 0.5f;
                    if (cx < left || cx > right || cy < top || cy > bottom)
                    {
                        // 圆角外侧：按到圆心的距离给抗锯齿
                        float dx = Mathf.Max(0f, Mathf.Max(left + corner - cx, cx - (right - corner)));
                        float dy = Mathf.Max(0f, Mathf.Max(top + corner - cy, cy - (bottom - corner)));
                        float outside = Mathf.Sqrt(dx * dx + dy * dy);
                        Plot(x, y, Mathf.Clamp01(corner - outside + 0.5f));
                    }
                    else
                    {
                        Plot(x, y, 1f);
                    }
                }
            }

            // 指针：向下的实心箭头。开着的时候尖头贴住横条，关着留 3.5px 缝。
            float gap = follow ? 0f : 3.5f;
            float tipY = bottom + 1.5f + gap;
            float baseY = tipY + 7f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float cx = x + 0.5f;
                    float cy = y + 0.5f;
                    if (cy < tipY - 0.5f || cy > baseY + 0.5f)
                    {
                        continue;
                    }
                    float t = Mathf.InverseLerp(tipY, baseY, cy);
                    float half = Mathf.Lerp(0.7f, 4.4f, t);
                    Plot(x, y, Mathf.Clamp01(half - Mathf.Abs(cx - 16f) + 0.5f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, w, h),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>刷新「固定播放头」那颗按钮的图标：开 = 强调色，关 = 弱化色。</summary>
        private void ApplyFollowModeIcon()
        {
            if (followModeButton == null)
            {
                return;
            }
            HideButtonLabel(followModeButton);
            Transform existing = followModeButton.transform.Find("FollowIcon");
            if (existing != null)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
            }
            if (followModeSprite != null)
            {
                UnityEngine.Object.Destroy(followModeSprite);
            }
            bool centered = followMode == TimelineFollowMode.CenteredPlayhead;
            followModeSprite = CreateFollowModeSprite(centered);
            Image icon = CreateImage("FollowIcon", followModeButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, centered ? Palette.Accent : TextMuted);
            icon.sprite = followModeSprite;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
        }

        /// <summary>
        /// 图标：一条竖线（播放头）+ 两侧内容条。
        /// 关 = 内容条靠右（自由滚动）；开 = 内容条在竖线两侧（居中）。
        /// </summary>
        private static Sprite CreateFollowModeSprite(bool centered)
        {
            const int w = 32;
            const int h = 28;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] px = new Color32[w * h];

            void Fill(int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x < 0 || y < 0 || x >= w || y >= h)
                        {
                            continue;
                        }
                        px[y * w + x] = new Color32(255, 255, 255, 255);
                    }
                }
            }

            // 播放头竖线
            Fill(15, 4, 16, 23);
            // 内容条：开 → 左右各一条；关 → 只在右侧
            Fill(6, 9, 13, 11);
            Fill(6, 16, 13, 18);
            Fill(18, 9, 25, 11);
            Fill(18, 16, 25, 18);
            if (!centered)
            {
                // 关：把左边两条擦掉，只留右边
                for (int y = 9; y <= 18; y++)
                {
                    for (int x = 6; x <= 13; x++)
                    {
                        px[y * w + x] = new Color32(0, 0, 0, 0);
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, w, h),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private void ApplySelectionFollowIcon()
        {
            if (selectionFollowButton == null)
            {
                return;
            }
            TextMeshProUGUI label = selectionFollowButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
            Transform existing = selectionFollowButton.transform.Find("SelectionFollowIcon");
            if (existing != null)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
            }
            if (selectionFollowSprite != null)
            {
                UnityEngine.Object.Destroy(selectionFollowSprite);
            }
            bool on = Main.Settings != null && Main.Settings.FollowSelectionOnClick;
            selectionFollowSprite = CreateEventFollowSprite(on);
            Image icon = CreateImage("SelectionFollowIcon", selectionFollowButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, TextPrimary);
            icon.sprite = selectionFollowSprite;
            icon.raycastTarget = false;
        }

        private void ToggleViewLock()
        {
            if (Main.Settings == null)
            {
                return;
            }

            bool locked = !(Main.Settings.LockPreviewPosition && Main.Settings.LockPreviewZoom);
            Main.Settings.LockPreviewPosition = locked;
            Main.Settings.LockPreviewZoom = locked;

            if (locked)
            {
                // Snap back rather than waiting for the per-frame enforcement, so the click has
                // a visible effect even when nothing is currently pressing the view around.
                EditorPlaybackBridge.ResetPreviewZoom();
                CameraPreviewDomain.ResetUserPan();
            }

            EditorPlaybackBridge.ApplyPreviewPositionLock(locked);
            ApplyViewLockIcon();
            Main.SaveSettings();
        }

        /// <summary>
        /// Repaints the padlock for the current lock state. Both settings are kept in step by
        /// ToggleViewLock, so either one tells the whole story.
        /// </summary>

        // ---------------------------------------------------------------- 放置事件面板

        /// <summary>第二行顶部相对面板顶部的偏移。第一行占 0..34，这里接在后面。</summary>
        private const float PaletteRowY = 42f + 2f;
        /// <summary>事件按钮之间的间隔。</summary>
        private const float PaletteGap = 3f;
        /// <summary>不同分类之间额外留出的间隔——分组靠它读出来。</summary>
        private const float PaletteMinGroupGap = 16f;
        /// <summary>按钮最大边长。事件面板保持这个尺寸，放不下就换行。</summary>
        private const float PaletteMaxButtonSize = 26f;
        /// <summary>第二行左右两边各留的空。</summary>
        private const float PaletteMargin = 12f;

        private readonly List<Button> paletteButtons = new List<Button>();
        private readonly List<Image> paletteIcons = new List<Image>();
        private readonly List<LevelEventType> paletteTypes = new List<LevelEventType>();
        private bool paletteBuilt;

        internal LevelEventType PaletteSlotType(int index)
        {
            return index >= 0 && index < paletteEntries.Count
                ? paletteEntries[index]
                : LevelEventType.None;
        }

        /// <summary>槽位对应的中文事件名（做悬停提示）。</summary>
        internal string PaletteSlotName(int index)
        {
            LevelEventType type = PaletteSlotType(index);
            return type == LevelEventType.None ? null : GetEventName(type);
        }

        /// <summary>
        /// 第二行的放置事件面板：按分类分组，所有可用事件平铺在一行里。
        ///
        /// 原版是「一排分类页签 + 一页按钮 + 翻页」，那是因为它的面板窄。
        /// 这里时间轴的宽度足够铺下全部六十来个事件，所以不做分页——直接一行
        /// 铺到底、分类之间留大间隔，找事件不用来回翻页，一眼扫完。
        ///
        /// 图标直接取游戏自己的贴图表（GCS.levelEventIcons），不自己画：
        /// 原版按钮用的就是它们，同一套图标才一眼认得出是哪个事件。这些表由
        /// 编辑器打开时填充，所以谱面加载完成前可能是空的；那种情况下按钮
        /// 先留空，等数据就绪由 RefreshEventPalette 补上。
        ///
        /// 点击一律转给 scnEditor.AddEventAtSelected：它已经处理了全部校验
        /// （Hold/Pause 与 FreeRoam/Twirl 的互斥、末格不能再放 FreeRoam、
        /// 可切换类型是移除还是新增）以及撤销快照。自己插事件会漏掉这些。
        /// </summary>
        private void BuildEventPalette(RectTransform parent)
        {
            paletteBuilt = true;
            RefreshEventPalette();
        }

        /// <summary>
        /// 事件的平铺顺序：按分类分组。分类顺序固定，不随谱面变化，
        /// 否则每次打开按钮位置都不一样。
        /// </summary>
        private static readonly LevelEventCategory[] PaletteCategoryOrder =
        {
            LevelEventCategory.Gameplay,
            LevelEventCategory.TrackFx,
            LevelEventCategory.DecorationFx,
            LevelEventCategory.VisualFx,
            LevelEventCategory.FxModifiers,
            LevelEventCategory.Conveniences,
            LevelEventCategory.Jank
        };

        private readonly List<LevelEventType> paletteEntries = new List<LevelEventType>();
        private readonly List<bool> paletteGroupStart = new List<bool>();

        /// <summary>
        /// 当前应该显示的事件，按分类分组、组内保持枚举顺序。
        /// 过滤规则照抄原版：只留启用的（isActive 会滤掉 pro / DLC 事件）、
        /// 排除 ChangeTrack、以及只能放在第一格之外的事件在选中第一格时隐藏。
        /// </summary>
        private void CollectPaletteEvents()
        {
            paletteEntries.Clear();
            paletteGroupStart.Clear();
            if (GCS.levelEventsInfo == null)
            {
                return;
            }

            for (int c = 0; c < PaletteCategoryOrder.Length; c++)
            {
                LevelEventCategory category = PaletteCategoryOrder[c];
                bool firstOfGroup = true;
                foreach (LevelEventInfo info in GCS.levelEventsInfo.Values)
                {
                    if (info == null)
                    {
                        continue;
                    }
                    if (info.type == LevelEventType.ChangeTrack)
                    {
                        continue;
                    }
                    if (!info.isActive)
                    {
                        continue;
                    }
                    if (info.categories == null)
                    {
                        continue;
                    }
                    if (!info.categories.Contains(category))
                    {
                        continue;
                    }
                    paletteEntries.Add(info.type);
                    paletteGroupStart.Add(firstOfGroup);
                    firstOfGroup = false;
                }
            }
        }

        /// <summary>
        /// 让第二行与当前谱面、选中格同步：重建按钮集合、算尺寸、摆位置。
        /// 谱面没加载时 GCS 的贴图表是空的，按钮会留空——这是预期行为。
        /// </summary>
        internal void RefreshEventPalette()
        {
            if (!paletteBuilt || root == null)
            {
                return;
            }

            CollectPaletteEvents();
            EnsurePaletteButtons(paletteEntries.Count);

            // 一行排不下就换行，而不是把按钮缩到看不清。
            //
            // 原来是「宽度够就一行铺满，不够就等比缩小到下限」——下限只有
            // 14px，事件多的时候会挤成一条几乎点不准的细格，而且一旦算出的
            // 行高超过预留的那一行，最右边几个就被裁掉，看着像「没显示
            // 完全、要点一下才出来」。现在保持按钮的正常尺寸，放不下就往
            // 下排，工具区跟着变高。
            float available = Mathf.Max(80f, root.rect.width - ContentShift - PaletteMargin * 2f);
            int layoutRows = LayoutPaletteRows(available);

            // 每个事件按钮挂上「悬停显示事件名」——和标题行那排图标按钮一致。
            for (int i = 0; i < paletteButtons.Count; i++)
            {
                string name = i < paletteEntries.Count ? PaletteSlotName(i) : null;
                UiHoverTip.Attach(paletteButtons[i], name);
            }

        }

        /// <summary>
        /// 把事件按钮按分类分行摆放，返回一共占了几行。
        ///
        /// 分类之间的大间隔是这一行里唯一允许被打断的地方：一行已经排到
        /// 装不下下一个按钮时，如果前面还有没放完的按钮，就换到下一行。
        /// 按分类断行而不是按固定数量，分组的读数才不会跨行断开。
        /// </summary>
        private int LayoutPaletteRows(float available)
        {
            float size = PaletteMaxButtonSize;
            float groupGap = Mathf.Max(PaletteMinGroupGap, size * 0.6f);

            float x = PaletteMargin + ContentShift;
            float y = PaletteRowY;
            int rows = 1;

            for (int i = 0; i < paletteEntries.Count; i++)
            {
                bool groupStart = i > 0 && paletteGroupStart[i];
                float advance = size + PaletteGap + (groupStart ? groupGap : 0f);

                // 换行判定：这一行已经放了东西，再加这个就超宽。
                if (x > PaletteMargin + ContentShift + 0.01f
                    && x + advance - PaletteGap > PaletteMargin + ContentShift + available)
                {
                    x = PaletteMargin + ContentShift;
                    y += size + PaletteGap;
                    rows++;
                    groupStart = false;
                }

                if (groupStart)
                {
                    x += groupGap;
                }

                Button button = paletteButtons[i];
                LevelEventType type = paletteEntries[i];
                paletteTypes[i] = type;
                button.gameObject.SetActive(true);
                SetTopLeft(button.GetComponent<RectTransform>(), x, y, size, size);

                Image icon = paletteIcons[i];
                if (icon != null)
                {
                    float inset = Mathf.Max(2f, size * 0.16f);
                    SetTopLeft(icon.rectTransform, inset, inset, size - inset * 2f, size - inset * 2f);
                    Sprite sprite = null;
                    if (GCS.levelEventIcons != null)
                    {
                        GCS.levelEventIcons.TryGetValue(type, out sprite);
                    }
                    icon.sprite = sprite;
                    icon.enabled = sprite != null;
                }
                x += size + PaletteGap;
            }

            // 这次没用到的按钮收起来。
            for (int i = paletteEntries.Count; i < paletteButtons.Count; i++)
            {
                paletteTypes[i] = LevelEventType.None;
                paletteButtons[i].gameObject.SetActive(false);
            }

            // 工具区高度跟着行数走，时间轴可视区让位。
            float wantedHeader = PaletteRowY + rows * (size + PaletteGap) + 6f;
            if (!Mathf.Approximately(headerHeight, wantedHeader))
            {
                headerHeight = wantedHeader;
                float rootHeight = root == null ? 0f : Mathf.Max(220f, root.rect.height);
                bodyHeight = rootHeight - headerHeight - 8f;
                // viewport 可能还不存在：BuildEventPalette 跑在 BuildUi 里
                // 创建 viewport 之前。那次调用只需要把 headerHeight 定下来，
                // 紧跟其后的 viewport 创建会直接读到新值；这里硬写会空引用，
                // 把整个 UI 构建打挂（F8 没反应就是这么来的）。
                if (viewport != null)
                {
                    SetTopLeft(viewport, GutterWidth + ContentShift, headerHeight, bodyWidth, bodyHeight);
                }
            }
            return rows;
        }

        /// <summary>按钮不够就补建。只增不减——事件少了只是隐藏，不销毁重建。</summary>
        private void EnsurePaletteButtons(int count)
        {
            while (paletteButtons.Count < count)
            {
                int index = paletteButtons.Count;
                Button button = CreateButton(
                    "PaletteEvent" + index, root, 0f, PaletteRowY,
                    PaletteMaxButtonSize, PaletteMaxButtonSize, string.Empty,
                    delegate { ActivatePaletteEvent(index); });
                TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    label.text = string.Empty;
                }
                Image icon = CreateImage("EventIcon", button.GetComponent<RectTransform>(),
                    0f, 0f, PaletteMaxButtonSize, PaletteMaxButtonSize, Color.white);
                icon.preserveAspect = true;
                icon.raycastTarget = false;

                paletteButtons.Add(button);
                paletteIcons.Add(icon);
                paletteTypes.Add(LevelEventType.None);
            }
        }

        // 齿标：一条竖线 + 一个时间文字。池子按屏宽最多 32 组。
        private readonly List<Image> tickLinePool = new List<Image>();
        private readonly List<TextMeshProUGUI> tickLabelPool = new List<TextMeshProUGUI>();

        private readonly List<Image> gridLinePool = new List<Image>();

        /// <summary>
        /// 画标尺：**刻度写的是格子编号**（`floor.seqID`），不是秒。
        ///
        /// 数据来自 `CollectRuler()` —— 它按可视格数均摊格宽、按 seqID 出标签，
        /// 这套数学是从原生 `RefreshGrid` 搬来的（变速谱里格子时长不等，
        /// 按时间均分会对不上格线）。原来它只被 ImGui 的 `DrawRuler` 调，
        /// 主体停掉后标尺就空了。
        /// </summary>
        private void UpdateRuler()
        {
            if (rulerBackdrop == null || viewport == null)
            {
                return;
            }
            CollectRuler();

            List<RulerTick> ticks = RulerTicks;
            int used = 0;
            for (int i = 0; i < ticks.Count && used < 64; i++)
            {
                RulerTick tick = ticks[i];
                if (tick.X < -40f || tick.X > viewport.rect.width + 40f)
                {
                    continue;
                }
                EnsureRulerSlot(used + 1);

                Image line = tickLinePool[used];
                if (!line.gameObject.activeSelf)
                {
                    line.gameObject.SetActive(true);
                }
                SetTopLeft(line.rectTransform, tick.X, RulerHeight - 9f, 1f, 8f);

                TextMeshProUGUI label = tickLabelPool[used];
                if (!label.gameObject.activeSelf)
                {
                    label.gameObject.SetActive(true);
                }
                label.text = tick.Label;
                SetTopLeft(label.rectTransform, tick.X + 3f, 1f, 56f, 16f);
                used++;
            }

            for (int i = used; i < tickLinePool.Count; i++)
            {
                if (tickLinePool[i].gameObject.activeSelf)
                {
                    tickLinePool[i].gameObject.SetActive(false);
                }
                if (tickLabelPool[i].gameObject.activeSelf)
                {
                    tickLabelPool[i].gameObject.SetActive(false);
                }
            }

            // 格线：比刻度密，铺满从标尺下沿到视口底。
            List<RulerLine> lines = RulerLines;
            int gridUsed = 0;
            for (int i = 0; i < lines.Count && gridUsed < 220; i++)
            {
                float x = lines[i].X;
                if (x < 0f || x > viewport.rect.width)
                {
                    continue;
                }
                EnsureGridSlot(gridUsed + 1);
                Image grid = gridLinePool[gridUsed];
                if (!grid.gameObject.activeSelf)
                {
                    grid.gameObject.SetActive(true);
                }
                SetTopLeft(grid.rectTransform, x, RulerHeight, 1f,
                    Mathf.Max(0f, viewport.rect.height - RulerHeight));
                gridUsed++;
            }
            for (int i = gridUsed; i < gridLinePool.Count; i++)
            {
                if (gridLinePool[i].gameObject.activeSelf)
                {
                    gridLinePool[i].gameObject.SetActive(false);
                }
            }
        }

        private void EnsureGridSlot(int count)
        {
            while (gridLinePool.Count < count)
            {
                Image grid = CreateImage("GridLine" + gridLinePool.Count,
                    gridLayer != null ? gridLayer : blockLayer,
                    0f, 0f, 1f, 10f, new Color(1f, 1f, 1f, 0.06f));
                grid.raycastTarget = false;
                gridLinePool.Add(grid);
            }
        }

        private void EnsureRulerSlot(int count)
        {
            while (tickLinePool.Count < count)
            {
                // **齿标要够亮。** 标尺底色是全透明的（靠模块底色透上来），
                // 1px / 22% 白 + 弱化文字叠在 #0B0B0B 上几乎看不见 ——
                // 用户反馈「齿标变透明了」就是这个。线和字都提一档。
                Image line = CreateImage("Tick" + tickLinePool.Count, viewport,
                    0f, 0f, 1f, 8f, new Color(1f, 1f, 1f, 0.55f));
                line.raycastTarget = false;
                tickLinePool.Add(line);

                TextMeshProUGUI label = CreateText("TickLabel" + tickLabelPool.Count,
                    viewport, 0f, 0f, 56f, 16f, string.Empty, 10f,
                    Palette.TextPrimary, TextAlignmentOptions.MidlineLeft, false);
                label.raycastTarget = false;
                tickLabelPool.Add(label);
            }
        }

        /// <summary>按需把块对象建到池子里（上限 `MarkerPoolSize`）。</summary>
        private TimelineMarker CreateMarker()
        {
            {
                int index = markerPool.Count;
                RectTransform rect = CreateRect("Block" + index, blockLayer,
                    0f, 0f, 100f, DefaultRowHeight);
                Image background = rect.gameObject.AddComponent<Image>();
                background.raycastTarget = true;
                // 块允许圆角：用和面板同一个九宫格圆角图，拉伸时四角不糊。
                if (roundedSprite != null)
                {
                    background.sprite = roundedSprite;
                    background.type = Image.Type.Sliced;
                }

                TimelineMarker marker = new TimelineMarker();
                marker.GameObject = rect.gameObject;
                marker.Rect = rect;
                marker.Background = background;

                // 选中用**白色描边**表示，不再提亮底色。
                if (outlineSprite != null)
                {
                    Image outline = CreateImage("Outline", rect, 0f, 0f, 100f,
                        DefaultRowHeight, Color.white);
                    outline.sprite = outlineSprite;
                    outline.type = Image.Type.Sliced;
                    outline.raycastTarget = false;
                    marker.Outline = outline;
                    outline.gameObject.SetActive(false);
                }

                Button button = rect.gameObject.AddComponent<Button>();
                button.targetGraphic = background;
                button.transition = Selectable.Transition.None;
                // **交给 ActivateMarker**：它区分「Ctrl 切换多选」和
                // 「普通点击替换选择」。原来这里直接调 ToggleSelectionOf，
                // 于是不按 Ctrl 连点几个块会变成多选（用户报的问题）。
                button.onClick.AddListener(delegate { ActivateMarker(marker); });

                TimelineBlockDrag drag = rect.gameObject.AddComponent<TimelineBlockDrag>();
                drag.Initialize(this, marker);
                marker.Drag = drag;

                marker.Label = CreateText("Label", rect, 22f, 0f, 80f, DefaultRowHeight,
                    string.Empty, 11f, Color.white, TextAlignmentOptions.MidlineLeft, false);
                marker.Label.overflowMode = TextOverflowModes.Ellipsis;
                marker.Label.raycastTarget = false;
                // **去掉阴影/描边**：块上的文字要干净，阴影会让黑字糊成一团。
                marker.Label.fontStyle = FontStyles.Normal;
                marker.Label.outlineWidth = 0f;
                Shadow labelShadow = marker.Label.GetComponent<Shadow>();
                if (labelShadow != null)
                {
                    Destroy(labelShadow);
                }
                Outline labelOutline = marker.Label.GetComponent<Outline>();
                if (labelOutline != null)
                {
                    Destroy(labelOutline);
                }
                // **关掉字体材质里的下投影（underlay）。** TMP 的阴影不是
                // `Shadow` 组件，而是材质上的 UNDERLAY 关键字 —— 只删组件
                // 是删不掉的（用户反馈「文字阴影还是没去掉」）。
                Material labelMaterial = marker.Label.fontMaterial;
                if (labelMaterial != null)
                {
                    labelMaterial.DisableKeyword("UNDERLAY_ON");
                    labelMaterial.DisableKeyword("UNDERLAY_INNER");
                }

                marker.Icon = CreateImage("Icon", rect, 4f, 5f, 16f, 16f, Color.white);
                marker.Icon.preserveAspect = true;
                marker.Icon.raycastTarget = false;

                RectTransform curveRect = CreateRect("Curve", rect, 4f, 4f, 40f, 10f);
                EaseCurveImage curve = curveRect.gameObject.AddComponent<EaseCurveImage>();
                curve.raycastTarget = false;
                marker.Curve = curve;

                markerPool.Add(marker);
                return marker;
            }
        }

        /// <summary>拖动时那条「原位置」竖线：每帧跟着视图换算，松手就藏。</summary>
        private void UpdateDragOriginGuide(float viewStart, float pixelsPerSecond,
            float viewHeight)
        {
            bool show = blockDragActive && !blockDragResizing
                && dragOrigins.Count > 0 && dragOrigins[0].Item != null;
            if (!show)
            {
                if (dragOriginGuide != null && dragOriginGuide.gameObject.activeSelf)
                {
                    dragOriginGuide.gameObject.SetActive(false);
                }
                return;
            }
            if (dragOriginGuide == null)
            {
                if (blockLayer == null)
                {
                    return;
                }
                Image guide = CreateImage("DragOriginGuide", blockLayer,
                    0f, RulerHeight, 1.5f, Mathf.Max(1f, viewHeight - RulerHeight),
                    DragOriginGuideColor);
                guide.raycastTarget = false;
                dragOriginGuide = guide.rectTransform;
            }
            if (!dragOriginGuide.gameObject.activeSelf)
            {
                dragOriginGuide.gameObject.SetActive(true);
            }
            float x = (dragOrigins[0].Start - viewStart) * pixelsPerSecond;
            SetTopLeft(dragOriginGuide, x, RulerHeight, 1.5f,
                Mathf.Max(1f, viewHeight - RulerHeight));
        }

        /// <summary>
        /// 把这一帧可见的块绑到池子里。几何直接照 ImGui 主体那套公式：
        /// 横轴按 `PixelsPerSecond`，纵轴按行号 × 行距，标尺高度以上被裁掉。
        /// </summary>
        private void UpdateBlocks()
        {
            if (blockLayer == null || viewport == null)
            {
                return;
            }

            float pixelsPerSecond = Mathf.Max(1f, PixelsPerSecond);
            float viewStart = ViewStartSeconds;
            float contentTop = RulerHeight - verticalScrollOffset;
            float stride = BlockStridePixels;
            float blockHeight = BlockHeightPixels;
            float viewWidth = viewport.rect.width;
            float viewHeight = viewport.rect.height;

            UpdateDragOriginGuide(viewStart, pixelsPerSecond, viewHeight);

            // **不要在这里清空 `freeMarkers`** —— 里面的对象是上一帧帧尾收进来的，
            // 正是这一帧绑定阶段要复用的。清在这一步的话，绑定阶段永远拿不到
            // 空闲对象，只能一路新建到池上限再 break（拖一下还是会丢一大片）。
            activeBlockIds.Clear();
            for (int i = 0; i < blockViews.Count; i++)
            {
                BlockView block = blockViews[i];
                float left = (block.Time - viewStart) * pixelsPerSecond;
                float width = Mathf.Max(BlockMinWidth,
                    block.Duration * pixelsPerSecond - BlockGap);
                float top = contentTop + block.Row * stride;

                if (left + width <= 0f || left >= viewWidth
                    || top + blockHeight <= RulerHeight || top >= viewHeight)
                {
                    continue;
                }
                TimelineMarker marker;
                if (!markerByBlockId.TryGetValue(block.Id, out marker))
                {
                    // **池满时回收"上一帧收起"的对象，而不是 break。**
                    //
                    // 原来撞到上限就跳出循环：滚一下视口，新进来的块都要新建
                    // 对象，一旦满了，后面所有块都没有实体 —— 表现就是
                    // "稍微动一下就消失一大片"（数据还在，拖过去照样判重叠变红）。
                    if (freeMarkers.Count > 0)
                    {
                        marker = freeMarkers[freeMarkers.Count - 1];
                        freeMarkers.RemoveAt(freeMarkers.Count - 1);
                        if (marker.BoundBlockId >= 0)
                        {
                            markerByBlockId.Remove(marker.BoundBlockId);
                        }
                    }
                    else if (markerPool.Count < MarkerPoolSize)
                    {
                        marker = CreateMarker();
                    }
                    else
                    {
                        break;
                    }
                    marker.BoundBlockId = block.Id;
                    markerByBlockId[block.Id] = marker;
                }
                activeBlockIds.Add(block.Id);
                marker.Item = FindItemForEvent(block.Event);
                if (!marker.GameObject.activeSelf)
                {
                    marker.GameObject.SetActive(true);
                }
                SetTopLeft(marker.Rect, left, top, width, blockHeight);

                // 选中不再改底色 —— 用描边表达（见下）。
                Color fill = block.BlockColor;
                fill.a = block.Active ? 1f : 0.46f;
                marker.Background.color = fill;

                if (marker.Outline != null)
                {
                    // **拖动中、而且还跟别人叠着 → 描红边**，松手时这一下会作废。
                    //
                    // 长事件往右铺得很远，拖动时只看这一块根本看不出后面撞没撞
                    // —— 这个红边就是那个提示（没有它，用户只能靠"撤回之后位置
                    // 怎么不对"来反推）。只查被拖着的那几块，每帧 O(块数)。
                    bool dragMember = blockDragActive && block.Event != null
                        && dragMembers.Contains(block.Event);
                    bool showOutline = block.Selected || dragMember;
                    if (marker.Outline.gameObject.activeSelf != showOutline)
                    {
                        marker.Outline.gameObject.SetActive(showOutline);
                    }
                    if (showOutline)
                    {
                        marker.Outline.color = dragMember && BlockOverlapsOthers(block.Event)
                            ? DragOverlapColor
                            : Color.white;
                        SetTopLeft(marker.Outline.rectTransform, -1f, -1f,
                            width + 2f, blockHeight + 2f);
                    }
                }

                marker.Label.text = block.Name ?? string.Empty;
                // 块上的文字/图标统一**纯黑**，在彩色底上对比最强。
                marker.Label.color = Color.black;
                SetTopLeft(marker.Label.rectTransform, block.Icon != null ? 22f : 8f, 0f,
                    Mathf.Max(10f, width - 28f), blockHeight);

                bool hasIcon = block.Icon != null;
                if (marker.Icon.gameObject.activeSelf != hasIcon)
                {
                    marker.Icon.gameObject.SetActive(hasIcon);
                }
                if (hasIcon)
                {
                    marker.Icon.sprite = block.Icon;
                    marker.Icon.color = Color.black;
                }

                // **行高压到一定程度就不画曲线了。** 再扁下去它只剩一条几乎
                // 贴着中线的细线，既读不出缓动形状、又和块名抢像素 ——
                // 不如干脆空出来。阈值取 26：最扁档（24）隐藏，默认档（30）正常画。
                bool hasCurve = block.Curve != null && block.Curve.Length >= 2
                    && width >= 44f && blockHeight >= EaseCurveHideBelowHeight;
                if (marker.Curve.gameObject.activeSelf != hasCurve)
                {
                    marker.Curve.gameObject.SetActive(hasCurve);
                }
                if (hasCurve)
                {
                    // **同一个数组不重复塞。** 每帧重设会让曲线跟着池子换绑抖动
                    // （用户看到「拖动时缓速一直在变」）。
                    if (!ReferenceEquals(marker.CurveValues, block.Curve))
                    {
                        marker.CurveValues = block.Curve;
                        marker.Curve.SetCurve(block.Curve);
                    }
                    // 缓速曲线**不用纯黑**：比文字浅一档，免得和名字糊在一起。
                    marker.Curve.color = new Color(0.12f, 0.12f, 0.12f, 0.95f);

                    // **轴心必须是左下角。** `EaseCurveImage` 的顶点是按
                    // 「(0,0) 在左下」写的（和 UILineRenderer 同一套约定）；
                    // 用左上角轴心就会上下镜像 —— 用户看到的就是
                    // 「拖动时曲线像 Y 轴反复翻转」。
                    RectTransform curveRect = marker.Curve.rectTransform;
                    curveRect.anchorMin = new Vector2(0f, 1f);
                    curveRect.anchorMax = new Vector2(0f, 1f);
                    curveRect.pivot = new Vector2(0f, 0f);
                    curveRect.anchoredPosition = new Vector2(4f, -(blockHeight - 4f));
                    curveRect.sizeDelta = new Vector2(width - 8f, blockHeight - 8f);
                }
            }

            // 这一帧没出现的块：把它的对象收进空闲表，留给下一帧的新块复用。
            // 先清空再重建，避免跨帧累积重复项（本帧复用过的不在此列，
            // 它们已经在 `activeBlockIds` 里）。
            freeMarkers.Clear();
            foreach (KeyValuePair<int, TimelineMarker> pair in markerByBlockId)
            {
                if (activeBlockIds.Contains(pair.Key))
                {
                    continue;
                }
                if (pair.Value.GameObject.activeSelf)
                {
                    pair.Value.GameObject.SetActive(false);
                }
                freeMarkers.Add(pair.Value);
            }
        }

        private void ActivatePaletteEvent(int slot)
        {
            if (slot < 0 || slot >= paletteTypes.Count)
            {
                return;
            }

            // 只读模式：点面板图标是**新建事件**（底下转的
            // `scnEditor.AddEventAtSelected`），会往谱面里加东西 —— 拦住。
            // 提示由 `AllowEdit` 统一发（工作台每次打开都回到只读，静默拦住
            // 会让人以为"按钮坏了"—— 这个误会已经骗了我们两回）。
            if (owner != null && !owner.AllowEdit())
            {
                return;
            }

            LevelEventType type = paletteTypes[slot];
            if (type == LevelEventType.None)
            {
                return;
            }
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            // 原版 AddEventAtSelected 加在「游戏里当前选中的格」上
            // （scnEditor.AddEventAtSelected: selectedFloors[0].seqID），
            // 而时间轴的播放头位置从来不写回那个选中——它只在点某个
            // 事件块时被同步过。所以从面板加事件会落到上次点过的那一格，
            // 而不是播放头停着的位置。
            //
            // 先把选中挪到播放头所在的格，再交给原版。这样它自己
            // 那套校验和撤销快照照旧生效，落点也对。
            int playheadFloor = PlayheadFloorIndex();
            if (playheadFloor >= 0)
            {
                // 纯粹是"把选中挪到播放头"，不该带动画面（否则定位开着会回环）。
                EditorPlaybackBridge.MarkNextFloorSelect(
                    EditorPlaybackBridge.FloorSelectPlayheadSync);
                editor.SelectFloor(editor.floors[playheadFloor], cameraJump: false);
            }

            // 交给原版：它带全部校验与撤销快照。
            editor.AddEventAtSelected(type);
            RefreshEventPalette();

            // 立刻重读谱面数据，别等那个 0.5 秒的轮询。
            //
            // 时间线的刷新是每帧检查一次「到点了没」，到了才走
            // RefreshData；而 RefreshData 内部还要靠指纹比对才知道
            // 事件数变了。两者叠加，一个新加的事件最坏要等半个周期
            // 才出现在时间线上——用户点完按钮盯着屏幕不动，会以为
            // 没生效。
            //
            // force: true 跳过指纹比对直接重建。这里刚改过谱面，
            // 重建是必然要做的，比对只会多花一次哈希。
            RefreshData(true);
            nextDataRefresh = Time.unscaledTime + DataRefreshInterval;

            // **纵向滚到刚加的那个块那儿。**
            //
            // 新事件是补到最下面一行的（见 `RebindBlockPlacements`），而时间轴一屏
            // 只显示十来行 —— 不滚的话用户点完按钮什么也看不到，以为没加上。
            ScrollToNewestBlock();
        }

        /// <summary>
        /// 让时间轴**立刻**重读谱面，不等轮询。
        ///
        /// 给"从别的模块改了谱面"的入口用（装饰物列表那四个新建按钮就是）——
        /// 时间线自己那条路本来就会调 `RefreshData`，不用重复。轮询虽然也会发现，
        /// 但最坏要等半个周期，用户点完按钮盯着屏幕会以为没生效。
        /// </summary>
        internal void RefreshDataNow()
        {
            RefreshData(true);
            nextDataRefresh = Time.unscaledTime + DataRefreshInterval;
        }

        /// <summary>
        /// 纵向滚到"最下面那一行"，也就是刚加的东西落在的地方。
        ///
        /// **所有"新建"入口都要调它** —— 事件面板加事件、装饰物列表那四个新建
        /// 按钮（文本/粒子/对象/图片）走的是两条不同的路，少调一处，那一条就会
        /// 表现得像"加不进去"（其实只是补在最下面、在屏幕外）。
        /// 横向一个字都不碰，理由见 `ScrollVerticallyToRow`。
        /// </summary>
        internal void ScrollToNewestBlock()
        {
            EnsureBlockPlacements();
            int lastRow = -1;
            for (int i = 0; i < blockPlacements.Count; i++)
            {
                BlockPlacement placement = blockPlacements[i];
                if (placement != null && placement.Row > lastRow)
                {
                    lastRow = placement.Row;
                }
            }
            ScrollVerticallyToRow(lastRow);
        }

        /// <summary>
        /// 把时间轴**纵向**滚到某一行，让它落在可视区**正中**（横向一个字都不碰）。
        ///
        /// 贴顶的话它上下都看不到东西 —— 而这个入口的用途就是"我新加的那个块
        /// 在哪儿"，要看的正是它周围那一片。可用高度＝内容区高减去标尺那一条，
        /// 再减掉块自身高度的一半，就是居中所需的偏移。
        ///
        /// 夹取交给 `ClampVerticalScroll` —— 别在这儿另算上下边界。
        /// </summary>
        internal void ScrollVerticallyToRow(int row)
        {
            if (row < 0)
            {
                return;
            }
            float visible = Mathf.Max(1f, bodyHeight - RulerHeight);
            verticalScrollOffset = row * BlockStridePixels
                - (visible - BlockHeightPixels) * 0.5f;
            ClampVerticalScroll();
        }

        /// <summary>
        /// 播放头当前停在哪一格。没有播放头（谱面为空）时返回 -1。
        ///
        /// LowerBoundFloors 给的是第一个 entryTime &gt;= time 的下标，
        /// 所以所在格要减一。
        ///
        /// **新建事件的入口都要先拿它把选中挪过去** —— 原版 `AddEventAtSelected` /
        /// `AddDecoration` 建在"游戏当前选中的格"上，而那个选中只在点事件块时
        /// 同步过；不挪的话新建的东西会落在上次点过的格（多半是 0）上，
        /// 而不是用户正看着的那一格。
        /// </summary>
        internal int PlayheadFloorIndex()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return -1;
            }
            float time = EditorPlaybackBridge.ReadSnapshot().CurrentTime;
            int index = LowerBoundFloors(editor.floors, time);
            return Mathf.Clamp(index - 1, 0, editor.floors.Count - 1);
        }

        private Sprite playheadGlowSprite;

        /// <summary>
        /// 播放头的辉光贴图：一块横向的 alpha 渐变。
        ///
        /// 中心不透明、向两侧用 smoothstep 衰减到 0，所以拉成任意高度的
        /// 细条都是一条干净的光带，没有硬边。用 1px 高即可——纵向不需要
        /// 渐变，整个高度维持同一个亮度。
        ///
        /// smoothstep 而不是线性：线性渐变在最外侧会以固定斜率撞到 0，
        /// 留下一道能看出来的边界；smoothstep 两端导数为 0，收得干净。
        /// </summary>
        private Sprite GetPlayheadGlowSprite()
        {
            if (playheadGlowSprite != null)
            {
                return playheadGlowSprite;
            }

            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false, true)
            {
                name = "ADOFAIEditorStudio.PlayheadGlow",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var px = new Color32[size];
            for (int x = 0; x < size; x++)
            {
                float t = Mathf.Abs((x + 0.5f) / size * 2f - 1f);
                float falloff = 1f - t;
                // 幂次衰减：能量集中在中心附近，边缘收得比 smoothstep 更快，
                // 光就不会铺成一片。
                falloff = falloff * falloff * falloff;
                px[x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(falloff * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);

            playheadGlowSprite = Sprite.Create(tex, new Rect(0f, 0f, size, 1),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            playheadGlowSprite.hideFlags = HideFlags.HideAndDontSave;
            return playheadGlowSprite;
        }

        private void ApplyViewLockIcon()
        {
            if (viewLockButton == null)
            {
                return;
            }

            bool locked = Main.Settings != null && Main.Settings.LockPreviewPosition;

            TextMeshProUGUI label = viewLockButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }

            Transform existing = viewLockButton.transform.Find("ViewLockIcon");
            if (existing != null)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
            }
            if (viewLockSprite != null)
            {
                UnityEngine.Object.Destroy(viewLockSprite);
            }

            viewLockSprite = CreateLockSprite(locked);
            Image icon = CreateImage(
                "ViewLockIcon",
                viewLockButton.GetComponent<RectTransform>(),
                7f,
                5f,
                18f,
                18f,
                locked ? TextPrimary : TextMuted
            );
            icon.sprite = viewLockSprite;
            icon.raycastTarget = false;
        }

        /// <summary>
        /// 绘制视图锁按钮的挂锁。
        ///
        /// 生成出来而不是取字体字形：工作区字体没有可靠的
        /// 挂锁码位，缺字会渲染成空白或方块。形状是
        /// 实心锁体加锁梁，解锁时锁梁画成打开并偏移，
        /// 状态一眼可见。
        /// </summary>
        private Sprite CreateLockSprite(bool locked)
        {
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = locked ? "ADOFAIEditorStudio.LockClosed" : "ADOFAIEditorStudio.LockOpen",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            // 锁体占下半部分；锁梁在它上方弯成弧。
            const float bodyLeft = 14f;
            const float bodyRight = 50f;
            const float bodyBottom = 8f;
            const float bodyTop = 34f;
            const float corner = 4f;

            // 锁梁是一段圆环。解锁时它被抬起并平移，
            // 不再把锁体闭合起来。
            float shackleCenterX = locked ? 32f : 40f;
            float shackleCenterY = locked ? 34f : 40f;
            const float shackleOuter = 15f;
            const float shackleInner = 9f;

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    // 锁体的圆角矩形。
                    float nearestX = Mathf.Clamp(px, bodyLeft + corner, bodyRight - corner);
                    float nearestY = Mathf.Clamp(py, bodyBottom + corner, bodyTop - corner);
                    float bodyDistance = Vector2.Distance(new Vector2(px, py), new Vector2(nearestX, nearestY));
                    float bodyAlpha = Mathf.Clamp01(corner + 0.5f - bodyDistance);

                    // 锁梁用的圆环上半部分。
                    float ringDistance = Vector2.Distance(
                        new Vector2(px, py), new Vector2(shackleCenterX, shackleCenterY));
                    float shackleAlpha = 0f;
                    if (py >= shackleCenterY)
                    {
                        float band = Mathf.Abs(ringDistance - (shackleOuter + shackleInner) * 0.5f);
                        float halfThickness = (shackleOuter - shackleInner) * 0.5f;
                        shackleAlpha = Mathf.Clamp01(halfThickness + 0.5f - band);
                    }

                    float alpha = Mathf.Clamp01(bodyAlpha + shackleAlpha);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect
            );
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private TextMeshProUGUI CreateText(
            string name,
            RectTransform parent,
            float x,
            float y,
            float width,
            float height,
            string value,
            float size,
            Color color,
            TextAlignmentOptions alignment,
            bool bold)
        {
            RectTransform rect = CreateRect(name, parent, x, y, width, height);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        private Button CreateButton(
            string name,
            RectTransform parent,
            float x,
            float y,
            float width,
            float height,
            string label,
            Action action)
        {
            Image image = CreateRoundedImage(name, parent, x, y, width, height, ButtonNormal);
            Button button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(delegate { action(); });
            CreateText("Label", image.rectTransform, 0f, 0f, width, height, label, 11f, TextSecondary, TextAlignmentOptions.Center, false);
            SetButtonPalette(button, false);
            return button;
        }

        private static void SetButtonPalette(Button button, bool selected)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = selected ? Selected : ButtonNormal;
            colors.highlightedColor = selected ? UiTheme.Current.SelectedHoverFill : ButtonHover;
            colors.pressedColor = selected ? UiTheme.Current.SelectedFill : ButtonHover * 0.85f;
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(ButtonNormal.r, ButtonNormal.g, ButtonNormal.b, 0.45f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }

        private static RectTransform CreateRect(string name, RectTransform parent, float x, float y, float width, float height)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            SetTopLeft(rect, x, y, width, height);
            return rect;
        }

        private static readonly Vector2 TopLeftAnchor = new Vector2(0f, 1f);

        /// <summary>
        /// 按左上轴心摆放一个 rect。
        ///
        /// 写入前先比对，值没变就不碰。这不是微优化：拖动时每帧要对
        /// 五百个以上的 rect 调用它（刻度标签、格子线、行斑纹、时间块
        /// 及其图标/文字/圆头/曲线），而写 RectTransform 的属性会让它变脏，
        /// 进而把所在的 Canvas 标脏。绝大多数情况下位置本来就没变——
        /// 只有正在移动的那一层在变——所以这一步跳掉的正是绝大部分写入。
        /// </summary>
        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            if (rect.anchorMin != TopLeftAnchor)
            {
                rect.anchorMin = TopLeftAnchor;
            }
            if (rect.anchorMax != TopLeftAnchor)
            {
                rect.anchorMax = TopLeftAnchor;
            }
            if (rect.pivot != TopLeftAnchor)
            {
                rect.pivot = TopLeftAnchor;
            }

            Vector2 position = new Vector2(x, -y);
            if (rect.anchoredPosition != position)
            {
                rect.anchoredPosition = position;
            }

            Vector2 size = new Vector2(width, height);
            if (rect.sizeDelta != size)
            {
                rect.sizeDelta = size;
            }
        }
    }
}
