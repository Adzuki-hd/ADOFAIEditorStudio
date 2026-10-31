using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 帧耗时诊断（模组内置，不是临时探针）。
    ///
    /// **开关**：`Settings.xml` 里的 VerboseDiagnostics。关着时所有方法第一行就返回，
    /// 只剩一次布尔判断的开销。
    ///
    /// **为什么要有它**：工作台卡不卡，靠"感觉"永远定位不到具体环节 ——
    /// 这一次掉帧排查里，先后怀疑过遮罩反射、材质克隆、网格 Clear、事件缓动，
    /// 全都不对；真正的原因是"同一帧重复求值"和"逐行拆绘制命令"。
    /// 有这条按秒聚合的日志，以后遇到卡顿先看数字，不再靠猜。
    ///
    /// **输出示例**（每 120 帧或 2 秒一行）：
    /// <code>
    /// [诊断] 帧 17.5ms 最差 40.9ms | 域 MoveTrack=1.75 RecolorTrack=0.17 ...（按耗时降序）
    ///        段 Timeline.U=2.9 Overlay.U=1.2 ... | 计数 MoveTrack.WriteCount=7369 ...
    ///        画布 重建1.20ms 渲染2.0次/帧
    /// </code>
    /// </summary>
    internal static class PreviewDiagnostics
    {
        private const int ReportFrames = 120;
        private const float ReportSeconds = 2f;
        // 域按耗时降序输出的条数上限。全谱 18 个域 + 装饰物子项都排得下，
        // 但超过 16 条时后面的一定是小到不构成优化目标的域，砍掉保日志可读。
        private const int MaxDomainLines = 16;
        private const int MaxPartLines = 16;
        // 段（Part）同类，但状态量在排查时往往要一起看（队列长度、可见格数、
        // 脏格数、候选数），8 条很容易被前面的挤掉，放宽到 16。
        //
        // **20 这个数踩过一次坑**：计数是按登记顺序输出的，前 20 条正好被
        // `P2.*`（9 条）+ `ScW.*`（8 条）+ `Ev.*`（3 条）占满，排在后面的
        // `Deco.Busy` / `Deco.Evts` / `Ev.T` / `SC.*` 一条都打不出来 —— 而
        // 那几条恰恰是「装饰物是件数太多还是历史太长」的唯一判据。
        // 现在放到 40，保证现有的全部计数都能出来，再留几个空位给下一轮探针。
        private const int MaxFlagLines = 40;

        internal static bool Enabled
        {
            get { return Main.Settings != null && Main.Settings.VerboseDiagnostics; }
        }

        // 帧
        private static int frames;
        private static float deltaSum;
        private static float deltaWorst;

        // ---- GC 计数（每帧） ----
        //
        // 尖峰帧上 `Overlay.U` 会涨到 5~19ms，可它里面**每一个被计时的子段
        // 都没涨**。这种"哪一段都没变、总时长却翻倍"的形态，最常见的解释
        // 就是垃圾回收：GC 的停顿会被算进当时正在跑的那段代码里，而它
        // 不属于任何一段。数一下每帧发生了几次 GC，就能证实或者排除。
        private static int lastGc0;
        private static int lastGc1;
        private static int lastGc2;
        private static bool gcPrimed;
        private static int gcSum0;
        private static int gcSum1;
        private static int gcSum2;
        private static int frameGcCount;
        private static int worstGcCount;
        private static float reportClock;
        // 本窗口内有几帧处于「原生播放中」/「工作台可见」。
        // 状态必须按窗口累计，理由见 Tick() 里的说明。
        private static int playFrames;
        private static int visFrames;

        // 各域耗时（按名字聚合，数量有限）
        private static readonly Dictionary<string, double> domainSum
            = new Dictionary<string, double>();
        private static readonly List<string> domainOrder = new List<string>();

        private static readonly StringBuilder builder = new StringBuilder(256);

        // ===== Unity 剖析计数（决定「剩下的 10ms 是 CPU 还是 GPU/限帧等待」）=====
        //
        // 之前只能拿「整帧间隔 − 我们自己量到的各段」去减，减出来的 10ms 一直
        // 只能说"是游戏原生开销"。但那句话分不清两种完全不同的情况：
        //
        //   · 主线程真的在算（`BehaviourUpdate` / `Physics2D.Processing` 大）
        //     → 还有 CPU 可挤，继续优化有意义；
        //   · 主线程算完在**等**（`WaitForTargetFPS` / `Gfx.WaitForPresentOnGfxThread` 大）
        //     → 帧率被限帧或 GPU 钉死，CPU 再怎么省也一点用没有。
        //
        // 这一组读的就是这个差别。`ProfilerRecorder` 在正式版播放器里能拿到的
        // 计数有限，拿不到的那项会带 `?` 后缀——带问号的不代表是 0。
        private sealed class RecorderSlot
        {
            internal string Name;
            internal Unity.Profiling.ProfilerRecorder Recorder;
            internal double SumMs;
            internal bool Valid;
        }

        private static readonly List<RecorderSlot> recorders = new List<RecorderSlot>();
        private static bool recordersInit;

        private static void EnsureRecorders()
        {
            if (recordersInit)
            {
                return;
            }
            recordersInit = true;

            // **先把引擎当前真正可用的标记枚举一遍。**
            //
            // 正式版播放器里能拿到的标记是编辑器的一个子集：`BehaviourUpdate`
            // 这类名字直接被剥掉，`StartNew` 拿回来就是 Invalid。可引擎其实
            // 还有 `Update.ScriptRunBehaviourUpdate` 这种"带前缀的全名"版本 ——
            // 光靠猜名字试不出来。`ProfilerRecorderHandle.GetAvailable` 给的是
            // 本机这套播放器**确切的**清单，按它去要，命中率是 100%。
            //
            // 顺带把名字里带 Update / Behaviour / Physics / Camera / Render 的
            // 全写进日志：游戏的 2620 个 scrFloor 的 Update/LateUpdate、装饰物管理器、
            // 物理都在这些标记下面，而这段一直是「播放 70 帧、暂停 150 帧」那
            // 6.9ms 的盲区。
            Dictionary<string, Unity.Profiling.ProfilerCategory> available
                = new Dictionary<string, Unity.Profiling.ProfilerCategory>();
            try
            {
                List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle> handles
                    = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
                Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
                StringBuilder list = new StringBuilder(512);
                int listed = 0;
                for (int i = 0; i < handles.Count; i++)
                {
                    Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderDescription description
                        = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handles[i]);
                    string markerName = description.Name;
                    if (string.IsNullOrEmpty(markerName))
                    {
                        continue;
                    }
                    if (!available.ContainsKey(markerName))
                    {
                        available[markerName] = description.Category;
                    }
                    if (listed < 160
                        && (markerName.IndexOf("Update", StringComparison.Ordinal) >= 0
                            || markerName.IndexOf("Behaviour", StringComparison.Ordinal) >= 0
                            || markerName.IndexOf("Physics", StringComparison.Ordinal) >= 0
                            || markerName.IndexOf("Camera", StringComparison.Ordinal) >= 0
                            || markerName.IndexOf("Render", StringComparison.Ordinal) >= 0))
                    {
                        list.Append(' ').Append(markerName);
                        listed++;
                    }
                }
                Main.Logger.Log("[剖析可用] 本机共 " + handles.Count
                    + " 个标记，其中相关的（截断到 " + listed + " 条）：" + list);
            }
            catch (Exception exception)
            {
                Main.Logger.Log("[剖析可用] 枚举失败：" + exception.Message);
            }

            AddRecorder("PlayerLoop", available);
            AddRecorder("BehaviourUpdate", available);
            AddRecorder("Update.ScriptRunBehaviourUpdate", available);
            AddRecorder("Update.ScriptRunDelayedDynamicFrameRate", available);
            AddRecorder("PreLateUpdate.ScriptRunBehaviourLateUpdate", available);
            AddRecorder("Render.MainThread", available);
            AddRecorder("Camera.Render", available);
            AddRecorder("Gfx.WaitForPresentOnGfxThread", available);
            // 本机正式版实测可用的两项：主线程等渲染线程多久、渲染线程一帧多长。
            // 如果主线程在等渲染线程，那说明瓶颈在另一半（精灵合批 / 提交），
            // 再把 CPU 侧的求值优化下去也不会让帧率动一毫秒。
            AddRecorder("Gfx.WaitForRenderThread", available);
            AddRecorder("CPU Render Thread Frame Time", available);
            AddRecorder("WaitForTargetFPS", available);
            AddRecorder("Physics2D.Processing", available);
        }

        private static void AddRecorder(
            string name, Dictionary<string, Unity.Profiling.ProfilerCategory> available)
        {
            RecorderSlot slot = new RecorderSlot();
            slot.Name = name;
            try
            {
                Unity.Profiling.ProfilerCategory category;
                if (available.TryGetValue(name, out category))
                {
                    // 清单里有 → 用它登记时那个分类去要，一次就能拿到。
                    slot.Recorder = Unity.Profiling.ProfilerRecorder.StartNew(
                        category, name, 1);
                }
                // 兜底：清单里没有（或者按名字查分类没命中）时，
                // 老办法两个常见分类各试一次。拿不到就留空，不报错、不影响主循环。
                if (!slot.Recorder.Valid)
                {
                    slot.Recorder.Dispose();
                    slot.Recorder = Unity.Profiling.ProfilerRecorder.StartNew(
                        Unity.Profiling.ProfilerCategory.Internal, name, 1);
                }
                if (!slot.Recorder.Valid)
                {
                    slot.Recorder.Dispose();
                    slot.Recorder = Unity.Profiling.ProfilerRecorder.StartNew(
                        Unity.Profiling.ProfilerCategory.Render, name, 1);
                }
                slot.Valid = slot.Recorder.Valid;
            }
            catch (Exception)
            {
                slot.Valid = false;
            }
            recorders.Add(slot);
        }

        private static void AppendProfilerLines()
        {
            if (recorders.Count == 0)
            {
                return;
            }
            builder.Append(" | 剖析");
            for (int i = 0; i < recorders.Count; i++)
            {
                RecorderSlot slot = recorders[i];
                builder.Append(' ').Append(slot.Name).Append('=');
                if (slot.Valid)
                {
                    builder.Append((slot.SumMs / Mathf.Max(1, frames)).ToString("0.00"));
                }
                else
                {
                    builder.Append('?');
                }
                slot.SumMs = 0.0;
            }
        }

        // 模组自身各段的耗时（Overlay.Update / Overlay.LateUpdate / Timeline.Update 等）
        private static readonly Dictionary<string, double> partSum
            = new Dictionary<string, double>();
        private static readonly List<string> partOrder = new List<string>();

        // ---- 最差帧快照 ----
        //
        // 120 帧平均只回答"这一窗贵不贵"，回答不了"为什么突然卡一下"。
        // 用户感受到的是波动，而波动由单帧决定：平均 8.5ms 但每 30 帧
        // 蹦一个 20ms，比稳定 10ms 难受得多。所以窗口里那一帧最差时，
        // 把它**当帧**的各段耗时单独存一份，窗口结束再打一行。
        //
        // 对账方式：`Tick()` 在 `Overlay.Update` 最前面跑，此刻
        // `Time.unscaledDeltaTime` 是**刚结束那一帧**的总时长，而
        // `frameParts` 里装的正是那一帧 `RecordPart` 记下的各段
        // （含上一帧的 LateUpdate，因为它在下一次 Tick 之前就记完了）。
        private static readonly Dictionary<string, double> frameParts
            = new Dictionary<string, double>();
        private static readonly List<string> framePartOrder = new List<string>();
        private static readonly Dictionary<string, double> worstParts
            = new Dictionary<string, double>();
        private static readonly List<string> worstPartOrder = new List<string>();
        private static float worstPartsDelta;
        private static double frameCanvasMs;
        private static double worstCanvasMs;

        // ===== PlayerLoop 探针：直接量「引擎跑所有 MonoBehaviour」花了多久 =====
        //
        // 本机正式版只有 44 个剖析标记，`BehaviourUpdate` 和带前缀的全名
        // （`Update.ScriptRunBehaviourUpdate`）一个都没留下 —— 而这恰恰是最后
        // 一块说不清的地方。把账减干净以后是：
        //
        //   13.7ms = AES 各段 3.0 + 画布重建 0.9 + 相机剔除提交 1.6 + 说不清的 8.2
        //
        // 另外两项已经把「渲染」这条路排除了：`Gfx.WaitForRenderThread` = 0.00，
        // `CPU Render Thread Frame Time` 只要 1.2~1.5ms。所以那 8.2ms 只能落在
        // 主线程的 MonoBehaviour 调度上 —— 2620 个 `scrFloor` 的 Update/LateUpdate
        // 是全工程唯一的 O(格数) 每帧路径。
        //
        // 拿它的办法是 Unity 官方支持的扩展点：把两个自定义子系统插进 PlayerLoop，
        // 一个插在 `ScriptRunBehaviourUpdate` 之前、一个插在之后，各读一次
        // `Stopwatch.GetTimestamp`。**不改任何游戏代码**，插进去的子系统总共只做
        // 两次时间戳读取和一次加法。
        private struct BehavProbeBegin { }
        private struct BehavProbeEnd { }
        private struct LateProbeBegin { }
        private struct LateProbeEnd { }

        private static bool playerLoopProbeInstalled;
        private static bool playerLoopProbeBroken;
        private static long behavBeginTicks;
        private static long lateBeginTicks;
        private static double behaviourSum;
        private static double lateSum;

        // 诊断开关的快照。注入的回调每帧要跑 4 次，不能每次都去解引用
        // `Main.Settings`；`Tick()` 每帧刷一次这个值就够了。
        private static bool enabledCache;

        /// <summary>
        /// 装 PlayerLoop 探针。只在诊断开的时候装，装一次。
        ///
        /// **安全阀**：任何一个目标子系统都没找到时**不调用** `SetPlayerLoop`，
        /// 保持引擎原来的循环结构不动 —— 宁可没有数据，也不能把游戏弄坏。
        /// </summary>
        private static void EnsurePlayerLoopProbe()
        {
            if (playerLoopProbeInstalled)
            {
                return;
            }
            playerLoopProbeInstalled = true;
            try
            {
                UnityEngine.LowLevel.PlayerLoopSystem root
                    = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
                int phases = WrapTopLevelPhases(ref root);
                int fixedDetail = WrapChildrenOf(ref root, "FixedUpdate", 0,
                    DetailStart, DetailPerGroup);
                int postDetail = WrapChildrenOf(ref root, "PostLateUpdate", 0,
                    DetailStart + DetailPerGroup, DetailPerGroup);
                int updates = InsertAroundSubsystem(ref root,
                    typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate),
                    typeof(BehavProbeBegin), OnBehavBegin,
                    typeof(BehavProbeEnd), OnBehavEnd, 0);
                int lates = InsertAroundSubsystem(ref root,
                    typeof(UnityEngine.PlayerLoop.PreLateUpdate.ScriptRunBehaviourLateUpdate),
                    typeof(LateProbeBegin), OnLateBegin,
                    typeof(LateProbeEnd), OnLateEnd, 0);
                if (phases == 0)
                {
                    Main.Logger.Log("[PlayerLoop探针] 一个顶层阶段都没认出来"
                        + "（引擎循环未改动），不安装");
                    return;
                }
                UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(root);
                Main.Logger.Log("[PlayerLoop探针] 已安装 阶段×" + phases
                    + " Update×" + updates + " LateUpdate×" + lates
                    + " 固细项×" + fixedDetail + " 后置细项×" + postDetail);
                // 【临时探针】把包到的子项名字一次性打出来。
                // 日志里 `后/XXX` 那些短标签只有对着这份清单才读得懂；
                // 而「余数」到底是阶段自身的开销还是没包到的子项，
                // 也要靠这份清单来判断 —— 名字少一个就说明还有盲区。
                StringBuilder names = new StringBuilder(1024);
                for (int i = 0; i < DetailMax; i++)
                {
                    string detail = detailName[DetailStart + i];
                    if (detail == null)
                    {
                        continue;
                    }
                    names.Append(detail).Append(' ');
                }
                Main.Logger.Log("[PlayerLoop探针] 子项清单 " + names);
            }
            catch (Exception exception)
            {
                playerLoopProbeBroken = true;
                Main.Logger.Log("[PlayerLoop探针] 安装失败（引擎循环未改动）："
                    + exception.Message);
            }
        }

        /// <summary>
        /// 递归找目标子系统，在它前后各插一个自定义系统。
        ///
        /// 返回插入次数。找不到返回 0 —— 调用方据此决定**不要**写回循环。
        /// 深度上限 8 只是防御，正常 PlayerLoop 三层就到底了。
        /// </summary>
        private static int InsertAroundSubsystem(
            ref UnityEngine.LowLevel.PlayerLoopSystem system,
            Type target,
            Type beginType, UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction beginCallback,
            Type endType, UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction endCallback,
            int depth)
        {
            if (depth > 8 || system.subSystemList == null || system.subSystemList.Length == 0)
            {
                return 0;
            }
            int inserted = 0;
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                if (system.subSystemList[i].type == target)
                {
                    int oldLength = system.subSystemList.Length;
                    UnityEngine.LowLevel.PlayerLoopSystem[] list
                        = new UnityEngine.LowLevel.PlayerLoopSystem[oldLength + 2];
                    for (int k = 0; k < i; k++)
                    {
                        list[k] = system.subSystemList[k];
                    }
                    list[i] = MakeSubsystem(beginType, beginCallback);
                    list[i + 1] = system.subSystemList[i];
                    list[i + 2] = MakeSubsystem(endType, endCallback);
                    for (int k = i + 1; k < oldLength; k++)
                    {
                        list[k + 2] = system.subSystemList[k];
                    }
                    system.subSystemList = list;
                    inserted++;
                    // 跳过刚插进去的两条，免得递归再从它们里面找一遍。
                    i += 2;
                }
                else
                {
                    inserted += InsertAroundSubsystem(ref system.subSystemList[i], target,
                        beginType, beginCallback, endType, endCallback, depth + 1);
                }
            }
            return inserted;
        }

        private static UnityEngine.LowLevel.PlayerLoopSystem MakeSubsystem(
            Type type, UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction callback)
        {
            UnityEngine.LowLevel.PlayerLoopSystem system
                = new UnityEngine.LowLevel.PlayerLoopSystem();
            system.type = type;
            system.updateDelegate = callback;
            system.subSystemList = null;
            return system;
        }

        // ===== 顶层阶段探针：把整个 PlayerLoop 按阶段拆开 =====
        //
        // 只包围 ScriptRunBehaviourUpdate 还不够用。实测「播放+工作台」38 个样本，
        // 均值 11.33ms 里只有 4.34ms 落在引擎的 Update/LateUpdate 里，画布 0.72、
        // 相机 1.31，**还剩 4.88ms 没有任何归属**。而「暂停+隐藏」的状态下残差
        // 只有 0.46ms —— 说明这 4.88ms 是播放态独有的。
        //
        // Unity 的每个顶层阶段（EarlyUpdate / FixedUpdate / PreUpdate / Update /
        // PreLateUpdate / PostLateUpdate）下面还挂着协程、物理、粒子、UI 画布、
        // 相机渲染等等，它们全都在我原来那两个探针的外面。所以不要再猜了，
        // 把这六个阶段各包一层，残差就按定义归零。
        //
        // 用**实例委托**而不是一堆静态方法：`PhaseProbe` 的每个实例带一个槽位号，
        // 回调只做两次时间戳读写。委托对象由静态数组持有，不会被 GC 收走
        // （IL2CPP 下调这个尤其要紧）。
        //
        // 槽位 0~5 是六个顶层阶段；槽位 6 以后是「细项」—— 把 `FixedUpdate` 和
        // `PostLateUpdate` 的子项也各包一层。为什么要拆这两个：
        //   实测播放态 `固`=3.64ms、暂停态 `固`=0.01ms，播放独有的 3.6ms 就藏在
        //   它下面（物理？行为 FixedUpdate？）；`后置` 2.78ms 里画布 0.86、
        //   相机 1.03 之外还有 0.9ms 不明。不拆开就只能继续猜，而猜是不能写的。
        private const int PhaseCount = 6;
        private const int DetailStart = PhaseCount;
        // 每组 32 个槽：实测 `FixedUpdate` 有 19 个子项、`PostLateUpdate` 约 20 个。
        // 上一版给 12 个，结果 `固`=3.52ms 而包到的子项全是 0 —— 剩下的 7 个
        // 没被包住的子项成了盲区。宁可多开槽位，也不能留盲区。
        //
        // **20 这个数又踩了一次**（2026-10-31）：安装日志打的是
        // `固细项×19 后置细项×20` —— `FixedUpdate` 只有 19 个子项，包全了，
        // 它的 `固余` 正好 0.00；而 `PostLateUpdate` 顶格包满 20 个，
        // `后余` 却还挂着 1.64ms。**余数不为零就是没包完的直接证据**：
        // 后置子项不止 20 个，第 21 个往后全在盲区里。
        //
        // 放到 32 之后打出来的是 `后置细项×32` —— 又是顶格。顶格就意味着
        // 仍然分不清「正好 32 个」还是「第 33 个还没包到」，等于又留了个盲区。
        // 所以再放到 40：真实子项数一定小于 40，安装日志里那个数字
        // 于是变成「有效值」而不是「上限值」，一眼就能判断包全了没有。
        private const int DetailPerGroup = 40;
        private const int DetailMax = DetailPerGroup * 2;
        private const int SlotCount = DetailStart + DetailMax;
        private static readonly PhaseProbe[] phaseProbes = new PhaseProbe[SlotCount];
        private static readonly long[] phaseBegin = new long[SlotCount];
        private static readonly double[] phaseSum = new double[SlotCount];
        private static readonly string[] phaseNames
            = { "早", "固", "前", "更", "后更", "后置" };
        // **长度必须是 SlotCount，不是 DetailMax。** 这里踩过一次：槽位编号是
        // 「DetailStart + 组内序号」的绝对下标，而数组按组内上限开，
        // 于是后置那组一写就越界。抛出点又恰好在 Tick 的报告路径上（没有
        // try/catch 兜着），结果每帧抛一次、UpdateCore 每帧中断 ——
        // 工作台整个卡死。教训：凡是"按槽位下标访问"的数组，长度一律用 SlotCount。
        private static readonly string[] detailName = new string[SlotCount];
        // 每个槽位这一窗口被触发了几次。
        // 用途：如果 `FixedUpdate` 阶段每帧跑了 5 次而子项加不起来，说明
        // 时间花在**阶段自身**而不是某个子项上，那是另一种改法。
        private static readonly int[] phaseHits = new int[SlotCount];

        private sealed class PhaseProbe
        {
            internal readonly int Slot;

            internal PhaseProbe(int slot)
            {
                Slot = slot;
            }

            internal void Begin()
            {
                if (playerLoopProbeBroken || !enabledCache)
                {
                    return;
                }
                try
                {
                    phaseBegin[Slot] = System.Diagnostics.Stopwatch.GetTimestamp();
                    phaseHits[Slot]++;
                }
                catch (Exception)
                {
                    playerLoopProbeBroken = true;
                }
            }

            internal void End()
            {
                if (playerLoopProbeBroken || phaseBegin[Slot] == 0L)
                {
                    return;
                }
                try
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    phaseSum[Slot] += (now - phaseBegin[Slot]) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
                    phaseBegin[Slot] = 0L;
                }
                catch (Exception)
                {
                    playerLoopProbeBroken = true;
                }
            }
        }

        /// <summary>
        /// 按类型名认阶段。**不用 `typeof`** —— 顶层阶段的结构体名在不同 Unity
        /// 版本里搬过家，用字符串匹配就完全不受影响，编译期也不依赖它们存在。
        /// </summary>
        private static int PhaseSlotOf(Type type)
        {
            if (type == null || type.Namespace != "UnityEngine.PlayerLoop")
            {
                return -1;
            }
            switch (type.Name)
            {
                case "EarlyUpdate": return 0;
                case "FixedUpdate": return 1;
                case "PreUpdate": return 2;
                case "Update": return 3;
                case "PreLateUpdate": return 4;
                case "PostLateUpdate": return 5;
                default: return -1;
            }
        }

        /// <summary>
        /// 给六个顶层阶段各套一对前后探针。返回包住的阶段数。
        /// 一个都没包住时调用方不要写回循环。
        /// </summary>
        private static int WrapTopLevelPhases(ref UnityEngine.LowLevel.PlayerLoopSystem root)
        {
            if (root.subSystemList == null || root.subSystemList.Length == 0)
            {
                return 0;
            }
            List<UnityEngine.LowLevel.PlayerLoopSystem> built
                = new List<UnityEngine.LowLevel.PlayerLoopSystem>(root.subSystemList.Length * 3);
            int wrapped = 0;
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                UnityEngine.LowLevel.PlayerLoopSystem phase = root.subSystemList[i];
                int slot = PhaseSlotOf(phase.type);
                if (slot < 0)
                {
                    built.Add(phase);
                    continue;
                }
                if (phaseProbes[slot] == null)
                {
                    phaseProbes[slot] = new PhaseProbe(slot);
                }
                built.Add(MakeSubsystem(phase.type, phaseProbes[slot].Begin));
                built.Add(phase);
                built.Add(MakeSubsystem(phase.type, phaseProbes[slot].End));
                wrapped++;
            }
            if (wrapped > 0)
            {
                root.subSystemList = built.ToArray();
            }
            return wrapped;
        }

        /// <summary>
        /// 把指定阶段下面的**每一个直接子项**各包一层，并把子项的类型名记下来当标签。
        ///
        /// 这是「不要再猜」的延伸：`固` 那 3.64ms 究竟是 `Physics2D.FixedUpdate`、
        /// `PhysicsFixedUpdate` 还是 `ScriptRunBehaviourFixedUpdate`，只有拆开才知道；
        /// 三者对应三种完全不同的应对办法。包住全部子项意味着它们的和**按定义**
        /// 等于母阶段，不会有漏项。
        ///
        /// 返回包住的子项数。槽位不够时**不再包**（宁缺毋滥，不覆盖别人）。
        /// </summary>
        private static int WrapChildrenOf(
            ref UnityEngine.LowLevel.PlayerLoopSystem system, string parentName, int depth,
            int slotBase, int slotMax)
        {
            if (depth > 8 || system.subSystemList == null || system.subSystemList.Length == 0)
            {
                return 0;
            }
            if (system.type != null && system.type.Name == parentName
                && system.subSystemList != null)
            {
                List<UnityEngine.LowLevel.PlayerLoopSystem> built
                    = new List<UnityEngine.LowLevel.PlayerLoopSystem>(
                        system.subSystemList.Length * 3);
                int wrapped = 0;
                for (int i = 0; i < system.subSystemList.Length; i++)
                {
                    UnityEngine.LowLevel.PlayerLoopSystem child = system.subSystemList[i];
                    if (child.type == null || wrapped >= slotMax)
                    {
                        built.Add(child);
                        continue;
                    }
                    int slot = slotBase + wrapped;
                    detailName[slot] = parentName + "." + child.type.Name;
                    if (phaseProbes[slot] == null)
                    {
                        phaseProbes[slot] = new PhaseProbe(slot);
                    }
                    built.Add(MakeSubsystem(child.type, phaseProbes[slot].Begin));
                    built.Add(child);
                    built.Add(MakeSubsystem(child.type, phaseProbes[slot].End));
                    wrapped++;
                }
                if (wrapped > 0)
                {
                    system.subSystemList = built.ToArray();
                }
                return wrapped;
            }
            int total = 0;
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                total += WrapChildrenOf(ref system.subSystemList[i], parentName, depth + 1,
                    slotBase, slotMax);
            }
            return total;
        }

        // 下面四个回调都在引擎主循环最热的路径上，除了读时间戳什么都不做。
        // 抛异常会把整个 PlayerLoop 打断，所以每条都套了 try/catch —— 真出事
        // 只把探针自己关掉（playerLoopProbeBroken），引擎照常跑。
        private static void OnBehavBegin()
        {
            if (playerLoopProbeBroken || !enabledCache)
            {
                return;
            }
            try
            {
                behavBeginTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            catch (Exception)
            {
                playerLoopProbeBroken = true;
            }
        }

        private static void OnBehavEnd()
        {
            if (playerLoopProbeBroken || behavBeginTicks == 0L)
            {
                return;
            }
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                behaviourSum += (now - behavBeginTicks) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                behavBeginTicks = 0L;
            }
            catch (Exception)
            {
                playerLoopProbeBroken = true;
            }
        }

        private static void OnLateBegin()
        {
            if (playerLoopProbeBroken || !enabledCache)
            {
                return;
            }
            try
            {
                lateBeginTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            catch (Exception)
            {
                playerLoopProbeBroken = true;
            }
        }

        private static void OnLateEnd()
        {
            if (playerLoopProbeBroken || lateBeginTicks == 0L)
            {
                return;
            }
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                lateSum += (now - lateBeginTicks) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                lateBeginTicks = 0L;
            }
            catch (Exception)
            {
                playerLoopProbeBroken = true;
            }
        }

        // 纯状态量（不是耗时），用于确认走的是哪条分支。**只保留最后一帧的值**：
        // 它回答的是"此刻队列里排了多少笔写入"，累计平均没有意义。
        private static readonly Dictionary<string, int> flagValue
            = new Dictionary<string, int>();
        private static readonly List<string> flagOrder = new List<string>();

        // ---- Canvas 重建统计 ----
        //
        // Unity 的渲染循环里，每个要渲染的画布先触发 preWillRenderCanvases，
        // 再跑 CanvasUpdateRegistry.PerformUpdate（把本帧所有脏 UI 的布局和
        // 几何重建做完），最后触发 willRenderCanvases —— Unity 自己的
        // PerformUpdate 订阅得比我们早，所以我们的回调执行时重建已经完成。
        // 两个回调之间测到的就是"本帧 Canvas 重建（布局+几何）的真实耗时"，
        // 这正是整帧里那笔"剩下的 12.8ms"的直接证据。
        private static bool canvasHookInstalled;
        private static long preRenderTimestamp;
        private static double canvasRebuildSum;
        private static int canvasRenderCount;

        // ---- 相机渲染阶段统计 ----
        //
        // `Render.MainThread` 这个剖析标记在正式版播放器里被剥掉了（日志里是 `?`），
        // 于是「主线程花在剔除 + 提交渲染上的时间」一直是个盲区。可 2620 个格子的
        // 谱面在播放时相机是一直动的：视锥剔除、透明物体按距离排序、合批重算，
        // 每帧都要重来一遍 —— 这笔钱恰恰只会出现在「播放」和「静止」的差值里，
        // 而且它不在 `Overlay.U` 里，减账时会全部落到"游戏原生"头上。
        //
        // 绕过剖析器直接测：Unity 的静态回调 `Camera.onPreCull` 在**剔除之前**
        // 触发、`onPostRender` 在**渲染提交之后**触发，两者之间就是这段。
        // 按相机实例分别记时间戳，因为多相机（比如 UI 覆盖相机）会交错回调，
        // 用一个公共字段会把两段的区间搅在一起。
        private static bool cameraHookInstalled;
        private static readonly Dictionary<int, long> cameraPreTimestamps
            = new Dictionary<int, long>();
        private static double cameraRenderSum;
        private static int cameraRenderCount;

        /// <summary>单个求值域的耗时（引擎在 profile 打开时调用）。</summary>
        internal static void RecordDomain(string name, double ms)
        {
            if (!Enabled || name == null)
            {
                return;
            }
            double current;
            if (!domainSum.TryGetValue(name, out current))
            {
                domainOrder.Add(name);
            }
            domainSum[name] = current + ms;

            // 域也进本帧的账。名字前缀 `域/` 是为了和 `段` 那边分开 ——
            // 两边同名（比如都有 `RecolorTrack`）会互相覆盖，最差帧就
            // 分不清"这是域的总时长还是某个子段"。
            AddToFrame("域/" + name, ms);
        }

        /// <summary>
        /// 把一笔耗时记进本帧的账（供"最差帧快照"使用）。
        /// </summary>
        private static void AddToFrame(string name, double ms)
        {
            double frameCurrent;
            if (!frameParts.TryGetValue(name, out frameCurrent))
            {
                framePartOrder.Add(name);
            }
            frameParts[name] = frameCurrent + ms;
        }

        /// <summary>
        /// 模组自身某一段的耗时（毫秒）。用来把「整帧」拆成「我们花的」和「游戏花的」。
        /// </summary>
        internal static void RecordPart(string name, double ms)
        {
            if (!Enabled || name == null)
            {
                return;
            }
            double current;
            if (!partSum.TryGetValue(name, out current))
            {
                partOrder.Add(name);
            }
            partSum[name] = current + ms;

            // 同一笔也记进本帧的账，供"最差帧快照"使用。
            AddToFrame(name, ms);
        }

        /// <summary>
        /// 记一个「本帧状态量」：队列长度、候选数量、开关状态之类。
        /// 同名的值直接覆盖 —— 输出时给的是报告窗口最后一帧的状态。
        /// </summary>
        internal static void RecordFlag(string name, int value)
        {
            if (!Enabled || name == null)
            {
                return;
            }
            if (!flagValue.ContainsKey(name))
            {
                flagOrder.Add(name);
            }
            flagValue[name] = value;
        }

        /// <summary>
        /// 记一帧「工作台可见」。和 <see cref="RecordFlag"/> 不同，这个是**累计**的：
        /// 可见性必须和帧耗时的统计窗口对齐，否则一个末尾才变可见的窗口
        /// 会拿隐藏状态的平均耗时冒充可见状态。
        /// </summary>
        internal static void NoteVisible(bool visible)
        {
            if (!Enabled || !visible)
            {
                return;
            }
            visFrames++;
        }

        /// <summary>
        /// 挂 Canvas 重建统计钩子。只挂一次、常驻；
        /// 开关关着时回调第一行就返回，几乎零开销。
        /// </summary>
        private static void EnsureCanvasHook()
        {
            if (canvasHookInstalled)
            {
                return;
            }
            canvasHookInstalled = true;
            // Unity 2020.2+ 才有 pre 事件；本项目跑在 Unity 6000（见 Player.log），
            // 编译引用与运行时一致，不会缺。
            Canvas.preWillRenderCanvases += OnPreWillRenderCanvases;
            Canvas.willRenderCanvases += OnWillRenderCanvases;
        }

        private static void OnPreWillRenderCanvases()
        {
            if (!Enabled)
            {
                return;
            }
            preRenderTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void OnWillRenderCanvases()
        {
            if (!Enabled || preRenderTimestamp == 0L)
            {
                return;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double rebuildMs = (now - preRenderTimestamp)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            canvasRebuildSum += rebuildMs;
            // 单帧版：最差帧快照要能看出"这一下是不是画布重建吃掉的"。
            frameCanvasMs += rebuildMs;
            canvasRenderCount++;
            preRenderTimestamp = 0L;
        }

        /// <summary>
        /// 挂相机渲染统计钩子。和画布钩子一样只挂一次、常驻，开关关着时
        /// 回调第一行就返回。
        /// </summary>
        private static void EnsureCameraHook()
        {
            if (cameraHookInstalled)
            {
                return;
            }
            cameraHookInstalled = true;
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
        }

        private static void OnCameraPreCull(Camera camera)
        {
            if (!Enabled || camera == null)
            {
                return;
            }
            cameraPreTimestamps[camera.GetInstanceID()]
                = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void OnCameraPostRender(Camera camera)
        {
            if (!Enabled || camera == null)
            {
                return;
            }
            int id = camera.GetInstanceID();
            long start;
            if (!cameraPreTimestamps.TryGetValue(id, out start))
            {
                return;
            }
            // 用完就删：相机被销毁重建时不会在字典里留垃圾。
            cameraPreTimestamps.Remove(id);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            cameraRenderSum += (now - start) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            cameraRenderCount++;
        }

        /// <summary>
        /// 记一段耗时（毫秒）。调用方自己取时间戳：
        /// <code>
        /// long t = System.Diagnostics.Stopwatch.GetTimestamp();
        /// ...被量的那段...
        /// PreviewDiagnostics.RecordSince("Prop.U", t);
        /// </code>
        ///
        /// 工作台里有一批 MonoBehaviour 的 Update/LateUpdate **一直没被量过**
        /// （参数栏、检查器、浏览器、悬浮提示……），而"整帧减去已知各段"之后
        /// 对不上账的那部分，很可能就藏在它们里。要砍之前先把它们量出来。
        /// </summary>
        internal static void RecordSince(string name, long startTimestamp)
        {
            if (!Enabled)
            {
                return;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            RecordPart(name, (now - startTimestamp) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>
        /// 每帧喂一次；攒够 120 帧或 2 秒打一行。
        ///
        /// **这段原先是 ImGui 桥接层负责的**，桥删掉之后没人输出了 ——
        /// 数字还在攒、却永远不落地，等于白攒。现在由工作台自己的 Update 调它。
        /// </summary>
        /// <summary>
        /// 数一遍场景里的 2D 物理对象，每 2 秒一次。
        ///
        /// 为什么要数：`固/Physics2DFixedUpdate` 在播放态是 **4ms 左右**，占 12.8ms 帧的
        /// 三分之一，而暂停时只有 0.02ms。已经证伪了「是我们写 Transform 把它惹贵的」
        /// —— 有 15 个样本一整个窗口零写入，物理照样 4ms。所以要么场景里的碰撞体/刚体
        /// 数量太夸张，要么是游戏自己在播放时把它们激活了。只有数出来才知道。
        ///
        /// 用 `FindObjectsOfType`（只算激活的）而不是 `Resources.FindObjectsOfTypeAll`：
        /// 物理只看得见激活对象，而且前者便宜得多。
        /// </summary>
        private static void CensusPhysics()
        {
            try
            {
                Collider2D[] colliders = UnityEngine.Object.FindObjectsOfType<Collider2D>();
                int polygon = 0;
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] is PolygonCollider2D)
                    {
                        polygon++;
                    }
                }
                RecordFlag("P2.Col", colliders.Length);
                RecordFlag("P2.Poly", polygon);
                Rigidbody2D[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody2D>();
                int dynamic = 0;
                int kinematic = 0;
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (bodies[i].bodyType == RigidbodyType2D.Dynamic)
                    {
                        dynamic++;
                    }
                    else if (bodies[i].bodyType == RigidbodyType2D.Kinematic)
                    {
                        kinematic++;
                    }
                }
                RecordFlag("P2.Rb", bodies.Length);
                RecordFlag("P2.Dyn", dynamic);
                RecordFlag("P2.Kin", kinematic);
                // 固定步长和倍速：如果播放时 fixedDeltaTime 被改小，物理一帧要跑多步，
                // 那就完全是另一回事了。
                RecordFlag("P2.Dt", Mathf.RoundToInt(Time.fixedDeltaTime * 10000f));
                RecordFlag("P2.Ts", Mathf.RoundToInt(Time.timeScale * 100f));
                // 0=FixedUpdate（引擎默认） 1=Update 2=Script（不自动步进）
                RecordFlag("P2.Mode", (int)Physics2D.simulationMode);
                // 1=查询前自动同步变换。抑制期间它必须为 1 —— 不自动步进之后
                // 这是唯一还能把移动过的碰撞体同步进 broadphase 的时机。
                RecordFlag("P2.AutoSync", Physics2D.autoSyncTransforms ? 1 : 0);
            }
            catch (Exception exception)
            {
                Main.Logger.Log("[诊断] 物理普查失败（已忽略）：" + exception.Message);
            }
        }

        internal static void Tick()
        {
            enabledCache = Enabled;
            if (!Enabled)
            {
                return;
            }
            EnsureCanvasHook();
            EnsureCameraHook();
            EnsureRecorders();
            EnsurePlayerLoopProbe();
            // **状态标记必须按窗口累计，不能只在窗口末尾取一次快照。**
            //
            // 踩过的坑：帧耗时是 120 帧的平均值，而原来这里写的是"这一刻"的
            // 播放位。于是一个"最后几帧才开始播放、前面一直暂停"的窗口会被标成
            // 播放态，我据此把暂停的 7.0ms 当成"播放已经达标"——结论直接反了。
            // 现在记的是"本窗口有多少帧处于该状态"，120=全程播放，0=全程暂停，
            // 中间值说明窗口跨界，这种样本不该和其他样本一起平均。
            int nativePlaying = EditorStudioOverlay.IsNativePlayModeRunning() ? 1 : 0;
            if (nativePlaying != 0)
            {
                playFrames++;
            }
            float delta = Time.unscaledDeltaTime;
            frames++;
            deltaSum += delta;
            if (delta > deltaWorst)
            {
                deltaWorst = delta;
            }
            // 把「刚结束那一帧」的总时长和它的分段账对上，留最差的一份。
            // 只在真的记过分段时才比 —— 工作台不可见的帧本来就没有账，
            // 拿它去比会把一个"没数据的 30ms"当成最差帧。
            if (frameParts.Count > 0 && delta > worstPartsDelta)
            {
                worstPartsDelta = delta;
                worstPartOrder.Clear();
                worstParts.Clear();
                for (int i = 0; i < framePartOrder.Count; i++)
                {
                    string key = framePartOrder[i];
                    worstParts[key] = frameParts[key];
                    worstPartOrder.Add(key);
                }
                worstCanvasMs = frameCanvasMs;
                worstGcCount = frameGcCount;
            }
            frameParts.Clear();
            framePartOrder.Clear();
            frameCanvasMs = 0.0;

            // GC 计数按帧记；第一次读只当作基准，不然开局会报出一堆假 GC。
            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            if (gcPrimed)
            {
                int d0 = gc0 - lastGc0;
                int d1 = gc1 - lastGc1;
                int d2 = gc2 - lastGc2;
                gcSum0 += d0;
                gcSum1 += d1;
                gcSum2 += d2;
                frameGcCount = d0 + d1 + d2;
            }
            else
            {
                gcPrimed = true;
                frameGcCount = 0;
            }
            lastGc0 = gc0;
            lastGc1 = gc1;
            lastGc2 = gc2;
            reportClock += delta;
            for (int i = 0; i < recorders.Count; i++)
            {
                RecorderSlot slot = recorders[i];
                if (!slot.Valid)
                {
                    continue;
                }
                // ProfilerRecorder 的时间值单位是**纳秒**。
                slot.SumMs += slot.Recorder.LastValue / 1000000.0;
            }
            if (frames < ReportFrames && reportClock < ReportSeconds)
            {
                return;
            }

            CensusPhysics();
            try
            {
            builder.Length = 0;
            builder.Append("[诊断] 帧 ")
                .Append((deltaSum / Mathf.Max(1, frames) * 1000f).ToString("0.0"))
                .Append("ms 最差 ").Append((deltaWorst * 1000f).ToString("0.0")).Append("ms");
            // 播放头位置（秒）。没有它，日志里的样本只能按"第几条"排列，
            // 看不出"前半程快、后半程慢"这种随谱面位置变化的规律——
            // 而那正是要查的东西。窗口平均值配上一个窗口末尾的位置足够对齐。
            builder.Append(" 时间 ")
                .Append(EditorPlaybackBridge.PreviewClockTime.ToString("0.0")).Append("s");
            // 「本窗口有多少帧在播放 / 工作台可见」。120=全程，0=全程没有，
            // 中间值说明窗口跨了状态边界——这种样本不能和纯态样本一起平均。
            builder.Append(" 状态 播").Append(playFrames)
                .Append("/可").Append(visFrames)
                .Append('/').Append(frames);

            // **域按耗时降序排**。以前按首次出现的插入顺序取前 6 个：
            // Deco.Model 这类子项总比 DecorationCore 先注册，会把主域挤出列表，
            // 而 Filter / Particle / Planet 这些大域排得更靠后 —— 日志里永远
            // 看不见它们，"谁是最大域"一直是从残缺样本里猜的。
            // 排序是 2 秒一次的诊断路径，闭包分配可以忽略。
            if (domainOrder.Count > 0)
            {
                domainOrder.Sort((a, b) => domainSum[b].CompareTo(domainSum[a]));
                builder.Append(" | 域 ");
                int limit = domainOrder.Count < MaxDomainLines
                    ? domainOrder.Count : MaxDomainLines;
                for (int i = 0; i < limit; i++)
                {
                    string name = domainOrder[i];
                    if (i > 0)
                    {
                        builder.Append(' ');
                    }
                    builder.Append(name).Append('=')
                        .Append((domainSum[name] / Mathf.Max(1, frames)).ToString("0.00"));
                }
            }
            // 段同样按耗时降序：Timeline.U 加进来之后共 4~7 段，
            // 排序比固定顺序更能直接看出"这一帧时间花在哪个面板"。
            if (partOrder.Count > 0)
            {
                partOrder.Sort((a, b) => partSum[b].CompareTo(partSum[a]));
                builder.Append(" | 段 ");
                int limit = partOrder.Count < MaxPartLines
                    ? partOrder.Count : MaxPartLines;
                for (int i = 0; i < limit; i++)
                {
                    string name = partOrder[i];
                    if (i > 0)
                    {
                        builder.Append(' ');
                    }
                    builder.Append(name).Append('=')
                        .Append((partSum[name] / Mathf.Max(1, frames)).ToString("0.00"));
                }
            }
            if (flagOrder.Count > 0)
            {
                builder.Append(" | 计数 ");
                int limit = flagOrder.Count < MaxFlagLines
                    ? flagOrder.Count : MaxFlagLines;
                for (int i = 0; i < limit; i++)
                {
                    string name = flagOrder[i];
                    if (i > 0)
                    {
                        builder.Append(' ');
                    }
                    builder.Append(name).Append('=').Append(flagValue[name]);
                }
            }
            // 画布重建：本窗口内每帧平均的「布局+几何重建耗时」与「渲染的画布数」。
            // 静止帧这项应该趋近 0；一直居高不下就是有东西每帧在盲写 RectTransform。
            builder.Append(" | 画布 重建")
                .Append((canvasRebuildSum / Mathf.Max(1, frames)).ToString("0.00"))
                .Append("ms 渲染")
                .Append((canvasRenderCount / (float)Mathf.Max(1, frames)).ToString("0.0"))
                .Append("次/帧");
            // 相机渲染（剔除 + 提交）：播放时相机一直在动，这笔是"播放 vs 静止"
            // 差值里最可疑的一项，而且完全不在模组的 `段` 统计里。
            builder.Append(" 相机")
                .Append((cameraRenderSum / Mathf.Max(1, frames)).ToString("0.00"))
                .Append("ms/")
                .Append((cameraRenderCount / (float)Mathf.Max(1, frames)).ToString("0.0"))
                .Append("台");
            // 引擎跑 MonoBehaviour 的那两段。这是最后一笔说不清的钱：
            // 减干净以后 13.7ms 里还有 8.2ms 不落在模组任何一段、也不落在渲染上。
            // 本机正式版把 `BehaviourUpdate` 这类剖析标记剥掉了（全机只剩 44 个），
            // 所以改用 PlayerLoop 注入自己在前后打时间戳 —— 只有这两行能回答
            // 「这 8.2ms 到底是引擎在跑 2620 个 scrFloor，还是别的东西」。
            builder.Append(" | 阶段");
            float denom = Mathf.Max(1, frames);
            for (int i = 0; i < PhaseCount; i++)
            {
                builder.Append(' ').Append(phaseNames[i])
                    .Append((phaseSum[i] / denom).ToString("0.00"));
            }
            // `固余`/`后余` = 阶段总时长 − 包到的子项之和。
            // 这两个余数是不是 0，直接决定下一步查哪里：
            // 余数≈阶段总时长 → 时间花在阶段自身（或没包到的子项），
            // 余数≈0 → 已经锁定到具体子项了。
            builder.Append(" 固次").Append((phaseHits[1] / denom).ToString("0.0"));
            double fixedChildSum = 0.0;
            double postChildSum = 0.0;
            for (int i = 0; i < DetailPerGroup; i++)
            {
                fixedChildSum += phaseSum[DetailStart + i];
                postChildSum += phaseSum[DetailStart + DetailPerGroup + i];
            }
            builder.Append(" 固余")
                .Append(((phaseSum[1] - fixedChildSum) / denom).ToString("0.00"))
                .Append(" 后余")
                .Append(((phaseSum[5] - postChildSum) / denom).ToString("0.00"));
            // 细项只打超过 0.02ms 的，按大小从大到小排 —— 这一行本来就已经很长，
            // 而真正要看的永远是最大的那两三个。
            if (detailName[DetailStart] != null)
            {
                List<int> order = new List<int>(DetailMax);
                for (int i = 0; i < DetailMax; i++)
                {
                    // **这里必须是 continue，不能是 break。**
                    // 槽位是「固 20 个 + 后置 20 个」两段拼起来的，而实测
                    // `FixedUpdate` 只有 19 个子项 —— 于是第 19 号槽是个空位。
                    // 原来写 break，扫描走到这个空位就整段结束，
                    // 后置那 20 个槽一个都打不出来。表现是日志里「后余」有
                    // 1.3~2.0ms 却看不到任何 `后/` 细项，等于白白留了个盲区。
                    if (detailName[DetailStart + i] == null)
                    {
                        continue;
                    }
                    if (phaseSum[DetailStart + i] / denom >= 0.01)
                    {
                        order.Add(i);
                    }
                }
                order.Sort(delegate (int a, int b)
                {
                    return phaseSum[DetailStart + b].CompareTo(phaseSum[DetailStart + a]);
                });
                if (order.Count > 0)
                {
                    builder.Append(" | 细项");
                    int limit = order.Count < 10 ? order.Count : 10;
                    for (int i = 0; i < limit; i++)
                    {
                        int index = DetailStart + order[i];
                        // 两条竖线的名字太长，日志一行已经快撑爆了，缩成短标签。
                        string label = detailName[index];
                        label = label.Replace("FixedUpdate.", "固/")
                            .Replace("PostLateUpdate.", "后/");
                        builder.Append(' ').Append(label)
                            .Append((phaseSum[index] / denom).ToString("0.00"));
                    }
                }
            }
            builder.Append(" | 引擎 Update")
                .Append((behaviourSum / denom).ToString("0.00"))
                .Append("ms Late")
                .Append((lateSum / denom).ToString("0.00"))
                .Append("ms");
            // 本窗口发生了几次 GC（0/1/2 代）。120 帧里 0~1 次算常态，
            // 出现两位数就说明每帧都在分配，得先治分配再谈微优化。
            builder.Append(" | GC ").Append(gcSum0).Append('/').Append(gcSum1)
                .Append('/').Append(gcSum2);
            if (playerLoopProbeBroken)
            {
                builder.Append("(探针异常已停)");
            }
            AppendProfilerLines();
            Main.Logger.Log(builder.ToString());
            ReportWorstFrame();
            }
            catch (Exception exception)
            {
                // **诊断自己出错，绝不能拖住工作台。** 2026-10-31：detailName 按
                // DetailMax 开数组却按绝对槽位下标访问，越界异常从 Tick 冒到
                // UpdateCore，每帧中断一次，整个工作台卡死。计时探针是为了找
                // 性能问题，不能反过来制造问题 —— 所以整段拼装+输出套 try，
                // 重置一律放 finally，出错只记一行、下一窗口照常跑。
                Main.Logger.Log("[诊断] 报告生成失败（已忽略，不影响功能）："
                    + exception.Message);
            }
            finally
            {
            frames = 0;
            deltaSum = 0f;
            deltaWorst = 0f;
            reportClock = 0f;
            playFrames = 0;
            visFrames = 0;
            domainSum.Clear();
            domainOrder.Clear();
            partSum.Clear();
            partOrder.Clear();
            canvasRebuildSum = 0.0;
            canvasRenderCount = 0;
            cameraRenderSum = 0.0;
            cameraRenderCount = 0;
            behaviourSum = 0.0;
            lateSum = 0.0;
            worstPartsDelta = 0f;
            worstParts.Clear();
            worstPartOrder.Clear();
            worstCanvasMs = 0.0;
            worstGcCount = 0;
            gcSum0 = 0;
            gcSum1 = 0;
            gcSum2 = 0;
            frameParts.Clear();
            framePartOrder.Clear();
            frameCanvasMs = 0.0;
            for (int i = 0; i < SlotCount; i++)
            {
                phaseSum[i] = 0.0;
                phaseHits[i] = 0;
            }
            // flag 不清：状态量没有"窗口"，下一次同名记录会覆盖。
            }
        }

        /// <summary>
        /// 打本窗口**最差那一帧**的分段账。
        ///
        /// 为什么要单独一行：平均帧耗时看不出波动。用户说的"波动大"是
        /// 「大多数帧 6.6ms、隔几十帧蹦一个 20ms」这种形态，而 120 帧
        /// 平均会把那一个 20ms 摊薄成 +0.1ms，从报表上完全看不出来。
        /// 这一行把那个 20ms 自己摊开，直接看是谁吃掉的。
        ///
        /// 只列前 10 名，且只列 ≥0.02ms 的 —— 一帧里排不进前十的部分
        /// 加起来也不到 0.2ms，列全反而不好读。
        /// </summary>
        private static void ReportWorstFrame()
        {
            if (worstParts.Count == 0)
            {
                return;
            }
            worstPartOrder.Sort((a, b) => worstParts[b].CompareTo(worstParts[a]));

            StringBuilder builder = new StringBuilder(320);
            builder.Append("[最差帧] ")
                .Append((worstPartsDelta * 1000f).ToString("0.0")).Append("ms");
            int limit = worstPartOrder.Count < 10 ? worstPartOrder.Count : 10;
            for (int i = 0; i < limit; i++)
            {
                string name = worstPartOrder[i];
                double ms = worstParts[name];
                if (ms < 0.02)
                {
                    break;
                }
                builder.Append(' ').Append(name).Append('=').Append(ms.ToString("0.00"));
            }
            // 画布重建不属于 `RecordPart`，但它正是"突然卡一下"的头号嫌疑，
            // 单列出来。GC 同理 —— 它会被算进当时正在跑的那段代码里，
            // 却不会被任何一段认领。
            builder.Append(" 画布重建=").Append(worstCanvasMs.ToString("0.00"));
            builder.Append(" GC=").Append(worstGcCount);
            Main.Logger.Log(builder.ToString());
        }
    }
}
