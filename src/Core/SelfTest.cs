using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MultiDesk.Core;
using MultiDesk.Native;

namespace MultiDesk
{
    /// <summary>
    /// 引擎自动化自测（--selftest）。只管理自测启动的记事本窗口，
    /// 通过窗口过滤器保证绝不碰用户正在使用的真实窗口。
    /// 退出码 0 = 全部通过，1 = 有失败。
    /// </summary>
    public static class SelfTest
    {
        private static readonly List<string> Lines = new List<string>();
        private static int _passed;
        private static int _failed;

        public static int Run(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir))
            {
                dataDir = Path.Combine(Path.GetTempPath(), "MultiDeskSelftest");
            }
            try
            {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            }
            catch { }
            try
            {
                Directory.CreateDirectory(dataDir);
            }
            catch { }

            Add("selftest start, dataDir=" + dataDir);
            Console.WriteLine("[selftest] start, dataDir=" + dataDir);

            List<Process> processes = new List<Process>();
            DesktopEngine engine = null;
            WindowsJournal journal = null;
            Win32WindowSystem windows = new Win32WindowSystem();

            try
            {
                List<IntPtr> handles;
                RunTests(dataDir, windows, processes, out engine, out journal, out handles);
            }
            catch (Exception ex)
            {
                Fail("unexpected exception: " + ex.Message);
                Add(ex.ToString());
            }
            finally
            {
                try { if (engine != null) engine.Shutdown(); } catch { }
                foreach (Process p in processes)
                {
                    try
                    {
                        if (p != null && !p.HasExited) p.Kill();
                    }
                    catch { }
                }
                try
                {
                    if (journal != null) journal.Clear();
                }
                catch { }
            }

            string summary = "Total=" + (_passed + _failed) + " Passed=" + _passed + " Failed=" + _failed;
            Add(summary);
            Console.WriteLine("[selftest] " + summary);
            SaveLog(dataDir);
            return _failed == 0 ? 0 : 1;
        }

        private static void RunTests(string dataDir, Win32WindowSystem windows, List<Process> processes,
            out DesktopEngine engine, out WindowsJournal journal, out List<IntPtr> handles)
        {
            engine = null;
            journal = null;
            handles = new List<IntPtr>();

            AppConfig config = AppConfig.CreateDefault();
            config.Sanitize();
            DesktopProfile desktopA = config.Desktops[0];
            DesktopProfile desktopB = config.Desktops.Count > 1 ? config.Desktops[1] : null;
            if (desktopB == null)
            {
                desktopB = new DesktopProfile();
                desktopB.Name = "测试桌面B";
                config.Desktops.Add(desktopB);
            }
            config.ActiveDesktopId = desktopA.Id;
            Check("config has 2 desktops", config.Desktops.Count >= 2);

            // 启动两个经典 Win32 GUI 程序作为测试窗口（字符映射表）
            Process p1 = LaunchTestGui();
            Process p2 = LaunchTestGui();
            if (p1 != null) processes.Add(p1);
            if (p2 != null) processes.Add(p2);
            Check("launch test gui #1", p1 != null);
            Check("launch test gui #2", p2 != null);

            IntPtr h1 = WaitForWindow(windows, p1, 15000);
            IntPtr h2 = WaitForWindow(windows, p2, 15000);
            Check("find test window #1", h1 != IntPtr.Zero);
            Check("find test window #2", h2 != IntPtr.Zero);
            if (h1 != IntPtr.Zero) handles.Add(h1);
            if (h2 != IntPtr.Zero) handles.Add(h2);

            HashSet<int> testPids = new HashSet<int>();
            if (p1 != null) testPids.Add(p1.Id);
            if (p2 != null) testPids.Add(p2.Id);

            string journalPath = Path.Combine(dataDir, "session.json");
            journal = new WindowsJournal(journalPath);
            engine = new DesktopEngine(config, windows, journal, Process.GetCurrentProcess().Id);
            engine.WindowFilter = delegate(WindowInfo w)
            {
                return testPids.Contains(w.ProcessId);
            };
            engine.Initialize(desktopA.Id);
            engine.Tick();
            Check("engine current desktop = A", string.Equals(engine.CurrentDesktopId, desktopA.Id, StringComparison.Ordinal));

            List<ManagedWindowInfo> managed = engine.GetManagedWindows();
            int managedTest = 0;
            for (int i = 0; i < managed.Count; i++)
            {
                if (testPids.Contains(managed[i].ProcessId)) managedTest++;
            }
            Check("both test windows managed (count=" + managedTest + ")", managedTest >= 2);
            Check("test windows visible on desktop A", AllVisible(windows, handles, 3000));

            // 切换到 B：窗口应隐藏，进程应继续运行
            engine.SwitchTo(desktopB.Id);
            Check("switch to desktop B", string.Equals(engine.CurrentDesktopId, desktopB.Id, StringComparison.Ordinal));
            Check("test windows hidden on B", AllHidden(windows, handles, 5000));
            Check("processes still running after switch", ProcessAlive(p1) && ProcessAlive(p2));

            // 切回 A：窗口恢复
            engine.SwitchTo(desktopA.Id);
            Check("back to desktop A", string.Equals(engine.CurrentDesktopId, desktopA.Id, StringComparison.Ordinal));
            Check("test windows restored on A", AllVisible(windows, handles, 5000));

            // 移动窗口到 B
            if (h1 != IntPtr.Zero)
            {
                bool moved = engine.MoveWindowToDesktop(h1, desktopB.Id);
                Check("move window #1 to desktop B", moved);
                Check("moved window hidden (poll)", WaitFor(delegate { return !windows.IsVisible(h1); }, 3000));
            }
            engine.SwitchTo(desktopB.Id);
            Check("window #1 visible on B", h1 != IntPtr.Zero && WaitFor(delegate { return windows.IsVisible(h1); }, 5000));
            Check("window #2 hidden on B", h2 != IntPtr.Zero && WaitFor(delegate { return !windows.IsVisible(h2); }, 5000));

            // 暂停/恢复
            engine.SetPaused(true);
            Check("paused: all windows visible", AllVisible(windows, handles, 5000));
            engine.SetPaused(false);
            engine.Tick();
            Check("resumed: window #2 hidden again", AllHidden(windows, new List<IntPtr> { h2 }, 5000));

            // 会话日志
            SessionJournal session = journal.Load();
            Check("journal file has hidden window records", session != null && session.Windows != null && session.Windows.Count >= 1);
            if (session != null)
            {
                Check("journal is from current boot", session.IsFromCurrentBoot());
            }

            // 崩溃恢复：用日志把窗口恢复出来
            if (session != null && session.Windows.Count > 0)
            {
                int restored = JournalRestorer.Restore(session, windows, null);
                Check("journal restorer restores hidden window", restored >= 1);
                Check("window restored after journal restore", AllVisible(windows, handles, 5000));
                engine.Tick(); // 让引擎再次执行桌面隔离
                Check("engine re-hides leaked window", AllHidden(windows, new List<IntPtr> { h2 }, 5000));
            }

            // 恢复全部 + 退出恢复
            engine.RestoreAllWindows();
            Check("restore all windows", AllVisible(windows, handles, 5000));
            SessionJournal afterRestore = journal.Load();
            Check("journal cleared after restore", afterRestore == null || afterRestore.Windows == null || afterRestore.Windows.Count == 0);

            // 切回 A 再关闭引擎，关闭时应恢复所有窗口
            engine.SwitchTo(desktopA.Id);
            engine.Shutdown();
            Check("all windows visible after engine shutdown", AllVisible(windows, handles, 5000));
            Check("processes alive at end", ProcessAlive(p1) && ProcessAlive(p2));
        }

        // ---------------- 辅助 ----------------

        /// <summary>启动本程序自身的 --test-window 模式作为测试窗口（进程/窗口行为完全可控）。</summary>
        private static Process LaunchTestGui()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = "--test-window --exit-after=120";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                Add("launch test gui failed: " + ex.Message);
                return null;
            }
        }

        private static IntPtr WaitForWindow(IWindowSystem windows, Process process, int timeoutMs)
        {
            if (process == null) return IntPtr.Zero;
            int waited = 0;
            while (waited < timeoutMs)
            {
                try
                {
                    IList<WindowInfo> list = windows.EnumerateWindows();
                    for (int i = 0; i < list.Count; i++)
                    {
                        WindowInfo w = list[i];
                        if (w.ProcessId == process.Id && w.IsVisible)
                        {
                            return w.Handle;
                        }
                    }
                }
                catch { }
                Thread.Sleep(100);
                waited += 100;
            }
            return IntPtr.Zero;
        }

        private static bool AllVisible(IWindowSystem windows, IList<IntPtr> handles, int timeoutMs)
        {
            return WaitFor(delegate
            {
                for (int i = 0; i < handles.Count; i++)
                {
                    if (handles[i] == IntPtr.Zero) continue;
                    if (!windows.IsWindow(handles[i]) || !windows.IsVisible(handles[i])) return false;
                }
                return true;
            }, timeoutMs);
        }

        private static bool AllHidden(IWindowSystem windows, IList<IntPtr> handles, int timeoutMs)
        {
            return WaitFor(delegate
            {
                for (int i = 0; i < handles.Count; i++)
                {
                    if (handles[i] == IntPtr.Zero) continue;
                    if (!windows.IsWindow(handles[i])) continue;
                    if (windows.IsVisible(handles[i])) return false;
                }
                return true;
            }, timeoutMs);
        }

        private static bool WaitFor(Func<bool> predicate, int timeoutMs)
        {
            int waited = 0;
            while (waited <= timeoutMs)
            {
                try
                {
                    if (predicate()) return true;
                }
                catch { }
                Thread.Sleep(100);
                waited += 100;
            }
            try { return predicate(); }
            catch { return false; }
        }

        private static bool ProcessAlive(Process p)
        {
            try
            {
                return p != null && !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                _passed++;
                Add("PASS  " + name);
            }
            else
            {
                _failed++;
                Add("FAIL  " + name);
            }
        }

        private static void Fail(string message)
        {
            _failed++;
            Add("FAIL  " + message);
        }

        private static void Add(string line)
        {
            Lines.Add(line);
            try { Console.WriteLine("[selftest] " + line); }
            catch { }
        }

        private static void SaveLog(string dataDir)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Lines.Count; i++) sb.AppendLine(Lines[i]);
                File.WriteAllText(Path.Combine(dataDir, "selftest.log"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
