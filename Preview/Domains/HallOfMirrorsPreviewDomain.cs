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

            scrController controller = scrController.instance;
            if (controller == null)
            {
                return;
            }

            if (!lastApplied.HasValue || lastApplied.Value != enabled)
            {
                controller.EnableHallOfMirrors(enabled);
                lastApplied = enabled;
            }

            // **和原版对齐：镜厅开着时背景相机不擦颜色（Depth）。**
            //
            // 之前为了让拖动不出现残影，这里无条件强制 Color —— 那等于把镜厅
            // 关掉了，整屏亮度/颜色都和原版对不上（用户实测：原版关掉镜厅后
            // 颜色才和 AES 一样）。
            //
            // 现在的折中：
            //   · 拖动播放头 / 跳转那一类随机访问 → Color，每帧从干净缓冲开始；
            //   · 正常播放 → Depth，保留原版镜厅的累积效果。
            // 镜厅本来就是随时间累积的视觉效果，逐帧播放才是它该有的样子。
            scrCamera camera = scrCamera.instance;
            if (camera != null && camera.Bgcamstatic != null)
            {
                bool scrubbing = EditorPlaybackBridge.IsTimelineScrubActive;
                UnityEngine.CameraClearFlags wanted = enabled && !scrubbing
                    ? UnityEngine.CameraClearFlags.Depth
                    : UnityEngine.CameraClearFlags.Color;
                if (camera.Bgcamstatic.clearFlags != wanted)
                {
                    camera.Bgcamstatic.clearFlags = wanted;
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
