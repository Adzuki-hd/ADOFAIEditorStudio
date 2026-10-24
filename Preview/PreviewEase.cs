using System.Collections.Generic;
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
        /// 把一条格通道从游戏手里接过来：杀掉它那条 tween，
        /// 让它不再按自己的时间线往回推我们写好的值。
        ///
        /// 为什么需要：游戏的 MoveTrack / RecolorTrack 等特效都是用
        /// DOTween 驱动格属性的，而且 tween 一旦建好就会一直跑到它
        /// 自己的时间线结束。求值器是按预览时间重新求值的，
        /// 两者会互相覆盖——表现就是「事件看起来完全没生效」。
        ///
        /// 已确认游戏会在格上驱动这些通道（见 ffxMoveFloorPlus /
        /// ffxRecolorFloorPlus / ffxFloorAppearPlus / DisappearPlus）：
        ///   Position, PositionX, PositionY, Rotation,
        ///   Scale, ScaleX, ScaleY, Opacity, Color, Glow
        /// 少接一个，那一个通道就会被游戏改回去。
        ///
        /// 查不到时几乎不花钱，杀过即移出字典，不会反复查。
        /// </summary>
        internal static void TakeOverChannel(scrFloor floor, TweenType type)
        {
            if (floor == null || floor.moveTweens == null)
            {
                return;
            }

            Tween tween;
            if (!floor.moveTweens.TryGetValue(type, out tween))
            {
                return;
            }

            if (tween != null && tween.active)
            {
                tween.Kill(false);
            }
            floor.moveTweens.Remove(type);
        }

        /// <summary>辉度通道。</summary>
        internal static void TakeOverGlow(scrFloor floor)
        {
            TakeOverChannel(floor, TweenType.Glow);
        }
        /// <summary>
        /// 按原生 tween 的方式求缓动值。Flash 家族会先把振幅截断成整数，
        /// 与 <c>SetEase</c> 的行为一致。
        /// </summary>
        /// <summary>
        /// 缓动查表的分辨率。1/512 的量化误差在动画上不可见，
        /// 但能把每帧几万次 `DOVirtual.EasedValue`（内部是 pow/sin/exp）
        /// 压成一次数组索引。
        /// </summary>
        private const int EaseLutSize = 512;

        /// <summary>每种 Ease 一张 0..1 的归一化表，按需生成，之后只读。</summary>
        private static readonly Dictionary<Ease, float[]> EaseLut
            = new Dictionary<Ease, float[]>();

        /// <summary>
        /// 按原生 tween 的方式求缓动值（带查表缓存）。
        ///
        /// **为什么必须缓存**：装饰物域每帧对每件装饰物的每个已开始事件都求一次
        /// 缓动，本谱（星相运轨）105 件装饰物 × 数百事件就是几万次，
        /// 而 `DOVirtual.EasedValue` 内部走 pow / sin / exp，单次约 1µs ——
        /// 正好是实测 DecorationCore 单帧 15~70ms 的量级。
        /// 缓动只依赖 (ease, ratio)，把 ratio 量化成 1/512 后查表即可。
        /// </summary>
        internal static float EasedValue(float origin, float target, float ratio, Ease ease)
        {
            float normalized = SampleEase(ease, ratio);
            return origin + (target - origin) * normalized;
        }

        private static float SampleEase(Ease ease, float ratio)
        {
            float clamped = ratio < 0f ? 0f : (ratio > 1f ? 1f : ratio);
            int index = (int)(clamped * EaseLutSize + 0.5f);
            if (index < 0)
            {
                index = 0;
            }
            else if (index > EaseLutSize)
            {
                index = EaseLutSize;
            }

            float[] table;
            if (!EaseLut.TryGetValue(ease, out table))
            {
                table = new float[EaseLutSize + 1];
                for (int i = 0; i < table.Length; i++)
                {
                    table[i] = float.NaN;
                }
                EaseLut[ease] = table;
            }

            float cached = table[index];
            // 0 是合法值（Linear / In 的起点），所以用"是否算过"另存一位：
            // 把未计算标记成 NaN，NaN != NaN 即可判断。
            if (cached.Equals(float.NaN) || float.IsNaN(cached))
            {
                float amplitude = DOTween.defaultEaseOvershootOrAmplitude;
                if (IsFlashEase(ease))
                {
                    amplitude = (int)amplitude;
                }
                cached = DOVirtual.EasedValue(0f, 1f, (float)index / EaseLutSize, ease, amplitude);
                table[index] = cached;
            }
            return cached;
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
