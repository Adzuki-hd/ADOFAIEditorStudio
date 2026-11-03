using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// `SetObject` —— 改**对象装饰物**的外观。
    ///
    /// 对照原生 `ffxSetObjectPlus.StartEffect`：它按 tag 找到
    /// `scrObjectDecoration`，然后看 `dec.objectType` 分两支：
    ///
    ///   · `Planet` —— 改球的颜色、尾焰颜色
    ///   · `Floor`  —— 改轨道角度、颜色、透明度、样式、图标
    ///
    /// 每个属性都配一个 `xxxUsed`，值来自事件的 `disabled` 表
    /// （`planetColorUsed = !evnt.disabled["planetColor"]`）—— 也就是参数栏
    /// 那个勾选框。勾掉表示"这条事件不动这个属性"，要沿用上一条的值。
    ///
    /// **为什么这个域以前不存在**：`scrObjectDecoration` 继承自 `scrDecoration`，
    /// 位置 / 缩放 / 旋转 / 透明度本来就被装饰物域驱动，所以对象装饰物能显示、
    /// 也能被 `MoveDecorations` 移动 —— 只是 `SetObject` 特有的那几项外观完全没接。
    /// 表现就是"对象装饰物出现了，但改它颜色的那条事件没反应"。
    /// </summary>
    internal sealed class SetObjectPreviewDomain : IPreviewDomain
    {
        /// <summary>一条 SetObject 事件在某一时刻算出来的样子。</summary>
        private sealed class ObjectKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal string TargetTags;

            // ---- Planet ----
            internal bool PlanetColorUsed;
            internal Color PlanetColor;
            internal bool PlanetTailColorUsed;
            internal Color PlanetTailColor;

            // ---- Floor ----
            internal bool TrackAngleUsed;
            internal float TrackAngle;
            internal bool TrackColorTypeUsed;
            internal FloorDecorationColorType TrackColorType;
            internal bool TrackColorUsed;
            internal Color TrackColor;
            internal bool SecondaryTrackColorUsed;
            internal Color SecondaryTrackColor;
            internal bool TrackColorAnimDurationUsed;
            internal float TrackColorAnimDuration;
            internal bool TrackOpacityUsed;
            internal float TrackOpacity;
            internal bool TrackStyleUsed;
            internal TrackStyle TrackStyle;
            internal bool TrackIconUsed;
            internal CustomFloorIcon TrackIcon;
            internal bool TrackIconAngleUsed;
            internal float TrackIconAngle;
            internal bool TrackIconFlippedUsed;
            internal bool TrackIconFlipped;

            public double Start
            {
                get { return StartTime; }
            }
        }

        /// <summary>某个装饰物上已经应用过的值，用来做脏值跳过。</summary>
        private sealed class AppliedState
        {
            internal bool HasPlanet;
            internal Color PlanetColor;
            internal Color PlanetTailColor;
            internal bool HasFloor;
            internal Color FloorColor;
            internal float FloorOpacity;
            internal TrackStyle FloorStyle;
            internal CustomFloorIcon FloorIcon;
            internal float FloorIconAngle;
            internal bool FloorIconFlipped;
        }

        private readonly List<ObjectKeyframe> keyframes = new List<ObjectKeyframe>();
        private readonly Dictionary<scrObjectDecoration, AppliedState> applied =
            new Dictionary<scrObjectDecoration, AppliedState>();
        private int lastAppliedIndex = int.MinValue;

        public string Name
        {
            get { return "SetObject"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            applied.Clear();
            lastAppliedIndex = int.MinValue;

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.SetObject);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                keyframes.Add(new ObjectKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    TargetTags = context.GetValue(levelEvent, "targetTags", string.Empty),

                    PlanetColorUsed = context.IsPropertyEnabled(levelEvent, "planetColor"),
                    PlanetColor = ReadColor(context, levelEvent, "planetColor", Color.white),
                    PlanetTailColorUsed = context.IsPropertyEnabled(levelEvent, "planetTailColor"),
                    PlanetTailColor = ReadColor(context, levelEvent, "planetTailColor", Color.white),

                    TrackAngleUsed = context.IsPropertyEnabled(levelEvent, "trackAngle"),
                    TrackAngle = context.GetValue(levelEvent, "trackAngle", 0f),
                    TrackColorTypeUsed = context.IsPropertyEnabled(levelEvent, "trackColorType"),
                    TrackColorType = context.GetValue(levelEvent, "trackColorType",
                        FloorDecorationColorType.Single),
                    TrackColorUsed = context.IsPropertyEnabled(levelEvent, "trackColor"),
                    TrackColor = ReadColor(context, levelEvent, "trackColor", Color.white),
                    SecondaryTrackColorUsed = context.IsPropertyEnabled(levelEvent, "secondaryTrackColor"),
                    SecondaryTrackColor = ReadColor(context, levelEvent, "secondaryTrackColor", Color.white),
                    TrackColorAnimDurationUsed = context.IsPropertyEnabled(levelEvent, "trackColorAnimDuration"),
                    TrackColorAnimDuration = context.GetValue(levelEvent, "trackColorAnimDuration", 2f),
                    TrackOpacityUsed = context.IsPropertyEnabled(levelEvent, "trackOpacity"),
                    TrackOpacity = context.GetValue(levelEvent, "trackOpacity", 100f),
                    TrackStyleUsed = context.IsPropertyEnabled(levelEvent, "trackStyle"),
                    TrackStyle = context.GetValue(levelEvent, "trackStyle", TrackStyle.Standard),
                    TrackIconUsed = context.IsPropertyEnabled(levelEvent, "trackIcon"),
                    TrackIcon = context.GetValue(levelEvent, "trackIcon", CustomFloorIcon.None),
                    TrackIconAngleUsed = context.IsPropertyEnabled(levelEvent, "trackIconAngle"),
                    TrackIconAngle = context.GetValue(levelEvent, "trackIconAngle", 0f),
                    TrackIconFlippedUsed = context.IsPropertyEnabled(levelEvent, "trackIconFlipped"),
                    TrackIconFlipped = context.GetValue(levelEvent, "trackIconFlipped", false)
                });
            }

            PreviewStableSort.Sort(keyframes,
                (left, right) => left.Start.CompareTo(right.Start));
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            if (keyframes.Count == 0)
            {
                return;
            }

            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);

            // 同一段里、同一时刻只跑一次。逐帧重复跑没有意义 ——
            // 这些属性是离散的目标值，不是插值量。
            if (index == lastAppliedIndex)
            {
                return;
            }
            lastAppliedIndex = index;

            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || manager.allDecorations == null)
            {
                return;
            }

            // 收集所有在 index 之前、且对自己的目标 tag 仍然生效的 keyframe。
            //
            // 为什么要往前扫而不是只取 index 那一条：这些属性是"逐属性持久"的
            // —— 一条只改球颜色的事件之后，某条更早、改了轨道透明度的事件
            // 对透明度依然有效。原生也是这个语义（每个属性各自一条 tween，
     	    // 没被新事件碰过的保持原样）。
            for (int d = 0; d < manager.allDecorations.Count; d++)
            {
                scrObjectDecoration decoration = manager.allDecorations[d] as scrObjectDecoration;
                if (decoration == null)
                {
                    continue;
                }
                ApplyFor(decoration, index);
            }
        }

        /// <summary>
        /// 把「到 index 为止仍生效」的那组属性应用到一件对象装饰物上。
        /// </summary>
        private void ApplyFor(scrObjectDecoration decoration, int index)
        {
            AppliedState state;
            if (!applied.TryGetValue(decoration, out state))
            {
                state = new AppliedState();
                applied[decoration] = state;
            }

            // 从最近一条开始往前找，每个属性取**第一条碰它的**事件。
            bool planetColorDone = false;
            bool tailColorDone = false;
            bool trackAngleDone = false;
            bool trackColorDone = false;
            bool trackOpacityDone = false;
            bool trackStyleDone = false;
            bool trackIconDone = false;
            bool trackIconAngleDone = false;
            bool trackIconFlippedDone = false;

            Color planetColor = default(Color);
            Color tailColor = default(Color);
            float trackAngle = 0f;
            Color trackColor = default(Color);
            Color secondaryColor = default(Color);
            FloorDecorationColorType colorType = FloorDecorationColorType.Single;
            float colorAnim = 2f;
            float trackOpacity = 100f;
            TrackStyle style = TrackStyle.Standard;
            CustomFloorIcon icon = CustomFloorIcon.None;
            float iconAngle = 0f;
            bool iconFlipped = false;

            for (int i = index; i >= 0; i--)
            {
                ObjectKeyframe key = keyframes[i];
                if (!Targets(decoration, key.TargetTags))
                {
                    continue;
                }

                if (!planetColorDone && key.PlanetColorUsed)
                {
                    planetColorDone = true;
                    planetColor = key.PlanetColor;
                }
                if (!tailColorDone && key.PlanetTailColorUsed)
                {
                    tailColorDone = true;
                    tailColor = key.PlanetTailColor;
                }
                if (!trackAngleDone && key.TrackAngleUsed)
                {
                    trackAngleDone = true;
                    trackAngle = key.TrackAngle;
                }
                if (!trackColorDone
                    && (key.TrackColorUsed || key.SecondaryTrackColorUsed))
                {
                    trackColorDone = true;
                    trackColor = key.TrackColor;
                    secondaryColor = key.SecondaryTrackColor;
                    colorType = key.TrackColorTypeUsed
                        ? key.TrackColorType : FloorDecorationColorType.Single;
                    colorAnim = key.TrackColorAnimDurationUsed
                        ? key.TrackColorAnimDuration : 2f;
                }
                if (!trackOpacityDone && key.TrackOpacityUsed)
                {
                    trackOpacityDone = true;
                    trackOpacity = key.TrackOpacity;
                }
                if (!trackStyleDone && key.TrackStyleUsed)
                {
                    trackStyleDone = true;
                    style = key.TrackStyle;
                }
                if (!trackIconDone && key.TrackIconUsed)
                {
                    trackIconDone = true;
                    icon = key.TrackIcon;
                }
                if (!trackIconAngleDone && key.TrackIconAngleUsed)
                {
                    trackIconAngleDone = true;
                    iconAngle = key.TrackIconAngle;
                }
                if (!trackIconFlippedDone && key.TrackIconFlippedUsed)
                {
                    trackIconFlippedDone = true;
                    iconFlipped = key.TrackIconFlipped;
                }

                if (planetColorDone && tailColorDone && trackAngleDone
                    && trackColorDone && trackOpacityDone && trackStyleDone
                    && trackIconDone && trackIconAngleDone && trackIconFlippedDone)
                {
                    break;
                }
            }

            try
            {
                switch (decoration.objectType)
                {
                    case ObjectDecorationType.Planet:
                        if (planetColorDone
                            && (!state.HasPlanet
                                || !Approximately(state.PlanetColor, planetColor)))
                        {
                            decoration.SetPlanetColor(planetColor);
                            state.PlanetColor = planetColor;
                        }
                        if (tailColorDone
                            && (!state.HasPlanet
                                || !Approximately(state.PlanetTailColor, tailColor)))
                        {
                            decoration.SetPlanetTailColor(tailColor);
                            state.PlanetTailColor = tailColor;
                        }
                        state.HasPlanet = true;
                        break;

                    case ObjectDecorationType.Floor:
                        if (trackColorDone)
                        {
                            decoration.SetFloorColor(trackColor, secondaryColor,
                                colorType, colorAnim, 0f, Ease.Linear);
                            state.FloorColor = trackColor;
                        }
                        if (trackOpacityDone
                            && (!state.HasFloor
                                || Mathf.Abs(state.FloorOpacity - trackOpacity) > 0.01f))
                        {
                            // 谱面里 trackOpacity 是 0~100 的百分比。
                            decoration.SetFloorOpacity(trackOpacity / 100f);
                            state.FloorOpacity = trackOpacity;
                        }
                        if (trackStyleDone && (!state.HasFloor || state.FloorStyle != style))
                        {
                            decoration.SetFloorStyle(style);
                            state.FloorStyle = style;
                        }
                        if (trackIconDone && (!state.HasFloor || state.FloorIcon != icon))
                        {
                            decoration.SetFloorIcon(icon);
                            state.FloorIcon = icon;
                        }
                        if (trackIconAngleDone
                            && (!state.HasFloor
                                || Mathf.Abs(state.FloorIconAngle - iconAngle) > 0.01f))
                        {
                            decoration.SetFloorIconAngle(iconAngle);
                            state.FloorIconAngle = iconAngle;
                        }
                        if (trackIconFlippedDone
                            && (!state.HasFloor || state.FloorIconFlipped != iconFlipped))
                        {
                            decoration.SetFloorIconFlipped(iconFlipped);
                            state.FloorIconFlipped = iconFlipped;
                        }
                        state.HasFloor = true;
                        break;
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/SetObject] 应用失败 tag="
                    + (decoration.tag ?? "?") + "：" + exception.Message);
            }
        }

        /// <summary>这条事件的 targetTags 是否包含这件装饰物的 tag。</summary>
        private static bool Targets(scrObjectDecoration decoration, string targetTags)
        {
            if (string.IsNullOrEmpty(targetTags))
            {
                return false;
            }
            string tag = decoration.tag;
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }
            // 多 tag 用逗号分隔（和 MoveDecorations 那套一致）。
            string[] parts = targetTags.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i].Trim(), tag.Trim(), StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static Color ReadColor(PreviewTimelineContext context,
            PreviewEventSnapshot levelEvent, string key, Color fallback)
        {
            try
            {
                return context.GetValue(levelEvent, key, fallback);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool Approximately(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) <= 0.002f
                && Mathf.Abs(a.g - b.g) <= 0.002f
                && Mathf.Abs(a.b - b.b) <= 0.002f
                && Mathf.Abs(a.a - b.a) <= 0.002f;
        }

        public void EndSession()
        {
            keyframes.Clear();
            applied.Clear();
            lastAppliedIndex = int.MinValue;
        }
    }
}
