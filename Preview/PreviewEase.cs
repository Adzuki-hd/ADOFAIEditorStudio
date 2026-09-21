using DG.Tweening;

namespace ADOFAIEditorStudio.Preview
{
    /// <summary>
    /// 缓动求值的统一入口。
    ///
    /// 求值器里凡是「按原生 tween 的方式插值」的地方都该走这里，
    /// 不要直接调 <c>DOVirtual.EasedValue</c>：那条路径会踩到一个
    /// 会让动画**终点错位**的坑。
    ///
    /// 坑在 Flash 家族（Flash / InFlash / OutFlash / InOutFlash）上。
    /// DOTween 的 <c>TweenSettingsExtensions.SetEase</c> 对这四种缓动
    /// 会把振幅截断成整数：
    ///
    ///   if (EaseManager.IsFlashEase(ease)) t.easeOvershootOrAmplitude = (int)t.easeOvershootOrAmplitude;
    ///
    /// 所以原生跑的是振幅 1，而 <c>DOVirtual.EasedValue</c> 的默认振幅是
    /// <c>DOTween.defaultEaseOvershootOrAmplitude</c>（1.70158）——**没有截断**。
    ///
    /// 这个差别在终点上是灾难性的。Flash 不是平滑曲线，它是按
    /// <c>ceil(ratio * 振幅)</c> 分段的锯齿，段数随振幅变：
    ///
    ///   振幅 1.70158：分 2 段，终点 res = 0.2984
    ///   振幅 1      ：分 1 段，终点 res = 1.0000
    ///
    /// 而且 EaseManager.Evaluate 的 Flash 分支**没有终点短路**
    /// （InExpo 之类的分支才有 <c>if (time == duration) return 1f;</c>），
    /// 所以那个 0.2984 就是它最终停下的值。
    ///
    /// 后果是一个从 1 淡到 0 的 opacity 只淡到 0.702，永远差一截。
    /// 《花束》第 64 格那条把 P1-flash 拉到全白的 color 事件、
    /// 以及第 74 格把它淡出的 opacity 事件，就因此留下了一层
    /// 七成不透明的白色蒙层——原版看不见，这里看得见。
    /// </summary>
    internal static class PreviewEase
    {
        /// <summary>
        /// 按原生 tween 的方式求缓动值。Flash 家族会先把振幅截断成整数，
        /// 与 <c>SetEase</c> 的行为一致。
        /// </summary>
        internal static float EasedValue(float origin, float target, float ratio, Ease ease)
        {
            float amplitude = DOTween.defaultEaseOvershootOrAmplitude;
            if (IsFlashEase(ease))
            {
                amplitude = (int)amplitude;
            }
            return DOVirtual.EasedValue(origin, target, ratio, ease, amplitude);
        }

        /// <summary>
        /// 对应 EaseManager.IsFlashEase——DOTween 把 Flash 家族的四个值
        /// 标记为 ease >= Flash 且 &lt;= InOutFlash。
        /// </summary>
        internal static bool IsFlashEase(Ease ease)
        {
            int value = (int)ease;
            return value >= (int)Ease.Flash && value <= (int)Ease.InOutFlash;
        }
    }
}
