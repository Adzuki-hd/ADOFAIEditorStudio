using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 工作台自己的撤销 / 重做历史。
    ///
    /// 它刻意不依赖 scnEditor.undoStates。那个栈靠不住：它会被人整个
    /// 替换掉（连同 SaveState 一起），快照被挪进替换方自己的私有静态列表，
    /// 而那份撤销只回放它自己 save-scope 窗口恰好记录到的改动 ——
    /// 在该窗口之外创建的装饰物，它根本撤不回来。
    ///
    /// 历史自己持有，无论有没有别的 mod 动过那个方法，撤销 / 重做
    /// 都正确，也不必去猜别人的内部实现。
    /// </summary>
    internal static class EditorCommandHistory
    {
        /// <summary>一次工作台编辑，能自己撤销并重新施加。</summary>
        private interface ICommand
        {
            string Description { get; }
            bool Undo();
            bool Redo();
        }

        // ---------------------------------------------------------------- 装饰物移动

        private sealed class DecorationMoveCommand : ICommand
        {
            internal scnEditor Editor;
            internal scrDecoration Decoration;
            internal LevelEvent Source;
            internal LevelEvent Keyframe;
            internal string OriginalTag;
            internal bool OriginalTagDisabled;
            internal string TaggedValue;
            internal string UniqueTag;
            internal HashSet<string> OriginalRuntimeTags;
            internal Vector2 StartPosition;
            internal Vector2 StartPivotOffset;
            internal int KeyframeIndex = -1;

            public string Description
            {
                get { return "装饰物拖动"; }
            }

            public bool Undo()
            {
                if (Editor == null || Editor.events == null || Decoration == null)
                {
                    return false;
                }

                int index = Keyframe == null ? -1 : FindReference(Editor.events, Keyframe);
                if (index >= 0)
                {
                    Editor.events.RemoveAt(index);
                }

                RestoreDecorationTags(scrDecorationManager.instance, this);
                Decoration.SetPosition(StartPosition, StartPivotOffset);
                return true;
            }

            public bool Redo()
            {
                if (Editor == null || Editor.events == null || Keyframe == null || Decoration == null)
                {
                    return false;
                }

                // 只有关键帧事件归工作台所有；装饰物的 tag 被改成了
                // 一个唯一值，好让关键帧只绑定到这一个装饰物。
                int insertAt = KeyframeIndex < 0 || KeyframeIndex > Editor.events.Count
                    ? Editor.events.Count
                    : KeyframeIndex;
                Editor.events.Insert(insertAt, Keyframe);
                ApplyUniqueDecorationTag(Decoration, UniqueTag);
                return true;
            }
        }

        // ------------------------------------------------------- 装饰物创建 / 删除

        /// <summary>
        /// 由媒体拖放添加的装饰物。撤销把它删掉；重做把它放回列表中的
        /// 原位置，这样反复撤销 / 重做不会打乱谱面。
        /// </summary>
        private sealed class CreateDecorationCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Decoration;
            internal int Index = -1;

            public string Description
            {
                get { return "新建装饰物"; }
            }

            public bool Undo()
            {
                return RemoveDecorationNow(Editor, Decoration);
            }

            public bool Redo()
            {
                return ReinsertDecoration(Editor, Decoration, Index);
            }
        }

        private sealed class RemoveDecorationCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Decoration;
            internal int Index = -1;

            public string Description
            {
                get { return "删除装饰物"; }
            }

            public bool Undo()
            {
                return ReinsertDecoration(Editor, Decoration, Index);
            }

            public bool Redo()
            {
                return RemoveDecorationNow(Editor, Decoration);
            }
        }

        /// <summary>
        /// 把装饰物放回列表中精确的位置。
        ///
        /// scnEditor.AddDecoration(dec, index) 实际插到 index + 1，并把 -1 当作“追加”，
        /// 所以它表达不了位置 0，还会把其余元素整体挪一位。关卡数据和
        /// manager 在这里改为直接更新，其余都照 AddDecoration 的做法，
        /// 只是插入点不同。
        /// </summary>
        private static bool ReinsertDecoration(scnEditor editor, LevelEvent decoration, int index)
        {
            if (editor == null || decoration == null || editor.decorations == null)
            {
                return false;
            }

            if (ContainsDecoration(editor, decoration))
            {
                return false;
            }

            try
            {
                int at = index < 0 || index > editor.decorations.Count
                    ? editor.decorations.Count
                    : index;
                editor.decorations.Insert(at, decoration);

                scrDecorationManager manager = scrDecorationManager.instance;
                if (manager != null)
                {
                    bool spritesLoaded;
                    manager.CreateDecoration(decoration, out spritesLoaded, at - 1);
                }
            }
            catch (Exception exception)
            {
                ReportFailure("redo", decoration.eventType.ToString(), exception);
                return false;
            }

            return ContainsDecoration(editor, decoration);
        }

        // ---------------------------------------------------------------- 属性编辑

        /// <summary>
        /// 在参数面板里输入的一个值。
        ///
        /// 撤销恢复旧值、重做重新施加新值，两者都走面板自己
        /// 用的同一条刷新路径，所以实时对象和预览会跟着变。
        /// 记在工作台自己的栈上，而不是交给 scnEditor.SaveState ——
        /// 那个方法可能已被替换，替换后的撤销只回放它自己 save-scope
        /// 窗口捕获到的改动，而直接写字段这种操作不在其中。
        /// </summary>
        private sealed class PropertyEditCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Target;
            internal string Key;
            internal object OldValue;
            internal object NewValue;
            internal bool OldDisabled;
            internal bool NewDisabled;

            public string Description
            {
                get { return "修改 " + Key; }
            }

            public bool Undo()
            {
                return Apply(OldValue, OldDisabled);
            }

            public bool Redo()
            {
                return Apply(NewValue, NewDisabled);
            }

            private bool Apply(object value, bool disabled)
            {
                if (Editor == null || Target == null || string.IsNullOrEmpty(Key))
                {
                    return false;
                }

                try
                {
                    Target[Key] = value;
                    if (Target.disabled != null && Target.disabled.ContainsKey(Key))
                    {
                        Target.disabled[Key] = disabled;
                    }

                    // 与面板编辑后执行的刷新相同：重跑 Setup，让实时
                    // 装饰物读到该值，再重建，让预览据此重新播种基线。
                    if (Target.info != null && Target.info.isDecoration)
                    {
                        Editor.UpdateDecorationObject(Target);
                    }
                    EditorPlaybackBridge.RequestEvaluatorRebuild("command-history");
                }
                catch (Exception exception)
                {
                    ReportFailure("apply", Description, exception);
                    return false;
                }

                return true;
            }
        }

        // ---------------------------------------------------------------- 栈

        private static readonly List<ICommand> undoStack = new List<ICommand>();
        private static readonly List<ICommand> redoStack = new List<ICommand>();

        // 编辑器自己的栈深度是 100；取同样的深度，长时间会话下内存有上限。
        private const int MaxHistory = 100;

        internal static bool CanUndo
        {
            get { return undoStack.Count > 0; }
        }

        internal static bool CanRedo
        {
            get { return redoStack.Count > 0; }
        }

        internal static void Clear()
        {
            undoStack.Clear();
            redoStack.Clear();
        }

        /// <summary>
        /// 记录一次已完成的编辑。任何重做分支都会被丢弃，这和所有编辑器的
        /// 行为一致：撤销之后再编辑，被撤销的那条未来就没了。
        /// </summary>
        private static void Record(ICommand command)
        {
            if (command == null)
            {
                return;
            }

            undoStack.Add(command);
            redoStack.Clear();
            if (undoStack.Count > MaxHistory)
            {
                undoStack.RemoveAt(0);
            }
        }

        internal static void RecordDecorationCreate(scnEditor editor, LevelEvent decoration, int index)
        {
            Record(new CreateDecorationCommand
            {
                Editor = editor,
                Decoration = decoration,
                Index = index
            });
        }

        internal static void RecordDecorationRemove(scnEditor editor, LevelEvent decoration, int index)
        {
            Record(new RemoveDecorationCommand
            {
                Editor = editor,
                Decoration = decoration,
                Index = index
            });
        }

        internal static void RecordPropertyEdit(
            scnEditor editor,
            LevelEvent target,
            string key,
            object oldValue,
            object newValue,
            bool oldDisabled,
            bool newDisabled)
        {
            // 一次什么都没改的提交 —— 点进输入框又点出去，或重新输入
            // 同一个数值 —— 不能占用一步撤销。
            if (target == null
                || string.IsNullOrEmpty(key)
                || (Equals(oldValue, newValue) && oldDisabled == newDisabled))
            {
                return;
            }

            Record(new PropertyEditCommand
            {
                Editor = editor,
                Target = target,
                Key = key,
                OldValue = oldValue,
                NewValue = newValue,
                OldDisabled = oldDisabled,
                NewDisabled = newDisabled
            });
        }

        internal static bool TryUndo(out string status)
        {
            status = "没有可撤回的工作台操作";
            if (undoStack.Count == 0)
            {
                return false;
            }

            ICommand command = undoStack[undoStack.Count - 1];
            try
            {
                if (!command.Undo())
                {
                    status = "撤回失败：" + command.Description;
                    return false;
                }
            }
            catch (Exception exception)
            {
                ReportFailure("undo", command.Description, exception);
                status = "撤回失败，详情见 Mod 日志";
                return false;
            }

            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(command);
            status = "已撤回" + command.Description;
            return true;
        }

        internal static bool TryRedo(out string status)
        {
            status = "没有可重做的工作台操作";
            if (redoStack.Count == 0)
            {
                return false;
            }

            ICommand command = redoStack[redoStack.Count - 1];
            try
            {
                if (!command.Redo())
                {
                    status = "重做失败：" + command.Description;
                    return false;
                }
            }
            catch (Exception exception)
            {
                ReportFailure("redo", command.Description, exception);
                status = "重做失败，详情见 Mod 日志";
                return false;
            }

            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(command);
            status = "已重做" + command.Description;
            return true;
        }

        private static void ReportFailure(string action, string description, Exception exception)
        {
            if (Main.Logger != null)
            {
                Main.Logger.Error("Workbench " + action + " failed for '" + description + "': "
                    + exception.GetType().Name + " - " + exception.Message);
            }
        }

        // ---------------------------------------------------------------- 共用工具

        /// <summary>
        /// 从关卡数据和 manager 里删掉一个装饰物，不走批量刷新。
        ///
        /// 通常这件事由 scnEditor.RemoveEvent 做，但它的默认路径会调用
        /// UpdateDecorationObjects，而后者开头就是 decManager.ClearDecorations()，会把
        /// 每个装饰物都重建一遍 —— 摧毁预览求值器仍然持有
        /// 引用的那些对象。这里只拆掉这一个。
        /// </summary>
        private static bool RemoveDecorationNow(scnEditor editor, LevelEvent decoration)
        {
            if (editor == null || decoration == null || editor.decorations == null)
            {
                return false;
            }

            scrDecorationManager manager = scrDecorationManager.instance;
            scrDecoration doomed = FindDecorationFor(decoration);

            editor.RemoveEvent(decoration, skipDecorationUpdate: true);

            if (manager != null && manager.allDecorations != null && doomed != null)
            {
                manager.allDecorations.Remove(doomed);
                if (manager.taggedDecorations != null && doomed.tags != null)
                {
                    foreach (string tag in doomed.tags)
                    {
                        List<scrDecoration> bucket;
                        if (manager.taggedDecorations.TryGetValue(tag, out bucket) && bucket != null)
                        {
                            bucket.Remove(doomed);
                        }
                    }
                }
                if (doomed.gameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(doomed.gameObject);
                }
            }

            return true;
        }

        private static scrDecoration FindDecorationFor(LevelEvent source)
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || manager.allDecorations == null || source == null)
            {
                return null;
            }

            for (int i = 0; i < manager.allDecorations.Count; i++)
            {
                scrDecoration decoration = manager.allDecorations[i];
                if (decoration != null && ReferenceEquals(decoration.sourceLevelEvent, source))
                {
                    return decoration;
                }
            }

            return null;
        }

        private static bool ContainsDecoration(scnEditor editor, LevelEvent decoration)
        {
            if (editor == null || editor.decorations == null || decoration == null)
            {
                return false;
            }

            return editor.decorations.Contains(decoration);
        }

        private static int FindReference(List<LevelEvent> events, LevelEvent target)
        {
            if (events == null)
            {
                return -1;
            }

            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(events[i], target))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void RestoreDecorationTags(scrDecorationManager manager, DecorationMoveCommand command)
        {
            command.Source["tag"] = command.OriginalTag ?? string.Empty;
            if (command.Source.disabled.ContainsKey("tag"))
            {
                command.Source.disabled["tag"] = command.OriginalTagDisabled;
            }

            ReplaceRuntimeTags(manager, command.Decoration, command.OriginalRuntimeTags);
        }

        /// <summary>
        /// 给装饰物施加一个唯一 tag，并同步到运行时的 tag 集合，让
        /// 关键帧事件只绑定到这一个装饰物，而不是所有共享
        /// 谱面 tag 的装饰物。
        /// </summary>
        private static string ApplyUniqueDecorationTag(scrDecoration decoration, string uniqueTag)
        {
            if (decoration == null || decoration.sourceLevelEvent == null)
            {
                return uniqueTag;
            }

            decoration.sourceLevelEvent["tag"] = uniqueTag;
            ReplaceRuntimeTags(scrDecorationManager.instance, decoration, new[] { uniqueTag });
            return uniqueTag;
        }

        internal static void ReplaceRuntimeTags(
            scrDecorationManager manager,
            scrDecoration decoration,
            IEnumerable<string> desiredTags)
        {
            var desired = new HashSet<string>(desiredTags ?? new string[0]);
            var current = decoration.tags == null
                ? new HashSet<string>()
                : new HashSet<string>(decoration.tags);

            foreach (string tag in current)
            {
                if (desired.Contains(tag))
                {
                    continue;
                }

                List<scrDecoration> tagged;
                if (manager.taggedDecorations.TryGetValue(tag, out tagged) && tagged != null)
                {
                    tagged.RemoveAll(value => value == decoration);
                    if (tagged.Count == 0)
                    {
                        manager.taggedDecorations.Remove(tag);
                    }
                }
            }

            foreach (string tag in desired)
            {
                List<scrDecoration> tagged;
                if (!manager.taggedDecorations.TryGetValue(tag, out tagged) || tagged == null)
                {
                    tagged = new List<scrDecoration>();
                    manager.taggedDecorations[tag] = tagged;
                }
                if (!tagged.Contains(decoration))
                {
                    tagged.Add(decoration);
                }
            }

            if (decoration.tags == null)
            {
                decoration.tags = new HashSet<string>();
            }
            decoration.tags.Clear();
            decoration.tags.UnionWith(desired);
        }

        // ---------------------------------------------------------------- 旧入口

        /// <summary>
        /// 记录一次装饰物拖动。调用方已经插好了关键帧事件、
        /// 也重新打过装饰物的 tag，所以这里只采集撤销所需的状态。
        /// </summary>
        internal static void RecordDecorationMove(
            scnEditor editor,
            scrDecoration decoration,
            LevelEvent source,
            LevelEvent keyframe,
            string originalTag,
            bool originalTagDisabled,
            string taggedValue,
            string uniqueTag,
            HashSet<string> originalRuntimeTags,
            Vector2 startPosition,
            Vector2 startPivotOffset)
        {
            Record(new DecorationMoveCommand
            {
                Editor = editor,
                Decoration = decoration,
                Source = source,
                Keyframe = keyframe,
                OriginalTag = originalTag,
                OriginalTagDisabled = originalTagDisabled,
                TaggedValue = taggedValue,
                UniqueTag = uniqueTag,
                OriginalRuntimeTags = new HashSet<string>(originalRuntimeTags ?? new HashSet<string>()),
                StartPosition = startPosition,
                StartPivotOffset = startPivotOffset,
                KeyframeIndex = editor == null || editor.events == null
                    ? -1
                    : FindReference(editor.events, keyframe)
            });
        }
    }
}
