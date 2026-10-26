using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MultiDesk.Core
{
    /// <summary>
    /// 多桌面核心引擎：维护「窗口 → 桌面」映射，负责切换桌面时隐藏/恢复窗口。
    /// 纯逻辑（不直接调用 Win32），所有 Windows API 通过 IWindowSystem 注入，便于单元测试。
    /// 
    /// 设计约定：
    ///  * 所有方法都在 UI 线程调用（控制器用 DispatcherTimer 定时调用 Tick）。
    ///  * 进程从未被结束，只是 ShowWindow(SW_HIDE)，因此「切换桌面不影响程序运行」。
    ///  * 新窗口默认归属：有 owner 且 owner 已归属某桌面 → 跟随 owner；
    ///    同进程已有窗口 → 跟随该进程最近的桌面；否则归属当前桌面。
    /// </summary>
    public class DesktopEngine
    {
        private class ManagedWindow
        {
            public IntPtr Handle;
            public int ProcessId;
            public long ProcessStartTicks;
            public string DesktopId;
            public bool HiddenByUs;
            public bool WasMinimized;
            public bool WasMaximized;
            public bool WasForeground;
            public string Title;
            public string ClassName;
            public IntPtr Owner;
        }

        private readonly AppConfig _config;
        private readonly IWindowSystem _ws;
        private readonly IWindowJournal _journal;
        private readonly int _ownProcessId;
        private readonly Dictionary<IntPtr, ManagedWindow> _windows = new Dictionary<IntPtr, ManagedWindow>();
        private readonly Dictionary<int, string> _lastDesktopByProcess = new Dictionary<int, string>();
        private string _currentDesktopId;
        private bool _paused;
        private string _lastJournalSignature;
        private DateTime _launchHintUntil = DateTime.MinValue;
        private readonly Dictionary<int, DateTime> _launchProcesses = new Dictionary<int, DateTime>();
        private readonly HashSet<string> _alwaysVisibleProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _windowIsolation = true;
        private readonly Dictionary<int, string> _processNameCache = new Dictionary<int, string>();

        /// <summary>额外的窗口过滤（测试专用；返回 false 的窗口不会被管理）。</summary>
        public Func<WindowInfo, bool> WindowFilter;

        /// <summary>是否写会话日志（单元测试可关闭）。</summary>
        public bool JournalEnabled = true;

        /// <summary>切换桌面完成（当前桌面可通过 CurrentDesktopId 获取）。</summary>
        public event EventHandler DesktopSwitched;

        /// <summary>被管理窗口集合发生变化（新窗口/窗口销毁/移动桌面）。</summary>
        public event EventHandler WindowsChanged;

        /// <summary>引擎内部错误信息（如隐藏窗口失败）。</summary>
        public event EventHandler<string> Error;

        public DesktopEngine(AppConfig config, IWindowSystem windows, IWindowJournal journal, int ownProcessId)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (windows == null) throw new ArgumentNullException("windows");
            _config = config;
            _ws = windows;
            _journal = journal;
            _ownProcessId = ownProcessId;
            ReloadAlwaysVisibleProcesses();
        }

        public string CurrentDesktopId
        {
            get { return _currentDesktopId; }
        }

        public bool Paused
        {
            get { return _paused; }
        }

        public int ManagedWindowCount
        {
            get { return _windows.Count; }
        }

        private void ReloadAlwaysVisibleProcesses()
        {
            _alwaysVisibleProcesses.Clear();
            try
            {
                if (_config != null && _config.Options != null && _config.Options.AlwaysVisibleProcesses != null)
                {
                    foreach (string name in _config.Options.AlwaysVisibleProcesses)
                    {
                        if (!string.IsNullOrEmpty(name)) _alwaysVisibleProcesses.Add(name.Trim());
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>是否启用窗口隔离（true=切换桌面时隐藏其它桌面窗口；false=所有桌面共用同一套可见窗口）。</summary>
        public bool WindowIsolationEnabled
        {
            get { return _windowIsolation; }
        }

        /// <summary>启用/关闭窗口隔离。关闭时会立即恢复所有被隐藏的窗口并停止窗口管理。</summary>
        public void SetWindowIsolation(bool enabled)
        {
            if (_windowIsolation == enabled) return;
            _windowIsolation = enabled;
            if (!enabled)
            {
                RestoreAllWindows();
                _windows.Clear();
                _processNameCache.Clear();
                _lastDesktopByProcess.Clear();
                WriteJournal();
                RaiseWindowsChanged();
            }
            else
            {
                Tick();
            }
        }

        /// <summary>更新「跨桌面显示的程序」列表（进程名，不区分大小写），并立即恢复这些程序已被隐藏的窗口。</summary>
        public void SetAlwaysVisibleProcesses(IEnumerable<string> processNames)
        {
            _alwaysVisibleProcesses.Clear();
            if (processNames != null)
            {
                foreach (string name in processNames)
                {
                    if (!string.IsNullOrEmpty(name)) _alwaysVisibleProcesses.Add(name.Trim());
                }
            }
            ApplyAlwaysVisible();
        }

        /// <summary>进程是否被设置为跨桌面显示。</summary>
        public bool IsProcessAlwaysVisible(int processId)
        {
            if (_alwaysVisibleProcesses.Count == 0) return false;
            string name;
            if (!_processNameCache.TryGetValue(processId, out name))
            {
                try
                {
                    using (Process p = Process.GetProcessById(processId)) name = p.ProcessName;
                }
                catch
                {
                    name = string.Empty;
                }
                _processNameCache[processId] = name ?? string.Empty;
            }
            return !string.IsNullOrEmpty(name) && _alwaysVisibleProcesses.Contains(name);
        }

        /// <summary>把被设置为跨桌面显示、但之前被隐藏的窗口全部恢复，并移出管理集合。</summary>
        private void ApplyAlwaysVisible()
        {
            if (_alwaysVisibleProcesses.Count == 0) return;
            bool changed = false;
            List<IntPtr> release = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                if (!IsProcessAlwaysVisible(kv.Value.ProcessId)) continue;
                if (kv.Value.HiddenByUs)
                {
                    ShowManaged(kv.Value);
                    changed = true;
                }
                release.Add(kv.Key);
            }
            for (int i = 0; i < release.Count; i++)
            {
                _windows.Remove(release[i]);
                changed = true;
            }
            if (changed)
            {
                WriteJournal();
                RaiseWindowsChanged();
            }
        }

        /// <summary>用户刚刚双击了桌面图标：接下来新开的窗口/被激活的窗口应归属当前桌面。</summary>
        public void BeginLaunchHint()
        {
            _launchHintUntil = DateTime.Now.AddSeconds(10);
        }

        /// <summary>登记刚启动的进程；它的新窗口在 hint 有效期内强制归属当前桌面。</summary>
        public void RegisterLaunchProcess(int processId)
        {
            if (processId <= 0) return;
            _launchProcesses[processId] = DateTime.Now.AddSeconds(10);
            BeginLaunchHint();
        }

        /// <summary>初始化：设定当前桌面，并把已存在的窗口都归入当前桌面。</summary>
        public void Initialize(string currentDesktopId)
        {
            _currentDesktopId = currentDesktopId;
            if (_config.FindDesktop(_currentDesktopId) == null && _config.Desktops.Count > 0)
            {
                _currentDesktopId = _config.Desktops[0].Id;
            }
            Tick();
            // 首次接管：把所有可见窗口归属到当前桌面（否则它们在切换时会「消失」）
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                if (string.IsNullOrEmpty(kv.Value.DesktopId))
                {
                    kv.Value.DesktopId = _currentDesktopId;
                }
            }
            RaiseWindowsChanged();
        }

        /// <summary>恢复应用崩溃时遗留的隐藏窗口。返回恢复数量。</summary>
        public int RecoverFromPreviousSession()
        {
            if (!JournalEnabled || _journal == null) return 0;
            try
            {
                SessionJournal journal = _journal.Load();
                if (journal == null || journal.Windows == null || journal.Windows.Count == 0) return 0;
                if (!journal.IsFromCurrentBoot()) return 0;
                int count = JournalRestorer.Restore(journal, _ws, null);
                _journal.Clear();
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>定时器驱动：发现新窗口、纠正桌面归属、维护会话日志。</summary>
        public void Tick()
        {
            if (_config.Desktops == null || _config.Desktops.Count == 0) return;
            if (!_windowIsolation)
            {
                if (_windows.Count > 0)
                {
                    RestoreAllWindows();
                    _windows.Clear();
                    _processNameCache.Clear();
                    _lastDesktopByProcess.Clear();
                    WriteJournal();
                    RaiseWindowsChanged();
                }
                return;
            }
            IList<WindowInfo> snapshot = _ws.EnumerateWindows();
            HashSet<IntPtr> alive = new HashSet<IntPtr>();
            bool changed = false;

            // 1) 清理已销毁窗口
            List<IntPtr> dead = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                if (!_ws.IsWindow(kv.Key))
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (IntPtr h in dead)
            {
                _windows.Remove(h);
                changed = true;
            }

            // 当前前台窗口（用于记录每个桌面的「活动窗口」）
            IntPtr foreground = IntPtr.Zero;
            try { foreground = _ws.GetForegroundWindow(); } catch { }

            // 2) 扫描窗口
            foreach (WindowInfo w in snapshot)
            {
                if (w == null || w.Handle == IntPtr.Zero) continue;
                alive.Add(w.Handle);
                if (w.ProcessId == _ownProcessId) continue;
                if (IsProcessAlwaysVisible(w.ProcessId))
                {
                    ManagedWindow pinned;
                    if (_windows.TryGetValue(w.Handle, out pinned))
                    {
                        if (pinned.HiddenByUs) ShowManaged(pinned);
                        _windows.Remove(w.Handle);
                        changed = true;
                    }
                    continue;
                }
                if (!IsManageable(w)) continue;

                ManagedWindow mw;
                if (!_windows.TryGetValue(w.Handle, out mw))
                {
                    if (!w.IsVisible) continue; // 只接管「用户看得见」的新窗口
                    mw = new ManagedWindow();
                    mw.Handle = w.Handle;
                    mw.ProcessId = w.ProcessId;
                    mw.ProcessStartTicks = GetProcessStartTicks(w.ProcessId);
                    mw.DesktopId = ResolveInitialDesktop(w);
                    mw.WasForeground = false;
                    _windows[w.Handle] = mw;
                    changed = true;
                    _lastDesktopByProcess[w.ProcessId] = mw.DesktopId;
                }

                // 更新元数据
                mw.Title = w.Title;
                mw.ClassName = w.ClassName;
                mw.Owner = w.Owner;

                bool visible = w.IsVisible;
                if (visible && w.Handle == foreground)
                {
                    mw.WasForeground = true;
                }

                // 3) 可见性与桌面归属一致性维护
                if (!_paused)
                {
                    if (visible && !mw.HiddenByUs && !string.Equals(mw.DesktopId, _currentDesktopId, StringComparison.Ordinal))
                    {
                        // 别的桌面的窗口露出来了：隐藏它
                        RecordWindowState(mw);
                        HideManaged(mw);
                        changed = true;
                    }
                    else if (visible && mw.HiddenByUs && !string.Equals(mw.DesktopId, _currentDesktopId, StringComparison.Ordinal))
                    {
                        // 应用自己又显示出来了：再次隐藏
                        HideManaged(mw);
                        changed = true;
                    }
                    else if (!visible && mw.HiddenByUs && string.Equals(mw.DesktopId, _currentDesktopId, StringComparison.Ordinal))
                    {
                        // 回到自己的桌面：恢复显示
                        ShowManaged(mw);
                        changed = true;
                    }
                }
            }

            // 3.5) 用户刚启动图标：如果激活的窗口属于其它桌面（单实例应用常见），拉到当前桌面显示
            if (!_paused && DateTime.Now < _launchHintUntil)
            {
                IntPtr hintForeground = IntPtr.Zero;
                try { hintForeground = _ws.GetForegroundWindow(); } catch { }
                ManagedWindow hintWindow;
                if (hintForeground != IntPtr.Zero && _windows.TryGetValue(hintForeground, out hintWindow)
                    && !string.Equals(hintWindow.DesktopId, _currentDesktopId, StringComparison.Ordinal))
                {
                    MoveWindowToDesktop(hintForeground, _currentDesktopId);
                    if (!_ws.IsVisible(hintForeground)) ShowManaged(hintWindow);
                    try { _ws.SetForegroundWindow(hintForeground); } catch { }
                    LogLaunchHintMove(hintForeground);
                    changed = true;
                }
                // 清理过期的进程登记
                if (_launchProcesses.Count > 0)
                {
                    List<int> expired = new List<int>();
                    foreach (KeyValuePair<int, DateTime> pair in _launchProcesses)
                    {
                        if (pair.Value <= DateTime.Now) expired.Add(pair.Key);
                    }
                    foreach (int pid in expired) _launchProcesses.Remove(pid);
                }
            }

            // 4) 暂停模式：把所有被隐藏窗口恢复显示
            if (_paused)
            {
                foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
                {
                    if (kv.Value.HiddenByUs)
                    {
                        ShowManaged(kv.Value);
                        changed = true;
                    }
                }
            }

            WriteJournal();

            if (changed) RaiseWindowsChanged();
        }

        /// <summary>切换桌面：隐藏旧桌面窗口，恢复目标桌面窗口。返回 false 表示桌面不存在。</summary>
        /// <summary>其它桌面上被隐藏、但进程仍在运行的窗口数量。</summary>
        public int HiddenWindowCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
                {
                    if (kv.Value.HiddenByUs) count++;
                }
                return count;
            }
        }

        public bool SwitchTo(string desktopId)
        {
            DesktopProfile target = _config.FindDesktop(desktopId);
            if (target == null) return false;
            if (string.Equals(desktopId, _currentDesktopId, StringComparison.Ordinal)) return true;

            if (!_windowIsolation)
            {
                // 窗口隔离关闭：只更新当前桌面（图标/壁纸由控制器刷新），不隐藏/恢复任何窗口，
                // 任务栏、托盘、程序状态完全不变。
                _currentDesktopId = desktopId;
                _config.ActiveDesktopId = desktopId;
                WriteJournal();
                EventHandler isolationOffHandler = DesktopSwitched;
                if (isolationOffHandler != null) isolationOffHandler(this, EventArgs.Empty);
                return true;
            }

            // 记录前台窗口
            IntPtr foreground = IntPtr.Zero;
            try { foreground = _ws.GetForegroundWindow(); } catch { }

            string oldDesktopId = _currentDesktopId;

            // 隐藏旧桌面窗口
            List<ManagedWindow> oldWindows = new List<ManagedWindow>();
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                if (string.Equals(kv.Value.DesktopId, oldDesktopId, StringComparison.Ordinal) && !kv.Value.HiddenByUs)
                {
                    oldWindows.Add(kv.Value);
                }
            }
            foreach (ManagedWindow mw in oldWindows)
            {
                try
                {
                    if (!_ws.IsWindow(mw.Handle)) continue;
                    RecordWindowState(mw);
                    if (mw.Handle == foreground) mw.WasForeground = true;
                    if (_ws.IsVisible(mw.Handle)) HideManaged(mw);
                }
                catch (Exception ex)
                {
                    RaiseError("隐藏窗口失败: " + ex.Message);
                }
            }

            // 切换当前桌面
            _currentDesktopId = desktopId;
            _config.ActiveDesktopId = desktopId;
            WriteJournal();

            // 显示目标桌面窗口（先显示无 owner 的窗口，再显示有 owner 的窗口）
            List<ManagedWindow> showList = new List<ManagedWindow>();
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                if (string.Equals(kv.Value.DesktopId, desktopId, StringComparison.Ordinal) && kv.Value.HiddenByUs)
                {
                    showList.Add(kv.Value);
                }
            }
            showList.Sort(delegate(ManagedWindow a, ManagedWindow b)
            {
                int ao = a.Owner != IntPtr.Zero && _windows.ContainsKey(a.Owner) ? 1 : 0;
                int bo = b.Owner != IntPtr.Zero && _windows.ContainsKey(b.Owner) ? 1 : 0;
                return ao.CompareTo(bo);
            });
            foreach (ManagedWindow mw in showList)
            {
                try
                {
                    ShowManaged(mw);
                }
                catch (Exception ex)
                {
                    RaiseError("恢复窗口失败: " + ex.Message);
                }
            }

            // 恢复前台窗口
            ManagedWindow fg = null;
            foreach (ManagedWindow mw in showList)
            {
                if (mw.WasForeground && _ws.IsVisible(mw.Handle))
                {
                    fg = mw;
                    break;
                }
            }
            if (fg != null)
            {
                try { _ws.SetForegroundWindow(fg.Handle); } catch { }
            }
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                kv.Value.WasForeground = false;
            }

            WriteJournal();
            RaiseWindowsChanged();
            EventHandler handler = DesktopSwitched;
            if (handler != null) handler(this, EventArgs.Empty);
            return true;
        }

        /// <summary>把某个窗口移动到指定桌面（若移动到非当前桌面会立即隐藏）。</summary>
        public bool MoveWindowToDesktop(IntPtr hwnd, string desktopId)
        {
            if (_config.FindDesktop(desktopId) == null) return false;
            ManagedWindow mw;
            if (!_windows.TryGetValue(hwnd, out mw)) return false;
            if (string.Equals(mw.DesktopId, desktopId, StringComparison.Ordinal)) return true;
            mw.DesktopId = desktopId;
            _lastDesktopByProcess[mw.ProcessId] = desktopId;
            if (!string.Equals(desktopId, _currentDesktopId, StringComparison.Ordinal))
            {
                if (_ws.IsVisible(hwnd))
                {
                    RecordWindowState(mw);
                    HideManaged(mw);
                }
            }
            else if (mw.HiddenByUs)
            {
                ShowManaged(mw);
            }
            WriteJournal();
            RaiseWindowsChanged();
            return true;
        }

        /// <summary>当前前台窗口（若被本引擎管理）。</summary>
        public IntPtr GetForegroundManagedWindow()
        {
            try
            {
                IntPtr fg = _ws.GetForegroundWindow();
                if (_windows.ContainsKey(fg)) return fg;
            }
            catch
            {
            }
            return IntPtr.Zero;
        }

        /// <summary>所有被管理窗口的快照（按进程名/标题排序）。</summary>
        public List<ManagedWindowInfo> GetManagedWindows()
        {
            List<ManagedWindowInfo> list = new List<ManagedWindowInfo>();
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                ManagedWindow mw = kv.Value;
                ManagedWindowInfo info = new ManagedWindowInfo();
                info.Handle = mw.Handle;
                info.ProcessId = mw.ProcessId;
                info.Title = mw.Title;
                info.ClassName = mw.ClassName;
                info.DesktopId = mw.DesktopId;
                info.HiddenByUs = mw.HiddenByUs;
                info.WasMinimized = mw.WasMinimized;
                info.WasMaximized = mw.WasMaximized;
                info.WasForeground = mw.WasForeground;
                info.ProcessStartTicks = mw.ProcessStartTicks;
                try { info.IsVisible = _ws.IsVisible(mw.Handle); } catch { }
                list.Add(info);
            }
            return list;
        }

        /// <summary>恢复所有被隐藏的窗口（不改变桌面归属）。</summary>
        public void RestoreAllWindows()
        {
            bool changed = false;
            foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
            {
                if (kv.Value.HiddenByUs)
                {
                    ShowManaged(kv.Value);
                    changed = true;
                }
            }
            WriteJournal();
            if (changed) RaiseWindowsChanged();
        }

        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;
            if (paused) RestoreAllWindows();
            else Tick();
        }

        public void Shutdown()
        {
            RestoreAllWindows();
        }

        /// <summary>把窗口从管理中移除（不再隐藏它）。</summary>
        public void ForgetWindow(IntPtr hwnd)
        {
            ManagedWindow mw;
            if (_windows.TryGetValue(hwnd, out mw))
            {
                if (mw.HiddenByUs)
                {
                    try { _ws.ShowWindow(hwnd); } catch { }
                }
                _windows.Remove(hwnd);
                WriteJournal();
                RaiseWindowsChanged();
            }
        }

        // ---------------- 内部实现 ----------------

        private static readonly string[] SkipClasses =
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "Taskbar",
            "Windows.UI.Core.CoreWindow", "Windows.UI.Core.CoreWindowLite",
            "EdgeUiInputTopWndClass", "TaskListThumbnailWnd", "ForegroundStaging",
            "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
            "IME", "MSCTFIME UI", "Default IME", "GDI+ Window",
            "WpfPopupWindow", "HwndWrapper"
        };

        private bool IsManageable(WindowInfo w)
        {
            if (string.IsNullOrEmpty(w.ClassName)) return false;
            if (IsProcessAlwaysVisible(w.ProcessId)) return false;
            for (int i = 0; i < SkipClasses.Length; i++)
            {
                if (string.Equals(w.ClassName, SkipClasses[i], StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (w.IsCloaked) return false;
            if (!w.HasVisibleSize) return false;
            if (w.Owner != IntPtr.Zero)
            {
                // 拥有者是 shell/桌面的弹窗不管理（如任务栏菜单、桌面右键菜单）
                try
                {
                    string ownerClass = _ws.GetClassName(w.Owner);
                    if (IsShellClass(ownerClass)) return false;
                }
                catch { }
            }
            if (WindowFilter != null && !WindowFilter(w)) return false;
            return true;
        }

        private static bool IsShellClass(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "Taskbar", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveInitialDesktop(WindowInfo w)
        {
            // 1) owner 跟随
            IntPtr owner = w.Owner;
            int guard = 0;
            while (owner != IntPtr.Zero && guard < 16)
            {
                ManagedWindow parent;
                if (_windows.TryGetValue(owner, out parent))
                {
                    return parent.DesktopId;
                }
                try { owner = _ws.GetOwner(owner); }
                catch { owner = IntPtr.Zero; }
                guard++;
            }
            // 2) 用户刚通过桌面图标启动的进程：新窗口强制归属当前桌面
            DateTime hintExpiry;
            if (_launchProcesses.TryGetValue(w.ProcessId, out hintExpiry) && hintExpiry > DateTime.Now)
            {
                return _currentDesktopId;
            }

            // 3) 同进程已有窗口跟随（保证对话框/新窗口跟应用走）
            string lastDesktop;
            if (_lastDesktopByProcess.TryGetValue(w.ProcessId, out lastDesktop) && _config.FindDesktop(lastDesktop) != null)
            {
                return lastDesktop;
            }
            // 3) 当前桌面
            return _currentDesktopId;
        }

        private void RecordWindowState(ManagedWindow mw)
        {
            try
            {
                if (!_ws.IsWindow(mw.Handle)) return;
                if (_ws.IsVisible(mw.Handle))
                {
                    mw.WasMinimized = _ws.IsMinimized(mw.Handle);
                    mw.WasMaximized = _ws.IsMaximized(mw.Handle);
                }
            }
            catch { }
        }

        private void HideManaged(ManagedWindow mw)
        {
            try
            {
                if (!_ws.IsWindow(mw.Handle)) return;
                _ws.HideWindow(mw.Handle);
                mw.HiddenByUs = true;
            }
            catch (Exception ex)
            {
                RaiseError("隐藏窗口失败: " + ex.Message);
            }
        }

        private void ShowManaged(ManagedWindow mw)
        {
            try
            {
                if (!_ws.IsWindow(mw.Handle)) return;
                _ws.ShowWindow(mw.Handle);
                if (mw.WasMaximized)
                {
                    _ws.MaximizeWindow(mw.Handle);
                }
                else if (mw.WasMinimized)
                {
                    _ws.MinimizeWindow(mw.Handle);
                }
                mw.HiddenByUs = false;
                mw.WasMinimized = false;
                mw.WasMaximized = false;
            }
            catch (Exception ex)
            {
                RaiseError("恢复窗口失败: " + ex.Message);
            }
        }

        private void WriteJournal()
        {
            if (!JournalEnabled || _journal == null) return;
            try
            {
                SessionJournal journal = new SessionJournal();
                journal.BootMarker = SessionJournal.CurrentBootMarker();
                foreach (KeyValuePair<IntPtr, ManagedWindow> kv in _windows)
                {
                    ManagedWindow mw = kv.Value;
                    if (!mw.HiddenByUs) continue;
                    if (!_ws.IsWindow(mw.Handle)) continue;
                    JournalWindow jw = new JournalWindow();
                    jw.Handle = mw.Handle.ToInt64();
                    jw.ProcessId = mw.ProcessId;
                    jw.ProcessStartTicks = mw.ProcessStartTicks;
                    jw.DesktopId = mw.DesktopId;
                    jw.WasMinimized = mw.WasMinimized;
                    jw.WasMaximized = mw.WasMaximized;
                    jw.WasForeground = mw.WasForeground;
                    jw.Title = mw.Title;
                    journal.Windows.Add(jw);
                }
                if (journal.Windows.Count == 0)
                {
                    // 没有隐藏窗口：只在之前有记录时清理一次，避免每 400ms 重复删除
                    if (_lastJournalSignature != null)
                    {
                        _journal.Clear();
                        _lastJournalSignature = null;
                    }
                    return;
                }
                // 内容不变则不重复写盘（隐藏窗口集合每 400ms 检查一次）
                StringBuilder signatureBuilder = new StringBuilder();
                foreach (JournalWindow window in journal.Windows)
                {
                    signatureBuilder.Append(window.Handle).Append(':')
                        .Append(window.ProcessId).Append(':')
                        .Append(window.DesktopId).Append(':')
                        .Append(window.WasMinimized ? '1' : '0')
                        .Append(window.WasMaximized ? '1' : '0')
                        .Append(';');
                }
                string signature = signatureBuilder.ToString();
                if (string.Equals(signature, _lastJournalSignature, StringComparison.Ordinal)) return;
                _journal.Save(journal);
                _lastJournalSignature = signature;
            }
            catch
            {
                // 日志失败不影响核心功能
            }
        }

        private static long GetProcessStartTicks(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                return p.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0;
            }
        }

        private void LogLaunchHintMove(IntPtr hwnd)
        {
            try
            {
                if (LaunchHintMoved != null) LaunchHintMoved(this, hwnd);
            }
            catch
            {
            }
        }

        /// <summary>启动图标后把其它桌面的窗口拉回当前桌面时触发（供日志/调试）。</summary>
        public event EventHandler<IntPtr> LaunchHintMoved;

        private void RaiseWindowsChanged()
        {
            EventHandler handler = WindowsChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseError(string message)
        {
            EventHandler<string> handler = Error;
            if (handler != null) handler(this, message);
        }
    }
}
