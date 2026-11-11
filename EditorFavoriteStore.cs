using System;
using System.Collections.Generic;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 参数栏下拉的收藏列表。缓速和高级滤镜各一份。
    ///
    /// **这里唯一知道「收藏是怎么存的」** —— 存储格式（换行分隔的字符串）
    /// 只出现在这个文件里，将来换成 JSON 或者按谱面分开存，只改这一处。
    ///
    /// 存进 `Main.Settings` 而不是写谱面：收藏是使用者的个人偏好，
    /// 跟具体哪一张谱面无关，也不该被撤回 / 联机同步。
    /// </summary>
    internal static class EditorFavoriteStore
    {
        /// <summary>
        /// 分隔符用换行。滤镜类名和缓速名都不含换行，但都可能含逗号
        /// （`Gameplay.AdvancedFilter` 这类里加参数的情况），所以不能沿用
        /// 联机那几项用逗号的习惯。
        /// </summary>
        private static readonly char[] Separators = new[] { '\n' };

        /// <summary>
        /// 读一份收藏。
        ///
        /// `filterKind = false` 是缓速，`true` 是高级滤镜。参数用 bool 而不是
        /// 枚举，是因为只有两种、且调用方手上本来就知道自己在建哪种下拉。
        /// </summary>
        public static string[] Get(bool filterKind)
        {
            string raw = ReadRaw(filterKind);
            if (string.IsNullOrEmpty(raw))
            {
                return new string[0];
            }

            return raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        }

        public static bool Contains(bool filterKind, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string[] all = Get(filterKind);
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(all[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 收藏 / 取消收藏。切完立刻写盘 —— 不写的话关游戏就丢了，
        /// 而这个 mod 通常是被 `taskkill` 硬杀的，没有优雅退出的机会。
        /// </summary>
        public static void Toggle(bool filterKind, string value)
        {
            if (string.IsNullOrEmpty(value) || Main.Settings == null)
            {
                return;
            }

            List<string> all = new List<string>(Get(filterKind));
            int found = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i], value, StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
            {
                all.RemoveAt(found);
            }
            else
            {
                all.Add(value);
            }

            WriteRaw(filterKind, string.Join("\n", all.ToArray()));
            Main.SaveSettings();
        }

        private static string ReadRaw(bool filterKind)
        {
            if (Main.Settings == null)
            {
                return string.Empty;
            }

            return filterKind ? Main.Settings.FavoriteFilters : Main.Settings.FavoriteEases;
        }

        private static void WriteRaw(bool filterKind, string raw)
        {
            if (Main.Settings == null)
            {
                return;
            }

            if (filterKind)
            {
                Main.Settings.FavoriteFilters = raw;
            }
            else
            {
                Main.Settings.FavoriteEases = raw;
            }
        }
    }
}
