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
