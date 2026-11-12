using System;
using System.Collections.Generic;

namespace ADOFAIEditorStudio.Preview.Timeline
{
    /// <summary>
    /// 单个通道的事件时间轴：一堆 (起始时间, 时长, 目标值, 缓动)，
    /// 给一个绝对时间，算出那一刻该是哪个值。
    ///
    /// **这个类是纯函数。** 它不读场景、不读别的域、不记上一帧算过什么。
    /// 同一个 `t` 调几次都是同一个结果——这是「拖动到任意位置画面都对」
    /// 的前提，也是为什么它被单独拆出来、能脱离 Unity 跑单元测试。
    ///
    /// 不依赖 Unity 和 DOTween：缓动走 `Func&lt;float, float&gt;`，
    /// 由调用方传进来。这样测试工程不需要引用游戏程序集。
    ///
    /// ## 求值规则
    ///
    /// 事件按起始时间升序。给定 `t`：
    ///
    ///   1. 二分找最后一个 `起始 &lt;= t` 的事件，记作 `cur`；
    ///   2. 没有这样的事件 → 返回 false，值用 fallback；
    ///   3. `cur` 的起始是负无穷（散开段）→ 直接返回它的目标值，不插值；
    ///   4. 时长 &lt;= 0，或者 `t` 已过 `cur.起始 + cur.时长` → 返回 `cur` 的目标值；
    ///   5. 否则 → 从起点插值到 `cur` 的目标值。
    ///
    /// **第 4 条是关键。** 一次性的补间跑完停在终点，不会自己弹回去；
    /// 求值器每帧重算，所以「已经结束」也必须继续按终值写。
    ///
    /// **时长 &lt;= 0 当作「立刻跑完」**，不是「永久插值」。谱面里
    /// `opacity: 0, duration: 0` 这种写法就是用来越过一切、当场生效的。
    /// </summary>
    internal sealed class ChannelTimeline
    {
        private struct Entry
        {
            internal double Start;
            internal float Duration;
            internal float Value;
            /// <summary>把 0..1 的进度映射成 0..1 的曲线值。null 等于线性。</summary>
            internal Func<float, float> Ease;
        }

        private readonly List<Entry> entries = new List<Entry>();

        /// <summary>事件数量。诊断用。</summary>
        internal int Count
        {
            get { return entries.Count; }
        }

        /// <summary>加入一个事件。加完要调一次 <see cref="Sort"/>。</summary>
        internal void Add(double start, float duration, float value, Func<float, float> ease)
        {
            entries.Add(new Entry
            {
                Start = start,
                Duration = duration,
                Value = value,
                Ease = ease
            });
        }

        /// <summary>
        /// 按起始时间稳定排序。
        ///
        /// 稳定是必要的：同一时刻有多条事件时，后加的那条应当赢
        /// （谱面里同一格上多条事件按书写顺序生效）。`List.Sort` 不保证
        /// 稳定，所以这里手写插入排序——每个通道的事件是个位数量级，
        /// 不值得为它引入额外的排序设施。
        /// </summary>
        internal void Sort()
        {
            for (int i = 1; i < entries.Count; i++)
            {
                Entry current = entries[i];
                int j = i - 1;
                while (j >= 0 && entries[j].Start > current.Start)
                {
                    entries[j + 1] = entries[j];
                    j--;
                }
                entries[j + 1] = current;
            }
        }

        /// <summary>
        /// 求 `t` 时刻的值。没有任何事件影响时返回 false，值用 `fallback`。
        /// </summary>
        internal bool TryEvaluate(double t, float fallback, out float value)
        {
            value = fallback;
            if (entries.Count == 0)
            {
                return false;
            }

            int index = FindLastStarted(t);
            if (index < 0)
            {
                return false;
            }

            Entry current = entries[index];

            // 散开段：起始是负无穷，代表「在格出现之前一直保持这个状态」。
            // 它的目标值就是绝对位置，不该再往前插值。
            if (double.IsNegativeInfinity(current.Start))
            {
                value = current.Value;
                return true;
            }

            float duration = current.Duration;
            if (duration <= 0f)
            {
                value = current.Value;
                return true;
            }

            double elapsed = t - current.Start;
            if (elapsed >= duration)
            {
                value = current.Value;
                return true;
            }

            float origin = OriginValue(index, fallback);
            if (elapsed <= 0d)
            {
                value = origin;
                return true;
            }

            float ratio = (float)(elapsed / duration);
            Func<float, float> ease = current.Ease;
            float eased = ease == null ? ratio : ease(ratio);
            value = origin + (current.Value - origin) * eased;
            return true;
        }

        /// <summary>
        /// 二分找最后一个 `起始 &lt;= t` 的下标。没有则返回 -1。
        /// </summary>
        private int FindLastStarted(double t)
        {
            int low = 0;
            int high = entries.Count - 1;
            int result = -1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (entries[mid].Start <= t)
                {
                    result = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return result;
        }

        /// <summary>
        /// 第 `index` 条事件的插值起点：它之前最近一条**已经跑完**的事件的终值，
        /// 没有就用 `fallback`。
        ///
        /// 往回找最近一条已结束的，而不是直接取 `index - 1` 的值——因为
        /// `index - 1` 可能还和当前这条重叠着（两条同时进行的事件），
        /// 那时该接的是更早那条跑完留下的值。
        /// </summary>
        private float OriginValue(int index, float fallback)
        {
            Entry current = entries[index];
            for (int i = index - 1; i >= 0; i--)
            {
                Entry previous = entries[i];
                if (double.IsNegativeInfinity(previous.Start))
                {
                    return previous.Value;
                }
                if ((float)current.Start >= previous.Start + previous.Duration)
                {
                    return previous.Value;
                }
            }
            return fallback;
        }
    }
}
