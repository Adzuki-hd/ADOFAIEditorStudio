using System.Diagnostics;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 查「选中装饰物的那一刻，画面整体异常」。
    ///
    /// 三个入口（画面点击 / 右侧装饰物列表 / 时间轴）都走
    /// `scnEditor.SelectDecoration(LevelEvent, bool, bool, bool, bool)`，
    /// 所以打在它上面一次就覆盖全部。
    ///
    /// 这个函数里有两件可能很贵的事：
    ///
    ///   1. `SaveStateScope` → `SaveState` → `levelData.Copy()`
    ///      **深拷贝整个关卡数据**。本谱 1303 条事件、585 条 MoveTrack，
    ///      一次拷贝不是小数目。
    ///   2. `MoveCameraToDecoration`（只有 `jumpToDecoration` 为真时才调，
    ///      AES 三个入口传的都是 false，所以正常情况下走不到）。
    ///
    /// 探针只在前置模式下工作（`VerboseDiagnostics`），记：
    ///   · 这次选中花了多久（能分开「卡顿」和「状态被改坏」）
    ///   · 选中前后的相机世界坐标（有没有被挪走）
    ///   · 选中前后的球坐标（「球乱跳」是不是真的发生在这一刻）
    ///   · 求值器是不是还在跑
    ///
    /// 只在**真的会出问题的那一帧**留数据，所以不打节流。
    /// </summary>
    internal static class SelectDecorationProbe
    {
        internal static void Prefix(LevelEvent levelEvent, bool jumpToDecoration, ref long __state)
        {
            __state = 0L;
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            __state = Stopwatch.GetTimestamp();
            Main.Logger.Log("[SelectDeco] 开始 floor=" + (levelEvent == null ? -1 : levelEvent.floor)
                + " jump=" + jumpToDecoration
                + " cam=" + CameraPosition()
                + " ball=" + BallPosition()
                + " evaluating=" + Preview.PreviewEvaluationEngine.IsActive + ".");
        }

        /// <summary>
        /// 选中结束之后再看一次。
        ///
        /// 用 Postfix 而不是别处：`SelectDecoration` 内部会
        /// `SaveStateScope`，那个 Dispose 之后 `changingState` 才归零 ——
        /// 有些逻辑正是靠它判断「现在是不是在改状态」。
        /// </summary>
        internal static void Postfix(LevelEvent levelEvent, long __state)
        {
            if (Main.Settings == null || !Main.Settings.VerboseDiagnostics)
            {
                return;
            }

            double ms = __state == 0L
                ? 0.0
                : (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            Main.Logger.Log("[SelectDeco] 结束 用时=" + ms.ToString("0.0") + "ms"
                + " cam=" + CameraPosition()
                + " ball=" + BallPosition()
                + " evaluating=" + Preview.PreviewEvaluationEngine.IsActive + ".");
        }

        /// <summary>给 Postfix 用的起点时间戳。放 Prefix 里开，避免多写一个 Patch 参数。</summary>
        private static string CameraPosition()
        {
            scrCamera camera = scrCamera.instance;
            if (camera == null || camera.transform == null)
            {
                return "-";
            }
            Vector3 p = camera.transform.position;
            return p.x.ToString("0.00") + "," + p.y.ToString("0.00");
        }

        private static string BallPosition()
        {
            scrPlayerManager manager = scrPlayerManager.instance;
            if (manager == null || manager.players == null)
            {
                return "-";
            }
            for (int i = 0; i < manager.players.Length; i++)
            {
                scrPlayer player = manager.players[i];
                if (player != null && player.planetarySystem != null
                    && player.planetarySystem.chosenPlanet != null)
                {
                    Vector3 p = player.planetarySystem.chosenPlanet.transform.position;
                    return p.x.ToString("0.00") + "," + p.y.ToString("0.00");
                }
            }
            return "-";
        }
    }
}
