using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class ScreenTilePreviewDomain : IPreviewDomain
    {
        private sealed class ScreenTileKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal Vector2 Tile;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<ScreenTileKeyframe> keyframes = new List<ScreenTileKeyframe>();
        private ScreenTile component;

        public string Name
        {
            get { return "ScreenTile"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            scrCamera camera = scrCamera.instance;
            component = camera == null ? null : camera.GetComponent<ScreenTile>();
            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.ScreenTile);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];

                keyframes.Add(new ScreenTileKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Tile = context.GetValue(levelEvent, "tile", Vector2.one)
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            if (component != null)
            {
                component.enabled = true;
            }
            Main.Logger.Log("[StaticPreview/ScreenTile] analyzed " + keyframes.Count
                + " events; component=" + (component != null) + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            if (component == null)
            {
                return;
            }

            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            Vector2 oldValue = index <= 0 ? Vector2.one : keyframes[index - 1].Tile;
            Vector2 newValue = index < 0 ? Vector2.one : keyframes[index].Tile;
            float ratio = 1f;
            Ease ease = Ease.Linear;
            if (index >= 0)
            {
                ScreenTileKeyframe current = keyframes[index];
                ratio = current.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((frame.Time - (float)current.Start) / current.Duration);
                ease = current.Ease;
            }

            float eased = PreviewEase.EasedValue(0f, 1f, ratio, ease);
            Vector2 value = Vector2.LerpUnclamped(oldValue, newValue, eased);
            component.tileX = value.x;
            component.tileY = value.y;
        }

        public void EndSession()
        {
            keyframes.Clear();
            component = null;
        }
    }
}
