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
    /// [诊断] 帧 16.7ms(60fps) 最差 48.2ms | 构建 2.1 渲染 0.2 搬运 3.4ms
    ///        命令 42 顶点 5210 | 域 DecorationCore=1.2 MoveTrack=0.1
    /// </code>
    /// </summary>
    internal static class PreviewDiagnostics
    {
        private const int ReportFrames = 120;
        private const float ReportSeconds = 2f;

        internal static bool Enabled
        {
            get { return Main.Settings != null && Main.Settings.VerboseDiagnostics; }
        }

        // 帧
        private static int frames;
        private static float deltaSum;
        private static float deltaWorst;
        private static float reportClock;

        // 各域耗时（按名字聚合，数量有限）
        private static readonly Dictionary<string, double> domainSum
            = new Dictionary<string, double>();
        private static readonly List<string> domainOrder = new List<string>();

        private static readonly StringBuilder builder = new StringBuilder(256);

        // 模组自身各段的耗时（Overlay.Update / Overlay.LateUpdate / Bridge.Update）
        private static readonly Dictionary<string, double> partSum
            = new Dictionary<string, double>();
        private static readonly List<string> partOrder = new List<string>();

        // 纯状态量（不是耗时），用于确认走的是哪条分支
        private static readonly Dictionary<string, int> flagValue
            = new Dictionary<string, int>();
        private static readonly List<string> flagOrder = new List<string>();

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
        }

        /// <summary>
        /// 每帧喂一次；攒够 120 帧或 2 秒打一行。
        ///
        /// **这段原先是 ImGui 桥接层负责的**，桥删掉之后没人输出了 ——
        /// 数字还在攒、却永远不落地，等于白攒。现在由工作台自己的 Update 调它。
        /// </summary>
        internal static void Tick()
        {
            if (!Enabled)
            {
                return;
            }
            float delta = Time.unscaledDeltaTime;
            frames++;
            deltaSum += delta;
            if (delta > deltaWorst)
            {
                deltaWorst = delta;
            }
            reportClock += delta;
            if (frames < ReportFrames && reportClock < ReportSeconds)
            {
                return;
            }

            builder.Length = 0;
            builder.Append("[诊断] 帧 ")
                .Append((deltaSum / Mathf.Max(1, frames) * 1000f).ToString("0.0"))
                .Append("ms 最差 ").Append((deltaWorst * 1000f).ToString("0.0")).Append("ms");

            if (domainOrder.Count > 0)
            {
                builder.Append(" | 域 ");
                for (int i = 0; i < domainOrder.Count && i < 6; i++)
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
            if (partOrder.Count > 0)
            {
                builder.Append(" | 段 ");
                for (int i = 0; i < partOrder.Count && i < 6; i++)
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
            Main.Logger.Log(builder.ToString());

            frames = 0;
            deltaSum = 0f;
            deltaWorst = 0f;
            reportClock = 0f;
            domainSum.Clear();
            domainOrder.Clear();
            partSum.Clear();
            partOrder.Clear();
        }
    }
}
