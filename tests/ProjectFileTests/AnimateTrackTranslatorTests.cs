using System;
using System.Collections.Generic;
using ADOFAIEditorStudio.Preview.Timeline;

namespace ADOFAIEditorStudio.Tests
{
    /// <summary>
    /// `AnimateTrackTranslator` 的测试。
    ///
    /// **这是 1.0.0 重构的技术核心。** 现在 AnimateTrack 和 MoveTrack 是两个
    /// 独立的求值域，各自维护通道所有权，靠「谁让位」的规则协调——
    /// 那套规则是联调出来的，没有单一判据，所以反复出 bug。
    ///
    /// 翻译器的做法：把「出现 / 消失」先翻译成一批 MoveTrack 片段，
    /// 和真实的 MoveTrack 进同一张时间表，统一按起始时间排序。
    /// 一旦只有一张表，「谁后启动谁赢」就是排序的自然结果，不需要让位规则。
    ///
    /// 跑法：`dotnet run --project tests/ProjectFileTests`
    /// </summary>
    internal static class AnimateTrackTranslatorTests
    {
        private static float Linear(float t)
        {
            return t;
        }

        internal static void Run()
        {
            TestNoneProducesNothing();
            TestAssembleProducesScatterAndSettle();
            TestGlobalSettingsWithoutEventsStillProduceFragments();
            TestLaterEventChangesTheAnimation();
        }

        private static void TestNoneProducesNothing()
        {
            AnimateTrackTranslator translator = new AnimateTrackTranslator();

            // 完全没有事件，也用 None / None 的默认设置。
            translator.SetGlobalSettings(
                AnimateTrackKind.None, 0f, AnimateTrackKind.None, 0f);

            List<MoveTrackFragment> fragments = translator.Translate();
            Program.CheckEqual(0, fragments.Count, "None：不产生任何片段");
        }

        private static void TestAssembleProducesScatterAndSettle()
        {
            // 第 1 格用 Assemble 出现，提前 3 拍。
            AnimateTrackTranslator translator = new AnimateTrackTranslator();
            translator.SetFloorCount(3);
            translator.SetFloor(1, entryTime: 10.0, crotchet: 0.5f, baselineX: 100f, baselineY: 200f,
                baselineRotation: 0f, baselineScaleX: 100f, baselineScaleY: 100f, baselineOpacity: 1f);
            translator.SetGlobalSettings(
                AnimateTrackKind.Assemble, 3f, AnimateTrackKind.None, 0f);

            List<MoveTrackFragment> fragments = translator.Translate();

            // Assemble 应当产生两段：
            //   1. 散开（起始 -∞，位置偏向随机）
            //   2. 落回（起始 = entryTime - 提前量，位置回到基线）
            bool hasScatter = false;
            bool hasSettle = false;
            for (int i = 0; i < fragments.Count; i++)
            {
                MoveTrackFragment f = fragments[i];
                if (double.IsNegativeInfinity(f.Start))
                {
                    hasScatter = true;
                    Program.Check(f.HasX || f.HasY, "Assemble：散开段应当有位置");
                }
                else if (f.Floor == 1 && (f.HasX || f.HasY))
                {
                    // 只看第 1 格：第 2 格没设基线，它自己的落回段是 0，不算错。
                    hasSettle = true;
                    Program.CheckNear(100f, f.X, 0.001f, "Assemble：落回段 X 应回到基线");
                    Program.CheckNear(200f, f.Y, 0.001f, "Assemble：落回段 Y 应回到基线");
                }
            }
            Program.Check(hasScatter, "Assemble：应当产生散开段");
            Program.Check(hasSettle, "Assemble：应当产生落回段");
        }

        private static void TestGlobalSettingsWithoutEventsStillProduceFragments()
        {
            // 谱面只在 settings 里声明了全局动画、一个 AnimateTrack 事件都没有。
            // 这种谱也必须产生动画——否则整张谱没有任何出现效果。
            AnimateTrackTranslator translator = new AnimateTrackTranslator();
            translator.SetFloorCount(2);
            translator.SetFloor(1, entryTime: 10.0, crotchet: 0.5f, baselineX: 50f, baselineY: 60f,
                baselineRotation: 0f, baselineScaleX: 100f, baselineScaleY: 100f, baselineOpacity: 1f);
            translator.SetGlobalSettings(
                AnimateTrackKind.Assemble, 3f, AnimateTrackKind.None, 0f);

            List<MoveTrackFragment> fragments = translator.Translate();
            Program.Check(fragments.Count > 0,
                "全局设置（无事件）：也应产生出现片段");
        }

        private static void TestLaterEventChangesTheAnimation()
        {
            // 第 1 格 Assemble，第 2 格改成 Fade。
            // 第 2 格应当用 Fade（透明度从 0 淡入），不是 Assemble。
            AnimateTrackTranslator translator = new AnimateTrackTranslator();
            translator.SetFloorCount(3);
            translator.SetFloor(1, entryTime: 10.0, crotchet: 0.5f, baselineX: 0f, baselineY: 0f,
                baselineRotation: 0f, baselineScaleX: 100f, baselineScaleY: 100f, baselineOpacity: 1f);
            translator.SetFloor(2, entryTime: 11.0, crotchet: 0.5f, baselineX: 0f, baselineY: 0f,
                baselineRotation: 0f, baselineScaleX: 100f, baselineScaleY: 100f, baselineOpacity: 1f);

            translator.SetGlobalSettings(
                AnimateTrackKind.Assemble, 3f, AnimateTrackKind.None, 0f);
            translator.AddEvent(2, AnimateTrackKind.Fade, 3f, AnimateTrackKind.None, 0f);

            List<MoveTrackFragment> fragments = translator.Translate();

            // 第 2 格的片段里应当有「从透明度 0 出发」的段。
            bool fadeFound = false;
            for (int i = 0; i < fragments.Count; i++)
            {
                MoveTrackFragment f = fragments[i];
                if (f.Floor == 2 && f.HasOpacity && f.Opacity < 0.01f)
                {
                    fadeFound = true;
                }
            }
            Program.Check(fadeFound, "后置事件：第 2 格应改用 Fade（透明度从 0 起）");
        }
    }
}
