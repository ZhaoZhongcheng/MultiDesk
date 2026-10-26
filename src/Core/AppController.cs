using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MultiDesk.Core;
using MultiDesk.Native;
using MultiDesk.UI;

namespace MultiDesk
{
    /// <summary>
    /// 应用程序总控制器：加载配置、创建桌面引擎、桌面图层、托盘、热键与设置窗口，
    /// 并实现 IAppHost 供 UI 层调用。
    /// </summary>
    public class AppController : IAppHost
    {
        private readonly AppConfig _config;
        private readonly ConfigStore _store;
        private readonly string _dataDir;
        private readonly string _journalPath;
        private readonly bool _uiOnly;
        private readonly bool _showSettingsAtStart;
        private readonly int _exitAfterSeconds;
        private readonly string _manageOnly;
        private readonly string _autoSwitchSpec;
        private readonly bool _dataDirExplicit;
        private readonly bool _freshConfig;
        private readonly ScreenLayout _layout;
        private readonly object _logLock = new object();

        private DesktopEngine _engine;
        private IDesktopSurface _layer;
        private ISettingsSurface _settings;
        private TrayIcon _tray;
        private HotkeyManager _hotkeys;
        private DispatcherTimer _tickTimer;
        private DispatcherTimer _exitTimer;
        private Process _watchdog;
        private bool _paused;
        private bool _shuttingDown;
        private bool _desktopIntegrationAvailable = true;
        private bool _loggedIconMetrics;
        private bool _realDesktopActive;
        private int _errorLogCount;
        private readonly string _markerPath;
        private IconMetrics _layoutMetrics;
        private DesktopIconViewManager _desktopIcons;
        private DispatcherTimer _iconGuardTimer;

        public event EventHandler CurrentDesktopChanged;
        public event EventHandler ConfigChanged;
        public event EventHandler PauseStateChanged;

        public AppController(string dataDir, bool uiOnly, bool showSettingsAtStart, int exitAfterSeconds, string manageOnly, string autoSwitchSpec, bool dataDirExplicit)
        {
            _uiOnly = uiOnly;
            _showSettingsAtStart = showSettingsAtStart;
            _exitAfterSeconds = exitAfterSeconds;
            _manageOnly = manageOnly;
            _autoSwitchSpec = autoSwitchSpec;
            _dataDirExplicit = dataDirExplicit;
            _dataDir = string.IsNullOrEmpty(dataDir) ? ConfigStore.DefaultDataDir() : dataDir;
            Directory.CreateDirectory(_dataDir);
            _journalPath = Path.Combine(_dataDir, "session.json");
            _markerPath = Path.Combine(_dataDir, DesktopIconViewManager.MarkerFileName);

            try
            {
                Log("[launch] parent=" + WindowsDesktopMetricsSafeParent() + " integrity="
                    + (ProcessIntegrity.IsLowIntegrity() ? "Low" : "Normal")
                    + " exeDir=" + _dataDir);
            }
            catch
            {
            }

            _layout = ScreenLayout.Capture();
            _store = new ConfigStore(_dataDir);
            _freshConfig = !_store.Exists;
            _config = _store.Load();
            _layoutMetrics = ResolveIconMetrics();

            foreach (DesktopProfile desktop in _config.Desktops)
            {
                if (_config.Options.AutoArrangeIcons)
                {
                    IconLayout.Normalize(desktop, _layout.LayoutArea, _layoutMetrics);
                }
                else
                {
                    IconLayout.NormalizeFree(desktop, _layout.LayoutArea);
                }
            }

            if (_freshConfig)
            {
                if (_uiOnly)
                {
                    AddDemoIcons();
                }
                else
                {
                    DesktopProfile active = _config.GetActiveDesktop();
                    if (active != null)
                    {
                        int imported = DesktopImporter.ImportExistingIcons(active, _layout.LayoutArea, _layoutMetrics);
                        Log("first run: imported " + imported + " desktop icon(s)");
                    }
                }
            }
            try { _store.Save(_config); }
            catch (Exception ex) { Log("save config failed: " + ex.Message); }

            RestoreShellWindowsOnce();

            if (!_uiOnly)
            {
                Win32WindowSystem windows = new Win32WindowSystem();
                WindowsJournal journal = new WindowsJournal(_journalPath);
                _engine = new DesktopEngine(_config, windows, journal, Process.GetCurrentProcess().Id);
                if (!string.IsNullOrEmpty(_manageOnly))
                {
                    // 安全测试/调试模式：只管理指定进程名的窗口（不碰用户的其他窗口）。
                    string filterName = _manageOnly;
                    Dictionary<int, string> processNames = new Dictionary<int, string>();
                    _engine.WindowFilter = delegate(WindowInfo w)
                    {
                        // 安全过滤：不管理其他 MultiDesk 实例的桌面图层/设置窗口，只管理测试目标窗口。
                        if (!string.IsNullOrEmpty(w.Title)
                            && (w.Title.IndexOf("桌面图层", StringComparison.Ordinal) >= 0
                                || w.Title.IndexOf("多桌面助手", StringComparison.Ordinal) >= 0))
                        {
                            return false;
                        }
                        string name;
                        if (!processNames.TryGetValue(w.ProcessId, out name))
                        {
                            try { name = Process.GetProcessById(w.ProcessId).ProcessName; }
                            catch { name = string.Empty; }
                            processNames[w.ProcessId] = name;
                        }
                        return string.Equals(name, filterName, StringComparison.OrdinalIgnoreCase);
                    };
                    Log("manage-only filter enabled: " + filterName);
                }
                _engine.DesktopSwitched += OnEngineDesktopSwitched;
                _engine.LaunchHintMoved += delegate(object sender, IntPtr hwnd) { Log("launch hint: moved window 0x" + hwnd.ToInt64().ToString("X") + " to current desktop"); };
                _engine.Error += OnEngineError;
                _engine.WindowsChanged += OnEngineWindowsChanged;
            }

            // 同步开机自启注册表与配置。
            // 只在「正常模式 + 未显式指定 --data-dir」时同步，避免 --ui-only / 测试
            // 数据目录启动时意外改动用户真实的 HKCU Run 启动项。
            try
            {
                if (!_uiOnly && !_dataDirExplicit)
                {
                    if (_config.Options.StartWithWindows && !AutoStart.IsEnabled()) AutoStart.SetEnabled(true);
                    else if (!_config.Options.StartWithWindows && AutoStart.IsEnabled()) AutoStart.SetEnabled(false);
                }
            }
            catch (Exception ex)
            {
                Log("sync autostart failed: " + ex.Message);
            }
        }

        // ---------------- 启动 / 关闭 ----------------

        public void Start()
        {
            Log("starting: uiOnly=" + _uiOnly + " dataDir=" + _dataDir + " desktops=" + _config.Desktops.Count
                + " fresh=" + _freshConfig + " active=" + _config.ActiveDesktopId);

            if (_engine != null)
            {
                int recovered = _engine.RecoverFromPreviousSession();
                if (recovered > 0) Log("recovered " + recovered + " leftover hidden window(s) from previous session");
            }

            // 隐藏系统真实桌面图标，避免与程序图层图标重复（退出/崩溃恢复由 marker + 看门狗处理）
            try
            {
                _desktopIntegrationAvailable = !ProcessIntegrity.IsLowIntegrity();
                if (!_desktopIntegrationAvailable)
                {
                    Log("running at LOW integrity: system desktop icon hiding and native desktop menu are unavailable (restricted launch)");
                }
                _desktopIcons = new DesktopIconViewManager(_markerPath, Log);
                bool hidden = _desktopIntegrationAvailable && _desktopIcons.Hide();
                if (hidden) Log("system desktop icons hidden");
            }
            catch (Exception ex)
            {
                Log("hide system desktop icons failed: " + ex.Message);
            }

            // 桌面图层窗口（全屏、最底层）
            try
            {
                DesktopLayerWindow layerWindow = new DesktopLayerWindow(this);
                _layer = layerWindow;
                layerWindow.Show();
            }
            catch (Exception ex)
            {
                Log("create desktop layer failed: " + ex.Message);
            }

            if (_engine != null)
            {
                _engine.SetWindowIsolation(_config.Options != null && _config.Options.IsolateWindows);
                _engine.Initialize(CurrentDesktop == null ? _config.ActiveDesktopId : CurrentDesktop.Id);
                _engine.SetAlwaysVisibleProcesses(_config.Options == null ? null : _config.Options.AlwaysVisibleProcesses);
                _tickTimer = new DispatcherTimer(DispatcherPriority.Background);
                _tickTimer.Interval = TimeSpan.FromMilliseconds(400);
                _tickTimer.Tick += OnTick;
                _tickTimer.Start();
                SpawnWatchdog();
            }

            if (!_uiOnly)
            {
                try
                {
                    _tray = new TrayIcon(this);
                }
                catch (Exception ex)
                {
                    Log("create tray icon failed: " + ex.Message);
                }
            }

            try
            {
                _hotkeys = new HotkeyManager();
                _hotkeys.SwitchToIndexRequested += delegate(int index) { RequestSwitchIndex(index); };
                _hotkeys.PreviousRequested += RequestPrevious;
                _hotkeys.NextRequested += RequestNext;
                _hotkeys.MoveWindowPreviousRequested += delegate { MoveForegroundWindowToIndex(CurrentDesktopIndex - 1); };
                _hotkeys.MoveWindowNextRequested += delegate { MoveForegroundWindowToIndex(CurrentDesktopIndex + 1); };
                _hotkeys.OpenSettingsRequested += OpenSettings;
                _hotkeys.RestoreAllRequested += RestoreAllWindows;
                _hotkeys.Register();
                Log("hotkeys: " + _hotkeys.Report);
            }
            catch (Exception ex)
            {
                Log("register hotkeys failed: " + ex.Message);
            }

            // 资源管理器重启后图标视图会重新出现，定时确保隐藏
            if (_desktopIcons != null && _desktopIntegrationAvailable)
            {
                _iconGuardTimer = new DispatcherTimer(DispatcherPriority.Background);
                _iconGuardTimer.Interval = TimeSpan.FromSeconds(1);
                _iconGuardTimer.Tick += delegate { try { if (!_realDesktopActive) _desktopIcons.EnsureHidden(); } catch { } };
                _iconGuardTimer.Start();
            }

            if (!_desktopIntegrationAvailable && !_uiOnly && _tray != null)
            {
                _tray.ShowBalloon("受限启动模式（功能受限）",
                    "当前程序以受限权限（聊天窗口/沙箱）运行：动态壁纸、原生右键菜单、图标与真实桌面一致等功能不可用。可右键本托盘图标 →「以完整权限重启（推荐）」；建议以后从桌面快捷方式启动。");
            }

            if (_uiOnly)
            {
                if (_showSettingsAtStart) OpenSettings();
            }
            else if (_freshConfig)
            {
                OpenSettings();
            }

            StartAutoSwitch();

            if (_exitAfterSeconds > 0)
            {
                _exitTimer = new DispatcherTimer();
                _exitTimer.Interval = TimeSpan.FromSeconds(_exitAfterSeconds);
                _exitTimer.Tick += delegate
                {
                    _exitTimer.Stop();
                    Log("exit-after timer reached, shutting down");
                    Shutdown();
                };
                _exitTimer.Start();
            }
        }

        public void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            Log("shutting down");
            try { if (_tickTimer != null) _tickTimer.Stop(); } catch { }
            try { if (_exitTimer != null) _exitTimer.Stop(); } catch { }
            try { if (_iconGuardTimer != null) { _iconGuardTimer.Stop(); _iconGuardTimer = null; } } catch { }
            try { if (_desktopIcons != null) { _desktopIcons.Restore(); _desktopIcons = null; } } catch (Exception ex) { Log("restore system desktop icons failed: " + ex.Message); }
            try { if (_engine != null) _engine.Shutdown(); } catch (Exception ex) { Log("engine shutdown error: " + ex.Message); }
            try { _store.Save(_config); } catch { }
            try { if (_tray != null) { _tray.Dispose(); _tray = null; } } catch { }
            try { if (_hotkeys != null) { _hotkeys.Dispose(); _hotkeys = null; } } catch { }
            try { if (_layer != null) { _layer.PrepareForExit(); _layer = null; } } catch { }
            try
            {
                if (_settings != null)
                {
                    Window win = _settings as Window;
                    if (win != null) { win.Hide(); }
                    _settings = null;
                }
            }
            catch { }
            try
            {
                if (_watchdog != null)
                {
                    try { if (!_watchdog.HasExited) _watchdog.Kill(); }
                    catch { }
                    _watchdog = null;
                }
            }
            catch { }
            Log("shutdown complete");
            try { Application.Current.Shutdown(0); }
            catch { Environment.Exit(0); }
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (_engine != null) _engine.Tick();
            }
            catch (Exception ex)
            {
                if (_errorLogCount < 20)
                {
                    _errorLogCount++;
                    Log("tick error: " + ex.Message);
                }
            }
        }

        /// <summary>调试/验证用：按 --auto-switch=秒:序号,... 的计划自动切换桌面。</summary>
        private void StartAutoSwitch()
        {
            if (string.IsNullOrEmpty(_autoSwitchSpec)) return;
            string[] items = _autoSwitchSpec.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < items.Length; i++)
            {
                string[] pair = items[i].Split(':');
                if (pair.Length != 2) continue;
                double delay;
                int indexOneBased;
                if (!double.TryParse(pair[0], out delay)) continue;
                if (!int.TryParse(pair[1], out indexOneBased)) continue;
                int targetIndex = indexOneBased - 1;
                DispatcherTimer timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(delay > 0.2 ? delay : 0.2);
                timer.Tick += delegate
                {
                    timer.Stop();
                    Log("auto-switch to desktop index " + (targetIndex + 1));
                    RequestSwitchIndex(targetIndex);
                };
                timer.Start();
            }
        }

        private void SpawnWatchdog()
        {
            try
            {
                string exePath = AutoStart.ExecutablePath;
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = "--watchdog " + Process.GetCurrentProcess().Id + " \"" + _journalPath + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                _watchdog = Process.Start(psi);
                Log("watchdog started: pid=" + (_watchdog == null ? 0 : _watchdog.Id));
            }
            catch (Exception ex)
            {
                Log("start watchdog failed: " + ex.Message);
            }
        }

        // ---------------- 引擎事件 ----------------

        private void OnEngineDesktopSwitched(object sender, EventArgs e)
        {
            AfterDesktopChanged(true);
        }

        private void OnEngineWindowsChanged(object sender, EventArgs e)
        {
            // 菜单在打开时动态构建，这里无需刷新
        }

        private void OnEngineError(object sender, string message)
        {
            if (_errorLogCount < 50)
            {
                _errorLogCount++;
                Log("engine error: " + message);
            }
        }

        private void AfterDesktopChanged(bool showOsd)
        {
            string currentId = _engine != null ? _engine.CurrentDesktopId : _config.ActiveDesktopId;
            if (!string.IsNullOrEmpty(currentId)) _config.ActiveDesktopId = currentId;
            try { _store.Save(_config); } catch (Exception ex) { Log("save after switch failed: " + ex.Message); }

            DesktopProfile desktop = CurrentDesktop;
            if (_layer != null)
            {
                try { _layer.RefreshSurface(); } catch (Exception ex) { Log("layer refresh failed: " + ex.Message); }
                if (showOsd && desktop != null)
                {
                    try { _layer.ShowSwitchOsd(desktop.Name); } catch { }
                }
            }
            if (_tray != null) _tray.Refresh();
            if (_engine != null) Log("desktop changed -> " + (desktop == null ? "?" : desktop.Name) + ", managed windows=" + _engine.ManagedWindowCount);
            Raise(CurrentDesktopChanged);
        }

        // ---------------- IAppHost ----------------

        public AppConfig Config
        {
            get { return _config; }
        }

        public DesktopProfile CurrentDesktop
        {
            get
            {
                string id = _engine != null ? _engine.CurrentDesktopId : _config.ActiveDesktopId;
                DesktopProfile desktop = _config.FindDesktop(id);
                if (desktop == null) desktop = _config.GetActiveDesktop();
                return desktop;
            }
        }

        public int CurrentDesktopIndex
        {
            get
            {
                DesktopProfile desktop = CurrentDesktop;
                if (desktop == null) return -1;
                return _config.IndexOfDesktop(desktop.Id);
            }
        }

        public int DesktopCount
        {
            get { return _config.Desktops == null ? 0 : _config.Desktops.Count; }
        }

        public bool IsPaused
        {
            get { return _paused; }
        }

        public bool IsDesktopIntegrationAvailable
        {
            get { return _desktopIntegrationAvailable; }
        }

        public string DataDirectory
        {
            get { return _dataDir; }
        }

        private static string WindowsDesktopMetricsSafeParent()
        {
            try
            {
                string parent = ProcessTree.GetParentProcessName();
                return string.IsNullOrEmpty(parent) ? "unknown" : parent;
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>把被旧版本误隐藏的任务栏/托盘浮层窗口恢复显示（不管理它们）。</summary>
        private void RestoreShellWindowsOnce()
        {
            try
            {
                string[] classes = new string[]
                {
                    "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
                    "TopLevelWindowForOverflowXamlIsland", "Taskbar"
                };
                NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        string className = NativeMethods.GetClassNameString(hWnd);
                        for (int i = 0; i < classes.Length; i++)
                        {
                            if (string.Equals(className, classes[i], StringComparison.OrdinalIgnoreCase))
                            {
                                if (!NativeMethods.IsWindowVisible(hWnd))
                                {
                                    NativeMethods.ShowWindowAsync(hWnd, NativeMethods.SW_SHOW);
                                    Log("[shell] restored hidden shell window: " + className);
                                }
                                break;
                            }
                        }
                    }
                    catch
                    {
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }
        }

        /// <summary>计算当前应使用的图标栅格：默认跟随真实 Windows 桌面，失败时使用上次缓存，最后回退默认。</summary>
        private IconMetrics ResolveIconMetrics()
        {
            try
            {
                if (_config != null && _config.Options != null && _config.Options.MatchWindowsIconMetrics)
                {
                    IconMetrics windowsMetrics;
                    if (WindowsDesktopMetrics.TryGet(_config.Options.IconSize, out windowsMetrics))
                    {
                        RememberWindowsMetrics(windowsMetrics);
                        if (!_loggedIconMetrics)
                        {
                            _loggedIconMetrics = true;
                            Log("[icon-metrics] follow Windows desktop: icon=" + windowsMetrics.IconSize
                                + " cell=" + windowsMetrics.CellWidth.ToString("F1") + "x" + windowsMetrics.CellHeight.ToString("F1")
                                + " origin=" + windowsMetrics.MarginX.ToString("F1") + "," + windowsMetrics.MarginY.ToString("F1"));
                        }
                        return windowsMetrics;
                    }
                    IconMetrics cached = GetCachedWindowsMetrics();
                    if (cached != null)
                    {
                        if (!_loggedIconMetrics)
                        {
                            _loggedIconMetrics = true;
                            Log("[icon-metrics] use cached Windows metrics (restricted launch): icon=" + cached.IconSize
                                + " cell=" + cached.CellWidth.ToString("F1") + "x" + cached.CellHeight.ToString("F1")
                                + " origin=" + cached.MarginX.ToString("F1") + "," + cached.MarginY.ToString("F1"));
                        }
                        return cached;
                    }
                }
            }
            catch
            {
            }
            // 受限启动且从未成功读取过真实桌面时：使用与 Windows 桌面一致的实测默认度量，
            // 避免回退到旧的 96x104 栅格导致图标间距变大。
            try
            {
                if (_config != null && _config.Options != null && _config.Options.MatchWindowsIconMetrics)
                {
                    int iconSize = _config.Options.IconSize;
                    if (iconSize < 24 || iconSize > 64) iconSize = 48;
                    if (!_loggedIconMetrics)
                    {
                        _loggedIconMetrics = true;
                        Log("[icon-metrics] use default Windows-like metrics (no cache): icon=" + iconSize);
                    }
                    return new IconMetrics(iconSize, 76.6667, 98.0, 0.0, 3.3333);
                }
            }
            catch
            {
            }
            return IconLayout.CreateMetrics(_config != null && _config.Options != null ? _config.Options.IconSize : 48);
        }

        private IconMetrics GetCachedWindowsMetrics()
        {
            try
            {
                AppOptions options = _config.Options;
                if (options.WindowsIconSize >= 24 && options.WindowsIconSize <= 128
                    && options.WindowsCellWidth >= 16 && options.WindowsCellWidth <= 400
                    && options.WindowsCellHeight >= 16 && options.WindowsCellHeight <= 400)
                {
                    return new IconMetrics(options.WindowsIconSize, options.WindowsCellWidth, options.WindowsCellHeight,
                        options.WindowsOriginX, options.WindowsOriginY);
                }
            }
            catch
            {
            }
            return null;
        }

        private void RememberWindowsMetrics(IconMetrics metrics)
        {
            try
            {
                if (metrics == null) return;
                AppOptions options = _config.Options;
                if (options.WindowsIconSize == metrics.IconSize
                    && Math.Abs(options.WindowsCellWidth - metrics.CellWidth) < 0.1
                    && Math.Abs(options.WindowsCellHeight - metrics.CellHeight) < 0.1
                    && Math.Abs(options.WindowsOriginX - metrics.MarginX) < 0.1
                    && Math.Abs(options.WindowsOriginY - metrics.MarginY) < 0.1)
                {
                    return;
                }
                options.WindowsIconSize = metrics.IconSize;
                options.WindowsCellWidth = metrics.CellWidth;
                options.WindowsCellHeight = metrics.CellHeight;
                options.WindowsOriginX = metrics.MarginX;
                options.WindowsOriginY = metrics.MarginY;
                SaveConfig();
            }
            catch
            {
            }
        }

        public IconMetrics GetIconMetrics()
        {
            return _layoutMetrics;
        }

        public bool IsRealDesktopActive
        {
            get { return _realDesktopActive; }
        }

        public int HiddenWindowCount
        {
            get
            {
                try { return _engine == null ? 0 : _engine.HiddenWindowCount; }
                catch { return 0; }
            }
        }

        /// <summary>在原桌面（真实 Windows 桌面）与多桌面之间切换，不退出程序。</summary>
        public void ToggleRealDesktop()
        {
            if (_realDesktopActive) ReturnFromRealDesktop();
            else ShowRealDesktop();
        }

        /// <summary>回到原桌面：暂时暂停窗口管理、恢复所有窗口与系统桌面图标、隐藏图层。</summary>
        public void ShowRealDesktop()
        {
            if (_realDesktopActive || _shuttingDown) return;
            _realDesktopActive = true;
            Log("[real-desktop] enter real desktop mode (windows kept running)");
            try
            {
                if (_engine != null) _engine.SetPaused(true);
            }
            catch (Exception ex) { Log("pause for real desktop failed: " + ex.Message); }
            try { RestoreAllWindows(); }
            catch (Exception ex) { Log("restore windows for real desktop failed: " + ex.Message); }
            try
            {
                if (!_uiOnly && _desktopIcons != null) _desktopIcons.Restore();
            }
            catch (Exception ex) { Log("restore desktop icons for real desktop failed: " + ex.Message); }
            try
            {
                if (_layer != null) _layer.SetRealDesktopMode(true);
            }
            catch (Exception ex) { Log("hide layer for real desktop failed: " + ex.Message); }
            if (_tray != null) _tray.Refresh();
            Raise(CurrentDesktopChanged);
        }

        /// <summary>从原桌面返回当前虚拟桌面：恢复窗口隐藏策略与系统桌面图标隐藏，显示图层。</summary>
        public void ReturnFromRealDesktop()
        {
            if (!_realDesktopActive || _shuttingDown) return;
            _realDesktopActive = false;
            Log("[real-desktop] back to virtual desktop");
            try
            {
                if (_engine != null) _engine.SetPaused(false);
            }
            catch (Exception ex) { Log("resume from real desktop failed: " + ex.Message); }
            try
            {
                if (!_uiOnly && _desktopIcons != null) _desktopIcons.Hide();
            }
            catch (Exception ex) { Log("hide desktop icons after real desktop failed: " + ex.Message); }
            try
            {
                if (_layer != null)
                {
                    _layer.SetRealDesktopMode(false);
                    _layer.RefreshSurface();
                }
            }
            catch (Exception ex) { Log("show layer after real desktop failed: " + ex.Message); }
            try
            {
                if (_engine != null && CurrentDesktop != null) _engine.SwitchTo(CurrentDesktop.Id);
            }
            catch (Exception ex) { Log("reapply desktop visibility failed: " + ex.Message); }
            if (_tray != null) _tray.Refresh();
            Raise(CurrentDesktopChanged);
        }

        /// <summary>是否启用窗口隔离（切换桌面时隐藏其它桌面窗口）。</summary>
        public bool IsWindowIsolationEnabled
        {
            get { return _config != null && _config.Options != null && _config.Options.IsolateWindows; }
        }

        /// <summary>启用/关闭窗口隔离。关闭后切换桌面只切换图标/壁纸，所有程序窗口、任务栏、托盘状态保持不变。</summary>
        public void SetWindowIsolation(bool enabled)
        {
            if (_config == null) return;
            if (_config.Options == null) _config.Options = new AppOptions();
            if (_config.Options.IsolateWindows == enabled) return;
            _config.Options.IsolateWindows = enabled;
            Log("[window-isolation] " + (enabled ? "enabled" : "disabled (desktops share the same visible windows)"));
            SaveConfig();
            try
            {
                if (_engine != null) _engine.SetWindowIsolation(enabled);
            }
            catch (Exception ex) { Log("apply window isolation failed: " + ex.Message); }
            if (_tray != null) _tray.Refresh();
            Raise(ConfigChanged);
        }

        /// <summary>当前「跨桌面显示的程序」列表（进程名）。</summary>
        public IList<string> GetAlwaysVisibleProcesses()
        {
            if (_config == null || _config.Options == null || _config.Options.AlwaysVisibleProcesses == null)
            {
                return new List<string>();
            }
            return new List<string>(_config.Options.AlwaysVisibleProcesses);
        }

        /// <summary>获取窗口所属进程名（失败返回空字符串）。</summary>
        public string GetProcessNameForWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return string.Empty;
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return string.Empty;
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>进程是否被设为跨桌面显示。</summary>
        public bool IsProcessAlwaysVisible(string processName)
        {
            if (string.IsNullOrEmpty(processName) || _config == null || _config.Options == null) return false;
            IList<string> list = _config.Options.AlwaysVisibleProcesses;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>设置/取消某个程序跨桌面显示（进程名，不区分大小写）。</summary>
        public void SetProcessAlwaysVisible(string processName, bool alwaysVisible)
        {
            if (string.IsNullOrEmpty(processName)) return;
            if (_config.Options == null) _config.Options = new AppOptions();
            if (_config.Options.AlwaysVisibleProcesses == null)
            {
                _config.Options.AlwaysVisibleProcesses = new List<string>();
            }
            IList<string> list = _config.Options.AlwaysVisibleProcesses;
            string existing = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], processName, StringComparison.OrdinalIgnoreCase))
                {
                    existing = list[i];
                    break;
                }
            }
            if (alwaysVisible && existing == null)
            {
                list.Add(processName);
                Log("[always-visible] pin program: " + processName);
            }
            else if (!alwaysVisible && existing != null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i], processName, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
                }
                Log("[always-visible] unpin program: " + processName);
            }
            SaveConfig();
            try
            {
                if (_engine != null) _engine.SetAlwaysVisibleProcesses(list);
            }
            catch (Exception ex) { Log("apply always-visible failed: " + ex.Message); }
            if (_tray != null) _tray.Refresh();
            Raise(ConfigChanged);
        }

        /// <summary>把当前前台窗口所属程序固定/取消固定到所有桌面；返回进程名（失败返回空字符串）。</summary>
        public string ToggleForegroundWindowAlwaysVisible()
        {
            try
            {
                IntPtr hwnd = GetForegroundManagedWindow();
                if (hwnd == IntPtr.Zero) return string.Empty;
                string processName = GetProcessNameForWindow(hwnd);
                if (string.IsNullOrEmpty(processName)) return string.Empty;
                bool pinned = IsProcessAlwaysVisible(processName);
                SetProcessAlwaysVisible(processName, !pinned);
                if (_tray != null)
                {
                    _tray.ShowBalloon(pinned ? "已取消跨桌面显示" : "已固定到所有桌面",
                        processName + (pinned ? " 的窗口不再跨桌面显示。" : " 的窗口将在所有桌面保持显示。"));
                }
                return processName;
            }
            catch (Exception ex)
            {
                Log("toggle foreground always-visible failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>为图标显示 Windows 原生右键菜单，并在菜单操作后同步图标状态（删除/重命名后清理失效图标）。</summary>
        public bool ShowIconContextMenu(IconEntry icon, IntPtr ownerHwnd, int screenX, int screenY)
        {
            if (icon == null || string.IsNullOrEmpty(icon.TargetPath)) return false;
            string target = icon.TargetPath;
            try
            {
                bool shown = ShellContextMenu.Show(target, ownerHwnd, screenX, screenY);
                if (shown)
                {
                    // 菜单操作（删除/重命名/剪切等）完成后同步：目标文件不在了就从桌面移除该图标
                    try
                    {
                        DispatcherTimer timer = new DispatcherTimer();
                        timer.Interval = TimeSpan.FromMilliseconds(700);
                        timer.Tick += delegate
                        {
                            timer.Stop();
                            RefreshAfterShellMenu(icon, target);
                        };
                        timer.Start();
                    }
                    catch
                    {
                    }
                }
                return shown;
            }
            catch (Exception ex)
            {
                Log("show icon context menu failed: " + ex.Message);
                return false;
            }
        }

        private void RefreshAfterShellMenu(IconEntry icon, string target)
        {
            try
            {
                bool targetExists = false;
                try
                {
                    targetExists = File.Exists(target) || Directory.Exists(target);
                }
                catch
                {
                    targetExists = true;
                }
                if (!targetExists)
                {
                    // 文件被删除/重命名：从所有桌面的图标集合中移除失效条目
                    bool removed = false;
                    foreach (DesktopProfile desktop in _config.Desktops)
                    {
                        if (desktop == null || desktop.Icons == null) continue;
                        for (int i = desktop.Icons.Count - 1; i >= 0; i--)
                        {
                            IconEntry entry = desktop.Icons[i];
                            if (entry != null && string.Equals(entry.Id, icon.Id, StringComparison.Ordinal))
                            {
                                desktop.Icons.RemoveAt(i);
                                removed = true;
                            }
                        }
                    }
                    if (removed)
                    {
                        Log("[shell-menu] target no longer exists, removed icon: " + target);
                        NotifyConfigChanged();
                        if (_layer != null) _layer.RefreshSurface();
                    }
                }
                else
                {
                    if (_layer != null) _layer.RefreshSurface();
                }
            }
            catch (Exception ex)
            {
                Log("refresh after shell menu failed: " + ex.Message);
            }
        }

        public bool IsUiOnly
        {
            get { return _uiOnly; }
        }

        public ScreenLayout Layout
        {
            get { return _layout; }
        }

        public string HotkeyReport
        {
            get { return _hotkeys == null ? string.Empty : _hotkeys.Report; }
        }

        public void RequestSwitch(string desktopId)
        {
            if (_shuttingDown || string.IsNullOrEmpty(desktopId)) return;
            if (_config.FindDesktop(desktopId) == null) return;

            // 切换前先把桌面图层内容（壁纸/图标/徽章）切到目标桌面并完成渲染，
            // 这样隐藏旧窗口时立刻是正确画面，避免大面积闪烁。
            if (_layer != null)
            {
                try { _layer.PrepareDesktop(desktopId); }
                catch (Exception ex) { Log("prepare desktop failed: " + ex.Message); }
            }

            if (_engine != null)
            {
                bool ok = _engine.SwitchTo(desktopId);
                if (!ok) Log("switch to desktop failed: " + desktopId);
                return;
            }

            // 仅 UI 的开发模式：模拟桌面切换（不涉及真实窗口）
            if (string.Equals(_config.ActiveDesktopId, desktopId, StringComparison.Ordinal)) return;
            _config.ActiveDesktopId = desktopId;
            AfterDesktopChanged(true);
        }

        public void RequestSwitchIndex(int index)
        {
            if (_config.Desktops == null) return;
            if (index < 0 || index >= _config.Desktops.Count) return;
            DesktopProfile desktop = _config.Desktops[index];
            if (desktop != null) RequestSwitch(desktop.Id);
        }

        public void RequestNext()
        {
            int count = DesktopCount;
            if (count <= 0) return;
            int index = CurrentDesktopIndex;
            if (index < 0) index = 0;
            RequestSwitchIndex((index + 1) % count);
        }

        public void RequestPrevious()
        {
            int count = DesktopCount;
            if (count <= 0) return;
            int index = CurrentDesktopIndex;
            if (index < 0) index = 0;
            RequestSwitchIndex((index - 1 + count) % count);
        }

        public void SaveConfig()
        {
            try
            {
                _store.Save(_config);
            }
            catch (Exception ex)
            {
                Log("save config failed: " + ex.Message);
            }
        }

        public void NotifyConfigChanged()
        {
            _layoutMetrics = ResolveIconMetrics();
            try
            {
                if (_engine != null && _config.Options != null)
                {
                    _engine.SetAlwaysVisibleProcesses(_config.Options.AlwaysVisibleProcesses);
                    _engine.SetWindowIsolation(_config.Options.IsolateWindows);
                }
            }
            catch
            {
            }
            SaveConfig();
            if (_layer != null)
            {
                try { _layer.RefreshSurface(); } catch (Exception ex) { Log("layer refresh failed: " + ex.Message); }
            }
            if (_tray != null) _tray.Refresh();
            Raise(ConfigChanged);
        }

        /// <summary>以完整权限（UAC 提权）重新启动程序，用于解决受限启动导致的功能受限。</summary>
        public void RestartElevated()
        {
            try
            {
                string error;
                if (AppRelauncher.RelaunchElevated(out error))
                {
                    Log("relaunch elevated requested by user");
                    Shutdown();
                    return;
                }
                Log("relaunch elevated failed: " + error);
                if (_tray != null) _tray.ShowBalloon("无法以完整权限重启", error ?? "未知错误");
            }
            catch (Exception ex)
            {
                Log("relaunch elevated error: " + ex.Message);
                if (_tray != null) _tray.ShowBalloon("无法以完整权限重启", ex.Message);
            }
        }

        public void OpenSettings()
        {
            try
            {
                if (_shuttingDown) return;
                if (_settings == null)
                {
                    SettingsWindow window = new SettingsWindow(this);
                    window.Closed += delegate { _settings = null; };
                    _settings = window;
                }
                Window win = _settings as Window;
                if (win == null) return;
                if (!win.IsVisible) win.Show();
                if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                win.Activate();
                try { _settings.RefreshSettings(); } catch { }
            }
            catch (Exception ex)
            {
                Log("open settings failed: " + ex.Message);
            }
        }

        public string LaunchIcon(IconEntry icon)
        {
            if (icon == null) return "图标为空";
            try
            {
                if (_engine != null) _engine.BeginLaunchHint();
                Process process;
                string error = Launcher.Launch(icon, out process);
                if (process != null && _engine != null)
                {
                    _engine.RegisterLaunchProcess(process.Id);
                    Log("launch icon: " + icon.DisplayName + " -> pid " + process.Id);
                }
                else
                {
                    Log("launch icon: " + icon.DisplayName + (string.IsNullOrEmpty(error) ? "" : (" failed: " + error)));
                }
                return error;
            }
            catch (Exception ex)
            {
                Log("launch icon failed: " + ex.Message);
                return ex.Message;
            }
        }

        public bool ShowDesktopContextMenu(int screenX, int screenY)
        {
            bool ok = false;
            if (_desktopIntegrationAvailable)
            {
                try
                {
                    ok = DesktopShell.ShowDesktopContextMenu(screenX, screenY);
                    if (!ok) Log("show desktop context menu failed");
                }
                catch (Exception ex)
                {
                    Log("show desktop context menu error: " + ex.Message);
                }
            }
            if (!ok)
            {
                Log("native desktop menu unavailable, fallback menu should be used");
                return false;
            }
            return true;
        }

        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;
            if (_engine != null)
            {
                try { _engine.SetPaused(paused); } catch (Exception ex) { Log("set paused failed: " + ex.Message); }
            }
            if (_tray != null) _tray.Refresh();
            Log("management paused=" + paused);
            Raise(PauseStateChanged);
        }

        public void RestoreAllWindows()
        {
            if (_engine != null)
            {
                try
                {
                    _engine.RestoreAllWindows();
                    Log("restore all windows requested");
                }
                catch (Exception ex) { Log("restore all windows failed: " + ex.Message); }
            }
            if (_tray != null) _tray.Refresh();
        }

        public void SetStartWithWindows(bool enabled)
        {
            try
            {
                AutoStart.SetEnabled(enabled);
                _config.Options.StartWithWindows = enabled;
                SaveConfig();
                Log("autostart set to " + enabled);
                Raise(ConfigChanged);
            }
            catch (Exception ex)
            {
                Log("set autostart failed: " + ex.Message);
                if (_tray != null) _tray.ShowBalloon("开机自启设置失败", ex.Message);
            }
        }

        // ---------------- 托盘 / 热键辅助 ----------------

        public IntPtr GetForegroundManagedWindow()
        {
            if (_engine == null) return IntPtr.Zero;
            try { return _engine.GetForegroundManagedWindow(); }
            catch { return IntPtr.Zero; }
        }

        public bool MoveForegroundWindowTo(string desktopId)
        {
            if (_engine == null) return false;
            IntPtr hwnd = _engine.GetForegroundManagedWindow();
            if (hwnd == IntPtr.Zero) return false;
            bool ok = _engine.MoveWindowToDesktop(hwnd, desktopId);
            if (ok && _tray != null) _tray.Refresh();
            return ok;
        }

        public bool MoveForegroundWindowToIndex(int index)
        {
            if (_config.Desktops == null) return false;
            if (index < 0 || index >= _config.Desktops.Count) return false;
            DesktopProfile desktop = _config.Desktops[index];
            if (desktop == null) return false;
            return MoveForegroundWindowTo(desktop.Id);
        }

        public IList<ManagedWindowInfo> GetManagedWindows()
        {
            if (_engine == null) return new List<ManagedWindowInfo>();
            try { return _engine.GetManagedWindows(); }
            catch { return new List<ManagedWindowInfo>(); }
        }

        // ---------------- 内部辅助 ----------------

        private void AddDemoIcons()
        {
            DesktopProfile work = _config.GetActiveDesktop();
            if (work == null) return;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system32 = Path.Combine(windows, "System32");
            AddDemoIcon(work, Path.Combine(system32, "notepad.exe"), "记事本");
            AddDemoIcon(work, Path.Combine(system32, "control.exe"), "控制面板");
            AddDemoIcon(work, Path.Combine(windows, "explorer.exe"), "资源管理器");
            AddDemoIcon(work, Path.Combine(system32, "cmd.exe"), "命令提示符");
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents))
            {
                IconEntry folderIcon = new IconEntry();
                folderIcon.TargetPath = documents;
                folderIcon.DisplayName = "我的文档";
                work.Icons.Add(folderIcon);
            }
            if (_config.Options.AutoArrangeIcons)
            {
                IconLayout.Normalize(work, _layout.LayoutArea, _layoutMetrics);
            }
            else
            {
                IconLayout.NormalizeFree(work, _layout.LayoutArea);
            }
            if (_config.Desktops.Count > 1)
            {
                DesktopProfile game = _config.Desktops[1];
                if (game != null)
                {
                    game.WallpaperMode = WallpaperMode.SolidColor;
                    game.BackgroundColor = "#FF2B1B3A";
                }
            }
        }

        private static void AddDemoIcon(DesktopProfile desktop, string path, string name)
        {
            if (!File.Exists(path)) return;
            IconEntry icon = new IconEntry();
            icon.TargetPath = path;
            icon.DisplayName = name;
            desktop.Icons.Add(icon);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { Log("event handler error: " + ex.Message); }
            }
        }

        public void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
                    File.AppendAllText(Path.Combine(_dataDir, "app.log"), line);
                    Debug.WriteLine("[MultiDesk] " + message);
                }
            }
            catch
            {
            }
        }
    }
}
