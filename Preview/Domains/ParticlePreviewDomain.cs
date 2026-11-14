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
            /// 上一次求值时这个系统是不是被装饰物域藏起来的。
            ///
            /// 用来抓住"重新被放出来"的那一帧：在隐藏状态下系统即使调过
            /// Play()，isPlaying 也可能报 true（标志置了位，但没有真正模拟），
            /// 光看 isPlaying 会以为"已经在播了"而不再启动，于是这个
            /// 发射器永远起不来。
            /// </summary>
            internal bool WasHidden;

            /// <summary>
            /// 这个装饰物的 `autoPlay` 还没来得及「烧掉」。
            ///
            /// 原版 `scrParticleDecoration.Update()`：
            ///
            ///     if (atStart) {
            ///         if (autoPlay &amp;&amp; !ADOBase.controller.paused) particleSystem.Play();
            ///         autoPlay = false;   // ← 不管播没播，无条件清掉
            ///     }
            ///
            /// **关键在最后那句是无条件的。** 编辑器 / 预览里
            /// `controller.paused` 恒为 true，`Play()` 被跳过，而 `autoPlay`
            /// 照样被清 —— 于是这批发射器在原版编辑器里**从来不会自己播**，
            /// 要等谱面某条 `SetParticle Start` 把它叫起来。
            ///
            /// 这里以前理解反了：保留这个闩、等物体第一次露面就补一次
            /// `Play()`，理由是"原生逻辑挂在 paused 后面，预览里不会发生的
            /// 事要我来补"。但这一处**恰恰不该补** —— 原版在编辑器里就是
            /// 不播，补了才是错的。
            ///
            /// 实测（`ViLLAGE OF CHRYSANTHEMUM`）：20 件 `autoPlay=True`
            /// 的发射器（`p4_tjsfud*` / `oe_ccccc`）里有 18 件在预览
            /// `t=0.000` 就被点着了，而它们要到 2464 格才出场、谱面也
            /// 从没有显式 `Start`。原版同一段画面干干净净 ——
            /// 用户报的「315 格附近很多不该出现的粒子」就是它们
            /// （三角形 / 花瓣 / 字符图三种图全对得上）。
            ///
            /// 所以这个标记现在只用于**把闩烧掉**，不再触发任何播放。
            /// </summary>
            internal bool AutoPlayPending;

            /// <summary>
            /// 已经因为「这一刻没有任何 mode 动作」把它停了。
            ///
            /// 只是去重：`ApplyTargetMode` 每帧都会走，没这个标记就会
            /// 每帧 `Stop()` 一次。真正的判据是"当前没有动作"，
            /// 见那里的注释。
            /// </summary>
            internal bool ParticlesStoppedForNoAction;

            /// <summary>
            /// 最近一条已经炸过的 emit 事件。只用来挡"同一条事件重复炸"，
            /// 真正的触发条件是播放头跨过它的起始时刻（见 ApplyEmit）。
            /// </summary>
            internal ParticleAction EmittedLast;

            /// <summary>
            /// 上一帧这个粒子系统是不是在播。只给"出现/停止"探针用：
            /// 992/993 多出不该有的发射器时，靠它定位"是谁在那一刻点着的"。
            /// </summary>
            internal bool WasPlaying;
        }

        /// <summary>
        /// 判断"这一步是不是真的跨过了事件"允许的最大跨度（谱面秒）。
        ///
        /// 一帧能挪出这么多秒，说明用户在快拖或者直接跳位置，而不是在
        /// 一帧一帧地走过事件。那种情况下原生也不会补这次爆发——粒子早该散了。
        /// 和求值引擎自己识别"跳转"用的阈值保持一致。
        /// </summary>
        private const float EmitCrossingMaxStep = 0.25f;

        private readonly List<DecoState> states = new List<DecoState>();
        private int particleLogCount;
        // 上一次求值的时刻。用来察觉"向后拖动"——时间倒退说明用户在
        // 回拖定位，此时必须丢掉所有"已经应用过"的闩，否则向前重放时
        // 那些 Start 不会再跑一次。
        private float lastEvaluatedTime = float.NegativeInfinity;
        private int lastDirection;

        /// <summary>
        /// 丢掉所有"已经应用过"的闩，让下一个 Start 必须重新跑一次。
        /// </summary>
        private void ResetLatches()
        {
            for (int i = 0; i < states.Count; i++)
            {
                states[i].AppliedTargetMode = null;
                states[i].EmittedLast = null;
                // 时间倒回去了：这条发射器可能又回到"还没有任何动作"的状态，
                // 允许再停一次（见 ApplyTargetMode）。
                states[i].ParticlesStoppedForNoAction = false;
            }
        }

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

            // 没有事件的粒子装饰物也要收进来——它们靠 autoPlay 自己起，
            // 而那个自动播放写在原生的 paused 闸之后，预览里永远不会发生。
            CollectAutoPlayDecorations(manager);

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

        /// <summary>
        /// 把声明了 `autoPlay` 的粒子装饰物登记下来，好让
        /// <see cref="ApplyAutoPlay"/> 按原版时机把这个闩**烧掉**。
        ///
        /// 走 `allDecorations` 而不是 `GetTaggedDecorations`：自动播放和 tag
        /// 无关，每个粒子装饰物都有自己的 `autoPlay` 标记。
        ///
        /// **这里不播任何东西。** 原版在编辑器里（`paused` 为真）同样不播 ——
        /// 详见 `AutoPlayPending` 与 `ApplyAutoPlay` 的注释。
        /// </summary>
        private void CollectAutoPlayDecorations(scrDecorationManager manager)
        {
            List<scrDecoration> all = manager.allDecorations;
            if (all == null)
            {
                return;
            }

            int autoPlayCount = 0;
            for (int i = 0; i < all.Count; i++)
            {
                scrParticleDecoration particle = all[i] as scrParticleDecoration;
                if (particle == null)
                {
                    continue;
                }

                DecoState state = GetOrCreateState(particle);
                LevelEvent source = particle.sourceLevelEvent;
                if (source == null)
                {
                    continue;
                }

                try
                {
                    if (source.GetBool("autoPlay"))
                    {
                        state.AutoPlayPending = true;
                        autoPlayCount++;
                    }
                }
                catch
                {
                    // 没声明 autoPlay 的装饰物读不出这个键，跳过即可。
                }
            }

            if (autoPlayCount > 0)
            {
                Main.Logger.Log("[StaticPreview/Particle] " + autoPlayCount
                    + " decorations declare autoPlay; per the native editor those"
                    + " must stay silent (paused blocks Play, and the flag is"
                    + " cleared anyway), so the evaluator only clears the flag.");
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

            // 上一帧的播放头时刻。Emit 要靠它判断"这一步是不是真的跨过了事件"，
            // 所以必须在下面把 lastEvaluatedTime 覆盖掉之前取出来。
            float previousTime = lastEvaluatedTime;

            // 时间方向翻转 = 用户在回拖定位。
            //
            // 必须清掉所有"这个动作已经跑过了"的闩。闩记的是"我跑过"，
            // 不是"系统现在是什么状态"；而回拖时装饰物域会把手里的装饰物
            // 藏起来（SetVisible(false) 对粒子装饰物就是 SetActive(false)），
            // 系统实际已经停了。不清闩的话，向前重放时这里认为
            // "Start 早就跑过了"而不动手，粒子再也回不来。
            //
            // 只在**翻转的那一帧**清，不是每个后退帧都清：持续回拖时
            // 每帧都清会让 StartFresh 每帧重跑一遍，粒子系统被反复重启。
            int direction = time > lastEvaluatedTime ? 1 : (time < lastEvaluatedTime ? -1 : 0);
            if (direction == -1 && lastDirection == 1)
            {
                ResetLatches();
            }
            if (direction != 0)
            {
                lastDirection = direction;
            }
            lastEvaluatedTime = time;

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

                ApplyAutoPlay(state);
                ApplyTargetMode(state, currentMode, time);
                ApplySimulationSpeed(state, previousSpeed, currentSpeed, time);
                ApplyEmit(state, currentEmit, previousTime, time);

                // 探针：播放状态跳变（起 / 停）各记一行。
                //
                // 992/993 那种"多出来的发射器"，本质就是某个系统在那一刻
                // 由停变播。记下当时的 tag、时间、当前生效的 mode 动作、
                // autoPlay 还挂没挂 —— 三者能直接指认是谁点着的。
                ParticleSystem probeSystem = state.Deco == null ? null : state.Deco.particleSystem;
                if (probeSystem != null && Main.Logger != null
                    && Main.Settings != null && Main.Settings.VerboseDiagnostics)
                {
                    bool playing = probeSystem.isPlaying;
                    if (playing != state.WasPlaying)
                    {
                        state.WasPlaying = playing;
                        ParticleAction applied = state.AppliedTargetMode;
                        Main.Logger.Log("[粒子出现] tag=" + state.Tag
                            + " t=" + time.ToString("0.000")
                            + " playing=" + playing
                            + " active=" + probeSystem.gameObject.activeSelf
                            + " 域内可见=" + state.Deco.GetVisible()
                            + " 事件visible=" + state.Deco.sourceLevelEvent.visible
                            + " mode=" + (applied == null ? "-" : applied.Mode.ToString())
                            + " 事件格=" + (applied == null ? "-" : applied.SourceFloor.ToString())
                            + " 事件时间=" + (applied == null ? "-" : applied.StartTime.ToString("0.000"))
                            + " autoPlay待播=" + state.AutoPlayPending + ".");
                    }
                }
            }
        }

        /// <summary>
        /// 把原版那一次「`autoPlay` 烧掉」补上 —— **只烧，不播**。
        ///
        /// 原版 `scrParticleDecoration.Update()`：
        ///
        ///     if (atStart) {
        ///         if (autoPlay &amp;&amp; !ADOBase.controller.paused) particleSystem.Play();
        ///         autoPlay = false;   // 无条件
        ///     }
        ///
        /// 编辑器 / 预览里 `paused` 恒为 true，所以 `Play()` 永远不会发生，
        /// 而 `autoPlay` 照样被清。AES 以前把这里当成"原生逻辑被 paused
        /// 挡住、要我来补"，结果替原版多播了一批本该沉默的发射器
        /// （见 `AutoPlayPending` 的注释）。
        ///
        /// 现在只把它标记清掉，一次 `Play()` 都不补。真正该起来的发射器
        /// 由谱面的 `SetParticle Start` 负责 —— 那才是原版的路径。
        ///
        /// 注意**不能在 Analyze 里直接清**：原版那句写在 `Update` 里，
        /// 而 `Update` 只在物体激活时才跑；物体一直被藏着的话闩会留到它
        /// 第一次露面。这里保留同样的时机（`ApplyAutoPlay` 每帧对着
        /// 已激活的系统调），只是把动作从"播"降级成"清"。
        /// </summary>
        private static void ApplyAutoPlay(DecoState state)
        {
            if (!state.AutoPlayPending)
            {
                return;
            }

            ParticleSystem system = state.Deco == null ? null : state.Deco.particleSystem;
            if (system == null)
            {
                return;
            }

            // 还藏着就先不动 —— 照抄原版「Update 只在激活时跑」的时机。
            if (!system.gameObject.activeSelf)
            {
                return;
            }

            state.AutoPlayPending = false;
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
                // **没有任何 mode 动作作用在这一刻 = 这个发射器必须静默。**
                //
                // 原版的粒子只有两条启动路径：`SetParticle Start`，或者
                // `autoPlay`（编辑器里被 `paused` 挡掉、且标志会被烧掉）。
                // 两者都没有的发射器，在原版编辑器里就是停着的。
                //
                // 以前这里直接 `return`，等于"不管它"——只要别的地方（比如
                // 拖动收尾的 `ParticleWatch.ResumeAll`）把它点着了，就再也
                // 没人负责让它停下，画面上于是多出一堆本该 1800 格才登场的
                // 粒子。在这里收口：没动作就停。
                //
                // **不能 `Clear()`**：那会连已发射的粒子一起抹掉，画面突然
                // 空一块。`StopEmitting` 只停发射，在飞的粒子自然消散，
                // 这也是原版 `SetParticle Stop` 的做法。
                if (state.ParticlesStoppedForNoAction)
                {
                    return;
                }
                ParticleSystem idle = state.Deco.particleSystem;
                if (idle != null && idle.isPlaying)
                {
                    idle.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }
                state.ParticlesStoppedForNoAction = true;
                return;
            }

            // 之后一旦真有动作作用，允许下一次"无动作"再停一回。
            state.ParticlesStoppedForNoAction = false;

            ParticleSystem system = state.Deco.particleSystem;
            if (system == null)
            {
                return;
            }

            bool alreadyApplied = state.AppliedTargetMode == current;

            // 回拖到"这个动作生效之前"：闩必须作废。
            //
            // 闩记的是"我跑过这个动作"，而现在时间已经退到它之前了，
            // 装饰物域把系统藏了起来。只靠"时间倒退的那一帧清闩"不够——
            // 拖动可能是从更早的位置朝这个动作前进的，那样根本没有
            // 倒退的那一帧。这里按时间判断，两个方向都覆盖。
            if (alreadyApplied && time < current.StartTime)
            {
                state.AppliedTargetMode = null;
                alreadyApplied = false;
            }

            // 系统没在播（或被藏起来了）时，Start 必须重跑，不管闩记着什么。
            //
            // 但**不能**顺手把它 SetActive(true)——那是装饰物域的职责。
            // 谱面会让一个 MoveDecorations visible:false 把一批粒子装饰物
            // 连同它们的 tag 一起藏掉（本谱 2106 格正是如此，藏的是
            // 1947 格 SetParticle Start 起来的那两个）。装饰物域把
            // GameObject 关掉，这里如果每帧再给它打开，两边就每帧
            // 打一次架、净结果是粒子常亮——用户看到的就是"该关的
            // 发射器没关"。
            //
            // 这里只承认"藏"这个事实：系统被关着就不再重复 Start，
            // 等装饰物域把它放出来（visible 重新为真）时再重新播。
            bool hidden = !system.gameObject.activeSelf;

            // 刚刚从隐藏变回可见：必须强制重启一次。
            //
            // 在隐藏状态下系统即使调过 Play()，isPlaying 也可能报 true——
            // 标志置了位但没有真正模拟。直接看 isPlaying 会得出
            // "已经在播了"的结论而跳过启动，这个发射器就再也起不来了。
            // 所以用"上一帧还藏着"这个事实来判定，比任何标志都可靠。
            bool justRevealed = state.WasHidden && !hidden;
            state.WasHidden = hidden;

            bool needsRestart = current.Mode == ParticlePlayMode.Start
                && !hidden
                && (justRevealed || !system.isPlaying);
            if (alreadyApplied && !needsRestart)
            {
                return;
            }
            if (hidden)
            {
                // 系统被装饰物域藏着。记下这个动作已经"处理过"，
                // 免得每帧重进这个分支；等它被放出来时 needsRestart
                // 会自然成立，Start 会重新跑。
                state.AppliedTargetMode = current;
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
            // 只处理"系统存在但不播"的情况。
            //
            // 以前这里还会 SetActive(true)——那是**越权**：可见性归装饰物域
            // （它按 MoveDecorations 的 visible 调 SetVisible，对粒子装饰物
            // 就是 SetActive）。两个域都去动同一个开关，就会每帧打架，
            // 表现为"该关的发射器关不掉"。这里不再碰激活状态，
            // 调用方已经确认过系统是活的。
            if (deco.sourceLevelEvent != null)
            {
                deco.ResetParticle(deco.sourceLevelEvent, true);

                // **把发射形状的尺寸写回"常驻那一套"的值。**
                //
                // `ResetParticle` 里写的是
                //     shape.scale = (Vector2)ev["scale"] / 100f * tileSize
                // 也就是按**格**换算过的尺寸；而原生每帧在 `scrParticleDecoration.Update`
                // 里用的是
                //     shape.scale = scale;      // scale 来自 SetScale(scaleVec) = scale/100
                // 两者差一个 tileSize（默认 100 倍）。
                //
                // 原生那边 Update 每帧都会盖回来，所以从来看不出来。求值器这边
                // 不一样：它在装饰物"透明 / 隐藏"时会跳过 SetScale（那是省开销的
                // 剔除），于是这个 100 倍大的发射面就留在系统上 ——
                // 粒子从一个巨大得多的矩形里往外发，看起来就是"位置全乱了"。
                // 本谱 739 / 754 那两条 MoveDecorations 正是 `opacity: 0` 和
                // `SetParticle Start` 一起发，正好踩中。
                //
                // 这里补一次，等价于原生 Update 每帧做的那一步，而且不依赖剔除。
                deco.SetScale(deco.scaleVec);
                deco.SetRotation(deco.rotAngle);
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
        /// 触发当前这条 emit 事件，规则只有一条：**播放头一步一步跨过它的起始时刻**。
        ///
        /// 原生的 EmitParticle 是个一次性事件——`ffxEmitParticlePlus.StartEffect`
        /// 只做一件事：对每个 tag 命中的粒子装饰物调一次
        /// `particleSystem.Emit(count)`。它没有 duration、没有"持续喷"的语义，
        /// 谱面里这条事件的字段也只有 tag / count / eventTag。
        ///
        /// 以前这里挂了两条额外的重复规则，都会让已经炸过的发射器再炸一次：
        ///
        ///   ① 同一条事件"已经活跃了一阵子、又有一秒没触发过"就重来一次
        ///      （墙钟时间 1 秒 + 谱面时间在事件起点后 1 秒内）。人停在事件附近
        ///      看画面，一秒后就是平地一声雷。
        ///   ② 回拖会 `ResetLatches()` 把 `EmittedLast` 清空，而这里只看
        ///      "闩是空的、且现在在事件之后"——于是回拖一点点再往前，
        ///      所有已经炸过的发射器会在**当前这个位置**同时补炸一次，
        ///      看起来就是"在后面突然又炸了"。
        ///
        /// 现在只按跨过判定：`previousTime < 起点 <= time`。回拖到事件之前
        /// 会把闩清掉，再往前跨过时自然会重炸；停在事件之后则一概不补。
        /// 另外要求这一步的跨度不超过 <see cref="EmitCrossingMaxStep"/>：
        /// 拖得飞快、或者直接跳到事件之后，原生也不会补这一次爆发
        /// （粒子早该散了），补了反而是凭空多一炸。
        /// </summary>
        private void ApplyEmit(DecoState state, ParticleAction current, float previousTime, float time)
        {
            // 回到事件之前：清闩，下次再跨过时重新炸。
            if (current == null)
            {
                state.EmittedLast = null;
                return;
            }

            if (state.EmittedLast == current)
            {
                return;
            }

            state.EmittedLast = current;

            bool crossed = previousTime < (float)current.StartTime && (float)current.StartTime <= time;
            bool stepIsContiguous = time - previousTime <= EmitCrossingMaxStep;
            if (!crossed || !stepIsContiguous)
            {
                return;
            }

            ParticleSystem system = state.Deco.particleSystem;
            if (system != null && current.EmitCount > 0)
            {
                system.Emit(current.EmitCount);
            }

            // 探针：每次真的补了一次爆发都记一行，用来核对"同一事件只炸一次"。
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[ParticleEmit] f" + current.SourceFloor
                    + " tag=" + current.SourceTag
                    + " count=" + current.EmitCount
                    + " start=" + current.StartTime.ToString("0.000")
                    + " prev=" + previousTime.ToString("0.000")
                    + " t=" + time.ToString("0.000")
                    + " deco=" + (state.Deco == null ? "?" : state.Deco.name)
                    + ".");
            }
        }

        public void EndSession()
        {
            states.Clear();
        }
    }
}
