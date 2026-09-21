using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class BloomPreviewDomain : IPreviewDomain
    {
        private sealed class BloomKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal bool Enabled;
            internal float Threshold;
            internal float Intensity;
            internal Color Color;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<BloomKeyframe> keyframes = new List<BloomKeyframe>();
        private VideoBloom component;

        public string Name
        {
            get { return "Bloom"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            scrCamera camera = scrCamera.instance;
            component = camera == null ? null : camera.transform.GetComponent<VideoBloom>();

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.Bloom);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];

                keyframes.Add(new BloomKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Enabled = context.GetValue(levelEvent, "enabled", false),
                    Threshold = context.GetValue(levelEvent, "threshold", 50f) / 100f,
                    Intensity = context.GetValue(levelEvent, "intensity", 100f) / 100f,
                    Color = ReadColor(levelEvent, "color", Color.white)
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            Main.Logger.Log("[StaticPreview/Bloom] analyzed " + keyframes.Count
                + " events; component=" + (component != null) + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            if (component == null)
            {
                return;
            }

            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            BloomKeyframe previous = index <= 0 ? null : keyframes[index - 1];
            BloomKeyframe current = index < 0 ? null : keyframes[index];

            if (current == null)
            {
                component.enabled = false;
                component.Threshold = 0.5f;
                component.MasterAmount = 1f;
                component.Tint = Color.white;
                return;
            }

            float oldThreshold = previous == null ? 0.5f : previous.Threshold;
            float oldIntensity = previous == null ? 1f : previous.Intensity;
            Color oldColor = previous == null ? Color.white : previous.Color;
            float ratio = DurationRatio((float)current.Start, current.Duration, frame.Time);
            component.enabled = current.Enabled;
            component.Threshold = PreviewEase.EasedValue(oldThreshold, current.Threshold, ratio, current.Ease);
            component.MasterAmount = PreviewEase.EasedValue(oldIntensity, current.Intensity, ratio, current.Ease);
            component.Tint = Color.LerpUnclamped(oldColor, current.Color,
                PreviewEase.EasedValue(0f, 1f, ratio, current.Ease));
        }

        public void EndSession()
        {
            keyframes.Clear();
            component = null;
        }

        private static float DurationRatio(float start, float duration, float time)
        {
            return duration <= 0f ? 1f : Mathf.Clamp01((time - start) / duration);
        }

        private static Color ReadColor(PreviewEventSnapshot levelEvent, string key, Color fallback)
        {
            try
            {
                return levelEvent.GetColor(key);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
