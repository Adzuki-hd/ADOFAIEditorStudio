using System;
using System.Collections.Generic;
using ADOFAIEditorStudio.Preview.Timeline;

namespace ADOFAIEditorStudio.Tests
{
    /// <summary>
    /// `ChannelTimeline` 的求值正确性测试。
    ///
    /// 这个类是 1.0.0 重构的地基：每个格子的每个通道（位置/旋转/缩放/透明度）
    /// 都靠它从「一堆带时间的值」算出「这一刻是什么」。
    ///
    /// **最重要的是最后那组「同一时刻反复求值结果相同」。** 整个重构的目标
    /// 是让拖动到任意位置画面都确定；如果求值本身会受调用顺序影响，
    /// 后面所有工作都白做。这一条必须是硬的。
    ///
    /// 跑法：`dotnet run --project tests/ProjectFileTests`
    /// 退出码 0 = 全过。
    /// </summary>
    internal static class ChannelTimelineTests
    {
        /// <summary>线性缓动，测试里用得多，单独提出来。</summary>
        private static float Linear(float t)
        {
            return t;
        }

        /// <summary>平方曲线，用来验证「缓动真的被用上了」。</summary>
        private static float Square(float t)
        {
            return t * t;
        }

        internal static void Run()
        {
            TestEmptyReturnsFallback();
            TestSingleEventHoldsValue();
            TestInterpolatesBetweenTwoEvents();
            TestAppliesEaseCurve();
            TestZeroDurationAppliesImmediately();
            TestNegativeInfinityIsScatterState();
            TestLaterEventWinsOnSameStart();
            TestEvaluationIsOrderIndependent();
        }

        private static void TestEmptyReturnsFallback()
        {
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Sort();

            float value;
            bool has = timeline.TryEvaluate(1.0, 0.5f, out value);
            Program.Check(!has, "空时间轴：TryEvaluate 应返回 false");
            Program.CheckEqual(0.5f, value, "空时间轴：应回落到 fallback");
        }

        private static void TestSingleEventHoldsValue()
        {
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(1.0, 0f, 0.25f, Linear);
            timeline.Sort();

            float value;
            timeline.TryEvaluate(0.5, 1f, out value);
            Program.CheckEqual(1f, value, "事件还没开始：应保持 fallback");

            timeline.TryEvaluate(1.0, 1f, out value);
            Program.CheckEqual(0.25f, value, "事件刚开始：应立刻取目标值");

            timeline.TryEvaluate(99.0, 1f, out value);
            Program.CheckEqual(0.25f, value, "事件早已结束：应保持终值");
        }

        private static void TestInterpolatesBetweenTwoEvents()
        {
            // t=0 起，4 秒从 0 走到 1。
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(0.0, 4f, 1f, Linear);
            timeline.Sort();

            float value;
            timeline.TryEvaluate(0.0, 0f, out value);
            Program.CheckNear(0f, value, 0.001f, "插值：起点应为 0");

            timeline.TryEvaluate(2.0, 0f, out value);
            Program.CheckNear(0.5f, value, 0.001f, "插值：中点应为 0.5");

            timeline.TryEvaluate(4.0, 0f, out value);
            Program.CheckNear(1f, value, 0.001f, "插值：终点应为 1");

            timeline.TryEvaluate(10.0, 0f, out value);
            Program.CheckNear(1f, value, 0.001f, "插值：跑完应保持 1");
        }

        private static void TestAppliesEaseCurve()
        {
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(0.0, 4f, 1f, Square);
            timeline.Sort();

            float value;
            timeline.TryEvaluate(2.0, 0f, out value);
            // 平方曲线在 0.5 处给出 0.25，不是线性的 0.5。
            Program.CheckNear(0.25f, value, 0.001f, "缓动：平方曲线中点应为 0.25");
        }

        private static void TestZeroDurationAppliesImmediately()
        {
            // 谱面里 `opacity: 0, duration: 0` 就是用来越过一切、当场生效的。
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(5.0, 0f, 0f, Linear);
            timeline.Sort();

            float value;
            timeline.TryEvaluate(4.999, 1f, out value);
            Program.CheckEqual(1f, value, "零时长：生效之前应保持 fallback");

            timeline.TryEvaluate(5.0, 1f, out value);
            Program.CheckEqual(0f, value, "零时长：到达时刻应立刻变成目标值");

            timeline.TryEvaluate(1000.0, 1f, out value);
            Program.CheckEqual(0f, value, "零时长：之后应永久保持");
        }

        private static void TestNegativeInfinityIsScatterState()
        {
            // 散开段的起始是负无穷，代表「格存在之前就一直保持这个状态」。
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(double.NegativeInfinity, 0f, 0.75f, Linear);
            timeline.Add(10.0, 1f, 0f, Linear);
            timeline.Sort();

            float value;
            timeline.TryEvaluate(-9999.0, 0f, out value);
            Program.CheckEqual(0.75f, value, "散开段：任意早的时刻都应取它的值");

            timeline.TryEvaluate(0.0, 0f, out value);
            Program.CheckEqual(0.75f, value, "散开段：零时刻也取它的值");
        }

        private static void TestLaterEventWinsOnSameStart()
        {
            // 同一时刻两条事件：后加的那条应当赢（谱面按书写顺序生效）。
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(1.0, 0f, 0.1f, Linear);
            timeline.Add(1.0, 0f, 0.9f, Linear);
            timeline.Sort();

            float value;
            timeline.TryEvaluate(1.0, 0f, out value);
            Program.CheckEqual(0.9f, value, "同一时刻两条事件：后加的那条应当赢");
        }

        private static void TestEvaluationIsOrderIndependent()
        {
            // **这是整个重构的地基。**
            //
            // 同一个时刻，正序问、逆序问、重复问，结果必须逐位相同。
            // 如果这条不成立，「拖到任意位置画面一致」就无从谈起。
            ChannelTimeline timeline = new ChannelTimeline();
            timeline.Add(double.NegativeInfinity, 0f, 0.3f, Linear);
            timeline.Add(2.0, 3f, 0.8f, Linear);
            timeline.Add(6.0, 2f, 0.1f, Square);
            timeline.Sort();

            double[] times = { 0.0, 1.0, 2.5, 4.0, 5.0, 6.5, 7.5, 100.0 };

            float[] forward = new float[times.Length];
            for (int i = 0; i < times.Length; i++)
            {
                timeline.TryEvaluate(times[i], 0.5f, out forward[i]);
            }

            float[] backward = new float[times.Length];
            for (int i = times.Length - 1; i >= 0; i--)
            {
                timeline.TryEvaluate(times[i], 0.5f, out backward[i]);
            }

            for (int i = 0; i < times.Length; i++)
            {
                Program.CheckEqual(forward[i], backward[i],
                    "顺序无关：t=" + times[i].ToString("0.0") + " 正序与逆序结果必须逐位相同");
            }

            // 同一时刻连问三次。
            float a, b, c;
            timeline.TryEvaluate(4.0, 0.5f, out a);
            timeline.TryEvaluate(4.0, 0.5f, out b);
            timeline.TryEvaluate(4.0, 0.5f, out c);
            Program.Check(a == b && b == c, "顺序无关：同一时刻连问三次结果必须完全相同");
        }
    }
}
