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

        /// <summary>上一次求值的播放头时间，用来识别"跳转"。</summary>
        private static float lastEvaluatedTime;
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
            SuppressEditorScaffolding(scnEditor.instance);

            // MoveTrack 的格 transform 必须在这里写，不能放在求值里：
            // 游戏的原生组件跑在求值器之后，会把它们重置回烘焙值。
            // 和上面那两条引导线是同一个道理。
            MoveTrack.ApplyPendingWrites();
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
                End();
                context = new PreviewTimelineContext(editor);
                PrepareEditorScenery();
                int enabledCount = 0;
                for (int i = 0; i < Domains.Count; i++)
                {
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
                }

                if (enabledCount == 0)
                {
                    throw new InvalidOperationException("No static preview domain could be initialized.");
                }

                active = true;
                Main.Logger.Log("Static preview evaluator ready with " + enabledCount
                    + "/" + Domains.Count + " domains.");
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
                }
                if (!matches)
                {
                    error = StaleSnapshotError;
                    return false;
                }

                long engineStart = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                float frameTime = Mathf.Max(0f, time);

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
                if (holdOwner != null)
                {
                    float heldTime = holdOwner.ResolveFrameHold(context, frameTime);
                    if (heldTime >= 0f)
                    {
                        frameTime = heldTime;
                    }
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
                Physics2D.SyncTransforms();
                if (profile)
                {
                    lastEngineMs = (float)(
                        (syncStart - engineStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    lastSyncMs = (float)(
                        (System.Diagnostics.Stopwatch.GetTimestamp() - syncStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

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
