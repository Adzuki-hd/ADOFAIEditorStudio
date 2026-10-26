using System;
using System.Collections.Generic;
using System.Reflection;
using ADOFAI;
using DG.Tweening;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// SetFilterAdvanced 的绝对时间求值器。
    ///
    /// 真正的滤镜组件归游戏所有。这个域只在原生的前景/背景相机上
    /// 创建同样的首趟行为，并在绝对时间轴时刻
    /// 写它们的公开字段。
    /// </summary>
    internal sealed class FilterAdvancedPreviewDomain : IPreviewDomain
    {
        private sealed class AdvancedEvent
        {
            internal double Start;
            internal float Duration;
            internal Ease Ease;
            internal bool Enabled;
            internal bool DisableOthers;
            internal string FilterName;
            internal FilterPlane Plane;
            internal readonly Dictionary<string, object> Properties =
                new Dictionary<string, object>();
        }

        private sealed class FilterTrack
        {
            internal string Name;
            internal FilterPlane Plane;
            internal Type FilterType;
            internal FieldInfo[] Fields;
            /// <summary>
            /// 背景平面的滤镜要同时挂到两台背景相机上。
            ///
            /// 原版只挂 `BGcam`（移动背景那台），但编辑器场景里静态背景
            /// 由 `Bgcamstatic` 画 —— 只挂一台的话，画面上看到的背景
            /// 不会被染色（本谱 2019 的 YUV 就是“原版蓝、预览紫”）。
            /// 两台各画各的层，不会重复染色。
            /// </summary>
            internal readonly List<MonoBehaviour> Components = new List<MonoBehaviour>();
            internal readonly List<bool> ComponentAdded = new List<bool>();
            internal bool OriginalEnabled;
            internal readonly Dictionary<string, object> OriginalValues =
                new Dictionary<string, object>();
            internal readonly List<Dictionary<string, object>> ComponentOriginals =
                new List<Dictionary<string, object>>();
            internal readonly List<bool> ComponentOriginalEnabled = new List<bool>();
            internal readonly List<AdvancedEvent> Events = new List<AdvancedEvent>();
        }

        private readonly Dictionary<string, FilterTrack> tracks =
            new Dictionary<string, FilterTrack>(StringComparer.Ordinal);
        private readonly HashSet<string> unsupportedFields =
            new HashSet<string>(StringComparer.Ordinal);

        public string Name
        {
            get { return "FilterAdvanced"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            EndSession();
            unsupportedFields.Clear();

            IList<PreviewEventSnapshot> source = context.GetEvents(LevelEventType.SetFilterAdvanced);
            int accepted = 0;
            for (int i = 0; i < source.Count; i++)
            {
                PreviewEventSnapshot levelEvent = source[i];
                string filterName = ReadString(levelEvent, "filter");
                if (string.IsNullOrEmpty(filterName))
                {
                    Main.Logger.Log("[StaticPreview/FilterAdvanced] skipped event without filter name.");
                    continue;
                }

                FilterPlane plane = ReadEnum(levelEvent, "plane", FilterPlane.Foreground);
                FilterTrack track = GetOrCreateTrack(filterName, plane);
                if (track == null)
                {
                    continue;
                }

                AdvancedEvent advancedEvent = new AdvancedEvent
                {
                    Start = context.GetEventStartTime(levelEvent),
                    Duration = context.GetEventDuration(levelEvent),
                    Ease = context.GetEase(levelEvent),
                    Enabled = ReadBool(levelEvent, "enabled", false),
                    DisableOthers = ReadBool(levelEvent, "disableOthers", false),
                    FilterName = filterName,
                    Plane = plane
                };

                // 原生 ffxSetFilterAdvancedPlus 在事件禁用某个滤镜时
                // 不改字段。拖动定位时保持这个行为。
                if (advancedEvent.Enabled)
                {
                    var data = levelEvent.GetData();
                    if (data != null)
                    {
                        foreach (KeyValuePair<string, object> pair in data)
                        {
                            if (!pair.Key.StartsWith("filter_", StringComparison.Ordinal)
                                || !IsPropertyEnabled(levelEvent, pair.Key))
                            {
                                continue;
                            }

                            string fieldName = pair.Key.Substring("filter_".Length);
                            advancedEvent.Properties[fieldName] = pair.Value;
                        }
                    }
                }

                track.Events.Add(advancedEvent);
                accepted++;
            }

            foreach (FilterTrack track in tracks.Values)
            {
                PreviewStableSort.Sort(track.Events, (left, right) => left.Start.CompareTo(right.Start));
            }

            Main.Logger.Log("[StaticPreview/FilterAdvanced] analyzed " + accepted
                + " events; tracks=" + tracks.Count + ".");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            foreach (FilterTrack track in tracks.Values)
            {
                if (track.Components.Count == 0)
                {
                    continue;
                }

                AdvancedEvent latest = FindLatestEvent(track.Events, frame.Time);
                bool enabled = latest != null && latest.Enabled;
                if (enabled && IsSuppressedByAnotherTrack(track, frame.Time, (float)latest.Start))
                {
                    enabled = false;
                }

                for (int c = 0; c < track.Components.Count; c++)
                {
                    if (track.Components[c] != null)
                    {
                        track.Components[c].enabled = enabled;
                    }
                }
                if (track.Fields == null)
                {
                    continue;
                }

                for (int i = 0; i < track.Fields.Length; i++)
                {
                    FieldInfo field = track.Fields[i];
                    if (!IsSupportedField(field.FieldType))
                    {
                        LogUnsupportedField(track, field);
                        continue;
                    }

                    ApplyField(track, field, frame.Time);
                }

                // 每约一秒打一次 YUV 的实际字段值：证明滤镜有没有被真正启用、
                    // 以及 100/-100 有没有被正确换算成 1/-1。
                if (track.Name == "CameraFilterPack_Color_YUV"
                    && Main.Settings != null && Main.Settings.DecorationProbe
                    && track.Components.Count > 0
                    && Time.frameCount % 60 == 0)
                {
                    FieldInfo u = FindField(track.Fields, "_U");
                    FieldInfo v = FindField(track.Fields, "_V");
                    FieldInfo y = FindField(track.Fields, "_Y");
                    MonoBehaviour probe = track.Components[0];
                    Main.Logger.Log("[FilterProbe] YUV t=" + frame.Time.ToString("F2")
                        + " enabled=" + probe.enabled
                        + " active=" + probe.gameObject.activeInHierarchy
                        + " cameras=" + track.Components.Count
                        + " U=" + (u == null ? "无" : u.GetValue(probe).ToString())
                        + " V=" + (v == null ? "无" : v.GetValue(probe).ToString())
                        + " Y=" + (y == null ? "无" : y.GetValue(probe).ToString()));
                }
            }
        }

        public void EndSession()
        {
            foreach (FilterTrack track in tracks.Values)
            {
                for (int c = 0; c < track.Components.Count; c++)
                {
                    MonoBehaviour component = track.Components[c];
                    if (component == null)
                    {
                        continue;
                    }

                    Dictionary<string, object> originals = c < track.ComponentOriginals.Count
                        ? track.ComponentOriginals[c]
                        : track.OriginalValues;
                    foreach (KeyValuePair<string, object> pair in originals)
                    {
                        FieldInfo field = FindField(track.Fields, pair.Key);
                        if (field == null)
                        {
                            continue;
                        }

                        try
                        {
                            field.SetValue(component, pair.Value);
                        }
                        catch (Exception exception)
                        {
                            Main.Logger.Error("[StaticPreview/FilterAdvanced] failed to restore "
                                + track.Name + "." + pair.Key + ": " + exception.Message);
                        }
                    }

                    bool added = c < track.ComponentAdded.Count && track.ComponentAdded[c];
                    if (added)
                    {
                        try
                        {
                            UnityEngine.Object.DestroyImmediate(component);
                        }
                        catch (Exception exception)
                        {
                            Main.Logger.Error("[StaticPreview/FilterAdvanced] failed to destroy "
                                + track.Name + ": " + exception.Message);
                        }
                    }
                    else
                    {
                        component.enabled = c < track.ComponentOriginalEnabled.Count
                            ? track.ComponentOriginalEnabled[c]
                            : track.OriginalEnabled;
                    }
                }
            }

            tracks.Clear();
        }

        private FilterTrack GetOrCreateTrack(string filterName, FilterPlane plane)
        {
            string key = ((int)plane).ToString() + ":" + filterName;
            FilterTrack existing;
            if (tracks.TryGetValue(key, out existing))
            {
                return existing;
            }

            Type filterType = ResolveFilterType(filterName);
            if (filterType == null)
            {
                Main.Logger.Log("[StaticPreview/FilterAdvanced] type not found: " + filterName);
                return null;
            }

            if (!typeof(MonoBehaviour).IsAssignableFrom(filterType))
            {
                Main.Logger.Log("[StaticPreview/FilterAdvanced] type is not a behaviour: "
                    + filterName);
                return null;
            }

            FilterTrack track = new FilterTrack
            {
                Name = filterName,
                Plane = plane,
                FilterType = filterType,
                Fields = filterType.GetFields(BindingFlags.Instance | BindingFlags.Public)
            };

            // 前景只挂主相机；背景两台都要挂（BGcam 画移动背景，
            // Bgcamstatic 画静态背景，编辑器里看到的那层是哪台不确定）。
            List<Camera> cameras = new List<Camera>();
            Camera mainCamera = GetPlaneCamera(FilterPlane.Foreground);
            if (plane == FilterPlane.Foreground)
            {
                if (mainCamera != null)
                {
                    cameras.Add(mainCamera);
                }
            }
            else
            {
                // **背景滤镜只能挂背景相机。**
                //
                // 试过挂主相机（camobj）：整谱画面会被重复/错误染色 ——
                // 编辑器里主相机也参与合成，背景滤镜上去就是全屏乱套。
                // 原版同样只挂 BGcam；Bgcamstatic 是补静态背景那层。
                scrCamera scr = scrCamera.instance;
                if (scr != null)
                {
                    if (scr.BGcam != null)
                    {
                        cameras.Add(scr.BGcam);
                    }
                    if (scr.Bgcamstatic != null && scr.Bgcamstatic != scr.BGcam)
                    {
                        cameras.Add(scr.Bgcamstatic);
                    }
                }
            }

            if (cameras.Count == 0)
            {
                Main.Logger.Log("[StaticPreview/FilterAdvanced] camera not found for plane "
                    + plane + ": " + filterName);
                return null;
            }

            for (int c = 0; c < cameras.Count; c++)
            {
                Camera camera = cameras[c];
                Component component = camera.gameObject.GetComponent(filterType);
                bool added = false;
                if (component == null)
                {
                    try
                    {
                        component = camera.gameObject.AddComponent(filterType);
                        added = component != null;
                    }
                    catch (Exception exception)
                    {
                        Main.Logger.Error("[StaticPreview/FilterAdvanced] AddComponent failed for "
                            + filterName + " on " + camera.name + ": " + exception.Message);
                        continue;
                    }
                }

                MonoBehaviour behaviour = component as MonoBehaviour;
                if (behaviour == null)
                {
                    continue;
                }

                track.Components.Add(behaviour);
                track.ComponentAdded.Add(added);
                track.ComponentOriginalEnabled.Add(behaviour.enabled);
                if (added)
                {
                    behaviour.enabled = false;
                }

                Dictionary<string, object> originals = new Dictionary<string, object>();
                for (int i = 0; i < track.Fields.Length; i++)
                {
                    FieldInfo field = track.Fields[i];
                    if (!IsSupportedField(field.FieldType))
                    {
                        continue;
                    }
                    try
                    {
                        originals[field.Name] = field.GetValue(component);
                        if (c == 0)
                        {
                            track.OriginalValues[field.Name] = originals[field.Name];
                        }
                    }
                    catch (Exception exception)
                    {
                        Main.Logger.Error("[StaticPreview/FilterAdvanced] failed to snapshot "
                            + filterName + "." + field.Name + ": " + exception.Message);
                    }
                }
                track.ComponentOriginals.Add(originals);

                Main.Logger.Log("[StaticPreview/FilterAdvanced] track " + filterName
                    + " plane=" + plane
                    + " camera=" + camera.name
                    + " active=" + camera.gameObject.activeInHierarchy
                    + " enabled=" + camera.enabled
                    + " added=" + added);
            }

            if (track.Components.Count == 0)
            {
                return null;
            }

            track.OriginalEnabled = track.ComponentOriginalEnabled[0];

            tracks.Add(key, track);
            return track;
        }

        private void ApplyField(FilterTrack track, FieldInfo field, float time)
        {
            AdvancedEvent currentEvent = null;
            AdvancedEvent previousEvent = null;
            for (int i = 0; i < track.Events.Count; i++)
            {
                AdvancedEvent candidate = track.Events[i];
                if (candidate.Start > time)
                {
                    break;
                }

                object ignored;
                if (!candidate.Properties.TryGetValue(field.Name, out ignored))
                {
                    continue;
                }

                previousEvent = currentEvent;
                currentEvent = candidate;
            }

            object original;
            if (currentEvent == null || !track.OriginalValues.TryGetValue(field.Name, out original))
            {
                return;
            }

            object fromValue = original;
            if (previousEvent != null)
            {
                object previousRaw;
                if (previousEvent.Properties.TryGetValue(field.Name, out previousRaw))
                {
                    object convertedPrevious;
                    if (TryConvertProperty(previousRaw, field.FieldType, field.Name, out convertedPrevious))
                    {
                        fromValue = convertedPrevious;
                    }
                }
            }

            object toValue;
            object rawValue = currentEvent.Properties[field.Name];
            if (!TryConvertProperty(rawValue, field.FieldType, field.Name, out toValue))
            {
                return;
            }

            float ratio = currentEvent.Duration <= 0f
                ? 1f
                : Mathf.Clamp01((time - (float)currentEvent.Start) / currentEvent.Duration);
            float eased = PreviewEase.EasedValue(0f, 1f, ratio, currentEvent.Ease);
            object value = Interpolate(fromValue, toValue, field.FieldType, eased);
            try
            {
                for (int c = 0; c < track.Components.Count; c++)
                {
                    if (track.Components[c] != null)
                    {
                        field.SetValue(track.Components[c], value);
                    }
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/FilterAdvanced] failed to set "
                    + track.Name + "." + field.Name + ": " + exception.Message);
            }
        }

        private bool IsSuppressedByAnotherTrack(FilterTrack target, float time, float ownStart)
        {
            foreach (FilterTrack other in tracks.Values)
            {
                if (other == target || other.Plane != target.Plane)
                {
                    continue;
                }

                for (int i = 0; i < other.Events.Count; i++)
                {
                    AdvancedEvent candidate = other.Events[i];
                    if (candidate.Start > time)
                    {
                        break;
                    }

                    if (candidate.DisableOthers && candidate.Start > ownStart)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void LogUnsupportedField(FilterTrack track, FieldInfo field)
        {
            string key = track.Name + "." + field.Name;
            if (unsupportedFields.Add(key))
            {
                Main.Logger.Log("[StaticPreview/FilterAdvanced] skipped unsupported field "
                    + key + " (" + field.FieldType.Name + ").");
            }
        }

        private static AdvancedEvent FindLatestEvent(List<AdvancedEvent> source, float time)
        {
            AdvancedEvent latest = null;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].Start > time)
                {
                    break;
                }

                latest = source[i];
            }

            return latest;
        }

        private static FieldInfo FindField(FieldInfo[] fields, string name)
        {
            if (fields == null)
            {
                return null;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Name == name)
                {
                    return fields[i];
                }
            }

            return null;
        }

        private static object Interpolate(object fromValue, object toValue, Type type, float eased)
        {
            if (type == typeof(float))
            {
                return Mathf.LerpUnclamped((float)fromValue, (float)toValue, eased);
            }

            if (type == typeof(int))
            {
                return Mathf.RoundToInt(Mathf.LerpUnclamped((int)fromValue, (int)toValue, eased));
            }

            if (type == typeof(Color))
            {
                return Color.LerpUnclamped((Color)fromValue, (Color)toValue, eased);
            }

            if (type == typeof(Vector2))
            {
                return Vector2.LerpUnclamped((Vector2)fromValue, (Vector2)toValue, eased);
            }

            return toValue;
        }

        private static bool TryConvertProperty(object raw, Type type, string fieldName,
            out object value)
        {
            value = null;
            if (raw == null)
            {
                return false;
            }

            try
            {
                float multiplier = RDEditorUtils.UnitMultiplier(
                    RDEditorUtils.FilterFieldToUnit(
                        RDEditorUtils.TypeToPropertyType(type), fieldName));

                if (type == typeof(float))
                {
                    value = Convert.ToSingle(raw) / multiplier;
                    return true;
                }

                if (type == typeof(int))
                {
                    value = Mathf.RoundToInt(Convert.ToSingle(raw) / multiplier);
                    return true;
                }

                if (type == typeof(Color))
                {
                    if (raw is Color)
                    {
                        value = raw;
                        return true;
                    }

                    string hex = raw as string;
                    Color color;
                    if (!string.IsNullOrEmpty(hex)
                        && ColorUtility.TryParseHtmlString(
                            hex.StartsWith("#", StringComparison.Ordinal) ? hex : "#" + hex,
                            out color))
                    {
                        value = color;
                        return true;
                    }

                    return false;
                }

                if (type == typeof(Vector2))
                {
                    if (raw is Vector2)
                    {
                        value = raw;
                        return true;
                    }

                    Tuple<float, float> tuple = raw as Tuple<float, float>;
                    if (tuple != null)
                    {
                        value = new Vector2(tuple.Item1, tuple.Item2);
                        return true;
                    }

                    System.Collections.IList list = raw as System.Collections.IList;
                    if (list != null && list.Count >= 2)
                    {
                        value = new Vector2(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]));
                        return true;
                    }
                }
            }
            catch
            {
                // 未知的滤镜数据不该让整个预览域失效。
            }

            return false;
        }

        private static bool IsSupportedField(Type type)
        {
            return type == typeof(float) || type == typeof(int)
                || type == typeof(Color) || type == typeof(Vector2);
        }

        private static Type ResolveFilterType(string filterName)
        {
            Type type = Type.GetType(filterName + ", Assembly-CSharp-firstpass");
            if (type != null)
            {
                return type;
            }

            try
            {
                Assembly assembly = Assembly.Load("Assembly-CSharp-firstpass");
                return assembly == null ? null : assembly.GetType(filterName);
            }
            catch
            {
                return null;
            }
        }

        private static Camera GetPlaneCamera(FilterPlane plane)
        {
            scrCamera camera = scrCamera.instance;
            if (camera == null)
            {
                return null;
            }

            return plane == FilterPlane.Background ? camera.BGcam : camera.camobj;
        }

        private static string ReadString(PreviewEventSnapshot levelEvent, string key)
        {
            object value;
            if (levelEvent == null || levelEvent.GetData() == null
                || !levelEvent.GetData().TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            return value as string ?? value.ToString();
        }

        private static bool ReadBool(PreviewEventSnapshot levelEvent, string key, bool fallback)
        {
            object value;
            if (levelEvent == null || levelEvent.GetData() == null
                || !levelEvent.GetData().TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static T ReadEnum<T>(PreviewEventSnapshot levelEvent, string key, T fallback)
            where T : struct
        {
            object value;
            if (levelEvent == null || levelEvent.GetData() == null
                || !levelEvent.GetData().TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            if (value is T)
            {
                return (T)value;
            }

            try
            {
                if (value is string)
                {
                    T parsed;
                    if (Enum.TryParse((string)value, true, out parsed))
                    {
                        return parsed;
                    }
                }

                return (T)Enum.ToObject(typeof(T), Convert.ToInt32(value));
            }
            catch
            {
                return fallback;
            }
        }

        private static bool IsPropertyEnabled(PreviewEventSnapshot levelEvent, string key)
        {
            if (levelEvent == null)
            {
                return true;
            }

            return levelEvent.IsPropertyEnabled(key);
        }
    }
}
