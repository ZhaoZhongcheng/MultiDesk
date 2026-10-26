using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace MultiDesk.Core
{
    /// <summary>
    /// 首次运行时把系统桌面上已有的图标导入到某个虚拟桌面，
    /// 让用户启用本程序后不会「桌面突然空了」。
    /// </summary>
    public static class DesktopImporter
    {
        private static readonly string[] SkipNames =
        {
            "desktop.ini", "thumbs.db", ".ds_store"
        };

        /// <summary>返回桌面文件夹候选路径（当前用户 + 公共桌面）。</summary>
        public static List<string> GetDesktopFolderPaths()
        {
            List<string> list = new List<string>();
            try
            {
                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(userDesktop) && Directory.Exists(userDesktop)) list.Add(userDesktop);
            }
            catch { }
            try
            {
                string pub = Environment.GetEnvironmentVariable("PUBLIC");
                if (!string.IsNullOrEmpty(pub))
                {
                    string publicDesktop = Path.Combine(pub, "Desktop");
                    if (Directory.Exists(publicDesktop) && !list.Contains(publicDesktop)) list.Add(publicDesktop);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 从系统桌面导入图标到指定虚拟桌面（不移动/不修改真实文件），
        /// 会自动排版。返回导入数量。已存在同路径图标会跳过。
        /// </summary>
        public static int ImportExistingIcons(DesktopProfile desktop, Rect area)
        {
            return ImportExistingIcons(desktop, area, IconLayout.Default);
        }

        public static int ImportExistingIcons(DesktopProfile desktop, Rect area, IconMetrics metrics)
        {
            if (desktop == null) return 0;
            List<string> paths = new List<string>();
            foreach (string folder in GetDesktopFolderPaths())
            {
                try
                {
                    foreach (string entry in Directory.GetFileSystemEntries(folder))
                    {
                        if (!IsImportCandidate(entry)) continue;
                        paths.Add(entry);
                    }
                }
                catch
                {
                }
            }
            int added = 0;
            foreach (string path in paths)
            {
                bool exists = false;
                foreach (IconEntry ic in desktop.Icons)
                {
                    if (ic != null && string.Equals(ic.TargetPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists) continue;
                IconEntry entryNew = Launcher.CreateFromPath(path);
                desktop.Icons.Add(entryNew);
                added++;
            }
            if (added > 0)
            {
                IconMetrics m = metrics ?? IconLayout.Default;
                if (m.IconSize == 48)
                {
                    IconLayout.Normalize(desktop, area, m);
                }
                else
                {
                    IconLayout.Normalize(desktop, area, m);
                }
            }
            return added;
        }

        private static bool IsImportCandidate(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) return false;
                foreach (string skip in SkipNames)
                {
                    if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase)) return false;
                }
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Hidden) == FileAttributes.Hidden) return false;
                if ((attrs & FileAttributes.System) == FileAttributes.System) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
