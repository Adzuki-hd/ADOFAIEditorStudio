using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 【临时探针·烘焙内部拆分】只读计时，不改变任何游戏行为。
    ///
    /// 为什么需要它：原生 <c>scnGame.ApplyEventsToFloors</c> 一次要 1.6 秒，
    /// 但它是游戏自己的方法，我们没法在它内部插桩。而这个方法的开销几乎
    /// 全部落在四个可以被外部计时的调用上：
    ///
    ///   1. <c>UnityEngine.Object.DestroyImmediate</c> —— 每格拆掉旧的特效组件；
    ///   2. <c>UnityEngine.GameObject.AddComponent(Type)</c> —— 按事件重建组件；
    ///   3. <c>scnLevelMaker.CalculateSingleFloorAngleLength</c> —— 每格重算几何；
    ///   4. <c>scnLevelMaker.CalculateFloorEntryTimes</c> —— 整条时间线重算。
    ///
    /// 四个数加起来离 1.6 秒还差多远，就把剩下的归给"逐格写 transform /
    /// 材质 / 图标的那些循环"。先分清主次，再决定要不要动，避免猜着优化。
    ///
    /// **只在 <see cref="Armed"/> 为真时累计。** 也就是只在 AES 自己那一次
    /// 烘焙调用的前后窗口里工作，其余任何时候这几条补丁只是空转一下就返回，
    /// 对游戏其余部分没有行为影响。
    /// </summary>
    internal static class BakeProbe
    {
        /// <summary>是否处于"正在测量"的窗口内。</summary>
        internal static bool Armed;

        private static bool registered;

        private static long destroyStart;
        private static long destroyTicks;
        private static int destroyCount;

        private static long addStart;
        private static long addTicks;
        private static int addCount;

        private static long angleStart;
        private static long angleTicks;
        private static int angleCount;

        private static long entryStart;
        private static long entryTicks;
        private static int entryCount;

        /// <summary>开始测量。返回 false 表示补丁没装上，调用方不必再配对调用 End。</summary>
        internal static bool Begin()
        {
            if (!registered)
            {
                registered = true;
                Register();
            }

            if (!patched)
            {
                return false;
            }

            destroyTicks = 0; destroyCount = 0;
            addTicks = 0; addCount = 0;
            angleTicks = 0; angleCount = 0;
            entryTicks = 0; entryCount = 0;
            Armed = true;
            return true;
        }

        /// <summary>结束测量并写一行拆分日志。</summary>
        internal static void End(double totalMs)
        {
            Armed = false;

            double destroyMs = Ticks(destroyTicks);
            double addMs = Ticks(addTicks);
            double angleMs = Ticks(angleTicks);
            double entryMs = Ticks(entryTicks);
            double restMs = totalMs - destroyMs - addMs - angleMs - entryMs;

            Main.Logger.Log("[BakeProbe] 总计=" + totalMs.ToString("0.0") + "ms"
                + " 销毁=" + destroyMs.ToString("0.0") + "ms/" + destroyCount + "次"
                + " 增组件=" + addMs.ToString("0.0") + "ms/" + addCount + "次"
                + " 单格角度=" + angleMs.ToString("0.0") + "ms/" + angleCount + "次"
                + " 进入时间=" + entryMs.ToString("0.0") + "ms/" + entryCount + "次"
                + " 其余(逐格写状态)=" + restMs.ToString("0.0") + "ms");
        }

        private static double Ticks(long value)
        {
            return value * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static bool patched;

        private static void Register()
        {
            try
            {
                Harmony harmony = new Harmony("ADOFAIEditorStudio.BakeProbe");

                MethodInfo destroy = typeof(UnityEngine.Object).GetMethod(
                    "DestroyImmediate",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(UnityEngine.Object) },
                    null);
                MethodInfo add = typeof(GameObject).GetMethod(
                    "AddComponent",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(Type) },
                    null);
                MethodInfo entry = FindNoArg(typeof(scrLevelMaker), "CalculateFloorEntryTimes");
                MethodInfo angle = FindOneArg(typeof(scrLevelMaker), "CalculateSingleFloorAngleLength");

                int hit = 0;
                if (destroy != null)
                {
                    harmony.Patch(destroy,
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("DestroyPrefix", StaticFlags)),
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("DestroyPostfix", StaticFlags)));
                    hit++;
                }
                if (add != null)
                {
                    harmony.Patch(add,
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("AddPrefix", StaticFlags)),
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("AddPostfix", StaticFlags)));
                    hit++;
                }
                if (entry != null)
                {
                    harmony.Patch(entry,
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("EntryPrefix", StaticFlags)),
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("EntryPostfix", StaticFlags)));
                    hit++;
                }
                if (angle != null)
                {
                    harmony.Patch(angle,
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("AnglePrefix", StaticFlags)),
                        new HarmonyMethod(typeof(BakeProbe).GetMethod("AnglePostfix", StaticFlags)));
                    hit++;
                }

                patched = hit > 0;
                Main.Logger.Log("[BakeProbe] 已挂上 " + hit + "/4 条计时补丁。");
            }
            catch (Exception exception)
            {
                patched = false;
                Main.Logger.Log("[BakeProbe] 挂补丁失败：" + exception.Message);
            }
        }

        private static MethodInfo FindNoArg(Type target, string name)
        {
            MethodInfo[] methods = target.GetMethods(InstanceFlags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name && methods[i].GetParameters().Length == 0)
                {
                    return methods[i];
                }
            }
            return null;
        }

        private static MethodInfo FindOneArg(Type target, string name)
        {
            MethodInfo[] methods = target.GetMethods(InstanceFlags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name && methods[i].GetParameters().Length == 1)
                {
                    return methods[i];
                }
            }
            return null;
        }

        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static void DestroyPrefix()
        {
            if (Armed) { destroyStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }

        private static void DestroyPostfix()
        {
            if (!Armed) { return; }
            destroyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - destroyStart;
            destroyCount++;
        }

        private static void AddPrefix()
        {
            if (Armed) { addStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }

        private static void AddPostfix()
        {
            if (!Armed) { return; }
            addTicks += System.Diagnostics.Stopwatch.GetTimestamp() - addStart;
            addCount++;
        }

        private static void EntryPrefix()
        {
            if (Armed) { entryStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }

        private static void EntryPostfix()
        {
            if (!Armed) { return; }
            entryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - entryStart;
            entryCount++;
        }

        private static void AnglePrefix()
        {
            if (Armed) { angleStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }

        private static void AnglePostfix()
        {
            if (!Armed) { return; }
            angleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - angleStart;
            angleCount++;
        }
    }
}
