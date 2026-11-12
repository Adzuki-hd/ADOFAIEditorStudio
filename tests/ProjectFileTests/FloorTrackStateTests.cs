using System;
using ADOFAIEditorStudio.Preview.Timeline;

namespace ADOFAIEditorStudio.Tests
{
    /// <summary>
    /// `FloorTrackState` 的求值测试：一格的六个通道各自独立求值，
    /// 互不干扰。
    ///
    /// 为什么通道要拆开：谱面里一条 `positionOffset: [8, null]` 只动 X 轴，
    /// Y 轴要留给别的动画。把 Vector2 当一个整体处理，就得在每个用到的地方
    /// 单独判 null——那是现在这一批 bug 的来源之一。
    ///
    /// 跑法：`dotnet run --project tests/ProjectFileTests`
    /// </summary>
    internal static class FloorTrackStateTests
    {
        private static float Linear(float t)
        {
            return t;
        }

        internal static void Run()
        {
            TestEmptyFloorUsesBaseline();
            TestEachChannelIsIndependent();
            TestChannelsCanDifferAtSameTime();
        }

        private static void TestEmptyFloorUsesBaseline()
        {
            FloorTrackState state = new FloorTrackState();
            state.SetBaseline(1f, 2f, 30f, 100f, 100f, 1f);

            FloorPose pose = state.Evaluate(0.0);
            Program.CheckNear(1f, pose.X, 0.001f, "空格：X 应等于基线");
            Program.CheckNear(2f, pose.Y, 0.001f, "空格：Y 应等于基线");
            Program.CheckNear(30f, pose.Rotation, 0.001f, "空格：旋转应等于基线");
            Program.CheckNear(100f, pose.ScaleX, 0.001f, "空格：X 缩放应等于基线");
            Program.CheckNear(100f, pose.ScaleY, 0.001f, "空格：Y 缩放应等于基线");
            Program.CheckNear(1f, pose.Opacity, 0.001f, "空格：不透明度应等于基线");
        }

        private static void TestEachChannelIsIndependent()
        {
            // 只给 X 轴和透明度加动画，其余通道必须保持在基线上。
            FloorTrackState state = new FloorTrackState();
            state.SetBaseline(1f, 2f, 30f, 100f, 100f, 1f);

            state.PositionX.Add(0.0, 2f, 5f, Linear);
            state.Opacity.Add(0.0, 2f, 0f, Linear);

            FloorPose pose = state.Evaluate(1.0);
            // X 从 1 插值到 5，走到一半 → 3
            Program.CheckNear(3f, pose.X, 0.001f, "通道独立：X 应在插值中途");
            // Y / 旋转 / 缩放从未被写，必须原样
            Program.CheckNear(2f, pose.Y, 0.001f, "通道独立：Y 不该被 X 的动画带动");
            Program.CheckNear(30f, pose.Rotation, 0.001f, "通道独立：旋转不该被动");
            Program.CheckNear(100f, pose.ScaleX, 0.001f, "通道独立：X 缩放不该被动");
            // 透明度从 1 插值到 0，走到一半 → 0.5
            Program.CheckNear(0.5f, pose.Opacity, 0.001f, "通道独立：透明度应在插值中途");
        }

        private static void TestChannelsCanDifferAtSameTime()
        {
            // 六个通道各自有自己的时间轴，同一时刻状态完全不同。
            FloorTrackState state = new FloorTrackState();
            state.SetBaseline(0f, 0f, 0f, 100f, 100f, 1f);

            state.PositionX.Add(0.0, 4f, 10f, Linear);
            state.Rotation.Add(2.0, 0f, 90f, Linear);
            state.ScaleY.Add(0.0, 4f, 0f, Linear);

            FloorPose pose = state.Evaluate(2.0);
            Program.CheckNear(5f, pose.X, 0.001f, "多通道：t=2 时 X 应到一半");
            Program.CheckNear(90f, pose.Rotation, 0.001f, "多通道：t=2 时旋转刚被设置，应立刻生效");
            Program.CheckNear(50f, pose.ScaleY, 0.001f, "多通道：t=2 时 Y 缩放应到一半");
        }
    }
}
