using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class ScreenScrollPreviewDomain : IPreviewDomain
    {
        private sealed class ScreenScrollKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal Vector2 Scroll;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<ScreenScrollKeyframe> keyframes = new List<ScreenScrollKeyframe>();
        private ScreenScroll component;

        public string Name
        {
            get { return "ScreenScroll"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            scrCamera camera = scrCamera.instance;
            component = camera == null ? null : camera.GetComponent<ScreenScroll>();
            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.ScreenScroll);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                keyframes.Add(new ScreenScrollKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Scroll = context.GetValue(levelEvent, "scroll", Vector2.zero) / 100f
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            if (component != null)
            {
                component.scrollSpeed = Vector2.zero;
                component.scrollOffset = Vector2.zero;
                component.enabled = true;
            }
            Main.Logger.Log("[StaticPreview/ScreenScroll] analyzed " + keyframes.Count
                + " events; component=" + (component != null) + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            if (component == null)
            {
                return;
            }

            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            if (index < 0 || keyframes[index].Scroll == Vector2.zero)
            {
                component.scrollOffset = Vector2.zero;
                return;
            }

            ScreenScrollKeyframe current = keyframes[index];
            component.scrollOffset = (frame.Time - (float)current.Start) * current.Scroll;
        }

        public void EndSession()
        {
            keyframes.Clear();
            component = null;
        }
    }
}
