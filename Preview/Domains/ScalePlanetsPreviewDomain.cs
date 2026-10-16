using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// ScalePlanets —— 把行星整个放大或缩小。
    ///
    /// 原生 ffxScalePlanetsPlus 把 `scale`（**百分比**）除以 100 得到目标，
    /// 然后对 `planet.planetScale` 起一条 tween；真正画出来的大小是在
    /// scrPlanet.Update 里算的：`scrMisc.ScaleSymmetric2D(transform, 0.9f * planetScale)`。
    ///
    /// 预览期间行星自己的 Update 是关的，所以那一步没人替它做——不补的话
    /// `scale: 0`（开局把球缩没）这类事件完全看不到效果。
    ///
    /// 两条都要写：`planetScale` 是别的代码会读的源，transform 的 localScale
    /// 才是画面上真正的大小（Update 关着，没人从前者推到后者）。
    /// </summary>
    internal sealed class ScalePlanetsPreviewDomain : IPreviewDomain
    {
        /// <summary>原生 Update 里那个系数：画出来的大小 = 0.9 * planetScale。</summary>
        private const float BaseScale = 0.9f;

        /// <summary>没被任何事件改过时的 planetScale。</summary>
        private const float DefaultScale = 1f;

        private sealed class ScaleKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal string Target;
            /// <summary>这条事件**刚开始那一刻**的缩放值，不是上一条的目标值。</summary>
            internal float From;
            internal float To;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<ScaleKeyframe> keyframes = new List<ScaleKeyframe>();

        public string Name
        {
            get { return "ScalePlanets"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.ScalePlanets);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                keyframes.Add(new ScaleKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Target = context.GetValue(levelEvent, "targetPlanet", "All"),
                    // scale 是百分比：原生 Decode 写的是 GetFloat("scale") / 100。
                    To = context.GetValue(levelEvent, "scale", 100f) / 100f
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));

            // 每条事件的起点是**上一条在它开始那一刻的值**，不是上一条的目标值。
            // 原生每开一条新 tween 都先 `planet.scaleTween.Kill()`，于是新 tween
            // 从被掐断时的当前值起步。事件挨得密的时候，拿目标值当起点会跳一下。
            float previousFrom = DefaultScale;
            float previousTo = DefaultScale;
            double previousStart = double.NegativeInfinity;
            float previousDuration = 0f;
            Ease previousEase = Ease.Linear;

            for (int i = 0; i < keyframes.Count; i++)
            {
                ScaleKeyframe keyframe = keyframes[i];
                keyframe.From = previousDuration <= 0f
                    ? previousTo
                    : PreviewEase.EasedValue(previousFrom, previousTo,
                        Mathf.Clamp01((float)((keyframe.StartTime - previousStart) / previousDuration)),
                        previousEase);

                previousFrom = keyframe.From;
                previousTo = keyframe.To;
                previousStart = keyframe.StartTime;
                previousDuration = keyframe.Duration;
                previousEase = keyframe.Ease;
            }

            Main.Logger.Log("[StaticPreview/ScalePlanets] analyzed " + keyframes.Count + " events.");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            ScaleKeyframe current = index < 0 ? null : keyframes[index];

            // 一条事件都没开始过就是原始大小。
            if (current == null)
            {
                ApplyAll(DefaultScale);
                return;
            }

            float scale = PreviewEase.EasedValue(current.From, current.To, Ratio(current, frame.Time), current.Ease);
            Apply(current.Target, scale);
        }

        public void EndSession()
        {
            keyframes.Clear();
            // 还回去，否则关掉工作台之后球还是被缩着的。
            ApplyAll(DefaultScale);
        }

        private static float Ratio(ScaleKeyframe keyframe, double time)
        {
            return keyframe.Duration <= 0f
                ? 1f
                : Mathf.Clamp01((float)((time - keyframe.StartTime) / keyframe.Duration));
        }

        private static void Apply(string target, float scale)
        {
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                PlanetarySystem system = player.planetarySystem;
                if (IsTarget(target, "FirePlanet"))
                {
                    SetPlanetScale(system.planetRed, scale);
                }
                else if (IsTarget(target, "IcePlanet"))
                {
                    SetPlanetScale(system.planetBlue, scale);
                }
                else if (IsTarget(target, "GreenPlanet"))
                {
                    SetPlanetScale(system.planetGreen, scale);
                }
                else
                {
                    if (system.planetList == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < system.planetList.Count; j++)
                    {
                        SetPlanetScale(system.planetList[j], scale);
                    }
                }
            }
        }

        private static void ApplyAll(float scale)
        {
            Apply("All", scale);
        }

        private static bool IsTarget(string value, string expected)
        {
            return string.Equals(value, expected, System.StringComparison.OrdinalIgnoreCase);
        }

        private static void SetPlanetScale(scrPlanet planet, float scale)
        {
            if (planet == null)
            {
                return;
            }

            planet.planetScale = scale;

            // 抄 scrMisc.ScaleSymmetric2D：只改 x/y，z 保持原样。
            Vector3 local = planet.transform.localScale;
            planet.transform.localScale = new Vector3(BaseScale * scale, BaseScale * scale, local.z);
        }
    }
}
