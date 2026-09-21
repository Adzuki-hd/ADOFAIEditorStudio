using System.Collections.Generic;
using ADOFAI;
using ADOFAI.FloorFX;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 粒子装饰物：SetParticle 和 EmitParticle。
    ///
    /// AddParticle 事件本身在这里不用做事——编辑器在关卡加载时
    /// 就实例化了那些装饰物，跟游戏完全一样，所以它们
    /// 早就在屏上了。只有那两个会在某个时间点改变粒子系统的
    /// 事件类型需要求值。
    ///
    /// 原生效果动画了大约三十个属性，但这里只覆盖谱面
    /// 真正驱动的三个：targetMode（play/stop/clear）、simulationSpeed
    /// 和 EmitParticle.count。其余的在效果触发时由原来的 tween
    /// 应用，重新实现没人用的通道只是死重。
    ///
    /// targetMode 和 Emit 是一次性动作，而求值器是绝对时间的纯函数，
    /// 同一个时刻可能被反复调用。所以它们相对上一次
    /// 求值的时刻做边沿触发，否则拖动定位会每帧都
    /// 喷一发。
    /// </summary>
    internal sealed class ParticlePreviewDomain : IPreviewDomain
    {
        private enum ParticleActionKind
        {
            TargetMode,
            SimulationSpeed,
            Emit
        }

        private sealed class ParticleAction : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal float Duration;
            internal Ease Ease;
            internal ParticleActionKind Kind;
            internal ParticlePlayMode Mode;
            internal float SimulationSpeed;
            internal int EmitCount;

            // 来源格和 tag，仅诊断用。
            internal int SourceFloor;
            internal string SourceTag;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private sealed class DecoState
        {
            internal scrParticleDecoration Deco;
            internal string Tag;
            internal List<ParticleAction> Actions = new List<ParticleAction>();

            // 最近一次真正跑过的单次动作，这样它不会被重放。
            internal ParticleAction AppliedTargetMode;

            /// <summary>
            /// 最近触发的 Emit 动作，以及它触发的真实时间。参考
            /// 预览器会在活跃的 emit 事件变化时重新触发，或者
            /// 同一个事件已经活跃了一阵子时——这是个重复守卫，不是
            /// 只跑一次的锁存，因为长 emit 本来就该持续喷。
            /// </summary>
            internal ParticleAction EmittedLast;
            internal float EmittedAtUnscaledTime = float.NegativeInfinity;
        }

        // 与参考预览器对「仍然活跃的 emit 重新触发」的速率限制一致。
        // still active.
        private const float EmitRepeatSeconds = 1f;
        private const float EmitActiveWindowSeconds = 1f;

        private readonly List<DecoState> states = new List<DecoState>();
        private int particleLogCount;

        public string Name
        {
            get { return "Particle"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            states.Clear();

            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null)
            {
                Main.Logger.Log("[StaticPreview/Particle] no decoration manager.");
                return;
            }

            Collect(context, manager, LevelEventType.SetParticle, ParticleActionKind.TargetMode);
            Collect(context, manager, LevelEventType.SetParticle, ParticleActionKind.SimulationSpeed);
            Collect(context, manager, LevelEventType.EmitParticle, ParticleActionKind.Emit);

            int actionCount = 0;
            int withSystem = 0;
            int playing = 0;
            for (int i = 0; i < states.Count; i++)
            {
                PreviewStableSort.Sort(states[i].Actions,
                    (left, right) => left.Start.CompareTo(right.Start));
                actionCount += states[i].Actions.Count;
                if (states[i].Deco != null && states[i].Deco.particleSystem != null)
                {
                    withSystem++;
                    if (states[i].Deco.particleSystem.isPlaying)
                    {
                        playing++;
                    }
                }
            }

            Main.Logger.Log("[StaticPreview/Particle] analyzed " + actionCount
                + " actions over " + states.Count + " decorations; withSystem=" + withSystem
                + " alreadyPlaying=" + playing + ".");
        }

        private void Collect(PreviewTimelineContext context, scrDecorationManager manager,
            LevelEventType eventType, ParticleActionKind kind)
        {
            IList<PreviewEventSnapshot> events = context.GetEvents(eventType);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];
                bool hasSpeed = context.IsPropertyEnabled(levelEvent, "simulationSpeed");

                // 一个 SetParticle 事件一次带好几个通道；只给正在
                // 收集的那个通道建动作，谱面没碰过的通道就跳过。
                // 那个通道就保持不动。
                var action = new ParticleAction
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Kind = kind,
                    SourceFloor = levelEvent.Floor,
                    SourceTag = context.GetValue(levelEvent, "tag", string.Empty)
                };

                switch (kind)
                {
                    case ParticleActionKind.TargetMode:
                        if (!context.IsPropertyEnabled(levelEvent, "targetMode"))
                        {
                            continue;
                        }
                        action.Mode = context.GetValue(levelEvent, "targetMode", ParticlePlayMode.Start);
                        break;
                    case ParticleActionKind.SimulationSpeed:
                        if (!hasSpeed)
                        {
                            continue;
                        }
                        // 原生 ffxSetParticlePlus.Decode 直接赋这个原值：
                        //   simulationSpeed = evnt.GetFloat("simulationSpeed");
                        // 只有装饰物自己的 decode 才除以 100。对事件值也做缩放
                        // 让每个事件驱动的速度都小了 100 倍。
                        action.SimulationSpeed = context.GetValue(levelEvent, "simulationSpeed", 100f);
                        break;
                    case ParticleActionKind.Emit:
                        action.EmitCount = context.GetValue(levelEvent, "count", 0);
                        break;
                }

                foreach (scrParticleDecoration dec in ResolveTargets(context, manager, levelEvent))
                {
                    DecoState state = GetOrCreateState(dec);
                    state.Actions.Add(action);
                }
            }
        }

        private static IEnumerable<scrParticleDecoration> ResolveTargets(
            PreviewTimelineContext context, scrDecorationManager manager,
            PreviewEventSnapshot levelEvent)
        {
            string tag = context.GetValue(levelEvent, "tag", string.Empty);
            if (string.IsNullOrEmpty(tag))
            {
                yield break;
            }

            string[] tags = tag.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (tags.Length == 0)
            {
                yield break;
            }

            foreach (scrDecoration decoration in manager.GetTaggedDecorations(tags))
            {
                scrParticleDecoration particle = decoration as scrParticleDecoration;
                if (particle != null)
                {
                    yield return particle;
                }
            }
        }

        private DecoState GetOrCreateState(scrParticleDecoration deco)
        {
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].Deco == deco)
                {
                    return states[i];
                }
            }

            var state = new DecoState { Deco = deco, Tag = TagOf(deco) };
            states.Add(state);
            return state;
        }

        /// <summary>
        /// 装饰物作者写的 tag 列表，仅诊断用。
        /// </summary>
        private static string TagOf(scrParticleDecoration deco)
        {
            scrDecoration decoration = deco;
            LevelEvent source = decoration == null ? null : decoration.sourceLevelEvent;
            if (source == null)
            {
                return "?";
            }

            try
            {
                return source.GetString("tag");
            }
            catch
            {
                return "?";
            }
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            float time = frame.Time;

            for (int i = 0; i < states.Count; i++)
            {
                DecoState state = states[i];
                if (state.Deco == null)
                {
                    continue;
                }

                ParticleAction currentSpeed = null;
                ParticleAction previousSpeed = null;
                ParticleAction currentMode = null;
                ParticleAction currentEmit = null;

                for (int j = 0; j < state.Actions.Count; j++)
                {
                    ParticleAction action = state.Actions[j];
                    if (action.Start > time)
                    {
                        break;
                    }

                    if (action.Kind == ParticleActionKind.SimulationSpeed)
                    {
                        previousSpeed = currentSpeed;
                        currentSpeed = action;
                    }
                    else if (action.Kind == ParticleActionKind.TargetMode)
                    {
                        currentMode = action;
                    }
                    else
                    {
                        currentEmit = action;
                    }
                }

                ApplyTargetMode(state, currentMode, time);
                ApplySimulationSpeed(state, previousSpeed, currentSpeed, time);
                ApplyEmit(state, currentEmit, time);
            }
        }

        /// <summary>
        /// 应用当前活跃的 targetMode 动作。
        ///
        /// 每帧重跑同一个动作会让发射持续重启，
        /// 所以正常只应用一次。Start 是例外：如果
        /// 系统实际上没在播放，那么这个动作必须重跑，不管
        /// 记住的标记是什么。向后拖动定位会让装饰物事件把物体
        /// 藏起来（SetVisible 映射到 SetActive），那会停止并清空系统，
        /// 而标记还指向那个 Start——于是向前重放时看到一个已停止的
        /// 系统和一个对得上的标记，粒子就再也回不来了。
        /// </summary>
        private void ApplyTargetMode(DecoState state, ParticleAction current, float time)
        {
            if (current == null)
            {
                return;
            }

            ParticleSystem system = state.Deco.particleSystem;
            if (system == null)
            {
                return;
            }

            bool alreadyApplied = state.AppliedTargetMode == current;
            bool needsRestart = current.Mode == ParticlePlayMode.Start
                && (!system.isPlaying || !system.gameObject.activeSelf);
            if (alreadyApplied && !needsRestart)
            {
                return;
            }

            state.AppliedTargetMode = current;

            switch (current.Mode)
            {
                case ParticlePlayMode.Start:
                    // 复用游戏自己的重新初始化，而不是裸调 Play()。
                    // ResetParticle 会停止并清空系统，把它的时间倒回，
                    // 重设随机种子，并从来源事件重写那约 25 个属性
                    // （duration、loop、颜色、尺寸、生命周期、形状、速度、发射
                    // 率……）。参考预览器在 Start 上做的正是这些。
                    // 只调 Play() 会留下上一轮的粒子，并让系统
                    // 停在陈旧设置上，于是喷发不断累积。
                    StartFresh(state.Deco, system);
                    break;
                case ParticlePlayMode.Stop:
                    system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                    break;
                case ParticlePlayMode.Clear:
                    system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    break;
            }

            // 在动作之后记日志，这样状态反映的是实际应用的结果；
            // 放在之前的话，一个未激活的系统永远读成 active=False，哪怕
            // 处理器刚刚把它打开。
            // 只记报告粒子缺陷的那几个格，而且上限放宽，这样
            // 谱面开头的事件挤不掉真正有意思的那些。
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics
                && current.SourceFloor >= 700 && particleLogCount < 200)
            {
                particleLogCount++;
                ParticleSystem.MainModule main = system.main;
                ParticleSystem.EmissionModule emission = system.emission;
                Main.Logger.Log("[ParticleMode] f" + current.SourceFloor
                    + " tag=" + current.SourceTag
                    + " mode=" + current.Mode
                    + " t=" + time.ToString("0.000")
                    + " start=" + current.StartTime.ToString("0.000")
                    + " playing=" + system.isPlaying
                    + " count=" + system.particleCount
                    + " max=" + main.maxParticles
                    + " rate=" + emission.rateOverTime.constantMax.ToString("0.0")
                    + " loop=" + main.loop
                    + " active=" + system.gameObject.activeInHierarchy
                    + " rndEnabled=" + (system.GetComponent<ParticleSystemRenderer>() != null
                        && system.GetComponent<ParticleSystemRenderer>().enabled)
                    + " pos=" + system.transform.position.x.ToString("0.0") + ","
                    + system.transform.position.y.ToString("0.0")
                    + " scale=" + system.transform.lossyScale.x.ToString("0.000") + ","
                    + system.transform.lossyScale.y.ToString("0.000")
                    + " startSize=" + main.startSize.constantMax.ToString("0.000")
                    + " camOrtho=" + (scrCamera.instance == null || scrCamera.instance.camobj == null
                        ? -1f
                        : scrCamera.instance.camobj.orthographicSize).ToString("0.0") + ".");
            }
        }

        private static void StartFresh(scrParticleDecoration deco, ParticleSystem system)
        {
            // 挂在未激活 GameObject 上的粒子系统根本不会被模拟，
            // 对它调 Play() 会静默失败。SetVisible 直接映射到
            // "particleSystem.gameObject.SetActive(visible)"，所以一个被留在
            // 隐藏状态的装饰物，不管被叫多少次播放都还是未激活——这
            // 就是为什么每个 Start 都跑了，喷发却始终不出现。
            if (!system.gameObject.activeSelf)
            {
                system.gameObject.SetActive(true);
            }

            if (deco.sourceLevelEvent != null)
            {
                deco.ResetParticle(deco.sourceLevelEvent, true);
            }

            // ResetParticle 会停掉系统；没有别的东西会自己再播放它。
            if (!system.isPlaying)
            {
                system.Play();
            }
        }

        /// <summary>
        /// tween 驱动的通道：装饰物存一个普通乘数，每次 update
        /// 把它应用到系统上，所以写入插值后的值就够了。
        /// </summary>
        private static void ApplySimulationSpeed(DecoState state, ParticleAction previous,
            ParticleAction current, float time)
        {
            if (current == null)
            {
                return;
            }

            float from = previous == null ? 1f : previous.SimulationSpeed;
            float ratio = current.Duration <= 0f
                ? 1f
                : Mathf.Clamp01((time - (float)current.StartTime) / current.Duration);
            float value = PreviewEase.EasedValue(from, current.SimulationSpeed, ratio, current.Ease);
            if (!Mathf.Approximately(state.Deco.simulationSpeed, value))
            {
                state.Deco.simulationSpeed = value;
            }
        }

        /// <summary>
        /// 触发当前活跃的那个 emit 动作，遵循参考
        /// 预览器的规则：活跃的 emit 事件变化时发射，同一个事件
        /// 只有在既已经活跃了一会儿、又已经一秒钟没触发过时，
        /// 才重新发射。长 emit 本来就该持续喷，所以只跑一次的锁存是错的；
        /// 速率限制才是阻止它每帧触发的东西。
        ///
        /// 因为判断只依据活跃事件本身，向后拖动定位
        /// 不需要特殊处理：活跃事件自己会变，emit
        /// 就会重新触发。
        /// </summary>
        private void ApplyEmit(DecoState state, ParticleAction current, float time)
        {
            if (current == null)
            {
                return;
            }

            float unscaledNow = Time.unscaledTime;
            bool eventChanged = state.EmittedLast != current;
            bool sameEventDueAgain = unscaledNow - state.EmittedAtUnscaledTime > EmitRepeatSeconds
                && time - current.StartTime < EmitActiveWindowSeconds;

            if (!eventChanged && !sameEventDueAgain)
            {
                return;
            }

            ParticleSystem system = state.Deco.particleSystem;
            if (system != null && current.EmitCount > 0)
            {
                system.Emit(current.EmitCount);
            }

            state.EmittedLast = current;
            state.EmittedAtUnscaledTime = unscaledNow;
        }

        public void EndSession()
        {
            states.Clear();
        }
    }
}
