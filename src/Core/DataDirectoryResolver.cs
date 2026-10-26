using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MultiDesk.Core
{
    /// <summary>
    /// 数据目录解析器：为程序挑选一个「当前进程确实可写」的数据目录。
    /// 
    /// 背景：部分环境（安全软件、企业策略、沙箱/受限启动器）会拒绝对
    /// %APPDATA% / %LOCALAPPDATA% 的写入。如果直接使用固定目录，程序会在启动时
    /// 抛出「访问被拒绝」而无法使用。这里按以下顺序探测可写目录：
    ///   1. 程序所在目录下的 MultiDesk-data（绿色便携模式，优先）
    ///   2. %LOCALAPPDATA%\MultiDesk
    ///   3. %APPDATA%\MultiDesk
    ///   4. %TEMP%\MultiDesk
    ///   5. %USERPROFILE%\MultiDesk
    /// 返回第一个能创建目录并写入探测文件的路径；全部失败时抛出带详细原因的异常。
    /// 显式传入 --data-dir 时只使用该目录（不可写则直接报错，避免用户以为配置生效）。
    /// </summary>
    public static class DataDirectoryResolver
    {
        private const string ProbeFileName = ".multidesk-write-probe";

        /// <summary>解析数据目录。report 返回最终选择/回退说明，便于写入日志。</summary>
        public static string Resolve(string explicitDir, out string report)
        {
            report = string.Empty;
            if (!string.IsNullOrEmpty(explicitDir))
            {
                string explicitError;
                string normalized = explicitDir;
                try
                {
                    normalized = Path.GetFullPath(explicitDir);
                }
                catch
                {
                }
                if (CanUse(normalized, out explicitError))
                {
                    report = "使用指定的数据目录: " + normalized;
                    return normalized;
                }
                throw new IOException("指定的数据目录不可写：" + normalized + Environment.NewLine +
                                      "原因：" + explicitError + Environment.NewLine +
                                      "请确认目录存在且当前用户有写权限，或换一个 --data-dir 目录。");
            }

            List<string> candidates = new List<string>();
            List<string> errors = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(candidates, PortableDataDir());
            AddCandidate(candidates, CombineSafe(SafeGetFolder(Environment.SpecialFolder.LocalApplicationData), "MultiDesk"));
            AddCandidate(candidates, CombineSafe(SafeGetFolder(Environment.SpecialFolder.ApplicationData), "MultiDesk"));
            try
            {
                AddCandidate(candidates, Path.Combine(Path.GetTempPath(), "MultiDesk"));
            }
            catch
            {
            }
            AddCandidate(candidates, CombineSafe(SafeGetFolder(Environment.SpecialFolder.UserProfile), "MultiDesk"));

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                string key = candidate;
                try { key = Path.GetFullPath(candidate); }
                catch { }
                if (!seen.Add(key)) continue;

                string error;
                if (CanUse(candidate, out error))
                {
                    report = "数据目录: " + candidate;
                    return candidate;
                }
                errors.Add(candidate + "  (" + error + ")");
            }

            throw new IOException("找不到可写的数据目录，程序无法保存配置。" + Environment.NewLine +
                                  "已尝试以下位置：" + Environment.NewLine +
                                  "  " + string.Join(Environment.NewLine + "  ", errors.ToArray()) + Environment.NewLine +
                                  "提示：可以用 --data-dir=<可写目录> 手动指定数据目录。");
        }

        /// <summary>程序所在目录下的便携数据目录（如不可写会在 Resolve 中自动回退）。</summary>
        public static string PortableDataDir()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry == null) return null;
                string location = entry.Location;
                if (string.IsNullOrEmpty(location)) return null;
                string dir = Path.GetDirectoryName(location);
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "MultiDesk-data");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>探测目录是否可创建并可写。</summary>
        public static bool CanUse(string dir, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(dir))
            {
                error = "路径为空";
                return false;
            }
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ProbeFileName);
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void AddCandidate(List<string> list, string path)
        {
            if (!string.IsNullOrEmpty(path)) list.Add(path);
        }

        private static string CombineSafe(string folder, string child)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                return Path.Combine(folder, child);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch
            {
                return null;
            }
        }
    }
}
