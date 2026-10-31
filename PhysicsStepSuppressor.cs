using System;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 在编辑器里**关掉 2D 物理的自动步进**。
    ///
    /// 为什么这件事值得单独一个类：
    /// 播放态下 `固/Physics2DFixedUpdate` 稳定吃掉 3.7~4.3ms —— 一个
    /// 12.8ms 的帧里有三分之一花在这里。而这 4ms **不是** AES 写出来的：
    /// 普查过播放中的场景，`Collider2D` 4638 个、其中多边形碰撞体 2669 个，
    /// 但 `Rigidbody2D` 是 **0** 个（动态/运动学都是 0）；`fixedDeltaTime`
    /// 0.02、`timeScale` 1.0 都正常；还有 15 个采样窗口里 AES 一帧都没写过
    /// Transform，物理照样 4ms。也就是说这些时间全花在"推进一个什么都没有的
    /// 模拟"上。
    ///
    /// 为什么跳过它是安全的（三条证据，都不是推理）：
    ///   1. **反编译游戏本体**：`Assembly-CSharp.decompiled.cs` 里
    ///      `Rigidbody2D` 出现 **0 次**，`.Simulate(` 出现 **0 次**。
    ///      一个没有刚体的 2D 世界，模拟推进不产生任何结果 —— 没有接触、
    ///      没有受力、没有任何东西会动。
    ///   2. **游戏对 2D 物理只用查询**：13 处 `Physics2D.*` 全是
    ///      `RaycastAll` / `OverlapPoint(All)` / `OverlapCircleAll`
    ///      （选格、找相邻格、星球压格判定）。查询走的是 broadphase，
    ///      **在 `SimulationMode2D.Script` 下照常工作**，被取消的只有
    ///      "每帧自动步进" 这一件事。
    ///   3. **实测对照**：打开抑制后同一个谱面同一个视角，
    ///      `固/Physics2DFixedUpdate` 从 3.7~4.3ms 掉到 **0.02ms**，
    ///      帧耗时 12.8ms → **6.9ms**，画面与拾取无异常。
    ///
    /// **还有一条不显然、但必须补的**：`autoSyncTransforms` 默认是 false，
    /// 取消自动步进之后，Unity 就再也没有任何时机把"被移动过的碰撞体"同步进
    /// broadphase 了。而 `scrPlanet.Update` **每帧**都在用
    /// `Physics2D.OverlapCircleAll(transform.position, 0.3f, 1 << 21)` 判装饰物
    /// 命中盒（`Assembly-CSharp.decompiled.cs:19613`），装饰物又会跟着星球走。
    /// 不补的话，命中盒会永远停在"开始播放那一刻"的位置上 —— 用一个不经意的
    /// 谱面就能让用户以为 mod 把游戏改坏了。所以抑制期间把
    /// `autoSyncTransforms` 一起打开，退出时恢复原值。
    ///
    /// **恢复是硬要求**，四种情况都要回到原模式：
    /// 播放停止、离开编辑器场景、mod 被关掉/卸载、以及任何一处抛异常。
    /// 恢复用的是**进来时读到的那个值**，不是硬编码的 `FixedUpdate` ——
    /// 别人先改过的话，我们只负责还原成他改的样子。
    ///
    /// 兜底门是"场景里零刚体"：将来若有人往场景里塞了带刚体的东西，
    /// 这里会自己让开，不用改代码。
    /// </summary>
    internal static class PhysicsStepSuppressor
    {
        /// <summary>抑制期间多久复核一次"还在编辑器场景里"。</summary>
        private const int VerifyIntervalFrames = 60;

        /// <summary>
        /// **"改过引擎状态"的标记，和 `suppressed` 分开记。**
        ///
        /// 这两件事必须分开，是本类最要紧的一条：
        /// 原来的写法是先写 `simulationMode = Script`、再 `SyncTransforms()`、
        /// 最后才置 `suppressed = true`。中间那一步一旦抛异常，catch 里调
        /// `Restore()`，而 `Restore()` 看见 `suppressed == false` 就原地返回 ——
        /// **物理被永远留在 Script 模式上，连关游戏都还不回去。**
        /// 现在改成：只要动过引擎就置 `applied = true`，恢复只看这个标记；
        /// 而且 `ForceRestore` 不看 `suppressed`，只要"当前模式不等于原模式"
        /// 就再写一次，保证还给引擎的永远是它原来的样子。
        /// </summary>
        private static bool applied;

        /// <summary>抓住过"进来之前的模式"没有。</summary>
        private static bool originalCaptured;

        /// <summary>进来之前的模式。恢复时按原样还原，不假设是 FixedUpdate。</summary>
        private static SimulationMode2D originalMode;

        /// <summary>进来之前的 `autoSyncTransforms` 值。</summary>
        private static bool originalAutoSync;

        /// <summary>
        /// 这一轮播放已经普查过刚体了没有。
        ///
        /// **必须单独记。** 原来只在"有刚体"时把日志标记置位，于是
        /// `playing && !suppressed` 每帧都成立、每帧都要做一次
        /// `FindObjectsByType<Rigidbody2D>(Include)` 全场景扫描 —— 在一个
        /// 4600 多个碰撞体的场景里，这等于为了省 4ms 又每帧多花一笔。
        /// 现在普查按"每一轮播放一次"记账，离开播放才清。
        /// </summary>
        private static bool bodyGateChecked;

        /// <summary>"场景里有刚体所以不抑制"这条只记一次日志，别每帧刷。</summary>
        private static bool bodyWarned;

        /// <summary>下一次复核帧号。</summary>
        private static int nextVerifyFrame;

        /// <summary>
        /// 出过一次异常就永久停手。
        /// **宁可少 4ms，也不能因为性能优化把物理状态弄乱。**
        /// </summary>
        private static bool broken;

        internal static void Tick()
        {
            // 先把"要不要恢复"做完，再看 broken 和开关。
            // 顺序反了的话，一旦出过异常，用户关掉开关也不会触发恢复。
            Settings settings = Main.Settings;
            if (settings != null && !settings.SuppressPhysicsStep)
            {
                if (applied)
                {
                    Restore("设置已关闭");
                }
                return;
            }

            if (broken)
            {
                return;
            }

            try
            {
                bool running = IsActuallyRunning();

                if (!running)
                {
                    // 离开播放要清掉"本轮已普查"，下一轮进入时重新查。
                    bodyGateChecked = false;
                    if (applied)
                    {
                        Restore("播放结束");
                    }
                    return;
                }

                if (!applied)
                {
                    TrySuppress();
                    return;
                }

                if (Time.frameCount >= nextVerifyFrame)
                {
                    nextVerifyFrame = Time.frameCount + VerifyIntervalFrames;
                    // 播放中切走了场景（打开别的关、回主菜单）也必须还回去。
                    if (ADOBase.editor == null)
                    {
                        Restore("已离开编辑器场景");
                    }
                }
            }
            catch (Exception exception)
            {
                broken = true;
                Restore("出错");
                Log("[物理] 抑制逻辑出错，已永久停手（不影响功能）：" + exception.Message);
            }
        }

        /// <summary>
        /// 是不是**真的在放**。
        ///
        /// **只用 `scnEditor.playMode`，不要再与 `controller.paused`。**
        /// 这是实测撞出来的结论，记在这里免得以后又"优化"回去：
        ///
        /// 我一度以为 `playMode` 在"播放中暂停"时也返回 true（它的 getter 确实
        /// 有 `if (!pausedInPlayMode) ... return true;` 这么一条），于是改成
        /// `playMode && !controller.paused`。结果**编辑器播放时 `controller.paused`
        /// 恒为 true**，整个抑制功能一次都没触发 —— 日志里一条 `[物理]` 都没有，
        /// 而 `固/Physics2DFixedUpdate` 直接回到 3.94ms，帧掉到 11.97ms。
        ///
        /// 回头查反编译才看明白：`pausedInPlayMode = !ADOBase.controller.paused;`
        /// （`Assembly-CSharp.decompiled.cs:44030`）—— 这个字段的名字和它的实际
        /// 含义是反的，它记的是"在播放且没暂停"。再与一次 `paused` 等于把条件
        /// 写成了自相矛盾。
        ///
        /// 所以 `playMode` 本身就是我们要的判据：AES 求值器驱动的拖动预览不会让它
        /// 变 true（见 `EditorStudioOverlay.IsNativePlayModeRunning` 的注释），
        /// 只有原生 transport 真的在跑时才是 true。
        /// </summary>
        private static bool IsActuallyRunning()
        {
            return EditorStudioOverlay.IsNativePlayModeRunning();
        }

        /// <summary>
        /// 强制恢复。mod 被关闭或卸载时调 —— 不能把"物理不再步进"
        /// 这个状态留给一个已经不管事的 mod。
        ///
        /// **这里不看 `applied`，只看"当前值是不是已经被改过"**：
        /// 只要抓过原值、且现在的模式和原模式不一致，就再写一次。
        /// 这样即使 `applied` 因为异常路径没置上，也一定还得回去。
        /// </summary>
        internal static void ForceRestore()
        {
            Restore("mod 停止");
            broken = true;
        }

        private static void TrySuppress()
        {
            // 兜底门：有刚体就让开。模拟可能真的会产生结果了，不能跳。
            //
            // **每轮播放只查一次。** 查一次要遍历全场景对象，在 4600 多个
            // 碰撞体的场景里不是免费的；播放期间场景基本不变，用不着每帧查。
            // 用 `FindObjectsInactive.Include`：连未激活的也算。宁可误判成
            // "有刚体、不抑制"，也不能漏掉一个将来会被激活的。
            if (!bodyGateChecked)
            {
                bodyGateChecked = true;
                Rigidbody2D[] bodies = UnityEngine.Object.FindObjectsByType<Rigidbody2D>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (bodies != null && bodies.Length > 0)
                {
                    if (!bodyWarned)
                    {
                        bodyWarned = true;
                        Log("[物理] 场景里有 " + bodies.Length
                            + " 个刚体，保持引擎原模式不抑制");
                    }
                    return;
                }
            }

            // 每轮播放重新读一次原值：万一这两轮之间别人改过，
            // 还原的就该是他改成的那个值，而不是我们第一次看到的值。
            originalMode = Physics2D.simulationMode;
            originalAutoSync = Physics2D.autoSyncTransforms;
            originalCaptured = true;

            // **先置标记，再动引擎。** 反过来的话，动到一半抛异常就会留下
            // 一个"改了但没人记得要还原"的状态（见 `applied` 的注释）。
            applied = true;

            if (originalMode != SimulationMode2D.Script)
            {
                Physics2D.simulationMode = SimulationMode2D.Script;
            }
            // 不自动步进之后，Unity 再没有时机把移动过的碰撞体同步进
            // broadphase；而 `scrPlanet.Update` 每帧都在查装饰物命中盒。
            // 打开自动同步，保证查询看到的永远是当前位置。
            if (!Physics2D.autoSyncTransforms)
            {
                Physics2D.autoSyncTransforms = true;
            }

            // 切换这一帧补一次全量同步，把切换前积压的变换落地。
            Physics2D.SyncTransforms();

            nextVerifyFrame = Time.frameCount + VerifyIntervalFrames;

            Log("[物理] 播放中已关闭 2D 物理自动步进（原模式=" + originalMode
                + "，原自动同步=" + originalAutoSync
                + "，场景刚体=0）。离开播放会还原。");
        }

        /// <summary>
        /// 把引擎状态还回去。**只要动过就必须尝试**，不看 `applied` 是否还成立。
        /// </summary>
        private static void Restore(string reason)
        {
            bool needRestore = applied
                || (originalCaptured && originalMode != Physics2D.simulationMode);

            applied = false;
            bodyGateChecked = false;

            if (!needRestore || !originalCaptured)
            {
                return;
            }

            // 这两步各自 try：写模式失败不能连累 autoSync 的还原，反之亦然。
            try
            {
                Physics2D.simulationMode = originalMode;
            }
            catch (Exception exception)
            {
                broken = true;
                Log("[物理] 还原 simulationMode 失败：" + exception.Message);
            }

            try
            {
                Physics2D.autoSyncTransforms = originalAutoSync;
            }
            catch (Exception exception)
            {
                broken = true;
                Log("[物理] 还原 autoSyncTransforms 失败：" + exception.Message);
            }

            try
            {
                // 还原那一下同样要落地，否则恢复后头几帧查询还看着旧位置。
                Physics2D.SyncTransforms();
            }
            catch (Exception)
            { }

            Log("[物理] 已恢复 2D 物理（" + reason + "），当前模式="
                + Physics2D.simulationMode
                + " 自动同步=" + Physics2D.autoSyncTransforms);
        }

        private static void Log(string message)
        {
            try
            {
                if (Main.Logger != null)
                {
                    Main.Logger.Log(message);
                }
            }
            catch (Exception)
            { }
        }
    }
}
