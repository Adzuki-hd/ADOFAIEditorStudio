using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Multiplayer
{
    /// <summary>
    /// 一次属性改动的描述。联机同步的最小单元。
    ///
    /// 比传整份谱面小得多，而且能精确定位到「哪个事件的哪个键」。
    /// </summary>
    internal sealed class EditPayload
    {
        /// <summary>改动落在哪张表：true = decorations，false = events。</summary>
        public bool isDecoration;
        /// <summary>事件在表里的下标。**注意：增删事件会让所有下标位移，所以这类改动不走增量。**</summary>
        public int index;
        public string[] keys;
        public string[] values;
        public bool[] disabled;
        /// <summary>谁改的。接收端显示用。</summary>
        public string from;

        /// <summary>
        /// 发送端认为这个下标上是**什么类型的事件**、在**第几格**。
        ///
        /// 这两样合起来是「这还是同一个事件吗」的判据。用它而不是
        /// 元素个数，是因为增删事件会让下标位移，但**追加不会**——
        /// 而追加是常见操作（拖装饰物就会追加一个关键帧事件）。详见
        /// <see cref="MultiplayerEditSink.StructureMatches"/>。
        /// </summary>
        public string eventType;
        public int floor;
    }

    /// <summary>
    /// 所有「写谱面」动作的统一出口：增量改动的广播与落地。
    ///
    /// **为什么要单独一层**：项目里写谱面的入口不止一处——参数栏、时间轴拖块、
    /// 装饰物列表，还有几处直接写 `LevelEvent` 绕过了历史记录
    /// （改格号、属性启用开关、装饰物显隐/锁定）。联机要同步改动，
    /// 就必须**每一个入口都经过这里**，漏一个就会变成
    /// 「有的改动会同步、有的不会」——那比全不同步更难查。
    ///
    /// 这一层只管「把这次改动描述出来、发出去、落地」。
    ///
    /// **不做冲突检测。** 两个人改同一个值时，后到的那条覆盖先到的
    /// —— 这是有意的，理由见 `MultiplayerSession` 的类注释。
    /// </summary>
    internal static class MultiplayerEditSink
    {
        /// <summary>
        /// 远端改动正在落地时的抑制深度。
        ///
        /// 用它挡住回音：B 收到 A 的改动 → 写进本地谱面 → 这次写入如果
        /// 又被当成「本地改动」报上去，就会广播回 A，A 再广播回来，
        /// 停不下来。落地期间挂起上报，环就断了。
        /// </summary>
        private static int suspendDepth;

        internal static bool Suspended
        {
            get { return suspendDepth > 0; }
        }

        /// <summary>在这个作用域内，所有上报都被吃掉。远端落地时用。</summary>
        internal static IDisposable Suspend()
        {
            suspendDepth++;
            return new SuspendScope();
        }

        private sealed class SuspendScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                suspendDepth = Mathf.Max(0, suspendDepth - 1);
            }
        }

        /// <summary>联机断开时清干净，免得半途的抑制状态留下来。</summary>
        internal static void Reset()
        {
            suspendDepth = 0;
            LastApplyFailure = ApplyFailure.None;
        }

        /// <summary>
        /// 最近一次 <see cref="ApplyRemote"/> 为什么落不了地。
        ///
        /// 分开记是因为**处理方式不同**：那个下标上已经是别的事件
        /// （两边增删过不同的事件，下标错位了）是没办法自动修的，只能让
        /// 用户决定谁推一次谱面；而下标越界或值还原不出来属于单条丢弃，
        /// 不构成「两边已经分叉」。
        /// 两者都不做自动重同步，原因见
        /// `MultiplayerSession.OnLocalStructuralChange`。
        ///
        /// **这和「冲突」是两回事。** 这里说的是「下标已经不指向同一个
        /// 事件了」，属于坐标失效，必须拦；而两个人改同一个事件的同一个
        /// 键，不算失效 —— 那就是后改的赢。
        /// </summary>
        internal enum ApplyFailure
        {
            None,
            /// <summary>那个下标上已经是另一个事件了（下标错位）。</summary>
            StructureMismatch,
            /// <summary>单条改动落不下去（下标越界、值还原不出来）。</summary>
            SingleEdit
        }

        internal static ApplyFailure LastApplyFailure { get; private set; }

        // ---------------------------------------------------------------- 上报

        /// <summary>
        /// 报一次改动。**所有写谱面的入口都要调它。**
        ///
        /// 参数用数组而不是单值：时间轴拖一个块会同时改 `floor` 和
        /// `angleOffset`（时间由这两者共同决定），那必须当成一次改动，
        /// 否则对面会看到块先跳一半再跳一半。
        /// </summary>
        internal static void Report(scnEditor editor, LevelEvent target,
            string[] keys, object[] values, bool[] disabled)
        {
            if (Suspended || editor == null || target == null
                || keys == null || values == null || keys.Length == 0)
            {
                // 远端改动正在落地（Suspended）是**完全正常**的——落地本身
                // 就会写一遍值，每次都记一笔会把日志刷满，所以那种情况
                // 直接返回。其余几种才是「不该发生但可能发生」，留痕。
                if (!Suspended)
                {
                    Main.Logger.Warning("[Edit] 上报被跳过：editor=" + (editor != null)
                        + " target=" + (target != null)
                        + " keys=" + (keys == null ? -1 : keys.Length));
                }
                return;
            }

            int index = IndexOf(editor, target, out bool isDecoration);
            if (index < 0)
            {
                // 找不到下标说明这个事件不在谱面的两张表里。报不出去——
                // 但**必须留下痕迹**：静默返回会让「某类改动不同步」
                // 变成完全查不到的幽灵问题。
                Main.Logger.Warning("[Edit] 找不到事件所在的位置（"
                    + keys[0] + "），这次改动不会同步。"
                    + " events=" + (editor.events == null ? -1 : editor.events.Count)
                    + " decorations=" + (editor.decorations == null ? -1 : editor.decorations.Count));
                return;
            }

            // 值都转成字符串：跨机器传对象序列化不可靠，
            // 而 LevelEvent 的索引器本来也吃字符串。
            string[] encoded = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                encoded[i] = Encode(values[i]);
            }

            var payload = new EditPayload
            {
                isDecoration = isDecoration,
                index = index,
                keys = keys,
                values = encoded,
                disabled = disabled,
                from = null,
                eventType = target.eventType.ToString(),
                floor = target.floor
            };

            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                Main.Logger.Log("[Edit] 广播 " + (isDecoration ? "装饰物" : "事件") + " #" + index
                    + " " + string.Join(",", keys)
                    + " = " + string.Join(",", encoded) + ".");
            }

            MultiplayerSession.OnLocalEdit(payload);
        }

        private static int IndexOf(scnEditor editor, LevelEvent target, out bool isDecoration)
        {
            isDecoration = false;

            if (editor.decorations != null)
            {
                for (int i = 0; i < editor.decorations.Count; i++)
                {
                    if (ReferenceEquals(editor.decorations[i], target))
                    {
                        isDecoration = true;
                        return i;
                    }
                }
            }

            if (editor.events != null)
            {
                for (int i = 0; i < editor.events.Count; i++)
                {
                    if (ReferenceEquals(editor.events[i], target))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string Encode(object value)
        {
            if (value == null)
            {
                // 谱面里 null 是有意义的值（相机位置、装饰物位移的
                // 「这个轴别动」就是 null 数组元素），所以要能原样往返。
                return null;
            }

            if (value is float f)
            {
                return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value is double d)
            {
                return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value is bool b)
            {
                return b ? "1" : "0";
            }
            if (value is Vector2 v2)
            {
                return v2.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + v2.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value is string s)
            {
                return s;
            }
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- 落地

        /// <summary>
        /// 把远端来的一次改动写进本地谱面。
        ///
        /// 整个落地过程都在 <see cref="Suspend"/> 里，所以它自己不会被
        /// 当成新的本地改动再报出去。
        /// </summary>
        internal static bool ApplyRemote(EditPayload payload)
        {
            if (payload == null || payload.keys == null || payload.values == null)
            {
                return false;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return false;
            }

            // **先确认两边的结构对得上，再动任何东西。**
            //
            // 增删事件不做自动同步，所以两边的事件表可能已经不一样长。
            // 这种情况下下标**依然有效**——只是指向了另一个事件。照写
            // 下去不会报错，会安静地改坏别人的数据，而且两边都不会
            // 察觉。宁可整条拒绝，也绝不猜着写。
            if (!StructureMatches(editor, payload, out string mismatch))
            {
                LastApplyFailure = ApplyFailure.StructureMismatch;
                Main.Logger.Log("[联机] 拒绝落地这次改动：" + mismatch
                    + "（按下标写会改到别的事件）。");
                return false;
            }

            LevelEvent target = FindTarget(editor, payload);
            if (target == null)
            {
                // 结构对得上却找不到——不该发生，留痕。
                LastApplyFailure = ApplyFailure.SingleEdit;
                Main.Logger.Log("[联机] 改动的目标事件找不到（"
                    + (payload.isDecoration ? "装饰物" : "事件") + " #" + payload.index + "）。");
                return false;
            }

            LastApplyFailure = ApplyFailure.None;

            using (Suspend())
            {
                for (int i = 0; i < payload.keys.Length && i < payload.values.Length; i++)
                {
                    string key = payload.keys[i];
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    // floor 不是 data 字典里的键，是 LevelEvent 的字段。
                    // 走索引器写它会往 data 里塞一个没人读的副本，格子没动。
                    if (string.Equals(key, "floor", StringComparison.Ordinal))
                    {
                        int parsed;
                        if (int.TryParse(payload.values[i], out parsed))
                        {
                            target.floor = parsed;
                        }
                        continue;
                    }

                    target[key] = Decode(payload.values[i], target, key);

                    if (payload.disabled != null && i < payload.disabled.Length
                        && target.disabled != null && target.disabled.ContainsKey(key))
                    {
                        target.disabled[key] = payload.disabled[i];
                    }
                }

                // 和面板编辑之后做的刷新一致：重跑 Setup 让实时装饰物读到新值，
                // 再请求求值器重建，预览据此重新播种基线。
                if (target.info != null && target.info.isDecoration)
                {
                    editor.UpdateDecorationObject(target);
                }
                EditorPlaybackBridge.RequestEvaluatorRebuild("multiplayer-remote-edit");
            }

            NoticeRemoteEdit(payload, target);

            return true;
        }

        /// <summary>
        /// 远端改的是**本机正在看着的那个事件**时，把新值刷到眼前。
        ///
        /// **必须重画整片参数栏，不能只改控件里的文字。** 参数栏的行是
        /// 按 `PropertyInfo` 元数据生成的：改一个键可能要连带改别的东西
        /// （启用开关的勾选状态、缓动曲线的形状、向量两个框之间的关系）。
        /// 只改文字的话，面板显示的是一半新一半旧的混合态，比不刷新更糟。
        ///
        /// 重画会销毁并重建所有控件，正在编辑的输入框会被一起销毁，
        /// 所以重建之后要把焦点和光标位置还原回去——见
        /// <see cref="EditorPropertyPanel.ReapplyEditingFocus"/>。
        /// 多数情况（对方改的是别的事件、或者你根本没在打字）根本不重建，
        /// 这一整套还原也就用不上。
        /// </summary>
        private static void NoticeRemoteEdit(EditPayload payload, LevelEvent target)
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                return;
            }

            // 参数栏只画「当前选中的那个事件」。改的不是它，面板上
            // 根本没有对应的行要刷。
            if (!ReferenceEquals(overlay.TimelineSelection, target))
            {
                return;
            }

            string who = string.IsNullOrEmpty(payload.from) ? "对方" : payload.from;
            overlay.RefreshInspectorFromRemote(who, payload.isDecoration);
        }

        /// <summary>
        /// 发送端那个下标上，是不是本机同一个事件。
        ///
        /// **这里原本比的是「两张表各有几个元素」，那是错的。**
        ///
        /// 当时的推理是「元素数不一样 → 下标多半错位了」。但拖一个装饰物
        /// 就会往 `events` 里**追加**一个 MoveDecorations 关键帧
        /// （见 `EditorSceneInteraction.BeginDecorationDrag`），追加
        /// **不移动任何已有下标**——于是这个判据会在完全正常的编辑之后
        /// 永久拒绝所有改动：一边 1001 个、一边 1000 个，从此一条都同步
        /// 不过去。表现就是「很多没同步」，而且查不出原因。
        ///
        /// 真正要回答的是「这个下标现在还是那个事件吗」。改成比
        /// **事件类型 + 所在格数**：插入或删除导致的下标位移，几乎必然
        /// 让这两样对不上（连续两个类型相同、格数也相同的事件基本不存在），
        /// 而单纯追加不影响任何已有下标，也就不会误判。
        /// </summary>
        private static bool StructureMatches(scnEditor editor, EditPayload payload, out string reason)
        {
            reason = null;

            LevelEvent target = FindTarget(editor, payload);
            if (target == null)
            {
                reason = "目标下标 " + payload.index + " 越界";
                return false;
            }

            if (!string.IsNullOrEmpty(payload.eventType)
                && !string.Equals(payload.eventType, target.eventType.ToString(), StringComparison.Ordinal))
            {
                reason = "下标 " + payload.index + " 上是另一个事件（对方是 "
                    + payload.eventType + "，本机是 " + target.eventType + "）";
                return false;
            }

            // **这次改动本身就在改格数时，不能拿格数判身份。**
            //
            // 发送端填的 `floor` 是**改完之后**的值（Report 是在值写进
            // 谱面之后才调的），而接收端此刻还是**改之前**的格数。两边
            // 天然对不上——拿它判等会把「把事件块拖到别的格」这种最正常
            // 的操作全部拒掉。拖动不同步就是这么来的。
            //
            // 事件类型不受这类编辑影响，仍然可靠；格数只在它**没被这次
            // 改动碰过**时才有比较的意义。
            if (!SharesKey(payload.keys, "floor") && payload.floor != target.floor)
            {
                reason = "下标 " + payload.index + " 上事件的格数对不上（对方是第 "
                    + payload.floor + " 格，本机是第 " + target.floor + " 格）";
                return false;
            }

            return true;
        }

        /// <summary>这次改动里有没有这个键。</summary>
        private static bool SharesKey(string[] keys, string key)
        {
            if (keys == null)
            {
                return false;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                if (string.Equals(keys[i], key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static LevelEvent FindTarget(scnEditor editor, EditPayload payload)        {
            if (payload.isDecoration)
            {
                return editor.decorations != null
                    && payload.index >= 0 && payload.index < editor.decorations.Count
                    ? editor.decorations[payload.index]
                    : null;
            }

            return editor.events != null
                && payload.index >= 0 && payload.index < editor.events.Count
                ? editor.events[payload.index]
                : null;
        }

        /// <summary>
        /// 把字符串还原成这个键该有的类型。
        ///
        /// **类型从「当前值」推断，而不是靠猜字符串格式，也不写类型表。**
        /// `LevelEvent[key]` 的索引器对类型是敏感的——同一个 "1" 可能是
        /// int 1、float 1f、也可能是枚举的第 1 项，猜错就写进一个
        /// 游戏读不懂的值。而当前值的类型本来就是对的，照着它还原即可。
        ///
        /// 当前值是 null 时（比如 position 的某个轴本来就是 null），
        /// 才退回按格式猜——那种情况只有浮点、向量、字符串几种可能。
        /// </summary>
        private static object Decode(string text, LevelEvent target, string key)
        {
            object current = null;
            try
            {
                current = target.GetData() != null && target.GetData().ContainsKey(key)
                    ? target.GetData()[key]
                    : null;
            }
            catch
            {
                // 取不到就当没有，下面按格式猜。
            }

            if (current != null)
            {
                return DecodeAs(text, current.GetType());
            }

            if (text == null)
            {
                return null;
            }

            // 空串代表布尔真：LevelEvent 里「启用但没有值」用空串表示，
            // 写成 "1" 反而会被当成数字。
            if (text.Length == 0)
            {
                return string.Empty;
            }

            if (text.IndexOf(',') >= 0)
            {
                Vector2 vector;
                if (TryParseVector(text, out vector))
                {
                    return vector;
                }
            }

            float number;
            if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                return number;
            }

            if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(text, "False", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return text;
        }

        /// <summary>按给定的目标类型还原。</summary>
        private static object DecodeAs(string text, Type type)
        {
            if (type == typeof(string))
            {
                return text ?? string.Empty;
            }

            if (text == null)
            {
                return null;
            }

            try
            {
                if (type == typeof(int))
                {
                    int i;
                    return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out i) ? (object)i : null;
                }
                if (type == typeof(float))
                {
                    float f;
                    return float.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out f) ? (object)f : null;
                }
                if (type == typeof(double))
                {
                    double d;
                    return double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d) ? (object)d : null;
                }
                if (type == typeof(bool))
                {
                    return text == "1" || string.Equals(text, "True", StringComparison.OrdinalIgnoreCase);
                }
                if (type == typeof(Vector2))
                {
                    Vector2 v;
                    return TryParseVector(text, out v) ? (object)v : null;
                }
                if (type.IsEnum)
                {
                    // 枚举按名字还原。它跨机器比按序号稳——序号会随
                    // 游戏更新重排，名字不会。
                    try
                    {
                        return Enum.Parse(type, text, true);
                    }
                    catch
                    {
                        int index;
                        if (int.TryParse(text, out index) && Enum.IsDefined(type, index))
                        {
                            return Enum.ToObject(type, index);
                        }
                        return null;
                    }
                }
            }
            catch
            {
                // 还原失败就当作写不进去，交给上层忽略这一项。
            }

            return null;
        }

        private static bool TryParseVector(string text, out Vector2 value)
        {
            value = Vector2.zero;
            string[] parts = text.Split(',');
            if (parts.Length != 2)
            {
                return false;
            }

            float x;
            float y;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x)
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y))
            {
                return false;
            }

            value = new Vector2(x, y);
            return true;
        }
    }
}
