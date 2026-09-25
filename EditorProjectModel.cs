using System;
using System.Collections.Generic;
using System.IO;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 媒体列表里的一条。谱面文件夹下每一张能被当成装饰物素材的图，
    /// 都对应这里的一条记录。
    /// </summary>
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

    /// <summary>
    /// 当前谱面的媒体索引：递归扫一遍谱面所在目录，把里面所有图片
    /// 收成一张表，供左侧媒体网格显示和拖放。
    ///
    /// 这里**不落任何文件**。它曾经会写一个 .adofaiprj「工程文件」，
    /// 里面存一份同样的媒体路径表、一个随机 GUID、以及谱面文件的
    /// SHA256。那几样东西全项目没有任何地方在读——路径表每半秒扫盘
    /// 就能重建，GUID 从生成那天起就没人看过。代价却是每打开一张谱面，
    /// 都往作者的谱面文件夹里多写三个文件（.adofaiprj/.bak/.tmp）。
    /// 索引本来就是从磁盘算出来的，没有理由再往磁盘写回去。
    /// </summary>
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

        internal string LevelPath { get; private set; }
        internal string RootDirectory { get; private set; }
        internal List<EditorProjectMediaItem> Media { get; private set; }
        internal string Error { get; private set; }
        private long mediaFingerprint;

        private EditorProjectSession()
        {
            Media = new List<EditorProjectMediaItem>();
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
                RootDirectory = Path.GetDirectoryName(levelPath)
            };
            session.RefreshMediaIndex();
            return session;
        }

        /// <summary>
        /// 重新扫一遍。返回「内容变了没有」——调用方据此决定
        /// 要不要重建列表。指纹里带文件长度和修改时间，所以增删图片、
        /// 或者覆盖保存一张图，都能被认出来。
        /// </summary>
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
            long nextFingerprint = 17L;
            for (int i = 0; i < files.Count; i++)
            {
                string absolute = files[i];
                string relative = ToRelativePath(absolute);
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
                    DirectoryPath = NormalizeDirectory(Path.GetDirectoryName(relative)),
                    Extension = Path.GetExtension(relative).ToLowerInvariant(),
                    LastWriteUtcTicks = lastWriteTicks
                });
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

            bool changed = nextFingerprint != mediaFingerprint;
            mediaFingerprint = nextFingerprint;
            Media = nextMedia;
            return changed;
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
