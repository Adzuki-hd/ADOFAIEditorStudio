using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    internal static class EditorSceneInteraction
    {
        private enum SceneTargetKind
        {
            None,
            Decoration,
            Floor
        }

        private const float DragThresholdPixels = 6f;
        private static readonly Vector3[] viewportCorners = new Vector3[4];
        private static readonly List<Collider2D> temporaryFloorColliders = new List<Collider2D>();
        private static readonly List<GameObject> temporaryFloorObjects = new List<GameObject>();

        private static SceneTargetKind pendingKind;
        private static Vector2 pointerDownScreen;
        private static Vector2 dragOriginWorld;
        private static bool dragging;
        private static bool resumeAfterDrag;
        private static float dragTime;
        private static scrDecoration pendingDecoration;
        private static scrFloor pendingFloor;
        private static LevelEvent decorationSource;
        private static LevelEvent decorationKeyframe;
        private static Vector2 decorationPositionAtDragStart;
        private static Vector2 decorationPivotOffsetAtDragStart;

        // ------------------------------------------------------------------ 媒体拖放
        //
        // 把缩略图从工作台的媒体面板拖进预览区，
        // 会即时创建一个装饰物。指针一进入 viewport 就立刻把装饰物建出来，
        // 好让用户看到的是真实对象而不是一个
        // 幻影；指针在别处松开就把它移除。滚轮
        // 修饰键可以在它还按着的时候微调（见 ApplyDropAdjustments）。
        private static bool mediaDropArmed;
        private static bool mediaDropActive;
        private static string mediaDropImage;
        private static string mediaDropAbsolutePath;
        private static LevelEvent mediaDropEvent;
        private static scrDecoration mediaDropDecoration;
        private static int mediaDropFloor = -1;

        // 滚轮累积出来的微调值，每次变化都写到事件上。
        private static float mediaDropScale = 100f;
        private static float mediaDropRotation;
        private static float mediaDropOpacity = 100f;
        private static int mediaDropDepth;
        // 视差是同时作用于两个轴的单一值：这个手势就是要让
        // 图片以不同速率跟随相机，而 X、Y 一起驱动，
        // 才能让图片移动时不发生错切。
        //
        // 默认 0，不是 100。scrParallax.SetTrans 算的是
        //   (camera - posCamAtStart) * multiplier + startPosition
        // 其中 multiplier = 0.01 * parallax，所以 100 得到的 multiplier 是 1，装饰物
        // 与相机一比一跟随 —— 这会把图片钉在屏幕上，完全忽略
        // 指针。原生拖动路径也依赖这一点：当 multiplier 是 1 时它会
        // 把该拖动轴清零。0 让装饰物固定在世界空间，正是
        // 「把它拖到我指着的地方」的含义。
        private static float mediaDropParallax;

        // 每格用固定倍数，而不是速度斜坡：这样快拨和慢拨
        // 落在同一个值上，滚轮才适合精调。
        // Input.GetAxis("Mouse ScrollWheel") 每格大约报 0.1，所以下面的格数
        // 是把那个原始值放大了；没有这步，一格只把缩放移动
        // 5%、旋转半度，用起来就像滚轮没反应。
        private const float ScrollNotchesPerUnit = 10f;
        private const float DropScalePerNotch = 1.12f;
        private const float DropRotatePerNotch = 5f;
        private const float DropOpacityPerNotch = 8f;
        private const int DropDepthPerNotch = 5;
        // 实际谱面里视差都写在 -100..100 内，所以每格 1 个单位：
        // 步子再大一点，两下就把整个可用范围越过去了。
        private const float DropParallaxPerNotch = 1f;

        /// <summary>
        /// 工作台在媒体缩略图上开始拖动时调用。此时什么都不
        /// 创建；指针到达预览区后装饰物才出现。
        /// </summary>
        internal static void ArmMediaDrop(string imageFilename, string absolutePath)
        {
            if (string.IsNullOrEmpty(imageFilename))
            {
                return;
            }

            CancelMediaDrop();
            mediaDropArmed = true;
            mediaDropImage = imageFilename;
            mediaDropAbsolutePath = absolutePath;
        }

        internal static bool IsMediaDropActive
        {
            get { return mediaDropArmed || mediaDropActive; }
        }

        /// <summary>
        /// 驱动媒体拖放手势。从 Tick 驱动，这样它与现有的场景拖动
        /// 共用同一套 viewport 几何和指针状态。
        /// </summary>
        private static void TickMediaDrop(RectTransform viewport)
        {
            if (!mediaDropArmed && !mediaDropActive)
            {
                return;
            }

            // 松开按键即提交或丢弃：在 viewport 内装饰物
            // 留下，在别处则被移除。
            if (Input.GetMouseButtonUp(0))
            {
                bool kept = mediaDropActive && IsScreenPointInside(viewport, Input.mousePosition);
                if (mediaDropActive && !kept)
                {
                    RemoveMediaDrop();
                }
                else if (kept)
                {
                    // 已提交：记下这次创建，好让 Ctrl+Z 能再把它删掉。记在这里
                    // 而不是创建时记，这样被放弃的拖动不留记录。
                    scnEditor dropEditor = scnEditor.instance;
                    if (dropEditor != null && dropEditor.decorations != null && mediaDropEvent != null)
                    {
                        EditorCommandHistory.RecordDecorationCreate(
                            dropEditor,
                            mediaDropEvent,
                            dropEditor.decorations.IndexOf(mediaDropEvent));
                    }
                }

                bool wasDropping = mediaDropActive;
                mediaDropArmed = false;
                mediaDropActive = false;
                mediaDropDecoration = null;
                mediaDropEvent = null;
                mediaDropFloor = -1;

                // 关卡的装饰物数量变了，求值器缓存的快照随之
                // 失效。现在才重建 —— 而不是在装饰物
                // 创建的那一刻 —— 有两重意义：重建会重跑 Analyze，
                // 它会重新读取每个装饰物自己的缩放/透明度/视差，作为新的
                // 基线，于是拖动时用滚轮做的微调就成了
                // 求值器驱动所用的值。在拖动中途做还会把播放头
                // 弹回去，因为新会话会重置时钟。
                if (wasDropping || kept)
                {
                    EditorPlaybackBridge.RequestEvaluatorRebuild("media-drop");
                }
                return;
            }

            bool inside = IsScreenPointInside(viewport, Input.mousePosition);
            if (!inside)
            {
                // 松开前离开 viewport 就丢弃它，这样从缩略图起手、
                // 到时间轴上结束的拖动不会留下游离的装饰物。创建
                // 时已经让快照失效了，所以这里也必须显式请求重建，
                // 否则取消拖动后预览会一直冻住。
                if (mediaDropActive)
                {
                    RemoveMediaDrop();
                    EditorPlaybackBridge.RequestEvaluatorRebuild("media-drop");
                }
                return;
            }

            if (!mediaDropActive && !TryCreateMediaDrop(viewport))
            {
                return;
            }

            ApplyDropAdjustments();
            MoveMediaDropToPointer(viewport);
        }

        private static bool IsScreenPointInside(RectTransform viewport, Vector2 screenPoint)
        {
            return viewport != null
                && RectTransformUtility.RectangleContainsScreenPoint(viewport, screenPoint, null);
        }

        private static bool TryCreateMediaDrop(RectTransform viewport)
        {
            if (!CanCreateDecorationCommand())
            {
                return false;
            }

            scnEditor editor = scnEditor.instance;

            // 先构造 event，填好内容，最后才交给编辑器。
            //
            // AddDecoration(LevelEventType) 自己把两半都做了 —— 它创建事件，
            // 紧接着调用 scrDecorationManager.CreateDecoration，其 Setup() 会读
            // decorationImage。所以之后才赋图片就太晚了：
            // 装饰物已经带着空图片建好了，于是它存在、会被保存，
            // 重载后也会出现，但工作台开着时它什么都不画。
            // floor 由 MoveMediaDropToPointer 在同一帧赋值；事件只需要一个
            // 合法的起始值，好让 CreateDecoration 能解析出父 floor。
            LevelEvent decoration = new LevelEvent(0, LevelEventType.AddDecoration);

            // LevelEvent(floor, type) 已经用 propertiesInfo 的 value_default
            // 播种了每个属性，所以这里只碰这个手势驱动的那些。手工
            // 全列一遍既多余又错误 —— 它猜了根本不存在的
            // 枚举名（FailHitboxType），还会把本来正常的默认值标成禁用。
            SetEnabled(decoration, "decorationImage", mediaDropImage);
            SetEnabled(decoration, "tag", CreateUniqueDecorationTag(scrDecorationManager.instance));
            SetEnabled(decoration, "relativeTo", DecPlacementType.Tile);
            SetEnabled(decoration, "position", Vector2.zero);
            SetEnabled(decoration, "scale", new Vector2(100f, 100f));
            SetEnabled(decoration, "rotation", 0f);
            SetEnabled(decoration, "opacity", 100f);
            SetEnabled(decoration, "depth", 0);
            SetEnabled(decoration, "parallax", Vector2.zero);

            // 这个开着时，SetDepth 提前返回并复制父 floor 的排序，
            // 完全忽略 depth 值 —— 于是对任何保留它的装饰物，Shift+滚轮
            // 微调都毫无作用。谱面都把它写成 false
            // （两个测试谱都显式这么写），所以跟着写，别信默认值。
            SetEnabled(decoration, "syncFloorDepth", false);

            // 不用 scnEditor.undoStates 做门槛：它可能已经被替换掉，
            // 快照被挪到别处，所以那个字段说明不了编辑器是否可用。
            // 丢弃提交之后才把撤销记录写进工作台自己的历史，
            // 而不是在这里 —— 被取消的拖动不能留下历史。
            editor.AddDecoration(decoration);
            if (!ContainsDecoration(editor, decoration))
            {
                ReportSelection("无法创建装饰物");
                mediaDropArmed = false;
                return false;
            }

            mediaDropEvent = decoration;
            mediaDropActive = true;
            mediaDropScale = 100f;
            mediaDropRotation = 0f;
            mediaDropOpacity = 100f;
            mediaDropDepth = 0;

            // 这里不调 UpdateDecorationObjects()。这个装饰物已由 AddDecoration
            // 经 scrDecorationManager.CreateDecoration 创建，而批量刷新开头就是
            // decManager.ClearDecorations() —— 它会销毁每一个装饰物再重建。
            // 预览求值器按状态缓存着 scrDecoration 引用（见
            // DecorationCorePreviewDomain.Evaluate），不会重新解析，所以一次重建会让
            // 其余每个装饰物都绑定到已销毁对象上，全都不再被
            // 驱动 —— 这就是拖放时它们消失的原因。
            mediaDropDecoration = FindDecorationFor(decoration);
            ReportSelection("新装饰物 · " + mediaDropImage
                + "     滚轮 缩放  ·  Alt 旋转  ·  Ctrl 透明度  ·  Shift 深度  ·  Ctrl+Shift 平行");
            return true;
        }

        private static bool ContainsDecoration(scnEditor editor, LevelEvent decoration)
        {
            if (editor == null || editor.decorations == null || decoration == null)
            {
                return false;
            }

            return editor.decorations.Contains(decoration);
        }

        private static scrDecoration FindDecorationFor(LevelEvent source)
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || manager.allDecorations == null || source == null)
            {
                return null;
            }

            for (int i = 0; i < manager.allDecorations.Count; i++)
            {
                scrDecoration decoration = manager.allDecorations[i];
                if (decoration != null && ReferenceEquals(decoration.sourceLevelEvent, source))
                {
                    return decoration;
                }
            }

            return null;
        }

        /// <summary>
        /// 把滚轮修饰键施加到活装饰物上。每个分支都写整套
        /// 属性，好让单个事件带上全部四项调整。
        /// </summary>
        private static void ApplyDropAdjustments()
        {
            LevelEvent decoration = mediaDropEvent;
            if (decoration == null)
            {
                return;
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                float notches = scroll * ScrollNotchesPerUnit;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                {
                    mediaDropRotation += notches * DropRotatePerNotch;
                    ReportAdjustment("旋转  " + mediaDropRotation.ToString("0.#") + "°");
                }
                else if (shift && control)
                {
                    // 先于任何单修饰键分支检查：Shift+Ctrl 是视差那一对，
                    // 否则下面那两个单修饰键分支会把它吞掉。
                    mediaDropParallax = Mathf.Clamp(
                        mediaDropParallax + notches * DropParallaxPerNotch, -200f, 200f);
                    ReportAdjustment("平行  X/Y  " + mediaDropParallax.ToString("0"));
                }
                else if (control)
                {
                    mediaDropOpacity = Mathf.Clamp(
                        mediaDropOpacity + notches * DropOpacityPerNotch, 0f, 100f);
                    ReportAdjustment("透明度  " + mediaDropOpacity.ToString("0") + "%");
                }
                else if (shift)
                {
                    mediaDropDepth += Mathf.RoundToInt(notches) * DropDepthPerNotch;
                    ReportAdjustment("深度  " + mediaDropDepth);
                }
                else
                {
                    mediaDropScale = Mathf.Clamp(
                        mediaDropScale * Mathf.Pow(DropScalePerNotch, notches), 1f, 20000f);
                    ReportAdjustment("缩放  " + mediaDropScale.ToString("0") + "%");
                }
            }

            decoration["scale"] = new Vector2(mediaDropScale, mediaDropScale);
            decoration["rotation"] = mediaDropRotation;
            decoration["opacity"] = mediaDropOpacity;
            decoration["depth"] = mediaDropDepth;
            decoration["parallax"] = new Vector2(mediaDropParallax, mediaDropParallax);
        }

        /// <summary>
        /// 滚轮调整的实时读数。读的就是事件写入所用的那些值，
        /// 所以屏幕上的数字就是会被保存的数字。
        /// </summary>
        private static void ReportAdjustment(string value)
        {
            if (Main.Overlay != null)
            {
                Main.Overlay.SetAdjustmentStatus(value);
            }
        }

        private static void MoveMediaDropToPointer(RectTransform viewport)
        {
            if (mediaDropEvent == null || !TryScreenToWorld(viewport, Input.mousePosition, out Vector2 world))
            {
                return;
            }

            // 绑定到指针下方的 floor，让图片跟着轨道走，
            // 这是谱面作者对拖到台面上的装饰物的预期。
            int floorIndex = FindFloorAtWorld(world);
            if (floorIndex < 0)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            scrFloor floor = editor.floors[floorIndex];
            if (floor == null)
            {
                return;
            }

            float tileSize = Mathf.Max(0.0001f, ADOBase.controller.tileSize);
            Vector3 anchor = floor.transform.position;
            Vector2 local = new Vector2(
                (world.x - anchor.x) / tileSize,
                (world.y - anchor.y) / tileSize);

            // 事件数据才是唯一真相：求值器会用它重新播种每个装饰物，
            // 保存/撤销快照也把它读回来，所以下面的实时 setter
            // 只是让改动在这一帧可见。
            bool floorChanged = floorIndex != mediaDropFloor;
            if (floorChanged)
            {
                mediaDropFloor = floorIndex;
                mediaDropEvent.floor = floorIndex;
                mediaDropEvent["relativeTo"] = DecPlacementType.Tile;
            }
            mediaDropEvent["position"] = local;

            scrDecoration decoration = mediaDropDecoration;
            if (decoration == null)
            {
                mediaDropDecoration = FindDecorationFor(mediaDropEvent);
                decoration = mediaDropDecoration;
                if (decoration == null)
                {
                    return;
                }
            }

            if (floorChanged)
            {
                // 从事件的 floor 和 position 重算 startPos，
                // 求值器的 Baseline 也从这里取。
                decoration.SetPlacementType(DecPlacementType.Tile);
                decoration.parentFloorNum = floorIndex;
                decoration.parentFloor = floor;
            }

            // 与 DecorationCorePreviewDomain 施加的顺序一致，这样预览自己的写入器
            // 和这里对装饰物的位置看法一致。
            Vector2 worldPosition = new Vector2(
                anchor.x + local.x * tileSize,
                anchor.y + local.y * tileSize);
            // 事件值是百分比，而 SetScale 收的是倍数：
            // DecorationCorePreviewDomain 算的是 `target = value.Scale.x / 100f`，
            // 装饰物自己的 scaleVec 本来就在同一倍数空间。把百分比
            // 原样透传会把图片画成 100 倍，刚拖下来的装饰物
            // 填满整个屏幕就是这个原因。
            decoration.SetPosition(worldPosition, decoration.pivotOffsetVec);
            decoration.SetRotation(mediaDropRotation);
            decoration.SetScale(new Vector2(mediaDropScale / 100f, mediaDropScale / 100f));
            decoration.SetOpacity(Mathf.Clamp01(mediaDropOpacity / 100f));

            // depth 必须过一遍 setter，和原生效果的做法一模一样
            // （"if (depthUsed) dec.SetDepth(targetDepth)"）。只写事件数据会
            // 让值存下来却从不绘制，于是 Shift+滚轮改的是一个没人看得见的
            // 数字。SetDepth 把 depth 映射到排序层和 order，负值进
            // "Default" 层，正值进 "Bg"，所以负 depth
            // 会把图片放到轨道后面。
            decoration.SetDepth(mediaDropDepth);

            // 视差同理：真正写 multiplier_x/y 的是 SetParallax（按 value * 0.01），
            // 它还会重跑屏幕夹取，所以光改事件值屏幕上什么都不会变。
            decoration.SetParallax(
                new Vector2(mediaDropParallax, mediaDropParallax),
                DecPlacementType.Tile);
        }

        private static int FindFloorAtWorld(Vector2 world)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                return -1;
            }

            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < editor.floors.Count; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor == null)
                {
                    continue;
                }

                float distance = ((Vector2)floor.transform.position - world).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// 丢弃进行中的装饰物。用编辑器自己的移除方式，好让关卡数据、
        /// 装饰物管理器和选中状态保持一致。
        /// </summary>
        internal static void RemoveMediaDrop()
        {
            LevelEvent decoration = mediaDropEvent;
            scrDecoration doomed = mediaDropDecoration;
            mediaDropEvent = null;
            mediaDropActive = false;
            mediaDropDecoration = null;
            mediaDropFloor = -1;

            if (decoration == null)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.decorations == null)
            {
                return;
            }

            scrDecorationManager manager = scrDecorationManager.instance;

            try

            {
                // skipDecorationUpdate: true 至关重要。默认路径会调
                // UpdateDecorationObjects()，它以 decManager.ClearDecorations() 开头，
                // 从关卡数据重建每一个装饰物 —— 销毁了预览求值器
                // 仍持有引用的那些对象，结果屏幕上其他每个
                // 装饰物都被干掉。改为只拆除这一个。
                editor.RemoveEvent(decoration, skipDecorationUpdate: true);
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[MediaDrop] could not remove decoration: " + exception.Message);
            }

            if (doomed != null)
            {
                if (manager != null && manager.allDecorations != null)
                {
                    manager.allDecorations.Remove(doomed);
                    if (manager.taggedDecorations != null && doomed.tags != null)
                    {
                        foreach (string tag in doomed.tags)
                        {
                            List<scrDecoration> bucket;
                            if (manager.taggedDecorations.TryGetValue(tag, out bucket) && bucket != null)
                            {
                                bucket.Remove(doomed);
                            }
                        }
                    }
                }
                if (doomed.gameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(doomed.gameObject);
                }
            }
        }

        internal static void CancelMediaDrop()
        {
            mediaDropArmed = false;
            RemoveMediaDrop();
        }

        internal static void ReportMediaRefresh()
        {
            ReportSelection("媒体列表已刷新");
        }

        internal static bool IsCapturingPointer
        {
            get { return pendingKind != SceneTargetKind.None && Input.GetMouseButton(0); }
        }

        internal static void Reset()
        {
            pendingKind = SceneTargetKind.None;
            dragging = false;
            resumeAfterDrag = false;
            pendingDecoration = null;
            pendingFloor = null;
            decorationSource = null;
            decorationKeyframe = null;
            CleanupTemporaryFloorColliders();
        }

        internal static void Tick(RectTransform viewport, EditorPlaybackSnapshot snapshot)
        {
            if (viewport == null || scnEditor.instance == null)
            {
                CancelMediaDrop();
                Reset();
                return;
            }

            TickMediaDrop(viewport);

            // 媒体拖放活着时独占指针；否则下面那段场景拖动
            // 也会去抓刚创建的那个装饰物。
            if (mediaDropArmed || mediaDropActive)
            {
                return;
            }

            bool pointerInside = RectTransformUtility.RectangleContainsScreenPoint(viewport, Input.mousePosition, null);
            if (Input.GetMouseButtonDown(0) && pointerInside)
            {
                BeginPointer(viewport);
            }

            if (Input.GetMouseButton(0) && pendingKind != SceneTargetKind.None)
            {
                float distance = Vector2.Distance(pointerDownScreen, Input.mousePosition);
                if (!dragging && distance >= DragThresholdPixels)
                {
                    BeginDrag(viewport, snapshot);
                }
                if (dragging)
                {
                    UpdateDrag(viewport);
                }
            }

            if (Input.GetMouseButtonUp(0) && pendingKind != SceneTargetKind.None)
            {
                EndPointer();
            }
        }

        private static void BeginPointer(RectTransform viewport)
        {
            Reset();
            pointerDownScreen = Input.mousePosition;
            GameObject target = PickObject(viewport, pointerDownScreen);
            if (target == null)
            {
                return;
            }

            scrDecoration decoration = target.GetComponentInParent<scrDecoration>();
            if (decoration != null && decoration.sourceLevelEvent != null && decoration.GetVisible())
            {
                pendingKind = SceneTargetKind.Decoration;
                pendingDecoration = decoration;
                scnEditor.instance.SelectDecoration(
                    decoration.sourceLevelEvent,
                    jumpToDecoration: false,
                    showPanel: false,
                    ignoreDeselection: false,
                    ignoreAdjustRect: true
                );
                ReportSelection("装饰物 · " + GetDecorationLabel(decoration.sourceLevelEvent));
                return;
            }

            scrFloor floor = target.GetComponent<scrFloor>() ?? target.GetComponentInParent<scrFloor>();
            if (floor != null)
            {
                pendingKind = SceneTargetKind.Floor;
                pendingFloor = floor;
                scnEditor.instance.SelectFloor(floor, cameraJump: false);
                ReportSelection("轨道 · 第 " + floor.seqID + " 格");
            }
        }

        private static void BeginDrag(RectTransform viewport, EditorPlaybackSnapshot snapshot)
        {
            if (pendingKind != SceneTargetKind.Decoration || !CanDragPendingDecoration())
            {
                return;
            }

            if (!CanCreateDecorationCommand())
            {
                return;
            }

            Vector2 world;
            if (!TryScreenToWorld(viewport, Input.mousePosition, out world))
            {
                return;
            }

            resumeAfterDrag = snapshot.State == EditorPlaybackState.Playing;
            if (resumeAfterDrag)
            {
                string pauseStatus;
                if (!EditorPlaybackBridge.TrySetPaused(true, out pauseStatus))
                {
                    dragging = false;
                    ReportSelection(pauseStatus);
                    return;
                }
            }

            EditorPlaybackSnapshot frozenSnapshot = EditorPlaybackBridge.ReadSnapshot();
            dragTime = frozenSnapshot.CurrentTime;
            dragOriginWorld = world;
            dragging = true;
            bool dragStarted;
            try
            {
                dragStarted = BeginDecorationDrag();
            }
            catch (Exception exception)
            {
                dragStarted = false;
                ReportSelection("无法开始装饰物拖动，详情见 Mod 日志");
                if (Main.Logger != null)
                {
                    Main.Logger.Error("Failed to begin decoration drag: "
                        + exception.GetType().Name + " - " + exception.Message);
                }
            }

            if (!dragStarted)
            {
                bool shouldResume = resumeAfterDrag;
                dragging = false;
                resumeAfterDrag = false;
                pendingKind = SceneTargetKind.None;
                pendingDecoration = null;
                if (shouldResume)
                {
                    EditorPlaybackBridge.TrySetPaused(false, out _);
                }
            }
        }

        private static bool BeginDecorationDrag()
        {
            if (pendingDecoration == null || pendingDecoration.sourceLevelEvent == null)
            {
                return false;
            }

            scnEditor editor = scnEditor.instance;

            string originalTag = pendingDecoration.sourceLevelEvent.GetString("tag");
            bool originalTagDisabled = pendingDecoration.sourceLevelEvent.disabled.ContainsKey("tag")
                && pendingDecoration.sourceLevelEvent.disabled["tag"];
            var originalRuntimeTags = pendingDecoration.tags == null
                ? new HashSet<string>()
                : new HashSet<string>(pendingDecoration.tags);
            scrDecorationManager manager = scrDecorationManager.instance;
            string uniqueTag = CreateUniqueDecorationTag(manager);
            int floorId;
            float angleOffset;
            ResolveEventTime(dragTime, out floorId, out angleOffset);
            LevelEvent keyframe = new LevelEvent(floorId, LevelEventType.MoveDecorations);
            DisableAllProperties(keyframe);
            SetEnabled(keyframe, "duration", 0f);
            SetEnabled(keyframe, "tag", uniqueTag);
            SetEnabled(keyframe, "relativeTo", DecPlacementType.LastPosition);
            SetEnabled(keyframe, "positionOffset", Vector2.zero);
            SetEnabled(keyframe, "angleOffset", angleOffset);
            SetEnabled(keyframe, "ease", Ease.Linear);
            SetEnabled(keyframe, "eventTag", string.Empty);

            // 这里不取编辑器快照。撤销归工作台自己的历史，
            // 它不依赖 scnEditor.undoStates —— 那个方法可能已被替换、
            // 快照被挪到别处，所以编辑器那个栈既不能用来验证什么，
            // 也不能用来驱动什么。
            decorationSource = pendingDecoration.sourceLevelEvent;
            decorationPositionAtDragStart = pendingDecoration.pivotPosVec;
            decorationPivotOffsetAtDragStart = pendingDecoration.pivotOffsetVec;
            KillPositionTweens(pendingDecoration.eventTweens);

            string taggedValue = ApplyUniqueDecorationTag(pendingDecoration, uniqueTag);
            decorationKeyframe = keyframe;
            editor.events.Add(decorationKeyframe);
            EditorCommandHistory.RecordDecorationMove(
                editor,
                pendingDecoration,
                decorationSource,
                decorationKeyframe,
                originalTag,
                originalTagDisabled,
                taggedValue,
                uniqueTag,
                originalRuntimeTags,
                decorationPositionAtDragStart,
                decorationPivotOffsetAtDragStart
            );
            ReportSelection("当前帧关键帧 · " + GetDecorationLabel(decorationSource));
            return true;
        }

        private static void UpdateDrag(RectTransform viewport)
        {
            Vector2 world;
            if (!TryScreenToWorld(viewport, Input.mousePosition, out world))
            {
                return;
            }
            Vector2 translation = world - dragOriginWorld;
            UpdateDecorationDrag(translation);
        }

        private static void UpdateDecorationDrag(Vector2 translation)
        {
            if (pendingDecoration == null || decorationKeyframe == null)
            {
                return;
            }

            Vector2 adjusted = scnEditor.instance.GetDecorationDragDelta(translation, pendingDecoration);
            Vector2 parallaxMultiplier = pendingDecoration.parallax.multiplier;
            if (Mathf.Approximately(parallaxMultiplier.x, 1f))
            {
                adjusted.x = 0f;
            }
            if (Mathf.Approximately(parallaxMultiplier.y, 1f))
            {
                adjusted.y = 0f;
            }

            decorationKeyframe["positionOffset"] = adjusted / ADOBase.controller.tileSize;
            pendingDecoration.SetPosition(decorationPositionAtDragStart + adjusted, decorationPivotOffsetAtDragStart);
        }

        private static void EndPointer()
        {
            bool shouldResume = dragging && resumeAfterDrag;
            bool changed = dragging && decorationKeyframe != null;
            Reset();
            if (changed)
            {
                // **拖装饰物是结构性改动，要通知联机层。**
                //
                // 它做的事是「往 events 里追加一个关键帧」——新增事件
                // 没法增量同步（没有稳定下标），只能整份重推。
                // `OnLocalStructuralChange` 会排一次自动同步，所以拖完
                // 对面自动就能看到，不需要用户做任何事。
                Multiplayer.MultiplayerSession.OnLocalStructuralChange();

                ReportSelection("当前帧关键帧已写入"
                    + (Multiplayer.MultiplayerSession.IsLinked
                        ? "，会自动同步给其他人" : "，可按 Ctrl+Z 撤回"));
            }
            if (shouldResume)
            {
                EditorPlaybackBridge.TrySetPaused(false, out _);
            }
        }

        private static GameObject PickObject(RectTransform viewport, Vector2 screenPoint)
        {
            Vector2 world;
            if (!TryScreenToWorld(viewport, screenPoint, out world))
            {
                return null;
            }

            GenerateNearbyFloorColliders(world);
            try
            {
                Physics2D.SyncTransforms();
                int mask = LayerMask.GetMask("Foreground", "Floor");
                RaycastHit2D[] hits = Physics2D.RaycastAll(world, Vector2.zero, 0f, mask);
                IEnumerable<GameObject> candidates = hits
                    .Where(hit => hit.collider != null)
                    .Select(hit => hit.collider.name == "Parent" && hit.collider.transform.parent != null
                        ? hit.collider.transform.parent.gameObject
                        : hit.collider.gameObject)
                    .OrderByDescending(GetSortingOrder);
                foreach (GameObject candidate in candidates)
                {
                    scrDecoration decoration = candidate.GetComponentInParent<scrDecoration>();
                    if (decoration != null
                        && decoration.sourceLevelEvent != null
                        && decoration.GetVisible())
                    {
                        return candidate;
                    }

                    scrFloor floor = candidate.GetComponent<scrFloor>()
                        ?? candidate.GetComponentInParent<scrFloor>();
                    if (floor != null)
                    {
                        return candidate;
                    }
                }
                return null;
            }
            finally
            {
                CleanupTemporaryFloorColliders();
            }
        }

        private static bool TryScreenToWorld(RectTransform viewport, Vector2 screenPoint, out Vector2 world)
        {
            world = Vector2.zero;
            Camera camera = EditorBridge.GetEditorCamera();
            if (camera == null || viewport == null)
            {
                return false;
            }

            viewport.GetWorldCorners(viewportCorners);
            float width = Mathf.Max(1f, viewportCorners[2].x - viewportCorners[0].x);
            float height = Mathf.Max(1f, viewportCorners[2].y - viewportCorners[0].y);
            float u = Mathf.Clamp01((screenPoint.x - viewportCorners[0].x) / width);
            float v = Mathf.Clamp01((screenPoint.y - viewportCorners[0].y) / height);
            float depth = Mathf.Abs(camera.transform.position.z);
            world = camera.ViewportToWorldPoint(new Vector3(u, v, depth));
            return true;
        }

        private static bool CanDragPendingDecoration()
        {
            if (pendingDecoration == null || pendingDecoration.sourceLevelEvent == null)
            {
                return false;
            }

            LevelEvent source = pendingDecoration.sourceLevelEvent;
            if (source.locked || pendingDecoration.forceLock)
            {
                ReportSelection("该装饰物已锁定，只能选择");
                return false;
            }

            DecPlacementType placement;
            if (source.TryGet<DecPlacementType>("relativeTo", out placement)
                && (placement == DecPlacementType.RedPlanet
                    || placement == DecPlacementType.BluePlanet
                    || placement == DecPlacementType.GreenPlanet))
            {
                ReportSelection("行星相对装饰物暂时只能选择");
                return false;
            }

            return true;
        }

        private static bool CanCreateDecorationCommand()
        {
            scnEditor editor = scnEditor.instance;
            scrDecorationManager manager = scrDecorationManager.instance;

            // 这里刻意不检查 scnEditor.undoStates。它不是就绪
            // 信号：那个栈可能已经被替换掉、快照被挪进别人的私有静态列表，
            // 于是编辑器完全可以正常工作，而它自己那个字段是 null
            // usable. Requiring it made every drop report "编辑器正在重建" and refuse.
            if (editor == null
                || !editor.initialized
                || editor.changingState != 0
                || editor.events == null
                || editor.decorations == null
                || editor.floors == null
                || editor.floors.Count == 0
                || scrConductor.instance == null
                || ADOBase.controller == null
                || manager == null
                || manager.taggedDecorations == null)
            {
                ReportSelection("编辑器正在重建，暂时不能拖动装饰物");
                return false;
            }
            return true;
        }

        private static void GenerateNearbyFloorColliders(Vector2 world)
        {
            CleanupTemporaryFloorColliders();
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.floors == null)
            {
                return;
            }

            float range = scrFloor.LongDimensions.magnitude;
            foreach (scrFloor floor in editor.floors)
            {
                if (floor == null || Vector2.Distance(floor.transform.position, world) > range)
                {
                    continue;
                }

                FloorMesh mesh = floor.GetComponent<FloorMesh>();
                if (mesh != null)
                {
                    mesh.GenerateCollider();
                    mesh.polygonCollider.enabled = true;
                    temporaryFloorColliders.Add(mesh.polygonCollider);
                }

                if (floor.GetComponent<FloorSpriteRenderer>() != null)
                {
                    GameObject generated = floor.GenerateCollider();
                    generated.name = "Parent";
                    temporaryFloorColliders.Add(generated.GetComponent<Collider2D>());
                    temporaryFloorObjects.Add(generated);
                }
            }
        }

        private static void CleanupTemporaryFloorColliders()
        {
            foreach (Collider2D collider in temporaryFloorColliders)
            {
                if (collider != null)
                {
                    collider.enabled = false;
                }
            }
            foreach (GameObject temporary in temporaryFloorObjects)
            {
                if (temporary != null)
                {
                    UnityEngine.Object.DestroyImmediate(temporary);
                }
            }
            temporaryFloorColliders.Clear();
            temporaryFloorObjects.Clear();
        }

        private static int GetSortingOrder(GameObject gameObject)
        {
            Renderer renderer = gameObject.GetComponentInParent<Renderer>();
            if (renderer != null)
            {
                return renderer.sortingOrder;
            }
            Canvas canvas = gameObject.GetComponentInParent<Canvas>();
            return canvas == null ? gameObject.layer : canvas.sortingOrder;
        }

        private static string CreateUniqueDecorationTag(scrDecorationManager manager)
        {
            string tag;
            do
            {
                tag = "aes_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            }
            while (manager.taggedDecorations.ContainsKey(tag));
            return tag;
        }

        private static string ApplyUniqueDecorationTag(scrDecoration decoration, string tag)
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            LevelEvent source = decoration.sourceLevelEvent;
            string existing = source.GetString("tag");
            source["tag"] = string.IsNullOrWhiteSpace(existing) ? tag : existing.Trim() + " " + tag;
            source.disabled["tag"] = false;
            EditorCommandHistory.ReplaceRuntimeTags(manager, decoration, ParseRuntimeTags(source.GetString("tag")));
            return source.GetString("tag");
        }

        private static HashSet<string> ParseRuntimeTags(string value)
        {
            var tags = new HashSet<string>(
                (value ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            );
            if (tags.Count == 0)
            {
                tags.Add("NO TAG");
            }
            return tags;
        }

        private static void ResolveEventTime(float time, out int floorId, out float angleOffset)
        {
            scnEditor editor = scnEditor.instance;
            floorId = 0;
            for (int i = 0; i < editor.floors.Count; i++)
            {
                if (editor.floors[i].entryTime > time)
                {
                    break;
                }
                floorId = i;
            }

            scrFloor floor = editor.floors[Mathf.Clamp(floorId, 0, editor.floors.Count - 1)];
            float secondsPerHalfBeat = 60f / Mathf.Max(0.001f, scrConductor.instance.bpm * floor.speed);
            angleOffset = (time - Convert.ToSingle(floor.entryTime)) / secondsPerHalfBeat * 180f;
        }

        private static void DisableAllProperties(LevelEvent levelEvent)
        {
            foreach (string key in levelEvent.disabled.Keys.ToList())
            {
                levelEvent.disabled[key] = true;
            }
        }

        private static void SetEnabled(LevelEvent levelEvent, string key, object value)
        {
            if (!levelEvent.disabled.ContainsKey(key))
            {
                return;
            }
            levelEvent[key] = value;
            levelEvent.disabled[key] = false;
        }

        private static void KillPositionTweens(Dictionary<TweenType, Tween> tweens)
        {
            if (tweens == null)
            {
                return;
            }
            Tween tween;
            if (tweens.TryGetValue(TweenType.PositionX, out tween))
            {
                tween?.Kill();
                tweens.Remove(TweenType.PositionX);
            }
            if (tweens.TryGetValue(TweenType.PositionY, out tween))
            {
                tween?.Kill();
                tweens.Remove(TweenType.PositionY);
            }
        }

        private static string GetDecorationLabel(LevelEvent levelEvent)
        {
            string image;
            if (levelEvent.TryGet<string>("decorationImage", out image) && !string.IsNullOrEmpty(image))
            {
                return image;
            }
            string tag;
            levelEvent.TryGet<string>("tag", out tag);
            return string.IsNullOrEmpty(tag) ? levelEvent.eventType.ToString() : tag;
        }

        private static void ReportSelection(string value)
        {
            if (Main.Overlay != null)
            {
                Main.Overlay.SetSceneSelectionStatus(value);
            }
        }
    }
}
