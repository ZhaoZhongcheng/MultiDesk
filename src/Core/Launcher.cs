using System;
using System.Diagnostics;
using System.IO;

namespace MultiDesk.Core
{
    /// <summary>启动桌面图标对应的文件/文件夹/网址。</summary>
    public static class Launcher
    {
        /// <summary>启动图标；成功返回 null，失败返回错误信息。</summary>
        public static string Launch(IconEntry icon)
        {
            Process process;
            return Launch(icon, out process);
        }

        /// <summary>启动图标，并通过 process 返回新进程（文件夹/系统外壳启动时可能为 null）。</summary>
        public static string Launch(IconEntry icon, out Process process)
        {
            process = null;
            if (icon == null) return "图标为空";
            string target = icon.TargetPath;
            if (string.IsNullOrEmpty(target)) return "图标目标为空";
            try
            {
                if (Directory.Exists(target))
                {
                    ProcessStartInfo psiDir = new ProcessStartInfo();
                    psiDir.FileName = "explorer.exe";
                    psiDir.Arguments = "\"" + target + "\"";
                    psiDir.UseShellExecute = true;
                    process = Process.Start(psiDir);
                    return null;
                }
                if (File.Exists(target))
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = target;
                    psi.UseShellExecute = true;
                    if (!string.IsNullOrEmpty(icon.Arguments)) psi.Arguments = icon.Arguments;
                    if (!string.IsNullOrEmpty(icon.WorkingDirectory) && Directory.Exists(icon.WorkingDirectory))
                    {
                        psi.WorkingDirectory = icon.WorkingDirectory;
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                    }
                    process = Process.Start(psi);
                    return null;
                }
                // 可能是网址、shell: 等特殊目标
                ProcessStartInfo psi2 = new ProcessStartInfo();
                psi2.FileName = target;
                psi2.UseShellExecute = true;
                if (!string.IsNullOrEmpty(icon.Arguments)) psi2.Arguments = icon.Arguments;
                process = Process.Start(psi2);
                return null;
            }
            catch (Exception ex)
            {
                // 受限权限环境下直接 ShellExecute 可能被拒绝（例如打开新建的文档），
                // 回退为让资源管理器（通常是普通权限进程）去打开它。
                string fallbackError = TryOpenViaExplorer(target, icon.Arguments);
                if (fallbackError == null) return null;
                return ex.Message;
            }
        }

        /// <summary>通过资源管理器打开文件/文件夹（受限环境下的回退方案）。</summary>
        private static string TryOpenViaExplorer(string target, string arguments)
        {
            try
            {
                if (string.IsNullOrEmpty(target)) return "目标为空";
                string extension = Path.GetExtension(target);
                if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(arguments))
                {
                    return "参数程序无法通过资源管理器回退";
                }
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = "\"" + target + "\"";
                psi.UseShellExecute = true;
                Process.Start(psi);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>打开资源管理器并选中指定文件/文件夹。</summary>
        public static string OpenFileLocation(string target)
        {
            if (string.IsNullOrEmpty(target)) return "路径为空";
            try
            {
                if (File.Exists(target))
                {
                    Process.Start("explorer.exe", "/select,\"" + target + "\"");
                    return null;
                }
                if (Directory.Exists(target))
                {
                    Process.Start("explorer.exe", "\"" + target + "\"");
                    return null;
                }
                return "路径不存在：" + target;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>由文件路径构造图标条目（显示名取文件名去扩展名）。</summary>
        public static IconEntry CreateFromPath(string path)
        {
            IconEntry ic = new IconEntry();
            ic.TargetPath = path;
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) name = path;
            ic.DisplayName = name;
            return ic;
        }
    }
}
