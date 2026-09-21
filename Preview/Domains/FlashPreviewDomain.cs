using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class FlashPreviewDomain : IPreviewDomain
    {
        private sealed class FlashKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal Color StartColor;
            internal Color EndColor;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<FlashKeyframe> background = new List<FlashKeyframe>();
        private readonly List<FlashKeyframe> foreground = new List<FlashKeyframe>();
        private Renderer backgroundRenderer;
        private Renderer foregroundRenderer;
        private Camera flashCamera;

        public string Name
        {
            get { return "Flash"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            background.Clear();
            foreground.Clear();
            scrCamera camera = scrCamera.instance;
            backgroundRenderer = camera == null ? null : camera.flashPlusRendererBg;
            foregroundRenderer = camera == null ? null : camera.flashPlusRendererFg;
            flashCamera = camera == null ? null : camera.camobj;

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.Flash);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];

                Color startColor = ReadColor(levelEvent, "startColor", Color.white);
                Color endColor = ReadColor(levelEvent, "endColor", Color.clear);
                startColor.a = context.GetValue(levelEvent, "startOpacity", 100f) / 100f;
                endColor.a = context.GetValue(levelEvent, "endOpacity", 0f) / 100f;
                FlashKeyframe keyframe = new FlashKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    StartColor = startColor,
                    EndColor = endColor
                };

                FlashPlane plane = context.GetValue(levelEvent, "plane", FlashPlane.Foreground);
                (plane == FlashPlane.Background ? background : foreground).Add(keyframe);
            }

            PreviewStableSort.Sort(background, (left, right) => left.Start.CompareTo(right.Start));
            PreviewStableSort.Sort(foreground, (left, right) => left.Start.CompareTo(right.Start));
            ConfigureRenderer(backgroundRenderer, 10, "Default");
            ConfigureRenderer(foregroundRenderer, 0, "FgFlash");
            Main.Logger.Log("[StaticPreview/Flash] analyzed bg=" + background.Count
                + " fg=" + foreground.Count + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            Apply(backgroundRenderer, background, frame.Time, flashCamera);
            Apply(foregroundRenderer, foreground, frame.Time, flashCamera);
        }

        public void EndSession()
        {
            background.Clear();
            foreground.Clear();
            backgroundRenderer = null;
            foregroundRenderer = null;
        }

        private static void Apply(Renderer renderer, List<FlashKeyframe> keyframes, float time,
            Camera camera)
        {
            if (renderer == null)
            {
                return;
            }

            // 这一面每帧都要铺满相机的视口。原生的 FlashPlus
            // 也是这么做的（"ScaleXY(2 * orthoSize * aspect,
            // 2 * orthoSize)"）：这个 quad 是按单位尺寸做的，
            // 否则只能盖住屏幕的一小块。不做这一步，闪光仍然会
            // 着色，但只覆盖视口中间的一小片。
            ScalePlaneToCamera(renderer, camera);

            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, time);
            Color color = Color.clear;
            if (index >= 0)
            {
                FlashKeyframe keyframe = keyframes[index];
                float ratio = keyframe.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((time - (float)keyframe.Start) / keyframe.Duration);
                float eased = PreviewEase.EasedValue(0f, 1f, ratio, keyframe.Ease);
                color = Color.LerpUnclamped(keyframe.StartColor, keyframe.EndColor, eased);
            }

            if (renderer.material.color != color)
            {
                renderer.material.color = color;
            }
        }

        private static void ScalePlaneToCamera(Renderer renderer, Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            float height = 2f * camera.orthographicSize;
            float width = height * camera.aspect;
            Vector3 scale = renderer.transform.localScale;
            if (!Mathf.Approximately(scale.x, width) || !Mathf.Approximately(scale.y, height))
            {
                renderer.transform.localScale = new Vector3(width, height, scale.z);
            }
        }

        private static void ConfigureRenderer(Renderer renderer, int layer, string sortingLayer)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.gameObject.layer = layer;
            renderer.sortingLayerName = sortingLayer;
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