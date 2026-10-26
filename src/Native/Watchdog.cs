using System;
using System.Diagnostics;
using System.Threading;
using MultiDesk.Core;

namespace MultiDesk.Native
{
    /// <summary>
    /// 看门狗：独立进程等待主进程退出；若主进程异常退出（崩溃/被强杀），
    /// 从会话日志中恢复被隐藏的窗口，避免用户程序「消失」。
    /// 
    /// 启动方式（与主程序同一个 exe）：MultiDesk.exe --watchdog &lt;mainPid&gt; &lt;journalPath&gt;
    /// </summary>
    public static class Watchdog
    {
        public static int Run(int mainPid, string journalPath)
        {
            try
            {
                // 等待主进程退出（若主进程已不存在，立即进入恢复流程）
                try
                {
                    Process main = Process.GetProcessById(mainPid);
                    main.WaitForExit();
                }
                catch (ArgumentException)
                {
                    // 主进程已经退出
                }
                catch
                {
                }

                Thread.Sleep(300); // 给主进程的正常退出恢复逻辑一点时间

                WindowsJournal journal = new WindowsJournal(journalPath);
                SessionJournal session = null;
                try { session = journal.Load(); }
                catch { }

                if (session != null && session.Windows != null && session.Windows.Count > 0 && session.IsFromCurrentBoot())
                {
                    Win32WindowSystem windows = new Win32WindowSystem();
                    int restored = JournalRestorer.Restore(session, windows, delegate(string message)
                    {
                        Debug.WriteLine("[watchdog] " + message);
                    });
                    WriteLog(journalPath, "watchdog restored " + restored + " window(s) after main process exit");
                }
                else
                {
                    WriteLog(journalPath, "watchdog: nothing to restore");
                }
                try { journal.Clear(); }
                catch { }

                // 主进程可能隐藏了系统真实桌面图标，这里一并恢复
                try
                {
                    string dataDir = System.IO.Path.GetDirectoryName(journalPath);
                    if (!string.IsNullOrEmpty(dataDir))
                    {
                        DesktopIconViewManager.RecoverFromMarker(System.IO.Path.Combine(dataDir, DesktopIconViewManager.MarkerFileName));
                    }
                }
                catch { }
                return 0;
            }
            catch (Exception ex)
            {
                WriteLog(journalPath, "watchdog error: " + ex.Message);
                return 1;
            }
        }

        private static void WriteLog(string journalPath, string message)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(journalPath);
                if (string.IsNullOrEmpty(dir)) return;
                System.IO.Directory.CreateDirectory(dir);
                string log = System.IO.Path.Combine(dir, "watchdog.log");
                System.IO.File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
