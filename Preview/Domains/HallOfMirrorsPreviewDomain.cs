using System.Collections.Generic;
using ADOFAI;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class HallOfMirrorsPreviewDomain : IPreviewDomain
    {
        private sealed class HallKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal bool Enabled;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<HallKeyframe> keyframes = new List<HallKeyframe>();
        private bool? lastApplied;

        public string Name
        {
            get { return "HallOfMirrors"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            lastApplied = null;
            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.HallOfMirrors);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                keyframes.Add(new HallKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Enabled = context.GetValue(levelEvent, "enabled", false)
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            Main.Logger.Log("[StaticPreview/HallOfMirrors] analyzed " + keyframes.Count + " events.");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            bool enabled = index >= 0 && keyframes[index].Enabled;
            if (lastApplied.HasValue && lastApplied.Value == enabled)
            {
                return;
            }

            scrController controller = scrController.instance;
            if (controller != null)
            {
                controller.EnableHallOfMirrors(enabled);
                lastApplied = enabled;

                // **预览里不保留"不擦背景"这件事。**
                //
                // 原生的 HallOfMirrors 只有一个副作用：
                //
                //   Bgcamstatic.clearFlags = enabled ? Depth : Color;
                //
                // 也就是开着的时候，背景相机**不擦颜色缓冲**，于是上一帧还没
                // 被覆盖的像素留在屏幕上 —— 那正是"镜厅"无限反射的错觉来源。
                // 它是**随时间累积**的效果，游戏里逐帧播放没问题；但工作台是
                // 随机访问的求值器：拖到某一刻时缓冲区里留的是"上一次显示过的
                // 画面"，于是表现为「上一帧以半透明形式叠在现在这一帧上」，
                // 拖动时还会闪。用户实测：删掉前面的镜厅事件，重影立刻消失。
                //
                // 所以记录状态归记录，渲染这边强制回到 Color：预览每帧都从
                // 干净的颜色缓冲开始。
                scrCamera camera = scrCamera.instance;
                if (camera != null && camera.Bgcamstatic != null)
                {
                    camera.Bgcamstatic.clearFlags = UnityEngine.CameraClearFlags.Color;
                }
            }
        }

        public void EndSession()
        {
            keyframes.Clear();
            lastApplied = null;
        }
    }
}
