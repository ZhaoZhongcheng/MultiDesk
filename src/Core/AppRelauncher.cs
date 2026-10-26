using System;
using System.IO;
using System.Text;
using System.Diagnostics;

namespace MultiDesk.Core
{
    /// <summary>
    /// 以完整权限（UAC 提权）重新启动本程序。
    /// 
    /// 背景：从聊天软件、沙箱或受限启动器启动时，进程可能是低完整性（Low IL），
    /// 此时 Windows UIPI 禁止它操作资源管理器的桌面窗口（隐藏真实图标、原生右键菜单、
    /// 隐藏其它程序的窗口都会失败）。以管理员/完整权限重新启动可以恢复全部功能。
    /// </summary>
    public static class AppRelauncher
    {
        /// <summary>请求以完整权限重启；成功返回 true（新进程已启动，调用方应退出）。</summary>
        public static bool RelaunchElevated(out string error)
        {
            error = null;
            try
            {
                string exePath = AutoStart.ExecutablePath;
                if (string.IsNullOrEmpty(exePath)) { error = "无法获取程序路径"; return false; }

                StringBuilder args = new StringBuilder();
                string[] commandLineArgs = Environment.GetCommandLineArgs();
                for (int i = 1; i < commandLineArgs.Length; i++)
                {
                    string arg = commandLineArgs[i];
                    if (string.IsNullOrEmpty(arg)) continue;
                    args.Append('"').Append(arg.Replace("\"", "\\\"")).Append("\" ");
                }
                args.Append("--elevated-relaunch");

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = args.ToString();
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                try
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                }
                catch
                {
                }
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
