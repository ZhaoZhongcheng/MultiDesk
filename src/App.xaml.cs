using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MultiDesk.Core;
using MultiDesk.Native;

namespace MultiDesk
{
    /// <summary>
    /// 应用入口：解析命令行、单实例控制、启动总控制器或看门狗/自测模式。
    /// 
    /// 支持的参数：
    ///   --data-dir=&lt;dir&gt;   使用指定数据目录（配置/日志/会话日志）
    ///   --ui-only            开发/演示模式：只显示桌面图层与（可选）设置窗口，不接管真实窗口
    ///   --show-settings      配合 --ui-only：启动时打开设置窗口
    ///   --exit-after=&lt;sec&gt;   若干秒后自动退出（便于自动化截图/测试）
    ///   --manage-only=&lt;进程名&gt;  安全测试模式：只管理指定进程名的窗口（不碰用户其他窗口）
    ///   --auto-switch=&lt;秒&gt;:&lt;序号&gt;,...  调试用：按计划自动切换桌面（例如 3:2,6:1）
    ///   --selftest           运行引擎自动化自测（只管理测试程序窗口），退出码 0=通过
    ///   --watchdog &lt;pid&gt; &lt;journal&gt;  内部使用：看门狗进程
    ///   --autostart          开机自启时使用（当前行为与普通启动相同）
    /// </summary>
    public partial class App : Application
    {
        private Mutex _instanceMutex;
        private EventWaitHandle _activationEvent;
        private AppController _controller;
        private string _dataDir;
        private EventWaitHandle _quitEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "md-startup-trace.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " args=" + string.Join("|", e.Args) + Environment.NewLine);
            }
            catch { }
            base.OnStartup(e);
            try { MultiDesk.Core.FileUnblocker.UnblockSelfAndSiblings(); } catch { }
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
            DispatcherUnhandledException += OnDispatcherException;
            try
            {
                Run(e.Args);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                try
                {
                    MessageBox.Show("多桌面助手启动失败：" + ex.Message, "多桌面助手",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                Shutdown(1);
            }
        }

        private void Run(string[] args)
        {
            bool uiOnly = false;
            bool showSettings = false;
            bool selftest = false;
            bool testWindow = false;
            string dataDir = null;
            int exitAfter = 0;
            int watchdogPid = -1;
            string watchdogJournal = null;
            string manageOnly = null;
            string autoSwitchSpec = null;
            bool uninstall = false;
            bool uninstallRun = false;
            bool uninstallSilent = false;
            bool uninstallRemoveData = false;
            string uninstallTargetDir = null;
            string uninstallTargetExe = null;
            int uninstallOwnerPid = 0;
            string uninstallTempDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? string.Empty;
                if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    dataDir = arg.Substring("--data-dir=".Length).Trim('"');
                }
                else if (string.Equals(arg, "--ui-only", StringComparison.OrdinalIgnoreCase)) uiOnly = true;
                else if (string.Equals(arg, "--show-settings", StringComparison.OrdinalIgnoreCase)) showSettings = true;
                else if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                else if (string.Equals(arg, "--test-window", StringComparison.OrdinalIgnoreCase)) testWindow = true;
                else if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (string.Equals(arg, "--uninstall-run", StringComparison.OrdinalIgnoreCase)) uninstallRun = true;
                else if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)) uninstallSilent = true;
                else if (string.Equals(arg, "--remove-data", StringComparison.OrdinalIgnoreCase)) uninstallRemoveData = true;
                else if (string.Equals(arg, "--quit", StringComparison.OrdinalIgnoreCase)) { SendQuit(dataDir); return; }
                else if (arg.StartsWith("--target-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    uninstallTargetDir = arg.Substring("--target-dir=".Length).Trim('"');
                }
                else if (arg.StartsWith("--uninstall-exe=", StringComparison.OrdinalIgnoreCase))
                {
                    uninstallTargetExe = arg.Substring("--uninstall-exe=".Length).Trim('"');
                }
                else if (arg.StartsWith("--uninstall-owner-pid=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(arg.Substring("--uninstall-owner-pid=".Length), out uninstallOwnerPid);
                }
                else if (arg.StartsWith("--uninstall-temp=", StringComparison.OrdinalIgnoreCase))
                {
                    uninstallTempDir = arg.Substring("--uninstall-temp=".Length).Trim('"');
                }
                else if (arg.StartsWith("--exit-after=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(arg.Substring("--exit-after=".Length), out exitAfter);
                }
                else if (arg.StartsWith("--manage-only=", StringComparison.OrdinalIgnoreCase))
                {
                    manageOnly = arg.Substring("--manage-only=".Length).Trim('"');
                }
                else if (arg.StartsWith("--auto-switch=", StringComparison.OrdinalIgnoreCase))
                {
                    autoSwitchSpec = arg.Substring("--auto-switch=".Length).Trim('"');
                }
                else if (string.Equals(arg, "--watchdog", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 2 < args.Length)
                    {
                        int.TryParse(args[i + 1], out watchdogPid);
                        watchdogJournal = args[i + 2];
                        i += 2;
                    }
                }
            }

            bool dataDirExplicit = !string.IsNullOrEmpty(dataDir);
            _dataDir = dataDir;

            if (uninstall || uninstallRun)
            {
                try
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "md-uninst-trace.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " uninstall=" + uninstall + " run=" + uninstallRun
                        + " silent=" + uninstallSilent + " removeData=" + uninstallRemoveData
                        + " targetDir=" + uninstallTargetDir + " targetExe=" + uninstallTargetExe
                        + " owner=" + uninstallOwnerPid + Environment.NewLine);
                }
                catch { }
                HandleUninstall(uninstall, uninstallRun, uninstallSilent, uninstallRemoveData,
                    uninstallTargetDir, uninstallTargetExe, uninstallOwnerPid, uninstallTempDir);
                return;
            }

            if (watchdogPid > 0 && !string.IsNullOrEmpty(watchdogJournal))
            {
                int code = Watchdog.Run(watchdogPid, watchdogJournal);
                Environment.ExitCode = code;
                Shutdown(code);
                return;
            }

            if (selftest)
            {
                // 未显式指定 --data-dir 时交给 SelfTest 使用临时目录，
                // 避免误删/清理用户真实数据目录。
                int code = SelfTest.Run(dataDirExplicit ? dataDir : null);
                Environment.ExitCode = code;
                Shutdown(code);
                return;
            }

            if (testWindow)
            {
                ShowTestWindow(exitAfter);
                return;
            }

            // 解析一个当前进程确实可写的数据目录（便携目录优先，受限环境自动回退）。
            string dataDirReport;
            try
            {
                dataDir = DataDirectoryResolver.Resolve(dataDir, out dataDirReport);
            }
            catch (Exception ex)
            {
                ShowDataDirectoryError(ex);
                Shutdown(1);
                return;
            }
            _dataDir = dataDir;

            // 受限启动检测：以低完整性（聊天软件/沙箱/受限启动器）运行时，
            // 系统会禁止隐藏真实桌面图标、弹出原生桌面菜单、隐藏其它程序窗口。
            // 默认提示用户以完整权限重新启动；用户可在设置中关闭该提示（SkipRestrictedPrompt）。
            bool noElevationPrompt = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--no-integrity-elevation", StringComparison.OrdinalIgnoreCase)) noElevationPrompt = true;
                if (string.Equals(args[i], "--elevated-relaunch", StringComparison.OrdinalIgnoreCase)) noElevationPrompt = true;
            }
            bool lowIntegrity = !uiOnly && !noElevationPrompt && MultiDesk.Native.ProcessIntegrity.IsLowIntegrity();
            if (lowIntegrity)
            {
                AppOptions restrictedOptions = ReadOptions(dataDir);
                bool skipPrompt = restrictedOptions != null && restrictedOptions.SkipRestrictedPrompt;
                bool autoElevate = restrictedOptions == null || restrictedOptions.AutoElevateWhenRestricted;
                // 用户选择过「不再提示」：默认直接尝试以完整权限重启（只有一个 UAC 确认框，不再弹程序提示框）。
                // 这样从聊天窗口/沙箱打开也能获得完整功能：动态壁纸、原生菜单、图标与真实桌面一致。
                if (skipPrompt && autoElevate)
                {
                    string autoError;
                    if (AppRelauncher.RelaunchElevated(out autoError))
                    {
                        Shutdown(0);
                        return;
                    }
                    // UAC 被取消：静默继续受限模式
                }
            }
            if (lowIntegrity && !IsRestrictedPromptSkipped(dataDir))
            {
                MessageBoxResult choice = MessageBox.Show(
                    "检测到程序以「受限模式」运行（可能来自聊天软件、沙箱或受限启动器）。" + Environment.NewLine +
                    "在这种模式下，Windows 会阻止程序隐藏真实桌面图标、弹出原生桌面右键菜单、隐藏其它程序的窗口，" + Environment.NewLine +
                    "因此实时壁纸/桌面右键菜单/窗口隔离等功能无法正常工作。" + Environment.NewLine + Environment.NewLine +
                    "是否以完整权限重新启动？（会弹出 Windows UAC 确认框，点「是」即可）" + Environment.NewLine +
                    "选择「否」将继续以受限模式运行，并且以后不再提示（可右键托盘图标 →「以完整权限重启」，" + Environment.NewLine +
                    "或在设置中重新打开该提示）。",
                    "多桌面助手 - 受限启动", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Yes)
                {
                    string relaunchError;
                    if (AppRelauncher.RelaunchElevated(out relaunchError))
                    {
                        Shutdown(0);
                        return;
                    }
                    MessageBox.Show(
                        "以完整权限重启失败：" + (relaunchError ?? "未知错误") + Environment.NewLine +
                        "（如果 UAC 弹窗被取消，可重新运行本程序后再试。）" + Environment.NewLine +
                        "程序将继续以受限模式运行。",
                        "多桌面助手", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    RememberSkipRestrictedPrompt(dataDir);
                }
            }

            // 单实例：同一数据目录只允许运行一个实例；第二个实例唤起已有实例的设置窗口
            string suffix = HashForName(dataDir);
            string mutexName = "Local\\MultiDesk_" + suffix;
            string eventName = "Local\\MultiDesk.Activate_" + suffix;
            bool createdNew = false;
            Mutex instanceMutex = null;
            for (int attempt = 0; attempt < 20 && !createdNew; attempt++)
            {
                instanceMutex = new Mutex(true, mutexName, out createdNew);
                if (createdNew) break;
                try { instanceMutex.Dispose(); } catch { }
                instanceMutex = null;
                // 提权重启/快速重复启动时，给旧实例一点退出时间
                System.Threading.Thread.Sleep(250);
            }
            _instanceMutex = instanceMutex;
            if (!createdNew)
            {
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(eventName))
                    {
                        ev.Set();
                    }
                }
                catch { }
                Shutdown(0);
                return;
            }

            // 上次运行若异常退出，可能留下了「系统桌面图标被隐藏」的标记：先恢复。
            try
            {
                DesktopIconViewManager.RecoverFromMarker(Path.Combine(dataDir, DesktopIconViewManager.MarkerFileName));
            }
            catch
            {
            }

            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            ThreadPool.RegisterWaitForSingleObject(_activationEvent, delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_controller != null) _controller.OpenSettings();
                }));
            }, null, Timeout.Infinite, false);

            try
            {
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MultiDesk.Quit_" + suffix);
                ThreadPool.RegisterWaitForSingleObject(_quitEvent, delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            if (_controller != null)
                            {
                                _controller.Shutdown();
                                _controller = null;
                            }
                        }
                        catch
                        {
                        }
                        Shutdown(0);
                    }));
                }, null, Timeout.Infinite, false);
            }
            catch
            {
            }

            _controller = new AppController(dataDir, uiOnly, showSettings, exitAfter, manageOnly, autoSwitchSpec, dataDirExplicit);
            _controller.Start();
            // Windows 11 默认把新托盘图标折叠进「隐藏的图标」；启动后延迟重试几次，
            // 等系统真正创建好 NotifyIconSettings 条目后再把它提升到任务栏显示。
            ScheduleTrayPromotion();
        }

        /// <summary>通过命名事件请求正在运行的实例优雅退出（恢复隐藏窗口/桌面图标后退出）。</summary>
        private void SendQuit(string dataDir)
        {
            try
            {
                string resolved = dataDir;
                if (string.IsNullOrEmpty(resolved))
                {
                    try
                    {
                        string report;
                        resolved = DataDirectoryResolver.Resolve(null, out report);
                    }
                    catch
                    {
                    }
                }
                if (!string.IsNullOrEmpty(resolved))
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\MultiDesk.Quit_" + HashForName(resolved)))
                    {
                        ev.Set();
                    }
                }
            }
            catch
            {
            }
            Shutdown(0);
        }

        /// <summary>
        /// 卸载入口：
        /// --uninstall：把本程序复制到临时目录并以 --uninstall-run 启动卸载界面，然后退出自身；
        /// --uninstall-run：在临时目录中运行，显示卸载窗口（--silent 则直接执行卸载）。
        /// </summary>
        private void HandleUninstall(bool uninstallArg, bool runArg, bool silent, bool removeData,
            string targetDir, string targetExe, int ownerPid, string tempDirArg)
        {
            string myExe = AutoStart.ExecutablePath;
            string myDir = string.Empty;
            try { myDir = Path.GetDirectoryName(myExe); } catch { }

            if (runArg)
            {
                string appDir = string.IsNullOrEmpty(targetDir) ? myDir : targetDir;
                string appExe = string.IsNullOrEmpty(targetExe) ? Path.Combine(appDir, "MultiDesk.exe") : targetExe;
                string tempDir = string.IsNullOrEmpty(tempDirArg) ? myDir : tempDirArg;
                if (silent)
                {
                    try
                    {
                        List<string> log = Uninstaller.Execute(appDir, appExe, ownerPid, removeData, true, 6000);
                        Uninstaller.WriteLog(log, appDir);
                    }
                    catch
                    {
                    }
                    try { Uninstaller.ScheduleTempCleanup(tempDir); } catch { }
                    Shutdown(0);
                    return;
                }
                try
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    MultiDesk.UI.UninstallWindow window = new MultiDesk.UI.UninstallWindow(appDir, appExe, ownerPid, tempDir);
                    window.Closed += delegate
                    {
                        try { Uninstaller.ScheduleTempCleanup(tempDir); } catch { }
                        Shutdown(0);
                    };
                    window.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开卸载程序：" + ex.Message, "多桌面助手",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                }
                return;
            }

            // --uninstall：复制到临时目录再运行，避免自己删除自己时文件被占用。
            try
            {
                string tempRoot = string.IsNullOrEmpty(tempDirArg) ? Path.GetTempPath() : tempDirArg;
                string temp = null;
                try
                {
                    Directory.CreateDirectory(tempRoot);
                    temp = Path.Combine(tempRoot, "MultiDeskUninstall_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                }
                catch
                {
                    temp = null;
                }
                if (string.IsNullOrEmpty(temp))
                {
                    // 受限环境/沙箱可能禁止写 %TEMP%：回退到程序目录的同级目录
                    string parent = null;
                    try { parent = Path.GetDirectoryName(myDir); } catch { }
                    if (string.IsNullOrEmpty(parent)) parent = myDir;
                    temp = Path.Combine(parent, "MultiDeskUninstall_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                }
                string copy = Path.Combine(temp, "MultiDeskUninstall.exe");
                File.Copy(myExe, copy, true);
                string childArgs = "--uninstall-run --target-dir=\"" + myDir + "\" --uninstall-exe=\"" + myExe
                    + "\" --uninstall-owner-pid=" + Process.GetCurrentProcess().Id
                    + " --uninstall-temp=\"" + temp + "\"";
                if (silent) childArgs += " --silent";
                if (removeData) childArgs += " --remove-data";
                ProcessStartInfo psi = new ProcessStartInfo(copy, childArgs);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = temp;
                Process.Start(psi);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动卸载程序：" + ex.Message, "多桌面助手",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>读取数据目录中的全局选项（读取失败返回 null，不阻塞启动）。</summary>
        private AppOptions ReadOptions(string dataDir)
        {
            try
            {
                ConfigStore store = new ConfigStore(dataDir);
                if (!store.Exists) return null;
                AppConfig cfg = store.Load();
                return cfg == null ? null : cfg.Options;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取配置中「不再提示受限模式」标记（读取失败不阻塞启动）。</summary>
        private bool IsRestrictedPromptSkipped(string dataDir)
        {
            try
            {
                ConfigStore store = new ConfigStore(dataDir);
                if (!store.Exists) return false;
                AppConfig cfg = store.Load();
                return cfg != null && cfg.Options != null && cfg.Options.SkipRestrictedPrompt;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>记住用户选择：以后以受限模式启动时不再弹提示。</summary>
        private void RememberSkipRestrictedPrompt(string dataDir)
        {
            try
            {
                ConfigStore store = new ConfigStore(dataDir);
                AppConfig cfg = store.Exists ? store.Load() : AppConfig.CreateDefault();
                if (cfg.Options == null) cfg.Options = new AppOptions();
                cfg.Options.SkipRestrictedPrompt = true;
                store.Save(cfg);
            }
            catch
            {
            }
        }

        /// <summary>延迟多次尝试把托盘图标设为在任务栏显示（Windows 11 首次创建图标条目需要时间）。</summary>
        private void ScheduleTrayPromotion()
        {
            try
            {
                int[] delays = new int[] { 2, 5, 10 };
                for (int i = 0; i < delays.Length; i++)
                {
                    DispatcherTimer timer = new DispatcherTimer();
                    timer.Interval = TimeSpan.FromSeconds(delays[i]);
                    timer.Tick += delegate(object sender, EventArgs e)
                    {
                        try
                        {
                            DispatcherTimer t = sender as DispatcherTimer;
                            if (t != null) t.Stop();
                            MultiDesk.Native.TrayIconPromoter.Promote();
                        }
                        catch
                        {
                        }
                    };
                    timer.Start();
                }
            }
            catch
            {
            }
        }

        /// <summary>数据目录不可用时的友好提示。</summary>
        private void ShowDataDirectoryError(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "多桌面助手无法创建数据目录，启动失败。" + Environment.NewLine + Environment.NewLine +
                    ex.Message + Environment.NewLine + Environment.NewLine +
                    "如果问题持续，请把程序放到一个有写权限的目录后重试，或用命令行参数指定数据目录：" + Environment.NewLine +
                    "MultiDesk.exe --data-dir=可写目录路径",
                    "多桌面助手", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            LogCrash(ex);
        }

        /// <summary>自测用的极简窗口（--test-window）：供 --selftest 创建可被管理的真实窗口。</summary>
        private void ShowTestWindow(int exitAfterSeconds)
        {
            Window window = new Window();
            window.Title = "MultiDesk 测试窗口";
            window.Width = 460;
            window.Height = 280;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Background = new SolidColorBrush(Color.FromRgb(30, 37, 54));
            TextBlock text = new TextBlock();
            text.Text = "MultiDesk 测试窗口";
            text.FontSize = 22;
            text.Foreground = Brushes.White;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            window.Content = text;
            window.Show();
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(exitAfterSeconds > 0 ? exitAfterSeconds : 120);
            timer.Tick += delegate
            {
                timer.Stop();
                Shutdown(0);
            };
            timer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_controller != null)
                {
                    _controller.Shutdown();
                    _controller = null;
                }
            }
            catch { }
            try
            {
                if (_instanceMutex != null)
                {
                    _instanceMutex.ReleaseMutex();
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                }
            }
            catch { }
            try
            {
                if (_activationEvent != null)
                {
                    _activationEvent.Dispose();
                    _activationEvent = null;
                }
            }
            catch { }
            try
            {
                if (_quitEvent != null)
                {
                    _quitEvent.Dispose();
                    _quitEvent = null;
                }
            }
            catch { }
            base.OnExit(e);
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            if (!e.Handled)
            {
                try
                {
                    MessageBox.Show("界面出现异常（已记录日志，程序将继续运行）：" + e.Exception.Message,
                        "多桌面助手", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
            e.Handled = true;
        }

        private void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            LogCrash(ex);
        }

        private void LogCrash(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + (ex == null ? "unknown" : ex.ToString()));
            string text = sb.ToString();

            // 优先写数据目录；若数据目录不可写（本次启动的常见原因），回退到程序目录/临时目录。
            List<string> targets = new List<string>();
            if (!string.IsNullOrEmpty(_dataDir)) targets.Add(Path.Combine(_dataDir, "crash.log"));
            try
            {
                string entry = System.Reflection.Assembly.GetEntryAssembly().Location;
                string entryDir = Path.GetDirectoryName(entry);
                if (!string.IsNullOrEmpty(entryDir)) targets.Add(Path.Combine(entryDir, "MultiDesk-crash.log"));
            }
            catch { }
            try
            {
                targets.Add(Path.Combine(Path.GetTempPath(), "MultiDesk-crash.log"));
            }
            catch { }

            foreach (string path in targets)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, text);
                    return;
                }
                catch { }
            }
        }

        private static string HashForName(string value)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(value.ToLowerInvariant());
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(data);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return "default";
            }
        }
    }
}
