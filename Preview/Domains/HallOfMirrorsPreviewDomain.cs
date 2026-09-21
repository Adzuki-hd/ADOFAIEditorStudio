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
            }
        }

        public void EndSession()
        {
            keyframes.Clear();
            lastApplied = null;
        }
    }
}
