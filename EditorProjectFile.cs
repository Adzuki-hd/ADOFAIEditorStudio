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
        internal const int CurrentFormatVersion = 2;

        /// <summary>工程文件的扩展名。</summary>
        internal const string FileExtension = ".aesprj";

        /// <summary>工程目录名的后缀：`<谱面名>_AES`。</summary>
        private const string DirectorySuffix = "_AES";

        /// <summary>
        /// 一个时间轴模块（分组）。
        ///
        /// 用户用钢笔工具在时间轴上点出一圈多边形，落在圈里的块就归到这个模块。
        /// 圈本身**只是用来定义归属**：一旦生成，成员表就是准的；多边形的顶点
        /// 还能继续拖动改形状，那时会按新形状重新算一次成员。
        ///
        /// 坐标是**时间轴内容坐标**（时间和行空间像素），不是屏幕坐标 ——
        /// 存屏幕坐标的话，滚动或缩放之后形状就和块对不上了。这一条和框选
        /// 那边踩过的坑一样（见框选锚点那段注释）。
        /// </summary>
        internal sealed class ProjectModule
        {
            /// <summary>模块名。空表示没自定义，界面上显示「模块 N」。</summary>
            public string Name = "";

            /// <summary>
            /// 多边形顶点，按**时间轴内容坐标**存：X = 时间（秒），Y = 行空间像素。
            /// 至少三个点才构成一个区域。
            /// </summary>
            public List<float> PointsX = new List<float>();
            public List<float> PointsY = new List<float>();

            /// <summary>折叠后只留一行名字，里面的块不画。</summary>
            public bool Collapsed;

            /// <summary>
            /// **旧格式遗留字段，不再写入。**
            ///
            /// 以前成员是用「事件身份」存的，而身份里带着起点时间和参数哈希 ——
            /// 拖一下、改个参数，整批键就失效，成员表和实际几何开始漂移。
            /// 为此在十几个调用点各写了一套"查不到就按几何兜底"，
            /// 兜底之间互不知情，bug 一个接一个（第二次拖不动、拖出拖回丢成员、
            /// 拖一个模块别的模块块全冒出来……）。
            ///
            /// 现在**几何是唯一真相**：模块的归属完全由「多边形 + 块矩形」决定，
            /// 每帧按需重算（`EnsureModuleMembersRebuilt`）。这个字段只为能读
            /// 旧文件而保留，读出来会被忽略。
            /// </summary>
            public List<string> Members = new List<string>();
        }

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

            /// <summary>
            /// 时间轴模块。老工程文件里没有这个字段，读出来是 null ——
            /// 下面 TryLoad 里补成空表，和「一个模块都没有」等价。
            /// </summary>
            public List<ProjectModule> Modules = new List<ProjectModule>();
        }

        /// <summary>
        /// 谱面 → 工程目录：`<谱面目录>/<谱面名>_AES/`。
        ///
        /// **目录名不开放自定义。** 跟谱面绑死才不会出现「工程被放到别处、
        /// 下次打开找不回来」的问题。能自定义的只有工程文件名。
        /// </summary>
        internal static string GetProjectDirectory(string chartPath)
        {
            // **空路径要先挡掉。**
            //
            // `Path.GetDirectoryName("")` 在 .NET Framework 4.8 上会抛
            // `ArgumentException`（不是返回 null）—— 而"谱面路径为空"是
            // **正常情况**：场景切换的中间态、还没打开谱面的时候都会拿到空串
            // （见 `EnsureBlockPlacements` 里那段关于 null 路径的说明）。
            // 由测试 `TestGetProjectDirectory` 抓到的。
            if (string.IsNullOrEmpty(chartPath))
            {
                return Path.Combine(".", "level" + DirectorySuffix);
            }

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
                if (loaded.Modules == null)
                {
                    // 老工程文件没有这个字段 —— 当成「没有模块」，不要当成读取失败。
                    loaded.Modules = new List<ProjectModule>();
                }
                // 逐个剔掉形态不对的模块（顶点少于三个、成员表是 null），
                // 免得界面上画一个空壳出来。
                for (int i = loaded.Modules.Count - 1; i >= 0; i--)
                {
                    ProjectModule module = loaded.Modules[i];
                    if (module == null
                        || module.PointsX == null || module.PointsY == null
                        || module.PointsX.Count < 3
                        || module.PointsX.Count != module.PointsY.Count)
                    {
                        loaded.Modules.RemoveAt(i);
                        continue;
                    }
                    if (module.Members == null)
                    {
                        module.Members = new List<string>();
                    }
                    // **旧文件里的成员表直接丢掉。** 现在是几何说了算，
                    // 留着它只会和实际位置打架（见 `ProjectModule.Members` 的说明）。
                    module.Members.Clear();
                    if (module.Name == null)
                    {
                        module.Name = "";
                    }
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
