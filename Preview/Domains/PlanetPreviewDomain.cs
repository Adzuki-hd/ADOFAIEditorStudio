using System;
using System.Reflection;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class PlanetPreviewDomain : IPreviewDomain
    {
        private static readonly FieldInfo CosmeticAngleTweenField = typeof(scrPlanet).GetField(
            "_cosmeticAngleTween",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private int lastFloor = -1;

        public string Name
        {
            get { return "Planet"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            lastFloor = -1;
            Main.Logger.Log("[StaticPreview/Planet] ready for " + context.Floors.Count + " floors.");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            int floorIndex = frame.Context.FindFloorAtOrBefore(frame.Time);
            PreviewFloorSnapshot floor = frame.Context.GetFloor(floorIndex);
            if (floor == null)
            {
                return;
            }

            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            foreach (scrPlayer player in playerManager.players)
            {
                if (player == null || player.planetarySystem == null)
                {
                    continue;
                }

                // ExtraBeats 的格做了建模：Pause 事件会拉长这一格，但不改变
                // 球进入它的位置，所以球只是走得
                // 更慢。这一点计时表本来就能推出来——NextEntryTime 就是
                // 下一格真正的进入时间，所以角速度自己就降下来了。
                bool complexFloor = floor.FreeRoam
                    || floor.HoldLength > -1
                    || floor.PlanetCount != 2
                    || (frame.Context.GetFloor(floorIndex - 1) != null
                        && frame.Context.GetFloor(floorIndex - 1).PlanetCount != 2)
                    || (frame.Context.GetFloor(floorIndex + 1) != null
                        && frame.Context.GetFloor(floorIndex + 1).PlanetCount != 2);

                if (complexFloor)
                {
                    // Freeroam、长按格和非双球格还没建模。旧的
                    // 兜底调的是原生 ScrubToFloorNumber 检查点重生路径，
                    // 那条路依赖游戏自己的模式机制，而求值器接管的
                    // 预览刻意不进那套机制。把球留在原地，
                    // 能保住预览其余部分正确。
                    if (floorIndex != lastFloor)
                    {
                        Main.Logger.Log("[StaticPreview/Planet] complex floor f="
                            + floorIndex + " left unmodelled.");
                    }
                }
                else
                {
                    EvaluateTwoPlanetFloor(frame, player, floorIndex, floor);
                }
            }

            lastFloor = floorIndex;
        }

        public void EndSession()
        {
            lastFloor = -1;
            lastLoggedFloor = -1;
            RestoreCosmeticAngles();
            AwakePlanets.Clear();
            ColouredPlanets.Clear();
            RestoreRings();
        }

        /// <summary>
        /// 清掉本域在每个球上留下的 cosmetic angle。
        ///
        /// 渲染器按 `angle + cosmeticAngle` 画球，而本域每帧直接设
        /// cosmeticAngle。游戏从不这么做：它只会在 TweenCosmeticAngle 内部
        /// 写这个字段，那条 tween 在自己的 OnKill 回调里把它重置为零。
        /// 所以这个值只会作为游戏自己起的某条 tween 的副作用被清掉——
        /// 而在求值器写过它之后，并不存在这样一条 tween。
        /// 这时把画面交回原生运行时，每个球就会画在一个游戏完全不知道的
        /// 角度上：球看起来转离了自己的格，而击打仍然准时，
        /// 因为 `angle` 本身是对的。
        /// </summary>
        private static void RestoreCosmeticAngles()
        {
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null
                    || player.planetarySystem.planetList == null)
                {
                    continue;
                }

                for (int j = 0; j < player.planetarySystem.planetList.Count; j++)
                {
                    scrPlanet planet = player.planetarySystem.planetList[j];
                    if (planet == null)
                    {
                        continue;
                    }

                    KillCosmeticAngleTween(planet);
                    planet.cosmeticAngle = 0f;
                }
            }
        }

        /// <summary>
        /// 把圆环交回给游戏。DriveRings 会禁用 scrRing，让它自己的 Update
        /// 不再覆盖本域设的缩放；一直禁用着的话，工作台一旦关闭，
        /// 原生编辑器就会坏掉。
        /// </summary>
        private static void RestoreRings()
        {
            scrPlayerManager playerManager = scrPlayerManager.instance;
            if (playerManager == null || playerManager.players == null)
            {
                return;
            }

            for (int i = 0; i < playerManager.players.Length; i++)
            {
                scrPlayer player = playerManager.players[i];
                if (player == null || player.planetarySystem == null
                    || player.planetarySystem.planetList == null)
                {
                    continue;
                }

                for (int j = 0; j < player.planetarySystem.planetList.Count; j++)
                {
                    scrPlanet planet = player.planetarySystem.planetList[j];
                    if (planet == null || planet.planetRenderer == null
                        || planet.planetRenderer.ringComp == null)
                    {
                        continue;
                    }

                    PlanetRenderer renderer = planet.planetRenderer;
                    renderer.ringComp.gameObject.SetActive(true);
                    renderer.ringComp.enabled = true;
                    if (renderer.ring != null)
                    {
                        renderer.ring.enabled = true;
                    }
                }
            }
        }

        private static void EvaluateTwoPlanetFloor(
            PreviewEvaluationFrame frame,
            scrPlayer player,
            int floorIndex,
            PreviewFloorSnapshot floor)
        {
            PreviewTimelineContext context = frame.Context;
            PlanetarySystem planetarySystem = player.planetarySystem;
            if (planetarySystem.planetList == null || planetarySystem.planetList.Count < 2)
            {
                return;
            }

            // 哪个球在轨道上，取决于这一格之前有多少个中途旋转格：
            // 每个中途旋转都交接给另一个球。
            scrPlanet chosen = planetarySystem.planetList[0];
            for (int i = 1; i <= floorIndex; i++)
            {
                PreviewFloorSnapshot traversedFloor = context.GetFloor(i);
                chosen = traversedFloor != null && traversedFloor.MidSpin
                    ? chosen.prev
                    : chosen.next;
            }
            if (chosen == null)
            {
                return;
            }

            planetarySystem.chosenPlanet = chosen;
            KillCosmeticAngleTween(chosen);
            chosen.cosmeticAngle = 0f;

            scrFloor boundFloor = context.GetFloorBinding(floorIndex);
            if (boundFloor == null)
            {
                return;
            }
            chosen.currfloor = boundFloor;

            double direction = floor.IsCounterClockwise ? -1d : 1d;
            planetarySystem.isCW = !floor.IsCounterClockwise;
            planetarySystem.speed = floor.Speed;

            // 0 格是倒计时格：原生 FirstFloorAngleSetup 把球放在离该格
            // 进入角半圈的位置（实测 -10.996 对 4.712）。在那里用
            // 进入角会让球落在错误的一侧，第一次落地也就错了。
            // landing wrong.
            //
            // 其他每一格都正好从它自己的 entryangle 起步。连续 119 格
            // 实测：tilePos(N) + (sin entryangle, cos entryangle) * radius
            // 落在 N-1 格中心，误差 0.01 以内；换成 exitangle 则
            // 落在 N+1 格中心。轨道半径等于 tileSize，也就是网格间距，
            // 所以球扫过的那个「转角」就是相邻格的中心。
            // 在这里加半个「每拍反角」（早先对轨道偏移的一个猜测）会让球
            // 偏出半圈，永远碰不到格。
            // 0 格是倒计时格：原生 FirstFloorAngleSetup 把球放在离该格
            // 进入角半圈的位置（实测 -10.996 对 4.712）。在那里用
            // 进入角会让球落在错误的一侧，第一次落地也就错了。
            //
            // 其他每一格都正好从它自己的 entryangle 起步，暂停格也一样。
            // 连续 119 格实测：tilePos(N) + (sin entryangle, cos
            // entryangle) * radius 落在 N-1 格中心，误差 0.01 以内；换成
            // exitangle 则落在 N+1 格中心。轨道半径等于 tileSize，也就是
            // 网格间距，所以球扫过的那个「转角」就是相邻格的
            // 中心。
            double snappedAngle = floorIndex == 0
                ? Math.PI * (0.5d - context.AdjustedCountdownTicks)
                : floor.EntryAngle;
            double cosmeticAngle = 0d;

            if (floorIndex > 0 && floor.ExtraBeats > 0f)
            {
                // 暂停格是游戏唯一不按进入角进入的情况。
                // 出自 scrPlanet.HandlePause，其中 num = direction：
                //
                //   num4 = nextfloor.entryangle - PI*num
                //   num5 = num4 - PI*num*(extraBeats+1) + (PI - angleLength)*num
                //   snappedLastAngle = num5
                //
                // 已对照 NYA 谱面 161 格的原生录像验证过（一个 180 度
                // 格，extraBeats 1，direction +1）：公式给出 -4.7124，游戏
                // 报的 snappedLastAngle = -4.7124。这个值落在该格的「退出」角上，
                // 所以球从退出角扫过整整一圈回到进入角，
                // 那正是下一格期望它在的地方。
                //
                // 然后叠加一层 cosmetic 偏移：p.angle 被重置回进入角，
                // 好让渲染位置连续，差值由
                // cosmeticAngle 承担，游戏用 OutSine 缓动在一拍内把它 tween 回零。
                // 同一份录像里实测它从 PI 开始排到 0，
                // 那就是球在行进前肉眼可见地退了一步。
                PreviewFloorSnapshot nextFloor = context.GetFloor(floorIndex + 1);
                double nextEntryAngle = nextFloor != null ? nextFloor.EntryAngle : floor.ExitAngle;
                double nativeStart = nextEntryAngle - Math.PI * direction
                    - Math.PI * direction * (floor.ExtraBeats + 1d)
                    + (Math.PI - floor.AngleLength) * direction;
                snappedAngle = nativeStart;

                // 渲染角度仍然必须从进入角起步，而球自己的起点
                // 在退出角上，所以这个偏移横跨两者之间的差，
                // 它的符号由 angleCorrectionDir 选择。原生：
                //
                //   num2 = mod(重置前的角度), num6 = mod(start)
                //   num7 = angleCorrectionType * (num2 - num6) * direction * sign(num6 < num2)
                //   TweenCosmeticAngle(-num7)
                //
                // 末尾那个符号因子让括号里的项变成了幅度，所以偏移
                // 化简为 -angleCorrectionType * |num2 - num6|。两个方向都在
                // NYA 谱面的 161 格上录过：angleCorrectionType 为 -1 时偏移从
                // +3.192 降到 0，为 +1 时从 -3.244 升到 0，起始角都是
                // -4.7124。漏掉这个因子会让两个方向表现一样。
                double entryMod = Mod2Pi(floor.EntryAngle);
                double startMod = Mod2Pi(nativeStart);
                cosmeticAngle = -floor.AngleCorrectionType * Math.Abs(entryMod - startMod);
            }

            // 球在单拍内扫过它自己的 angleLength，而一格有多少拍
            // 是暂停改变的量。NextEntryTime 是下一格真正的
            // 进入时间，extraBeats 是 Pause 事件多加的拍数，所以
            // 一拍的长度等于该格真实时长除以 (1 + extraBeats)。
            //
            // 这个区分很要紧：改成除以整格时长，会把扫动摊到
            // 暂停的每一拍上，球就会以半速转。
            // NYA 谱面 161 格实测——一个 180 度格、速度 2，正常应该是
            // 0.2 秒转半圈——用整格时长的写法却让它跑了该格完整的
            // 0.4 秒。把角度摊到一拍上，转动速率就和任何其他格
            // 一样，这正是游戏的做法。
            //
            // 这也去掉了一个重复的速度因子：速率以前由
            // BaseBpm * floor.Speed 构成，之后又乘了一遍 floor.Speed，于是任何带
            // 速度倍率的格都按速度的平方在跑。SetSpeed 为 2 时就是 4 倍——实测
            // 600 BPM 对预期的 300，而且过了 76 格之后球跑得太靠前，
            // 再也对不上它正在按的那一格。
            double elapsed = frame.Time - floor.EntryTime;
            double tileDuration = floor.NextEntryTime - floor.EntryTime;
            double beatSeconds = tileDuration / (1d + floor.ExtraBeats);

            // 暂停格又是例外：scrPlanet.Update_RefreshAngles 每拍推进
            // PI * speed，跟 angleLength 无关，所以在一格持续
            // (1 + extraBeats) 拍时，球转过 PI * (1 + extraBeats)——一拍暂停
            // 就是整整一圈。161 格实测：6.22 rad，预期 2*PI = 6.28。其他
            // 每一格的扫动就只是它自己的 angleLength。
            //
            // 0 格是另一个例外，那里的扫动「不是」它的 angleLength。
            // FirstFloorAngleSetup 把球放在 PI * (0.5 - adjustedCountdownTicks)——
            // 在这张谱面上是 -10.996，而该格的退出角是 1.571——倒计时
            // 持续 adjustedCountdownTicks 拍。scrPlanet 全程每拍推进
            // PI * speed，所以球在落地前走过 PI * adjustedCountdownTicks：
            // -10.996 + 4*PI 正好等于 1.571。改成用该格的 angleLength (PI)，
            // 它只转了四分之一路，离格中心还差半圈，
            // 然后一直爬行到播放头到达 1 格。
            double sweep;
            if (floorIndex == 0)
            {
                // 不另乘速度因子：crotchetAtStart 已经带了它
                // （60 / (bpm * pitch * speed)），这就是为什么 scrPlanet 的速率
                // 算出来是每拍 PI * speed。在这里再乘一次 floor.Speed 会让它变平方
                // ——就是上面普通格那里点出的同一个错误。
                sweep = Math.PI * context.AdjustedCountdownTicks;
            }
            else if (floor.ExtraBeats > 0f)
            {
                sweep = Math.PI * (1d + floor.ExtraBeats);
            }
            else
            {
                sweep = floor.AngleLength;
            }

            double progress = tileDuration <= 1e-6d
                ? 0d
                : Math.Min(1d, elapsed / tileDuration);
            double angle = snappedAngle + progress * sweep * direction;

            // cosmetic 偏移只影响画出来的样子，从不影响球落在哪里：
            // 原生在算渲染位置时把它加到角度上，`angle` 本身
            // 不动。它用 OutSine 缓动在一拍内从起始值 tween 到零，
            // 然后杀掉那条 tween，所以从播放头直接求这条曲线能得到同样的形状，
            // 而不需要在帧之间携带 tween 状态。
            //
            // 从 A 到 0 的 OutSine 是 A * (1 - sin(f * PI/2))，已对照 161 格的
            // 原生录像核过：f = 0.00 / 0.09 / 0.21 / 0.33 时实测
            // 3.192 / 2.738 / 2.148 / 1.596，公式给出 3.142 / 2.705 / 2.139 / 1.606。
            // A * (1 - cos) 完全对不上——它从零开始往上爬。
            double renderedAngle = angle;
            if (cosmeticAngle != 0d && beatSeconds > 1e-6d)
            {
                double f = Math.Min(1d, elapsed / beatSeconds);
                renderedAngle = angle + cosmeticAngle * (1d - Math.Sin(f * Math.PI * 0.5d));
            }

            // 球绕着它脚下的格转，半径 = tileSize * radiusScale。
            // 以前在这里额外加的半个「每拍反角」中心偏移，
            // 是对「两格中点」轨道的猜测；上面那个实测出来的转角不变量
            // 说明方向就是角度本身。
            double radius = context.TileSize * floor.RadiusScale;

            // 行星骑在哪一格的位置上，由 stickToFloor 决定。
            //
            // 原生 scrPlanet 是二选一：
            //
            //   if (scrFloor2.stickToFloor) pos = scrFloor2.transform.position;
            //   else                        pos = scrFloor2.startPos;
            //
            // 也就是「粘住」时骑着格子被挪到的地方，「不粘」时回到作者
            // 摆的布局位置。这个开关来自 PositionTrack 的 stickToFloors 参数，
            // 而且它是**逐格累积**的粘性状态，初值取 settings.stickToFloors
            // （scnGame 里 flag 一路带下去，每格写进 floor.stickToFloor）。
            //
            // 求值器以前恒用 transform.position，等于永远粘住，于是
            // 「黏性轨道」这个参数在预览里完全没有作用——把它关掉，
            // 画面照旧跟着轨道跑。
            // 行星绕着的中心。用快照的 BallPosition，而不是在这里
            // 再判一次 stickToFloor —— 相机跟随也读同一个字段。
            // 这个 bug 最初就是"两个地方各算一遍、只改了一处"造成的，
            // 所以定义只留一份。
            Vector3 centre = floor.BallPosition;
            Vector3 offset = AngleToVector(renderedAngle) * (float)radius;
            Vector3 position = centre + offset;

            // 位置在这里算，而不是去调原生
            // Update_RefreshAngles：那个 helper 从
            // conductor.songposition_minusi 推导角度，而求值器接管的预览驱动自己的
            // 时钟、不写 conductor，所以那个 helper 会读到陈旧的时间。
            // 墙钟字段仍然会设，这样其他读它们的家伙（比如跟随
            // 相机）跟屏上的东西保持一致。
            chosen.angle = angle;
            chosen.cosmeticAngle = (float)(renderedAngle - angle);
            chosen.SetSnappedLastAngle(snappedAngle);
            chosen.SetTargetExitAngle(floorIndex == 0
                ? snappedAngle + floor.AngleLength
                : snappedAngle + floor.AngleLength * direction);
            chosen.cosmeticRadius = (float)radius;
            chosen.transform.position = position;

            // 双球关卡播放时两个球都在屏上：选中的那个
            // 绕着它按的格转，另一个等在同一格
            // 的中心。这里修掉了早先两个错误。以前只把选中的球
            // 藏起来，于是玩家刚落地的那颗球在另一颗出现的同一刻
            // 消失；而且空闲的那颗球根本没被摆过，就
            // 停在它加载时被留在的地方。求值器接管预览期间
            // 球自己的 Update 是关的，所以无论姿态还是 sprite 可见性
            // 都不会被别的东西还原。
            // 另一颗球停在同一个位置——但要用**和选中球同一个基准**。
            //
            // 以前这里写的是 boundFloor.transform.position（实位），
            // 而这个开关关掉时选中球已经改用 startPos 了。两颗球于是
            // 被摆到两个不同的地方：日志里 f7 是
            //   选中球 (9.00,0.03) 按 layout 算，非选中球 (12.00,-1.50) 还在实位，
            // 差 2.12 —— 正是"黏性轨道没生效"看起来的样子。
            Vector3 pivot = centre;
            for (int i = 0; i < planetarySystem.planetList.Count; i++)
            {
                scrPlanet planet = planetarySystem.planetList[i];
                if (planet == null)
                {
                    continue;
                }

                if (planet != chosen)
                {
                    planet.transform.position = pivot;
                }

                PlanetRenderer renderer = planet.planetRenderer;
                if (renderer != null && renderer.sprite != null)
                {
                    renderer.sprite.visible = true;
                }

                // 球的 Update 关着，所以没人会调 Revive，拖尾 /
                // 核心 / 火花粒子和圆环在整个预览期间都休眠着。
                // lite revive 把它们打开，而不碰 hittable 和
                // emoji 表情挂载点，原生路径在非合作模式下对这两个传 false。
                EnsureRendererAwake(planet, renderer);

                // 颜色来自原生加载用的同一个地方，所以玩家自己选的
                // 红/蓝以及任何自定义预设都会被尊重，而不是靠
                // 猜。持久化读取很便宜；结果存下来，这样这段
                // 每个球只跑一次，而不是每帧跑。
                EnsureRendererColour(planet, renderer);
            }

            DriveRings(planetarySystem, chosen, floorIndex, (float)elapsed, frame.Time, context.TileSize);

            if (floorIndex != lastLoggedFloor)
            {
                lastLoggedFloor = floorIndex;
                LogPlanetState(chosen, position, floorIndex, frame.Time,
                    snappedAngle, angle, floor, context, planetarySystem);
            }
            scrController controller = scrController.instance;
            if (controller != null)
            {
                controller.rotationEase = floor.PlanetEase;
            }
        }

        private static int lastLoggedFloor = -1;

        /// <summary>原生 scrRing.Switch 以 Ease.Linear 在 0.1s 内淡变。</summary>
        private const float RingFadeSeconds = 0.1f;

        /// <summary>
        /// 驱动虚线引导环。
        ///
        /// 这个环是引导线，不是活跃球上的装饰物：它属于停在当前格
        /// 中心的那个球，正因如此——因为环是那颗球的子节点、
        /// 半径是一格——它恰好描出移动中的球走的那一圈。
        /// 把它放到移动中的球上，会把圆心跟着球一起带走，
        /// 画出一个对不上任何东西的偏心圆。
        ///
        /// 没有交叉淡变。对照正在运行的游戏实测，环就是完整尺寸
        /// 出现在停驻的球上、在整个飞行过程中完全不出现在
        /// 飞行中的球上。早先的版本在离开的球上留了一段衰减残影；
        /// 那就是环看起来在旋转中途消失的原因。
        ///
        /// 这里不能信原生 scrRing，有两个原因。它的闲时自转是
        /// 按 Time.timeSinceLevelLoad 走的，那是墙钟时间，对
        /// 由绝对播放头驱动的预览来说是错的；而且它的淡变是一条 DOTween，
        /// 每次 Switch 跑都会重写目标，所以在多数帧上都会发生的切换
        /// 永远收敛不了。
        private static void DriveRings(
            PlanetarySystem planetarySystem,
            scrPlanet chosen,
            int floorIndex,
            float floorElapsed,
            float playhead,
            float tileSize)
        {
            if (planetarySystem == null || chosen == null || planetarySystem.planetList == null)
            {
                return;
            }

            // 交接是一次交叉淡变：刚落地的那颗球淡入，刚离开的
            // 那颗淡出，用原生的 0.1s 线性。进度是从
            // 当前格的进入时间相对播放头来量的，而不是从
            // Time.unscaledDeltaTime 积分出来的，所以向后拖动能正确排空它，
            // 值也始终是播放头的纯函数。倒计时格没有交接
            // 要播，所以它一开始就是稳定状态。
            float fade = floorIndex <= 0 || RingFadeSeconds <= 0f
                ? 1f
                : Mathf.Clamp01(floorElapsed / RingFadeSeconds);

            for (int i = 0; i < planetarySystem.planetList.Count; i++)
            {
                scrPlanet planet = planetarySystem.planetList[i];
                if (planet == null)
                {
                    continue;
                }

                PlanetRenderer renderer = planet.planetRenderer;
                if (renderer == null || renderer.ringComp == null)
                {
                    continue;
                }

                // 求值器停在格中心的那颗球戴着环；绕着转的那颗
                // 不戴。`chosen` 是绕转的那颗——求值器把
                // 另一颗放在轴心上，所以「没被选中」正好就是「停在格上」。
                // 它就是刚落地的那颗，所以它淡入而另一颗淡出。
                bool carriesRing = planet != chosen;
                float percent = carriesRing ? fade : 1f - fade;

                // 环的缩放归求值器管，所以 scrRing.Update 必须被压住。
                // 它还启用着，而且跑在本域之后：对一颗不是
                // isChosen 的球，它会调 Switch(false)，让自己那条 DOTween 把 scalePercent
                // 往零拖，然后用那个值写 transform.localScale。让它继续跑，
                // 就是为什么一个位置和缩放都正确的环，会在每次旋转中途
                // 缩到没有。
                renderer.ringComp.enabled = false;

                if (percent <= 0.0001f)
                {
                    renderer.ringComp.gameObject.SetActive(false);
                    continue;
                }

                renderer.ringComp.gameObject.SetActive(true);
                if (renderer.ring != null)
                {
                    renderer.ring.enabled = true;
                }

                // 原生 UpdateRingSegments 不建圆；它只设材质上的
                // 虚线密度和每个玩家的相位偏移。仍然要调它，
                // 因为相位取决于这颗球归哪个玩家。
                renderer.ringComp.UpdateRingSegments();

                // 原生：transform.eulerAngles = back * (isCW ? 1 : -1) * time * 30。
                // 由播放头驱动，这样环的自转跟时间轴走，
                // 而不是跟墙钟走。
                float spin = (planetarySystem.isCW ? 1f : -1f) * playhead * 30f;
                renderer.ringComp.transform.localRotation = Quaternion.Euler(0f, 0f, spin);

                // 原生 Switch 把环的尺寸设成 tileSize，而环是球的
                // 子节点，所以球自己的缩放得除回去。一格
                // 正好就是行进中的球走的那一圈。
                float planetScale = planet.transform.localScale.x;
                if (Mathf.Abs(planetScale) < 1e-6f)
                {
                    planetScale = 1f;
                }
                renderer.ringComp.transform.localScale =
                    Vector3.one * (percent * tileSize / planetScale);
            }
        }
        private static readonly System.Collections.Generic.HashSet<scrPlanet> AwakePlanets =
            new System.Collections.Generic.HashSet<scrPlanet>();
        private static readonly System.Collections.Generic.HashSet<scrPlanet> ColouredPlanets =
            new System.Collections.Generic.HashSet<scrPlanet>();

        /// <summary>
        /// 把一颗球的粒子和环唤醒一次。PlanetRenderer.Revive 是
        /// 原生游戏每次重生时用的；liteRevive 不碰 hittable 和 emoji 表情
        /// 挂载点，与原生在非合作模式下的调用一致。
        /// </summary>
        private static void EnsureRendererAwake(scrPlanet planet, PlanetRenderer renderer)
        {
            if (renderer == null || AwakePlanets.Contains(planet))
            {
                return;
            }
            AwakePlanets.Add(planet);
            try
            {
                renderer.Revive(true);
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/Planet] revive failed: " + exception);
            }
        }

        /// <summary>
        /// 按原生加载器的方式应用玩家存的球颜色，好让
        /// 预览显示玩家自己的火/冰颜色，而不是默认值。
        /// </summary>
        private static void EnsureRendererColour(scrPlanet planet, PlanetRenderer renderer)
        {
            if (renderer == null || ColouredPlanets.Contains(planet))
            {
                return;
            }
            ColouredPlanets.Add(planet);
            try
            {
                renderer.LoadPlanetColor(planet.isRed);
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/Planet] colour load failed: " + exception);
            }
        }
        /// <summary>
        /// 只读。报告求值器设了什么、渲染器实际在
        /// 显示什么，这样「球看不见」能和「球没被放到该放的地方」
        /// 区分开：球自己的 Update 驱动 sprite.visible，而这个
        /// 预览让球的 Update 一直关着，所以只凭算出来的位置证明不了什么。
        /// </summary>
        private static void LogPlanetState(
            scrPlanet chosen,
            Vector3 position,
            int floorIndex,
            float time,
            double snappedAngle,
            double angle,
            PreviewFloorSnapshot floor,
            PreviewTimelineContext context,
            PlanetarySystem planetarySystem)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            PlanetRenderer renderer = chosen.planetRenderer;
            bool spriteVisible = false;
            bool spriteEnabled = false;
            try
            {
                if (renderer != null && renderer.sprite != null)
                {
                    spriteVisible = renderer.sprite.visible;
                    spriteEnabled = renderer.sprite.enabled;
                }
            }
            catch (Exception)
            {
                // 渲染器可能在重建；这一行剩下的部分仍然有用。
            }

            // 轨道锚在球上一次落地的位置，而不是格的
            // 中心，所以光靠求值器自己的数字没法核对几何：
            // 原生算出的格的角就印在旁边。
            scrFloor nativeFloor = context.GetFloorBinding(floorIndex);
            string native = "-";
            if (nativeFloor != null)
            {
                native = "e=" + nativeFloor.entryangle.ToString("0.000")
                    + " x=" + nativeFloor.exitangle.ToString("0.000")
                    + " len=" + nativeFloor.angleLength.ToString("0.000")
                    + " n=" + nativeFloor.numPlanets
                    + " ccw=" + nativeFloor.isCCW
                    + " r=" + nativeFloor.radiusScale.ToString("0.00")
                    + " t=" + nativeFloor.entryTime.ToString("0.00")
                    + " nt=" + (nativeFloor.nextfloor == null
                        ? "-"
                        : nativeFloor.nextfloor.entryTime.ToString("0.00"))
                    + " pos=" + nativeFloor.transform.position.x.ToString("0.00") + ","
                    + nativeFloor.transform.position.y.ToString("0.00")
                    + " eb=" + floor.ExtraBeats.ToString("0.00")
                    + " nentry=" + nativeFloor.nextfloor?.entryTime.ToString("0.00")
                    + " len=" + nativeFloor.angleLength.ToString("0.00")
                    + " act=" + floor.AngleCorrectionType.ToString("0")
                    + " corr=" + DescribePauseCorrection(floorIndex, snappedAngle, angle, floor)
                    + " nea=" + (nativeFloor.nextfloor == null
                        ? "-"
                        : nativeFloor.nextfloor.entryangle.ToString("0.000"));
            }

            Main.Logger.Log("[PlanetState] t=" + time.ToString("0.00")
                + " f=" + floorIndex
                + " pos=" + position.x.ToString("0.00") + "," + position.y.ToString("0.00")
                + " live=" + chosen.transform.position.x.ToString("0.00") + ","
                + chosen.transform.position.y.ToString("0.00")
                + " radius=" + chosen.cosmeticRadius.ToString("0.00")
                + " tile=" + (chosen.transform.position - position).magnitude.ToString("0.00")
                + " snap=" + snappedAngle.ToString("0.000")
                + " snapLive=" + chosen.snappedLastAngle.ToString("0.000")
                + " ang=" + angle.ToString("0.000")
                + " angLive=" + chosen.angle.ToString("0.000")
                + " | chosenIdx=" + chosen.planetIndex
                + " | planets " + DescribePlanets(planetarySystem, chosen)
                + " | native " + native
                + " | eval e=" + floor.EntryAngle.ToString("0.000")
                + " x=" + floor.ExitAngle.ToString("0.000")
                + " len=" + floor.AngleLength.ToString("0.000")
                + " enabled=" + chosen.enabled
                + " active=" + chosen.gameObject.activeInHierarchy
                + " spriteVisible=" + spriteVisible
                + " spriteEnabled=" + spriteEnabled + ".");
        }

        /// <summary>
        /// 报告每颗球相对当前格的位置以及它是不是被选中的
        /// 那颗，这样「球一落地就消失、另一颗出现」能和交接错了
        /// 区分开：如果两种描述指的是同一颗球，
        /// 那交接是对的，错的只是可见性规则。
        /// </summary>
        private static string DescribePlanets(PlanetarySystem planetarySystem, scrPlanet chosen)
        {
            if (planetarySystem == null || planetarySystem.planetList == null)
            {
                return "-";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < planetarySystem.planetList.Count; i++)
            {
                scrPlanet planet = planetarySystem.planetList[i];
                if (planet == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(planet.planetIndex)
                    .Append(planet == chosen ? "*" : "")
                    .Append("=(")
                    .Append(planet.transform.position.x.ToString("0.00")).Append(',')
                    .Append(planet.transform.position.y.ToString("0.00")).Append(')')
                    .Append(" f")
                    .Append(planet.currfloor == null ? "-" : planet.currfloor.seqID.ToString())
                    .Append(" vis=")
                    .Append(planet.planetRenderer != null && planet.planetRenderer.sprite != null
                        && planet.planetRenderer.sprite.visible ? "1" : "0")
                    .Append(" dead=")
                    .Append(planet.dead ? "1" : "0")
                    // 环是个 LineRenderer，它的缩放跟随 scalePercent *tileSize，
                    // 由 scrRing.Update 从 planet.isChosen 驱动。报告它实际的
                    // 缩放，是唯一能分辨到底哪颗球真正戴着环的办法，
                    // 而不是我们打算让哪颗戴。`act` 是 GameObject 自己的
                    // active 状态，也就是求值器直接切换的那个字段。
                    .Append(" ring=")
                    .Append(DescribeRing(planet))
                    .Append(" act=")
                    .Append(planet.planetRenderer != null && planet.planetRenderer.ringComp != null
                        && planet.planetRenderer.ringComp.gameObject.activeSelf ? "1" : "0");
            }
            return builder.ToString();
        }

        private static string DescribeRing(scrPlanet planet)
        {
            if (planet == null || planet.planetRenderer == null)
            {
                return "-";
            }

            PlanetRenderer renderer = planet.planetRenderer;
            try
            {
                if (renderer.ringComp == null)
                {
                    return "noComp";
                }

                // scrRing.Update 靠比较 planet.isChosen 来转移环，其中
                // isChosen 表示 planetarySystem.chosenPlanet == 环自己那颗球
                // 的引用。把环的球索引和这颗球的索引并排报出来，
                // 就能回答环是不是在看着它所属的那颗球。
                bool lineEnabled = renderer.ring != null && renderer.ring.enabled;
                bool active = renderer.ringComp.gameObject.activeInHierarchy;
                float scale = renderer.ringComp.transform.localScale.x;
                scrPlanet watched = renderer.ringComp.planet;
                return scale.ToString("0.00")
                    + "/c" + (renderer.ringComp.enabled ? "1" : "0")
                    + (lineEnabled ? "L1" : "L0")
                    + (active ? "A1" : "A0")
                    + " chos" + (planet.isChosen ? "1" : "0")
                    + " watch" + (watched == null ? "-" : watched.planetIndex.ToString())
                    + (watched == planet ? "=self" : "!=self");
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string DescribePauseCorrection(
            int floorIndex, double snappedAngle, double angle, PreviewFloorSnapshot floor)
        {
            if (floor == null || floor.ExtraBeats <= 0f)
            {
                return "-";
            }

            double num = floor.IsCounterClockwise ? -1d : 1d;
            double twoPi = Math.PI * 2d;
            double num2 = snappedAngle - Math.Floor(snappedAngle / twoPi) * twoPi;
            double num6 = angle - Math.Floor(angle / twoPi) * twoPi;
            double num7 = floor.AngleCorrectionType * (num2 - num6) * num
                * ((num6 < num2) ? 1d : -1d);
            return "n2=" + num2.ToString("0.000")
                + " n6=" + num6.ToString("0.000")
                + " off=" + (-num7).ToString("0.000");
        }

        /// <summary>scrMisc.mod：在整圈上的正取模。</summary>
        private static double Mod2Pi(double value)
        {
            const double twoPi = Math.PI * 2d;
            return value - Math.Floor(value / twoPi) * twoPi;
        }

        /// <summary>
        /// 从 `from` 到 `to` 走最短路径的有符号差，对齐原生角度修正
        /// 用的方向判断。暂停格最多只接受半圈的
        /// 修正，所以走最短路径才是对的。
        /// </summary>
        private static double ShortestDelta(double from, double to)
        {
            double delta = to - from;
            const double twoPi = Math.PI * 2d;
            while (delta > Math.PI)
            {
                delta -= twoPi;
            }
            while (delta < -Math.PI)
            {
                delta += twoPi;
            }
            return delta;
        }

        private static Vector3 AngleToVector(double angleRadians)        {
            return new Vector3((float)Math.Sin(angleRadians), (float)Math.Cos(angleRadians), 0f);
        }

        private static void KillCosmeticAngleTween(scrPlanet planet)
        {
            if (planet == null || CosmeticAngleTweenField == null)
            {
                return;
            }

            Tween tween = CosmeticAngleTweenField.GetValue(planet) as Tween;
            if (tween != null && tween.active)
            {
                tween.Kill(false);
            }
            CosmeticAngleTweenField.SetValue(planet, null);
        }
    }
}
