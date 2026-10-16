using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Multiplayer
{
    /// <summary>
    /// 谱面的读写、**跨机可比**的指纹，以及整体重同步时把收到的谱面落到磁盘。
    ///
    /// 两条贯穿始终的原则：
    ///
    /// **① 绝不自己 JSON 往返谱面。** 项目里那条硬规则在这里同样适用——
    /// 尾逗号、`3.201651E-06` 这种数字写法、属性顺序，都会在标准 JSON
    /// 库的往返里被改掉。所以需要「内存里的谱面文本」时，一律用
    /// **游戏自己的编码器** `levelData.Encode()`：那正是玩家按 Ctrl+S 时
    /// 游戏写文件用的同一个方法，格式天然就是对的。
    ///
    /// **② 也不自己写文件。** 走 `RDFile.WriteAllText`，和
    /// `scnEditor.SaveLevel` 完全一致，编码与 BOM 行为都跟着游戏走。
    /// </summary>
    internal static class ChartTransfer
    {
        /// <summary>当前关卡的绝对路径。没有打开关卡时为 null。</summary>
        internal static string CurrentLevelPath
        {
            get
            {
                try
                {
                    string path = ADOBase.levelPath;
                    return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
                }
                catch
                {
                    // 场景切换期间这个静态属性可能暂时取不到。
                    return null;
                }
            }
        }

        /// <summary>
        /// 会话副本。加入别人的房间时，**所有写入都落到它身上**，
        /// 房主那份原文件全程不动。
        ///
        /// 为什么要有它：加入者本来是在**同一张谱的同一份文件**上编辑
        /// （大家用同一个谱面目录结构）。那样一来，加入者的改动就直接
        /// 写进了他本地那份「房主的谱」——文件被污染了，而且没人知道
        /// 它现在是哪一版。
        ///
        /// **副本只能放在谱面同目录，不能放系统临时目录。**
        /// ADOFAI 的音频和装饰物贴图是按**谱面所在目录**去解析的
        /// （`Path.Combine(Path.GetDirectoryName(levelPath), ...)`），
        /// 把 .adofai 单独挪到 %TEMP% 会让预览没声音、装饰物贴图全丢。
        /// 所以这里是同目录下一个带标记的副本文件。
        ///
        /// 生命周期：加入时建、退出/断开时删。上次崩溃留下的残骸会在
        /// 下一次加入同一张谱时先清掉。
        /// </summary>
        private const string SessionSuffix = ".aes-session.adofai";

        /// <summary>加入房间期间为 true，写入目标改为会话副本。</summary>
        private static bool useSessionCopy;
        private static string sessionCopyPath;

        /// <summary>这份谱面对应的会话副本路径。</summary>
        private static string SessionPathFor(string levelPath)
        {
            if (string.IsNullOrEmpty(levelPath))
            {
                return null;
            }
            string withoutExtension = Path.ChangeExtension(levelPath, null);
            return withoutExtension + SessionSuffix;
        }

        /// <summary>
        /// 加入者**没有打开谱面**时会话副本放哪。
        ///
        /// 正常情况副本挨着用户的谱面放（媒体要跟着谱面目录走，见
        /// <see cref="MediaDirectory"/>）。但加入房间是允许空手进的
        /// ——那时没有「谱面目录」可言，就落在一个固定的会话目录里，
        /// 断开时整目录删掉。
        /// </summary>
        private static string DefaultSessionDirectory()
        {
            try
            {
                string root = Application.persistentDataPath;
                return string.IsNullOrEmpty(root)
                    ? null
                    : Path.Combine(root, "AESMultiplayerSession");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>当前是不是没有打开任何谱面。</summary>
        internal static bool HasOpenLevel
        {
            get { return CurrentLevelPath != null; }
        }

        /// <summary>会话副本所在的目录。没有会话副本时为 null。</summary>
        private static string SessionDirectory()
        {
            if (string.IsNullOrEmpty(sessionCopyPath))
            {
                return null;
            }
            try
            {
                return Path.GetDirectoryName(sessionCopyPath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 切到会话副本模式，并把副本**立刻**建出来。
        ///
        /// 立刻建是有意的：不建的话，用户加入之后到对方第一次推谱面
        /// 之间有一个空窗，这期间他按 Ctrl+S 会把改动写进房主那份
        /// 原文件——正是要避免的事。
        ///
        /// **允许空手加入**（没打开任何谱面）：那时副本落在固定的会话
        /// 目录里，断开时整目录删掉。之后收到的谱面会写进这个副本，
        /// 编辑器也被切过去（见 <see cref="MultiplayerSession.JoinHost"/>）。
        ///
        /// 建不出来（目录只读之类）就退回原来的行为，只记一条日志。
        /// 联机不该因为「副本建不出来」而整个不可用。
        /// </summary>
        internal static bool BeginSessionCopy()
        {
            EndSessionCopy();

            string copy;
            bool needsDirectory = false;

            string path = CurrentLevelPath;
            if (path != null)
            {
                copy = SessionPathFor(path);
            }
            else
            {
                // 没打开谱面：落到固定会话目录。用 main.adofai 这个名字，
                // 万一用户自己去看，一眼知道是张谱面。
                string directory = DefaultSessionDirectory();
                if (string.IsNullOrEmpty(directory))
                {
                    return false;
                }
                copy = Path.Combine(directory, "main" + SessionSuffix);
                needsDirectory = true;
            }

            if (string.IsNullOrEmpty(copy))
            {
                return false;
            }

            // 上一次跑崩了会留下残骸，先清掉，别把旧的当新的用。
            TryDelete(copy);

            string chart = EncodeMemoryChart();
            if (string.IsNullOrEmpty(chart))
            {
                // 没打开谱面时内存里那份空谱面也能编码，所以这里一般是
                // 「编辑器还没起来」。给它一段最小可用的空谱面，
                // 反正马上就会被对方的谱面盖掉。
                chart = EmptyChart;
            }

            try
            {
                if (needsDirectory)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(copy));
                }
                RDFile.WriteAllText(copy, chart);
            }
            catch (Exception exception)
            {
                Warn("建会话副本失败，将直接编辑原文件", exception);
                TryDelete(copy);
                return false;
            }

            useSessionCopy = true;
            sessionCopyPath = copy;
            RegisterActiveCopy(copy);
            Main.Logger.Log("[联机] 已建会话副本：" + Path.GetFileName(copy)
                + "（你的改动写这里，不会动原文件）");
            return true;
        }

        /// <summary>
        /// 把会话副本在编辑器里打开。
        ///
        /// 编辑器已经在跑就直接 `OpenLevel`；**还没起来**（空手加入、
        /// 游戏停在主菜单）就请求载入编辑器场景并让它自动打开。
        /// 两处调用点共用这一个入口，免得各写一份判空。
        /// </summary>
        internal static void OpenSessionCopyInEditor()
        {
            string path = CurrentWritePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                RequestSceneLoad(path);
                return;
            }

            try
            {
                editor.OpenLevel(path);
            }
            catch (Exception exception)
            {
                Warn("打开会话副本失败", exception);
            }
        }

        /// <summary>
        /// 在编辑器场景还没起来时，请求载入它并自动打开指定谱面。
        ///
        /// 用 `scnEditor.levelToOpenOnLoad` 而不是自己去调 `OpenLevel`：
        /// 场景刚载入时组件还没 Awake/Start，这时碰 `scnEditor.instance`
        /// 会拿到半成品（那正是「打开关卡报 get_playMode 空引用」的由来）。
        /// 设好这个字段再载入场景，游戏自己的 `Start()` 会把谱面接过去。
        /// </summary>
        private static void RequestSceneLoad(string path)
        {
            try
            {
                scnEditor.levelToOpenOnLoad = path;

                scrLoader loader = scrLoader.instance;
                if (loader != null)
                {
                    loader.LoadScene("scnEditor");
                }
                else
                {
                    UnityEngine.SceneManagement.SceneManager.LoadScene("scnEditor");
                }

                Main.Logger.Log("[联机] 编辑器还没打开，已请求载入并自动打开：" + path);
            }
            catch (Exception exception)
            {
                Warn("请求载入编辑器场景失败", exception);
            }
        }

        /// <summary>
        /// 空手加入时的起步谱面。立刻就会被对方的谱面盖掉，
        /// 填个能过游戏解析器的最小内容就行。
        /// </summary>
        private const string EmptyChart =
            "{\n\t\"angleData\": [],\n\t\"settings\": {},\n\t\"actions\": [],\n\t\"decorations\": []\n}";

        /// <summary>结束会话副本模式，把副本删掉。断开、关工作台时调。</summary>
        internal static void EndSessionCopy()
        {
            string directory = SessionDirectory();

            if (!string.IsNullOrEmpty(sessionCopyPath))
            {
                UnregisterActiveCopy(sessionCopyPath);
                if (TryDelete(sessionCopyPath))
                {
                    Main.Logger.Log("[联机] 已删除会话副本："
                        + Path.GetFileName(sessionCopyPath));
                }
                sessionCopyPath = null;
            }
            useSessionCopy = false;

            // 空手加入时副本落在专门的会话目录里，那时连目录一起清掉；
            // 有谱面时副本是挨着用户谱面放的，绝不能删那个目录。
            if (!string.IsNullOrEmpty(directory)
                && string.Equals(directory, DefaultSessionDirectory(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch (Exception exception)
                {
                    Warn("清理会话目录失败", exception);
                }
            }
        }

        /// <summary>
        /// 启动时扫一遍，清掉上一次崩溃留下的会话副本。
        ///
        /// **只扫游戏自己的数据目录，不扫用户的谱面目录。** 用户的谱面
        /// 放哪儿都有可能（可以完全在游戏目录之外），全盘扫代价太大，
        /// 而且会碰到一大堆无关文件。落在别处的残骸由
        /// <see cref="BeginSessionCopy"/> 在下次加入同一张谱时清掉。
        ///
        /// 跑在后台线程：目录里文件可能不少，不该卡住启动那一帧。
        /// </summary>
        internal static void SweepLeftoversInBackground()
        {
            // `Application.persistentDataPath` 是 Unity API，只能在主线程读。
            // 在这里先拿到，再把路径交给后台线程去扫。
            string root;
            try
            {
                root = Application.persistentDataPath;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            // 显式转成 ThreadStart：匿名委托在 ThreadStart 和
            // ParameterizedThreadStart 之间有歧义，编译器不肯猜。
            string registry = RegistryPath();

            var thread = new System.Threading.Thread(
                (System.Threading.ThreadStart)delegate
                {
                    // 先按登记表删（能覆盖「谱面在任意目录」的情况），
                    // 再扫一遍数据目录兜底。
                    if (!string.IsNullOrEmpty(registry))
                    {
                        SweepRegistry(registry);
                    }
                    SweepLeftovers(root);
                })
            {
                IsBackground = true,
                Name = "AES-MP-Sweep"
            };
            thread.Start();
        }

        private const int SweepFileCap = 4000;

        private static void SweepLeftovers(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                int seen = 0;
                int removed = 0;
                foreach (string file in Directory.EnumerateFiles(
                             root, "*" + SessionSuffix, SearchOption.AllDirectories))
                {
                    // 目录可能巨大，给个上限。扫不完也不要紧——这些文件
                    // 只是碍眼，不影响正确性（下次加入同一张谱会清掉）。
                    if (++seen > SweepFileCap)
                    {
                        break;
                    }
                    if (TryDelete(file))
                    {
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    Main.Logger.Log("[联机] 启动清理：删掉了 " + removed + " 个残留的会话副本。");
                }
            }
            catch (Exception exception)
            {
                // 清理失败无所谓，不能因为扫目录出问题就影响启动。
                Warn("扫描残留会话副本失败", exception);
            }
        }

        // ------------------------------------------------------------ 崩溃残留的登记

        /// <summary>
        /// 活跃会话副本的登记表，放在游戏数据目录。
        ///
        /// **为什么需要它**：副本建在**用户的谱面目录**旁边（可以完全在
        /// 游戏目录之外，比如 `E:\谱子\`），进程被强杀时我们没有机会
        /// 删它，而下次启动去全盘找是不可能的。所以活着的时候把路径写进
        /// 这个清单，下次启动照着清单删。
        ///
        /// 正常退出会把自己从清单里划掉，清单空了就删掉文件本身。
        /// </summary>
        private static string RegistryPath()
        {
            try
            {
                string root = Application.persistentDataPath;
                return string.IsNullOrEmpty(root)
                    ? null
                    : Path.Combine(root, "aes-mp-sessions.txt");
            }
            catch
            {
                return null;
            }
        }

        private static void RegisterActiveCopy(string path)
        {
            string registry = RegistryPath();
            if (registry == null)
            {
                return;
            }
            try
            {
                File.AppendAllText(registry, path + Environment.NewLine);
            }
            catch (Exception exception)
            {
                // 登记不上只是「崩溃后可能留个残骸」，不值得让联机失败。
                Warn("登记会话副本失败", exception);
            }
        }

        private static void UnregisterActiveCopy(string path)
        {
            string registry = RegistryPath();
            if (registry == null)
            {
                return;
            }
            try
            {
                if (!File.Exists(registry))
                {
                    return;
                }
                var kept = new List<string>();
                string[] lines = File.ReadAllLines(registry);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)
                        || string.Equals(line, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    kept.Add(line);
                }

                if (kept.Count == 0)
                {
                    File.Delete(registry);
                }
                else
                {
                    File.WriteAllLines(registry, kept.ToArray());
                }
            }
            catch (Exception exception)
            {
                Warn("注销会话副本失败", exception);
            }
        }

        /// <summary>启动时按登记表清掉上次没删掉的副本。跑在后台线程。</summary>
        private static void SweepRegistry(string registry)
        {
            try
            {
                if (!File.Exists(registry))
                {
                    return;
                }

                int removed = 0;
                string[] lines = File.ReadAllLines(registry);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (TryDelete(lines[i]))
                    {
                        removed++;
                    }
                }
                File.Delete(registry);
                if (removed > 0)
                {
                    Main.Logger.Log("[联机] 启动清理：删掉了 " + removed + " 个上次残留的会话副本。");
                }
            }
            catch (Exception exception)
            {
                Warn("清理会话副本登记表失败", exception);
            }
        }

        private static bool IsSessionCopy(string path)
        {
            return !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(sessionCopyPath)
                && string.Equals(path, sessionCopyPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>这次写入该落到哪个文件。外面要重载关卡时也用它。</summary>
        internal static string CurrentWritePath
        {
            get { return TargetPath(); }
        }

        /// <summary>这次写入该落到哪个文件。</summary>
        private static string TargetPath()
        {
            return useSessionCopy && !string.IsNullOrEmpty(sessionCopyPath)
                ? sessionCopyPath
                : CurrentLevelPath;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception exception)
            {
                Warn("删除会话副本失败", exception);
            }
            return false;
        }

        // ------------------------------------------------------------ 谱面引用的媒体

        /// <summary>
        /// 单文件体积上限。超了就跳过，只记一条日志。
        ///
        /// 定这个数是为了让 base64 之后还塞得进单条消息
        /// （`NetLink.MaxMessageBytes` 是 32MB，base64 膨胀 4/3，
        /// 所以 16MB 原文约 21MB 编码后，留了余量）。
        /// </summary>
        internal const long MaxMediaBytes = 16L * 1024 * 1024;

        /// <summary>
        /// 从谱面文本里抠出它引用到的媒体文件名。
        ///
        /// **不解析 JSON**，只在原文里找「带媒体后缀的字符串字面量」。
        /// 项目里那条硬规则在这里同样是底线：JSON 往返会破坏尾逗号、
        /// 数字写法和属性顺序。按后缀扫还顺带覆盖了将来游戏新增的字段
        /// ——不需要维护一张字段名表。
        ///
        /// 实测引用长这样：`"songFilename": "520am-125.ogg"`，
        /// 装饰物贴图同理，都是**裸文件名**。
        /// </summary>
        internal static List<string> ExtractMediaNames(string chartText)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(chartText))
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < chartText.Length)
            {
                int open = chartText.IndexOf('"', i);
                if (open < 0)
                {
                    break;
                }
                int close = chartText.IndexOf('"', open + 1);
                if (close < 0)
                {
                    break;
                }

                string value = chartText.Substring(open + 1, close - open - 1);
                i = close + 1;

                // 只认短的单行名字。
                //
                // **这里不能再排斥反斜杠**：谱面引用子目录里的贴图是很常见的
                // （实测 232 个谱面里有 5608 处这样的引用，形如
                // 「[Dynamic Decorations] xxx\0.png」）。早期版本在这里把带
                // 路径的引用一律跳过，导致**子目录里的贴图永远传不过去**，
                // 而那是这类谱面的常态。安全性改由落盘前的路径校验负责
                // （见 TryResolveMedia）。
                if (value.Length == 0 || value.Length > 260
                    || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                {
                    continue;
                }

                if (HasMediaExtension(value) && seen.Add(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        private static readonly string[] MediaExtensions =
        {
            ".png", ".jpg", ".jpeg", ".ogg", ".mp3", ".wav"
        };

        private static bool HasMediaExtension(string value)
        {
            for (int i = 0; i < MediaExtensions.Length; i++)
            {
                if (value.EndsWith(MediaExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 媒体文件该放哪个目录——**谱面所在的目录**。
        ///
        /// 游戏就是按这个目录去解析音频和贴图的，所以收到的媒体必须落在
        /// 同一个地方，否则谱面照样找不到它们。
        /// </summary>
        internal static string MediaDirectory()
        {
            // 没有会话副本时退回原谱面目录；空手加入时这会落到专门的
            // 会话目录上（媒体和谱面必须在一起，游戏按谱面目录找它们）。
            string path = sessionCopyPath ?? CurrentLevelPath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                return Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把一个**对端给的媒体引用**解析成谱面目录下的绝对路径。
        ///
        /// 引用可以是子目录相对路径（`[Dynamic Decoration] xxx\0.png`），
        /// 这正是这类谱面的常态，所以不能简单拒掉。但名字来自网络，
        /// 必须挡住 `..\..\Windows\...` 这种穿越。
        ///
        /// 做法：规整成绝对路径，再要求它**确实落在谱面目录内**。
        /// 用前缀比较时补上目录分隔符，免得 `C:\a\b` 被判成 `C:\a\bc`
        /// 的子路径。
        /// </summary>
        internal static bool TryResolveMedia(string name, out string full)
        {
            full = null;

            string directory = MediaDirectory();
            if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            // 统一分隔符：谱面用的是反斜杠，Path 在别的平台上认正斜杠。
            string normalized = name.Replace('\\', Path.DirectorySeparatorChar)
                                    .Replace('/', Path.DirectorySeparatorChar);

            if (normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            try
            {
                string root = Path.GetFullPath(directory);
                string candidate = Path.GetFullPath(Path.Combine(root, normalized));

                string rootWithSeparator = root.EndsWith(
                    Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;

                if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    Main.Logger.Warning("[联机] 媒体引用指向谱面目录之外，已拒绝：" + name);
                    return false;
                }

                full = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>本地有没有这个媒体文件。</summary>
        internal static bool HasMedia(string name)
        {
            string full;
            return TryResolveMedia(name, out full) && File.Exists(full);
        }

        /// <summary>读一个媒体文件。超过上限或读不到返回 null。</summary>
        internal static byte[] ReadMedia(string name)
        {
            string full;
            if (!TryResolveMedia(name, out full))
            {
                return null;
            }

            try
            {
                if (!File.Exists(full))
                {
                    return null;
                }

                var info = new FileInfo(full);
                if (info.Length > MaxMediaBytes)
                {
                    Main.Logger.Log("[联机] 「" + name + "」有 "
                        + (info.Length / 1024 / 1024) + "MB，超过单文件上限，跳过不传。");
                    return null;
                }

                return File.ReadAllBytes(full);
            }
            catch (Exception exception)
            {
                Warn("读取媒体文件失败（" + name + "）", exception);
                return null;
            }
        }

        /// <summary>
        /// 把一个媒体文件写到谱面目录（按引用里的相对路径）。
        ///
        /// 同名已存在就跳过、不覆盖——同名不同内容极罕见，而覆盖是
        /// 单向不可逆的破坏。对方那边本来就有的话，本来也不需要传。
        /// </summary>
        internal static bool WriteMedia(string name, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxMediaBytes)
            {
                return false;
            }

            string full;
            if (!TryResolveMedia(name, out full))
            {
                Main.Logger.Warning("[联机] 收到的媒体引用不合法，已拒绝：" + name);
                return false;
            }

            try
            {
                if (File.Exists(full))
                {
                    return false;
                }

                // 引用带子目录时目录可能还不存在，先建出来。
                string parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllBytes(full, data);
                Main.Logger.Log("[联机] 已接收媒体：" + name
                    + "（" + (data.Length / 1024) + "KB）");
                return true;
            }
            catch (Exception exception)
            {
                Warn("写入媒体文件失败（" + name + "）", exception);
                return false;
            }
        }

        /// <summary>
        /// 内存里当前的谱面文本。取不到时返回 null。
        ///
        /// 用游戏自己的编码器，所以这段文本和「按 Ctrl+S 会写出来的那段」
        /// 是同一份东西——这也是整份同步时该发的内容。
        /// </summary>
        internal static string EncodeMemoryChart()
        {
            try
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null || editor.levelData == null)
                {
                    return null;
                }
                return editor.levelData.Encode();
            }
            catch (Exception exception)
            {
                Warn("序列化谱面失败", exception);
                return null;
            }
        }

        /// <summary>读磁盘上那份谱面的原文。读不到返回 null。</summary>
        internal static string ReadDiskChart(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return File.Exists(path) ? RDFile.ReadAllText(path) : null;
            }
            catch (Exception exception)
            {
                Warn("读取谱面失败", exception);
                return null;
            }
        }

        /// <summary>
        /// 一段谱面文本的指纹。**跨机器可比**。
        ///
        /// 项目里已有的两个指纹（`EditorTimelineView.ComputeDataFingerprint`、
        /// `EditorDecorationList.ComputeDataFingerprint`）都不能干这个：
        /// 它们哈希 `GetInstanceID()` 和 `RuntimeHelpers.GetHashCode()`，
        /// 那是**进程内的身份**,两台机器上必然不同。这里只哈希文本内容本身。
        /// </summary>
        internal static string Fingerprint(string chartText)
        {
            if (chartText == null)
            {
                return string.Empty;
            }

            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(chartText)));
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        builder.Append(hash[i].ToString("x2"));
                    }
                    return builder.ToString();
                }
            }
            catch (Exception exception)
            {
                Warn("计算谱面指纹失败", exception);
                return string.Empty;
            }
        }

        /// <summary>断开联机时清掉排队与去重状态，下次连上重新开始。</summary>
        internal static void ResetApplyState()
        {
            queuedChart = null;
            lastAppliedFingerprint = null;
            reloadSettleUntil = 0f;
        }

        /// <summary>
        /// 声明「这份谱面已经落地过了」，即把它记为上一次落地的内容。
        ///
        /// 用在**自己推出去的**那份谱面上：它绕一圈回到本机时，内容和我们
        /// 内存里的一致，本来也会被指纹那道闸挡掉，但显式记一笔更稳——
        /// 结构改动是自动推的，重复落地意味着用户那边毫无理由地多闪一次重载。
        /// </summary>
        internal static void MarkApplied(string chartText)
        {
            string fingerprint = Fingerprint(chartText);
            if (!string.IsNullOrEmpty(fingerprint))
            {
                lastAppliedFingerprint = fingerprint;
            }
        }

        /// <summary>内存里当前谱面的指纹。取不到时返回空串。</summary>
        internal static string MemoryFingerprint()
        {
            return Fingerprint(EncodeMemoryChart());
        }

        /// <summary>
        /// 把一份谱面文本落到当前关卡路径，然后在编辑器里重新打开它。
        ///
        /// `scnEditor.OpenLevel(path)` 是干净的直接重载入口——它只做
        /// 「暂停 + 起协程载入」，**不会**像无参那个重载一样弹未保存提示。
        /// 所以覆盖前的判断必须由调用方先做（见联机层的
        /// `OfferChart`），而不是让游戏自己去弹窗。
        ///
        /// 返回是否真的写了文件；重载是协程，不在这里等它完成。
        /// </summary>
        /// <summary>上一次落地的谱面指纹，用来挡重复投递。</summary>
        private static string lastAppliedFingerprint;

        /// <summary>
        /// 重载窗口。`OpenLevel` 是协程，落地之后要过一会儿新谱面才真的进到编辑器里。
        /// 这个窗口之内再收到一份谱面就只记下最新的那份，等窗口过去再落地——
        /// 否则第二份会在第一份还没载完时写进同一个文件，两边都不知道自己覆盖了谁。
        /// </summary>
        private static float reloadSettleUntil;

        /// <summary>窗口里收到的最新一份谱面，等窗口过去再落地。</summary>
        private static string queuedChart;
        private const float ReloadSettleSeconds = 1.5f;

        internal static bool ApplyChart(string chartText)
        {
            return ApplyChart(chartText, true);
        }

        /// <summary>
        /// 落地一份谱面。`reload` 为 false 时只写盘、不重载编辑器。
        ///
        /// 加入房间期间正是这样用的：那段时间要落好几份东西（会话副本、
        /// 房主的谱面、媒体），每次都重载的话，中间态会一直变——而
        /// **文字装饰物在重载到一半时最容易显示不出来**，用户看到的就是
        /// 「有几行字不见了」。所以加入期间只写盘（加载遮罩盖着，用户
        /// 也看不到中间态），最后一次性干净重载。
        /// </summary>
        internal static bool ApplyChart(string chartText, bool reload)
        {
            if (string.IsNullOrEmpty(chartText))
            {
                return false;
            }

            string path = TargetPath();
            if (path == null)
            {
                Main.Logger.Log("[联机] 没有打开中的关卡，收到谱面后无处可放。");
                return false;
            }

            string fingerprint = Fingerprint(chartText);

            // 同一份谱面重复到达（对方重推、我们重拉）就不必再覆盖一遍。
            if (!string.IsNullOrEmpty(lastAppliedFingerprint)
                && string.Equals(fingerprint, lastAppliedFingerprint, StringComparison.Ordinal))
            {
                return true;
            }

            // 上一次重载还没落定。记住最新这份，等窗口过去再落地。
            if (UnityEngine.Time.unscaledTime < reloadSettleUntil)
            {
                queuedChart = chartText;
                Main.Logger.Log("[联机] 上一份谱面还在重载，这份先排队。");
                return true;
            }

            bool ok = reload ? WriteAndReload(path, chartText) : WriteOnly(path, chartText);
            if (ok)
            {
                lastAppliedFingerprint = fingerprint;
                reloadSettleUntil = UnityEngine.Time.unscaledTime + ReloadSettleSeconds;
            }
            return ok;
        }

        /// <summary>
        /// 由工作台每帧调一次：把重载窗口里排队的那份谱面落地。
        /// </summary>
        internal static void TickPendingApply()
        {
            if (queuedChart == null || UnityEngine.Time.unscaledTime < reloadSettleUntil)
            {
                return;
            }

            string chart = queuedChart;
            queuedChart = null;
            string path = TargetPath();
            if (path == null)
            {
                return;
            }

            string fingerprint = Fingerprint(chart);
            if (string.Equals(fingerprint, lastAppliedFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            Main.Logger.Log("[联机] 落地排队中的那份谱面。");
            if (WriteAndReload(path, chart))
            {
                lastAppliedFingerprint = fingerprint;
                reloadSettleUntil = UnityEngine.Time.unscaledTime + ReloadSettleSeconds;
            }
        }

        /// <summary>只写盘，不重载编辑器。加入期间用它，避免中间态反复重载。</summary>
        private static bool WriteOnly(string path, string chartText)
        {
            return WriteFile(path, chartText);
        }

        /// <summary>
        /// 写盘 + 备份。不含重载。
        ///
        /// **落盘之前先留一份备份——但会话副本不用。**
        ///
        /// 备份是为了挡住「覆盖了用户的数据、事后无法退回」。而会话副本
        /// 本身就是那个「可随时丢弃」的临时文件（加入时建、断开时删），
        /// 它一开始就是从原文件复制来的，内容一模一样。每收一次谱面
        /// 就在旁边再堆一份 .bak 只会把谱面目录塞满垃圾。
        /// </summary>
        private static bool WriteFile(string path, string chartText)
        {
            string backup = null;
            if (!IsSessionCopy(path))
            {
                backup = BackupExisting(path);
                if (backup == null)
                {
                    // 备份都写不出来，就别覆盖了。宁可这次同步失败。
                    Main.Logger.Error("[联机] 无法备份原谱面（" + path + "），为避免覆盖，已取消这次同步。");
                    return false;
                }
            }

            try
            {
                // 和 scnEditor.SaveLevel 走同一个入口，编码与 BOM 行为一致。
                RDFile.WriteAllText(path, chartText);
                Main.Logger.Log("[联机] 已覆盖 " + Path.GetFileName(path)
                    + (backup == null ? "（会话副本，无需备份）。" : "（原文件备份在 " + backup + "）。"));
            }
            catch (Exception exception)
            {
                Warn("写入谱面失败", exception);
                return false;
            }

            return true;
        }

        private static bool WriteAndReload(string path, string chartText)
        {
            if (!WriteFile(path, chartText))
            {
                return false;
            }

            try
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                {
                    // **编辑器场景还没加载。**
                    //
                    // 只有「空手加入」会走到这里——用户还没打开过任何谱面，
                    // 游戏可能停在主菜单，那条路径上根本没有 scnEditor。
                    //
                    // 这时不能调 `OpenLevel`（对象不存在），改成走游戏自己的
                    // 机制：把路径塞进 `levelToOpenOnLoad`，再载入编辑器场景。
                    // `scnEditor.Start()` 会把它接过去自动打开，这正是
                    // `levelToOpenOnLoad` 存在的意义（见 ADOFAI mod 的常规做法）。
                    RequestSceneLoad(path);
                    return true;
                }
                editor.OpenLevel(path);
                Main.Logger.Log("[联机] 已应用对方谱面并重载：" + path);
            }
            catch (Exception exception)
            {
                Warn("重载关卡失败（文件已写入）", exception);
            }

            return true;
        }

        /// <summary>
        /// 把现有谱面文件复制一份带时间戳的备份。返回备份路径；失败返回 null。
        ///
        /// 放在谱面同目录，名字形如 `level.adofai.bak-20260925-221530`。
        /// 不覆盖已有的备份，所以反复同步会攒下几份，用户自己挑着删。
        /// </summary>
        private static string BackupExisting(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    // 本来就没有这个文件，没什么可备份的。
                    return path + ".new";
                }

                string backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, false);
                return backup;
            }
            catch (Exception exception)
            {
                Warn("备份原谱面失败", exception);
                return null;
            }
        }

        /// <summary>
        /// 比对与指纹都用的归一化：去掉 BOM、统一换行、去掉首尾空白。
        ///
        /// 不做更激进的规范化（比如重排键、去空格）——那会把真正的内容差异
        /// 也抹平，而「有没有改动」这件事宁可保守。
        /// </summary>
        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text.Substring(1);
            }

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return text.Trim();
        }

        private static void Warn(string what, Exception exception)
        {
            if (Main.Logger != null)
            {
                Main.Logger.Warning("[联机] " + what + "：" + exception.Message);
            }
        }
    }
}
