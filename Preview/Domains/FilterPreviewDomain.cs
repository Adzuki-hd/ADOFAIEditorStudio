using System;
using System.Collections.Generic;
using System.Reflection;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 基于反射的 SetFilter 预览。滤镜组件本身仍然是
    /// 游戏自己的 CameraFilterPack 行为；之所以用反射，只是因为
    /// 那些类位于游戏的第一趟程序集里，而且它们的属性
    /// 名逐滤镜不同。
    /// </summary>
    internal sealed class FilterPreviewDomain : IPreviewDomain
    {
        private sealed class FilterEvent
        {
            internal double Start;
            internal float Duration;
            internal Ease Ease;
            internal bool Enabled;
            internal bool DisableOthers;
            internal float Intensity;
        }

        private readonly Dictionary<Filter, List<FilterEvent>> events =
            new Dictionary<Filter, List<FilterEvent>>();
        private readonly Dictionary<Filter, float> defaults = new Dictionary<Filter, float>();
        private Dictionary<Filter, MonoBehaviour> components;

        /// <summary>
        /// 每个要求禁用其他滤镜的时刻，按时间排序。
        /// 原生行为是触发那一刻的一次状态变更，所以求某一帧时
        /// 要的是最近一次这样的触发，而不只是
        /// 「恰好处于活跃的那个事件」。
        /// </summary>
        private readonly List<DisableTrigger> disableTriggers = new List<DisableTrigger>();

        private struct DisableTrigger
        {
            internal float Time;
            internal Filter Filter;
        }

        public string Name
        {
            get { return "Filter"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            events.Clear();
            defaults.Clear();
            components = scrVfxPlus.instance == null ? null : scrVfxPlus.instance.filterToComp;
            if (scrVfxPlus.instance != null && scrVfxPlus.instance.filterDefaultValues != null)
            {
                foreach (KeyValuePair<Filter, float> pair in scrVfxPlus.instance.filterDefaultValues)
                {
                    defaults[pair.Key] = pair.Value;
                    events[pair.Key] = new List<FilterEvent>();
                }
            }

            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.SetFilter);
            for (int i = 0; i < source.Count; i++)
            {
                PreviewEventSnapshot levelEvent = source[i];
                Filter filter = context.GetValue(levelEvent, "filter", Filter.Grayscale);
                List<FilterEvent> bucket;
                if (!events.TryGetValue(filter, out bucket))
                {
                    bucket = new List<FilterEvent>();
                    events.Add(filter, bucket);
                }

                bucket.Add(new FilterEvent
                {
                    Start = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Enabled = context.GetValue(levelEvent, "enabled", false),
                    DisableOthers = context.GetValue(levelEvent, "disableOthers", false),
                    Intensity = context.GetValue(levelEvent, "intensity", 0f) / 100f
                });
            }

            foreach (List<FilterEvent> bucket in events.Values)
            {
                PreviewStableSort.Sort(bucket, (left, right) => left.Start.CompareTo(right.Start));
            }

            // 单独收集：一次触发禁用的是「其他」滤镜，所以
            // 求每个滤镜时都得考虑它，而不只是它自己那个。
            disableTriggers.Clear();
            foreach (KeyValuePair<Filter, List<FilterEvent>> pair in events)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].DisableOthers)
                    {
                        disableTriggers.Add(new DisableTrigger
                        {
                            Time = (float)pair.Value[i].Start,
                            Filter = pair.Key
                        });
                    }
                }
            }
            PreviewStableSort.Sort(disableTriggers,
                (left, right) => left.Time.CompareTo(right.Time));

            Main.Logger.Log("[StaticPreview/Filter] analyzed " + source.Count
                + " events; components=" + (components == null ? 0 : components.Count)
                + "; disableOthers=" + disableTriggers.Count + ".");
        }

        /// <summary>
        /// 当某个 disableOthers 事件把这个滤镜关掉、之后
        /// 又没有任何东西把它打开时，为 true。
        ///
        /// 原生是在触发那一刻命令式地做这件事（它会禁用其他每一个
        /// 组件），所以从绝对时间重建它，就等于问：在这个滤镜
        /// 自己最后一个事件之后，有没有发生过这样的触发——
        /// 它自己更晚的事件会把它重新打开，谱面就是靠这个把滤镜再开的。
        /// </summary>
        private bool IsSuppressedByAnother(Filter filter, float time, float ownLastEventTime)
        {
            for (int i = disableTriggers.Count - 1; i >= 0; i--)
            {
                DisableTrigger trigger = disableTriggers[i];
                if (trigger.Time > time)
                {
                    continue;
                }
                if (trigger.Filter == filter)
                {
                    continue;
                }

                // 触发是按时间排好的，所以这是可能生效的最近一次。
                // 是否胜出，取决于对这个滤镜来说哪个更靠后。
                return trigger.Time > ownLastEventTime;
            }

            return false;
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            if (components == null)
            {
                return;
            }

            foreach (KeyValuePair<Filter, MonoBehaviour> pair in components)
            {
                MonoBehaviour component = pair.Value;
                if (component == null)
                {
                    continue;
                }

                List<FilterEvent> bucket;
                if (!events.TryGetValue(pair.Key, out bucket))
                {
                    component.enabled = false;
                    continue;
                }

                FilterEvent current = null;
                FilterEvent previous = null;
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (bucket[i].Start > frame.Time)
                    {
                        break;
                    }

                    previous = current;
                    current = bucket[i];
                }

                if (current == null)
                {
                    component.enabled = false;
                    ApplyIntensity(component, pair.Key, defaults.ContainsKey(pair.Key) ? defaults[pair.Key] : 0f);
                    continue;
                }

                // 谱面别处的某个 disableOthers 事件会把这个滤镜关掉，
                // 直到有东西再次指向它为止。
                if (IsSuppressedByAnother(pair.Key, frame.Time, (float)current.Start))
                {
                    component.enabled = false;
                    continue;
                }

                float oldIntensity = previous == null
                    ? (defaults.ContainsKey(pair.Key) ? defaults[pair.Key] : 0f)
                    : previous.Intensity;
                float ratio = current.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((frame.Time - (float)current.Start) / current.Duration);
                float intensity = PreviewEase.EasedValue(oldIntensity, current.Intensity, ratio, current.Ease);
                component.enabled = current.Enabled;
                if (component.enabled)
                {
                    ApplyIntensity(component, pair.Key, intensity);
                }
            }
        }

        public void EndSession()
        {
            events.Clear();
            defaults.Clear();
            disableTriggers.Clear();
            components = null;
        }

        /// <summary>
        /// 写入滤镜的强度，一次一个 case，对齐
        /// ffxSetFilterPlus.SetFilter。
        ///
        /// 这里有两点要紧。第一，这些 shader 大多读的是一个 STATIC 属性，
        /// 所以原生代码会同时给实例字段和静态字段赋值
        /// ("...Film_Grain.Value = v; CameraFilterPackLegacy_Film_Grain.ChangeValue = v;").
        /// 只写实例字段会让 shader 停在默认值上。第二，每个
        /// 滤镜映射自己的成员名和缩放；之前的兜底是去猜
        /// 一堆可能的名字，结果可能扰动无关的字段。
        /// </summary>
        private static void ApplyIntensity(MonoBehaviour component, Filter filter, float value)
        {
            switch (filter)
            {
                case Filter.VHS:
                    SetNumericMember(component, "TRACKING", 0.212f * value);
                    break;
                case Filter.LED:
                    SetNumericMember(component, "Size", Mathf.RoundToInt(5f * value));
                    break;
                case Filter.Drawing:
                    SetNumericMember(component, "Fade_With_Original", Mathf.Clamp(value, 0f, 1f));
                    break;
                case Filter.Compression:
                    SetNumericMember(component, "Parasite", 3f * value);
                    break;
                case Filter.Waves:
                    SetNumericMember(component, "WaveIntensity", 10f * value);
                    SetStaticNumericMember(component, "ChangeWaveIntensity", 10f * value);
                    break;
                case Filter.Pixelate:
                    SetNumericMember(component, "_Pixelisation", 4f * value);
                    SetStaticNumericMember(component, "ChangePixel", 4f * value);
                    break;
                case Filter.Rain:
                    SetNumericMember(component, "Intensity", 0.5f * value);
                    break;
                case Filter.Blizzard:
                    SetNumericMember(component, "_Speed", value);
                    break;
                case Filter.PixelSnow:
                    SetNumericMember(component, "Threshold", 0.9f + 0.1f * value);
                    break;
                case Filter.Static:
                    SetNumericMember(component, "Fade", value);
                    SetStaticNumericMember(component, "ChangeValue", value);
                    break;
                case Filter.Grain:
                    SetNumericMember(component, "Value", 32f * value);
                    SetStaticNumericMember(component, "ChangeValue", 32f * value);
                    break;
                case Filter.MotionBlur:
                    SetNumericMember(component, "velocityScale", 0.375f * value);
                    break;
                case Filter.Fisheye:
                    SetNumericMember(component, "Distortion", value);
                    SetStaticNumericMember(component, "ChangeDistortion", value);
                    break;
                case Filter.Aberration:
                    SetNumericMember(component, "Offset", value * 0.04f - 0.02f);
                    SetStaticNumericMember(component, "ChangeOffset", value * 0.04f - 0.02f);
                    break;
                case Filter.Sepia:
                case Filter.Grayscale:
                    SetNumericMember(component, "Intensity", value);
                    break;
                case Filter.HexagonBlack:
                    SetNumericMember(component, "Value", Mathf.Max(0.2f, value));
                    SetStaticNumericMember(component, "ChangeValue", Mathf.Max(0.2f, value));
                    break;
                case Filter.Posterize:
                    SetNumericMember(component, "Posterize", value * 20f);
                    SetStaticNumericMember(component, "ChangePosterize", value * 20f);
                    break;
                case Filter.Sharpen:
                    SetNumericMember(component, "Value2", value);
                    SetStaticNumericMember(component, "ChangeValue2", value);
                    break;
                case Filter.Contrast:
                    SetNumericMember(component, "Contrast", value + 1f);
                    SetStaticNumericMember(component, "ChangeContrast", value + 1f);
                    break;
                case Filter.OilPaint:
                    SetNumericMember(component, "Value", value);
                    SetStaticNumericMember(component, "ChangeValue", value);
                    break;
                case Filter.Blur:
                    SetNumericMember(component, "Amount", value * 2f);
                    SetStaticNumericMember(component, "ChangeAmount", value * 2f);
                    break;
                case Filter.BlurFocus:
                    SetNumericMember(component, "_Size", Mathf.Max(0.10001f, value));
                    SetStaticNumericMember(component, "ChangeSize", Mathf.Max(0.10001f, value));
                    break;
                case Filter.GaussianBlur:
                    SetNumericMember(component, "Size", Mathf.Max(0.10001f, value));
                    SetStaticNumericMember(component, "ChangeSize", value);
                    break;
                case Filter.WaterDrop:
                    float drop = Mathf.Lerp(64f, 8f, Mathf.Clamp01(value));
                    SetNumericMember(component, "Distortion", drop);
                    SetStaticNumericMember(component, "ChangeDistortion", drop);
                    break;
                case Filter.LightWater:
                    SetNumericMember(component, "Intensity", value * 2.4f);
                    SetStaticNumericMember(component, "ChangeValue4", value * 2.4f);
                    break;

                // 原生 switch 里这些是空体列出的：它们纯粹
                // 靠组件的 enabled 标志来开关，不带强度。
                case Filter.Invert:
                case Filter.EightiesTV:
                case Filter.FiftiesTV:
                case Filter.Arcade:
                case Filter.Glitch:
                case Filter.Neon:
                case Filter.Handheld:
                case Filter.NightVision:
                case Filter.Funk:
                case Filter.Tunnel:
                case Filter.Weird3D:
                case Filter.EdgeBlackLine:
                case Filter.SuperDot:
                case Filter.Petals:
                case Filter.PetalsInstant:
                    break;

                default:
                    // 这个滤镜没有已知的成员名，所以宁可不写，
                    // 也不去猜几个可能的名字。
                    break;
            }
        }

        /// <summary>
        /// 在组件的类型上给静态字段或属性赋值。
        /// CameraFilterPack 的 shader 读的是静态值，所以只写实例
        /// 成员会让它们停在默认值上。
        /// </summary>
        private static void SetStaticNumericMember(object target, string name, float value)
        {
            const BindingFlags flags = BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic;

            Type type = target.GetType();
            FieldInfo field = type.GetField(name, flags);
            if (field != null && IsNumeric(field.FieldType))
            {
                field.SetValue(null, ConvertNumeric(field.FieldType, value));
                return;
            }

            System.Reflection.PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite && IsNumeric(property.PropertyType))
            {
                property.SetValue(null, ConvertNumeric(property.PropertyType, value), null);
            }
        }

        private static void SetNumericMember(object target, string name, float value)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && IsNumeric(field.FieldType))
            {
                field.SetValue(target, ConvertNumeric(field.FieldType, value));
            }

            System.Reflection.PropertyInfo property = type.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && IsNumeric(property.PropertyType))
            {
                property.SetValue(target, ConvertNumeric(property.PropertyType, value), null);
            }
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(float) || type == typeof(double) || type == typeof(int);
        }

        private static object ConvertNumeric(Type type, float value)
        {
            if (type == typeof(double))
            {
                return (double)value;
            }
            if (type == typeof(int))
            {
                return Mathf.RoundToInt(value);
            }
            return value;
        }
    }
}