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
                new PlanetPreviewDomain()
            };
        }

        private static PreviewTimelineContext context;

        // 帧开销剖析，仅在 VerboseDiagnostics 打开时填充。
        private static readonly float[] lastDomainMs = new float[32];
        private static float lastSyncMs;
        private static int lastProfileFrame = -1000;

        /// <summary>
        /// 单独测量引擎自身的工作（校验、帧构建、域
        /// 派发、同步），与各域分开，这样帧开销的差距可以
        /// 被归因，而不是靠猜。
        /// </summary>
        private static void LogProfile()
        {
            if (Time.frameCount - lastProfileFrame < 60)
            {
                return;
            }
            lastProfileFrame = Time.frameCount;

            System.Text.StringBuilder builder = new System.Text.StringBuilder("[PreviewCost] ");
            float total = lastSyncMs;
            for (int i = 0; i < Domains.Count && i < lastDomainMs.Length; i++)
            {
                if (lastDomainMs[i] < 0.05f)
                {
                    continue;
                }
                total += lastDomainMs[i];
                builder.Append(Domains[i].Name).Append('=')
                    .Append(lastDomainMs[i].ToString("0.00")).Append("ms ");
            }
            builder.Append("sync=").Append(lastSyncMs.ToString("0.00"))
                .Append("ms domains=").Append(total.ToString("0.00"))
                .Append("ms engine=").Append(lastEngineMs.ToString("0.00"))
                .Append("ms validate=").Append(lastValidateMs.ToString("0.00"))
                .Append("ms.");
            Main.Logger.Log(builder.ToString());
        }

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
                PreviewEvaluationFrame frame = new PreviewEvaluationFrame(context, Mathf.Max(0f, time));
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
                    LogProfile();
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
