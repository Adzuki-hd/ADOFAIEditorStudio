using System;
using System.Collections.Generic;

namespace ADOFAIEditorStudio.Preview.Timeline
{
    /// <summary>
    /// 把「出现 / 消失动画」翻译成一批 <see cref="MoveTrackFragment"/>。
    ///
    /// ## 为什么需要这个
    ///
    /// 现在的预览分成两个独立的求值域：一个管 MoveTrack，一个管 AnimateTrack。
    /// 两者写同一批通道（位置、缩放、透明度），于是必须有一套「谁该让位」
    /// 的规则来协调。那套规则是反复联调调出来的，没有单一判据，
    /// 改一处就会在别处破——这就是「散开之后回不来」这类 bug 反复出现的原因。
    ///
    /// 翻译器把问题换掉：出现 / 消失不再是一个独立的东西，而是**先翻译成
    /// 一批普通的位移片段**，和真实 MoveTrack 进同一张表。
    /// 表按起始时间排序，后启动的段自然覆盖先启动的段。
    /// 不需要让位规则，因为只有一张表。
    ///
    /// ## 三段时间
    ///
    /// 每种出现动画翻译成两段：
    ///
    ///   1. **散开段**：起始 -∞，持续 0。代表「格出现之前一直保持这个状态」。
    ///   2. **落回段**：起始 = 入场时刻 - 提前量 × 一拍，持续 = min(半拍, 0.5 秒)。
    ///      把姿态从散开拉回基线。
    ///
    /// 消失动画翻译成第三段，原理相同但方向相反，且跑完必须保持终态。
    ///
    /// ## 纯函数
    ///
    /// 这个类不碰 Unity、不碰游戏类型、不读场景。随机散开用按格号播种的
    /// 确定性随机数——同一个格号永远得到同一个偏移，所以同一个谱面
    /// 反复求值结果一致。
    /// </summary>
    internal sealed class AnimateTrackTranslator
    {
        /// <summary>某一格的基线姿态 + 入场时刻，供翻译使用。</summary>
        private struct FloorInfo
        {
            internal double EntryTime;
            internal float Crotchet;
            internal float X;
            internal float Y;
            internal float Rotation;
            internal float ScaleX;
            internal float ScaleY;
            internal float Opacity;
        }

        /// <summary>一个 AnimateTrack 事件：从哪一格开始换成什么动画。</summary>
        private struct EventInfo
        {
            internal int Floor;
            internal AnimateTrackKind Appear;
            internal float BeatsAhead;
            internal AnimateTrackKind Disappear;
            internal float BeatsBehind;
        }

        private FloorInfo[] floors = new FloorInfo[0];
        private readonly List<EventInfo> events = new List<EventInfo>();

        private AnimateTrackKind globalAppear;
        private float globalBeatsAhead;
        private AnimateTrackKind globalDisappear;
        private float globalBeatsBehind;

        internal void SetFloorCount(int count)
        {
            floors = new FloorInfo[Math.Max(0, count)];
        }

        internal void SetFloor(int index, double entryTime, float crotchet,
            float baselineX, float baselineY, float baselineRotation,
            float baselineScaleX, float baselineScaleY, float baselineOpacity)
        {
            if (index < 0 || index >= floors.Length)
            {
                return;
            }
            floors[index] = new FloorInfo
            {
                EntryTime = entryTime,
                Crotchet = crotchet,
                X = baselineX,
                Y = baselineY,
                Rotation = baselineRotation,
                ScaleX = baselineScaleX,
                ScaleY = baselineScaleY,
                Opacity = baselineOpacity
            };
        }

        /// <summary>
        /// 谱面 settings 里声明的全局动画。**一个事件都没有的谱也靠它**——
        /// 从 None 起步会让这类谱完全没有出现效果。
        /// </summary>
        internal void SetGlobalSettings(AnimateTrackKind appear, float beatsAhead,
            AnimateTrackKind disappear, float beatsBehind)
        {
            globalAppear = appear;
            globalBeatsAhead = beatsAhead;
            globalDisappear = disappear;
            globalBeatsBehind = beatsBehind;
        }

        internal void AddEvent(int floor, AnimateTrackKind appear, float beatsAhead,
            AnimateTrackKind disappear, float beatsBehind)
        {
            events.Add(new EventInfo
            {
                Floor = floor,
                Appear = appear,
                BeatsAhead = beatsAhead,
                Disappear = disappear,
                BeatsBehind = beatsBehind
            });
        }

        /// <summary>翻译全部格。返回的列表按起始时间升序，可直接并进 MoveTrack 的表。</summary>
        internal List<MoveTrackFragment> Translate()
        {
            List<MoveTrackFragment> result = new List<MoveTrackFragment>();
            if (floors.Length == 0)
            {
                return result;
            }

            events.Sort((a, b) => a.Floor.CompareTo(b.Floor));

            AnimateTrackKind appear = globalAppear;
            float beatsAhead = globalBeatsAhead;
            AnimateTrackKind disappear = globalDisappear;
            float beatsBehind = globalBeatsBehind;

            int cursor = 0;
            for (int floor = 0; floor < floors.Length; floor++)
            {
                // 事件按格号推进：这一格有事件就换动画，没有就沿用上一个。
                while (cursor < events.Count && events[cursor].Floor == floor)
                {
                    EventInfo info = events[cursor];
                    appear = info.Appear;
                    beatsAhead = info.BeatsAhead;
                    disappear = info.Disappear;
                    beatsBehind = info.BeatsBehind;
                    cursor++;
                }

                // 第 0 格不做出现动画：它一开始就在场上。
                if (floor > 0 && appear != AnimateTrackKind.None)
                {
                    AppendAppear(result, floor, appear, beatsAhead);
                }

                if (disappear != AnimateTrackKind.None)
                {
                    AppendDisappear(result, floor, disappear, beatsBehind);
                }
            }

            result.Sort((a, b) => a.Start.CompareTo(b.Start));
            return result;
        }

        private void AppendAppear(List<MoveTrackFragment> result, int floor,
            AnimateTrackKind kind, float beatsAhead)
        {
            FloorInfo info = floors[floor];
            float crotchet = info.Crotchet <= 0f ? 0.5f : info.Crotchet;

            // 散开段：格出现之前一直保持这个状态。
            MoveTrackFragment scatter = default(MoveTrackFragment);
            scatter.Floor = floor;
            scatter.Start = double.NegativeInfinity;
            scatter.Duration = 0f;
            ApplyScatterState(ref scatter, floor, kind, info);
            result.Add(scatter);

            // 落回段：从散开处回到基线。
            double settleStart = info.EntryTime - Math.Max(0f, beatsAhead) * crotchet;
            float settleDuration = Math.Min(crotchet * 0.5f, 0.5f);

            MoveTrackFragment settle = default(MoveTrackFragment);
            settle.Floor = floor;
            settle.Start = Math.Max(0d, settleStart);
            settle.Duration = settleDuration;
            ApplyBaselineState(ref settle, info);
            result.Add(settle);
        }

        private void AppendDisappear(List<MoveTrackFragment> result, int floor,
            AnimateTrackKind kind, float beatsBehind)
        {
            FloorInfo info = floors[floor];
            float crotchet = info.Crotchet <= 0f ? 0.5f : info.Crotchet;

            // 消失从「下一格入场」起算，再按 beatsBehind 往前推。
            double nextEntry = floor + 1 < floors.Length
                ? floors[floor + 1].EntryTime
                : info.EntryTime + crotchet;
            double start = nextEntry + beatsBehind * crotchet;

            MoveTrackFragment gone = default(MoveTrackFragment);
            gone.Floor = floor;
            gone.Start = start;
            gone.Duration = Math.Min(crotchet * 0.5f, 0.5f);
            ApplyScatterState(ref gone, floor, kind, info);
            result.Add(gone);
        }

        /// <summary>散开状态：按动画类型决定哪些通道偏离基线。</summary>
        private static void ApplyScatterState(ref MoveTrackFragment fragment, int floor,
            AnimateTrackKind kind, FloorInfo info)
        {
            switch (kind)
            {
                case AnimateTrackKind.Assemble:
                case AnimateTrackKind.Assemble_Far:
                {
                    float range = kind == AnimateTrackKind.Assemble_Far ? 8f : 4f;
                    uint seed = DeterministicSeed(floor);
                    fragment.HasX = true;
                    fragment.X = info.X + Range(ref seed, -range, range);
                    fragment.HasY = true;
                    fragment.Y = info.Y + Range(ref seed, -range, range);
                    fragment.HasRotation = true;
                    fragment.Rotation = info.Rotation + Range(ref seed, -75f, 75f);
                    break;
                }
                case AnimateTrackKind.Extend:
                case AnimateTrackKind.Grow:
                case AnimateTrackKind.Grow_Spin:
                    fragment.HasScaleX = true;
                    fragment.ScaleX = 0f;
                    fragment.HasScaleY = true;
                    fragment.ScaleY = 0f;
                    break;
                case AnimateTrackKind.Fade:
                    fragment.HasOpacity = true;
                    fragment.Opacity = 0f;
                    break;
                case AnimateTrackKind.Drop:
                case AnimateTrackKind.Rise:
                    fragment.HasY = true;
                    fragment.Y = info.Y + (kind == AnimateTrackKind.Drop ? 8f : -8f);
                    fragment.HasScaleX = true;
                    fragment.ScaleX = 0f;
                    fragment.HasScaleY = true;
                    fragment.ScaleY = 0f;
                    break;
            }
        }

        /// <summary>基线状态：所有被动过的通道都回到基线。</summary>
        private static void ApplyBaselineState(ref MoveTrackFragment fragment, FloorInfo info)
        {
            fragment.HasX = true;
            fragment.X = info.X;
            fragment.HasY = true;
            fragment.Y = info.Y;
            fragment.HasRotation = true;
            fragment.Rotation = info.Rotation;
            fragment.HasScaleX = true;
            fragment.ScaleX = info.ScaleX;
            fragment.HasScaleY = true;
            fragment.ScaleY = info.ScaleY;
            fragment.HasOpacity = true;
            fragment.Opacity = info.Opacity;
        }

        /// <summary>
        /// 按格号播种的确定性随机数（xorshift32）。
        ///
        /// 用自己的一份而不是 UnityEngine.Random：那个是全局状态，
        /// 和游戏其它地方共用，调用顺序一变结果就变。这里同一个格号
        /// 永远得到同一串数，谱面反复求值结果一致——这是「拖动到任意
        /// 位置画面相同」的必要条件。
        /// </summary>
        private static uint DeterministicSeed(int floor)
        {
            uint seed = (uint)floor * 2654435761u + 1013904223u;
            return seed == 0u ? 1u : seed;
        }

        private static float Range(ref uint state, float min, float max)
        {
            // xorshift32
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            float unit = (state & 0xFFFFFF) / 16777215f;
            return min + (max - min) * unit;
        }
    }
}
