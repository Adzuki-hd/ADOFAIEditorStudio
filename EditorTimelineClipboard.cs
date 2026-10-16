using System.Collections.Generic;
using ADOFAI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 时间轴事件的剪贴板。
    ///
    /// **存的是属性的副本，不是 `LevelEvent` 对象本身。** 粘贴要造出**新的**
    /// 事件：把同一个对象同时挂在两处，两边会互相改；而且撤销掉的旧对象被
    /// 粘到别处之后，就没法单独撤销那一边了。
    ///
    /// 只在工作台内部用，不走系统剪贴板 —— 跨进程要序列化，还得处理媒体引用，
    /// 而这里要的全部就是"同一张谱里复制一段再粘到别处"。
    /// </summary>
    internal static class EditorTimelineClipboard
    {
        internal sealed class Entry
        {
            internal LevelEventType Type;

            /// <summary>
            /// 相对被复制集合里**最小格号**的偏移。
            ///
            /// 粘贴时保留这个相对关系，所以复制相邻几格的一串事件再粘到别处，
            /// 它们之间的先后不会被压平。
            /// </summary>
            internal int FloorOffset;

            internal Dictionary<string, object> Data;
            internal Dictionary<string, bool> Disabled;
        }

        private static readonly List<Entry> entries = new List<Entry>();

        internal static int Count
        {
            get { return entries.Count; }
        }

        internal static bool HasContent
        {
            get { return entries.Count > 0; }
        }

        internal static void Clear()
        {
            entries.Clear();
        }

        /// <summary>
        /// 复制一批事件，返回实际收下的条数。
        ///
        /// 装饰物会被跳过：它们的增删走的是另一套（原生编辑器自己有一份
        /// `selectedDecorations`），混在一起粘出去会造出对不上的东西。
        /// </summary>
        internal static int Copy(IEnumerable<LevelEvent> source)
        {
            entries.Clear();
            if (source == null)
            {
                return 0;
            }

            List<LevelEvent> ordered = new List<LevelEvent>();
            foreach (LevelEvent levelEvent in source)
            {
                if (levelEvent != null && !levelEvent.IsDecoration)
                {
                    ordered.Add(levelEvent);
                }
            }

            if (ordered.Count == 0)
            {
                return 0;
            }

            int baseFloor = int.MaxValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].floor < baseFloor)
                {
                    baseFloor = ordered[i].floor;
                }
            }

            // 按格号排一遍：复制的是**集合**，顺序不该取决于用户先框到谁。
            ordered.Sort(delegate (LevelEvent left, LevelEvent right)
            {
                return left.floor.CompareTo(right.floor);
            });

            for (int i = 0; i < ordered.Count; i++)
            {
                LevelEvent levelEvent = ordered[i];
                Dictionary<string, object> data = levelEvent.GetData();
                entries.Add(new Entry
                {
                    Type = levelEvent.eventType,
                    FloorOffset = levelEvent.floor - baseFloor,
                    Data = data == null
                        ? new Dictionary<string, object>()
                        : new Dictionary<string, object>(data),
                    Disabled = levelEvent.disabled == null
                        ? new Dictionary<string, bool>()
                        : new Dictionary<string, bool>(levelEvent.disabled)
                });
            }

            return entries.Count;
        }

        /// <summary>
        /// 把剪贴板里的内容粘到 targetFloor（相对最小格号的那一格），
        /// 返回造出来的事件。
        ///
        /// 建事件的方式和原生 `scnEditor.AddEvent` 一致：
        /// `new LevelEvent(floor, type)` → 补属性 → `events.Add`。
        /// **原生也是直接 Add、不按格号排序**，所以这里照办 ——
        /// 自己排一遍反而会和原生存出来的顺序不一样。
        /// </summary>
        internal static List<LevelEvent> Paste(scnEditor editor, int targetFloor)
        {
            List<LevelEvent> created = new List<LevelEvent>();
            if (editor == null || editor.events == null || entries.Count == 0)
            {
                return created;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                LevelEvent levelEvent = new LevelEvent(targetFloor + entry.FloorOffset, entry.Type);

                foreach (KeyValuePair<string, object> pair in entry.Data)
                {
                    // floor 在构造时就定了，再按索引器写它只会往 data 里塞一个
                    // 没人读的副本（原生 ApplyEvent 走的是字段）。
                    if (string.Equals(pair.Key, "floor", System.StringComparison.Ordinal))
                    {
                        continue;
                    }
                    levelEvent[pair.Key] = pair.Value;
                }

                if (levelEvent.disabled != null)
                {
                    foreach (KeyValuePair<string, bool> pair in entry.Disabled)
                    {
                        if (levelEvent.disabled.ContainsKey(pair.Key))
                        {
                            levelEvent.disabled[pair.Key] = pair.Value;
                        }
                    }
                }

                editor.events.Add(levelEvent);
                created.Add(levelEvent);
            }

            if (created.Count > 0)
            {
                EditorPlaybackBridge.RequestEvaluatorRebuild("event-paste");
            }

            return created;
        }
    }
}
