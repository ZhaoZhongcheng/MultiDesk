using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MultiDesk.Core;

namespace MultiDesk.UI
{
    /// <summary>
    /// 桌面图层窗口：铺满整个虚拟屏幕、永远位于最底层的关键窗口。
    ///
    /// 职责：
    ///   * 绘制当前桌面的壁纸（系统壁纸 / 指定图片 / 纯色）与图标集合；
    ///   * 处理图标单击选中、双击启动、拖动换位、从资源管理器拖入文件；
    ///   * 自绘右键菜单（窗口不激活，不能用 WPF ContextMenu）；
    ///   * 切换桌面时显示 OSD 提示。
    ///
    /// 窗口层级：
    ///   * WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE：不进任务栏 / Alt+Tab，点击不抢焦点；
    ///   * WM_WINDOWPOSCHANGING 中把 hwndInsertAfter 改成 HWND_BOTTOM：始终垫底但
    ///     仍盖住真实桌面（Progman / WorkerW），因此真实桌面图标完全被本图层遮住；
    ///   * 另有 800ms 的 SetWindowPos(HWND_BOTTOM) 兜底，防止被其他程序挤到上层。
    ///
    /// 数据约定：直接读写 host.Config / host.CurrentDesktop，修改后调用
    /// host.NotifyConfigChanged() 保存并刷新。
    /// </summary>
    public partial class DesktopLayerWindow : Window, IDesktopSurface
    {
        // ================= Win32 常量 =================
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        // ================= 状态 =================
        private readonly IAppHost _host;
        private readonly DispatcherTimer _bottomTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _menuWatchTimer;
        private readonly Dictionary<string, DesktopLayerIconView> _iconViews =
            new Dictionary<string, DesktopLayerIconView>(StringComparer.Ordinal);

        private HwndSource _hwndSource;
        private IntPtr _hwnd = IntPtr.Zero;
        private ScreenLayout _layout;
        private DesktopLayerMenu _menu;
        private string _selectedIconId;
        private string _lastDesktopId;
        private Storyboard _osdStoryboard;
        private bool _loaded;
        private bool _settingBottom;
        private bool _hostEventsHooked;
        private bool _refreshScheduled;
        private IconMetrics _iconMetrics = IconLayout.Default;
        private string _lastLayoutSignature;
        private DesktopProfile _preparedDesktop;
        private int _preparedDesktopIndex = -1;
        private bool _realDesktopMode;

        public DesktopLayerWindow(IAppHost host)
        {
            _host = host;
            InitializeComponent();

            _bottomTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _bottomTimer.Interval = TimeSpan.FromMilliseconds(800.0);
            _bottomTimer.Tick += BottomTimerTick;

            _toastTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _toastTimer.Interval = TimeSpan.FromSeconds(3.0);
            _toastTimer.Tick += ToastTimerTick;

            _menuWatchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _menuWatchTimer.Interval = TimeSpan.FromMilliseconds(200.0);
            _menuWatchTimer.Tick += MenuWatchTimerTick;

            _menu = new DesktopLayerMenu(MenuLayer);
            _menu.Closed += MenuClosedHandler;

            Loaded += WindowLoadedHandler;
            Closed += WindowClosedHandler;

            AttachInputHandlers();
        }

        // ================= 窗口生命周期 =================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                // 不出现在任务栏/Alt+Tab，并且永不抢焦点
                int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                _hwndSource = HwndSource.FromHwnd(_hwnd);
                if (_hwndSource != null) _hwndSource.AddHook(WndProc);

                ApplyScreenLayout(true);
                PushToBottom();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] OnSourceInitialized 失败: " + ex);
            }
        }

        private void WindowLoadedHandler(object sender, RoutedEventArgs e)
        {
            try
            {
                _loaded = true;
                ApplyScreenLayout(true);
                HookHostEvents(true);
                PushToBottom();
                if (!_bottomTimer.IsEnabled) _bottomTimer.Start();
                RefreshSurface();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] Loaded 处理失败: " + ex);
            }
        }

        private void WindowClosedHandler(object sender, EventArgs e)
        {
            try
            {
                _bottomTimer.Stop();
                _toastTimer.Stop();
                _menuWatchTimer.Stop();
                CloseMenu();
                HookHostEvents(false);
                if (_hwndSource != null)
                {
                    _hwndSource.RemoveHook(WndProc);
                    _hwndSource = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] Closed 处理失败: " + ex);
            }
        }

        private void WindowSizeChangedHandler(object sender, SizeChangedEventArgs e)
        {
            try
            {
                UpdateBadgePosition();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] SizeChanged 处理失败: " + ex.Message);
            }
        }

        // ================= 消息处理 =================

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (msg == WM_WINDOWPOSCHANGING)
                {
                    // 强制置底：把任何 Z 序调整都改成 HWND_BOTTOM。
                    // 自身 SetWindowPos 兜底调用会带 _settingBottom 标记，避免无意义的重复改写。
                    if (!_settingBottom && lParam != IntPtr.Zero)
                    {
                        WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                        if (wp.hwndInsertAfter != HWND_BOTTOM)
                        {
                            wp.hwndInsertAfter = HWND_BOTTOM;
                            Marshal.StructureToPtr(wp, lParam, false);
                        }
                    }
                }
                else if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
                {
                    // 显示器分辨率 / DPI 变化：重新抓取布局，重排图标后再刷新
                    Dispatcher.BeginInvoke(new Action(OnDisplaySettingsChanged), DispatcherPriority.Background);
                }
                else if (msg == WM_SYSCOMMAND)
                {
                    // 拒绝最小化：系统的「显示桌面 / Win+D」会给所有窗口发 SC_MINIMIZE，
                    // 图层一旦被最小化就会露出真实桌面图标，因此这里直接吞掉该命令。
                    int command = wParam.ToInt32() & 0xFFF0;
                    if (command == SC_MINIMIZE)
                    {
                        handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] WndProc 处理失败: " + ex);
            }
            return IntPtr.Zero;
        }

        private void OnDisplaySettingsChanged()
        {
            try
            {
                ApplyScreenLayout(true);
                DesktopProfile desktop = GetCurrentDesktop();
                if (desktop != null)
                {
                    try
                    {
                        // 分辨率/DPI 变化后按当前图标尺寸重新规整：网格模式重新排布，自由模式只夹回屏幕
                        IconMetrics metrics = GetIconMetrics();
                        if (IsAutoArrangeIcons())
                        {
                            IconLayout.Normalize(desktop, LayoutArea, metrics);
                        }
                        else
                        {
                            IconLayout.NormalizeFree(desktop, LayoutArea);
                            ClampAllIconsToArea(desktop, LayoutArea, metrics);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[DesktopLayer] 分辨率变化后规整图标失败: " + ex.Message);
                    }
                }
                RefreshSurface();
                try
                {
                    if (_host != null && desktop != null) _host.SaveConfig();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 分辨率变化后保存配置失败: " + ex.Message);
                }
                PushToBottom();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 处理显示设置变化失败: " + ex);
            }
        }

        private void BottomTimerTick(object sender, EventArgs e)
        {
            try
            {
                if (IsVisible) PushToBottom();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 置底定时器失败: " + ex.Message);
            }
        }

        /// <summary>把窗口压到 Z 序最底部（但仍位于真实桌面之上）。</summary>
        private void PushToBottom()
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                _settingBottom = true;
                SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 置底失败: " + ex.Message);
            }
            finally
            {
                _settingBottom = false;
            }
        }

        // ================= 布局 =================

        private void ApplyScreenLayout(bool force)
        {
            ScreenLayout layout = null;
            try
            {
                layout = ScreenLayout.Capture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 抓取屏幕布局失败: " + ex.Message);
            }
            if (layout == null) return;

            bool changed = _layout == null || !_layout.EqualsTo(layout);
            _layout = layout;
            if (!force && !changed) return;

            try
            {
                double left = SystemParameters.VirtualScreenLeft;
                double top = SystemParameters.VirtualScreenTop;
                double width = layout.VirtualScreen.Width;
                double height = layout.VirtualScreen.Height;
                if (width < 1.0) width = 1.0;
                if (height < 1.0) height = 1.0;

                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                if (double.IsNaN(Left) || Math.Abs(Left - left) > 0.5) Left = left;
                if (double.IsNaN(Top) || Math.Abs(Top - top) > 0.5) Top = top;
                if (double.IsNaN(Width) || Math.Abs(Width - width) > 0.5) Width = width;
                if (double.IsNaN(Height) || Math.Abs(Height - height) > 0.5) Height = height;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 设置窗口边界失败: " + ex);
            }

            UpdateBadgePosition();
        }

        private ScreenLayout EnsureLayout()
        {
            if (_layout == null)
            {
                try
                {
                    _layout = ScreenLayout.Capture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 抓取屏幕布局失败: " + ex.Message);
                }
            }
            return _layout;
        }

        private Rect LayoutArea
        {
            get
            {
                ScreenLayout layout = EnsureLayout();
                if (layout == null) return new Rect(0.0, 0.0, 1920.0, 1040.0);
                return layout.LayoutArea;
            }
        }

        private double GetDpiScale()
        {
            try
            {
                if (_hwndSource != null && _hwndSource.CompositionTarget != null)
                {
                    Matrix matrix = _hwndSource.CompositionTarget.TransformToDevice;
                    if (matrix.M11 > 0.01) return matrix.M11;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 获取 DPI 失败: " + ex.Message);
            }
            return 1.0;
        }

        // ================= IDesktopSurface：刷新 =================

        public void RefreshSurface()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(RefreshSurface), DispatcherPriority.Background);
                    return;
                }
                if (!_loaded) return;

                // 正常刷新总是以控制器当前激活桌面为准（清掉 PrepareDesktop 的预渲染覆盖）
                _preparedDesktop = null;
                _preparedDesktopIndex = -1;

                ApplyScreenLayout(false);

                DesktopProfile desktop = GetCurrentDesktop();
                string desktopId = desktop == null ? null : desktop.Id;
                if (!string.Equals(desktopId, _lastDesktopId, StringComparison.Ordinal))
                {
                    // 切换桌面：释放上一张壁纸的解码缓存，避免多张大图常驻内存；
                    // 并按当前图标尺寸规整新桌面的图标位置（只修正越界/重叠，不破坏有效布局）
                    _lastDesktopId = desktopId;
                    WallpaperService.ClearCache();
                    NormalizeIconsForCurrentMetrics(desktop, false);
                }

                RebuildWallpaper();
                RebuildIcons();
                UpdateBadge();
                CloseMenu();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] RefreshSurface 失败: " + ex);
            }
        }

        private void ScheduleRefresh()
        {
            if (_refreshScheduled) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _refreshScheduled = false;
                RefreshSurface();
            }), DispatcherPriority.Background);
        }

        // ================= IDesktopSurface：切换防闪烁预渲染 =================

        /// <summary>
        /// 切换桌面防闪烁（由 AppController 在隐藏旧窗口前调用）：
        /// 先把壁纸/图标/徽章切到目标桌面并强制完成一次渲染，
        /// 这样隐藏/显示窗口的瞬间屏幕上已经是目标桌面的画面，避免大面积闪烁。
        /// 随后的 RefreshSurface() 会清掉这里的预渲染覆盖，只做一次轻量刷新。
        /// </summary>
        public void PrepareDesktop(string desktopId)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    // 同步等待渲染完成：切换流程对时序敏感，必须用 Send 优先级
                    Dispatcher.Invoke(new Action(delegate { PrepareDesktop(desktopId); }), DispatcherPriority.Send);
                    return;
                }
                if (!_loaded) return;

                AppConfig config = GetConfig();
                DesktopProfile target = config == null ? null : config.FindDesktop(desktopId);
                if (target == null)
                {
                    // 目标桌面不存在：退回正常刷新
                    RefreshSurface();
                    return;
                }

                int index = config.IndexOfDesktop(desktopId);
                _preparedDesktop = target;
                _preparedDesktopIndex = index < 0 ? 0 : index;

                // 目标桌面与当前不同：同步释放上一张壁纸缓存并更新 _lastDesktopId，
                // 让随后的 RefreshSurface 复用已解码的目标壁纸，不会重复解码。
                if (!string.Equals(target.Id, _lastDesktopId, StringComparison.Ordinal))
                {
                    _lastDesktopId = target.Id;
                    WallpaperService.ClearCache();
                }

                ApplyScreenLayout(false);
                RebuildWallpaper();
                RebuildIcons();
                UpdateBadge();
                CloseMenu();

                // 强制完成一次布局与渲染，保证窗口隐藏前画面已就绪
                UpdateLayout();
                Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] PrepareDesktop 失败: " + ex);
            }
        }

        // ================= IDesktopSurface：原桌面模式 =================

        /// <summary>
        /// 「原桌面模式」开关（1.2.4）：active=true 时隐藏图层窗口，让用户看到真实系统桌面；
        /// active=false 时重新显示图层并完整刷新（壁纸/图标/徽章），恢复多桌面视图。
        /// 隐藏期间 OSD / Toast 一律不弹（ShowSwitchOsd / ShowToast 内部有 _realDesktopMode 判断）。
        /// </summary>
        public void SetRealDesktopMode(bool active)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(delegate { SetRealDesktopMode(active); }), DispatcherPriority.Send);
                    return;
                }
                if (!_loaded) return;

                if (active)
                {
                    if (_realDesktopMode) return;
                    _realDesktopMode = true;
                    CloseMenu();
                    // 暂停置底轮询，减少无意义的系统调用
                    try { _bottomTimer.Stop(); } catch { }
                    // 隐藏图层窗口（另一路：控制器还会恢复所有真实窗口并恢复系统桌面图标）
                    if (IsVisible) Hide();
                }
                else
                {
                    if (!_realDesktopMode) return;
                    _realDesktopMode = false;
                    // 重新显示窗口：不做激活，立即刷新内容并重新压到底层
                    Show();
                    ApplyScreenLayout(true);
                    // 恢复时强制重绘（隐藏期间配置/桌面可能已变化）
                    _lastDesktopId = null;
                    RefreshSurface();
                    UpdateLayout();
                    PushToBottom();
                    if (!_bottomTimer.IsEnabled) _bottomTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] SetRealDesktopMode 失败: " + ex);
            }
        }

        private void HookHostEvents(bool attach)
        {
            if (_host == null) return;
            try
            {
                if (attach)
                {
                    if (_hostEventsHooked) return;
                    _host.CurrentDesktopChanged += HostCurrentDesktopChangedHandler;
                    _host.ConfigChanged += HostConfigChangedHandler;
                    _hostEventsHooked = true;
                }
                else
                {
                    if (!_hostEventsHooked) return;
                    _host.CurrentDesktopChanged -= HostCurrentDesktopChangedHandler;
                    _host.ConfigChanged -= HostConfigChangedHandler;
                    _hostEventsHooked = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 订阅/退订 host 事件失败: " + ex.Message);
            }
        }

        private void HostCurrentDesktopChangedHandler(object sender, EventArgs e)
        {
            ScheduleRefresh();
        }

        private void HostConfigChangedHandler(object sender, EventArgs e)
        {
            ScheduleRefresh();
        }

        // ================= IDesktopSurface：OSD / 退出 =================

        public void ShowSwitchOsd(string desktopName)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(delegate { ShowSwitchOsd(desktopName); }), DispatcherPriority.Background);
                    return;
                }
                if (!_loaded) return;
                // 原桌面模式下图层隐藏，不弹 OSD
                if (_realDesktopMode || !IsVisible) return;

                OsdText.Text = string.IsNullOrEmpty(desktopName) ? "桌面" : desktopName;
                PositionOsd();

                OsdBorder.Visibility = Visibility.Visible;
                OsdBorder.BeginAnimation(UIElement.OpacityProperty, null);
                OsdBorder.Opacity = 0.0;

                // 淡入 150ms → 停留 900ms → 淡出 300ms
                DoubleAnimationUsingKeyFrames frames = new DoubleAnimationUsingKeyFrames();
                frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                frames.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150.0))));
                frames.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1050.0))));
                frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350.0))));

                Storyboard storyboard = new Storyboard();
                Storyboard.SetTarget(frames, OsdBorder);
                Storyboard.SetTargetProperty(frames, new PropertyPath(UIElement.OpacityProperty));
                storyboard.Children.Add(frames);
                storyboard.Completed += OsdStoryboardCompleted;
                _osdStoryboard = storyboard;
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 显示切换 OSD 失败: " + ex);
            }
        }

        private void OsdStoryboardCompleted(object sender, EventArgs e)
        {
            try
            {
                if (sender != _osdStoryboard) return;
                _osdStoryboard = null;
                OsdBorder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] OSD 结束处理失败: " + ex.Message);
            }
        }

        public void PrepareForExit()
        {
            try
            {
                _bottomTimer.Stop();
                _toastTimer.Stop();
                _menuWatchTimer.Stop();
                CloseMenu();
                HookHostEvents(false);
                if (IsVisible) Hide();
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] PrepareForExit 失败: " + ex);
            }
        }

        // ================= 壁纸渲染 =================

        /// <summary>
        /// 重建壁纸：
        ///  * System + 有桌面权限（正常 Medium 启动）→ 完全透明，露出真实桌面/动态壁纸；
        ///  * System + 受限启动（Low IL/沙箱，hide 真实图标会失败）→ 用系统壁纸图片做不透明背景，
        ///    避免真实桌面图标与程序图标重复显示；
        ///  * Image / Solid → WallpaperService 绘制（保持 5 种填充）。
        /// </summary>
        private void RebuildWallpaper()
        {
            try
            {
                DesktopProfile desktop = GetDisplayDesktop();
                if (desktop == null)
                {
                    // 设计器/骨架模式（host 为 null）：给一个深色兜底，便于预览
                    SetWallpaper(WallpaperService.CreateFallbackBrush(), true);
                    return;
                }

                if (desktop.WallpaperMode == WallpaperMode.System)
                {
                    if (IsDesktopIntegrationAvailable())
                    {
                        // 可以隐藏真实桌面图标：透明以露出真实桌面（含第三方动态壁纸）
                        SetWallpaper(null, false);
                    }
                    else
                    {
                        // 受限模式：绘制注册表里的系统壁纸图片作为不透明背景
                        WallpaperFit fit = WallpaperService.GetSystemWallpaperFit();
                        Brush systemBrush = WallpaperService.CreateBrush(desktop, GetVirtualScreen(), GetDpiScale());
                        if (systemBrush == null) systemBrush = WallpaperService.CreateFallbackBrush();
                        // Fit/Center 模式图片不会铺满，需要暗色底遮住真实桌面图标
                        bool needOpaqueBase = fit == WallpaperFit.Fit || fit == WallpaperFit.Center;
                        SetWallpaper(systemBrush, needOpaqueBase);
                    }
                    return;
                }

                Brush brush = WallpaperService.CreateBrush(desktop, GetVirtualScreen(), GetDpiScale());
                if (brush == null) brush = WallpaperService.CreateFallbackBrush();
                SetWallpaper(brush, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 壁纸渲染失败: " + ex);
                try
                {
                    SetWallpaper(WallpaperService.CreateFallbackBrush(), true);
                }
                catch
                {
                }
            }
        }

        /// <summary>设置壁纸层：brush 为 null 表示完全透明（露出真实桌面）；needOpaqueBase 时加暗色底。</summary>
        private void SetWallpaper(Brush brush, bool needOpaqueBase)
        {
            if (WallpaperImage != null) WallpaperImage.Fill = brush;
            if (WallpaperBase != null)
            {
                WallpaperBase.Visibility = (brush != null && needOpaqueBase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private Rect GetVirtualScreen()
        {
            ScreenLayout layout = EnsureLayout();
            return layout != null ? layout.VirtualScreen : new Rect(0.0, 0.0, 1920.0, 1080.0);
        }

        /// <summary>当前进程是否有权限操作系统桌面（低完整性/沙箱启动时为 false，需要内置回退方案）。</summary>
        private bool IsDesktopIntegrationAvailable()
        {
            if (_host == null) return false;
            try
            {
                return _host.IsDesktopIntegrationAvailable;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 读取桌面集成可用性失败: " + ex.Message);
                return false;
            }
        }

        // ================= 图标渲染 =================

        private void RebuildIcons()
        {
            try
            {
                IconCanvas.Children.Clear();
                _iconViews.Clear();

                DesktopProfile desktop = GetDisplayDesktop();
                IconMetrics metrics = GetIconMetrics();
                _iconMetrics = metrics;
                bool autoArrange = IsAutoArrangeIcons();
                if (desktop == null || desktop.Icons == null) return;

                // 图标大小 / 自动排列选项发生变化时，当前桌面按新参数重新排布一次。
                // 首次刷新只做「规整」（保留用户已有的有效位置），避免每次启动重排打乱布局；
                // 之后真正检测到选项变化时才整体重排。
                string signature = metrics.IconSize + "|" + (autoArrange ? "1" : "0");
                if (!string.Equals(signature, _lastLayoutSignature, StringComparison.Ordinal))
                {
                    bool firstLayout = _lastLayoutSignature == null;
                    _lastLayoutSignature = signature;
                    NormalizeIconsForCurrentMetrics(desktop, !firstLayout);
                }

                Rect area = LayoutArea;
                for (int i = 0; i < desktop.Icons.Count; i++)
                {
                    IconEntry icon = desktop.Icons[i];
                    if (icon == null) continue;

                    double x = icon.X;
                    double y = icon.Y;
                    if (double.IsNaN(x) || double.IsInfinity(x))
                    {
                        x = area.X + metrics.MarginX + (i % 8) * metrics.CellWidth;
                    }
                    if (double.IsNaN(y) || double.IsInfinity(y))
                    {
                        y = area.Y + metrics.MarginY + (i / 8) * metrics.CellHeight;
                    }

                    DesktopLayerIconView view = new DesktopLayerIconView(icon, metrics);
                    Canvas.SetLeft(view, x);
                    Canvas.SetTop(view, y);
                    Panel.SetZIndex(view, 10);
                    if (!string.IsNullOrEmpty(_selectedIconId) &&
                        string.Equals(_selectedIconId, icon.Id, StringComparison.Ordinal))
                    {
                        view.SetSelected(true);
                    }
                    IconCanvas.Children.Add(view);
                    if (!string.IsNullOrEmpty(icon.Id) && !_iconViews.ContainsKey(icon.Id))
                    {
                        _iconViews.Add(icon.Id, view);
                    }
                }

                if (!string.IsNullOrEmpty(_selectedIconId) && !_iconViews.ContainsKey(_selectedIconId))
                {
                    _selectedIconId = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 图标渲染失败: " + ex);
            }
        }

        // ================= 图标尺寸 / 排列模式 =================

        /// <summary>
        /// 当前生效的图标栅格度量：优先用控制器提供的 host.GetIconMetrics()
        /// （默认跟随真实 Windows 桌面：用户机约 icon=48、cell≈76.7x98、origin≈(0,3.3)）；
        /// host 为空或返回非法值时退回按 Options.IconSize 计算的默认度量。
        /// </summary>
        private IconMetrics GetIconMetrics()
        {
            if (_host != null)
            {
                try
                {
                    IconMetrics metrics = _host.GetIconMetrics();
                    if (IsValidMetrics(metrics)) return metrics;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 读取 host 图标度量失败: " + ex.Message);
                }
            }
            int size = 48;
            AppConfig config = GetConfig();
            if (config != null && config.Options != null) size = config.Options.IconSize;
            if (size != 32 && size != 48 && size != 64) size = 48;
            return IconLayout.CreateMetrics(size);
        }

        private static bool IsValidMetrics(IconMetrics metrics)
        {
            if (metrics == null) return false;
            if (metrics.IconSize <= 0) return false;
            if (metrics.CellWidth < metrics.IconSize) return false;
            if (metrics.CellHeight < metrics.IconSize) return false;
            if (double.IsNaN(metrics.CellWidth) || double.IsNaN(metrics.CellHeight)) return false;
            if (double.IsInfinity(metrics.CellWidth) || double.IsInfinity(metrics.CellHeight)) return false;
            return true;
        }

        /// <summary>配置中的「自动排列图标」开关（默认开启）。</summary>
        private bool IsAutoArrangeIcons()
        {
            AppConfig config = GetConfig();
            if (config == null || config.Options == null) return true;
            return config.Options.AutoArrangeIcons;
        }

        /// <summary>
        /// 按当前图标尺寸/排列模式规整桌面图标：
        /// optionsChanged=true（用户改了大小或排列开关）→ 网格模式整体重排，自由模式仅夹回屏幕；
        /// optionsChanged=false（切换桌面/显式调用）→ 网格模式只修正越界与重叠，保留有效位置。
        /// 位置有变化时保存配置。
        /// </summary>
        private void NormalizeIconsForCurrentMetrics(DesktopProfile desktop, bool optionsChanged)
        {
            if (desktop == null || desktop.Icons == null || desktop.Icons.Count == 0) return;
            try
            {
                IconMetrics metrics = GetIconMetrics();
                bool autoArrange = IsAutoArrangeIcons();
                Rect area = LayoutArea;
                double[] before = CaptureIconPositions(desktop);

                if (autoArrange)
                {
                    if (optionsChanged) IconLayout.ArrangeAll(desktop, area, metrics);
                    else IconLayout.Normalize(desktop, area, metrics);
                }
                else
                {
                    IconLayout.NormalizeFree(desktop, area);
                    ClampAllIconsToArea(desktop, area, metrics);
                }

                if (PositionsChanged(before, desktop))
                {
                    try
                    {
                        if (_host != null) _host.SaveConfig();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[DesktopLayer] 规整图标后保存配置失败: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 规整图标位置失败: " + ex);
            }
        }

        /// <summary>把自由模式下的图标位置夹回可放置区域（不改动合法坐标）。</summary>
        private void ClampAllIconsToArea(DesktopProfile desktop, Rect area, IconMetrics metrics)
        {
            if (desktop == null || desktop.Icons == null) return;
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry icon = desktop.Icons[i];
                if (icon == null) continue;
                Point clamped = ClampIconPosition(new Point(icon.X, icon.Y), area, metrics);
                icon.X = clamped.X;
                icon.Y = clamped.Y;
            }
        }

        /// <summary>自由摆放模式：把任意坐标夹到屏幕可用区内（按当前单元格尺寸留边）。</summary>
        private static Point ClampIconPosition(Point position, Rect area, IconMetrics metrics)
        {
            IconMetrics m = metrics != null ? metrics : IconLayout.Default;
            double maxX = area.X + area.Width - m.CellWidth;
            double maxY = area.Y + area.Height - m.CellHeight;
            if (maxX < area.X) maxX = area.X;
            if (maxY < area.Y) maxY = area.Y;
            if (double.IsNaN(position.X) || double.IsInfinity(position.X)) position.X = area.X + m.MarginX;
            if (double.IsNaN(position.Y) || double.IsInfinity(position.Y)) position.Y = area.Y + m.MarginY;
            if (position.X < area.X) position.X = area.X;
            if (position.Y < area.Y) position.Y = area.Y;
            if (position.X > maxX) position.X = maxX;
            if (position.Y > maxY) position.Y = maxY;
            return position;
        }

        private static double[] CaptureIconPositions(DesktopProfile desktop)
        {
            if (desktop == null || desktop.Icons == null) return new double[0];
            double[] values = new double[desktop.Icons.Count * 2];
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry icon = desktop.Icons[i];
                if (icon == null) continue;
                values[i * 2] = icon.X;
                values[i * 2 + 1] = icon.Y;
            }
            return values;
        }

        private static bool PositionsChanged(double[] before, DesktopProfile desktop)
        {
            if (desktop == null || desktop.Icons == null) return false;
            if (before.Length != desktop.Icons.Count * 2) return true;
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry icon = desktop.Icons[i];
                if (icon == null) continue;
                if (before[i * 2] != icon.X || before[i * 2 + 1] != icon.Y) return true;
            }
            return false;
        }


        private void SelectIcon(string iconId)
        {
            if (string.Equals(_selectedIconId, iconId, StringComparison.Ordinal)) return;
            _selectedIconId = iconId;
            foreach (KeyValuePair<string, DesktopLayerIconView> pair in _iconViews)
            {
                if (pair.Value == null) continue;
                pair.Value.SetSelected(!string.IsNullOrEmpty(iconId) &&
                                       string.Equals(pair.Key, iconId, StringComparison.Ordinal));
            }
        }

        // ================= 桌面名徽章 =================

        private void UpdateBadge()
        {
            try
            {
                bool visible = false;
                string text = string.Empty;
                DesktopProfile desktop = GetDisplayDesktop();
                AppConfig config = GetConfig();
                bool optionShowName = config == null || config.Options == null || config.Options.ShowDesktopName;
                if (desktop != null && desktop.ShowNameBadge && optionShowName)
                {
                    visible = true;
                    int index = GetDisplayDesktopIndex();
                    if (index < 0) index = 0;
                    string name = string.IsNullOrEmpty(desktop.Name) ? "桌面" : desktop.Name;
                    text = (index + 1) + " · " + name;
                }

                if (!visible)
                {
                    NameBadge.Visibility = Visibility.Collapsed;
                    return;
                }
                NameBadgeText.Text = text;
                UpdateBadgePosition();
                NameBadge.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 更新桌面名徽章失败: " + ex);
            }
        }

        private void UpdateBadgePosition()
        {
            try
            {
                if (NameBadge == null) return;
                ScreenLayout layout = EnsureLayout();
                if (layout == null) return;
                Rect virtualScreen = layout.VirtualScreen;
                Rect workArea = layout.PrimaryWorkArea;
                double right = Math.Max(0.0, virtualScreen.Right - workArea.Right) + 18.0;
                double bottom = Math.Max(0.0, virtualScreen.Bottom - workArea.Bottom) + 40.0;
                NameBadge.Margin = new Thickness(0.0, 0.0, right, bottom);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 更新徽章位置失败: " + ex.Message);
            }
        }

        // ================= OSD / Toast 定位 =================

        private void PositionOsd()
        {
            ScreenLayout layout = EnsureLayout();
            if (layout == null) return;
            Rect area = layout.PrimaryWorkArea;
            OsdText.MaxWidth = Math.Max(240.0, area.Width - 240.0);
            double bottomOffset = Math.Max(60.0, area.Height * 0.18);
            PositionElementBottomCenter(OsdBorder, bottomOffset, area);
        }

        private static void PositionElementBottomCenter(FrameworkElement element, double bottomOffset, Rect area)
        {
            if (element == null) return;
            try
            {
                element.Measure(new Size(Math.Max(120.0, area.Width - 80.0), double.PositiveInfinity));
                Size size = element.DesiredSize;
                if (size.Width <= 0.0) size.Width = 200.0;
                if (size.Height <= 0.0) size.Height = 48.0;
                double left = area.X + (area.Width - size.Width) / 2.0;
                double top = area.Y + area.Height - size.Height - bottomOffset;
                if (left < area.X) left = area.X;
                if (top < area.Y) top = area.Y;
                Canvas.SetLeft(element, left);
                Canvas.SetTop(element, top);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 定位覆盖元素失败: " + ex.Message);
            }
        }

        /// <summary>显示 3 秒错误提示（不弹 MessageBox）。</summary>
        private void ShowToast(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message)) return;
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(delegate { ShowToast(message); }), DispatcherPriority.Background);
                    return;
                }
                if (!_loaded) return;
                // 原桌面模式下图层隐藏，不弹 Toast
                if (_realDesktopMode || !IsVisible) return;

                ToastText.Text = message;
                ScreenLayout layout = EnsureLayout();
                if (layout != null)
                {
                    Rect area = layout.PrimaryWorkArea;
                    ToastText.MaxWidth = Math.Max(240.0, area.Width * 0.72);
                    PositionElementBottomCenter(ToastBorder, 96.0, area);
                }
                ToastBorder.Visibility = Visibility.Visible;
                ToastBorder.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120.0)));
                _toastTimer.Stop();
                _toastTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 显示 Toast 失败: " + ex);
            }
        }

        private void ToastTimerTick(object sender, EventArgs e)
        {
            _toastTimer.Stop();
            try
            {
                DoubleAnimation fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(240.0));
                fade.Completed += ToastFadeCompleted;
                ToastBorder.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] Toast 淡出失败: " + ex.Message);
            }
        }

        private void ToastFadeCompleted(object sender, EventArgs e)
        {
            try
            {
                ToastBorder.Visibility = Visibility.Collapsed;
                ToastBorder.Opacity = 0.0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] Toast 隐藏失败: " + ex.Message);
            }
        }

        // ================= 数据访问小工具 =================

        private AppConfig GetConfig()
        {
            if (_host == null) return null;
            try
            {
                return _host.Config;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 读取 Config 失败: " + ex.Message);
                return null;
            }
        }

        private DesktopProfile GetCurrentDesktop()
        {
            if (_host == null) return null;
            try
            {
                return _host.CurrentDesktop;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 读取 CurrentDesktop 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>实际要渲染的桌面：PrepareDesktop 预渲染时为目标桌面，否则为控制器当前桌面。</summary>
        private DesktopProfile GetDisplayDesktop()
        {
            if (_preparedDesktop != null) return _preparedDesktop;
            return GetCurrentDesktop();
        }

        /// <summary>实际要渲染的桌面序号（与 GetDisplayDesktop 对应）。</summary>
        private int GetDisplayDesktopIndex()
        {
            if (_preparedDesktop != null) return _preparedDesktopIndex;
            try
            {
                if (_host != null) return _host.CurrentDesktopIndex;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 读取桌面序号失败: " + ex.Message);
            }
            return -1;
        }

        private void NotifyHostChanged()
        {
            try
            {
                if (_host != null) _host.NotifyConfigChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] NotifyConfigChanged 失败: " + ex);
            }
        }

        // ================= Win32 声明 =================

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
