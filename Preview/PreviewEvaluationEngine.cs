using System;
using System.Collections.Generic;
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

            active = false;
            context = null;
        }
    }
}