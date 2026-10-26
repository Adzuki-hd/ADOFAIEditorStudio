using System.Diagnostics;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 只读探针：记录「谁把某一格淡到不可见」。
    ///
    /// 背景：轨道格的不透明度由 `scrFloor.opacity` 决定，游戏里有好几条
    /// 路径会 Tween 它（ChangeTrack 的 appear/disappear、MoveTrack 的
    /// opacity、freeroam 收尾……），还有求值器自己的写回。只看最终值
    /// 分不清是哪一条，所以这里在 TweenOpacity 上挂一个前缀，
    /// 目标接近 0 时把调用栈的头两帧写进日志。
    ///
    /// 只在设置 → 开发者 → 轨道探针打开时工作，每秒最多一条。
    /// </summary>
    internal static class FloorOpacityProbe
    {
        private static float lastLogTime = float.NegativeInfinity;

        private static void Prefix(scrFloor __instance, float opacity, float duration)
        {
            if (Main.Settings == null || !Main.Settings.TrackProbe)
            {
                return;
            }
            // 只关心「淡到看不见」的那一类；恢复成 1 的不记。
            if (opacity > 0.01f)
            {
                return;
            }
            if (Time.unscaledTime - lastLogTime < 1f)
            {
                return;
            }
            lastLogTime = Time.unscaledTime;

            // 帧 0 是 TweenOpacity 自己，帧 1 才是真正的调用方。
            string caller = "?";
            StackTrace trace = new StackTrace(1, false);
            if (trace.FrameCount > 1)
            {
                caller = Describe(trace.GetFrame(1));
                if (trace.FrameCount > 2)
                {
                    caller += " <- " + Describe(trace.GetFrame(2));
                }
            }

            Main.Logger.Log("[OpacityProbe] f"
                + (__instance == null ? -1 : __instance.seqID)
                + " to=" + opacity.ToString("0.00")
                + " dur=" + duration.ToString("0.000")
                + " base=" + (__instance == null ? "-" : __instance.opacityVal.ToString("0.00"))
                + " caller=" + caller);
        }

        private static string Describe(StackFrame frame)
        {
            if (frame == null)
            {
                return "?";
            }
            System.Reflection.MethodBase method = frame.GetMethod();
            if (method == null || method.DeclaringType == null)
            {
                return "?";
            }
            return method.DeclaringType.Name + "." + method.Name;
        }
    }
}
