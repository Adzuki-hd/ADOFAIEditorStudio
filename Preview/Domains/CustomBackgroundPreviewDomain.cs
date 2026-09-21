using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class CustomBackgroundPreviewDomain : IPreviewDomain
    {
        private sealed class BackgroundKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal Color Color;
            internal string Image;
            internal Color ImageColor;
            internal Vector2 Parallax;
            internal BgDisplayMode DisplayMode;
            internal bool ImageSmoothing;
            internal bool LockRotation;
            internal bool Loop;
            internal float ScalingRatio;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<BackgroundKeyframe> keyframes = new List<BackgroundKeyframe>();
        private BackgroundKeyframe defaultState;
        private int lastAppliedIndex = int.MinValue;

        public string Name
        {
            get { return "CustomBackground"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            lastAppliedIndex = int.MinValue;
            LevelData levelData = scnGame.instance == null ? null : scnGame.instance.levelData;
            defaultState = CreateDefault(levelData);

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.CustomBackground);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];

                keyframes.Add(new BackgroundKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Color = ReadColor(levelEvent, "color", defaultState.Color),
                    Image = context.GetValue(levelEvent, "bgImage", string.Empty),
                    ImageColor = ReadColor(levelEvent, "imageColor", Color.white),
                    Parallax = context.GetValue(levelEvent, "parallax", Vector2.zero) / 100f,
                    DisplayMode = context.GetValue(levelEvent, "bgDisplayMode", BgDisplayMode.FitToScreen),
                    ImageSmoothing = context.GetValue(levelEvent, "imageSmoothing", true),
                    LockRotation = context.GetValue(levelEvent, "lockRot", false),
                    Loop = context.GetValue(levelEvent, "loopBG", true),
                    ScalingRatio = context.GetValue(levelEvent, "scalingRatio", 100f) / 100f
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            Main.Logger.Log("[StaticPreview/CustomBackground] analyzed " + keyframes.Count + " events.");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            if (index == lastAppliedIndex)
            {
                return;
            }

            if (Apply(index < 0 ? defaultState : keyframes[index]))
            {
                lastAppliedIndex = index;
            }
        }

        public void EndSession()
        {
            keyframes.Clear();
            defaultState = null;
            lastAppliedIndex = int.MinValue;
        }

        private static bool Apply(BackgroundKeyframe state)
        {
            scnGame game = scnGame.instance;
            scrCamera camera = scrCamera.instance;
            scnGame customLevel = ADOBase.customLevel;
            if (state == null || game == null || camera == null || customLevel == null
                || customLevel.custBG == null || customLevel.imgHolder == null)
            {
                return false;
            }

            camera.Bgcamstatic.backgroundColor = state.Color;
            scrCustomBackgroundSprite customBackground = customLevel.custBG;
            if (string.IsNullOrEmpty(state.Image))
            {
                customBackground.SetCustomBG(null, Color.white, false, true, true, 1f, false, true);
                if (game.editorBG != null)
                {
                    game.editorBG.SetActive(string.IsNullOrEmpty(game.levelData.bgVideo)
                        && game.levelData.bgShowDefaultBGIfNoImage);
                }
                return true;
            }

            TextureManager.CustomSprite sprite;
            if (!customLevel.imgHolder.customSprites.TryGetValue(state.Image, out sprite) || sprite == null)
            {
                return false;
            }

            customBackground.SetCustomBG(sprite, state.ImageColor,
                state.DisplayMode == BgDisplayMode.Tiled,
                state.Loop,
                state.DisplayMode == BgDisplayMode.FitToScreen,
                state.ScalingRatio,
                state.LockRotation,
                state.ImageSmoothing);
            customBackground.parallax.multiplier_x = state.Parallax.x;
            customBackground.parallax.multiplier_y = state.Parallax.y;
            if (game.editorBG != null)
            {
                game.editorBG.SetActive(false);
            }
            return true;
        }

        private static BackgroundKeyframe CreateDefault(LevelData levelData)
        {
            if (levelData == null)
            {
                return new BackgroundKeyframe
                {
                    StartTime = float.NegativeInfinity,
                    Color = Color.black,
                    Image = string.Empty,
                    ImageColor = Color.white,
                    Parallax = Vector2.zero,
                    DisplayMode = BgDisplayMode.FitToScreen,
                    ImageSmoothing = true,
                    Loop = true,
                    ScalingRatio = 1f
                };
            }

            BgDisplayMode displayMode = levelData.backgroundSettings == null
                ? BgDisplayMode.FitToScreen
                : levelData.backgroundSettings.Get<BgDisplayMode>("bgDisplayMode", BgDisplayMode.FitToScreen);
            return new BackgroundKeyframe
            {
                StartTime = float.NegativeInfinity,
                Color = levelData.backgroundColor,
                Image = levelData.bgImage,
                ImageColor = levelData.bgImageColor,
                Parallax = levelData.bgParallax,
                DisplayMode = displayMode,
                ImageSmoothing = levelData.bgSmoothing,
                LockRotation = levelData.bgLockRot,
                Loop = levelData.bgLooping,
                ScalingRatio = levelData.scalingRatio
            };
        }

        private static Color ReadColor(PreviewEventSnapshot levelEvent, string key, Color fallback)
        {
            try
            {
                return levelEvent.GetColor(key, fallback);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
