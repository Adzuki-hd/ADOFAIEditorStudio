using System;
using System.Collections.Generic;

namespace ADOFAIEditorStudio.Preview
{
    /// <summary>
    /// 时间轴关键帧的稳定排序。
    ///
    /// List.Sort 是不稳定的内省排序，所以两个起始格相同、
    /// 因而共享同一个起始时间的事件，出来的顺序不定。这一点要紧，
    /// 因为谱面自身的顺序是有意义的：两个事件共享起始时间时，
    /// 文件里靠后的那个才是游戏最后应用的，而每个事件都会杀掉前一个
    /// tween，所以是文件顺序决定谁的值胜出。排序时不加平局判据，
    /// 会让一个 zoom-out 事件盖过它前面的 zoom-in。
    ///
    /// 这里以输入顺序为准，因为 GetEvents 返回的就是谱面顺序，
    /// 对相等的 key 保留原序即可复现游戏的行为。
    /// </summary>
    internal static class PreviewStableSort
    {
        internal static void Sort<T>(List<T> list, Comparison<T> comparison)
        {
            if (list == null || list.Count < 2 || comparison == null)
            {
                return;
            }

            T[] source = list.ToArray();
            int[] order = new int[source.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, delegate (int left, int right)
            {
                int result = comparison(source[left], source[right]);
                // 平局时回落到原始位置，这正是排序稳定的原因，
                // 也保住了谱面自身的顺序。
                return result != 0 ? result : left.CompareTo(right);
            });

            for (int i = 0; i < list.Count; i++)
            {
                list[i] = source[order[i]];
            }
        }
    }
}