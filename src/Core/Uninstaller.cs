using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace MultiDesk.Core
{
    /// <summary>
    /// 卸载逻辑：退出正在运行的程序、恢复隐藏窗口、删除程序文件/快捷方式/开机启动/托盘设置，
    /// 可选删除用户配置数据（MultiDesk-data）。
    /// 
    /// 注意：只删除本程序已知的文件，不会删除目标目录里的其它文件；目录只有在变空后才会被删除。
    /// </summary>
    public static class Uninstaller
    {
        /// <summary>本程序发行的已知文件（可安全删除）。</summary>
        public static readonly string[] KnownFiles =
        {
            "MultiDesk.exe",
            "app.ico",
            "使用说明.txt",
            "启动多桌面助手.bat",
            "多桌面助手.lnk",
            "卸载多桌面助手.bat",
            "卸载多桌面助手.lnk",
            "解除安全警告.bat"
        };

        private static readonly string[] ShortcutNames =
        {
            "多桌面助手.lnk",
            "卸载多桌面助手.lnk",
            "MultiDesk.lnk",
            "MultiDesk 多桌面助手.lnk"
        };

        /// <summary>查找本程序使用的数据目录（便携目录优先，其次 AppData 等位置）。</summary>
        public static string FindDataDir(string targetDir)
        {
            try
            {
                string portable = Path.Combine(targetDir, "MultiDesk-data");
                if (Directory.Exists(portable)) return portable;

                string[] candidates = new string[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiDesk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiDesk"),
                    Path.Combine(Path.GetTempPath(), "MultiDesk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MultiDesk")
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (Directory.Exists(candidates[i])) return candidates[i];
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>等待指定进程退出（提权/看门狗重启等场景）。</summary>
        public static void WaitForProcessExit(int processId, int timeoutMs)
        {
            if (processId <= 0) return;
            try
            {
                Process p = Process.GetProcessById(processId);
                p.WaitForExit(Math.Max(1000, timeoutMs));
            }
            catch
            {
            }
        }

        /// <summary>请求正在运行的程序正常退出（会恢复隐藏窗口和系统桌面图标）；失败时返回 false。</summary>
        public static bool RequestQuit(string targetExe, string dataDir, int timeoutMs)
        {
            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe)) return false;
            try
            {
                string args = "--quit";
                if (!string.IsNullOrEmpty(dataDir)) args += " --data-dir=\"" + dataDir + "\"";
                ProcessStartInfo psi = new ProcessStartInfo(targetExe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(targetExe);
                Process p = Process.Start(psi);
                if (p == null) return false;
                return p.WaitForExit(Math.Max(1000, timeoutMs));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>强制结束从目标 exe 路径运行的程序进程（看门狗会恢复被隐藏的窗口）。</summary>
        public static bool KillInstances(string targetExe)
        {
            bool killed = false;
            try
            {
                string expected = Path.GetFullPath(targetExe);
                Process[] processes = Process.GetProcessesByName("MultiDesk");
                for (int i = 0; i < processes.Length; i++)
                {
                    Process p = processes[i];
                    try
                    {
                        string path = null;
                        try { path = p.MainModule.FileName; } catch { }
                        if (!string.IsNullOrEmpty(path) && !string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        p.Kill();
                        killed = true;
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch
            {
            }
            return killed;
        }

        /// <summary>删除程序文件与目录（目录只在变空后删除）。返回日志行。</summary>
        public static List<string> RemoveFiles(string targetDir, bool removeData)
        {
            List<string> log = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
                {
                    log.Add("目标目录不存在：" + targetDir);
                    return log;
                }
                if (removeData)
                {
                    string portableData = Path.Combine(targetDir, "MultiDesk-data");
                    if (Directory.Exists(portableData))
                    {
                        try
                        {
                            Directory.Delete(portableData, true);
                            log.Add("已删除配置数据：" + portableData);
                        }
                        catch (Exception ex)
                        {
                            log.Add("删除配置数据失败：" + ex.Message);
                        }
                    }
                }
                for (int i = 0; i < KnownFiles.Length; i++)
                {
                    string file = Path.Combine(targetDir, KnownFiles[i]);
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                            log.Add("已删除：" + KnownFiles[i]);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Add("删除 " + KnownFiles[i] + " 失败：" + ex.Message);
                    }
                }
                // 目录只有在没有其它文件时才删除（避免误删用户自己的文件）
                try
                {
                    if (Directory.Exists(targetDir) && Directory.GetFileSystemEntries(targetDir).Length == 0)
                    {
                        Directory.Delete(targetDir, false);
                        log.Add("已删除目录：" + targetDir);
                    }
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                log.Add("删除文件失败：" + ex.Message);
            }
            return log;
        }

        /// <summary>删除桌面/开始菜单/任务栏固定区里指向本程序的快捷方式。</summary>
        public static List<string> RemoveShortcuts(string targetExe)
        {
            List<string> log = new List<string>();
            string[] folders = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
            };
            for (int f = 0; f < folders.Length; f++)
            {
                try
                {
                    string folder = folders[f];
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                    for (int n = 0; n < ShortcutNames.Length; n++)
                    {
                        string file = Path.Combine(folder, ShortcutNames[n]);
                        try
                        {
                            if (File.Exists(file))
                            {
                                File.Delete(file);
                                log.Add("已删除快捷方式：" + file);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
            return log;
        }

        /// <summary>删除开机自启注册表项（HKCU\...\Run 下的 MultiDesk）。</summary>
        public static bool RemoveAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return false;
                    if (key.GetValue("MultiDesk") == null) return false;
                    key.DeleteValue("MultiDesk", false);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>删除 Windows 11 通知区域里本程序图标的设置。</summary>
        public static int RemoveNotifyIconSettings(string targetExe)
        {
            int removed = 0;
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return 0;
                    string[] names = root.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        try
                        {
                            using (RegistryKey item = root.OpenSubKey(names[i], true))
                            {
                                if (item == null) continue;
                                string path = item.GetValue("ExecutablePath") as string;
                                if (string.IsNullOrEmpty(path)) continue;
                                if (!string.Equals(path, targetExe, StringComparison.OrdinalIgnoreCase)) continue;
                                item.Close();
                                root.DeleteSubKeyTree(names[i], false);
                                removed++;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return removed;
        }

        /// <summary>执行完整卸载流程，返回日志。</summary>
        public static List<string> Execute(string targetDir, string targetExe, int ownerProcessId,
            bool removeData, bool removeShortcuts, int waitProcessMs)
        {
            List<string> log = new List<string>();
            log.Add("开始卸载：" + (targetExe ?? targetDir));
            WaitForProcessExit(ownerProcessId, Math.Max(2000, waitProcessMs));
            string dataDir = FindDataDir(targetDir);
            bool quitOk = RequestQuit(targetExe, dataDir, 6000);
            if (!quitOk)
            {
                log.Add("未能请求正常退出（可能程序未运行），尝试结束进程…");
                if (KillInstances(targetExe)) log.Add("已结束仍在运行的 MultiDesk 进程（看门狗会恢复被隐藏窗口）。");
                // 给看门狗恢复窗口/桌面图标一点时间
                System.Threading.Thread.Sleep(1500);
            }
            else
            {
                log.Add("已请求程序正常退出（窗口/桌面图标已恢复）。");
            }
            if (removeShortcuts)
            {
                log.AddRange(RemoveShortcuts(targetExe));
            }
            if (RemoveAutoStart()) log.Add("已删除开机自动启动项。");
            int icons = RemoveNotifyIconSettings(targetExe);
            if (icons > 0) log.Add("已删除托盘图标设置 " + icons + " 项。");
            log.AddRange(RemoveFiles(targetDir, removeData));
            log.Add("卸载完成。");
            return log;
        }

        /// <summary>用 cmd 在本进程退出后删除临时目录（卸载器自身在临时目录中运行）。</summary>
        public static void ScheduleTempCleanup(string tempDir)
        {
            try
            {
                if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir)) return;
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + tempDir + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch
            {
            }
        }

        /// <summary>把日志写入临时文件，便于静默模式/排错。</summary>
        public static string WriteLog(List<string> log)
        {
            return WriteLog(log, null);
        }

        /// <summary>写卸载日志：优先 %TEMP%，不可写时写到 fallbackDir。</summary>
        public static string WriteLog(List<string> log, string fallbackDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]");
            if (log != null)
            {
                for (int i = 0; i < log.Count; i++) sb.AppendLine(log[i]);
            }
            string text = sb.ToString();
            string path = Path.Combine(Path.GetTempPath(), "MultiDesk-uninstall.log");
            try
            {
                File.WriteAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch
            {
            }
            try
            {
                if (!string.IsNullOrEmpty(fallbackDir) && Directory.Exists(fallbackDir))
                {
                    path = Path.Combine(fallbackDir, "MultiDesk-uninstall.log");
                    File.WriteAllText(path, text, Encoding.UTF8);
                    return path;
                }
            }
            catch
            {
            }
            return path;
        }
    }
}
