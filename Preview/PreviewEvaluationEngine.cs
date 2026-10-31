using System;
using System.Collections.Generic;
using System.Reflection;
using ADOFAIEditorStudio.Preview.Domains;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview
{
    internal static class PreviewEvaluationEngine
    {
        private static readonly MoveTrackPreviewDomain MoveTrack = new MoveTrackPreviewDomain();
        private static readonly List<IPreviewDomain> Domains = BuildDomains();

        /// <summary>
        /// 【临时探针·重建耗时】上一次重建时每个域 Analyze 花了多少毫秒，
        /// 下标与 `Domains` 对齐。只在重建时写，用途见 `TryBegin` 里那段说明。
        /// </summary>
        private static readonly double[] lastAnalyzeMs = new double[64];

        /// <summary>把 Stopwatch 时间戳差换算成毫秒。</summary>
        private static double MsSince(long startTimestamp)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>上一次求值的播放头时间，用来识别"跳转"。</summary>
        private static float lastEvaluatedTime;

        /// <summary>
        /// 帧内去重（B1）：上一次成功求值所在的帧号与时间。
        /// 同一帧用同一个时间再进来一次时整趟跳过 —— 见 TryEvaluate 里那段说明。
        /// </summary>
        private static int lastEvalFrame = -1;
        private static float lastEvalTime = float.NaN;

        /// <summary>
        /// 同帧脏位：外部在同帧内改了谱面时置起来，让下一次 TryEvaluate
        /// 不要因为「同帧同时间」被跳过。
        /// </summary>
        private static bool sameFrameDirty;

        // ---- 【临时探针】B1 帧内去重到底该不该开 ----
        //
        // B1 现在是硬关着的（见 TryEvaluate 里的说明），理由是"打开后
        // `MoveTrack.WriteCount` 恒为 0、轨道消失动画失效"。可那只是现象，
        // 没有量过"一帧到底被调几次"。如果实际是 2~4 次，那么**所有域**都在
        // 白算 2~4 倍 —— 这是比任何单个域优化都大一个数量级的杠杆；
        // 如果实际只有 1 次，那 B1 从头到尾就是个无效开关，不用再管。
        //
        // 三个数各自回答一个问题：
        //   Ev.Calls  = 本帧到目前为止进 TryEvaluate 几次（>1 才说明有重复）
        //   Ev.SameT  = 其中"和上一次传入的时间相同"几次（去重要靠它命中）
        //   Ev.Dup    = 真正会被 B1 跳过几次（还要求同帧且没有脏位）
        // 全部按帧号自动清零，取本帧最后一次写入的值，所以是"本帧累计"。
        private static int probeEvalFrame = -1;
        private static int probeEvalCalls;
        private static int probeEvalSameTime;
        private static int probeEvalDup;
        private static float probeEvalLastTime = float.NaN;

        /// <summary>上一次调 `Physics2D.SyncTransforms` 的帧号（B2：一帧最多一次）。</summary>
        private static int lastSyncFrame = -1;

        /// <summary>
        /// **自上次物理同步以来，我们真正改动过多少个 Transform。**
        ///
        /// 为什么要有这个计数：`Physics2D.SyncTransforms()` 的开销**不按改动量走，
        /// 按场景里碰撞体的规模走**。2600 格的谱实测一次 0.66~1.08ms（`Sc.Sync`），
        /// 而稳态播放帧里我们一笔 Transform 都没写（`ScW.Pos/Rot/Scale` 恒为 0）——
        /// 也就是说每帧都在花 1ms 同步一堆根本没变的东西。
        ///
        /// 它的唯一用途是让**同一帧内**的脚本射线/重叠查询读到新位置；
        /// 物理模拟步之前 Unity 自己会同步一次，所以「没写过就跳过」是安全的。
        /// 同一帧里要拾取的话，`EditorSceneInteraction.PickObject` 会自己先同步。
        ///
        /// 任何真正写格/球 Transform 的路径都要调 <see cref="NoteTransformWritten"/>。
        /// </summary>
        private static int transformWritesSinceSync;

        /// <summary>上一次无条件同步的帧号（防漏记的兜底，见同步处的说明）。</summary>
        private static int lastSafetySyncFrame = -1;

        /// <summary>
        /// 登记「这一帧我们改动了 Transform」。见 `transformWritesSinceSync` 的说明。
        /// 逐格写的时候别在循环里逐次调，循环外按计数调一次就行。
        ///
        /// **只给「格」记账，不要给球/星球/装饰物记账。**
        /// 那些东西游戏自己每帧就在写（scrPlanet.LateUpdate 之类），原版从来没为它们
        /// 同步过——给它们记账等于每帧都点亮一次，0.85ms 的同步就永远省不掉（实测过）。
        /// </summary>
        internal static void NoteTransformWritten()
        {
            transformWritesSinceSync++;
        }

        /// <summary>
        /// 宣告「这一帧的数据变了，别用帧内去重」。
        ///
        /// 调用方：属性提交、撤销/重做、媒体拖放、重新 Analyze 之后。
        /// 漏调不会崩，只是那一次可能读到上一趟的结果。
        /// </summary>
        internal static void InvalidateSameFrame()
        {
            sameFrameDirty = true;
        }
        /// <summary>
        /// 一帧里播放头挪出这么多秒，就认定是拖动 / 瞬间点击而不是正常播放
        /// （正常一帧约 0.016 秒）。见 TryEvaluate 里"跳转让各域作废历史"。
        /// </summary>
        private const float HistoryJumpSeconds = 0.25f;

        private static List<IPreviewDomain> BuildDomains()
        {
            // AnimateTrack 与 MoveTrack 共用格的 transform/opacity 通道，
            // 所以它由 MoveTrack 的 evaluate 内部驱动，而不是
            // 单独注册：两个独立写入者都从起始基线复位，
            // 每个采样都会互相覆盖。
            MoveTrack.AttachAnimateTrack(new AnimateTrackPreviewDomain());

            return new List<IPreviewDomain>
            {
                // Camera 和 Planet 以前在这里被排除。Planet 仍然排除：球
                // 位置与跟随相机走原生确定性路径
                // （ScrubToFloorNumber + Update_RefreshAngles）。Camera 已回归，
                // 因为现在 ball follow 和 MoveCamera 都由同一个写入者负责，
                // 不再是 SyncResidentNativeCameraFocus 旁边的第二个写入者。
                MoveTrack,
                // **必须排在最前**：freeroam 出场时它会挪网格格子和球所在那格，
                // 而 Camera 和 Planet 都读它算出来的位置（运镜起点、球的圆心）。
                new FreeRoamPreviewDomain(),
                // 点亮球已踩过的格。辉度的显隐由 hasLit 决定，而格子建出来时
                // 它是 false——预览里没有球踩过，不补这一步辉度永远看不见。
                new LightedTrackPreviewDomain(),
                new CameraPreviewDomain(),
                new RecolorTrackPreviewDomain(),
                new DecorationCorePreviewDomain(),
                new FlashPreviewDomain(),
                new BloomPreviewDomain(),
                new CustomBackgroundPreviewDomain(),
                new ScreenTilePreviewDomain(),
                new ScreenScrollPreviewDomain(),
                new HallOfMirrorsPreviewDomain(),
                new ShakeScreenPreviewDomain(),
                new FilterPreviewDomain(),
                new FilterAdvancedPreviewDomain(),
                new ParticlePreviewDomain(),
                // 必须排在 Planet 之前：它写 planet.transform.localScale，
                // 而 Planet 域的圆环要靠那个缩放把自己除回去。
                new ScalePlanetsPreviewDomain(),
                new PlanetPreviewDomain()
            };
        }

        private static PreviewTimelineContext context;

        // 帧开销剖析，仅在 VerboseDiagnostics 打开时填充。
        private static readonly float[] lastDomainMs = new float[32];
        private static float lastSyncMs;

        private static float lastEngineMs;
        private static float lastValidateMs;        private static bool active;
        private static readonly bool[] DomainEnabled = new bool[Domains.Count];

        // ------------------------------------------------------------ 编辑器专属引导线

        // scnEditor 的"格子连接线"是私有字段，拿一次缓存住。
        private static FieldInfo floorConnectorsField;

        /// <summary>
        /// 隐藏编辑器的「格子连接线」。
        ///
        /// 位置轨道把轨道偏移之后，编辑区会画一条滚动虚线，从格子连到
        /// 偏移后的落点——那是给作者对齐用的引导线，**原版播放时不存在**
        /// （播放走 scnGame，那边根本没有 scnEditor）。预览跑在编辑器
        /// 场景里，所以它会露出来，看着像"预览里多了一堆虚线"。
        ///
        /// 必须每帧压，不能只关一次：编辑器在编辑时会反复重建这些
        /// 连线对象，一次性的隐藏会被下一次重建覆盖掉。
        /// 关工作台时由 ReleaseEditorScaffolding 还原。
        /// </summary>
        private static void SuppressEditorScaffolding(scnEditor editor)
        {
            if (editor == null)
            {
                return;
            }

            if (floorConnectorsField == null)
            {
                floorConnectorsField = typeof(scnEditor).GetField(
                    "floorConnectors", BindingFlags.Instance | BindingFlags.NonPublic);
                if (floorConnectorsField == null)
                {
                    return;
                }
            }

            try
            {
                GameObject connectors = floorConnectorsField.GetValue(editor) as GameObject;
                if (connectors != null && connectors.activeSelf)
                {
                    connectors.SetActive(false);
                }
            }
            catch (Exception)
            {
                // 场景切换期间编辑器单例可能失效；下一帧再来。
            }

            SuppressParticleGizmos();
        }

        // 粒子发射器在编辑器里的那个图标（gizmo）。列表缓存一份：
        // 这谱有 800 多个装饰物，每帧全量扫一遍不值当。
        private static List<GameObject> particleGizmos;
        private static int particleGizmoCacheCount = -1;

        /// <summary>
        /// 隐藏粒子发射器在编辑器里显示的图标。
        ///
        /// 原生 scrParticleDecoration.Update 里是：
        ///
        ///     if (ADOBase.isLevelEditor &amp;&amp; gizmo != null)
        ///         gizmo.SetActive(ADOBase.isEditingLevel);
        ///
        /// 那是给作者在编辑器里看见、并且能选中发射器用的**编辑辅助**，
        /// 不属于关卡内容——原版播放时不会有它。预览跑在编辑器场景里、
        /// 又处在编辑状态，所以它会一直亮着。
        ///
        /// 必须每帧压：上面那句每帧都会把它重新点亮。
        /// </summary>
        private static void SuppressParticleGizmos()
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            List<scrDecoration> all = manager == null ? null : manager.allDecorations;
            if (all == null)
            {
                return;
            }

            if (particleGizmos == null || particleGizmoCacheCount != all.Count)
            {
                particleGizmoCacheCount = all.Count;
                if (particleGizmos == null)
                {
                    particleGizmos = new List<GameObject>();
                }
                particleGizmos.Clear();
                for (int i = 0; i < all.Count; i++)
                {
                    scrParticleDecoration particle = all[i] as scrParticleDecoration;
                    if (particle != null && particle.gizmo != null)
                    {
                        particleGizmos.Add(particle.gizmo);
                    }
                }
            }

            for (int i = 0; i < particleGizmos.Count; i++)
            {
                GameObject gizmo = particleGizmos[i];
                if (gizmo != null && gizmo.activeSelf)
                {
                    gizmo.SetActive(false);
                }
            }
        }

        /// <summary>
        /// 由 EditorStudioOverlay.LateUpdate 每帧调用。
        ///
        /// 必须放在 LateUpdate，不能放在求值里：游戏自己的
        /// scrParticleDecoration.Update 会在本帧稍后执行
        /// `gizmo.SetActive(isEditingLevel)`，把求值阶段关掉的图标
        /// 又点亮。LateUpdate 保证在所有 Update 之后、渲染之前，
        /// 这时候压下去才留得住。
        ///
        /// 这个坑在项目里出现过三次（scrRing、行星尾焰、现在是 gizmo）：
        /// **原生组件在同帧之后运行，会覆盖你写的值。**
        /// </summary>
        internal static void TickEditorScaffoldingLate()
        {
            if (!active)
            {
                return;
            }
            // 【临时探针 · Scaffold 拆解】两段分开计时。
            bool probe = PreviewDiagnostics.Enabled;
            long t0 = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            SuppressEditorScaffolding(scnEditor.instance);
            if (probe)
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                PreviewDiagnostics.RecordPart("Sc.Suppress",
                    (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                t0 = t1;
            }

            // MoveTrack 的格 transform 必须在这里写，不能放在求值里：
            // 游戏的原生组件跑在求值器之后，会把它们重置回烘焙值。
            // 和上面那两条引导线是同一个道理。
            MoveTrack.ApplyPendingWrites();
            if (probe)
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                PreviewDiagnostics.RecordPart("Sc.Write",
                    (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        /// <summary>
        /// 一格在**本帧**真正在哪。
        ///
        /// 先问 MoveTrack 的待写队列，没有才看 transform。
        /// 不能直接读 transform.position：MoveTrack 的写入排在 LateUpdate
        /// （原生组件跑在求值器之后，会把 transform 重置回烘焙值），
        /// 而球和相机都在求值阶段跑，比写入早——直接读会拿到**上一帧**的位置。
        /// 轨道正在动的时候，那一帧的差就是「球没粘在轨道上」的样子。
        /// </summary>
        internal static Vector3 FloorPositionThisFrame(scrFloor floor)
        {
            if (floor == null)
            {
                return Vector3.zero;
            }

            Vector3 pending;
            return MoveTrack.TryGetPendingPosition(floor, out pending)
                ? pending
                : floor.transform.position;
        }

        /// <summary>
        /// 关工作台时把编辑器引导线还回去。
        ///
        /// 它属于原生编辑器：用户在编辑器里改位置轨道时，那几条虚线
        /// 是有用的引导。预览结束后不该替作者把它永久关掉。
        /// </summary>
        private static void ReleaseEditorScaffolding()
        {
            if (floorConnectorsField == null)
            {
                return;
            }

            try
            {
                scnEditor editor = scnEditor.instance;
                GameObject connectors = editor == null
                    ? null
                    : floorConnectorsField.GetValue(editor) as GameObject;
                if (connectors != null && !connectors.activeSelf)
                {
                    connectors.SetActive(true);
                }
            }
            catch (Exception)
            {
                // 编辑器可能已经销毁（关卡关了），那就没什么要还原的。
            }
        }

        internal static bool IsActive
        {
            get { return active; }
        }

        /// <summary>
        /// 补做"只有进播放才会做"的那几件场景准备。
        ///
        /// 原生把这一批（SetFxPlusFromComponents / PrepVfx / ColorFreeroam /
        /// DrawHolds / DrawMultiPlanet）放在 scrController.WaitForStartCo 那个
        /// 进播放的协程里。求值器接管的预览刻意不调 Play()，于是它们全都不跑。
        ///
        /// 目前只有 ColorFreeroam 需要在这里补：它做两件事——
        ///   1. 把 freeroam 的**生成格**染成入口格的颜色
        ///      （`item2.SetColor(item.parentFloor.floorRenderer.material.GetColor("_Color"))`）
        ///   2. 把入口格**本体**的 renderer 关掉，让网格取代它
        ///      （`item.parentFloor.floorRenderer.renderer.enabled = false`）
        ///
        /// 不做的话 freeroam 那一段是"默认外观的网格 + 还多一个正常格"。网格
        /// 本身是 `ApplyEventsToFloors` 里 `MakeFreeroamGrid` 建的，那条路
        /// 加载时就跑了，所以格子是有的——只是没人给它上色。
        ///
        /// DrawMultiPlanet 在 `ApplyEventsToFloors`（21234）里已经调过，不用补。
        /// </summary>
        private static void PrepareEditorScenery()
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker == null)
            {
                return;
            }

            try
            {
                maker.ColorFreeroam();
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview] ColorFreeroam failed: " + exception);
            }
        }

        internal static bool TryBegin(scnEditor editor, out string error)
        {
            error = null;
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
            {
                error = "Editor timeline is not ready.";
                return false;
            }

            try
            {
                // 【临时探针·重建耗时】会话重建一次就写一行，把「上下文快照」
                // 和「各域 Analyze」分开计时。
                //
                // 为什么要这一段：拖动松手会触发一次全量重建，而那几秒里
                // 现有诊断只能看到 `Overlay.U` 一个总数（段表是 120 帧的
                // 窗口平均，还会把尖峰摊平）。不加这一段，就永远不知道该
                // 砍哪一半。只在重建时写，平时零成本。
                long totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
                End();

                long contextStart = System.Diagnostics.Stopwatch.GetTimestamp();
                context = new PreviewTimelineContext(editor);
                double contextMs = MsSince(contextStart);

                PrepareEditorScenery();
                int enabledCount = 0;
                for (int i = 0; i < Domains.Count; i++)
                {
                    long domainStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        Domains[i].Analyze(context);
                        DomainEnabled[i] = true;
                        enabledCount++;
                    }
                    catch (Exception exception)
                    {
                        DomainEnabled[i] = false;
                        Main.Logger.Error("Static preview domain " + Domains[i].Name
                            + " failed to analyze: " + exception);
                    }
                    lastAnalyzeMs[i] = MsSince(domainStart);
                }

                if (enabledCount == 0)
                {
                    throw new InvalidOperationException("No static preview domain could be initialized.");
                }

                active = true;
                Main.Logger.Log("Static preview evaluator ready with " + enabledCount
                    + "/" + Domains.Count + " domains.");

                // 按域逐条列出毫秒数。18 个域名一次写完，读日志时不用再
                // 靠减法反推是哪个域吃掉的时间。
                System.Text.StringBuilder analyzeLine = new System.Text.StringBuilder(512);
                analyzeLine.Append("[RebuildCost] 上下文快照=").Append(contextMs.ToString("0.0"))
                    .Append("ms 重建合计=").Append(MsSince(totalStart).ToString("0.0")).Append("ms |");
                for (int i = 0; i < Domains.Count; i++)
                {
                    analyzeLine.Append(' ').Append(Domains[i].Name)
                        .Append('=').Append(lastAnalyzeMs[i].ToString("0.0"));
                }
                Main.Logger.Log(analyzeLine.ToString());
                return true;
            }
            catch (Exception exception)
            {
                End();
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 会话进行中编辑器的数据变了，缓存快照不再
        /// 描述它。这点与其他所有失败都不同：它可以通过
        /// 重新分析恢复，而求值器未激活或编辑器缺失则不行。
        /// </summary>
        internal const string StaleSnapshotError = "Static preview snapshot is stale; rebuild the session.";

        internal static bool IsStaleSnapshot(string error)
        {
            return string.Equals(error, StaleSnapshotError, StringComparison.Ordinal);
        }

        internal static bool TryEvaluate(float time, out string error)
        {
            error = null;
            if (!active || context == null || context.Editor == null
                || context.Editor.GetInstanceID() != context.EditorInstanceId)
            {
                error = "Static preview evaluator is not active.";
                return false;
            }

            try
            {
                bool profile = Main.Settings != null && Main.Settings.VerboseDiagnostics;

                long validateStart = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                bool matches = context.MatchesCurrentEditor();
                if (profile)
                {
                    lastValidateMs = (float)(
                        (System.Diagnostics.Stopwatch.GetTimestamp() - validateStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    // 【临时探针】快照校验这笔一直只算不报 —— 它发生在
                    // `engineStart` 之前，所以既不算进任何「域」，也不算进
                    // `Sc.Rest`（那个是 engine 总耗时减域合计）。结果是
                    // `OU.D` 和「域合计」之间总差出 0.5ms 左右，没人知道是谁的。
                    // 先让它现形，再决定要不要动 `FloorChecksPerFrame`。
                    PreviewDiagnostics.RecordPart("Ev.Validate", lastValidateMs);
                }
                if (!matches)
                {
                    error = StaleSnapshotError;
                    return false;
                }

                long engineStart = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                float frameTime = Mathf.Max(0f, time);

                // **B1 暂时关闭（回归调查中）。**
                //
                // 打开后实测 `MoveTrack.WriteCount` 恒为 0、轨道消失动画失效。
                // 怀疑「同帧同时间」的判据在拖动/播放路径上误命中：
                // 那些路径可能就是**故意**在同一时间点重复求值来驱动逐帧效果
                //（`ffxFloorDisappearPlus` 这类）。
                // 在查清之前先关掉，保持每趟都算。
                //
                // **先量再开（Ev.Calls / Ev.SameT / Ev.Dup）**：上面的怀疑如果成立，
                // Ev.Dup 会明显大于 0 且 Ev.Calls 远大于 1 —— 那就是真有人在同帧
                // 同时间反复调；如果 Ev.Dup 恒为 0，说明 B1 从来没命中过，
                // 当初"打开后 WriteCount=0"另有原因，不该靠关这个开关来躲。
                if (Time.frameCount != probeEvalFrame)
                {
                    probeEvalFrame = Time.frameCount;
                    probeEvalCalls = 0;
                    probeEvalSameTime = 0;
                    probeEvalDup = 0;
                    probeEvalLastTime = float.NaN;
                }
                probeEvalCalls++;
                bool probeSameTime = Mathf.Abs(frameTime - probeEvalLastTime) <= 0.00001f;
                if (probeSameTime)
                {
                    probeEvalSameTime++;
                }
                if (probeSameTime && !sameFrameDirty && lastEvalFrame == Time.frameCount)
                {
                    probeEvalDup++;
                }
                probeEvalLastTime = frameTime;
                PreviewDiagnostics.RecordFlag("Ev.Calls", probeEvalCalls);
                PreviewDiagnostics.RecordFlag("Ev.SameT", probeEvalSameTime);
                PreviewDiagnostics.RecordFlag("Ev.Dup", probeEvalDup);
                PreviewDiagnostics.RecordFlag("Ev.T", (int)(frameTime * 1000f));

                const bool frameDedupEnabled = false;
                if (frameDedupEnabled
                    && !sameFrameDirty
                    && lastEvalFrame == Time.frameCount
                    && Mathf.Abs(frameTime - lastEvalTime) <= 0.00001f)
                {
                    error = null;
                    return true;
                }
                sameFrameDirty = false;
                lastEvalFrame = Time.frameCount;
                lastEvalTime = frameTime;

                // **「掉帧」演出：整帧定格。**
                //
                // 谱面把相机甩到极远（配合 HallOfMirrors 的「不清屏」）时，原生
                // 屏幕上留住的其实是上一帧 —— 看起来是卡帧，不是黑屏。预览每帧
                // 都要清屏，照抄就成黑闪，所以把这类帧整个换成上一张正常帧的
                // 时间重算：相机、滤镜、装饰物透明度、粒子全部一起回到那一张。
                //
                // 必须在**跑各域之前**做：只冻相机的话，同一帧里的
                // SetFilter / Bloom / MoveDecorations 照样会变，还是闪。
                CameraPreviewDomain holdOwner = CameraPreviewDomain.Active;
                // 【临时探针】定格判定每帧都会把整条相机事件链从头重放一遍
                // （`EvaluateChannels` 从 t=0 扫到播放头，成本随位置线性增长），
                // 而同一帧稍后相机域自己又会重放一次。这笔钱此前混在 `Sc.Rest` 里。
                long holdStart = profile && holdOwner != null
                    ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (holdOwner != null)
                {
                    float heldTime = holdOwner.ResolveFrameHold(context, frameTime);
                    if (heldTime >= 0f)
                    {
                        frameTime = heldTime;
                    }
                }
                if (profile && holdOwner != null)
                {
                    PreviewDiagnostics.RecordPart("Cam.Hold",
                        (System.Diagnostics.Stopwatch.GetTimestamp() - holdStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }

                // **播放头跳转 → 让各域作废它们的历史状态。**
                //
                // 一帧里挪出超过阈值就一定是拖动或瞬间点击（正常播放一帧只走
                // 约 0.016 秒）。各域里那些「携带值 / 已应用缓存 / 会话状态」
                // 都假设播放头连续移动；跳转必须显式告诉它们历史作废，否则它们
                // 会把上一次的值当成"已经算对了"而跳过写入——实测的表现就是
                // 慢拖能还原、快拖和瞬间点击不还原。
                if (lastEvaluatedTime > 0f
                    && Math.Abs(frameTime - lastEvaluatedTime) > HistoryJumpSeconds)
                {
                    for (int i = 0; i < Domains.Count; i++)
                    {
                        IHistoryResettable resettable = Domains[i] as IHistoryResettable;
                        if (resettable == null)
                        {
                            continue;
                        }

                        try
                        {
                            resettable.ResetHistory();
                        }
                        catch (Exception exception)
                        {
                            Main.Logger.Error("ResetHistory failed for "
                                + Domains[i].Name + ": " + exception);
                        }
                    }
                }
                lastEvaluatedTime = frameTime;

                PreviewEvaluationFrame frame = new PreviewEvaluationFrame(context, frameTime);
                int enabledCount = 0;
                long syncStart = 0L;
                for (int i = 0; i < Domains.Count; i++)
                {
                    if (!DomainEnabled[i])
                    {
                        continue;
                    }

                    long domainStart = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    try
                    {
                        Domains[i].Evaluate(frame);
                        enabledCount++;
                    }
                    catch (Exception exception)
                    {
                        DomainEnabled[i] = false;
                        Main.Logger.Error("Static preview domain " + Domains[i].Name
                            + " was disabled after an evaluation failure: " + exception);
                    }

                    if (profile)
                    {
                        lastDomainMs[i] = (float)(
                            (System.Diagnostics.Stopwatch.GetTimestamp() - domainStart)
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                        PreviewDiagnostics.RecordDomain(Domains[i].Name, lastDomainMs[i]);
                    }
                }

                if (enabledCount == 0)
                {
                    throw new InvalidOperationException("All static preview domains are disabled.");
                }

                // 编辑器专属的引导线不属于预览内容。这里压一次，
                // 但**真正的压制在 LateUpdate**（见 TickEditorScaffoldingLate）：
                // 游戏自己的 scrParticleDecoration.Update 会在本帧稍后把
                // gizmo 重新点亮，只在求值里压等于白压。

                syncStart = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                // **B2：一帧最多同步一次。**
                //
                // 原来每次求值都调 —— 而一帧有 2~4 次调用（见上面 B1 那段），
                // 等于把 2600 格 + 500 装饰物被写过的 Transform 反复刷进物理世界。
                // 真正需要物理一致性的拾取路径（`EditorSceneInteraction.PickObject`）
                // 自己会先同步一次，这里不需要每趟都做。
                //
                // 帧内去重后同一帧本来只会走到这里一次；这条是给「同帧不同时间」
                // （比如拖动时每帧两次精确采样）兜底。
                if (lastSyncFrame != Time.frameCount)
                {
                    lastSyncFrame = Time.frameCount;

                    // **只在真的写过 Transform 时同步。**（2026-10-30）
                    //
                    // 上面那段是「一帧最多一次」，那是 4 倍变 1 倍；
                    // 这一条是「没必要就不做」，把 1 次也省掉。
                    //
                    // 实测（杪夏谱 2600 格）：稳态播放帧 `ScW.Pos/Rot/Scale` 恒为 0，
                    // 我们一笔 Transform 都没写，但每帧仍要花 `Sc.Sync`=0.66~1.08ms
                    // 把 2600 个碰撞体刷一遍。同步的代价只跟场景规模有关，跟改动量无关。
                    //
                    // 安全性：`SyncTransforms` 只影响**同一帧内脚本发起**的射线/重叠查询；
                    // 物理模拟步之前 Unity 自己会同步。已知的脚本查询方
                    // （`EditorSceneInteraction.PickObject`）本来就会自己先同步一次。
                    //
                    // 兜底：每 30 帧无条件同步一次。这一条不是给已知路径的——
                    // 是防「以后新增了写 Transform 的地方忘了调 NoteTransformWritten」，
                    // 那种情况最坏也只会连续 30 帧（约 0.18 秒）读到旧碰撞体。
                    // 分摊下来每帧 0.03ms，换掉这个隐患值得。
                    //
                    // **autoSync 开着的时候整段都跳过。**（2026-10-31）
                    //
                    // 实测：`Sc.Sync` 高值窗口集中在谱面时间 257.8~304.4 秒那一段，
                    // 那正是 MoveTrack 连续动格的段落 —— 每帧都在写 Transform，
                    // 于是每帧都触发一次显式同步，120 帧窗口平均 0.86ms
                    // （不是「每 30 帧一次」：真是那样的话平均值应该是 0.03）。
                    // 而那一段帧耗时正好从 7.1ms 抬到 9.5ms。
                    //
                    // 但不必要：`PhysicsStepSuppressor` 在播放期把
                    // `Physics2D.autoSyncTransforms` 置成了 true（关掉自动步进之后，
                    // 查询前必须靠它把碰撞体同步进 broadphase），所以 Unity 在**每次
                    // 查询之前自己会同步**，我们这一下是纯重复工作。
                    // 而且时序上我们同步的是上一帧的写入（MoveTrack 的真正落地在
                    // LateUpdate），连「同帧查询读到新位置」这个设计目的都没达到。
                    //
                    // 保留安全网本身：它只在 autoSync 被关掉（别的 mod 改的、
                    // 或游戏版本改了行为）时才生效，那种环境里它是唯一的兜底。
                    // 写入计数每帧清零：下一帧重新按「这一帧有没有写」判断，
                    // 不会因为跳过同步而越攒越多。
                    bool wroteTransforms = transformWritesSinceSync > 0;
                    bool safetySyncDue = Time.frameCount - lastSafetySyncFrame >= 30;
                    bool autoSyncHandles = Physics2D.autoSyncTransforms;
                    if (!autoSyncHandles && (wroteTransforms || safetySyncDue))
                    {
                        lastSafetySyncFrame = Time.frameCount;
                        Physics2D.SyncTransforms();
                    }
                    transformWritesSinceSync = 0;
                    if (profile)
                    {
                        // 探针：1 = 这一帧是「autoSync 兜住了，我们没同步」。
                        PreviewDiagnostics.RecordFlag("Sc.SyncSkip", autoSyncHandles ? 1 : 0);
                    }
                }
                if (profile)
                {
                    lastEngineMs = (float)(
                        (syncStart - engineStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    lastSyncMs = (float)(
                        (System.Diagnostics.Stopwatch.GetTimestamp() - syncStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

                    // 【临时探针】把这两笔也报出来。整帧减去 Overlay.U 之后那
                    // 9~10ms 到底是谁的，一直只是推断（"播放态原生开销"）。
                    // 这两笔能量化的先量化：物理同步（`Sc.Sync`）和
                    // 引擎里各域之外的那部分（`Sc.Rest` = 引擎总耗时 − 各域之和）。
                    PreviewDiagnostics.RecordPart("Sc.Sync", lastSyncMs);
                    double domainSum = 0.0;
                    for (int i = 0; i < Domains.Count; i++)
                    {
                        domainSum += lastDomainMs[i];
                    }
                    PreviewDiagnostics.RecordPart("Sc.Rest", lastEngineMs - domainSum);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        internal static void End()
        {
            for (int i = 0; i < Domains.Count; i++)
            {
                // 某个域在这里抛异常，不能阻止其余域被拆除。
                // End() 是原生交接要做的第一件事，异常从这里逃出
                // 会让交接还没开始就中止，运行时停在
                // 显示编辑器但没有音乐的状态——而且没人会报告原因，因为
                // 失败点在域日志记录之上。
                try
                {
                    Domains[i].EndSession();
                }
                catch (Exception exception)
                {
                    Main.Logger.Error("Preview domain " + Domains[i].Name
                        + " failed to end: " + exception);
                }
                DomainEnabled[i] = false;
            }

            ReleaseEditorScaffolding();
            particleGizmos = null;
            particleGizmoCacheCount = -1;

            active = false;
            context = null;
        }
    }
}
