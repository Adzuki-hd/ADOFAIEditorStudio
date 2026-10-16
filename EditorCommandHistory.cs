using System;
using System.Collections.Generic;
using System.Reflection;
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
        /// 改一个事件的「生效 / 停用」（`LevelEvent.active` 那个字段）。
        ///
        /// **事件级**的开关，和属性级那个 `disabled` 字典不是一回事：那个只让
        /// 某一条属性不参与求值，这个让整个事件失效。原版编辑器把它放在自己的
        /// 事件清单上，工作台一直没入口 —— 于是"哪些事件被停用了"看得见、改不了。
        ///
        /// 走游戏自己的 `scnEditor.EnableEvent`，**不是直接赋 `active`**：
        /// 它顺手调 `ApplyEventsToFloors()`，少了那一步值改了、画面和播放都不跟。
        /// </summary>
        private sealed class EventActiveCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Target;
            internal bool From;
            internal bool To;

            public string Description
            {
                get { return To ? "启用事件" : "停用事件"; }
            }

            public bool Undo()
            {
                return Apply(From);
            }

            public bool Redo()
            {
                return Apply(To);
            }

            private bool Apply(bool active)
            {
                if (Editor == null || Target == null)
                {
                    return false;
                }
                try
                {
                    Editor.EnableEvent(Target, active);
                    EditorPlaybackBridge.RequestEvaluatorRebuild("event-active");
                    return true;
                }
                catch (Exception exception)
                {
                    ReportFailure("event-active", Description, exception);
                    return false;
                }
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

        /// <summary>当前批次攒了几条（排查拖动撤销用）。不在批次里时是 -1。</summary>
        internal static int BatchCount
        {
            get { return batch == null ? -1 : batch.Count; }
        }

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
        /// <summary>
        /// 批处理期间收下的命令。非空表示"正在攒一条复合记录"。
        /// 见 BeginBatch / EndBatch。
        /// </summary>
        private static List<ICommand> batch;
        private static string batchLabel;

        /// <summary>
        /// 把接下来的若干次 Record 攒成**一条**撤销记录。
        ///
        /// 用它的场景都是"用户做了一次操作，但底下记了好几条命令"：
        /// 拖一整批事件、给一批事件批量改参数。不攒的话撤销要按好几次，
        /// 而且中间几次是"只回退了一半"的状态，看着像坏了。
        ///
        /// **必须配对调用 EndBatch**；中途异常逃出去会让攒下的命令永远
        /// 不入栈（东西改了却撤不回来），所以调用方要放在 try/finally 里。
        /// </summary>
        internal static void BeginBatch(string label)
        {
            batch = new List<ICommand>();
            batchLabel = label;
        }

        /// <summary>收口：攒了多条就合成一条复合命令入栈，只有一条就按普通方式入栈。</summary>
        internal static void EndBatch()
        {
            List<ICommand> collected = batch;
            string label = batchLabel;
            batch = null;
            batchLabel = null;
            if (collected == null || collected.Count == 0)
            {
                return;
            }

            if (collected.Count == 1)
            {
                Push(collected[0]);
                return;
            }

            Push(new CompositeCommand
            {
                Commands = collected.ToArray(),
                Label = string.IsNullOrEmpty(label) ? collected.Count + " 项" : label
            });
        }

        private static void Push(ICommand command)
        {
            undoStack.Add(command);
            redoStack.Clear();
            if (undoStack.Count > MaxHistory)
            {
                undoStack.RemoveAt(0);
            }

            ReportToMultiplayer(command);

            // 告诉原生编辑器"谱面被动过了"，否则改完直接退出它不会提醒保存。
            MarkEditorUnsaved();
        }

        /// <summary>
        /// 置上原生编辑器那个"有未保存改动"的标志。
        ///
        /// 工作台把编辑记在自己的栈上（见文件头），**从不调 scnEditor.SaveState**：
        /// 那会往原生撤销栈里压一份 `levelData.Copy()` 深拷贝、还会清掉重做栈，
        /// 而那些快照恰恰是工作台不想要的。代价是原生的 `unsavedChanges` 一直是
        /// false —— 于是除 Ctrl+S 之外的一切改动都不算"改过"：标题栏不显示未保存
        /// 标记，退出也不弹提示，改动就这么没了。
        ///
        /// 这里只置那个标志，走它的 setter（set 里会顺带刷新标题栏文件名），
        /// 完全不碰撤销栈。原版 `scnEditor.SaveLevel()` 存成功后自己会把它清回
        /// false，所以不需要我们收尾。
        /// </summary>
        private static void MarkEditorUnsaved()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            if (!_unsavedProbeDone)
            {
                _unsavedProbeDone = true;
                _unsavedProperty = typeof(scnEditor).GetProperty(
                    "unsavedChanges",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (_unsavedProperty == null || !_unsavedProperty.CanWrite)
                {
                    Main.Logger.Error(
                        "[Unsaved] 找不到 scnEditor.unsavedChanges —— 退出前的保存提示会失效");
                }
            }

            if (_unsavedProperty == null || !_unsavedProperty.CanWrite)
            {
                return;
            }

            try
            {
                _unsavedProperty.SetValue(editor, true, null);
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[Unsaved] 置未保存标志失败：" + exception.Message);
            }
        }

        // 全限定：ADOFAI 里也有个 PropertyInfo（事件的属性类型），
        // 直接写 PropertyInfo 会撞名。
        private static System.Reflection.PropertyInfo _unsavedProperty;
        private static bool _unsavedProbeDone;

        // ------------------------------------------------------- 时间轴块移动

        /// <summary>
        /// 时间轴上把块往上/往下挪一行（纵向）。**整批一起**。
        ///
        /// **只管行号，不管横向。** 横向的还原另有其人 —— 被拖的那个由
        /// `CommitBlockDrag` 记、每个跟随者由 `CommitFollowerDrag` 各记一条
        /// （都是谱面 `floor`/`angleOffset` 的改动）。
        ///
        /// 纵向是**纯画布布局**、没有别人管它，所以整批都得记进来：
        /// 只记被拖的那一个的话，框选一堆拖完按 Ctrl+Z 只退回一个，
        /// 其余的留在原地 —— 和"能整批横向拖、纵向只有它自己动"是同一个缺口。
        ///
        /// 时间轴那边**一次拖动只在松手时记一条**，不是每帧一条：
        /// 每帧记的话拖一次鼠标就是上百条，按一次 Ctrl+Z 只退一行。
        /// </summary>
        private sealed class MoveTimelineBlockRowsCommand : ICommand
        {
            internal EditorTimelineView View;
            internal LevelEvent[] Targets;
            internal int[] FromRows;
            internal int[] ToRows;

            public string Description
            {
                get { return "移动事件块"; }
            }

            public bool Undo()
            {
                return Apply(FromRows);
            }

            public bool Redo()
            {
                return Apply(ToRows);
            }

            private bool Apply(int[] rows)
            {
                if (View == null || Targets == null || rows == null)
                {
                    return false;
                }
                for (int i = 0; i < Targets.Length && i < rows.Length; i++)
                {
                    View.MoveBlockRowByEvent(Targets[i], rows[i]);
                }
                return true;
            }
        }

        /// <summary>
        /// 记一次整批纵向移动。**一个都没动就什么都不记** ——
        /// 点一下没拖动也会走到这里。
        /// </summary>
        internal static void RecordTimelineBlockRows(EditorTimelineView view,
            LevelEvent[] targets, int[] fromRows, int[] toRows)
        {
            if (view == null || targets == null || targets.Length == 0
                || fromRows == null || toRows == null)
            {
                return;
            }
            Record(new MoveTimelineBlockRowsCommand
            {
                View = view,
                Targets = targets,
                FromRows = fromRows,
                ToRows = toRows
            });
        }

        /// <summary>
        /// 记一次"事件的生效 / 停用"。
        ///
        /// **调用方要先把值改好**（`scnEditor.EnableEvent`）再叫这里 ——
        /// `Record` 只入栈，不负责应用。值没变就什么都不记。
        /// </summary>
        internal static void RecordEventActive(scnEditor editor, LevelEvent target, bool from, bool to)
        {
            if (editor == null || target == null || from == to)
            {
                return;
            }
            Record(new EventActiveCommand
            {
                Editor = editor,
                Target = target,
                From = from,
                To = to
            });
        }

        private static void Record(ICommand command)
        {
            if (command == null)
            {
                return;
            }

            // 攒批期间先收着，等 EndBatch 统一入栈；但**联机上报照旧每条都发** ——
            // 对面收到的是逐条的实际改动，与这边怎么组织撤销栈无关。
            if (batch != null)
            {
                batch.Add(command);
                ReportToMultiplayer(command);
                return;
            }

            Push(command);
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
                    || command is RemoveDecorationCommand
                    // 事件的增删同样是结构性改动：事件没有稳定下标，
                    // 增量同步没法表达"多了一个 / 少了一个"。
                    // CompositeCommand 是它们的批量形式，一样处理。
                    || command is CreateEventCommand
                    || command is RemoveEventCommand
                    // 事件的生效/停用也是结构性的：增量同步那套是"给这个事件
                    // 的某个键写个值"，而 `active` 是 LevelEvent 的字段，
                    // 不在数据字典里，按增量发过去对面认不了。
                    || command is EventActiveCommand
                    || command is CompositeCommand)
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

        // ------------------------------------------------------- 事件创建 / 删除

        /// <summary>
        /// 一串命令当**一次**编辑用 —— 一条撤销记录。
        ///
        /// 删掉框选的十个事件如果记成十条，撤销要按十次；而且中间那几次
        /// 撤出来的是"删了一半"的谱面，看着像坏了。
        ///
        /// **撤销按倒序、重做按正序**，这不是随手写的：事件是**按下标插回**
        /// 的，而插回的顺序决定了位置对不对。见 RemoveEvents 里那段。
        /// </summary>
        private sealed class CompositeCommand : ICommand
        {
            internal ICommand[] Commands;
            internal string Label = "批量编辑";

            public string Description
            {
                get { return Label; }
            }

            public bool Undo()
            {
                bool ok = true;
                for (int i = Commands.Length - 1; i >= 0; i--)
                {
                    if (Commands[i] != null && !Commands[i].Undo())
                    {
                        ok = false;
                    }
                }
                return ok;
            }

            public bool Redo()
            {
                bool ok = true;
                for (int i = 0; i < Commands.Length; i++)
                {
                    if (Commands[i] != null && !Commands[i].Redo())
                    {
                        ok = false;
                    }
                }
                return ok;
            }
        }

        /// <summary>新建一个事件（粘贴走这条）。撤销把它删掉，重做放回原下标。</summary>
        private sealed class CreateEventCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Event;
            internal int Index = -1;

            public string Description
            {
                get { return "新建事件"; }
            }

            public bool Undo()
            {
                return RemoveEventNow(Editor, Event);
            }

            public bool Redo()
            {
                return ReinsertEvent(Editor, Event, Index);
            }
        }

        /// <summary>
        /// 删掉一个事件。**名字里带 Remove 而不是 Record，因为它真的会删** ——
        /// 和 RemoveDecoration 是同一条约定（先执行、再记录），调用方不要再删一次。
        /// </summary>
        private sealed class RemoveEventCommand : ICommand
        {
            internal scnEditor Editor;
            internal LevelEvent Event;
            internal int Index = -1;

            public string Description
            {
                get { return "删除事件"; }
            }

            public bool Undo()
            {
                return ReinsertEvent(Editor, Event, Index);
            }

            public bool Redo()
            {
                return RemoveEventNow(Editor, Event);
            }
        }

        internal static void RecordEventCreate(scnEditor editor, LevelEvent levelEvent, int index)
        {
            Record(new CreateEventCommand
            {
                Editor = editor,
                Event = levelEvent,
                Index = index
            });
        }

        /// <summary>
        /// 一批新事件记成**一条**撤销记录（粘贴走这条）。
        ///
        /// firstIndex 是这批里**第一个**在事件表里的下标；后面的都是依次追加的
        /// （原生 AddEvent 就是直接 Add），所以下标连续。
        /// 撤销按倒序删、重做按正序插回 —— 见 CompositeCommand。
        /// </summary>
        internal static void RecordEventCreates(scnEditor editor, IList<LevelEvent> created, int firstIndex)
        {
            if (editor == null || created == null || created.Count == 0)
            {
                return;
            }

            ICommand[] commands = new ICommand[created.Count];
            for (int i = 0; i < created.Count; i++)
            {
                commands[i] = new CreateEventCommand
                {
                    Editor = editor,
                    Event = created[i],
                    Index = firstIndex + i
                };
            }

            Record(new CompositeCommand
            {
                Commands = commands,
                Label = "粘贴 " + created.Count + " 个事件"
            });
        }

        /// <summary>
        /// 删掉一个事件并记进撤销历史。**会真的动手删。**
        ///
        /// index 必须在删除之前取好 —— 删完后面的下标整体前移，
        /// 那是撤销时要插回去的位置（和 RemoveDecoration 同理）。
        /// </summary>
        internal static bool RemoveEvent(scnEditor editor, LevelEvent levelEvent, int index)
        {
            if (editor == null || levelEvent == null)
            {
                return false;
            }

            if (!RemoveEventNow(editor, levelEvent))
            {
                return false;
            }

            Record(new RemoveEventCommand
            {
                Editor = editor,
                Event = levelEvent,
                Index = index
            });
            return true;
        }

        /// <summary>
        /// 一次删掉多个事件，记成**一条**撤销记录。
        ///
        /// **必须按"删除前的下标从大到小"删。** 先删小的会把后面的大下标
        /// 整体前移，再按原下标去删就删错了人 —— 而且不会报错，只会安静地
        /// 少删一个、多删一个。
        ///
        /// 记录里存的是**删除前**的原下标。撤销时 CompositeCommand 会倒序
        /// 执行，也就是**从小到大插回**：插回下标 1 之后，原来下标 2 的还在
        /// 下标 2，所以再插回 3 也是对的。反过来就会互相错位。
        /// </summary>
        internal static bool RemoveEvents(scnEditor editor, IList<LevelEvent> targets)
        {
            if (editor == null || editor.events == null || targets == null || targets.Count == 0)
            {
                return false;
            }

            List<int> indices = new List<int>();
            List<LevelEvent> events = new List<LevelEvent>();
            for (int i = 0; i < targets.Count; i++)
            {
                int index = FindReference(editor.events, targets[i]);
                if (index < 0)
                {
                    continue;
                }
                indices.Add(index);
                events.Add(targets[i]);
            }

            if (events.Count == 0)
            {
                return false;
            }

            List<int> order = new List<int>();
            for (int i = 0; i < indices.Count; i++)
            {
                order.Add(i);
            }
            order.Sort(delegate (int left, int right)
            {
                return indices[right].CompareTo(indices[left]);
            });

            List<ICommand> commands = new List<ICommand>();
            for (int i = 0; i < order.Count; i++)
            {
                int slot = order[i];
                if (RemoveEventNow(editor, events[slot]))
                {
                    commands.Add(new RemoveEventCommand
                    {
                        Editor = editor,
                        Event = events[slot],
                        Index = indices[slot]
                    });
                }
            }

            if (commands.Count == 0)
            {
                return false;
            }

            Record(new CompositeCommand
            {
                Commands = commands.ToArray(),
                Label = "删除 " + commands.Count + " 个事件"
            });
            return true;
        }

        /// <summary>
        /// 一次删掉一批**事件 + 装饰物**，记成**一条**撤销记录。
        ///
        /// 两个来源本来就分属两套：时间轴的多选是事件，画面里选中的
        /// （`scnEditor.selectedDecorations`）是装饰物。但用户按一次 Delete
        /// 就是一次操作，撤销也该按一次 —— 所以在这里合成一条命令，
        /// 而不是让两边各记各的（那样要按好几次才撤得回来，中间几次
        /// 还是"删了一半"的谱面）。
        ///
        /// 两个列表各自按**删除前的下标从大到小**删，理由见 RemoveEvents。
        /// 两者互不影响（不同的列表），所以谁先谁后无所谓。
        /// </summary>
        internal static bool RemoveSelection(scnEditor editor,
            IList<LevelEvent> events, IList<LevelEvent> decorations)
        {
            if (editor == null)
            {
                return false;
            }

            // **按"它实际在哪个列表里"归类，不按"谁把它选中的"。**
            //
            // 时间轴选中的目标里可能混着装饰物和**文本** —— 文本（`AddText` 建的）
            // 在游戏那边和装饰物同族，存在 `editor.decorations` 里。原来一律当
            // 普通事件去 `editor.events` 里找，找不到就静默跳过，表现就是
            // 「加了文本，Del 删不掉」：既不报错、也不删。
            List<LevelEvent> eventTargets = new List<LevelEvent>();
            List<LevelEvent> decorationTargets = new List<LevelEvent>();

            // ① 调用方直接点名的装饰物（原生那份选中里的）
            if (decorations != null)
            {
                for (int i = 0; i < decorations.Count; i++)
                {
                    LevelEvent target = decorations[i];
                    if (target != null && FindReference(decorationTargets, target) < 0)
                    {
                        decorationTargets.Add(target);
                    }
                }
            }

            // ② 事件目标：看它究竟躺在哪个列表里，再决定走哪条删除路径
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    LevelEvent target = events[i];
                    if (target == null || FindReference(decorationTargets, target) >= 0)
                    {
                        continue;
                    }
                    if (FindReference(editor.decorations, target) >= 0)
                    {
                        decorationTargets.Add(target);
                    }
                    else
                    {
                        eventTargets.Add(target);
                    }
                }
            }

            List<ICommand> commands = new List<ICommand>();
            CollectRemovals(editor, editor.events, eventTargets, commands, false);
            CollectRemovals(editor, editor.decorations, decorationTargets, commands, true);

            if (commands.Count == 0)
            {
                // **一条都没删成得说一声。** 静默失败最难查 ——「Del 删不掉」这种
                // 就是一路静默下来的。把目标实际在哪儿打出来，下次同类一眼可见。
                if (Main.Logger != null && eventTargets.Count + decorationTargets.Count > 0)
                {
                    Main.Logger.Log("[删除] 没删成：目标 "
                        + eventTargets.Count + " 个按事件找 / "
                        + decorationTargets.Count + " 个按装饰物找，都没在对应列表里");
                }
                return false;
            }

            Record(new CompositeCommand
            {
                Commands = commands.ToArray(),
                Label = "删除 " + commands.Count + " 项"
            });
            return true;
        }

        /// <summary>
        /// 把一个列表里要删的那些按"删除前下标从大到小"删掉，
        /// 并把对应的撤销命令追加进 commands。
        /// </summary>
        private static void CollectRemovals(scnEditor editor, List<LevelEvent> list,
            IList<LevelEvent> targets, List<ICommand> commands, bool isDecoration)
        {
            if (list == null || targets == null || targets.Count == 0)
            {
                return;
            }

            List<int> indices = new List<int>();
            List<LevelEvent> removable = new List<LevelEvent>();
            for (int i = 0; i < targets.Count; i++)
            {
                int index = FindReference(list, targets[i]);
                if (index < 0)
                {
                    continue;
                }
                indices.Add(index);
                removable.Add(targets[i]);
            }

            if (removable.Count == 0)
            {
                return;
            }

            List<int> order = new List<int>();
            for (int i = 0; i < indices.Count; i++)
            {
                order.Add(i);
            }
            order.Sort(delegate (int left, int right)
            {
                return indices[right].CompareTo(indices[left]);
            });

            for (int i = 0; i < order.Count; i++)
            {
                int slot = order[i];
                bool removed = isDecoration
                    ? RemoveDecorationNow(editor, removable[slot])
                    : RemoveEventNow(editor, removable[slot]);
                if (!removed)
                {
                    continue;
                }

                if (isDecoration)
                {
                    commands.Add(new RemoveDecorationCommand
                    {
                        Editor = editor,
                        Decoration = removable[slot],
                        Index = indices[slot]
                    });
                }
                else
                {
                    commands.Add(new RemoveEventCommand
                    {
                        Editor = editor,
                        Event = removable[slot],
                        Index = indices[slot]
                    });
                }
            }
        }

        private static bool RemoveEventNow(scnEditor editor, LevelEvent levelEvent)
        {
            if (editor == null || levelEvent == null || editor.events == null)
            {
                return false;
            }

            // 原生对**非装饰物**的删除就是 `events.Remove(evnt)`，不额外刷新
            // （只有装饰物那条分支要更新 gizmo、重建对象）。
            // 走 FindReference + RemoveAt 而不是 List.Remove：后者按 Equals
            // 找，万一 LevelEvent 重载过 Equals 就可能删错人。
            int index = FindReference(editor.events, levelEvent);
            if (index < 0)
            {
                return false;
            }

            editor.events.RemoveAt(index);
            EditorPlaybackBridge.RequestEvaluatorRebuild("event-remove");
            return true;
        }

        private static bool ReinsertEvent(scnEditor editor, LevelEvent levelEvent, int index)
        {
            if (editor == null || levelEvent == null || editor.events == null)
            {
                return false;
            }

            if (FindReference(editor.events, levelEvent) >= 0)
            {
                return false;
            }

            int at = index < 0 || index > editor.events.Count ? editor.events.Count : index;
            editor.events.Insert(at, levelEvent);
            EditorPlaybackBridge.RequestEvaluatorRebuild("event-insert");
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
