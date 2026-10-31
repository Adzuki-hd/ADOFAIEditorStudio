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

        /// <summary>
        /// `DG.Tweening.Ease` 一共多少个值：Unset=0 … INTERNAL_Custom=37，共 38 个。
        /// 值域固定，所以表可以按 `(int)ease` 直接下标访问，不需要字典。
        /// </summary>
        private const int EaseLutSlots = 38;

        /// <summary>
        /// 每种 Ease 一张 0..1 的归一化表，**下标就是 `(int)ease`**；
        /// 某张表为 null 表示还没建过，首次访问时一次填满，之后只读。
        ///
        /// 为什么不用 Dictionary：一帧要采样 1 万–5 万次（见 `EasedValue` 的说明），
        /// 每次一次枚举键哈希 + 桶探测纯属白烧；Ease 是固定枚举，
        /// 天生适合当数组下标。
        /// </summary>
        private static readonly float[][] EaseLut = new float[EaseLutSlots][];

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

            // **Linear 不查表。**
            //
            // 它本身就是 y = x，而查表会把 ratio 量化到 1/EaseLutSize 的
            // 最近一格 —— 等于把一条直线切成 512 级台阶。短事件（几拍到
            // 几十拍）一格只差几毫秒，看不出来；但谱面里那种
            // `duration: 1000` 拍、铺满整个尾声的缓慢移动，每一级台阶要
            // 等 0.2 秒才跳一次，用户看到的就是「一卡一卡类似步进」。
            //
            // 实测对账（2026-10-31，谱面《杪夏》2285 格）：
            //   · 事件 `fg1`：duration = 1000 拍、ease = Linear、
            //     positionOffset.y = 444。
            //   · `TileSize` = 1.5，所以总位移 = 444 × 1.5 = 666。
            //   · 666 / 512 = 1.301 —— 日志里每一级台阶的高度正是 1.301，
            //     而且每 34 帧才跳一次，帧时间列却是每帧连续推进的。
            // 三个数逐位吻合，根因没有别的解释。
            //
            // `Ease.Unset` 一起走这条路：DOTween 里它就是「不缓动」，
            // `DOVirtual.EasedValue` 对它返回的同样是线性值。
            if (ease == Ease.Linear || ease == Ease.Unset)
            {
                return clamped;
            }

            float scaled = clamped * EaseLutSize;
            int easeIndex = (int)ease;
            // **越界保护不能省。** 枚举值可能来自谱面数据里的整数强转，
            // 真塞进一个 38 及以上的值，下标访问会当场抛 IndexOutOfRange——
            // 而这里正是每帧几万次的热点路径，崩一次整帧就没了。
            // 越界时退回单点现算，行为和旧字典实现一致（那个会给任意值建表）。
            if (easeIndex < 0 || easeIndex >= EaseLutSlots)
            {
                return ComputeEaseValue(ease, (int)(scaled + 0.5f));
            }

            float[] table = EaseLut[easeIndex];
            if (table == null)
            {
                // **整表一次填满。** 原来是用 NaN 当"这一格还没算过"的哨兵、
                // 逐格 lazy 填，于是每次采样都得多做两条 NaN 分支
                // （0 是合法值，没法用 0 表示没算过）。整张表只有
                // EaseLutSize+1 = 513 个值、每个 Ease 最多填一次，
                // 预计算的 513 次比热点路径上的两条分支便宜得多。
                table = BuildEaseTable(ease);
                EaseLut[easeIndex] = table;
            }

            // **Flash 家族保持最近邻。**
            //
            // 它的曲线本身就是按 `ceil(ratio * 振幅)` 分段的锯齿（见类注释），
            // 台阶是原生行为。插值会把锯齿抹成斜坡，等于改掉原版的样子。
            if (IsFlashEase(ease))
            {
                int flashIndex = (int)(scaled + 0.5f);
                if (flashIndex < 0)
                {
                    flashIndex = 0;
                }
                else if (flashIndex > EaseLutSize)
                {
                    flashIndex = EaseLutSize;
                }
                return table[flashIndex];
            }

            // **其余缓动在相邻采样点之间线性插值。**
            //
            // 只取最近邻的话，任何缓动碰上上面那种超长事件都会变成
            // 512 级台阶。插值多两次浮点运算，换来连续曲线，而且比最近邻
            // 更接近原生 `DOVirtual.EasedValue` 的精确值。
            int lower = (int)scaled;
            if (lower < 0)
            {
                lower = 0;
            }
            else if (lower > EaseLutSize - 1)
            {
                lower = EaseLutSize - 1;
            }
            float from = table[lower];
            return from + (table[lower + 1] - from) * (scaled - lower);
        }

        /// <summary>
        /// 把一张 0..1 的归一化表一次填满（`EaseLutSize + 1` 个采样点）。
        /// </summary>
        private static float[] BuildEaseTable(Ease ease)
        {
            float[] table = new float[EaseLutSize + 1];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = ComputeEaseValue(ease, i);
            }
            return table;
        }

        /// <summary>
        /// 单点现算一个采样值。**Flash 家族的振幅截断在这里做**：
        /// 与 `TweenSettingsExtensions.SetEase` 一致，先把振幅截成整数，
        /// 否则终点会错位（原因见类注释）。填表和越界回退共用这一份，
        /// 免得两条路的语义哪天走散。
        /// </summary>
        private static float ComputeEaseValue(Ease ease, int index)
        {
            float amplitude = DOTween.defaultEaseOvershootOrAmplitude;
            if (IsFlashEase(ease))
            {
                amplitude = (int)amplitude;
            }
            return DOVirtual.EasedValue(0f, 1f, (float)index / EaseLutSize, ease, amplitude);
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
