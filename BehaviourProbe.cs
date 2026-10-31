using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 【临时探针·引擎每帧回调】只读计时，不改变任何游戏行为。
    ///
    /// 为什么需要它：`[诊断]` 里 `引擎 Update`（= 包住
    /// `Update.ScriptRunBehaviourUpdate` 的那段时间）一直比"我们能记账的
    /// 那段加起来"多出 1.1~1.5ms。第一版只挂了 9 个猜测目标，实测合计
    /// 只有 0.21~0.30ms —— 猜错了对象。第二版自动扫了 `Assembly-CSharp`
    /// 里全部无参 `Update`/`LateUpdate`（409 条），合计 0.65ms，**仍然对不上**。
    ///
    /// 结论：钱不在游戏程序集里。所以这一版改成**扫所有已加载程序集**
    /// （跳过 mscorlib / System / UnityEngine 原生模块 —— 那几家的 Update
    /// 方法体是空的，真开销在 native 里，挂上去只会多一笔看不见的税）。
    /// 每条都打上程序集短标签，一眼就能看出是谁。
    ///
    /// 结果里额外给出 `U小计` / `L小计`：它们和 `[诊断]` 的 `引擎 Update`
    /// / `引擎 Late` 直接对照。两者相等 → 账结清了；`U小计` 明显小 →
    /// 还有程序集没扫到，继续放宽范围。
    ///
    /// 只在 `Main.Settings.VerboseDiagnostics` 打开时工作；关掉后前缀的
    /// 第一个 `if` 就返回，对游戏没有行为影响。
    /// </summary>
    internal static class BehaviourProbe
    {
        /// <summary>只在该窗口内累加。</summary>
        private static bool armed;

        private static bool registered;
        private static bool patched;

        /// <summary>一个被测方法。Ticks 是累计耗时，Count 是调用次数。</summary>
        private sealed class Slot
        {
            internal string Name;
            internal bool IsLate;
            internal long Ticks;
            internal int Count;
        }

        private static readonly List<Slot> slots = new List<Slot>();
        private static readonly Dictionary<MethodBase, int> indexByMethod =
            new Dictionary<MethodBase, int>();

        // 热路径缓存：同一帧里同一方法往往连着调用很多次，
        // 用它把字典查找省成一次引用比较。
        private static MethodBase cachedMethod;
        private static int cachedIndex;
        private const int NoSlot = -1;

        private static int windowStartFrame = -1;

        // 上限只是保险：挂补丁本身要编译，几千条会让开局卡好几秒。
        // 真撞上这个数，日志里会写出来，不至于悄悄少测一半还以为测全了。
        private const int MaxPatches = 2000;

        /// <summary>挂补丁。只做一次；失败就永久放弃，不反复重试。</summary>
        internal static void EnsureRegistered()
        {
            if (registered)
            {
                return;
            }
            registered = true;

            int hit = 0;
            int failed = 0;
            StringBuilder census = new StringBuilder(512);

            try
            {
                Harmony harmony = new Harmony("ADOFAIEditorStudio.BehaviourProbe");
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < assemblies.Length; a++)
                {
                    Assembly assembly = assemblies[a];
                    if (assembly == null || assembly.IsDynamic)
                    {
                        continue;
                    }
                    string assemblyName = assembly.GetName().Name;
                    if (SkipAssembly(assemblyName))
                    {
                        continue;
                    }

                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException exception)
                    {
                        // 加载不全时不放弃整批：能拿到的照样挂。
                        types = exception.Types;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    int before = hit;
                    for (int i = 0; i < types.Length; i++)
                    {
                        Type type = types[i];
                        if (type == null || type.IsAbstract || type.IsGenericTypeDefinition)
                        {
                            continue;
                        }
                        MethodInfo[] methods;
                        try
                        {
                            methods = type.GetMethods(
                                BindingFlags.Instance | BindingFlags.Public
                                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        for (int m = 0; m < methods.Length; m++)
                        {
                            MethodInfo method = methods[m];
                            if (method.IsAbstract || method.IsGenericMethod)
                            {
                                continue;
                            }
                            if (method.GetParameters().Length != 0
                                || method.ReturnType != typeof(void))
                            {
                                continue;
                            }
                            string name = method.Name;
                            bool isLate;
                            if (name == "Update")
                            {
                                isLate = false;
                            }
                            else if (name == "LateUpdate")
                            {
                                isLate = true;
                            }
                            else
                            {
                                continue;
                            }

                            if (PatchOne(harmony, assemblyName, type, method, isLate))
                            {
                                hit++;
                                if (hit >= MaxPatches)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                failed++;
                            }
                        }
                        if (hit >= MaxPatches)
                        {
                            break;
                        }
                    }
                    if (hit > before)
                    {
                        census.Append(ShortName(assemblyName)).Append('+')
                            .Append(hit - before).Append(' ');
                    }
                    if (hit >= MaxPatches)
                    {
                        break;
                    }
                }

                patched = hit > 0;
                PatchModOnUpdates(harmony, ref hit, ref failed, census);
                patched = hit > 0;
                Main.Logger.Log("[行为探针] 全程序集发现：挂上 " + hit + " 条只读计时补丁"
                    + (failed > 0 ? "（" + failed + " 条挂失败，通常是被内联）" : "")
                    + (hit >= MaxPatches ? "（已到上限 " + MaxPatches + "，后面的程序集没扫）" : "")
                    + "，分布：" + census);
            }
            catch (Exception exception)
            {
                patched = false;
                Main.Logger.Log("[行为探针] 挂补丁失败：" + exception.Message);
            }
        }

        /// <summary>
        /// 跳过框架程序集。判断依据不是"重要不重要"，而是
        /// **成本记不在这里**：UnityEngine.* 那几个原生模块的托管 `Update`
        /// 方法体是空壳，真正的时间花在 native 侧，挂补丁只多一笔税、
        /// 换不来任何信息。`UnityEngine.UI` 例外 —— EventSystem 的
        /// Update 是真的托管代码。
        /// </summary>
        private static bool SkipAssembly(string name)
        {
            if (name == null)
            {
                return true;
            }
            if (name == "mscorlib" || name == "netstandard" || name == "WindowsBase")
            {
                return true;
            }
            if (name.StartsWith("System", StringComparison.Ordinal))
            {
                return true;
            }
            if (name.StartsWith("Mono", StringComparison.Ordinal))
            {
                return true;
            }
            if (name.StartsWith("UnityEngine", StringComparison.Ordinal))
            {
                // UnityEngine.UI 是真托管实现，要留。
                return name != "UnityEngine.UI";
            }
            return false;
        }

        private static string ShortName(string assemblyName)
        {
            if (assemblyName == "Assembly-CSharp")
            {
                return "Game";
            }
            if (assemblyName == "Assembly-CSharp-firstpass")
            {
                return "First";
            }
            if (assemblyName == "UnityEngine.UI")
            {
                return "UI";
            }
            if (assemblyName == "ADOFAIEditorStudio")
            {
                return "AES";
            }
            // 其它一律保留原名，太长就截断。
            return assemblyName.Length <= 10 ? assemblyName
                : assemblyName.Substring(0, 10);
        }

        private static bool PatchOne(Harmony harmony, string assemblyName, Type type,
            MethodInfo method, bool isLate)
        {
            return PatchOne(harmony, method, isLate,
                ShortName(assemblyName) + ":" + type.Name + "."
                + (isLate ? "Late" : "Upda"));
        }

        private static bool PatchOne(Harmony harmony, MethodInfo method, bool isLate,
            string label)
        {
            try
            {
                // **先挂补丁、成功了再登记槽位。** 反过来的话，挂失败的
                // 方法会留下一个永远为 0 的槽，日志里白白占一个名次。
                harmony.Patch(method,
                    new HarmonyMethod(typeof(BehaviourProbe).GetMethod("Prefix", StaticFlags)),
                    new HarmonyMethod(typeof(BehaviourProbe).GetMethod("Postfix", StaticFlags)));

                Slot slot = new Slot();
                slot.Name = label;
                slot.IsLate = isLate;
                slots.Add(slot);
                indexByMethod[method] = slots.Count - 1;
                cachedMethod = null;
                return true;
            }
            catch (Exception)
            {
                // 单条挂不上不影响其它方法；这条顶多是个盲区。
                return false;
            }
        }

        /// <summary>
        /// 拆 `UnityModManager.UI.Update` 那 1ms。
        ///
        /// 为什么单列：UMM 的 `UI.Update()` 对自己 `modEntries` 里**每个
        /// 活跃 mod** 调一次 `OnUpdate(ModEntry, float)`。这个委托带两个
        /// 参数，所以前面"无参 Update/LateUpdate"的自动发现**一条都扫不到**
        /// —— 于是别的 mod 每帧干的活全被合并进了 UMM 自己那一格里，
        /// 表现就是"一个我们没写过的类吃了 1ms，却不知道是谁"。
        ///
        /// 这里直接顺着委托拿到每个 mod 的 `OnUpdate` 真实方法挂计时，
        /// 标签用 mod 的 Id。**只读**，不改任何回调行为。
        /// </summary>
        private static void PatchModOnUpdates(Harmony harmony, ref int hit,
            ref int failed, StringBuilder census)
        {
            try
            {
                List<UnityModManagerNet.UnityModManager.ModEntry> entries
                    = UnityModManagerNet.UnityModManager.modEntries;
                if (entries == null)
                {
                    return;
                }
                int before = hit;
                for (int i = 0; i < entries.Count; i++)
                {
                    UnityModManagerNet.UnityModManager.ModEntry entry = entries[i];
                    if (entry == null || entry.OnUpdate == null)
                    {
                        continue;
                    }
                    string id = entry.Info != null && entry.Info.Id != null
                        ? entry.Info.Id : "?";
                    Delegate[] targets = entry.OnUpdate.GetInvocationList();
                    for (int t = 0; t < targets.Length; t++)
                    {
                        MethodInfo method = targets[t] == null ? null : targets[t].Method;
                        if (method == null || method.IsAbstract || method.IsGenericMethod)
                        {
                            continue;
                        }
                        if (PatchOne(harmony, method, false, "OnUpd:" + id))
                        {
                            hit++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }
                if (hit > before)
                {
                    census.Append("OnUpd+").Append(hit - before).Append(' ');
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Log("[行为探针] 拆 OnUpdate 失败：" + exception.Message);
            }
        }

        private static readonly BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// 前缀只做两件事：判开关、记起点。`__state` 会把起点原样交给后缀，
        /// 所以同一帧里嵌套调用也不会串味。
        /// </summary>
        private static void Prefix(MethodBase __originalMethod, ref long __state)
        {
            __state = 0L;
            if (!armed)
            {
                return;
            }
            if (IndexOf(__originalMethod) == NoSlot)
            {
                return;
            }
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void Postfix(MethodBase __originalMethod, long __state)
        {
            if (!armed || __state == 0L)
            {
                return;
            }
            int index = IndexOf(__originalMethod);
            if (index == NoSlot)
            {
                return;
            }

            Slot slot = slots[index];
            slot.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - __state;
            slot.Count++;

            // 帧号一变就看看这个窗口够不够 120 帧。放在这里（而不是另开
            // 一个 MonoBehaviour）是因为这些方法每帧必被调用，天然就是时钟。
            int frame = Time.frameCount;
            if (frame == windowStartFrame)
            {
                return;
            }
            if (windowStartFrame < 0)
            {
                windowStartFrame = frame;
                return;
            }
            if (frame - windowStartFrame < 120)
            {
                return;
            }

            Flush();
            windowStartFrame = frame;
        }

        private static int IndexOf(MethodBase method)
        {
            if (ReferenceEquals(method, cachedMethod))
            {
                return cachedIndex;
            }
            int index;
            if (!indexByMethod.TryGetValue(method, out index))
            {
                return NoSlot;
            }
            cachedMethod = method;
            cachedIndex = index;
            return index;
        }

        private static void Flush()
        {
            double scale = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            // 按窗口平均耗时倒序，只打前 10 名 + 其余合计：
            // 全打出来日志会长得没法看，而第 11 名以后加起来也不到 0.1ms。
            List<int> order = new List<int>(slots.Count);
            double total = 0.0;
            double updateTotal = 0.0;
            double lateTotal = 0.0;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot.Count == 0)
                {
                    continue;
                }
                order.Add(i);
                double ms = slot.Ticks * scale / 120.0;
                total += ms;
                if (slot.IsLate)
                {
                    lateTotal += ms;
                }
                else
                {
                    updateTotal += ms;
                }
            }
            order.Sort(delegate (int a, int b)
            {
                return slots[b].Ticks.CompareTo(slots[a].Ticks);
            });

            StringBuilder builder = new StringBuilder(420);
            builder.Append("[行为探针] 时间 ")
                .Append(EditorPlaybackBridge.PreviewClockTime.ToString("0.0")).Append('s');
            double shown = 0.0;
            int limit = Mathf.Min(10, order.Count);
            for (int i = 0; i < limit; i++)
            {
                Slot slot = slots[order[i]];
                double ms = slot.Ticks * scale / 120.0;
                shown += ms;
                builder.Append(' ').Append(slot.Name).Append('=')
                    .Append(ms.ToString("0.000"))
                    .Append('/').Append(slot.Count / 120);
            }
            builder.Append(" 其余=").Append((total - shown).ToString("0.000"));
            builder.Append(" 合计=").Append(total.ToString("0.000")).Append("ms/帧");
            // 这两项直接和 `[诊断]` 的 `引擎 Update` / `引擎 Late` 对照。
            builder.Append(" U小计=").Append(updateTotal.ToString("0.000"));
            builder.Append(" L小计=").Append(lateTotal.ToString("0.000"));
            Main.Logger.Log(builder.ToString());

            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].Ticks = 0L;
                slots[i].Count = 0;
            }
        }

        /// <summary>每帧由 Overlay 调一次，决定这一帧要不要记。</summary>
        internal static void Tick()
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                // 关掉时立刻停手：补丁还挂着，但前缀第一个分支就返回。
                armed = false;
                return;
            }
            if (!registered)
            {
                EnsureRegistered();
            }
            armed = patched;
        }
    }
}
