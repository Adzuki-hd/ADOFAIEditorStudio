namespace ADOFAIEditorStudio.Preview
{
    internal interface IPreviewTimedKeyframe
    {
        /// <summary>
        /// 事件在绝对时间轴上的开始时刻，单位秒。
        ///
        /// **必须是 double，不能是 float。** 原版的
        /// <c>ffxPlusBase.startTime</c> 就是 double，而它不只是"精度更高"——
        /// 它是有语义的：作者会用极小的 angleOffset（比如 -0.001）来指定
        /// 同一格上两个事件的先后。换算成时间是
        /// <c>0.001 / 180 * 60 / bpm</c>（85bpm 下约 3.9e-6 秒），
        /// 而 float 在 400 秒量级上的最小刻度（ULP）是 3.1e-5 —— 差值被
        /// 整个吞掉，两个事件时间变得完全相等，排序退化，顺序由
        /// 排序算法决定而不是作者决定。
        ///
        /// 《目的地》289 格就是这么炸的：作者用 -0.001 把"瞬间拉高泛光"
        /// 排在"渐隐"之前，float 吞掉差值后顺序反转，求值取到瞬间事件，
        /// 于是整格停在 200% 泛光上。
        /// </summary>
        double Start { get; }
    }
}
