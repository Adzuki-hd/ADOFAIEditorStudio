using System;
using System.Collections.Generic;

namespace ADOFAIEditorStudio.Preview
{
    /// <summary>
    /// 负责一个同场景预览会话。
    ///
    /// 后端刻意不提供 Play()/Stop()。它只分析
    /// 当前编辑器场景，在绝对时间轴时间上对可视域求值。
    /// 原生播放仍是独立的一套播放控制，供播放按钮使用。
    /// </summary>
    internal sealed class EvaluatorPreviewBackend
    {
        private static readonly PreviewDomainCoverage[] Coverage =
        {
            new PreviewDomainCoverage("Planet", PreviewDomainState.Active,
                "two-planet floors are evaluated directly; complex floors use a native floor checkpoint"),
            new PreviewDomainCoverage("Camera", PreviewDomainState.Active,
                "MoveCamera position, rotation, zoom and easing"),
            new PreviewDomainCoverage("Flash", PreviewDomainState.Active,
                "background and foreground flash planes"),
            new PreviewDomainCoverage("Bloom", PreviewDomainState.Active,
                "VideoBloom parameters and easing"),
            new PreviewDomainCoverage("CustomBackground", PreviewDomainState.Active,
                "background color/image state"),
            new PreviewDomainCoverage("ScreenTile", PreviewDomainState.Active,
                "screen tiling and easing"),
            new PreviewDomainCoverage("ScreenScroll", PreviewDomainState.Active,
                "screen scroll offset"),
            new PreviewDomainCoverage("HallOfMirrors", PreviewDomainState.Active,
                "native controller toggle"),
            new PreviewDomainCoverage("RecolorTrack", PreviewDomainState.Active,
                "per-floor FloorRenderer color/style/pulse evaluation"),
            new PreviewDomainCoverage("MoveTrack", PreviewDomainState.Active,
                "per-floor transform, scale and opacity evaluation"),
            new PreviewDomainCoverage("Filter", PreviewDomainState.Active,
                "reflection-backed SetFilter component and common intensity mappings"),
            new PreviewDomainCoverage("FilterAdvanced", PreviewDomainState.Active,
                "native first-pass filter components, public field snapshots and absolute-time easing"),
            new PreviewDomainCoverage("Video", PreviewDomainState.Planned,
                "requires deterministic VideoPlayer frame seeking"),
            new PreviewDomainCoverage("DecorationCore", PreviewDomainState.Active,
                "tag-matched MoveDecorations position, pivot, rotation, scale, opacity and color"),
            new PreviewDomainCoverage("DecorationImage", PreviewDomainState.Planned,
                "requires native sprite/mask refresh"),
            new PreviewDomainCoverage("DecorationText", PreviewDomainState.Planned,
                "requires native text decoration refresh"),
            new PreviewDomainCoverage("DecorationObjectParticle", PreviewDomainState.Planned,
                "particle/object history is not yet deterministic"),
        };

        private bool active;
        private int editorInstanceId;
        private float lastEvaluatedTime = float.NaN;

        internal bool IsActive
        {
            get { return active; }
        }

        internal int ActiveDomainCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Coverage.Length; i++)
                {
                    if (Coverage[i].State == PreviewDomainState.Active)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        internal int DomainCount
        {
            get { return Coverage.Length; }
        }

        internal float LastEvaluatedTime
        {
            get { return lastEvaluatedTime; }
        }

        internal IList<PreviewDomainCoverage> GetCoverage()
        {
            return Coverage;
        }

        internal bool TryBegin(scnEditor editor, out string error)
        {
            error = null;
            if (editor == null)
            {
                error = "Editor timeline is not ready.";
                return false;
            }

            if (active && editor.GetInstanceID() == editorInstanceId)
            {
                return true;
            }

            End();
            if (!PreviewEvaluationEngine.TryBegin(editor, out error))
            {
                return false;
            }

            active = true;
            editorInstanceId = editor.GetInstanceID();
            lastEvaluatedTime = float.NaN;
            Main.Logger.Log("[Evaluator] session ready: " + ActiveDomainCount + "/"
                + DomainCount + " active domains; " + GetPlannedDomainCount() + " planned.");
            return true;
        }

        internal bool TryEvaluate(float time, out string error)
        {
            error = null;
            scnEditor editor = scnEditor.instance;
            if (!active || editor == null || editor.GetInstanceID() != editorInstanceId)
            {
                error = "Evaluator preview session is not active.";
                return false;
            }

            float targetTime = Math.Max(0f, time);
            if (!PreviewEvaluationEngine.TryEvaluate(targetTime, out error))
            {
                // 快照为每个格、事件、装饰物各缓存一个引用，因此任何
                // 增删其中一项的编辑都会让它失效——一个从
                // 媒体面板拖出的装饰物、一次原生 Ctrl+Z、在游戏自带面板里删除。
                // 求值器只能通过 MatchesCurrentEditor 察觉，随后拒绝
                // 运行；而没有任何东西会自行重建它，于是预览
                // 永久卡死，尽管编辑本身已经正确应用。
                //
                // 在这里重新分析，能让这类编辑自我修复，而不必依赖
                // 每个调用点自己记得去请求。时钟不受影响：只重建
                // 缓存上下文，播放头仍停在原位。
                if (!PreviewEvaluationEngine.IsStaleSnapshot(error))
                {
                    return false;
                }

                // 媒体拖放在进行中有意让快照失效——
                // 它刚加了一个装饰物并直接驱动它。拖动中途重建会
                // 用还没调好的值重新种下该装饰物的基线，并让
                // 拖动卡顿；拖放结束时它自己会请求重建。
                if (EditorSceneInteraction.IsMediaDropActive)
                {
                    return false;
                }

                if (!PreviewEvaluationEngine.TryBegin(editor, out string rebuildError))
                {
                    error = "Rebuild after an edit failed: " + rebuildError;
                    return false;
                }

                Main.Logger.Log("[EvaluatorOwned] snapshot rebuilt after a level edit.");
                if (!PreviewEvaluationEngine.TryEvaluate(targetTime, out error))
                {
                    return false;
                }
            }

            lastEvaluatedTime = targetTime;
            return true;
        }

        internal void End()
        {
            if (active)
            {
                PreviewEvaluationEngine.End();
            }

            active = false;
            editorInstanceId = 0;
            lastEvaluatedTime = float.NaN;
        }

        private int GetPlannedDomainCount()
        {
            int count = 0;
            for (int i = 0; i < Coverage.Length; i++)
            {
                if (Coverage[i].State == PreviewDomainState.Planned)
                {
                    count++;
                }
            }

            return count;
        }
    }
}