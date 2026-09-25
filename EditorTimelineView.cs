using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAI;
using ADOFAIEditorStudio.Preview.Domains;
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
            Other
        }

        private sealed class TimelineItem
        {
            internal LevelEvent Event;
            internal TimelineLane Lane;
            internal float StartTime;
            internal float Duration;
            internal int FloorId;
            internal int SourceOrder;
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
            // 块起点画出的柔和圆头，让事件有个可见的起点，
            // 而不是块一开始就是生硬的切口。它也是唯一能标出
            // 零时长事件的东西，那种块否则只是一条细缝。
            internal Image Cap;
            internal EaseCurveImage Curve;
            internal Image Icon;
            internal TextMeshProUGUI Label;
            // 精确选中时点亮的白色描边。常驻对象，只切换显隐，
            // 尺寸由 BindMarker 按当前行高写——ALT+滚轮改行高后
            // 下一帧就跟上。
            internal Image Outline;
            // 拖动交互。挂在块上，随池复用，所以创建一次就够。
            internal TimelineBlockDrag Drag;
            internal TimelineItem Item;
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
            private Vector2 pressScreen;
            private float anchorScreenX;
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
                mode = Mode.None;
                dragging = false;

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

                pressScreen = eventData.pressPosition;
                anchorScreenX = eventData.pressPosition.x;
                originalStartTime = item.StartTime;
                originalDuration = item.Duration;

                dragging = true;
                view.BeginBlockDrag(marker, mode != Mode.Move);
                eventData.Use();
            }

            public void OnDrag(PointerEventData eventData)
            {
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

                // 块可能已经被池回收给别人了（RefreshData 重建了 TimelineItem）。
                // 这时手上这个 item 是孤儿，继续改它没有意义。
                if (marker.Item == null)
                {
                    Cancel();
                    return;
                }

                float deltaX = eventData.position.x - anchorScreenX;
                if (mode == Mode.Move && Mathf.Abs(deltaX) < DragThreshold)
                {
                    return;
                }

                float deltaTime = deltaX / Mathf.Max(1f, view.pixelsPerSecond);

                switch (mode)
                {
                    case Mode.Move:
                    {
                        // 拖块身：起点吸附到格边界，时长不变。
                        float start = view.SnapBlockTime(originalStartTime + deltaTime);
                        view.PreviewBlockDrag(marker, start, originalDuration);
                        break;
                    }
                    case Mode.ResizeRight:
                    {
                        // 右边缘：**不吸附**。吸附是"落到哪一格"这件事，
                        // 对拉长没有意义——把终点吸到最近的格边界，会让
                        // 一个短事件的终点吸回它自己起点所在的那条边界，
                        // 时长恒为 0，块当场塌成最小宽度，看起来就是
                        // 「拉不动」。长度要的是自由伸缩。
                        float end = originalStartTime + originalDuration + deltaTime;
                        view.PreviewBlockDrag(marker, originalStartTime, Mathf.Max(0f, end - originalStartTime));
                        break;
                    }
                    case Mode.ResizeLeft:
                    {
                        // 左边缘：同样不吸附。**右端钉住不动**，起点动多少，
                        // 时长就反向变多少。
                        float end = originalStartTime + originalDuration;
                        float start = Mathf.Min(originalStartTime + deltaTime, end);
                        view.PreviewBlockDrag(marker, start, end - start);
                        break;
                    }
                }
            }

            public void OnEndDrag(PointerEventData eventData)
            {
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
            internal int EventRowStart;
            internal int EventRowCount;
            internal int ShownRows;

            // 本分类框的行在 layoutRows 里的起始下标。
            internal int EventRowBandStart;

            // 本分类框自己的滚动位置，单位是行。每个分类框是一个固定
            // 高度的窗口，一次只显示几行；在其中一个里滚动只移动
            // 该分类框的内容，所以要看更靠后的分类框，不必先滚过
            // 前面每个分类框的所有行。
            internal float RowScrollOffset;

            // 本分类框窗口在内容空间里的 Y，由 BuildRowTable 填入。
            internal float WindowTop;
            internal float WindowBottom;
            internal float EventRowsTop;

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
            internal TimelineSection Section;
        }

        private sealed class CollapsedBadge
        {
            internal RectTransform Rect;
            internal Image Arrow;
            internal TextMeshProUGUI Label;
        }

        private const int LaneCount = 6;
        private const int MarkerPoolSize = 320;
        private const int MajorTickPoolSize = 32;
        private const int FloorLinePoolSize = 160;
        private const int RowBackgroundPoolSize = 32;
        private const int SectionHeaderPoolSize = 96;
        private const int CollapsedBadgePoolSize = 320;
        // 起始时间相差在这个范围内的两个事件，算作同一列（用于提示标记）。
        private const float ColumnGroupSeconds = 0.02f;
        private static readonly Color ChevronColor = new Color(0.96f, 0.96f, 0.96f, 0.80f);
        private readonly List<float> hintTimes = new List<float>();
        private readonly List<int> hintCounts = new List<int>();
        // 工具栏占两行，给后续要加的小按钮留位置。
        // 第一行放时间轴自己的控件（筛选、缩放、播放模式…），
        // 第二行预留给后续功能。主体从两行之下开始。
        private const float ToolRowHeight = 34f;
        private const float ToolRowGap = 2f;
        private static readonly float ToolRow2Y = ToolRowHeight + ToolRowGap;
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
        /// </summary>
        private const float ContentShift = DecoPaneWidth + DecoPaneGap;
        /// <summary>
        /// 竖条顶端离面板顶的距离。
        ///
        /// 它**不**从 headerHeight 起——headerHeight 是事件按钮占了几行之后
        /// 才定下来的，跟着它走的话，竖条顶端会随调色板行数上下跳，
        /// 而且一开始就比「时间轴」标题低一大截。竖条是整块面板的一个
        /// 兄弟区域，顶端贴面板顶、底端铺到面板下沿，跟调色板占几行无关。
        /// </summary>
        private const float DecoPaneTop = 8f;
        private const float RulerHeight = 28f;
        // 播放头：青色发光，由三层叠出来（外辉光 / 内辉光 / 实心芯线）。
        private const float PlayheadCoreWidth = 2f;
        private const float PlayheadGlowInnerWidth = 6f;
        // 辉光只比芯线宽一点点。铺太开会把播放头读成一条粗带子，
        // 而不是「一条细线带着一层薄光」。
        private const float PlayheadGlowWidth = 8f;
        private const float PlayheadHandleWidth = 11f;
        private static readonly Color PlayheadColor = new Color(0.310f, 0.839f, 0.788f, 1f);
        private static readonly Color PlayheadGlowInnerColor = new Color(0.310f, 0.839f, 0.788f, 0.35f);
        private static readonly Color PlayheadGlowOuterColor = new Color(0.310f, 0.839f, 0.788f, 0.14f);
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
        private static readonly float SectionWindowRows = SectionVisibleRows + SectionSliverRows;
        private const float BadgeWidth = 68f;
        private const float BadgeHeight = 7f;
        // 默认缩放下可见跨度是十二秒，让分类框打开时显示的是一段
        // 事件而不是整张谱面——时间轴是用来处理某一段的，
        // 全局概览交给标尺和滚动条。缩放读数的基准就是
        // 这个值，所以默认视图显示 100%。
        private const float DefaultPixelsPerSecond = 192f;
        // 默认值的一半：分类框能拉到的最远缩放，对应默认视图的 2 倍跨度。
        private const float MinimumPixelsPerSecond = 96f;
        private const float MaximumPixelsPerSecond = 640f;
        private const float DataRefreshInterval = 0.5f;
        private const float SelectionRefreshInterval = 0.12f;
        private const float EventGap = 3f;
        private const float MinimumPointEventWidth = 64f;
        private const float MarkerIconLeft = 6f;
        private const float MarkerIconGap = 5f;
        // 起点圆头。尺寸取自行高，这样时间轴缩放时它会跟着变；再内缩一部分，
        // 让它看着像坐在块内部，而不是挂在块左边缘外面。
        private const float MarkerCapSize = 9f;
        private const float MarkerCapLeft = 3.5f;
        private const float ResizeHandleHeight = 9f;

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
        private static readonly Color TimelineCard = new Color(0.140f, 0.140f, 0.140f, 0.90f);
        // 标尺条铺在卡片上，颜色刻意更深，好让时间标签
        // 有自己的底带。
        private static readonly Color RulerStrip = new Color(0.145f, 0.145f, 0.145f, 0.95f);
        // 分类框窗口的填充色：不透明，且与卡片同值，让各窗口读成
        // 一整块表面，而不是一排瓷砖。
        private static readonly Color WindowFill = new Color(0.140f, 0.140f, 0.140f, 1f);
        // 标尺线和格线是参考线，不是内容：淡到能待在事件
        // 块下面，又不至于和它们抢注意力。
        // 标尺刻度和格线。从 0.16 的 alpha 提上来，那个值在面板上直接消失了，
        // 但仍远低于面板边框，让时间轴仍读成内容而不是
        // 又一层边框。
        private static readonly Color GridLineColor = new Color(0.78f, 0.78f, 0.78f, 0.28f);
        private static readonly Color TextPrimary = Palette.TextPrimary;
        private static readonly Color TextSecondary = Palette.TextSecondary;
        private static readonly Color TextMuted = Palette.TextMuted;
        // 每条泳道一个色相，颜色一眼就能告诉你事件属于哪个家族。
        private static readonly float[] LaneBaseHues = { 0.53f, 0.72f, 0.42f, 0.94f, 0.10f, 0.58f };
        // 同一泳道里每个事件按自身 id 在色相上错开一点的幅度。
        private const float BlockHueJitter = 0.10f;
        // 事件块的亮度（相对亮度，不是 HSV 的 V）。
        //
        // 0.45 落在「不深不浅」那一段：对分类框底色约 7:1，六个家族彼此
        // 又在同一条亮度线上，所以谁也不会比谁更抢眼，但每个色相都还
        // 认得出来。低了整片读成灰，高了就刺眼。
        private const float BlockBrightness = 0.45f;
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

        private EditorStudioOverlay owner;
        private RectTransform root;
        private RectTransform viewport;
        /// <summary>
        /// 一个分类框的窗口：不透明的盒子，裁掉自己溢出的行。
        ///
        /// 盒子让分类框可滚动而不是无限铺开。一个分类框持有的行
        /// 远多于窗口显示的行数，不裁剪的话多出来的行会直接画到
        /// 下面的分类框上。遮罩负责裁剪，背景让盒子实心，这样两个
        /// 叠在一起的分类框不会互相透出来。
        /// </summary>
        private sealed class SectionWindow
        {
            internal RectTransform Rect;
            internal RectTransform Content;
            internal Image Background;
        }

        private const int SectionWindowPoolSize = 32;
        private readonly List<SectionWindow> sectionWindowPool = new List<SectionWindow>();
        private int sectionWindowIndex;

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
        private TMP_FontAsset font;
        private Sprite roundedSprite;
        private EditorDecorationList decoPane;
        private Texture2D circleTexture;
        private Sprite circleSprite;
        private Texture2D roundedTexture;
        // 选中事件用的空心环形 sprite。做成九宫格切片，拉伸时四角保持
        // 圆角、四边不糊——和 roundedSprite 一样的做法，只是中间挖空。
        private Sprite outlineSprite;
        private Texture2D outlineTexture;
        private Button viewLockButton;
        private Button layoutModeButton;
        private Button followModeButton;
        private Button selectionFollowButton;
        private Button snapDragButton;
        private Sprite layoutModeSprite;
        private Sprite followModeSprite;
        private Sprite selectionFollowSprite;
        private Sprite snapDragSprite;
        private Sprite viewLockSprite;
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
        /// 播放时视图怎么跟随。
        /// </summary>
        private enum TimelineFollowMode
        {
            /// <summary>播放头自由走，视图只在它快出界时才推一格。</summary>
            FreePlayhead,
            /// <summary>播放头钉在时间轴正中，内容从右往左滚。</summary>
            CenteredPlayhead
        }

        private TimelineLayoutMode layoutMode = TimelineLayoutMode.SectionRoll;
        private TimelineFollowMode followMode = TimelineFollowMode.FreePlayhead;

        internal bool IsSectionRollLayout
        {
            get { return layoutMode == TimelineLayoutMode.SectionRoll; }
        }

        internal bool IsCenteredPlayhead
        {
            get { return followMode == TimelineFollowMode.CenteredPlayhead; }
        }

        /// <summary>切换排列方式。布局立刻重算。</summary>
        internal void ToggleLayoutMode()
        {
            layoutMode = layoutMode == TimelineLayoutMode.SectionRoll
                ? TimelineLayoutMode.LayerStack
                : TimelineLayoutMode.SectionRoll;
            verticalScrollOffset = 0f;
            layoutDirty = true;
            rowTableDirty = true;
            visualsDirty = true;
        }

        /// <summary>切换播放时的跟随方式。</summary>
        internal void ToggleFollowMode()
        {
            followMode = followMode == TimelineFollowMode.FreePlayhead
                ? TimelineFollowMode.CenteredPlayhead
                : TimelineFollowMode.FreePlayhead;
            // 切到居中时立刻把画面挪过去，否则要等下一帧播放才动。
            centerViewOnNextFollow = true;
            manualNavigationUntil = 0f;
            visualsDirty = true;
        }

        /// <summary>居中播放模式下，内容相对播放头的偏移。</summary>
        private const float CenteredPlayheadFraction = 0.5f;

        /// <summary>切到居中的那一帧要把视图挪过去，之后靠 FollowPlayback 维持。</summary>
        private bool centerViewOnNextFollow;
        private readonly List<Button> laneFilterButtons = new List<Button>();
        private readonly List<Image> laneFilterIcons = new List<Image>();
        private bool laneFilterStateInitialized;
        private LevelEvent selectedEvent;
        private float bodyWidth;
        private float bodyHeight;
        private float pixelsPerSecond = DefaultPixelsPerSecond;
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
        private int displayRowCount;
        private float layoutContentHeight;
        private float layoutTotalHeight;
        private bool dataFingerprintValid;
        private bool viewInitialized;
        private bool visualsDirty = true;
        private bool layoutDirty = true;
        // 只滚动分类框时用它，避免为一格滚动重建整个布局。
        // 布局本身没变，变的只是各行在窗口里的纵向偏移。
        private bool rowTableDirty;
        private bool middleDragging;
        // 居中浏览：中键横拖时，记录拖动开始时播放头所在的时间。
        // 拖动量按时间累加到它上面，于是「线不动、时间在扫」。
        private float centerBrowseStartTime;
        private float centerBrowseAccum;
        // 居中浏览自己开了一次 scrub，结束时由它负责关掉。
        private bool centerBrowseScrubbing;
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
            circleSprite = CreateCircleSprite();
            outlineSprite = CreateOutlineSprite();
            BuildUi();
            viewInitialized = true;
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
            if (circleTexture != null)
            {
                Destroy(circleTexture);
                circleTexture = null;
            }
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
            if (decoPane != null)
            {
                decoPane.SetPaneRect(GutterWidth, DecoPaneTop, DecoPaneWidth,
                    Mathf.Max(RowPaneMinimumHeight, rootHeight - DecoPaneTop - 8f));
            }

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
            RelayoutIfResized();
            HandleKeyboardShortcuts();
            TickEdgeAutoScroll();
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

            // 切到「固定中间」的那一帧要立刻对齐，即使当前没在播放——
            // 否则用户点了按钮画面纹丝不动，看不出这个模式有什么不同。
            if (centerViewOnNextFollow)
            {
                centerViewOnNextFollow = false;
                FollowPlayback(snapshot.CurrentTime);
            }
            else if (snapshot.State == EditorPlaybackState.Playing
                && now >= manualNavigationUntil)
            {
                FollowPlayback(snapshot.CurrentTime);
            }
            // 居中模式下每帧把视图摆回中点，且不设任何例外。
            //
            // 这是「固定中间」的字面含义：那根竖线永远在正中不动，
            // 是内容在它下面滑过去。所以不能让拖动、缩放锚点或任何
            // 别的路径把视图挪走——只要挪了，竖线就离开中心，模式就
            // 看起来失效了。
            //
            // 纵向滚动不受影响：那是看更多行，与水平居中无关。
            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                SyncCenteredView(snapshot.CurrentTime);
            }

            if (visualsDirty)
            {
                RefreshGeometry();
            }

            UpdatePlayhead(snapshot);
        }

        /// <summary>
        /// 指针落在哪个分类框的盒子上，没有则返回 null。用盒子当前的屏幕
        /// rect 判断，而不是去查行表，因为用户指的正是那个盒子
        /// 本身。
        /// </summary>
        private bool FindSectionWindowAt(Vector2 screenPosition, out TimelineSection section)
        {
            section = null;
            for (int i = 0; i < sectionWindowIndex && i < sectionWindowPool.Count; i++)
            {
                SectionWindow window = sectionWindowPool[i];
                if (window.Rect == null || !window.Rect.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (RectTransformUtility.RectangleContainsScreenPoint(window.Rect, screenPosition, null)
                    && i < layoutSections.Count)
                {
                    section = layoutSections[i];
                    return true;
                }
            }
            return false;
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!ContainsScreenPoint(eventData.position) || Mathf.Approximately(eventData.scrollDelta.y, 0f))
            {
                return;
            }

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (control)
            {
                float anchorX = GetPointerAnchorX(eventData.position);
                ZoomAt(eventData.scrollDelta.y > 0f ? 1.25f : 0.8f, anchorX);
            }
            else if (alt)
            {
                AdjustRowHeight(eventData.scrollDelta.y > 0f ? 1f : -1f);
            }
            else if (FindSectionWindowAt(eventData.position, out TimelineSection hovered) && !shift)
            {
                // 在分类框盒子里滚动，移动的是该分类框自己的行，所以一个装着
                // 几百个事件的分类框不用动时间轴就能翻看。
                //
                // 先于下面的横向回退判断，因为当每个分类框都被压成
                // 固定高度的窗口后，时间轴常常根本没有纵向溢出——
                // 而那个分支会把「没有纵向可滚」当成「在时间上滚动」，
                // 于是滚轮还没走到这里就被它吃掉了。
                if (!hovered.Collapsed && hovered.EventRowCount > SectionWindowRows)
                {
                    float step = rowHeight * 1.7f * eventData.scrollDelta.y;
                    float maxOffset = (hovered.EventRowCount - SectionWindowRows) * rowHeight;
                    hovered.RowScrollOffset = Mathf.Clamp(hovered.RowScrollOffset - step, 0f, maxOffset);
                    // 只有行表要重算，类型分组、行装箱和排序都不受影响。
                    rowTableDirty = true;
                }
            }
            else if (shift || GetMaximumVerticalScroll() <= 0.01f)
            {
                viewStartTime -= eventData.scrollDelta.y * 150f / Mathf.Max(1f, pixelsPerSecond);
                ClampViewStart();
                MarkManualNavigation();
            }
            else
            {
                verticalScrollOffset -= eventData.scrollDelta.y * rowHeight * 1.7f;
                ClampVerticalScroll();
            }

            visualsDirty = true;
            eventData.Use();
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

            EditorPlaybackSnapshot snapshot = EditorPlaybackBridge.ReadSnapshot();
            float playheadX = (snapshot.CurrentTime - viewStartTime) * pixelsPerSecond;
            bool rulerHit = y >= 0f && y <= RulerHeight;
            bool playheadHit = playheadX >= 0f && playheadX <= bodyWidth && Mathf.Abs(x - playheadX) <= 7f;
            if (!rulerHit && !playheadHit)
            {
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
            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                // 居中浏览：先把当前播放头时间记下来，之后按拖动量累加。
                //
                // 必须真的进一次 scrub 状态：TryScrubTimelineToTime 开头就是
                // `if (!timelineScrubActive) return false;`，不进状态的话每次
                // 提交都会被静默拒绝，表现就是「拖不动」。
                string browseStatus;
                if (EditorPlaybackBridge.TryBeginTimelineScrub(out browseStatus))
                {
                    centerBrowseScrubbing = true;
                    centerBrowseStartTime = EditorPlaybackBridge.ReadSnapshot().CurrentTime;
                    centerBrowseAccum = 0f;
                }
            }

            middleDragging = eventData.button == PointerEventData.InputButton.Middle && ContainsScreenPoint(eventData.position);
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                anyPointerCapture = true;
                MarkManualNavigation();
                eventData.Use();
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
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

            // ── 临时诊断：中键拖动到底有没有真的平移 ──

            if (followMode == TimelineFollowMode.CenteredPlayhead)
            {
                // 居中浏览：线钉在屏幕上，拖动改变的是它指向的时间。
                // 往右拖 = 内容右移 = 中线指向更早的时间，也就是「抓住内容拖」
                // 的手感，跟剪辑软件里拖时间轴一致。
                //
                // 这里复用 scrubbing 的提交路径而不是直接改 viewStartTime：
                // 画面要跟着线走，必须走求值器的定位，否则只有时间轴在动、
                // 预览纹丝不动。
                centerBrowseAccum -= eventData.delta.x / Mathf.Max(1f, pixelsPerSecond);
                float browseTime = Mathf.Clamp(
                    centerBrowseStartTime + centerBrowseAccum,
                    0f,
                    Mathf.Max(0f, navigationDuration));
                pendingScrubTime = browseTime;
                scrubPending = true;
                manualNavigationUntil = 0f;
                nextScrubSubmitTime = 0f;
            }
            else
            {
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
                // 居中的 scrub 由这里收尾：提交最后一次定位并退出状态，
                // 否则 timelineScrubActive 会一直挂着，后续定位全被拒。
                centerBrowseScrubbing = false;
                SubmitPendingScrub(true);
                EditorPlaybackBridge.EndTimelineScrub();
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
            bool plus = Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
            bool minus = Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
            bool zero = Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0);

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
                float scrollRatio = layoutTotalHeight <= 0f
                    ? 0f
                    : Mathf.Clamp01(verticalScrollOffset / layoutTotalHeight);
                rowHeight = requested;
                changed = true;
                layoutDirty = true;
                visualsDirty = true;

                // 让光标下始终是谱面的同一处，而不是保持同一个
                // 行号——行号已经不再对应固定位置了。
                verticalScrollOffset = scrollRatio * layoutTotalHeight;
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
        /// 只在「自由播放头」模式下生效。居中模式下视图是播放头的从属量，
        /// 再叠一层自动滚动会互相打架。
        /// </summary>
        private void TickEdgeAutoScroll()
        {
            if (!scrubbing || !scrubPointerValid
                || followMode != TimelineFollowMode.FreePlayhead)
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

        private void BuildUi()
        {
            float rootWidth = Mathf.Max(800f, root.rect.width);
            float rootHeight = Mathf.Max(220f, root.rect.height);
            bodyWidth = rootWidth - (GutterWidth + ContentShift) - 10f;
            bodyHeight = rootHeight - headerHeight - 8f;
            lastRootHeight = rootHeight;

            // 标题和它右边那排模式按钮整体右移，给左侧的装饰物列表让位。
            CreateText("Heading", root, 18f + ContentShift, 7f, 78f, 30f, "时间轴", 17f, TextPrimary, TextAlignmentOptions.MidlineLeft, true);

            // 车道筛选按钮原来在这里，是一排文字按钮（全部/镜头/视效/…）。
            // 现在改成原生图标、无文字、多选，挂在预览面板的工具栏上——
            // 也就是预览画面上方那条，离它们控制的东西更近。

            // 排列方式与播放方式的切换按钮，放第一行左侧。
            // 图标自绘：字体里没有可靠的表意字形，缺字会渲染成方块。
            layoutModeButton = CreateButton(
                "LayoutMode", root, 100f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { ToggleLayoutMode(); ApplyLayoutModeIcon(); });
            ApplyLayoutModeIcon();

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

            // 拖动事件块时是否吸附到格边界。放在一排开关的最后，
            // 和上面三个一样是"模式开关"的性质。
            snapDragButton = CreateButton(
                "SnapDrag", root, 208f + ContentShift, 7f, 32f, 29f, string.Empty,
                delegate { ToggleSnapDrag(); });
            ApplySnapDragIcon();

            BuildEventPalette(root);

            float controlsX = rootWidth - 76f;
            zoomText = CreateText("Zoom", root, controlsX - 58f, 8f, 52f, 28f, "100%", 11f, TextMuted, TextAlignmentOptions.Center, false);
            CreateButton("ZoomOut", root, controlsX, 7f, 28f, 29f, "-", delegate { ZoomAt(0.8f, bodyWidth * 0.5f); });
            CreateButton("ZoomIn", root, controlsX + 32f, 7f, 28f, 29f, "+", delegate { ZoomAt(1.25f, bodyWidth * 0.5f); });
            Button rewindButton = CreateButton("GoToStart", root, controlsX - 96f, 7f, 32f, 29f, "|<", ReturnToStart);
            ApplyNativeRewindIcon(rewindButton);

            // 视图锁：一个按钮同时管相机位置和缩放。
            //
            // 就放在筛选按钮之后，摘要列从它之后开始。这排条的
            // 右端已经排满了——时间读数右对齐，
            // 最后几位数字一直顶到播放控件——所以早先尝试把锁放在
            // 那里，结果被「00:00.000」盖住了。
            viewLockButton = CreateButton(
                "ViewLock",
                root,
                550f + ContentShift,
                7f,
                32f,
                29f,
                string.Empty,
                delegate { ToggleViewLock(); }
            );
            ApplyViewLockIcon();

            // 这个设置会持久化，所以启动时必须告知求值器——
            // 否则会出现一开始实际未锁、按钮却已显示为锁定状态的情况。
            if (Main.Settings != null)
            {
                EditorPlaybackBridge.ApplyPreviewPositionLock(Main.Settings.LockPreviewPosition);
            }

            timeText = CreateText("Time", root, rootWidth - 420f, 8f, 240f, 28f, "00:00.000 / 00:00.000", 12f, TextSecondary, TextAlignmentOptions.MidlineRight, false);
            // 宽度按「从起点到时间读数左缘」算，不能写死。
            // 时间读数右对齐占 rootWidth-420 起的一段，摘要右移之后
            // 如果还按老的定宽铺开，会直接压在读数上。
            float summaryLeft = 590f + ContentShift;
            summaryText = CreateText("Summary", root, summaryLeft, 8f,
                Mathf.Max(100f, rootWidth - 420f - summaryLeft - 12f), 28f, string.Empty, 11f,
                TextMuted, TextAlignmentOptions.MidlineLeft, false);
            summaryText.overflowMode = TextOverflowModes.Ellipsis;

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

            for (int i = 0; i < RowBackgroundPoolSize; i++)
            {
                Image row = CreateImage(
                    "RowBackground" + i,
                    viewport,
                    0f,
                    RulerHeight,
                    bodyWidth,
                    rowHeight,
                    new Color(1f, 1f, 1f, 0.030f)
                );
                row.raycastTarget = false;
                row.gameObject.SetActive(false);
                rowBackgrounds.Add(row.rectTransform);
            }

            for (int i = 0; i < SectionWindowPoolSize; i++)
            {
                CreateSectionWindow(i);
            }

            markerLayer = CreateRect("MarkerLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            for (int i = 0; i < FloorLinePoolSize; i++)
            {
                Image line = CreateImage("FloorLine" + i, viewport, 0f, RulerHeight, 1f, bodyHeight - RulerHeight, GridLineColor);
                line.raycastTarget = false;
                line.gameObject.SetActive(false);
                floorLines.Add(line);
            }

            // 现在时间网格上只剩标尺标签：每秒一条的竖线
            // 被去掉了，改用每格一条的线，因为那才是真正
            // 标出事件能落在哪儿的东西。
            for (int i = 0; i < MajorTickPoolSize; i++)
            {
                TextMeshProUGUI label = CreateText("MajorLabel" + i, viewport, 0f, 0f, 72f, RulerHeight, string.Empty, 9.5f, TextMuted, TextAlignmentOptions.MidlineLeft, false);
                label.gameObject.SetActive(false);
                majorTickLabels.Add(label);
                majorTickLabelText.Add(string.Empty);
            }

            for (int i = 0; i < MarkerPoolSize; i++)
            {
                CreateMarker(i);
            }

            // 这些层必须在填充各自的池之前就存在，否则
            // 表头会被挂到场景根节点而不是画布下，导致
            // 完全渲染不出来。
            sectionHeaderLayer = CreateRect("SectionHeaderLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);
            badgeLayer = CreateRect("CollapsedBadgeLayer", viewport, 0f, 0f, bodyWidth, bodyHeight);

            // 分类框表头和「+N」标签位于事件块之上，这样折叠
            // 起来的分类框标签不会被它自己最后一块盖住。
            for (int i = 0; i < SectionHeaderPoolSize; i++)
            {
                CreateSectionHeader(i);
            }
            for (int i = 0; i < CollapsedBadgePoolSize; i++)
            {
                CreateCollapsedBadge(i);
            }

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
                new Color(0.31f, 0.34f, 0.41f, 0.50f)
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
                new Color(0.46f, 0.50f, 0.59f, 0.72f)
            ).raycastTarget = false;
            TimelineResizeHandle resizeHandle = resizeHandleImage.gameObject.AddComponent<TimelineResizeHandle>();
            resizeHandle.Initialize(owner, root);
            resizeHandleImage.transform.SetAsLastSibling();

            BuildDecorationPane();

            UpdateFilterVisuals();
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
        private void BuildDecorationPane()
        {
            float paneHeight = Mathf.Max(RowPaneMinimumHeight, root.rect.height - DecoPaneTop - 8f);
            Image background = CreateImage("DecoPane", root, GutterWidth, DecoPaneTop,
                DecoPaneWidth, paneHeight, TimelineCard);
            background.raycastTarget = true;
            if (roundedSprite != null)
            {
                background.sprite = roundedSprite;
                background.type = Image.Type.Sliced;
            }

            decoPane = background.gameObject.AddComponent<EditorDecorationList>();
            decoPane.Initialize(background.rectTransform, font, roundedSprite, DecoPaneWidth);
            decoPane.SetPaneRect(GutterWidth, DecoPaneTop, DecoPaneWidth, paneHeight);
        }

        /// <summary>竖条的最小高度。再矮就没法放搜索框和一行列表了。</summary>
        private const float RowPaneMinimumHeight = 140f;

        /// <summary>
        /// 构建可点击的分类框表头：图标、事件名，以及该框持有的
        /// 行数。点击它在展开与折叠之间切换。
        /// </summary>
        private void CreateSectionHeader(int index)
        {
            Image background = CreateRoundedImage(
                "SectionHeader" + index,
                sectionHeaderLayer,
                0f,
                0f,
                bodyWidth,
                SectionHeaderHeight,
                new Color(0.105f, 0.105f, 0.105f, 0.96f)
            );
            background.raycastTarget = true;
            Button button = background.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            Image icon = CreateImage(
                "Icon",
                background.rectTransform,
                MarkerIconLeft,
                0f,
                SectionHeaderHeight - 8f,
                SectionHeaderHeight - 8f,
                Color.white
            );
            icon.raycastTarget = false;
            icon.preserveAspect = true;

            TextMeshProUGUI label = CreateText(
                "Label",
                background.rectTransform,
                MarkerIconLeft + SectionHeaderHeight,
                0f,
                Mathf.Max(1f, bodyWidth - SectionHeaderHeight - 80f),
                SectionHeaderHeight,
                string.Empty,
                11.5f,
                TextSecondary,
                TextAlignmentOptions.MidlineLeft,
                false
            );
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;

            var view = new SectionHeaderView
            {
                GameObject = background.gameObject,
                Rect = background.rectTransform,
                Background = background,
                Icon = icon,
                Label = label,
                Button = button
            };
            button.onClick.AddListener(delegate { ToggleSection(view.Section); });
            background.gameObject.SetActive(false);
            sectionHeaderPool.Add(view);
        }

        private void CreateCollapsedBadge(int index)
        {
            // 一个无图形的根节点，箭头和计数是它的子节点。让根节点自己
            // 不带图形，正是为了避免两套布局打架：把箭头挂在
            // 根节点自己的 rect 上，会导致箭头的定位覆盖提示标记的定位，
            // 结果每个提示都同时跑到左边缘和窗口顶部。
            RectTransform root = CreateRect("CollapsedBadge" + index, badgeLayer, 0f, 0f, BadgeWidth, BadgeHeight);

            // 画出来的 V 形，不是字形。工作台字体没有箭头——「⌄」会渲染成
            // 豆腐块——而烘进纹理的形状不依赖字体，所以它
            // 不会在字体覆盖不同的机器上退化。
            Image arrow = CreateImage("Arrow", root, 0f, 0f, BadgeWidth, BadgeHeight, ChevronColor);
            arrow.raycastTarget = false;
            arrow.type = Image.Type.Simple;
            arrow.preserveAspect = false;

            TextMeshProUGUI label = CreateText(
                "Count",
                root,
                0f,
                0f,
                BadgeWidth,
                BadgeHeight,
                string.Empty,
                24f,
                ChevronColor,
                TextAlignmentOptions.Bottom,
                false
            );

            collapsedBadgePool.Add(new CollapsedBadge
            {
                Rect = root,
                Arrow = arrow,
                Label = label
            });
            root.gameObject.SetActive(false);
        }

        /// <summary>
        /// 把分类框打开或合上。折叠集合单独保存，因为
        /// 分类在每趟布局都会重建。
        /// </summary>
        private void ToggleSection(TimelineSection section)
        {
            if (section == null)
            {
                return;
            }

            section.Collapsed = !section.Collapsed;
            layoutDirty = true;
            visualsDirty = true;
        }

        private void CreateMarker(int index)
        {
            GameObject markerObject = new GameObject("EventMarker" + index, typeof(RectTransform), typeof(Image), typeof(Button));
            markerObject.transform.SetParent(markerLayer, false);
            RectTransform rect = markerObject.GetComponent<RectTransform>();
            SetTopLeft(rect, 0f, 0f, MinimumPointEventWidth, rowHeight - RowVerticalPadding * 2f);
            Image background = markerObject.GetComponent<Image>();
            background.sprite = roundedSprite;
            background.type = Image.Type.Sliced;
            background.color = PanelAlt;
            Button button = markerObject.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            // 事件自带的编辑器图标，取自编辑器打开时游戏从
            // Resources 加载的 sprite 表。尺寸取自行高，这样
            // 会跟随时间轴缩放。
            float iconSize = GetMarkerIconSize(rowHeight);
            GameObject iconObject = new GameObject("Icon", typeof(RectTransform));
            iconObject.transform.SetParent(rect, false);
            Image icon = iconObject.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            SetTopLeft(icon.rectTransform, MarkerIconLeft, 0f, iconSize, rowHeight - RowVerticalPadding * 2f);

            TextMeshProUGUI label = CreateText(
                "Label",
                rect,
                MarkerIconLeft,
                0f,
                Mathf.Max(1f, MinimumPointEventWidth - MarkerIconLeft * 2f),
                rowHeight - RowVerticalPadding * 2f,
                string.Empty,
                11f,
                TextPrimary,
                TextAlignmentOptions.MidlineLeft,
                false
            );
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;

            // 缓动曲线叠加层。曲线由视图自己栅格化成 mesh，
            // 而不是从 sprite 印上去：纹理必须
            // 拉伸到相差约 30 倍的块宽上，这种情况下
            // 只可能在横向或纵向之一保持锐利，不可能
            // 两者兼得。这个 Image 铺满整个块，改为把几何体内缩，
            // 于是任何缩放级别下笔画宽度都保持不变。
            GameObject curveObject = new GameObject("Curve", typeof(RectTransform));
            curveObject.transform.SetParent(rect, false);
            EaseCurveImage curve = curveObject.AddComponent<EaseCurveImage>();
            curve.raycastTarget = false;
            curve.color = Color.white;

            GameObject capObject = new GameObject("Cap", typeof(RectTransform));
            capObject.transform.SetParent(rect, false);
            Image cap = capObject.AddComponent<Image>();
            cap.raycastTarget = false;
            cap.sprite = circleSprite;
            float capRowHeight = rowHeight - RowVerticalPadding * 2f;
            SetTopLeft(cap.rectTransform, MarkerCapLeft,
                (capRowHeight - MarkerCapSize) * 0.5f, MarkerCapSize, MarkerCapSize);

            // 白色描边层，铺满整个块。九宫格拉伸，所以块宽从十几像素
            // 到几百像素，边框粗细都一样。常驻但默认隐藏，只有精确
            // 选中的块才点亮。
            GameObject outlineObject = new GameObject("Outline", typeof(RectTransform));
            outlineObject.transform.SetParent(rect, false);
            Image outline = outlineObject.AddComponent<Image>();
            outline.raycastTarget = false;
            outline.sprite = outlineSprite;
            outline.type = Image.Type.Sliced;
            outline.color = Color.white;
            outline.gameObject.SetActive(false);
            // 挪到最底层。UI 按子物体顺序绘制，而描边是铺满整块的
            // 一个环——留在最后会盖住图标和文字。
            outline.transform.SetAsFirstSibling();

            var marker = new TimelineMarker
            {
                GameObject = markerObject,
                Rect = rect,
                Background = background,
                Cap = cap,
                Curve = curve,
                Icon = icon,
                Label = label,
                Outline = outline
            };
            button.onClick.AddListener(delegate { ActivateMarker(marker); });

            // 拖动交互挂在块自己身上。它不实现 IPointerDownHandler，
            // 所以不会和上面这个 Button 抢事件，也不会挡住父级视图——
            // 细节见 TimelineBlockDrag 的注释。
            marker.Drag = markerObject.AddComponent<TimelineBlockDrag>();
            marker.Drag.Initialize(this, marker);

            markerObject.SetActive(false);
            markerPool.Add(marker);
        }

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
            displayRowCount = 0;
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
                LabelColor = GetEventLabelColor(levelEvent.eventType, lane)
            };
            laneItems[(int)lane].Add(item);
            totalDuration = Mathf.Max(totalDuration, startTime + duration);
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
            displayRowCount = 0;
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

            BuildRowTable();
            layoutItems.Sort(CompareTimelineItems);

            layoutDirty = false;
            ClampVerticalScroll();
            UpdateSummary();
        }

        /// <summary>
        /// Distinct event types in first-appearance order. The key is an int rather
        /// than LevelEventType so decoration events, which carry the decoration type
        /// instead of an event type, are grouped by what they actually are.
        /// </summary>


        /// <summary>
        /// Distinct event types in first-appearance order. The key is an int rather
        /// than LevelEventType so decoration events, which carry the decoration type
        /// instead of an event type, are grouped by what they actually are.
        /// </summary>
        private List<int> CollectOrderedEventTypes()
        {
            var seen = new HashSet<int>();
            var ordered = new List<int>();
            for (int i = 0; i < layoutItems.Count; i++)
            {
                int key = GetLayoutTypeKey(layoutItems[i]);
                if (seen.Add(key))
                {
                    ordered.Add(key);
                }
            }
            return ordered;
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
        /// Lays the bands out vertically: a header row, its visible event rows, then a
        /// gap before the next band. Every row keeps an entry even when its band is
        /// collapsed, so row indices stay stable and a stale DisplayRow cannot resolve
        /// to a different band's geometry.
        /// </summary>


        /// <summary>
        /// Lays the bands out vertically: a header row, its visible event rows, then a
        /// gap before the next band. Every row keeps an entry even when its band is
        /// collapsed, so row indices stay stable and a stale DisplayRow cannot resolve
        /// to a different band's geometry.
        /// </summary>
        private void BuildRowTable()
        {
            layoutRowSection.Clear();
            layoutRowY.Clear();
            layoutContentHeight = 0f;
            float y = 0f;
            for (int i = 0; i < layoutSections.Count; i++)
            {
                TimelineSection section = layoutSections[i];
                if (i > 0)
                {
                    y += SectionGap;
                }

                section.WindowTop = y;

                // Header row: its own entry, at the top of the window.
                section.HeaderRow = layoutRowSection.Count;
                layoutRowSection.Add(i);
                layoutRowY.Add(y);
                y += SectionHeaderHeight;

                section.EventRowsTop = y;
                section.EventRowStart = layoutRowSection.Count;

                if (layoutMode == TimelineLayoutMode.SectionRoll)
                {
                    // Every row the band owns gets an entry, but the Y stored is where it
                    // sits *inside the window* -- the window does not grow to fit them.
                    // Rows below the window keep stacking past its foot and are covered by
                    // the next band, which is what makes a band a box that scrolls rather
                    // than a list that sprawls.
                    for (int row = 0; row < section.EventRowCount; row++)
                    {
                        layoutRowSection.Add(i);
                        layoutRowY.Add(y + row * rowHeight - section.RowScrollOffset);
                    }

                    // The window is this tall whatever the band holds, so toggling one
                    // band never shifts the ones under it.
                    y = section.EventRowsTop + SectionWindowRows * rowHeight;
                }
                else
                {
                    // 图层模式：每个事件类型一条平铺的带，纵向全部展开，
                    // 既不裁剪也不分带滚动。
                    //
                    // 某条带事件多，就把下面的带往下推。收窄靠预览工具栏
                    // 上那排车道图标，只画启用中的类型。
                    for (int row = 0; row < section.EventRowCount; row++)
                    {
                        layoutRowSection.Add(i);
                        layoutRowY.Add(y + row * rowHeight);
                    }

                    y = section.EventRowsTop + Mathf.Max(1, section.EventRowCount) * rowHeight;
                }

                section.WindowBottom = y;
            }

            layoutContentHeight = y;
            layoutTotalHeight = layoutContentHeight;
            displayRowCount = layoutRowSection.Count;
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


        /// <summary>
        /// The band a row belongs to, by the full row table (headers included). Only
        /// meaningful for rows that belong to a band; a row a collapsed band hides
        /// still resolves, which keeps a stale DisplayRow harmless.
        /// </summary>
        private TimelineSection GetSectionForRow(int row)
        {
            if (row < 0 || row >= layoutRowSection.Count)
            {
                return null;
            }

            int index = layoutRowSection[row];
            return index >= 0 && index < layoutSections.Count ? layoutSections[index] : null;
        }


        private int GetRowIndexAtY(float contentY)
        {
            int low = 0;
            int high = layoutRowY.Count - 1;
            int found = -1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (layoutRowY[middle] <= contentY)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return found;
        }


        private float GetRowTop(int row)
        {
            if (row <= 0)
            {
                return 0f;
            }
            return row < layoutRowY.Count ? layoutRowY[row] : layoutTotalHeight;
        }


        private float GetRowBottom(int row)
        {
            return row < layoutRowY.Count ? layoutRowY[row] + rowHeight : layoutTotalHeight;
        }


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
            RefreshGrid();
            RefreshRowBackgrounds();
            RefreshSectionHeaders();
            float viewEndTime = viewStartTime + bodyWidth / pixelsPerSecond;
            int markerIndex = 0;
            float viewBottom = verticalScrollOffset + bodyHeight - RulerHeight;

            // Driven band by band rather than by a global row number. layoutRows is
            // indexed per event row while the row table also carries a header row per
            // band, so a single row index cannot address both: doing that shifted every
            // band after the first by one row and let blocks land in the wrong band.
            sectionWindowIndex = 0;
            int culledAbove = 0;
            int culledBelow = 0;
            int sectionsDrawn = 0;
            for (int sectionIndex = 0;
                sectionIndex < layoutSections.Count && markerIndex < MarkerPoolSize;
                sectionIndex++)
            {
                TimelineSection section = layoutSections[sectionIndex];

                // Position this band's box and hand its content layer to the markers below, so
                // each band's rows are clipped by their own box instead of drawing over the band
                // underneath.
                SectionWindow window = AcquireSectionWindow(section);
                RectTransform markerHost = window == null ? markerLayer : window.Content;
                if (window != null)
                {
                    sectionsDrawn++;
                }
                // Only the rows inside this band's window are looked at. The band's rows are
                // counted from its own scroll offset, so a band showing rows 40-44 walks four
                // rows rather than forty.
                int firstVisible = Mathf.Clamp(
                    Mathf.FloorToInt(section.RowScrollOffset / Mathf.Max(1f, rowHeight)),
                    0, Mathf.Max(0, section.EventRowCount - 1));
                // 层堆叠模式没有窗口，分类框按它自己的行数整个展开，所以
                // 不能再用卷帘那个「窗口里装几行」的上限去卡它——那样一个
                // 装着几百个事件的分类只会画出前几行。真正限制每趟画多少
                // 的是标记池，去重、剔除和池子检查都在下面各自处理。
                int lastVisible = layoutMode == TimelineLayoutMode.SectionRoll
                    ? Mathf.Min(
                        section.EventRowCount,
                        firstVisible + Mathf.CeilToInt(SectionWindowRows) + 1)
                    : section.EventRowCount;

                for (int bandRow = firstVisible; bandRow < lastVisible && markerIndex < MarkerPoolSize; bandRow++)
                {
                    int row = section.EventRowStart + bandRow;
                    int rowIndex = section.EventRowBandStart + bandRow;

                    // Rows above the viewport are skipped rather than drawn: markers
                    // come from a fixed pool, and binding off-screen rows would exhaust
                    // it before reaching the visible ones.
                    // Content-space Y (not yet offset by the timeline's scroll), because the
                    // culling below compares against content-space bounds. Mixing the two frames
                    // is what dropped every row once the timeline was scrolled: rowTop already had
                    // verticalScrollOffset removed, and the test removed it again, so the two
                    // sides drifted apart by the scroll distance and all rows failed the test.
                    float contentTop = section.EventRowsTop
                        + (bandRow * rowHeight - section.RowScrollOffset);

                    // Rows past this window's foot belong to the band below, which covers them.
                    if (contentTop + rowHeight <= section.EventRowsTop
                        || contentTop >= section.WindowBottom)
                    {
                        culledAbove++;
                        continue;
                    }
                    // And rows outside the timeline's own viewport.
                    if (contentTop + rowHeight < verticalScrollOffset
                        || contentTop > verticalScrollOffset + bodyHeight - RulerHeight)
                    {
                        culledBelow++;
                        continue;
                    }

                    if (rowIndex < 0 || rowIndex >= layoutRows.Count)
                    {
                        continue;
                    }

                    List<RowInterval> intervals = layoutRows[rowIndex];
                    int intervalIndex = LowerBoundRowEnd(intervals, viewStartTime);
                    while (intervalIndex < intervals.Count
                        && intervals[intervalIndex].StartTime <= viewEndTime
                        && markerIndex < MarkerPoolSize)
                    {
                        TimelineItem item = intervals[intervalIndex++].Item;
                        float rawX = (item.StartTime - viewStartTime) * pixelsPerSecond;
                        float rawWidth = GetItemPixelWidth(item);
                        if (rawX + rawWidth < 0f)
                        {
                            continue;
                        }

                        float clippedX = Mathf.Max(0f, rawX);
                        float clippedRight = Mathf.Min(bodyWidth, rawX + rawWidth);
                        float clippedWidth = clippedRight - clippedX;
                        if (clippedWidth < 8f)
                        {
                            continue;
                        }

                        // Positioned from the row table rather than from the item's row
                        // index, because rows no longer share a height now that each band
                        // is introduced by a header.
                        // Window-relative: the band is a box whose own scroll decides which of
                        // its rows show, and the box itself moves with the timeline's scroll.
                        // Window-relative: the box carries the timeline scroll, so a row's place
                        // inside it depends only on the band's own scroll.
                        float y = contentTop - section.EventRowsTop + RowVerticalPadding;
                        TimelineMarker marker = markerPool[markerIndex++];
                        if (marker.Rect.parent != markerHost)
                        {
                            marker.Rect.SetParent(markerHost, false);
                        }
                        BindMarker(
                            marker,
                            item,
                            clippedX,
                            clippedWidth,
                            IsTimelineItemSelected(item),
                            IsTimelineItemInSelectedFloor(item),
                            y,
                            rowHeight - RowVerticalPadding * 2f
                        );
                    }
                }

                // Only when the band's own rows cannot all fit in the viewport does the
                // "+N" hint have anything to say: N counts the rows still below the
                // visible area, so it shrinks as the chart is scrolled down. A collapsed
                // band reports everything it is hiding, since none of it is on screen.
                //
                // The band also has to have started on screen. Without that check every
                // band still below the fold would report all of its rows and pile its
                // badge onto the same spot at the foot of the viewport.
                // Counted against the band's own window, not the timeline viewport: the badge
                // reports rows still below what *this box* is showing, which is what the "+N"
                // means now that each band scrolls on its own.
                // The layer stack has no roll, so nothing is hidden and the hint has nothing
                // to say.
                if (layoutMode == TimelineLayoutMode.SectionRoll
                    && !section.Collapsed
                    && section.EventRowCount > SectionWindowRows)
                {
                    int firstHidden = Mathf.FloorToInt(section.RowScrollOffset / Mathf.Max(1f, rowHeight))
                        + Mathf.CeilToInt(SectionWindowRows);
                    if (firstHidden < section.EventRowCount)
                    {
                        DrawSectionBadges(section, firstHidden, markerHost);
                    }
                }
            }

            for (int i = markerIndex; i < markerPool.Count; i++)
            {
                markerPool[i].Item = null;
                markerPool[i].GameObject.SetActive(false);
            }
            RefreshSectionWindows();
            RefreshCollapsedBadges();
            UpdateFilterVisuals();
        }

        /// <summary>
        /// Pins a "+N" hint to the bottom of a band that runs past the viewport, so it
        /// reads as "there are N more rows below this". It is not attached to any one
        /// block: it stays at the foot of the band and N falls as the chart is scrolled,
        /// which is what makes it obvious there is more to scroll through.
        /// </summary>
        /// <summary>
        /// One hint per column of hidden events, pinned to the top of the band's window.
        ///
        /// A stack of events sharing a start time is what the reader sees as a column, so the hint
        /// belongs over that column rather than over each row -- one per row produced a ladder of
        /// identical marks that said nothing about where the missing events were.
        ///
        /// Adjacent columns are merged, because the point of the mark is where the timeline
        /// continues below and two chevrons a few pixels apart say that no better than one.
        ///
        /// The hints are children of the window's content layer, which is masked: that is what
        /// makes them part of the roll.
        /// </summary>


        /// <summary>
        /// Pins a "+N" hint to the bottom of a band that runs past the viewport, so it
        /// reads as "there are N more rows below this". It is not attached to any one
        /// block: it stays at the foot of the band and N falls as the chart is scrolled,
        /// which is what makes it obvious there is more to scroll through.
        /// </summary>
        /// <summary>
        /// One hint per column of hidden events, pinned to the top of the band's window.
        ///
        /// A stack of events sharing a start time is what the reader sees as a column, so the hint
        /// belongs over that column rather than over each row -- one per row produced a ladder of
        /// identical marks that said nothing about where the missing events were.
        ///
        /// Adjacent columns are merged, because the point of the mark is where the timeline
        /// continues below and two chevrons a few pixels apart say that no better than one.
        ///
        /// The hints are children of the window's content layer, which is masked: that is what
        /// makes them part of the roll.
        /// </summary>
        private void DrawSectionBadges(TimelineSection section, int firstHiddenRow, RectTransform host)
        {
            if (host == null || firstHiddenRow >= section.EventRowCount)
            {
                return;
            }

            // One entry per distinct column of hidden events. Neighbours merge, not just exact
            // duplicates: two columns a few pixels apart would otherwise draw two chevrons on top
            // of each other and read as one smeared mark, which is exactly the confusion the hint
            // exists to remove.
            hintTimes.Clear();
            hintCounts.Clear();
            float maxHintGap = Mathf.Max(2f * BadgeWidth / Mathf.Max(1f, pixelsPerSecond), 3f * ColumnGroupSeconds);
            for (int bandRow = firstHiddenRow; bandRow < section.EventRowCount; bandRow++)
            {
                int rowIndex = section.EventRowBandStart + bandRow;
                if (rowIndex < 0 || rowIndex >= layoutRows.Count)
                {
                    continue;
                }

                List<RowInterval> intervals = layoutRows[rowIndex];
                for (int k = 0; k < intervals.Count; k++)
                {
                    float start = intervals[k].StartTime;
                    int slot = -1;
                    for (int h = 0; h < hintTimes.Count; h++)
                    {
                        if (Mathf.Abs(hintTimes[h] - start) <= maxHintGap)
                        {
                            slot = h;
                            break;
                        }
                    }

                    if (slot < 0)
                    {
                        hintTimes.Add(start);
                        hintCounts.Add(1);
                    }
                    else
                    {
                        hintCounts[slot]++;
                    }
                }
            }

            // Pinned to the FOOT of the window, not the head. The mark means "this band carries on
            // below", so it has to sit at the cut, where the eye is already looking for what got
            // clipped -- at the top it reads as decoration on the header instead.
            float hintHeight = Mathf.Max(44f, rowHeight * 0.95f);
            float hintTop = SectionWindowRows * rowHeight - hintHeight - 1f;
            for (int h = 0; h < hintTimes.Count; h++)
            {
                if (collapsedBadgeIndex >= collapsedBadgePool.Count)
                {
                    break;
                }

                // Same coordinate space as the markers: absolute time minus the view origin, placed
                // straight into the window's content layer. An absolute time dropped in unshifted
                // lands far off to the right, and clamping every one of those to the same edge is
                // what stacked the whole set into a single mark.
                float columnX = (hintTimes[h] - viewStartTime) * pixelsPerSecond;
                if (columnX > bodyWidth)
                {
                    continue;
                }

                CollapsedBadge badge = collapsedBadgePool[collapsedBadgeIndex++];
                if (badge.Rect != host)
                {
                    badge.Rect.SetParent(host, false);
                }

                // Forced to the front every pass. Markers share this parent and are recycled, so
                // a marker that changes parent lands after the badge and paints over it -- the
                // hint is only ever correct when it is the last sibling. Hoisting once at reparent
                // time is not enough, because the badge stops reparenting after the first frame.
                badge.Rect.SetAsLastSibling();

                // Clamped at the left edge the way the markers are, so a hint whose column has
                // half scrolled off keeps marking it rather than sliding inward and pointing at
                // the wrong time.
                SetTopLeft(badge.Rect, Mathf.Max(0f, columnX), hintTop, BadgeWidth, hintHeight);

                // The arrow owns a slice at the foot of the badge and the count sits above it,
                // so neither covers the other. Laid out here because the slice depends only on
                // the row height.
                // The arrow is sized on its own and centred, not stretched across the badge. The
                // badge is as wide as the count needs; a V spanning all of that reads as a rule
                // rather than a chevron.
                float arrowHeight = Mathf.Max(4f, hintHeight * 0.24f);
                float arrowWidth = Mathf.Min(BadgeWidth, Mathf.Max(16f, BadgeWidth * 0.42f));
                SetTopLeft(badge.Arrow.rectTransform,
                    (BadgeWidth - arrowWidth) * 0.5f,
                    hintHeight - arrowHeight,
                    arrowWidth,
                    arrowHeight);
                badge.Arrow.sprite = GetChevronSprite(arrowWidth, arrowHeight);
                // Texture carries coverage only; the tint is what names the colour.
                badge.Arrow.color = ChevronColor;

                SetTopLeft(badge.Label.rectTransform, 0f, 0f, BadgeWidth, hintHeight - arrowHeight);
                badge.Label.text = "+" + hintCounts[h];
                badge.Label.color = ChevronColor;
                badge.Rect.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// Content-space Y of the last row a band shows, in the band's current state.
        /// </summary>


        /// <summary>
        /// Content-space Y of the last row a band shows, in the band's current state.
        /// </summary>
        private float GetBandBottom(TimelineSection section)
        {
            if (section.ShownRows <= 0)
            {
                return GetRowTop(section.HeaderRow) + SectionHeaderHeight;
            }

            int lastRow = section.EventRowStart + section.ShownRows - 1;
            return GetRowTop(lastRow) + rowHeight;
        }

        /// <summary>
        /// How many of a band's visible rows start below the viewport. Counted from the
        /// far end so a band with thousands of rows does not walk all of them.
        /// </summary>


        /// <summary>
        /// How many of a band's visible rows start below the viewport. Counted from the
        /// far end so a band with thousands of rows does not walk all of them.
        /// </summary>
        private int CountRowsBelowViewport(TimelineSection section, float viewBottom)
        {
            int below = 0;
            for (int bandRow = section.ShownRows - 1; bandRow >= 0; bandRow--)
            {
                if (GetRowTop(section.EventRowStart + bandRow) <= viewBottom)
                {
                    break;
                }
                below++;
            }
            return below;
        }


        private void RefreshCollapsedBadges()
        {
            for (int i = collapsedBadgeIndex; i < collapsedBadgePool.Count; i++)
            {
                collapsedBadgePool[i].Rect.gameObject.SetActive(false);
            }
            collapsedBadgeIndex = 0;
        }

        private int collapsedBadgeIndex;

        /// <summary>
        /// Draws the visible band headers. A header stands in for its band, so it also
        /// carries the collapse state: a chevron in front of the icon and the row count
        /// on the right.
        /// </summary>


        /// <summary>
        /// Draws the visible band headers. A header stands in for its band, so it also
        /// carries the collapse state: a chevron in front of the icon and the row count
        /// on the right.
        /// </summary>
        private void RefreshSectionHeaders()
        {
            int poolIndex = 0;
            for (int i = 0; i < layoutSections.Count && poolIndex < sectionHeaderPool.Count; i++)
            {
                TimelineSection section = layoutSections[i];
                float y = RulerHeight + GetRowTop(section.HeaderRow) - verticalScrollOffset;
                if (y >= bodyHeight)
                {
                    break;
                }
                if (y + SectionHeaderHeight <= RulerHeight)
                {
                    continue;
                }

                SectionHeaderView view = sectionHeaderPool[poolIndex];
                SetTopLeft(view.Rect, 0f, y, bodyWidth, SectionHeaderHeight);
                view.Section = section;
                view.Label.text = SectionTitle(section);
                view.Icon.gameObject.SetActive(section.Icon != null);
                if (section.Icon != null)
                {
                    view.Icon.sprite = section.Icon;
                }

                view.Background.color = section.Collapsed
                    ? new Color(0.127f, 0.127f, 0.127f, 0.98f)
                    : new Color(0.105f, 0.105f, 0.105f, 0.96f);
                view.GameObject.SetActive(true);
                poolIndex++;
            }

            for (int i = poolIndex; i < sectionHeaderPool.Count; i++)
            {
                sectionHeaderPool[i].GameObject.SetActive(false);
            }
        }


        private static string SectionTitle(TimelineSection section)
        {
            // Plain triangles rather than the small variants: these are in the CJK
            // font's glyph set, the small ones usually are not and would render as
            // missing-glyph boxes.
            return (section.Collapsed ? "▶ " : "▼ ")
                + section.Title
                + "  (" + section.EventRowCount + ")";
        }


        private static float GetMarkerIconSize(float height)
        {
            return Mathf.Clamp(height - 8f, 9f, 16f);
        }

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


        private void ApplyMarkerIcon(TimelineMarker marker, TimelineItem item, float height, bool exactSelected)
        {
            Sprite icon = GetEventIcon(item.Event);
            float iconSize = GetMarkerIconSize(rowHeight);
            bool shown = icon != null && iconSize > 0f;

            marker.Icon.gameObject.SetActive(shown);
            if (shown)
            {
                marker.Icon.sprite = icon;
                // 图标是白色剪影（游戏里正是靠这个把它染成黑色或
                // vfxIconColor 来表示状态），所以贴在彩色块上必须自己上色，
                // 否则白对彩，小尺寸下糊成一团。默认用块自己的深色，
                // 和文字一致；精确选中的那块换成白色，因为那时
                // 填充被提亮、描边也变深了，反倒需要图标跳出来。
                marker.Icon.color = exactSelected ? Color.white : item.LabelColor;
                SetTopLeft(marker.Icon.rectTransform, MarkerIconLeft, 0f, iconSize, height);
            }

            // The text starts after the icon when there is one, and keeps its original
            // margin when there is not, so a missing sprite cannot leave a gap.
            float textLeft = shown ? MarkerIconLeft + iconSize + MarkerIconGap : MarkerIconLeft;
            SetTopLeft(marker.Label.rectTransform, textLeft, 0f,
                Mathf.Max(1f, marker.Rect.sizeDelta.x - textLeft - MarkerIconLeft), height);
        }


        private void BindMarker(
            TimelineMarker marker,
            TimelineItem item,
            float x,
            float width,
            bool exactSelected,
            bool inSelectedFloor,
            float y,
            float height)
        {
            SetTopLeft(marker.Rect, x, y, width, height);
            ApplyMarkerIcon(marker, item, height, exactSelected);
            Color baseColor = item.BlockColor;
            if (!item.Active)
            {
                baseColor.a = 0.38f;
            }
            // 两档高亮：
            //   exactSelected —— 点中的就是这一个事件，深色描边 + 轻微提亮
            //   inSelectedFloor —— 只是它所在的格被选中，仅轻微提亮
            // 以前两档合成一档，于是选中一格会让整行所有事件一起变样，
            // 看不出点的到底是哪个。
            Color blockColor = baseColor;
            if (exactSelected || inSelectedFloor)
            {
                blockColor = Color.Lerp(baseColor, Color.white, 0.06f);
            }
            blockColor.a = item.Active ? 1f : 0.46f;
            marker.Background.color = blockColor;

            if (marker.Outline != null)
            {
                marker.Outline.gameObject.SetActive(exactSelected);
                if (exactSelected)
                {
                    // 描边用块自己的深色，不用白色：白描边是浅填充时期
                    // 定下的，现在填充是饱和的中调色，白框会亮过块本身，
                    // 选中反而看不清轮廓。深色框和块同色相，读起来是
                    // 「这个块被圈出来了」而不是「这个块发光了」。
                    marker.Outline.color = item.LabelColor;
                    // 尺寸跟着传入的行高走，所以 ALT+滚轮调整时间块高度后
                    // 下一帧描边就贴合新高度，不用额外通知。
                    SetTopLeft(marker.Outline.rectTransform, 0f, 0f, width, height);
                }
            }
            // 起点圆头：夹在填充和文字之间的中深色。比文字浅一点，
            // 才看得出它是标在块上的一个记号，而不是块被切开的一道缝。
            if (marker.Cap != null)
            {
                Color capColor = Color.Lerp(item.BlockColor, item.LabelColor, 0.45f);
                capColor.a = item.Active ? 0.95f : 0.40f;
                marker.Cap.color = capColor;
            }
            // Auto-sized rather than a single size: the block's width is decided by the event's
            // duration, so a fixed size either overflows a short block or wastes a long one. TMP
            // shrinks towards the minimum to fit the block, and the minimum is low enough that a
            // name on a very short block still shows something rather than being clipped away.
            marker.Label.enableAutoSizing = true;
            marker.Label.fontSizeMin = 9f;
            marker.Label.fontSizeMax = Mathf.Clamp(rowHeight * 0.46f, 11.5f, 17f);
            marker.Label.fontStyle = exactSelected ? FontStyles.Bold : FontStyles.Normal;
            // The dark partner of the pale fill, stored on the item rather than derived here.
            // Deriving it by brightening the fill used to work when the fills were dark; with pale
            // fills the same rule produces a white label on a white block.
            marker.Label.color = item.Active
                ? item.LabelColor
                : new Color(item.LabelColor.r, item.LabelColor.g, item.LabelColor.b, 0.55f);
            marker.Label.text = item.Name;
            marker.Label.alignment = TextAlignmentOptions.MidlineLeft;
            marker.Item = item;
            marker.GameObject.SetActive(true);
            ApplyEaseCurve(marker, item, width, height);

            if (Main.Settings != null && Main.Settings.VerboseDiagnostics
                && item.Event != null && item.Duration > 0.01f && easedLogged < 12)
            {
                easedLogged++;
                Main.Logger.Log("[TimelineEase] " + item.Name
                    + " dur=" + item.Duration.ToString("0.000")
                    + " w=" + width.ToString("0.0")
                    + " h=" + height.ToString("0.0")
                    + " ease=" + GetTimelineEase(item.Event) + ".");
            }
        }

        private int easedLogged;

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
        private const float EaseCurveThickness = 1.4f;
        private const float EaseCurveMarginX = 3f;
        private const float EaseCurveMarginY = 2f;

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
        private const float EaseCurveDotRadius = 2.2f;
        private const float EaseCurveDotFeather = 0.6f;


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
        /// Rasterises the event's easing as a line strip inside its block. The mesh is
        /// built in local UI pixels rather than stamped from a texture: a texture would
        /// have to stretch across block widths that differ by a factor of ~30, and no
        /// single resolution stays crisp both horizontally and vertically. Drawing the
        /// strip directly also lets the stroke keep a constant width at every zoom
        /// level instead of being scaled along with the block.
        /// </summary>


        /// <summary>
        /// Rasterises the event's easing as a line strip inside its block. The mesh is
        /// built in local UI pixels rather than stamped from a texture: a texture would
        /// have to stretch across block widths that differ by a factor of ~30, and no
        /// single resolution stays crisp both horizontally and vertically. Drawing the
        /// strip directly also lets the stroke keep a constant width at every zoom
        /// level instead of being scaled along with the block.
        /// </summary>
        private static void ApplyEaseCurve(TimelineMarker marker, TimelineItem item,
            float width, float height)
        {
            EaseCurveImage curve = marker.Curve;
            if (curve == null)
            {
                return;
            }

            float curveWidth = width - EaseCurveMarginX * 2f;
            float curveHeight = height - EaseCurveMarginY * 2f;

            // The mesh insets itself by the stroke's feather plus the end dots' radius,
            // so the block has to clear that before the curve is worth drawing at all.
            float meshInset = EaseCurveThickness * 0.5f + EaseCurveFeather + EaseCurveDotRadius;
            bool fits = item.Event != null && item.Duration > 0.01f
                && curveWidth >= meshInset * 2f + 2f
                && curveHeight >= meshInset * 2f + 2f;

            // The curve sits on a block of the same hue and is drawn in that hue's dark partner,
            // the same one the label uses. Pushing the pale fill towards white instead -- the rule
            // that worked when the fills were dark -- now lands on the fill itself and the curve
            // vanishes.
            Color curveColor = item.LabelColor;
            curveColor.a = item.Active ? 1f : 0.42f;
            curve.color = curveColor;

            curve.enabled = fits;
            if (!fits)
            {
                return;
            }

            RectTransform rect = curve.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(EaseCurveMarginX, -EaseCurveMarginY);
            rect.sizeDelta = new Vector2(curveWidth, curveHeight);

            curve.SetCurve(GetEaseCurveValues(GetTimelineEase(item.Event)));
            curve.SetVerticesDirty();
        }

        /// <summary>
        /// An Image whose geometry is a curve instead of a quad. Subclassing is what
        /// makes this reliable: CanvasUpdateRegistry rebuilds every graphic each time it
        /// is marked dirty, so a mesh pushed straight onto the canvas renderer would be
        /// overwritten by the next layout or filter pass.
        /// </summary>
        private sealed class EaseCurveImage : Image
        {
            private float[] _values;

            internal void SetCurve(float[] values)
            {
                _values = values;
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
                float inner = EaseCurveThickness * 0.5f;
                float outer = inner + EaseCurveFeather;
                float inset = outer + EaseCurveDotRadius;
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

                float inner = EaseCurveDotRadius - EaseCurveDotFeather;
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
                        centreX + cos * EaseCurveDotRadius,
                        centreY + sin * EaseCurveDotRadius,
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


        private void RefreshRowBackgrounds()
        {
            int poolIndex = 0;

            // Stripes come from the window's own rows: each band draws a stripe per row it is
            // showing, at that row's place inside the box.
            for (int sectionIndex = 0;
                sectionIndex < layoutSections.Count && poolIndex < rowBackgrounds.Count;
                sectionIndex++)
            {
                TimelineSection section = layoutSections[sectionIndex];
                int firstVisible = Mathf.Clamp(
                    Mathf.FloorToInt(section.RowScrollOffset / Mathf.Max(1f, rowHeight)),
                    0, Mathf.Max(0, section.EventRowCount - 1));
                int lastVisible = Mathf.Min(
                    section.EventRowCount,
                    firstVisible + Mathf.CeilToInt(SectionWindowRows) + 1);

                for (int bandRow = firstVisible;
                    bandRow < lastVisible && poolIndex < rowBackgrounds.Count;
                    bandRow++)
                {
                    // Content space for the tests, viewport space for the placement -- the same
                    // split the markers use. Mixing the two here shifted every stripe by the
                    // timeline's scroll amount and, because this path was written without the
                    // ruler offset, by the ruler's height as well.
                    float contentTop = section.EventRowsTop
                        + (bandRow * rowHeight - section.RowScrollOffset);
                    if (contentTop + rowHeight <= section.EventRowsTop
                        || contentTop >= section.WindowBottom)
                    {
                        continue;
                    }
                    if (contentTop + rowHeight < verticalScrollOffset
                        || contentTop > verticalScrollOffset + bodyHeight - RulerHeight)
                    {
                        continue;
                    }

                    float rowTop = RulerHeight + contentTop - verticalScrollOffset;

                    RectTransform background = rowBackgrounds[poolIndex];
                    SetTopLeft(background, 0f, rowTop, bodyWidth, rowHeight);
                    // Faint: these bands stripe the card, they are not the surface.
                    background.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.030f);
                    background.gameObject.SetActive(true);
                    poolIndex++;
                }
            }

            for (int i = poolIndex; i < rowBackgrounds.Count; i++)
            {
                rowBackgrounds[i].gameObject.SetActive(false);
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
            blockDragOriginalFloor = item.FloorId;
            blockDragOriginalAngleOffset = GetEnabledFloat(item.Event, "angleOffset");
            blockDragOriginalDuration = GetEnabledFloat(item.Event, "duration");
            blockDragHadDuration = CanResizeItem(item);

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
            if (!blockDragActive)
            {
                return;
            }
            blockDragActive = false;

            TimelineItem item = marker == null ? null : marker.Item;
            scnEditor editor = scnEditor.instance;
            if (item == null || item.Event == null || editor == null
                || editor.floors == null || editor.floors.Count == 0)
            {
                visualsDirty = true;
                return;
            }

            float finalStart = item.StartTime;
            float finalDuration = blockDragHadDuration
                ? Mathf.Max(0f, item.Duration)
                : blockDragOriginalDuration;

            // 先还原成原值：下面的 CommitBlockDrag 要用真实的 old 值
            // 和它比较来决定记不记这一步撤销，而 item 上现在挂着的
            // 还是拖动中的中间值。最终值已经存在 finalStart / finalDuration 里。
            item.StartTime = originalStart;
            item.Duration = originalDuration;

            CommitBlockDrag(editor, item, finalStart, finalDuration);
        }

        /// <summary>拖动被中断（块被回收等），丢弃这次操作。</summary>
        private void CancelBlockDrag()
        {
            blockDragActive = false;
            visualsDirty = true;
        }

        private void CommitBlockDrag(
            scnEditor editor,
            TimelineItem item,
            float finalStart,
            float finalDuration)
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

            int oldFloor = blockDragOriginalFloor;
            float oldAngleOffset = blockDragOriginalAngleOffset;

            bool floorChanged = newFloor != oldFloor;
            bool angleChanged = !Mathf.Approximately(newAngleOffset, oldAngleOffset);

            float newDurationValue = blockDragHadDuration
                ? ConvertDurationToData(editor, newFloor, finalDuration)
                : blockDragOriginalDuration;
            bool durationChanged = blockDragHadDuration
                && !Mathf.Approximately(newDurationValue, blockDragOriginalDuration);

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

            if (blockDragHadDuration)
            {
                AddKey(levelEvent, "duration", keys, oldValues, newValues, oldDisabled, newDisabled,
                    blockDragOriginalDuration, newDurationValue, durationChanged);
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
                    + (blockDragHadDuration
                        ? " dur " + blockDragOriginalDuration.ToString("0.###") + "->" + newDurationValue.ToString("0.###")
                        : ""));
            }
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
        /// 时刻更近。这与 PACL2 的 GetFloorNumberTimeSec 是同一思路
        /// （它对 FloorsTime 做二分取格号），区别只是我们同时看
        /// 左右两边，好判断"更近的是哪边"。
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

        private static bool IsAltHeld()
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
            resizeCursorTexture.Apply(false, true);
            return resizeCursorTexture;
        }

        private bool cursorIsResize;

        /// <summary>鼠标悬停在块的左/右边缘时切换成左右箭头。</summary>
        private void ApplyBlockDragCursor(bool onLeftEdge, bool onRightEdge)
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

        private float GetItemPixelWidth(TimelineItem item)
        {
            float durationWidth = item.Duration > 0.001f
                ? item.Duration * pixelsPerSecond - EventGap
                : 0f;
            return Mathf.Max(MinimumPointEventWidth, durationWidth);
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
            return ReferenceEquals(item.Event, selectedEvent) || IsDecorationSelected(item);
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


        private void RefreshGrid()
        {
            // 标尺显示格号而不是秒数：与游戏编辑器里的格号一致，定位事件时
            // 不用在「时间」和「第几格」之间心算。
            int tickIndex = 0;
            float viewEnd = viewStartTime + bodyWidth / Mathf.Max(1f, pixelsPerSecond);

            // 刻度按「格子」而不是时间走：格子之间的时长可以不相等（变速谱），
            // 按时间均分就对不上格线了。所以这里遍历格子，每隔 step 格标一个号。
            //
            // 编号用 seqID，和游戏编辑器里显示的格号一致。
            scnEditor rulerEditor = scnEditor.instance;
            if (rulerEditor != null && rulerEditor.floors != null && rulerEditor.floors.Count > 0)
            {
                // 每格当前占多少像素。用「可视区里的格数」均摊，而不是全谱总格数：
                // 全谱平均在放大后会严重低估格宽，刻度就标得过密、糊成一片。
                int firstVisible = LowerBoundFloors(rulerEditor.floors, viewStartTime);
                int lastVisible = LowerBoundFloors(rulerEditor.floors, viewEnd);
                int visibleFloors = Mathf.Max(1, lastVisible - firstVisible + 1);
                float pixelsPerFloor = bodyWidth / (float)visibleFloors;
                int step = Mathf.Max(1, Mathf.RoundToInt(84f / Mathf.Max(1f, pixelsPerFloor)));

                for (int f = firstVisible; f < rulerEditor.floors.Count && tickIndex < MajorTickPoolSize; f++)
                {
                    scrFloor floor = rulerEditor.floors[f];
                    float floorTime = Convert.ToSingle(floor.entryTime);
                    if (floorTime > viewEnd)
                    {
                        break;
                    }
                    if (f % step != 0)
                    {
                        continue;
                    }

                    float x = (floorTime - viewStartTime) * pixelsPerSecond;
                    TextMeshProUGUI label = majorTickLabels[tickIndex];
                    SetTopLeft(label.rectTransform, x + 4f, 0f, 72f, RulerHeight);
                    string tickText = floor.seqID.ToString();
                    if (majorTickLabelText[tickIndex] != tickText)
                    {
                        majorTickLabelText[tickIndex] = tickText;
                        label.text = tickText;
                    }
                    label.gameObject.SetActive(true);
                    tickIndex++;
                }
            }
            for (int i = tickIndex; i < MajorTickPoolSize; i++)
            {
                majorTickLabels[i].gameObject.SetActive(false);
            }

            scnEditor editor = scnEditor.instance;
            int floorLineIndex = 0;
            if (editor != null && editor.floors != null && editor.floors.Count > 0)
            {
                int floorIndex = LowerBoundFloors(editor.floors, viewStartTime);
                float lastX = float.NegativeInfinity;
                while (floorIndex < editor.floors.Count && floorLineIndex < FloorLinePoolSize)
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
                    Image line = floorLines[floorLineIndex++];
                    SetTopLeft(line.rectTransform, x, RulerHeight, 1f, bodyHeight - RulerHeight);
                    line.gameObject.SetActive(true);
                    lastX = x;
                }
            }
            for (int i = floorLineIndex; i < FloorLinePoolSize; i++)
            {
                floorLines[i].gameObject.SetActive(false);
            }
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
            float current = Mathf.Max(0f, snapshot.CurrentTime);
            float x = (current - viewStartTime) * pixelsPerSecond;
            bool inside = viewInitialized && x >= 0f && x <= bodyWidth;
            playhead.gameObject.SetActive(inside);
            if (inside)
            {
                SetTopLeft(playhead, x, 0f, 2f, bodyHeight);
                SyncPlayheadHeight();
            }
            timeText.text = FormatTime(current) + " / " + FormatTime(Mathf.Max(totalDuration, snapshot.Duration, current));
        }


        /// <summary>
        /// Places the view so the playhead sits at the centre.
        ///
        /// In centred mode viewStartTime is derived entirely from currentTime --
        /// it is not a value anything else may set. So every path that moves the
        /// view (middle-drag panning, a zoom anchor) has to have this run again
        /// in the same frame, or the playhead drifts off centre and the mode
        /// looks broken.
        ///
        /// ClampViewStart is deliberately not called: it clamps the view to
        /// [0, duration - visible span], but centred mode needs padding at both
        /// ends of the chart or the playhead can never reach the centre.
        /// </summary>
        private void SyncCenteredView(float currentTime)
        {
            float wanted = currentTime
                - bodyWidth * CenteredPlayheadFraction / Mathf.Max(1f, pixelsPerSecond);
            if (!Mathf.Approximately(viewStartTime, wanted))
            {
                viewStartTime = wanted;
                visualsDirty = true;
            }
        }

        /// <summary>
        /// 把视图挪到播放头附近。只在播放头接近视口边缘时才动，
        /// 这样正常播放时画面是「一页一页」翻，而不是每帧都在挪。
        /// </summary>
        private void FollowPlayback(float currentTime)
        {
            if (!viewInitialized || pixelsPerSecond <= 0f)
            {
                return;
            }

            float x = (currentTime - viewStartTime) * pixelsPerSecond;
            float targetStart = viewStartTime;
            if (x > bodyWidth * 0.88f)
            {
                targetStart = currentTime - bodyWidth * 0.18f / pixelsPerSecond;
            }
            else if (x < bodyWidth * 0.08f)
            {
                targetStart = currentTime - bodyWidth * 0.12f / pixelsPerSecond;
            }
            if (!Mathf.Approximately(targetStart, viewStartTime))
            {
                viewStartTime = targetStart;
                ClampViewStart();
                visualsDirty = true;
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
            LevelEventType.EditorComment
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

            selectedEvent = item.Event;
            if (item.IsDecoration)
            {
                editor.SelectDecoration(
                    item.Event,
                    jumpToDecoration: false,
                    showPanel: false,
                    ignoreDeselection: false,
                    ignoreAdjustRect: true
                );
            }
            else if (editor.floors != null && editor.floors.Count > 0)
            {
                int floorId = Mathf.Clamp(item.FloorId, 0, editor.floors.Count - 1);
                editor.SelectFloor(editor.floors[floorId], cameraJump: false);
            }
            UpdateSummary();
            visualsDirty = true;
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


        private void ApplyNativeRewindIcon(Button button)
        {
            if (button == null)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            Sprite sprite = editor == null || editor.rewind == null || editor.rewind.image == null
                ? null
                : editor.rewind.image.sprite;
            if (sprite == null)
            {
                return;
            }

            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
            Image icon = CreateImage(
                "NativeRewindIcon",
                button.GetComponent<RectTransform>(),
                7f,
                5f,
                18f,
                18f,
                Color.white
            );
            icon.sprite = sprite;
            icon.type = Image.Type.Simple;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
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
            float anchorTime = viewStartTime + anchorX / oldPixels;
            pixelsPerSecond = requestedPixels;
            viewStartTime = anchorTime - anchorX / pixelsPerSecond;
            ClampViewStart();
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
            float maximumStart = Mathf.Max(0f, navigationDuration - bodyWidth / Mathf.Max(1f, pixelsPerSecond));
            viewStartTime = Mathf.Clamp(viewStartTime, 0f, maximumStart);
        }


        private float GetMaximumVerticalScroll()
        {
            float visibleHeight = Mathf.Max(0f, bodyHeight - RulerHeight);
            return Mathf.Max(0f, layoutTotalHeight - visibleHeight);
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


        private static int LowerBoundRowEnd(List<RowInterval> intervals, float time)
        {
            int low = 0;
            int high = intervals.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (intervals[middle].EndTime < time)
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
                case LevelEventType.ChangeTrack:
                case LevelEventType.ColorTrack:
                case LevelEventType.AnimateTrack:
                case LevelEventType.RecolorTrack:
                case LevelEventType.MoveTrack:
                case LevelEventType.PositionTrack:
                case LevelEventType.Hide:
                case LevelEventType.ScaleMargin:
                case LevelEventType.ScaleRadius:
                case LevelEventType.TileDimensions:
                case LevelEventType.SetFloorIcon:
                    return false;

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


        private static float NiceInterval(float raw)
        {
            raw = Mathf.Max(0.001f, raw);
            float exponent = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float fraction = raw / exponent;
            float nice = fraction <= 1f ? 1f : fraction <= 2f ? 2f : fraction <= 5f ? 5f : 10f;
            return nice * exponent;
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

        /// <summary>
        /// The ruler tick text, e.g. 1:05.
        ///
        /// Built into a reused StringBuilder rather than concatenated: during a
        /// horizontal drag this runs thirty-odd times a frame and was a steady
        /// source of garbage. The ticks themselves still go through the text
        /// cache, so repeated frames do not actually write to TMP.
        /// </summary>
        private static string FormatRulerTime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            int minutes = Mathf.FloorToInt(seconds / 60f);
            int wholeSeconds = Mathf.FloorToInt(seconds - minutes * 60f);

            RulerTimeBuilder.Length = 0;
            RulerTimeBuilder.Append(minutes);
            RulerTimeBuilder.Append(':');
            if (wholeSeconds < 10)
            {
                RulerTimeBuilder.Append('0');
            }
            RulerTimeBuilder.Append(wholeSeconds);
            return RulerTimeBuilder.ToString();
        }

        /// <summary>
        /// Positions the next band's box and returns it, or null once the pool is spent.
        ///
        /// The box carries the timeline's own scroll, so its own scroll offset only decides which
        /// of the band's rows sit inside it.
        /// </summary>


        /// <summary>
        /// Positions the next band's box and returns it, or null once the pool is spent.
        ///
        /// The box carries the timeline's own scroll, so its own scroll offset only decides which
        /// of the band's rows sit inside it.
        /// </summary>
        private SectionWindow AcquireSectionWindow(TimelineSection section)
        {
            if (sectionWindowIndex >= sectionWindowPool.Count)
            {
                return null;
            }

            SectionWindow window = sectionWindowPool[sectionWindowIndex++];
            // RulerHeight offset, as every other element inside viewport uses: the ruler strip
            // occupies the top of the viewport, so content-space Y is measured from below it.
            // Without it every window sat one ruler-height too high and overlapped the strip.
            float top = RulerHeight + section.EventRowsTop - verticalScrollOffset;

            // Section roll is a fixed-height box (the mask clips the overflow); the layer
            // stack has no roll, so the window must grow to hold every row or the band
            // clips its own content.
            float windowRows = layoutMode == TimelineLayoutMode.SectionRoll
                ? SectionWindowRows
                : Mathf.Max(1, section.EventRowCount);
            float windowHeight = windowRows * rowHeight;

            SetTopLeft(window.Rect, 0f, top, bodyWidth, windowHeight);
            window.Rect.gameObject.SetActive(true);
            // The layer stack takes no background: there each type should read as a flat
            // layer rather than as a box.
            window.Background.color = layoutMode == TimelineLayoutMode.SectionRoll
                ? WindowFill
                : Color.clear;
            window.Content.sizeDelta = new Vector2(bodyWidth, windowHeight);
            return window;
        }

        /// <summary>Hides any boxes the pool did not need this pass.</summary>


        /// <summary>Hides any boxes the pool did not need this pass.</summary>
        private void RefreshSectionWindows()
        {
            for (int i = sectionWindowIndex; i < sectionWindowPool.Count; i++)
            {
                sectionWindowPool[i].Rect.gameObject.SetActive(false);
            }
        }


        private void CreateSectionWindow(int index)
        {
            GameObject windowObject = new GameObject("SectionWindow" + index, typeof(RectTransform), typeof(Image));
            windowObject.transform.SetParent(viewport, false);
            RectTransform rect = windowObject.GetComponent<RectTransform>();
            SetTopLeft(rect, 0f, 0f, bodyWidth, SectionWindowRows * rowHeight);

            Image background = windowObject.GetComponent<Image>();
            background.color = WindowFill;
            background.raycastTarget = false;

            // Clips this band's rows to the box. Rows past the foot are cut rather than drawn
            // over the band underneath.
            windowObject.AddComponent<RectMask2D>();

            RectTransform content = CreateRect("Content", rect, 0f, 0f, bodyWidth, SectionWindowRows * rowHeight);

            sectionWindowPool.Add(new SectionWindow
            {
                Rect = rect,
                Content = content,
                Background = background
            });
            windowObject.SetActive(false);
        }


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

        /// <summary>
        /// A white circle with a soft edge, for the origin cap and the section dots.
        ///
        /// Antialiased across the last pixel of the radius rather than hard-clipped, because at
        /// nine pixels across a hard edge reads as a square with its corners knocked off.
        /// </summary>


        /// <summary>
        /// A white circle with a soft edge, for the origin cap and the section dots.
        ///
        /// Antialiased across the last pixel of the radius rather than hard-clipped, because at
        /// nine pixels across a hard edge reads as a square with its corners knocked off.
        /// </summary>
        private Sprite CreateCircleSprite()
        {
            const int size = 32;
            const float radius = size * 0.5f - 1f;
            circleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "ADOFAIEditorStudio.Circle",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            float centre = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - centre;
                    float dy = y + 0.5f - centre;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(radius + 0.5f - distance) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            circleTexture.SetPixels32(pixels);
            circleTexture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                circleTexture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
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
            selectionFollowSprite = CreateSelectionFollowSprite(on);
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
                    // 竖条不跟着 headerHeight 走（它铺满整块面板的高度），
                    // 但调色板换行会改 bodyHeight，这里顺带重算一次没有坏处。
                    if (decoPane != null)
                    {
                        decoPane.SetPaneRect(GutterWidth, DecoPaneTop, DecoPaneWidth,
                            Mathf.Max(RowPaneMinimumHeight, rootHeight - DecoPaneTop - 8f));
                    }
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

        private void ActivatePaletteEvent(int slot)
        {
            if (slot < 0 || slot >= paletteTypes.Count)
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
        }

        /// <summary>
        /// 播放头当前停在哪一格。没有播放头（谱面为空）时返回 -1。
        ///
        /// LowerBoundFloors 给的是第一个 entryTime &gt;= time 的下标，
        /// 所以所在格要减一。
        /// </summary>
        private int PlayheadFloorIndex()
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

        private void ApplyLayoutModeIcon()
        {
            if (layoutModeButton == null)
            {
                return;
            }
            TextMeshProUGUI label = layoutModeButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
            Transform existing = layoutModeButton.transform.Find("LayoutIcon");
            if (existing != null)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
            }
            if (layoutModeSprite != null)
            {
                UnityEngine.Object.Destroy(layoutModeSprite);
            }
            layoutModeSprite = CreateLayoutModeSprite(layoutMode == TimelineLayoutMode.LayerStack);
            Image icon = CreateImage("LayoutIcon", layoutModeButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, TextPrimary);
            icon.sprite = layoutModeSprite;
            icon.raycastTarget = false;
        }

        private void ApplyFollowModeIcon()
        {
            if (followModeButton == null)
            {
                return;
            }
            TextMeshProUGUI label = followModeButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
            }
            Transform existing = followModeButton.transform.Find("FollowIcon");
            if (existing != null)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
            }
            if (followModeSprite != null)
            {
                UnityEngine.Object.Destroy(followModeSprite);
            }
            followModeSprite = CreateFollowModeSprite(followMode == TimelineFollowMode.CenteredPlayhead);
            Image icon = CreateImage("FollowIcon", followModeButton.GetComponent<RectTransform>(),
                5f, 6f, 18f, 16f, TextPrimary);
            icon.sprite = followModeSprite;
            icon.raycastTarget = false;
        }

        /// <summary>
        /// 排列方式图标：三条圆角横条。
        ///
        /// 卷帘模式三条之间留出明显的缝——缝就是「每个类型是一个独立盒子」
        /// 这件事本身；图层模式三条贴得更近、更薄，读作一整叠连续图层。
        ///
        /// 圆角用距离场画（到圆角矩形边界的距离），直边保持精确、转角圆滑，
        /// 而不是靠削角凑近似。文字字形不可靠，所以一律自绘。
        /// </summary>
        private static Sprite CreateLayoutModeSprite(bool layerStack)
        {
            const int w = 32;
            const int h = 28;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var px = new Color32[w * h];

            // 图层模式：条更薄、更密；卷帘模式：条略厚、缝更大。
            float barH = layerStack ? 5.2f : 4.6f;
            float gap = layerStack ? 2.2f : 5.4f;
            float radius = barH * 0.5f;

            for (int b = 0; b < 3; b++)
            {
                float top = h - (5f + b * (barH + gap));
                float left = 7f;
                float right = w - 7f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, left + radius, right - radius);
                        float cy = Mathf.Clamp(y + 0.5f, top + radius, top + barH - radius);
                        float dx = x + 0.5f - cx;
                        float dy = y + 0.5f - cy;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(radius + 0.5f - d);
                        if (a > 0f)
                        {
                            px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                        }
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            Sprite sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect);
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
        }

        /// <summary>
        /// 播放方式图标：一个圆点代表播放头，外加两条向它收拢的圆头短线。
        ///
        /// 圆点落在左侧＝播放头自由；落在正中且两侧短线对称＝内容朝中点汇聚。
        /// 用几何位置表达状态，比画箭头更省笔画，缩到 24px 也读得清。
        /// </summary>
        private static Sprite CreateFollowModeSprite(bool centered)
        {
            const int w = 32;
            const int h = 28;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var px = new Color32[w * h];

            float dotX = centered ? w * 0.5f : w * 0.32f;
            float dotY = h * 0.5f;
            const float dotR = 3.4f;

            // 圆点
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x + 0.5f - dotX;
                    float dy = y + 0.5f - dotY;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(dotR + 0.5f - d);
                    if (a > 0f)
                    {
                        px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                    }
                }
            }

            // 两侧的圆头短线：居中时左右对称，自由时只画右侧一段表示余量。
            const float segR = 1.5f;
            int segCount = centered ? 2 : 1;
            for (int s = 0; s < segCount; s++)
            {
                float inward = centered ? (s == 0 ? 1f : -1f) : 1f;
                float x0 = dotX + inward * (dotR + 3.5f);
                float x1 = dotX + inward * (dotR + 9.5f);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float px0 = Mathf.Min(x0, x1);
                        float px1 = Mathf.Max(x0, x1);
                        float sx = Mathf.Clamp(x + 0.5f, px0, px1);
                        float dx = x + 0.5f - sx;
                        float dy = y + 0.5f - dotY;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(segR + 0.5f - d) * 0.85f;
                        if (a > 0f)
                        {
                            int o = y * w + x;
                            byte v = (byte)Mathf.RoundToInt(a * 255f);
                            if (v > px[o].a) px[o] = new Color32(255, 255, 255, v);
                        }
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            Sprite sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect);
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
        }

        /// <summary>
        /// 跟随选中图标：右边一个方块（被选中的事件），左边一个箭头。
        ///
        /// 开启时箭头指向方块（点哪跳哪），关闭时箭头朝外背离方块
        /// （不跟过去）。两个状态一眼能分开，不用看文字。
        /// </summary>
        /// <summary>
        /// 跟随选中图标：一个取景框加十字准星。
        ///
        /// 两态的差别刻意做大，因为按钮只有 18×16 像素，细微区别看不见：
        ///   跟随 —— 四条手臂一直伸到中心附近，并有一个实心中心点
        ///   关闭 —— 手臂收得很短、离中心留一大段空，且没有中心点
        /// 这样缩到 18px 宽也一眼能分开，不用去猜箭头朝哪边。
        /// </summary>
        private static Sprite CreateSelectionFollowSprite(bool follow)
        {
            const int w = 32;
            const int h = 28;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var px = new Color32[w * h];

            const float cx = 16f;
            const float cy = 14f;
            const float box = 11f;      // 取景框半边长
            const float armThick = 2f;

            // 手臂内端离中心多远。跟随贴到 2.5，关闭退到 10——
            // 中心留下的空档差四倍，这是两态最主要的差别。
            float armInner = follow ? 2.5f : 10f;

            void Stroke(float x0, float y0, float x1, float y1, float r)
            {
                float lox = Mathf.Min(x0, x1);
                float hix = Mathf.Max(x0, x1);
                float loy = Mathf.Min(y0, y1);
                float hiy = Mathf.Max(y0, y1);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float sx = Mathf.Clamp(x + 0.5f, lox, hix);
                        float sy = Mathf.Clamp(y + 0.5f, loy, hiy);
                        float dx = x + 0.5f - sx;
                        float dy = y + 0.5f - sy;
                        float a = Mathf.Clamp01(r + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
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

            // 十字的四条手臂，从取景框边界指向中心。
            Stroke(cx, cy - box, cx, cy - armInner, armThick);
            Stroke(cx, cy + armInner, cx, cy + box, armThick);
            Stroke(cx - box, cy, cx - armInner, cy, armThick);
            Stroke(cx + armInner, cy, cx + box, cy, armThick);

            // 四角括弧。两态都画——它是「取景/定位」的通用记号，
            // 也让手臂收短的那一态不至于散成四个孤立短线。
            const float tick = 4.5f;
            const float tickR = 1.4f;
            Stroke(cx - box, cy - box, cx - box + tick, cy - box, tickR);
            Stroke(cx - box, cy - box, cx - box, cy - box + tick, tickR);
            Stroke(cx + box - tick, cy - box, cx + box, cy - box, tickR);
            Stroke(cx + box, cy - box, cx + box, cy - box + tick, tickR);
            Stroke(cx - box, cy + box - tick, cx - box, cy + box, tickR);
            Stroke(cx - box, cy + box, cx - box + tick, cy + box, tickR);
            Stroke(cx + box, cy + box - tick, cx + box, cy + box, tickR);
            Stroke(cx + box - tick, cy + box, cx + box, cy + box, tickR);

            // 中心实心点。只有跟随态有——第二个一眼可辨的差别，
            // 和手臂长度互相印证。
            if (follow)
            {
                const float dotR = 2.8f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float dx = x + 0.5f - cx;
                        float dy = y + 0.5f - cy;
                        float a = Mathf.Clamp01(dotR + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
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

            tex.SetPixels32(px);
            tex.Apply(false, true);
            Sprite sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect);
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
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
            CreateText("Label", image.rectTransform, 0f, 0f, width, height, label, 10.5f, TextSecondary, TextAlignmentOptions.Center, false);
            SetButtonPalette(button, false);
            return button;
        }

        private static void SetButtonPalette(Button button, bool selected)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = selected ? Selected : ButtonNormal;
            colors.highlightedColor = selected ? new Color(0.055f, 0.485f, 0.495f, 1f) : ButtonHover;
            colors.pressedColor = new Color(0.06f, 0.60f, 0.58f, 1f);
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
