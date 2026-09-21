using System;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 按绝对时间驱动的屏幕抖动。
    ///
    /// 偏移量由游戏用的那个 DOTween.Shake 本身产生
    /// （ffxShakeScreenPlus: "DOTween.Shake(() => cam.shake, x => cam.shake = x *
    /// multiplier, duration, strength, RoundToInt(20 * intensity), 90f, false, fadeOut)"）。
    /// 复用它是对齐波形的唯一办法：自己手写一个替代品，
    /// 无论推导得多仔细，都是另一条曲线。早期版本从
    /// Random.value * PI 里取方向，这会把竖直分量限制在
    /// sin >= 0，让相机偏向一侧——在屏幕上看起来就是视图
    /// 永久偏低。
    ///
    /// Shake 是有状态的，所以这里造一个一次性 tween，跳到指定
    /// 时间，然后杀掉。这样域就保持为绝对时间的纯函数，
    /// 时间轴才能双向拖动并自由重算。
    /// </summary>
    internal sealed class ShakeScreenPreviewDomain : IPreviewDomain
    {
        private sealed class ShakeKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal float Strength;
            internal float Intensity;
            internal bool FadeOut;

            /// <summary>
            /// 抖动 tween，构建一次后反复采样，同时保存
            /// 它的 setter 最后写入的偏移。
            ///
            /// DOTween.Shake 在构造时就生成整条随机路径
            /// （"num8 = Random.Range(0, 360); ... num8 += Random.Range(-randomness,
            /// randomness)"）。因此每次采样都新建 tween，会每帧走
            /// 一条不同的随机路径，相邻两帧的方向对不上
            /// ——低强度抖动读起来像剧烈乱抖。每个事件一个
            /// tween，才能保持单条连续路径。
            /// </summary>
            internal Tween Tween;
            internal Vector3 LastValue;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<ShakeKeyframe> keyframes = new List<ShakeKeyframe>();

        public string Name
        {
            get { return "ShakeScreen"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.ShakeScreen);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                keyframes.Add(new ShakeKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Strength = context.GetValue(levelEvent, "strength", 100f) / 100f,
                    Intensity = context.GetValue(levelEvent, "intensity", 100f) / 100f,
                    FadeOut = context.GetValue(levelEvent, "fadeOut", false)
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            Main.Logger.Log("[StaticPreview/ShakeScreen] analyzed " + keyframes.Count + " events.");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            scrCamera camera = scrCamera.instance;
            if (camera == null)
            {
                return;
            }

            Vector2 shake = Vector2.zero;
            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            if (index >= 0)
            {
                shake = SampleShake(keyframes[index], frame.Time);
            }

            if (camera.shake != shake)
            {
                camera.shake = shake;
            }
        }

        public void EndSession()
        {
            // 缓存的 tween 会跨采样保活，所以会话结束时必须杀掉，
            // 否则它们会在 DOTween 的注册表里越积越多。
            for (int i = 0; i < keyframes.Count; i++)
            {
                Tween tween = keyframes[i].Tween;
                if (tween != null && tween.active)
                {
                    tween.Kill(false);
                }
                keyframes[i].Tween = null;
            }
            keyframes.Clear();
        }

        private static Vector2 SampleShake(ShakeKeyframe shake, float time)
        {
            float elapsed = time - (float)shake.StartTime;
            if (elapsed < 0f || shake.Duration <= 0f || elapsed > shake.Duration)
            {
                return Vector2.zero;
            }

            float multiplier = EvaluateMultiplier(shake, elapsed / shake.Duration);
            int vibrato = Mathf.RoundToInt(20f * shake.Intensity);

            // tween 只创建一次，之后仅做采样。每帧重建会
            // 每次挑一条新的随机路径，这正是小幅度抖动会乱抖的原因。
            // 它被暂停并直接跳到指定时间，Unity 从不
            // 推进它，采样之间也不会泄漏状态。
            if (shake.Tween == null)
            {
                shake.Tween = DOTween.Shake(
                    () => shake.LastValue,
                    value => shake.LastValue = value,
                    shake.Duration,
                    shake.Strength,
                    vibrato,
                    90f,
                    false,
                    shake.FadeOut);
                if (shake.Tween != null)
                {
                    shake.Tween.Pause();
                }
            }

            if (shake.Tween == null)
            {
                return Vector2.zero;
            }

            // andPlay 保持 false：Goto 已经把 tween 强制到指定时间并
            // 应用了值，让它播放会自行推进，于是
            // 采样得到的抖动会偏离求值器驱动的时钟。
            shake.Tween.Goto(elapsed, false);
            return new Vector2(shake.LastValue.x, shake.LastValue.y) * multiplier;
        }

        /// <summary>
        /// 原生特效用来乘在抖动上的包络：Linear 保持满
        /// 幅度，InOut 缓动在中点折叠，Out 缓动从满幅衰减，
        /// 其余的都是向上爬升。
        /// </summary>
        private static float EvaluateMultiplier(ShakeKeyframe shake, float ratio)
        {
            if (shake.Ease == Ease.Linear)
            {
                return 1f;
            }

            string name = shake.Ease.ToString();
            if (name.StartsWith("InOut", StringComparison.Ordinal))
            {
                float folded = PreviewEase.EasedValue(0f, 2f, ratio, shake.Ease);
                return folded > 1f ? 2f - folded : folded;
            }
            if (name.StartsWith("Out", StringComparison.Ordinal))
            {
                return PreviewEase.EasedValue(1f, 0f, ratio, shake.Ease);
            }
            return PreviewEase.EasedValue(0f, 1f, ratio, shake.Ease);
        }
    }
}