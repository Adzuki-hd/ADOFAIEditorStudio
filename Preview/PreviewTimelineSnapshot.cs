using System;
using System.Collections;
using System.Collections.Generic;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview
{
    internal sealed class PreviewFloorSnapshot
    {
        internal PreviewFloorSnapshot(scrFloor source, int index)
        {
            Index = index;
            EntryTime = source == null ? 0d : source.entryTime;
            EntryBeat = source == null ? 0d : source.entryBeat;
            Speed = source == null || source.speed <= 0f ? 1f : source.speed;
            IsCounterClockwise = source != null && source.isCCW;
            MidSpin = source != null && source.midSpin;
            FreeRoam = source != null && source.freeroam;
            ExtraBeats = source == null ? 0f : source.extraBeats;
            // 由 Pause 事件的 angleCorrectionDir 设置；驱动原生角度修正
            // tween，在暂停格上把行星的入射角对齐回来。
            AngleCorrectionType = source == null ? -1f : source.angleCorrectionType;
            HoldLength = source == null ? -1 : source.holdLength;
            PlanetCount = source == null ? 2 : Mathf.Max(2, source.numPlanets);
            RadiusScale = source == null ? 1f : source.radiusScale;
            PlanetEase = source == null ? Ease.Linear : source.planetEase;
            PlanetEaseParts = source == null ? 1 : source.planetEaseParts;
            PlanetEasePartBehavior = source == null
                ? EasePartBehavior.Mirror
                : source.planetEasePartBehavior;
            EntryAngle = source == null ? 0d : source.entryangle;
            ExitAngle = source == null ? 0d : source.exitangle;
            AngleLength = source == null ? 0d : source.angleLength;
            NextEntryTime = source == null || source.nextfloor == null
                ? (source == null ? 0d : source.entryTime)
                : source.nextfloor.entryTime;
            StickToFloor = source != null && source.stickToFloor;

            // startPos 只是作者摆的布局位置。PositionTrack 事件
            // 把位移累加进 offsetPos，原生代码把格放在
            // "startPos + offsetPos" 处（scnGame 设置 offsetPos；scnEditor 写
            // transform.position = startPos + offsetPos）。相机 Tile 模式锚定在
            // floor.transform.position 上，所以只读 startPos 会让锚点留在
            // 没移动过的布局位置，视图就高高地停在一条已经被
            // 推下去的轨道上方。
            StartPosition = source == null
                ? Vector3.zero
                : source.startPos + source.offsetPos;
            StartRotation = source == null
                ? Vector3.zero
                : source.startRot.WithZ(source.startRot.z + source.rotationOffset);
            StartScale = source == null ? Vector3.one : source.startScale;
            BaseOpacity = source == null ? 1f : source.opacityVal;
            BallPosition = source == null
                ? Vector3.zero
                : (StickToFloor ? source.startPos + source.offsetPos : source.startPos);
        }

        internal int Index { get; private set; }
        internal double EntryTime { get; private set; }
        internal double EntryBeat { get; private set; }
        internal float Speed { get; private set; }
        internal bool IsCounterClockwise { get; private set; }
        internal bool MidSpin { get; private set; }
        internal bool FreeRoam { get; private set; }
        internal float ExtraBeats { get; private set; }
        internal float AngleCorrectionType { get; private set; }

        internal int HoldLength { get; private set; }
        internal int PlanetCount { get; private set; }
        internal float RadiusScale { get; private set; }
        internal Ease PlanetEase { get; private set; }
        internal int PlanetEaseParts { get; private set; }
        internal EasePartBehavior PlanetEasePartBehavior { get; private set; }
        internal double EntryAngle { get; private set; }
        internal double ExitAngle { get; private set; }
        internal double AngleLength { get; private set; }

        /// <summary>
        /// 下一格的进入时间。这里推导的「本格走过的角度」是
        /// floorBpm * 3 * (nextEntryTime - entryTime)，而不是取自
        /// scrFloor.angleLength；两者在长按/多拍格上不同，而这个
        /// 差异会改变 angleOffset &gt;= travel-angle 修正在不在适用范围内，
        /// 进而挪动事件的起始时间。
        /// </summary>
        internal double NextEntryTime { get; private set; }
        internal bool StickToFloor { get; private set; }
        internal Vector3 StartPosition { get; private set; }
        internal Vector3 StartRotation { get; private set; }
        internal Vector3 StartScale { get; private set; }
        internal float BaseOpacity { get; private set; }

        /// <summary>
        /// 球在这一格上真正的落点，也就是行星绕着的那个中心。
        ///
        /// 由 stickToFloor 决定，原生 scrPlanet 是二选一：
        ///
        ///   if (stickToFloor) pos = floor.transform.position;  // startPos + offsetPos
        ///   else              pos = floor.startPos;            // 作者摆的布局位
        ///
        /// 为什么不能直接用 StartPosition：那个值只包含**位移**语义
        /// （作者把轨道推下去之后，相机也要跟着下去），对「不粘」的格
        /// 是错的——轨道被推走了，球却留在布局位上，相机再锚到位移后的
        /// 位置就会偏出去。第 7 格 offset=(1,-1) 时那个偏差正好是
        /// 往右偏一格的量。
        /// </summary>
        internal Vector3 BallPosition { get; private set; }
    }

    internal sealed class PreviewEventSnapshot
    {
        private readonly Dictionary<string, object> data;
        private readonly Dictionary<string, bool> disabled;

        internal PreviewEventSnapshot(LevelEvent source, int sequence)
        {
            Floor = source == null ? -1 : source.floor;
            EventType = source == null ? LevelEventType.None : source.eventType;
            Sequence = sequence;
            Active = source != null && source.active;
            Visible = source != null && source.visible;
            data = new Dictionary<string, object>(StringComparer.Ordinal);
            disabled = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (source != null && source.GetData() != null)
            {
                foreach (KeyValuePair<string, object> pair in source.GetData())
                {
                    data[pair.Key] = CloneValue(pair.Value);
                }
            }
            if (source != null && source.disabled != null)
            {
                foreach (KeyValuePair<string, bool> pair in source.disabled)
                {
                    disabled[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>
        /// RepeatEvents 把一个作者写的事件展开成多个运行时实例，落在
        /// 后面的格或拍上。这些克隆共享那份（已深拷贝、从不修改的）
        /// 数据字典，只有格和起始时间不同，
        /// 与原生 ApplyEvent 循环重建它们的方式一致。
        /// </summary>
        private PreviewEventSnapshot(PreviewEventSnapshot source, int floor, int sequence,
            double startTime, float duration)
        {
            Floor = floor;
            EventType = source.EventType;
            Sequence = sequence;
            Active = source.Active;
            Visible = source.Visible;
            data = source.data;
            disabled = source.disabled;
            StartTime = startTime;
            Duration = duration;
            ExpandedFrom = source;
        }

        internal PreviewEventSnapshot CreateExpanded(int floor, int sequence, double startTime)
        {
            return new PreviewEventSnapshot(this, floor, sequence, startTime, Duration);
        }

        internal PreviewEventSnapshot ExpandedFrom { get; private set; }

        internal int Floor { get; private set; }
        internal LevelEventType EventType { get; private set; }
        internal int Sequence { get; private set; }
        internal double StartTime { get; set; }
        internal float Duration { get; set; }
        internal bool Active { get; private set; }
        internal bool Visible { get; private set; }

        internal IReadOnlyDictionary<string, object> GetData()
        {
            return data;
        }

        internal bool ContainsKey(string key)
        {
            return data.ContainsKey(key);
        }

        /// <summary>
        /// 只读诊断：把键的原始值原样交出来，不做任何类型转换。
        ///
        /// 用于确认数组里的 null 在运行时到底是什么 —— 是 null、
        /// 是 double.NaN，还是已经被读成了 0。相机那些「NaN 轴表示保持
        /// 不动」的判据全靠这个区分。
        /// </summary>
        /// <summary>
        /// 读一个二维向量，并且**保住数组里 null 的语义**。
        ///
        /// 原生的 RDUtils.GetRandomVector2 对数组里的 null 返回 NaN，
        /// 而 NaN 是「这个轴不动」的信号——相机那道逐轴的门就是靠它
        /// 决定要不要重启一根轴的 tween。
        ///
        /// 通用 GetValue&lt;Vector2&gt; 走的是 Convert.ChangeType，
        /// 对 List&lt;object&gt; 会抛异常后回落到 fallback；实测
        /// `position: [-4, null]` 读出来 Y 是 0 而不是 NaN，
        /// 于是「null 表示不动」这条规则从来没生效过——
        /// 相机每遇到一个只写单轴的位置事件就重启另一根轴，
        /// 把它正在跑的 tween 连同目标一起丢掉。
        /// </summary>
        internal Vector2 GetVector2WithNaN(string key, Vector2 fallback)
        {
            object raw = GetRawValue(key);
            IList<object> list = raw as IList<object>;
            if (list == null || list.Count < 2)
            {
                return fallback;
            }

            return new Vector2(AxisOrNaN(list[0]), AxisOrNaN(list[1]));
        }

        private static float AxisOrNaN(object value)
        {
            if (value == null)
            {
                return float.NaN;
            }
            try
            {
                return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return float.NaN;
            }
        }

        internal object GetRawValue(string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? value : null;
        }

        internal bool IsPropertyEnabled(string key)
        {
            if (!Active || !data.ContainsKey(key))
            {
                return false;
            }

            bool isDisabled;
            return !disabled.TryGetValue(key, out isDisabled) || !isDisabled;
        }

        /// <summary>
        /// 这个属性到底存不存在，而不管它有没有带值。
        /// 原生效果用 Decode 的 GetRandomFloat 解码 key，缺 key 时返回 0，
        /// 并且只按 disabled[key] 判断——所以一个存在但为 null 的 key
        /// （rotation、position 或 zoom 上的 "None" 选项）照样会跑，用零值。
        /// 把 null 值当成「不存在」、改成保持上一个值，会让相机
        /// 旋转停在最后一个事件设的值上。在 M3 谱面上实测：270、274 和
        /// 277 格完全省略了 rotation，原生相机在每一格都转回 0，
        /// 而这个域则一直停在 -6.377。
        /// </summary>
        internal bool HasPropertyKey(string key)
        {
            return Active && !IsDisabled(key);
        }

        internal bool IsDisabled(string key)
        {
            bool isDisabled;
            return disabled.TryGetValue(key, out isDisabled) && isDisabled;
        }

        internal T GetValue<T>(string key, T fallback)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            if (value is T)
            {
                return (T)value;
            }

            try
            {
                Type targetType = typeof(T);
                if (targetType.IsEnum)
                {
                    if (value is string)
                    {
                        return (T)Enum.Parse(targetType, (string)value, true);
                    }
                    return (T)Enum.ToObject(targetType, Convert.ToInt32(value));
                }
                if (targetType == typeof(string))
                {
                    return (T)(object)value.ToString();
                }
                return (T)Convert.ChangeType(value, targetType);
            }
            catch
            {
                return fallback;
            }
        }

        internal string GetString(string key)
        {
            return GetValue(key, string.Empty);
        }

        internal float GetFloat(string key)
        {
            return GetValue(key, 0f);
        }

        internal int GetInt(string key)
        {
            return GetValue(key, 0);
        }

        internal bool GetBool(string key)
        {
            return GetValue(key, false);
        }

        internal Tuple<int, TileRelativeTo> GetTile(string key)
        {
            return GetValue<Tuple<int, TileRelativeTo>>(key, null);
        }

        internal Color GetColor(string key)
        {
            return GetColor(key, Color.white);
        }

        internal Color GetColor(string key, Color fallback)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            if (value is Color)
            {
                return (Color)value;
            }

            Color color;
            string text = value as string;
            if (!string.IsNullOrEmpty(text)
                && ColorUtility.TryParseHtmlString(text.StartsWith("#", StringComparison.Ordinal)
                    ? text
                    : "#" + text, out color))
            {
                return color;
            }
            return fallback;
        }

        private static object CloneValue(object value)
        {
            if (value == null || value is string || value.GetType().IsValueType)
            {
                return value;
            }

            Tuple<int, TileRelativeTo> tile = value as Tuple<int, TileRelativeTo>;
            if (tile != null)
            {
                return new Tuple<int, TileRelativeTo>(tile.Item1, tile.Item2);
            }
            Tuple<float, float> floatPair = value as Tuple<float, float>;
            if (floatPair != null)
            {
                return new Tuple<float, float>(floatPair.Item1, floatPair.Item2);
            }
            Tuple<Vector2, Vector2> vectorPair = value as Tuple<Vector2, Vector2>;
            if (vectorPair != null)
            {
                return new Tuple<Vector2, Vector2>(vectorPair.Item1, vectorPair.Item2);
            }

            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key != null)
                    {
                        copy[entry.Key.ToString()] = CloneValue(entry.Value);
                    }
                }
                return copy;
            }

            IList list = value as IList;
            if (list != null)
            {
                List<object> copy = new List<object>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    copy.Add(CloneValue(list[i]));
                }
                return copy;
            }

            return value.ToString();
        }
    }
}
