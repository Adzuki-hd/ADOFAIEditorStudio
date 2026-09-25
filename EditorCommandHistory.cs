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

                // 同上：集合变了就要重建快照缓存。
                EditorPlaybackBridge.RequestEvaluatorRebuild("decoration-insert");
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

        /// <summary>
        /// 一次改动同时写多个键。
        ///
        /// 时间轴上拖一个事件块会同时动两样东西：把块往前挪会改 `floor`
        /// 和 `angleOffset`（时间由这两者共同决定），拉长会改 `duration`。
        /// 这些必须算**一步**撤销——否则用户按一次 Ctrl+Z 只退回一半，
        /// 块落到一个从没出现过的中间状态上。
        /// </summary>
        private sealed class MultiPropertyEditCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Target;
            internal string[] Keys;
            internal object[] OldValues;
            internal object[] NewValues;
            internal bool[] OldDisabled;
            internal bool[] NewDisabled;

            public string Description
            {
                get { return Keys == null || Keys.Length == 0 ? "修改事件" : "修改 " + string.Join(" / ", Keys); }
            }

            public bool Undo()
            {
                return Apply(OldValues, OldDisabled);
            }

            public bool Redo()
            {
                return Apply(NewValues, NewDisabled);
            }

            private bool Apply(object[] values, bool[] disabled)
            {
                if (Editor == null || Target == null || Keys == null
                    || values == null || Keys.Length != values.Length)
                {
                    return false;
                }

                try
                {
                    for (int i = 0; i < Keys.Length; i++)
                    {
                        string key = Keys[i];
                        if (string.IsNullOrEmpty(key))
                        {
                            continue;
                        }

                        // floor 不是 data 字典里的键，它是 LevelEvent 的字段
                        // （见 EditorPropertyPanel.BuildFloor）。走索引器写它
                        // 会往 data 里塞一个没人读的副本，格子其实没动。
                        if (string.Equals(key, "floor", StringComparison.Ordinal))
                        {
                            Target.floor = Convert.ToInt32(values[i]);
                            continue;
                        }

                        Target[key] = values[i];
                        if (disabled != null && i < disabled.Length
                            && Target.disabled != null && Target.disabled.ContainsKey(key))
                        {
                            Target.disabled[key] = disabled[i];
                        }
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

            ReportToMultiplayer(command);
        }

        /// <summary>
        /// 把这次改动报给联机层。
        ///
        /// 放在 <see cref="Record"/> 里是因为**它是所有命令唯一的收口**——
        /// 五个 RecordXxx 入口都经过它，在这里接一次就全覆盖了。
        ///
        /// 分两类：
        ///
        ///   **值改动**（改属性、拖块）→ 报「哪个事件的哪个键变成了什么」。
        ///   联机按这个增量同步，几百字节的事。
        ///
        ///   **结构性改动**（新建/删除装饰物）→ 只报「结构变了」。
        ///   这类绝不能增量同步：事件没有 ID，全靠数组下标定位，
        ///   删掉一个后面全部位移，按旧下标去写就会改到别人的事件上。
        ///   所以接收方收到这个信号后走整体重同步。
        /// </summary>
        /// <summary>
        /// 把这次改动报给联机层。
        ///
        /// 放在 <see cref="Record"/> 里是因为**它是所有命令唯一的收口**——
        /// 五个 RecordXxx 入口都经过它，在这里接一次就全覆盖了。
        ///
        /// `undone` 表示这次是**撤回**而不是新编辑：撤回之后实际生效的是
        /// 旧值，要报旧值出去。不区分方向的话，撤回会把新值再发一遍，
        /// 对面看到的是「什么都没变」——撤回同步不了就是这么来的。
        ///
        /// 分两类：
        ///
        ///   **值改动**（改属性、拖块）→ 报「哪个事件的哪个键变成了什么」。
        ///   联机按这个增量同步，几百字节的事。
        ///
        ///   **结构性改动**（新建/删除装饰物、拖装饰物）→ 只报「结构变了」。
        ///   这类绝不能增量同步：事件没有 ID，全靠数组下标定位，
        ///   删掉一个后面全部位移，按旧下标去写就会改到别人的事件上。
        ///   所以走整份重推。
        /// </summary>
        private static void ReportToMultiplayer(ICommand command)
        {
            ReportToMultiplayer(command, false);
        }

        private static void ReportToMultiplayer(ICommand command, bool undone)
        {
            if (Multiplayer.MultiplayerEditSink.Suspended)
            {
                // 远端改动正在落地，它自己不能再报出去，否则会来回弹。
                return;
            }

            try
            {
                PropertyEditCommand single = command as PropertyEditCommand;
                if (single != null)
                {
                    Multiplayer.MultiplayerEditSink.Report(single.Editor, single.Target,
                        new[] { single.Key },
                        new[] { undone ? single.OldValue : single.NewValue },
                        new[] { undone ? single.OldDisabled : single.NewDisabled });
                    return;
                }

                MultiPropertyEditCommand multi = command as MultiPropertyEditCommand;
                if (multi != null)
                {
                    Multiplayer.MultiplayerEditSink.Report(multi.Editor, multi.Target,
                        multi.Keys,
                        undone ? multi.OldValues : multi.NewValues,
                        undone ? multi.OldDisabled : multi.NewDisabled);
                    return;
                }

                if (command is DecorationMoveCommand
                    || command is CreateDecorationCommand
                    || command is RemoveDecorationCommand)
                {
                    // **这三类都是结构性改动，不能按增量同步。**
                    //
                    // 事件没有 ID，全靠数组下标定位，而这三类都会往
                    // `editor.events` 里插入或删除事件——后面所有事件的下标
                    // 整体位移。如果按「旧下标 + 新值」发出去，对面会拿这个
                    // 下标去写**另一个事件**，改坏别人的数据，而且两边
                    // 都不会收到任何提示。
                    //
                    // 拖装饰物看起来像「改个位置」，实际是新建一个
                    // MoveDecorations 关键帧事件（见
                    // EditorSceneInteraction.BeginDecorationDrag），
                    // 所以它属于这一类，不是值改动。
                    //
                    // 撤回/重做同样走这里：它们也是删/建关键帧事件，
                    // 一样只能整份重推。所以不看 `undone`。
                    Multiplayer.MultiplayerSession.OnLocalStructuralChange();
                    return;
                }
            }
            catch (Exception exception)
            {
                // 联机上报出问题绝不能影响编辑本身。
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("[联机] 上报本地改动失败：" + exception.Message);
                }
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

        /// <summary>
        /// 删掉一个装饰物并记进撤销历史。
        ///
        /// **名字里带 Remove 而不是 Record，因为它会真的动手删。**
        ///
        /// 这里原先叫 `RecordDecorationRemove`，只往撤销栈里塞了一条命令
        /// 就完事——而命令的 `Redo()` 只在「重做」时才会被调用，于是
        /// 删除动作**从来没有真正执行过**：点了删除，历史里多一条记录，
        /// 装饰物还在原地。这是那一整套「先做后记」约定里唯一漏掉
        /// 执行的一处（对照 `editor.AddDecoration` 之后才 Record 的
        /// 新建路径）。
        ///
        /// 所以这里先执行、再记录，把约定补齐。调用方不需要自己做删除。
        ///
        /// **index 必须在删除之前取好**——删完后面的下标会整体前移，
        /// 那是撤销时要插回去的位置。
        /// </summary>
        internal static bool RemoveDecoration(scnEditor editor, LevelEvent decoration, int index)
        {
            if (editor == null || decoration == null)
            {
                return false;
            }

            if (!RemoveDecorationNow(editor, decoration))
            {
                return false;
            }

            Record(new RemoveDecorationCommand
            {
                Editor = editor,
                Decoration = decoration,
                Index = index
            });
            return true;
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

        /// <summary>
        /// 记录一次时间轴拖动。新旧值全部相同时不记录——用户按下去
        /// 又原样放开，不该占一步撤销。
        /// </summary>
        internal static void RecordBlockDrag(
            scnEditor editor,
            LevelEvent target,
            string[] keys,
            object[] oldValues,
            object[] newValues,
            bool[] oldDisabled,
            bool[] newDisabled)
        {
            if (target == null || keys == null || keys.Length == 0
                || oldValues == null || newValues == null
                || oldValues.Length != keys.Length || newValues.Length != keys.Length)
            {
                return;
            }

            bool changed = false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (!Equals(oldValues[i], newValues[i]))
                {
                    changed = true;
                    break;
                }
                if (oldDisabled != null && newDisabled != null
                    && i < oldDisabled.Length && i < newDisabled.Length
                    && oldDisabled[i] != newDisabled[i])
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
            {
                return;
            }

            Record(new MultiPropertyEditCommand
            {
                Editor = editor,
                Target = target,
                Keys = keys,
                OldValues = oldValues,
                NewValues = newValues,
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

            // **撤回也要同步出去。**
            //
            // 这里原本只动本地栈，联机完全不知道撤回了什么——所以撤回在
            // 对面看是「没反应」，而两台机器就此分叉。
            //
            // 报的是 `undone = true`：撤回之后生效的是**旧值**，
            // 拿新值去报等于把刚撤掉的东西又推回去。
            ReportToMultiplayer(command, true);
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

            // 重做就是重新施加新值，和当初那次编辑报的一样。
            ReportToMultiplayer(command, false);
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

            // 装饰物的集合变了，求值器的快照缓存得重建。
            //
            // 不重建的话它还握着刚被销毁的那个对象的引用，下一帧去碰
            // 就是 MissingReferenceException。这和 PropertyEditCommand
            // 改完值要请求重建是同一个道理。
            //
            // 放在这里而不是调用方，是因为删除有四条路径
            // （直接删、撤销新建、重做删除、以及将来可能的批量删），
            // 放在辅助函数里一次覆盖全。
            EditorPlaybackBridge.RequestEvaluatorRebuild("decoration-remove");

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
