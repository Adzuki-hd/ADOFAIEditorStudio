using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// AES 工程文件（`.aesprj`）—— 存「事件 → 时间轴行号」这一张表。
    ///
    /// **为什么要单独一个文件，还要放进子目录：**
    /// 谱面（`.adofai`）是玩家的东西，AES 一个字节都不该往里塞。工程放进
    /// 独立的子目录之后，玩家分享谱面时只拖谱面和媒体、整个目录自然落不下，
    /// 不会把 AES 的痕迹带给别人。
    ///
    /// **为什么这里可以用标准 JSON：**
    /// 「绝不 JSON 往返 `.adofai`」那条规矩是针对谱面的 —— 它用游戏自己写的
    /// 解析器，标准库往返会改坏数字格式（`3.201651E-06`）、属性顺序和尾逗号。
    /// 工程文件是我们自己写的、也只由我们自己读，标准 JSON 没有任何问题。
    ///
    /// **表里存的是什么：**
    /// 键 = 事件身份（类型 + 格 + 同类型同格里的序号），值 = 它坐在时间轴第几行。
    /// 身份不包含任何参数值，所以在 AES 里改透明度、颜色、位置都不会让布局丢。
    /// </summary>
    internal static class EditorProjectFile
    {
        /// <summary>文件格式版本。将来改结构时用它做兼容分支。</summary>
        internal const int CurrentFormatVersion = 1;

        /// <summary>工程文件的扩展名。</summary>
        internal const string FileExtension = ".aesprj";

        /// <summary>工程目录名的后缀：`<谱面名>_AES`。</summary>
        private const string DirectorySuffix = "_AES";

        /// <summary>工程文件的内容。</summary>
        internal sealed class Project
        {
            /// <summary>格式版本。</summary>
            public int Version = CurrentFormatVersion;

            /// <summary>
            /// 这份工程对应的谱面文件名。**只作核对，不参与还原** ——
            /// 目录已经跟谱面绑死了，这个字段是给人看的。
            /// </summary>
            public string Chart = "";

            /// <summary>事件身份 → 时间轴行号。</summary>
            public Dictionary<string, int> Rows = new Dictionary<string, int>();
        }

        /// <summary>
        /// 谱面 → 工程目录：`<谱面目录>/<谱面名>_AES/`。
        ///
        /// **目录名不开放自定义。** 跟谱面绑死才不会出现「工程被放到别处、
        /// 下次打开找不回来」的问题。能自定义的只有工程文件名。
        /// </summary>
        internal static string GetProjectDirectory(string chartPath)
        {
            string directory = Path.GetDirectoryName(chartPath);
            string name = Path.GetFileNameWithoutExtension(chartPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = ".";
            }
            if (string.IsNullOrEmpty(name))
            {
                name = "level";
            }
            return Path.Combine(directory, name + DirectorySuffix);
        }

        /// <summary>
        /// 在工程目录里找工程文件。
        ///
        /// **不按固定文件名找** —— 工程名是可改的，文件名跟着工程名走，
        /// 所以扫目录里第一个 `.aesprj`。一个谱面只有一个工程，够用了。
        /// </summary>
        internal static string FindProjectFile(string chartPath)
        {
            try
            {
                string directory = GetProjectDirectory(chartPath);
                if (!Directory.Exists(directory))
                {
                    return null;
                }
                string[] files = Directory.GetFiles(directory, "*" + FileExtension);
                return files.Length > 0 ? files[0] : null;
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[工程] 扫工程目录失败：" + exception.Message);
                return null;
            }
        }

        /// <summary>
        /// 把 `_aesId` 写回 `.adofai` —— **文本层面插字段，不做 JSON 往返**。
        ///
        /// **只补缺的，不覆盖已有的值。** 已经有 ID 说明是我们写过的，覆盖它没有
        /// 意义；而"整份重写"的风险大得多（数字格式、尾逗号、属性顺序都会变）。
        ///
        /// **要从后往前插**：每插一次后面的字符都会后移，从前面开始做的话，
        /// 先前算好的位置全部失效。
        ///
        /// 返回空串表示成功，否则是失败原因。
        /// </summary>
        internal static string InjectEventIds(string chartPath, IList<int> ids)
        {
            if (string.IsNullOrEmpty(chartPath) || !File.Exists(chartPath))
            {
                return "谱面路径无效";
            }
            if (ids == null || ids.Count == 0)
            {
                return "";
            }
            try
            {
                string text = File.ReadAllText(chartPath, Encoding.UTF8);
                // **两个数组都要写，顺序和 `ReadEventIds` 一致**（先 actions
                // 后 decorations）—— 不然 ID 会插到错的事件上。
                List<int> starts = FindObjectStartsInArray(text, "actions");
                int firstCount = starts.Count;
                starts.AddRange(FindObjectStartsInArray(text, "decorations"));
                if (starts.Count != ids.Count)
                {
                    // 数对不上就别动 —— 说明扫到的对象和实际事件不是一回事，
                    // 硬插会插到别的对象里去。
                    return "事件数对不上（谱面 " + starts.Count + " / ID " + ids.Count
                        + "；actions " + firstCount + " + decorations "
                        + (starts.Count - firstCount) + "）";
                }

                var builder = new StringBuilder(text);
                for (int i = starts.Count - 1; i >= 0; i--)
                {
                    int brace = starts[i];
                    int end = FindObjectEnd(text, brace);
                    if (end < 0 || ReadIdInObject(text, brace, end) > 0)
                    {
                        continue;   // 已经有号了，跳过
                    }
                    builder.Insert(brace + 1, "\"_aesId\":" + ids[i] + ",");
                }

                // **动玩家文件之前先留一份。** 这个方法会改写谱面原文，
                // 万一插错位置，至少还能捞回来。备份是覆盖式的（只有一份），
                // 不跟着每次打开堆一堆 —— 它只是"最近一次改动前"的样子。
                try
                {
                    File.Copy(chartPath, chartPath + ".aesbak", true);
                }
                catch
                {
                    // 备份失败不拦写入：多半是只读或者被占用，那种情况下
                    // 后面的 WriteAllText 也会失败。
                }

                File.WriteAllText(chartPath, builder.ToString(), new UTF8Encoding(false));
                return "";
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[工程] 写谱面事件 ID 失败：" + exception.Message);
                return exception.Message;
            }
        }

        /// <summary>找出某个 JSON 数组里每个顶层对象开头那个 `{` 的位置。</summary>
        private static List<int> FindObjectStartsInArray(string text, string arrayKey)
        {
            var starts = new List<int>();
            int index = FindJsonKey(text, arrayKey);
            if (index < 0)
            {
                return starts;
            }
            while (index < text.Length && text[index] != '[')
            {
                index++;
            }
            if (index >= text.Length)
            {
                return starts;
            }

            int depth = 0;
            bool inString = false;
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    // **从 `[` 开始扫时 depth 是 0，第一个 `{` 就是在 0 的时候进来的**
                    // —— 判 `depth == 1` 会把它漏掉。
                    if (depth == 0)
                    {
                        starts.Add(i);
                    }
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
                else if (c == ']' && depth == 0)
                {
                    break;   // 数组结束是 `]`，不是 `}`，别认错
                }
            }
            return starts;
        }

        /// <summary>从 `{` 找到配对的 `}`，返回它的下标；找不到返回 -1。</summary>
        private static int FindObjectEnd(string text, int braceStart)
        {
            int depth = 0;
            bool inString = false;
            for (int i = braceStart; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// 从 `.adofai` 原文里**按顺序**取出 `actions` 数组里每个事件的 `_aesId`
        /// （没有这个键的记 0）。
        ///
        /// **为什么必须自己扫文本**：游戏的 `LevelEvent` 解析时只保留它认识的键，
        /// `_aesId` 会被直接丢掉 —— 从游戏那边根本读不回来，只能看原文。
        /// 这个函数**只读、不改写**任何字节，所以和「绝不 JSON 往返」不冲突。
        ///
        /// **顺序为什么可信**：这是**文件里的顺序**（`editor.events` 也是照它灌的），
        /// 而撤销/重做改的是内存、文件不动 —— 这正是前面几个身份方案
        /// （文件序号 / 时间 / 时间位次）缺的那个「稳」。
        /// </summary>
        internal static List<int> ReadEventIds(string chartPath)
        {
            var ids = new List<int>();
            if (string.IsNullOrEmpty(chartPath) || !File.Exists(chartPath))
            {
                return ids;
            }
            try
            {
                string text = File.ReadAllText(chartPath, Encoding.UTF8);
                // **两个数组都要读，而且顺序是先 actions 后 decorations。**
                //
                // `SourceOrder` 是按「先 `editor.events`、再 `editor.decorations`」
                // 连续编号的（见 `CollectTimelineItems`），所以这张表必须照同一个
                // 顺序拼 —— 只读 actions 的话，装饰物那一段下标就全越界了。
                ids.AddRange(ReadIdsInArray(text, "actions"));
                ids.AddRange(ReadIdsInArray(text, "decorations"));
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[工程] 读谱面事件 ID 失败：" + exception.Message);
            }
            return ids;
        }

        /// <summary>读某一个 JSON 数组里每个顶层对象的 `_aesId`（没有记 0）。</summary>
        private static List<int> ReadIdsInArray(string text, string arrayKey)
        {
            var ids = new List<int>();
            int index = FindJsonKey(text, arrayKey);
            if (index < 0)
            {
                return ids;
            }
            while (index < text.Length && text[index] != '[')
            {
                index++;
            }
            if (index >= text.Length)
            {
                return ids;
            }

            int depth = 0;
            int objectStart = -1;
            bool inString = false;
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    // 字符串里只认转义和收尾，其余一律跳过 ——
                    // 不然值里出现的 `{` `}` 会把深度算错。
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    // **从 `[` 起扫时 depth 是 0，第一个 `{` 就是在 0 的时候进来的。**
                    if (depth == 0)
                    {
                        objectStart = i;
                    }
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        ids.Add(ReadIdInObject(text, objectStart, i));
                        objectStart = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;   // 数组结束是 `]`，不是 `}`
                }
            }
            return ids;
        }

        /// <summary>在 `[start, end)` 这段对象文本里找 `_aesId`，没有返回 0。</summary>
        private static int ReadIdInObject(string text, int start, int end)
        {
            const string needle = "\"_aesId\"";
            int at = text.IndexOf(needle, start, StringComparison.Ordinal);
            if (at < 0 || at >= end)
            {
                return 0;
            }
            at += needle.Length;
            while (at < end && (text[at] == ' ' || text[at] == ':' || text[at] == '\t'
                || text[at] == '\r' || text[at] == '\n'))
            {
                at++;
            }
            int valueStart = at;
            while (at < end && text[at] >= '0' && text[at] <= '9')
            {
                at++;
            }
            if (at == valueStart)
            {
                return 0;
            }
            int value;
            return int.TryParse(text.Substring(valueStart, at - valueStart), out value)
                ? value : 0;
        }

        /// <summary>找 `"键"` 后跟冒号的位置，找不到返回 -1。</summary>
        private static int FindJsonKey(string text, string key)
        {
            string needle = "\"" + key + "\"";
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                int after = at + needle.Length;
                while (after < text.Length && char.IsWhiteSpace(text[after]))
                {
                    after++;
                }
                if (after < text.Length && text[after] == ':')
                {
                    return after;
                }
                at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            }
            return -1;
        }

        /// <summary>
        /// 读工程文件。读不到、或者内容不完整，一律返回 false ——
        /// 调用方按「这张谱还没有工程」处理，不让一个坏文件把界面卡住。
        /// </summary>
        internal static bool TryLoad(string path, out Project project)
        {
            project = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                Project loaded = JsonConvert.DeserializeObject<Project>(text);
                if (loaded == null)
                {
                    return false;
                }
                if (loaded.Rows == null)
                {
                    loaded.Rows = new Dictionary<string, int>();
                }
                project = loaded;
                return true;
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[工程] 读工程文件失败（按没有工程处理）：" + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// 写工程文件，目录不存在就建。
        /// 返回空串表示成功，否则是失败原因（给弹窗显示用）。
        /// </summary>
        internal static string Save(string path, Project project)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "工程路径为空";
            }
            if (project == null)
            {
                return "没有可写的内容";
            }
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                project.Version = CurrentFormatVersion;
                string text = JsonConvert.SerializeObject(project, Formatting.Indented);
                // 不带 BOM：这文件将来可能被别的工具读，BOM 只会添乱。
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return "";
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[工程] 写工程文件失败：" + exception.Message);
                return exception.Message;
            }
        }
    }
}
