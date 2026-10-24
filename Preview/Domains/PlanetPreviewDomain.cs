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

        // 每格的球数（2 或 3）、活动列表的顺序、以及"这一格轮到列表里第几位"。
        // 由 Analyze 预计算，见 BuildMultiPlanetTrack。
        //
        // activeOrderByFloor 是每格 3 个槽位 id 的定长记录（不足的填 0）：
        // 0 = planetRed、1 = planetBlue、2 = planetGreen。
        private static int[] planetCountByFloor = new int[0];
        private static int[] chosenIndexByFloor = new int[0];
        private static int[] activeOrderByFloor = new int[0];

        public string Name
        {
            get { return "Planet"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            lastFloor = -1;
            BuildMultiPlanetTrack(context);
            Main.Logger.Log("[StaticPreview/Planet] ready for " + context.Floors.Count + " floors.");
        }

        /// <summary>
        /// 预计算每格的球数、活动列表的顺序、以及"这一格轮到列表里第几位"。
        ///
        /// 原生编辑器定位走的是 PlanetarySystem.ScrubToFloorNumber：先把列表收成
        /// 两颗，再逐格步进，遇到 numPlanets 变化就 SetNumPlanets 重排列表。
        /// 收成两颗之后 next/prev 指向同一颗，所以步进就是按格数前进一位。
        ///
        /// 这里把整件事写成对一个小槽位列表的操作，**不去碰游戏的对象**：
        ///   - 变多：新球插在**当前这颗**的位置上（SetNumPlanets 里的 Insert
        ///     用的是 chosenPlanet.planetIndex），当前这颗被挤到右边
        ///   - 变少：删掉的是当前这颗**前面**的那颗（GetMultiPlanet(idx, -1)）
        ///   - 中途旋转格交接给另一颗，方向反过来
        ///
        /// 新球永远是 planetGreen：原生从 availablePlanets[0] 取，那串的顺序是
        /// Green/Yellow/Purple/Pink/Orange/Cyan，而收缩时 Green 会被 Insert 回
        /// 队首，所以反复进出三球段拿到的都是它。
        ///
        /// 已用探针数据（planet-trajectory-mp.csv，3-2-5 第 0~67 格）逐格核对，
        /// 连列表顺序都对得上：第 54 格红、第 55 格进三球且当格蓝（此时列表是
        /// [Green,Red,Blue]）、之后 Blue→Green→Red 循环、第 67 格回双球是蓝。
        /// </summary>
        private void BuildMultiPlanetTrack(PreviewTimelineContext context)
        {
            int floors = context.FloorCount;
            planetCountByFloor = new int[floors];
            chosenIndexByFloor = new int[floors];
            activeOrderByFloor = new int[floors * 3];

            int[] order = new int[3];
            order[0] = 0;
            order[1] = 1;
            int size = 2;
            int index = 0;

            for (int i = 0; i < floors; i++)
            {
                PreviewFloorSnapshot snapshot = context.GetFloor(i);
                int count = snapshot == null ? 2 : Mathf.Clamp(snapshot.PlanetCount, 2, 3);

                if (i > 0 && count != size)
                {
                    if (count > size)
                    {
                        for (int k = size; k > index; k--)
                        {
                            order[k] = order[k - 1];
                        }
                        order[index] = 2;
                        index++;
                    }
                    else
                    {
                        int remove = ((index - 1) % size + size) % size;
                        for (int k = remove; k < size - 1; k++)
                        {
                            order[k] = order[k + 1];
                        }
                        index--;
                    }
                    size = count;
                }

                if (i > 0)
                {
                    index += snapshot != null && snapshot.MidSpin ? -1 : 1;
                }
                index = ((index % size) + size) % size;

                planetCountByFloor[i] = count;
                chosenIndexByFloor[i] = index;
                for (int k = 0; k < count; k++)
                {
                    activeOrderByFloor[i * 3 + k] = order[k];
                }
            }
        }

        /// <summary>scrMisc.GetInverseAnglePerBeatMultiplanet：π(n-2)/n。双球是 0，三球是 π/3。</summary>
        private static double InverseAnglePerBeatMultiplanet(int planets)
        {
            return Math.PI * (planets - 2.0) / planets;
        }

        private static int multiPlanetColoursAppliedFor = -1;

        /// <summary>
        /// 多出来的那几颗球的原生配色。
        ///
        /// 不能走 EnsureRendererColour：那条调的是 PlanetRenderer.LoadPlanetColor(bool)，
        /// 只分得出红/蓝，而 planetGreen 的 isRed 是 false，会被刷成蓝色——
        /// 这就是"第三颗颜色不对"。
        ///
        /// 原生给多出来的球上色走的是 PlanetarySystem.ApplyMultiplanetColors()：
        /// 它按**对象名**上色（planetGreen 就是绿），也认得自定义预设。
        ///
        /// 球数回到 2 时把标记清掉，这样下一次再进三球段会重新上一次色。
        /// </summary>
        private static void ApplyMultiPlanetColours(PlanetarySystem system, int count)
        {
            if (system == null)
            {
                return;
            }

            if (count <= 2)
            {
                multiPlanetColoursAppliedFor = -1;
                return;
            }

            if (multiPlanetColoursAppliedFor == count)
            {
                return;
            }

            multiPlanetColoursAppliedFor = count;
            try
            {
                system.ApplyMultiplanetColors();
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/Planet] multi-planet colours failed: " + exception);
            }
        }

        /// <summary>
        /// 场上该显示哪几颗球，和原生保持同步。
        ///
        /// 球数变少时原生由 SetNumPlanets 对多出来的那颗调 PlanetRenderer.Destroy()：
        /// 它不只关 sprite，还把 tailParticles / coreParticles / sparks 停掉、
        /// glow 关掉——只关 sprite 的话尾焰会留在画面上（实测就是这个现象）。
        /// 所以这里直接调它，别自己拼。
        ///
        /// 反过来，从隐藏切回显示时要走 Revive()，对应原生的 Rewind() → Revive()。
        /// 不能只靠上面循环里的 EnsureRendererAwake：那是"每颗球只做一次"的，
        /// 一颗球被收起来之后再放出来它就不会再唤醒了。
        ///
        /// 只看 red/blue/green 这三颗：它们才是求值器会摆上场的。
        /// </summary>
        private static void SyncPlanetVisibility(
            PlanetarySystem system, scrPlanet a, scrPlanet b, scrPlanet c, int count)
        {
            for (int i = 0; i < 3; i++)
            {
                scrPlanet planet = SlotPlanet(system, i);
                if (planet == null)
                {
                    continue;
                }

                PlanetRenderer renderer = planet.planetRenderer;
                if (renderer == null)
                {
                    continue;
                }

                bool active = (count > 0 && planet == a)
                    || (count > 1 && planet == b)
                    || (count > 2 && planet == c);

                if (active)
                {
                    if (shownPlanets.Add(planet))
                    {
                        try
                        {
                            // 放回场上走原生的 Rewind()（SetNumPlanets 插球时就是
                            // 调它）：它内部会 Revive()，把 scrPlanet.dead 清掉。
                            // PlanetRenderer.Revive(lite) 只碰渲染器，清不掉那个
                            // 旗标——而收起时 Die() 把它设成了 true，不清的话
                            // 这颗球再摆出来也是"死的"。
                            planet.Rewind();
                        }
                        catch (Exception exception)
                        {
                            Main.Logger.Error("[StaticPreview/Planet] revive failed: " + exception);
                        }
                    }

                    if (renderer.sprite != null)
                    {
                        renderer.sprite.visible = true;
                    }
                }
                else if (shownPlanets.Remove(planet))
                {
                    try
                    {
                        // 收起时照原生的**播放**路径走：scrPlanet.Die(0.3f)。
                        // 它内部就是 planetRenderer.Explode(0.3f)——球是**爆炸**
                        // 消失的，原版就是这样（实测确认：原版绿球爆炸消失，
                        // 只关 sprite 会变成"直接消失"，少一记爆炸）。
                        //
                        // 原生只有编辑器定位那条路（SetNumPlanets 传了
                        // scrubChosen）才用 Destroy() 绕开爆炸。预览要对齐的是
                        // 播放，所以用 Die。
                        planet.Die(0.3f);
                    }
                    catch (Exception exception)
                    {
                        Main.Logger.Error("[StaticPreview/Planet] hide failed: " + exception);
                    }
                }
            }
        }

        /// <summary>槽位 id → 场上的球。0 = planetRed，1 = planetBlue，2 = planetGreen。</summary>
        private static scrPlanet SlotPlanet(PlanetarySystem system, int slot)
        {
            if (system == null)
            {
                return null;
            }

            switch (slot)
            {
                case 1:
                    return system.planetBlue;
                case 2:
                    return system.planetGreen;
                default:
                    return system.planetRed;
            }
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
                // 多行星格现在建模到 3 颗——原生自己也是把 numPlanets 夹在
                // 2..3 之间（MultiPlanet 事件判定的分支里 `Math.Min(num7, 3)`），
                // 所以 >3 这条其实走不到，留着是给将来放宽时当闸门用。
                //
                // **freeroam 也不在这里**：探针量到它和普通格只差"整段时长装在
                // extraBeats 里"，角度照样每拍 π（见下面 sweep 那段）。
                bool complexFloor = floor.PlanetCount > 3
                    || (frame.Context.GetFloor(floorIndex - 1) != null
                        && frame.Context.GetFloor(floorIndex - 1).PlanetCount > 3)
                    || (frame.Context.GetFloor(floorIndex + 1) != null
                        && frame.Context.GetFloor(floorIndex + 1).PlanetCount > 3);

                if (complexFloor)
                {
                    // 旧的兜底调的是原生 ScrubToFloorNumber 检查点重生路径，
                    // 那条路依赖游戏自己的模式机制，而求值器接管的预览刻意
                    // 不进那套机制。把球留在原地，能保住预览其余部分正确。
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
            multiPlanetColoursAppliedFor = -1;
            RestoreCosmeticAngles();
            AwakePlanets.Clear();
            ColouredPlanets.Clear();
            shownPlanets.Clear();
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

            // 这一格场上有哪几颗球、轮到哪颗停在格上，都在 Analyze 里预计算好了
            // （见 BuildMultiPlanetTrack）。这里只做查表。
            //
            // 为什么不直接对活的 planetList 步进：那个列表有 8 个元素——
            // planetRed、planetBlue，外加 6 颗给多球格准备的克隆（全停在原点、
            // currfloor 为空）。对 8 元环步进，第 N 格算出的是 planetList[N % 8]，
            // 只有 N % 8 恰好是 0 或 1 时才对。
            //
            // 这个 bug 在长按上看得最清楚。探针实测「测试1」第 25 格：原生选中球
            // 是 Blue，而按 N % 8 算出来 parked 也是 Blue（碰巧对），chosen 却取到
            // planetList[2] = Cyan——一颗看不见的克隆。结果真球 Red 被摁在滑移线上
            // 直着往前推，该绕转的那颗根本没在飞。
            int count = planetCountByFloor[floorIndex];
            int chosenSlot = chosenIndexByFloor[floorIndex];
            int orderBase = floorIndex * 3;

            scrPlanet parked = SlotPlanet(planetarySystem, activeOrderByFloor[orderBase + chosenSlot]);
            // movingToNext：原生 Update_RefreshAngles 里 `movingToNext = next`，
            // 也就是活动列表里排在当前这颗后面的那颗。
            scrPlanet flying = count > 1
                ? SlotPlanet(planetarySystem, activeOrderByFloor[orderBase + (chosenSlot + 1) % count])
                : null;
            // 多边形上那颗：原生那个 `for (i = 2; i < num2; i++)` 的循环，
            // num2 = 3 时只有 i = 2 一个。
            scrPlanet third = count > 2
                ? SlotPlanet(planetarySystem, activeOrderByFloor[orderBase + (chosenSlot + 2) % count])
                : null;

            if (parked == null || flying == null)
            {
                return;
            }

            // 三球格上多出来的那颗的原生配色（红/蓝那两颗走下面循环里的
            // EnsureRendererColour）。
            ApplyMultiPlanetColours(planetarySystem, count);

            // **原生里 isChosen 的是"停在格上"的那颗，不是飞的那颗。**
            //
            // scrPlanet.LateUpdate：
            //   if (currfloor.stickToFloor && isChosen && ...)
            //       transform.position = currfloor.transform.position + holdOffsetPos + addBob;
            // 也就是说被"选中"的球停在格中心、戴着环；**在轨道上飞的是另一颗**。
            // 探针也印证了：第 0 格 isChosen 是 PlanetRed，原版画面上正是红球戴环停在 0 号砖上；
            // 三球段的每一帧也都量到"选中那颗离格心的距离恰好是 0"。
            //
            // 求值器以前把这两个角色用反了——让 isChosen 那颗去飞、另一颗停在中心，
            // 于是屏幕上两颗球的角色整个对调，看起来就是"颜色反了"。
            // 所以 `parked` 是游戏认定的 isChosen，`chosen` 是飞的那颗。
            scrPlanet chosen = flying;

            planetarySystem.chosenPlanet = parked;
            KillCosmeticAngleTween(chosen);
            chosen.cosmeticAngle = 0f;

            scrFloor boundFloor = context.GetFloorBinding(floorIndex);
            if (boundFloor == null)
            {
                return;
            }
            chosen.currfloor = boundFloor;
            parked.currfloor = boundFloor;

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

            // 多行星格起步角还多一项。原生落地时：
            //
            //   num3 = scrMisc.GetInverseAnglePerBeatMultiplanet(floor.numPlanets) * dir;
            //   movingToNext.snappedLastAngle = mod(exitAngle + PI, 2π) + num3;
            //
            // 而 GetInverseAnglePerBeatMultiplanet(n) = π(n-2)/n，双球是 0（所以双球
            // 一直看不出问题），三球是 π/3。
            //
            // 探针实测（3-2-5 第 55 格第一帧）：entry=4.7124、ang=5.7974，差 1.0850；
            // 而 π/3 + 0.024×(π/2) = 1.0849 ✓。
            if (count > 2)
            {
                snappedAngle += direction * InverseAnglePerBeatMultiplanet(count);
            }
            double cosmeticAngle = 0d;

            // **freeroam 不走这里**：它的 extraBeats 装的是整段时长，命中这个
            // 条件会套上 Pause 事件的 snappedAngle 公式和 cosmetic 偏移，
            // 而 freeroam 的起点角不是那么来的。实测（planet-trajectory-free.csv）
            // freeroam 里球就是"从进入角起步、每拍 π"，和普通格一样。
            if (floorIndex > 0 && floor.ExtraBeats > 0f && !floor.FreeRoam)
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
                // **完整式子，不要化简成 `-type * |Δ|`。**
                //
                // 原生是：
                //   num7 = angleCorrectionType * (num2 - num6) * direction
                //          * (num6 < num2 ? 1 : -1);
                //   TweenCosmeticAngle(-num7);
                //
                // 以前这里化简成了 `-type * |entryMod - startMod|`，把 direction 和
                // 那个比较产生的符号一起丢掉了。NYA 那张谱两个方向恰好都对得上，
                // 所以一直没暴露；Dream Flower 第 174 格（type=-1、dir=-1、
                // startMod > entryMod）就露了：实测原生 cosmetic 是 **-π**，
                // 化简式给的是 **+π**——两者差 2π 看着一样，但**衰减过程中的中间值
                // 差半圈**，表现正是"飞行中角度不对、落地却对"（它一拍内衰减到 0）。
                double correctionSign = startMod < entryMod ? 1d : -1d;
                cosmeticAngle = -floor.AngleCorrectionType * (entryMod - startMod)
                    * direction * correctionSign;
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
                // 暂停格和 freeroam 共用这条，但**扫角不能按 (1+extraBeats) 拍算**。
                //
                // 原生这一段时长是「走完该格角度的时间 + extraBeats 拍」
                // （CalculateFloorEntryTimes：num4 = GetTimeBetweenAngles(entry, exit)，
                // 再加 extraBeats * 一拍），而球每拍推进 π。
                // 以前写成 π*(1+extraBeats)，等于假设该格的角度时间恰好是 1 拍——
                // 180° 的暂停格确实如此（NYA 161 格就是，所以这个错一直没暴露），
                // 但 270° 的格（angleLength 1.5π）就差半圈。
                //
                // 实测 AnRain 第 10 格：原生从 −4.7124 扫到 +1.5708（=2π），
                // AES 只扫到 0（=1.5π），表现就是球"还差一点角度就突然落地"。
                //
                // context 里现成的 floorTravelAngle 就是这一段该扫的角度
                // （单位是度，每拍 180 度），用它可以自动对上真实拍数。
                sweep = context.GetFloorTravelAngle(floor) * Math.PI / 180d;
                // 一拍多长也跟着它走：球每拍扫 π，所以拍数 = sweep/π。
                // 暂停格的 cosmetic 衰减是按拍算的，用错会让它慢下来。
                beatSeconds = sweep <= 1e-6d ? beatSeconds : tileDuration * Math.PI / sweep;
            }
            else
            {
                sweep = floor.AngleLength;
            }

            double progress = tileDuration <= 1e-6d
                ? 0d
                : Math.Min(1d, elapsed / tileDuration);
            double angle = snappedAngle + progress * sweep * direction;

            // freeroam 出场那条 cosmetic（下面算完 renderedAngle 之后叠上去）。
            //
            // 原生在出场那一刻把球的角度**重设**了一次：
            //
            //   num3 = nextfloor.isCCW ? -1 : 1;
            //   num4 = nextfloor.entryangle - π * num3;
            //   snappedLastAngle = num4 - π * outBeats * num3;
            //   ...
            //   num5 = mod(angle);  TweenCosmeticAngle(-(num5 - num2));   // num2 = 重设前的 mod
            //
            // 那一项只是把这次重设造成的**视觉跳变**抹平：TweenCosmeticAngle 把
            // cosmeticAngle 设成那个偏移、再在一拍内 OutSine 衰减到 0。
            //
            // 这里**不重设硬值**——两边角速度一样（都是每拍 π），所以"重设硬值 +
            // 叠 cosmetic"和"保留原角 + 叠同一个 cosmetic"逐帧完全等价，后者改动
            // 小得多。硬值这边原生落地时会再写一次（`mod(exit+π) + IAPB(n)·dir`），
            // 实测 f=33 的 snappedLastAngle 正好等于该格 entryangle，没有半圈差。
            double freeExitCosmetic = 0d;
            double freeExitStart = 0d;
            double freeExitSnapped = 0d;
            int freeExitDirection = (int)direction;
            bool freeExitActive = false;
            if (floor.FreeRoam && floorIndex + 1 < context.FloorCount)
            {
                scrFloor entryBinding = context.GetFloorBinding(floorIndex);
                PreviewFloorSnapshot nextFloor = context.GetFloor(floorIndex + 1);
                if (entryBinding != null && nextFloor != null
                    && entryBinding.freeroamEndEarlyBeats > 0
                    && beatSeconds > 1e-6d)
                {
                    double outBeats = entryBinding.freeroamEndEarlyBeats;
                    freeExitStart = floor.NextEntryTime - beatSeconds * outBeats;
                    if (frame.Time >= freeExitStart)
                    {
                        // 出场把硬值重设成这条（num4 - π*outBeats*num3），方向随
                        // 下一格（原生先 `item.isCCW = item.nextfloor.isCCW`）。
                        int exitDirection = nextFloor.IsCounterClockwise ? -1 : 1;
                        freeExitDirection = exitDirection;
                        freeExitSnapped = nextFloor.EntryAngle
                            - Math.PI * exitDirection
                            - Math.PI * outBeats * exitDirection;
                        freeExitActive = true;

                        // cosmetic 的起始值：把重设那一瞬的视觉接过去。**必须和
                        // 硬值的重设一起用**——只加 cosmetic 不重设硬值的话，
                        // 渲染角会当场跳掉这一整份（实测就是"进倒计时突然差 180°"）。
                        double beforeRaw = snappedAngle
                            + ((freeExitStart - floor.EntryTime) / tileDuration) * sweep * direction;
                        double afterRaw = freeExitSnapped;
                        freeExitCosmetic = Mod2Pi(beforeRaw) - Mod2Pi(afterRaw);
                    }
                }
            }

            // 出场开始后硬值换成重设那条（角速度不变，起点变了）。
            if (freeExitActive)
            {
                angle = freeExitSnapped
                    + (frame.Time - freeExitStart) / beatSeconds * Math.PI * freeExitDirection;
            }

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

            // freeroam 出场那条 cosmetic。和上面暂停格那条形状相同，但衰减是
            // 从**出场时刻**起算的，不是从进格时刻。
            if (freeExitCosmetic != 0d && beatSeconds > 1e-6d)
            {
                double f = Math.Min(1d, (frame.Time - freeExitStart) / beatSeconds);
                renderedAngle += freeExitCosmetic * (1d - Math.Sin(f * Math.PI * 0.5d));
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
            // 行星绕着的中心。走 context 的 ResolveBallCentre，定义只留一份。
            // 它和快照的 BallPosition 有一处要紧的区别：粘住的格现读**活的**
            // transform.position，因为 MoveTrack 是直接对那个字段起 tween 的
            // （ffxMoveFloorPlus），位移不进 offsetPos——只读快照的话，
            // 轨道动到哪球都还停在原处。
            Vector3 centre = frame.Context.ResolveBallCentre(floorIndex);

            // freeroam 段：圆心不是入口格，是**球所在的那颗网格格**。
            //
            // 原生里球的 currfloor 在 freeroam 期间换成了生成格（探针实测：
            // f=32 那一段 gen=1、grid=(0,0)），而球是被 LateUpdate 钉在
            // currfloor.position 上的。那颗格平时就摆在入口格的位置，所以两者
            // 一开始是一样的；但出场动画会把它挪到下一格的入口边缘，球要跟着走
            // （原生 stickToFloors 时不给球单独起 tween，就是靠这个）。
            if (floor.FreeRoam && FreeRoamPreviewDomain.BallTileValid)
            {
                centre = FreeRoamPreviewDomain.BallTilePosition;
            }

            // 长按格的圆心还会**顺着轨道滑向下一格的入口边缘**，绕转是绕着
            // 这个移动中的心做的；普通格的圆心就钉在格中心。
            //
            // 原生把那一步混在 holdOffsetPos 里（scrPlanet.Update 的长按分支），
            // 最终位置是：
            //   actualPos = 格中心 + holdOffsetPos + 绕转向量 * 半径
            // 而 holdOffsetPos 里的 storedPosition 会和别处抵消掉，展开之后
            // 滑移就是一个纯插值：
            //
            //   target = 下一格的中心
            //            - tileSize * 下一格的 radiusScale
            //              * ClockwiseAngleToVector(本格的 exitangle)
            //
            // 也就是「下一格的入口边缘」。
            //
            // progress 用和角度同一条线性进度：原生用的是带 rotationEase 的
            // easedPerc，但本域算角度时本来就没走缓动（planetEase 还没建模），
            // 两边用同一条才不会出现"转的是缓动、滑的是匀速"。
            if (floor.HoldLength > -1)
            {
                PreviewFloorSnapshot nextFloor = frame.Context.GetFloor(floorIndex + 1);
                if (nextFloor != null)
                {
                    Vector3 exitEdge = AngleToVector(floor.ExitAngle)
                        * (float)(context.TileSize * nextFloor.RadiusScale);
                    Vector3 holdTarget = frame.Context.ResolveBallCentre(floorIndex + 1) - exitEdge;
                    centre = Vector3.LerpUnclamped(centre, holdTarget, (float)progress);
                }

                // 滑移上还叠了一层小幅摆动。原生 scrPlanet.Update 的长按分支：
                //
                //   addBob = tileSize * 0.1f * Vector2.one * sin(angle - exitangle)
                //   transform.position = storedPosition + addBob + holdOffsetPos;
                //
                // 注意两个细节：它是 **Vector2.one**，也就是沿对角线摆、
                // 不是垂直于长按线；幅度是 0.1 * tileSize。原版里那颗球
                // 走出"波浪"就是它。
                //
                // 探针实测（测试1 第 25 格，tileSize=1.5）：t=11.332 那一帧
                // ang=6.0892、exit=1.5708 → 0.15*sin(4.5184) = -0.1472，
                // 而同帧「球位置 − 格心」的垂直分量正好是 -0.1472。
                // 逐帧核过，四位小数都对得上。
                //
                // 加在 centre 上、而不是只加给停着的那颗：飞的那颗在原生的
                // 锚点（tempTransPos）就是滑动那颗的位置，所以两边一起摆，
                // 飞的那条的轨迹才是一圈绕着**正在摆动的**圆心。
                if (floor.HoldDistance > 0f)
                {
                    float bob = (float)Math.Sin(angle - floor.ExitAngle) * 0.1f * context.TileSize;
                    centre += new Vector3(bob, bob, 0f);
                }

                DriveHoldLine(frame, floorIndex, progress, chosen);
            }
            else
            {
                // 刚走过去的那条长按线要填满。原生在落到下一格时把
                // holdCompletion 设成 1；求值器只碰当前格，不补这一步的话，
                // 球走过去之后那条线会停在它离开时的进度上。
                CompleteHoldLine(frame, floorIndex - 1);
            }

            Vector3 offset = AngleToVector(renderedAngle) * (float)radius;
            Vector3 position = centre + offset;

            // 位置在这里算，而不是去调原生
            // Update_RefreshAngles：那个 helper 从
            // conductor.songposition_minusi 推导角度，而求值器接管的预览驱动自己的
            // 时钟、不写 conductor，所以那个 helper 会读到陈旧的时间。
            // 墙钟字段仍然会设，这样其他读它们的家伙（比如跟随
            // 相机）跟屏上的东西保持一致。
            // 飞的那颗**只写位置和半径，不写角度字段**——这是照原生的。
            //
            // 原生每帧推进角度的只有 isChosen 那颗（= 停着的那颗，Update_RefreshAngles
            // 只对它跑）；飞的那颗自己的 angle / cosmeticAngle 是**旧值**，它只有
            // 位置是被那句 `movingToNext.transform.position = actualPos` 算出来的。
            // 渲染器仍然按各自的 angle 画朝向，所以原版里飞的那颗朝向就是陈旧的。
            //
            // 以前这里把角度写给了飞的那颗——角色正好反了，停着那颗于是留着上一格
            // 的角（差半圈）。上一版改成两颗都写，把飞的那颗本应陈旧的朝向也写新了，
            // 于是还剩一颗差半圈。现在是只写停着那颗。
            chosen.cosmeticRadius = (float)radius;
            chosen.transform.position = position;

            // 停着的那颗（游戏认定的 isChosen）写全套角度。
            double ballCosmetic = renderedAngle - angle;
            ApplyBallAngle(parked, angle, ballCosmetic, snappedAngle,
                floorIndex == 0 ? snappedAngle + floor.AngleLength
                    : snappedAngle + floor.AngleLength * direction, (float)radius);

            // 三球格上还有第三颗。原生那个多边形循环
            // （Update_RefreshAngles 里 `for (i = 2; i < num2; i++)`）在 num2=3 时
            // 只跑 i=2，就是它。
            //
            // 位置不照抄那段公式，是**按探针数据定的**：三球段逐帧量到
            //   - 选中那颗离格心 0（27126 帧全是 0.0）
            //   - 另外两颗离格心都恰好是一个 cosmeticRadius，相对角 0 和 −π/3
            // 也就是"以格心为一个顶点、边长 = R 的正三角形"——两两相距也正好是 R。
            //
            // （探针里 26922 帧量到相对角 0、1161 帧量到 −0.33π。后者帧数少
            //   是因为原生那句 `if (!p.dead)` 会让第三颗在部分帧不摆、停在老位置，
            //   那种帧量到的距离是 37.7 这种离群值。这里按"摆"的那一支做。）
            //
            // 原生那段多边形公式按着展开得到的是 1.38R 上的点，和实测形状对不上，
            // 所以以实测为准。
            if (third != null)
            {
                double thirdAngle = renderedAngle - direction * Math.PI / 3.0;
                third.angle = thirdAngle;
                third.cosmeticAngle = 0f;
                third.cosmeticRadius = (float)radius;
                third.transform.position = centre + AngleToVector(thirdAngle) * (float)radius;
            }

            // 场上的球都在屏上：停着的那颗在圆心，飞的那颗在圆上绕，
            // 三球格上第三颗也在圆上（长按期间圆心会滑，几颗一起跟着挪）。
            //
            // 这里修掉了早先两个错误。以前只把选中的球藏起来，于是玩家刚
            // 落地的那颗球在另一颗出现的同一刻消失；而且空闲的那颗球根本
            // 没被摆过，就停在它加载时被留在的地方。求值器接管预览期间
            // 球自己的 Update 是关的，所以无论姿态还是 sprite 可见性
            // 都不会被别的东西还原。
            //
            // 以前这里写的是 boundFloor.transform.position（实位），
            // 而这个开关关掉时选中球已经改用 startPos 了。两颗球于是
            // 被摆到两个不同的地方：日志里 f7 是
            //   选中球 (9.00,0.03) 按 layout 算，非选中球 (12.00,-1.50) 还在实位，
            // 差 2.12 —— 正是"黏性轨道没生效"看起来的样子。
            //
            // 只摆场上这几颗。planetList 里另外那几颗是给多球格准备的克隆，
            // 把它们的 sprite 打开、或者把它们也摆进来，画面上就会多出球来。
            for (int i = 0; i < count; i++)
            {
                scrPlanet planet = i == 0 ? parked : (i == 1 ? flying : third);
                if (planet == null)
                {
                    continue;
                }

                if (planet == parked)
                {
                    // 停着的那颗 = 游戏认定的 isChosen。它在原生里的位置是
                    // `currfloor.transform.position + holdOffsetPos + addBob`
                    // （scrPlanet.LateUpdate），长按格上则由 Update 的长按分支
                    // 换成滑移位置——两者都正好是圆心，所以摆在 centre 上。
                    //
                    // 以前这里存疑过"它到底该不该绕"，试过两次让它绕都坏了。
                    // 探针给了答案：**该绕的是飞的那颗**，这颗就该停在圆心
                    // （三球段逐帧量到它离格心恰好 0）。
                    planet.transform.position = centre;
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
                //
                // **多出来的球不能走这里**：LoadPlanetColor 只认 isRed，
                // planetGreen 会被刷成蓝色，它走 ApplyMultiPlanetColours。
                //
                // 判据必须是**球本身**，不能按"第几颗"——绿球在三球段里会
                // 轮流当停着/飞的那颗，按位置排除会漏掉它转到别的位子的那些格。
                if (planet == planetarySystem.planetRed || planet == planetarySystem.planetBlue)
                {
                    EnsureRendererColour(planet, renderer);
                }
            }

            // 收缩时把多出来的那颗收起来（原生是在 SetNumPlanets 里 Destroy 的）。
            SyncPlanetVisibility(planetarySystem, parked, flying, third, count);

            DriveRings(planetarySystem, parked, chosen, third, floorIndex,
                (float)elapsed, frame.Time, context.TileSize);

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
            scrPlanet parked,
            scrPlanet chosen,
            scrPlanet third,
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

            // 只驱动场上那几颗。planetList 里另外那些是给多球格准备的克隆，
            // 它们不在场上，不该给它们开环。
            for (int i = 0; i < 3; i++)
            {
                scrPlanet planet = i == 0 ? parked : (i == 1 ? chosen : third);
                if (planet == null)
                {
                    continue;
                }

                PlanetRenderer renderer = planet.planetRenderer;
                if (renderer == null || renderer.ringComp == null)
                {
                    continue;
                }

                // 环只戴在"停在格上"的那颗（游戏认定的 isChosen）身上。
                // 三球格上另外两颗都在飞/绕，都不戴。
                bool carriesRing = planet == parked;
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
        /// 求值器此刻**显示着**的球。球数变化时用它判断谁需要 Destroy（收起）
        /// 谁需要 Revive（重新唤醒），见 SyncPlanetVisibility。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<scrPlanet> shownPlanets =
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

        /// <summary>
        /// 把一组角度写进一颗球。
        ///
        /// 只给**停着的那颗**（游戏认定的 isChosen）用。原生每帧推进角度的就是
        /// 它——Update_RefreshAngles 只对 isChosen 跑；飞的那颗自己的 angle 是旧值，
        /// 它只有位置是被算出来的（`movingToNext.transform.position = actualPos`）。
        /// 详见调用处。
        /// </summary>
        private static void ApplyBallAngle(
            scrPlanet planet, double angle, double cosmetic, double snapped,
            double targetExit, float radius)
        {
            if (planet == null)
            {
                return;
            }

            planet.angle = angle;
            planet.cosmeticAngle = (float)cosmetic;
            planet.SetSnappedLastAngle(snapped);
            planet.SetTargetExitAngle(targetExit);
            planet.cosmeticRadius = radius;
        }

        /// <summary>scrMisc.mod：在整圈上的正取模。</summary>
        private static double Mod2Pi(double value)
        {
            const double twoPi = Math.PI * 2d;
            return value - Math.Floor(value / twoPi) * twoPi;
        }

        /// <summary>
        /// 长按那条线：球走过多少就填多少颜色。
        ///
        /// 原生在 hold 分支里每帧做两件事——把 touchColor 设成**当前这颗球**的
        /// 颜色（红球走过去就填红、蓝球就填蓝），然后按进度填。
        /// 求值器以前完全没碰它，所以那条线一路不变色。
        ///
        /// **两个字段都要写，只写 holdCompletion 是不够的。**
        /// scrHoldRenderer 有**自己的 Update**，每帧最后一句是
        ///
        ///   UpdateColor(startFloor.holdCompletionEased);
        ///
        /// 读的是 `holdCompletionEased`，而不是 `holdCompletion`——原生在
        /// scrPlanet.Update 的 hold 分支里两个都写
        /// （`holdCompletion = perc; holdCompletionEased = easedPerc;`），
        /// 而求值器期间那个组件是关掉的（FreezeStaticPreviewNativeUpdates），
        /// 于是 `holdCompletionEased` 冻在关卡加载时的 0。
        ///
        /// 结果就是每帧两个写入者抢调色板上的 `_Completion`：原生写 0，
        /// 这里写 progress。拖动时指针不动的帧求值器不重算，线被擦回 0，
        /// 一动又填上——看起来就是**一直闪**；播放时求值器的写入总被
        /// 原生那次覆盖，看起来就是**根本没变色**。
        ///
        /// 解法不是把 scrHoldRenderer 压住（它还得跑 UpdateMesh，移动轨道
        /// 上的长按线要靠它跟位置），而是把状态补全到和原生一致：
        /// 这样原生每帧那一下写的正好是我们想要的，谁先谁后都对。
        /// </summary>
        private static void DriveHoldLine(
            PreviewEvaluationFrame frame, int floorIndex, double progress, scrPlanet chosen)
        {
            scrFloor holdFloor = frame.Context.GetFloorBinding(floorIndex);
            if (holdFloor == null || holdFloor.holdRenderer == null)
            {
                return;
            }

            holdFloor.holdCompletion = (float)progress;
            holdFloor.holdCompletionEased = (float)progress;
            if (chosen != null && chosen.planetRenderer != null)
            {
                holdFloor.holdRenderer.touchColor =
                    chosen.planetRenderer.planetColor.ToRealColor();
            }
            holdFloor.holdRenderer.UpdateColor((float)progress);
        }

        /// <summary>
        /// 走过的那条长按线填满。
        ///
        /// 带变化检测：只有还没填满时才写，否则每一帧都会去动一次网格。
        /// 两个进度字段都要补，理由见 DriveHoldLine。
        /// </summary>
        private static void CompleteHoldLine(PreviewEvaluationFrame frame, int floorIndex)
        {
            if (floorIndex < 0)
            {
                return;
            }

            scrFloor holdFloor = frame.Context.GetFloorBinding(floorIndex);
            if (holdFloor == null || holdFloor.holdRenderer == null
                || (holdFloor.holdCompletion >= 1f && holdFloor.holdCompletionEased >= 1f))
            {
                return;
            }

            holdFloor.holdCompletion = 1f;
            holdFloor.holdCompletionEased = 1f;
            holdFloor.holdRenderer.UpdateColor(1f);
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
