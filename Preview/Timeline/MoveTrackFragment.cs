namespace ADOFAIEditorStudio.Preview.Timeline
{
    /// <summary>轨道的出现动画类型。数值顺序和谱面文件里的枚举一致。</summary>
    internal enum AnimateTrackKind
    {
        None = 0,
        Assemble = 1,
        Assemble_Far = 2,
        Extend = 3,
        Grow = 4,
        Grow_Spin = 5,
        Fade = 6,
        Drop = 7,
        Rise = 8
    }

    /// <summary>
    /// 一段轨道位移/变形。
    ///
    /// **MoveTrack 事件和「出现 / 消失动画」翻译出来的东西是同一种类型。**
    /// 这是整个重构的关键：一旦两者进同一个列表、按起始时间统一排序，
    /// 「谁后启动谁赢」就只是排序的结果，不再需要两个域之间那套
    /// 「谁该让位」的规则。
    ///
    /// 每个通道有自己的「有没有」标志。谱面里 `positionOffset: [8, null]`
    /// 只动 X，这里就只置 `HasX`；不置的通道完全不受影响。
    /// </summary>
    internal struct MoveTrackFragment
    {
        /// <summary>这段属于哪一格。</summary>
        internal int Floor;

        /// <summary>起始时间（秒）。散开段是负无穷。</summary>
        internal double Start;

        /// <summary>持续多久（秒）。&lt;= 0 表示当场跑完。</summary>
        internal float Duration;

        internal bool HasX;
        internal float X;
        internal bool HasY;
        internal float Y;

        internal bool HasRotation;
        internal float Rotation;

        internal bool HasScaleX;
        internal float ScaleX;
        internal bool HasScaleY;
        internal float ScaleY;

        internal bool HasOpacity;
        internal float Opacity;
    }
}
