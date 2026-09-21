using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ADOFAI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    [Serializable]
    internal sealed class EditorProjectDocument
    {
        public string format = "ADOFAIEditorStudioProject";
        public int version = 1;
        public string projectId;
        public string level;
        public string levelFingerprint;
        public List<string> mediaRoots = new List<string> { "." };
        public List<string> media = new List<string>();

        [JsonExtensionData]
        public IDictionary<string, JToken> extensionData = new Dictionary<string, JToken>();
    }

    internal sealed class EditorProjectMediaItem
    {
        internal string RelativePath;
        internal string AbsolutePath;
        internal string FileName;
        internal string DirectoryPath;
        internal string Extension;

        /// <summary>
        /// 文件修改时间，用来把最新的媒体排在前面。读不到文件
        /// 元数据时为 0，这样这类条目排到最后。
        /// </summary>
        internal long LastWriteUtcTicks;
    }

    internal sealed class EditorProjectSession
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif"
        };

        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".obsidian",
            "Backups",
            "Cache",
            "Temp",
            "__MACOSX"
        };

        internal EditorProjectDocument Document { get; private set; }
        internal string LevelPath { get; private set; }
        internal string ProjectPath { get; private set; }
        internal string RootDirectory { get; private set; }
        internal List<EditorProjectMediaItem> Media { get; private set; }
        internal List<string> MediaDirectories { get; private set; }
        internal string Error { get; private set; }
        private long mediaFingerprint;
        private long levelFileStamp;

        private EditorProjectSession()
        {
            Media = new List<EditorProjectMediaItem>();
            MediaDirectories = new List<string>();
        }

        internal static EditorProjectSession LoadCurrent()
        {
            return Load(GetCurrentLevelPath());
        }

        internal static EditorProjectSession Load(string levelPath)
        {
            if (string.IsNullOrWhiteSpace(levelPath) || !File.Exists(levelPath))
            {
                return null;
            }

            levelPath = Path.GetFullPath(levelPath);
            var session = new EditorProjectSession
            {
                LevelPath = levelPath,
                ProjectPath = Path.ChangeExtension(levelPath, ".adofaiprj"),
                RootDirectory = Path.GetDirectoryName(levelPath)
            };
            session.LoadDocument();
            session.RefreshMediaIndex();
            return session;
        }

        internal bool RefreshMediaIndex()
        {
            if (string.IsNullOrWhiteSpace(RootDirectory) || !Directory.Exists(RootDirectory))
            {
                return false;
            }

            var files = new List<string>();
            CollectImageFiles(RootDirectory, files);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            var nextMedia = new List<EditorProjectMediaItem>(files.Count);
            var nextPaths = new List<string>(files.Count);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long nextFingerprint = 17L;
            for (int i = 0; i < files.Count; i++)
            {
                string absolute = files[i];
                string relative = ToRelativePath(absolute);
                string directory = NormalizeDirectory(Path.GetDirectoryName(relative));
                long lastWriteTicks = 0L;
                try
                {
                    lastWriteTicks = new FileInfo(absolute).LastWriteTimeUtc.Ticks;
                }
                catch
                {
                    // 文件可能在枚举与读取元数据之间消失。
                }
                nextMedia.Add(new EditorProjectMediaItem
                {
                    RelativePath = relative,
                    AbsolutePath = absolute,
                    FileName = Path.GetFileName(relative),
                    DirectoryPath = directory,
                    Extension = Path.GetExtension(relative).ToLowerInvariant(),
                    LastWriteUtcTicks = lastWriteTicks
                });
                nextPaths.Add(relative);
                directories.Add(directory);
                AddParentDirectories(directory, directories);
                try
                {
                    var info = new FileInfo(absolute);
                    unchecked
                    {
                        nextFingerprint = nextFingerprint * 31L + StringComparer.OrdinalIgnoreCase.GetHashCode(relative);
                        nextFingerprint = nextFingerprint * 31L + info.Length;
                        nextFingerprint = nextFingerprint * 31L + info.LastWriteTimeUtc.Ticks;
                    }
                }
                catch
                {
                    // 文件可能在枚举与读取元数据之间消失。
                }
            }

            directories.Remove(".");
            var nextDirectories = new List<string>(directories);
            nextDirectories.Sort(CompareDirectoryPaths);

            bool indexChanged = !SequenceEquals(Document.media, nextPaths);
            bool mediaChanged = nextFingerprint != mediaFingerprint;
            mediaFingerprint = nextFingerprint;
            if (Document.media == null)
            {
                Document.media = new List<string>();
            }
            if (Document.mediaRoots == null || Document.mediaRoots.Count == 0)
            {
                Document.mediaRoots = new List<string> { "." };
                indexChanged = true;
            }
            if (indexChanged)
            {
                Document.media = nextPaths;
                SaveDocument();
            }
            RefreshLevelFingerprint();

            Media = nextMedia;
            MediaDirectories = nextDirectories;
            return indexChanged || mediaChanged;
        }

        private void RefreshLevelFingerprint()
        {
            try
            {
                var info = new FileInfo(LevelPath);
                long nextStamp;
                unchecked
                {
                    nextStamp = info.Length * 397L ^ info.LastWriteTimeUtc.Ticks;
                }
                if (levelFileStamp != 0L && nextStamp != levelFileStamp)
                {
                    SaveDocument();
                }
                levelFileStamp = nextStamp;
            }
            catch
            {
                // 编辑器保存时可能以原子替换的方式换掉谱面文件。
            }
        }

        internal void SaveDocument()
        {
            try
            {
                if (Document == null || string.IsNullOrWhiteSpace(ProjectPath))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(Document.projectId))
                {
                    Document.projectId = Guid.NewGuid().ToString("N");
                }
                Document.format = "ADOFAIEditorStudioProject";
                Document.version = 1;
                Document.level = Path.GetFileName(LevelPath);
                Document.levelFingerprint = ComputeFileFingerprint(LevelPath);

                string json = JsonConvert.SerializeObject(Document, Formatting.Indented);
                string temporaryPath = ProjectPath + ".tmp";
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(ProjectPath))
                {
                    string backupPath = ProjectPath + ".bak";
                    File.Replace(temporaryPath, ProjectPath, backupPath, true);
                }
                else
                {
                    File.Move(temporaryPath, ProjectPath);
                }
                Error = null;
            }
            catch (Exception exception)
            {
                Error = "工程文件保存失败：" + exception.Message;
                if (Main.Logger != null)
                {
                    Main.Logger.Warning(Error);
                }
            }
        }

        private void LoadDocument()
        {
            Document = null;
            if (File.Exists(ProjectPath))
            {
                try
                {
                    Document = JsonConvert.DeserializeObject<EditorProjectDocument>(File.ReadAllText(ProjectPath, Encoding.UTF8));
                    if (Document == null || !string.Equals(Document.format, "ADOFAIEditorStudioProject", StringComparison.Ordinal))
                    {
                        Document = null;
                    }
                }
                catch (Exception exception)
                {
                    Error = "工程文件读取失败：" + exception.Message;
                }
            }

            if (Document == null)
            {
                Document = new EditorProjectDocument
                {
                    projectId = Guid.NewGuid().ToString("N"),
                    level = Path.GetFileName(LevelPath),
                    mediaRoots = new List<string> { "." },
                    media = new List<string>()
                };
                SaveDocument();
            }
            else
            {
                if (Document.media == null)
                {
                    Document.media = new List<string>();
                }
                if (Document.mediaRoots == null || Document.mediaRoots.Count == 0)
                {
                    Document.mediaRoots = new List<string> { "." };
                }
            }
        }

        private void CollectImageFiles(string directory, List<string> files)
        {
            string[] entries;
            try
            {
                entries = Directory.GetFiles(directory);
                for (int i = 0; i < entries.Length; i++)
                {
                    if (ImageExtensions.Contains(Path.GetExtension(entries[i])))
                    {
                        files.Add(entries[i]);
                    }
                }

                string[] subdirectories = Directory.GetDirectories(directory);
                for (int i = 0; i < subdirectories.Length; i++)
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(subdirectories[i])))
                    {
                        CollectImageFiles(subdirectories[i], files);
                    }
                }
            }
            catch (Exception exception)
            {
                Error = "媒体索引读取失败：" + exception.Message;
            }
        }

        private string ToRelativePath(string absolutePath)
        {
            string relative = absolutePath.Substring(RootDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == ".")
            {
                return ".";
            }
            return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');
        }

        private static void AddParentDirectories(string directory, HashSet<string> directories)
        {
            string current = directory;
            while (!string.IsNullOrWhiteSpace(current) && current != ".")
            {
                int slash = current.LastIndexOf('/');
                current = slash < 0 ? "." : current.Substring(0, slash);
                if (current != ".")
                {
                    directories.Add(current);
                }
            }
        }

        private static int CompareDirectoryPaths(string left, string right)
        {
            int depthLeft = left.Split('/').Length;
            int depthRight = right.Split('/').Length;
            int result = depthLeft.CompareTo(depthRight);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static bool SequenceEquals(List<string> left, List<string> right)
        {
            if (left == null || left.Count != right.Count)
            {
                return false;
            }
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private static string ComputeFileFingerprint(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var builder = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        builder.Append(hash[i].ToString("x2"));
                    }
                    return builder.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string GetCurrentLevelPath()
        {
            try
            {
                if (scnGame.instance != null && !string.IsNullOrWhiteSpace(ADOBase.levelPath))
                {
                    return Path.GetFullPath(ADOBase.levelPath);
                }
            }
            catch
            {
                // 工作台重建期间，谱面可能正处于场景切换的中间状态。
            }
            return null;
        }
    }
}