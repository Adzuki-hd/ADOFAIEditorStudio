using System;

namespace ADOFAIEditorStudio.Preview.Timeline
{
    /// <summary>一格在某一时刻的姿态。单位和谱面一致（缩放是百分点，不透明度 0~1）。</summary>
    internal struct FloorPose
    {
        internal float X;
        internal float Y;
        internal float Rotation;
        internal float ScaleX;
        internal float ScaleY;
        internal float Opacity;
    }

    /// <summary>
    /// 一格的六个通道各自一条时间轴。
    ///
    /// **为什么要拆成六个。** 谱面里 `positionOffset: [8, null]` 这种写法
    /// 意思是「只改 X」，Y 轴要留给别的动画。把二维向量当一个整体处理，
    /// 就得在每个消费的地方单独判 null、单独决定「这个轴别动」——
    /// 那种散落各处的特判正是现在这一批 bug 的来源。
    ///
    /// 拆开之后，每个轴的行为和标量完全一样，`null` 在解析阶段就变成
    /// 「这个通道不加事件」，后面不需要再判。
    ///
    /// **这个类是纯函数。** 给定时间就出姿态，不读场景、不记历史。
    /// 拖动到任意位置、来回拖、跳着拖，同一个 `t` 永远同一个结果。
    /// </summary>
    internal sealed class FloorTrackState
    {
        internal readonly ChannelTimeline PositionX = new ChannelTimeline();
        internal readonly ChannelTimeline PositionY = new ChannelTimeline();
        internal readonly ChannelTimeline Rotation = new ChannelTimeline();
        internal readonly ChannelTimeline ScaleX = new ChannelTimeline();
        internal readonly ChannelTimeline ScaleY = new ChannelTimeline();
        internal readonly ChannelTimeline Opacity = new ChannelTimeline();

        private float baselineX;
        private float baselineY;
        private float baselineRotation;
        private float baselineScaleX = 100f;
        private float baselineScaleY = 100f;
        private float baselineOpacity = 1f;

        /// <summary>
        /// 设定基线：没有任何事件影响时这一格该有的样子。
        ///
        /// 基线来自游戏的烘焙结果（轨道本来摆在哪、原本多大），
        /// 所以在 Analyze 时抓一次，之后只读。
        /// </summary>
        internal void SetBaseline(float x, float y, float rotation,
            float scaleX, float scaleY, float opacity)
        {
            baselineX = x;
            baselineY = y;
            baselineRotation = rotation;
            baselineScaleX = scaleX;
            baselineScaleY = scaleY;
            baselineOpacity = opacity;
        }

        /// <summary>所有通道排序。加完事件后调一次。</summary>
        internal void Sort()
        {
            PositionX.Sort();
            PositionY.Sort();
            Rotation.Sort();
            ScaleX.Sort();
            ScaleY.Sort();
            Opacity.Sort();
        }

        /// <summary>这一格在 `t` 时刻的姿态。</summary>
        internal FloorPose Evaluate(double t)
        {
            FloorPose pose = default(FloorPose);
            pose.X = EvaluateChannel(PositionX, t, baselineX);
            pose.Y = EvaluateChannel(PositionY, t, baselineY);
            pose.Rotation = EvaluateChannel(Rotation, t, baselineRotation);
            pose.ScaleX = EvaluateChannel(ScaleX, t, baselineScaleX);
            pose.ScaleY = EvaluateChannel(ScaleY, t, baselineScaleY);
            pose.Opacity = EvaluateChannel(Opacity, t, baselineOpacity);
            return pose;
        }

        /// <summary>
        /// 求单个通道。没有事件影响时用基线。
        ///
        /// 这里用 `TryEvaluate` 的返回值而不是「值为零」，是因为零是
        /// 合法值（透明度 0、缩放 0 都常见），不能拿它当「没有事件」。
        /// </summary>
        private static float EvaluateChannel(ChannelTimeline channel, double t, float baseline)
        {
            float value;
            channel.TryEvaluate(t, baseline, out value);
            return value;
        }
    }
}
