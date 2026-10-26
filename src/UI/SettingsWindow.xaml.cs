using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiDesk.Core;
using WinForms = System.Windows.Forms;

namespace MultiDesk.UI
{
    /// <summary>
    /// 设置窗口：桌面增删改排序、每桌面壁纸与图标管理、常规选项、快捷键说明与帮助。
    ///
    /// 数据约定（见 IAppHost）：界面直接修改 host.Config 下的对象，
    /// 修改完成后调用 host.NotifyConfigChanged() 让控制器保存配置并刷新桌面图层/托盘。
    /// host 可能为 null（骨架/设计器模式），所有入口都做了保护。
    /// </summary>
    public partial class SettingsWindow : Window, ISettingsSurface
    {
        /// <summary>右侧内容区当前显示的页面。</summary>
        private enum SettingsPage
        {
            Desktops,
            General,
            Help
        }

        private static readonly Regex HexColorRegex =
            new Regex("^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

        /// <summary>纯色壁纸的 8 个预设颜色。</summary>
        private static readonly string[] PresetColors = new string[]
        {
            "#FF1B1F2A", "#FF0F172A", "#FF1E2536", "#FF2D3A5F",
            "#FF5B8DEF", "#FF7C6CF0", "#FF2E7D5B", "#FFB0763B"
        };

        /// <summary>「图标大小」可选值（像素），与界面下拉项一一对应。</summary>
        private static readonly int[] IconSizeValues = new int[] { 32, 48, 64 };

        // 代码里用到的少量固定画刷（窗口资源字典中的同名资源无法在纯 C# 里直接取用，避免 FindResource 失败）
        private static readonly Brush NavActiveBackground =
            CreateFrozenBrush(Color.FromArgb(0x33, 0x5B, 0x8D, 0xEF));
        private static readonly Brush AccentSolid = CreateFrozenBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
        private static readonly Brush SecondarySolid = CreateFrozenBrush(Color.FromRgb(0x98, 0xA2, 0xB8));
        private static readonly Brush DangerSolid = CreateFrozenBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
        private static readonly Brush WarningSolid = CreateFrozenBrush(Color.FromRgb(0xE3, 0xB3, 0x41));

        private readonly IAppHost _host;
        private readonly List<HotkeyItem> _hotkeys;

        private SettingsPage _page;
        private string _selectedDesktopId;

        /// <summary>重入保护计数：>0 时所有“改配置/写界面”的事件处理器直接返回。</summary>
        private int _suppressDepth;
        private bool _refreshing;
        private bool _refreshQueued;
        private bool _initialized;
        private bool _closed;
        private bool _allowClose;
        private bool _welcomeDismissed;
        private Brush _dropZoneNormalBorder;

        public SettingsWindow(IAppHost host)
        {
            _host = host;
            InitializeComponent();

            _page = SettingsPage.Desktops;
            _hotkeys = CreateHotkeyList();
            _dropZoneNormalBorder = IconDropZone.BorderBrush;

            InitializeStaticUi();

            if (_host != null)
            {
                _host.ConfigChanged += Host_StateChanged;
                _host.CurrentDesktopChanged += Host_StateChanged;
                _host.PauseStateChanged += Host_StateChanged;
                DesktopProfile current = null;
                try { current = _host.CurrentDesktop; }
                catch { }
                if (current != null) _selectedDesktopId = current.Id;
            }

            _initialized = true;
            ShowPage(SettingsPage.Desktops);
            RefreshSettings();
        }

        // =====================================================================
        // ISettingsSurface
        // =====================================================================

        /// <summary>从 host 重新拉取数据，刷新侧栏、当前页控件与欢迎条。</summary>
        public void RefreshSettings()
        {
            if (_closed) return;
            if (_refreshing) return;
            _refreshing = true;
            BeginSuppress();
            try
            {
                if (_host == null || _host.Config == null)
                {
                    ApplyEmptyState();
                    return;
                }

                AppConfig config = _host.Config;
                if (config.Desktops == null) config.Desktops = new List<DesktopProfile>();

                EnsureSelectedDesktop(config);
                RefreshDesktopList(config);
                RefreshWelcomeBanner(config);
                RefreshCurrentPage();
                RefreshActionButtons(config);
                RefreshSwitchButtonState();
            }
            finally
            {
                EndSuppress();
                _refreshing = false;
            }
        }

        public bool IsOpen
        {
            get { return IsVisible; }
        }

        /// <summary>退出程序前允许窗口真正关闭（供控制器在 Shutdown 时调用，可选）。</summary>
        public void PrepareForExit()
        {
            _allowClose = true;
            try { Close(); }
            catch { }
        }

        // =====================================================================
        // 初始化 / 静态界面
        // =====================================================================

        private void InitializeStaticUi()
        {
            BeginSuppress();
            try
            {
                FitCombo.ItemsSource = new List<string> { "填充", "适应", "拉伸", "居中", "平铺" };
                IconSizeCombo.ItemsSource = new List<string> { "小（32px）", "中（48px）", "大（64px）" };
                HotkeyItems.ItemsSource = _hotkeys;

                // 8 个预设颜色小方块
                foreach (string hex in PresetColors)
                {
                    Button button = new Button();
                    button.Style = (Style)FindResource("PresetColorButtonStyle");
                    button.Background = BrushFromHex(hex);
                    button.Tag = hex;
                    button.ToolTip = hex;
                    button.Click += PresetColor_Click;
                    PresetColorsPanel.Children.Add(button);
                }
            }
            finally
            {
                EndSuppress();
            }
        }

        private static List<HotkeyItem> CreateHotkeyList()
        {
            List<HotkeyItem> list = new List<HotkeyItem>();
            list.Add(new HotkeyItem("Ctrl + Alt + 1 ~ 9", "切换到第 1 ~ 9 个桌面"));
            list.Add(new HotkeyItem("Ctrl + Alt + ← / →", "切换到上一个 / 下一个桌面"));
            list.Add(new HotkeyItem("Ctrl + Alt + Shift + ← / →", "把当前窗口移到上一个 / 下一个桌面"));
            list.Add(new HotkeyItem("Ctrl + Alt + S", "打开设置窗口"));
            list.Add(new HotkeyItem("Ctrl + Alt + Shift + R", "恢复所有窗口"));
            return list;
        }

        // =====================================================================
        // 刷新：侧栏与页面
        // =====================================================================

        private void ApplyEmptyState()
        {
            DesktopList.ItemsSource = new List<DesktopListItem>();
            DesktopCountText.Text = "共 0 个";
            NameBox.Text = string.Empty;
            WallpaperPathBox.Text = "（未选择图片）";
            ColorHexBox.Text = "#FF1B1F2A";
            IconItems.ItemsSource = new List<IconListItem>();
            IconCountText.Text = "0 个";
            IconEmptyState.Visibility = Visibility.Visible;
            WelcomeBanner.Visibility = Visibility.Collapsed;
            DataDirBox.Text = "（未知）";
            StartWithWindowsCheck.IsChecked = false;
            PauseCheck.IsChecked = false;
            NewDesktopButton.IsEnabled = false;
            MoveUpButton.IsEnabled = false;
            MoveDownButton.IsEnabled = false;
            DeleteDesktopButton.IsEnabled = false;
            SwitchDesktopButton.IsEnabled = false;
        }

        private void EnsureSelectedDesktop(AppConfig config)
        {
            if (!string.IsNullOrEmpty(_selectedDesktopId) && config.FindDesktop(_selectedDesktopId) != null)
            {
                return;
            }
            _selectedDesktopId = null;
            DesktopProfile current = null;
            try { current = _host.CurrentDesktop; }
            catch { }
            if (current != null && config.FindDesktop(current.Id) != null)
            {
                _selectedDesktopId = current.Id;
                return;
            }
            if (config.Desktops.Count > 0 && config.Desktops[0] != null)
            {
                _selectedDesktopId = config.Desktops[0].Id;
            }
        }

        private DesktopProfile GetSelectedDesktop()
        {
            if (_host == null || _host.Config == null) return null;
            AppConfig config = _host.Config;
            DesktopProfile desktop = config.FindDesktop(_selectedDesktopId);
            if (desktop != null) return desktop;

            DesktopProfile current = null;
            try { current = _host.CurrentDesktop; }
            catch { }
            if (current != null) return current;

            if (config.Desktops != null)
            {
                for (int i = 0; i < config.Desktops.Count; i++)
                {
                    if (config.Desktops[i] != null) return config.Desktops[i];
                }
            }
            return null;
        }

        private void RefreshDesktopList(AppConfig config)
        {
            List<DesktopListItem> items = new List<DesktopListItem>();
            string currentId = null;
            try
            {
                DesktopProfile current = _host.CurrentDesktop;
                if (current != null) currentId = current.Id;
            }
            catch { }

            for (int i = 0; i < config.Desktops.Count; i++)
            {
                DesktopProfile desktop = config.Desktops[i];
                if (desktop == null) continue;

                DesktopListItem item = new DesktopListItem();
                item.Id = desktop.Id;
                item.IndexText = (i + 1).ToString();
                item.DisplayName = string.IsNullOrEmpty(desktop.Name) ? "（未命名桌面）" : desktop.Name;
                int iconCount = desktop.Icons == null ? 0 : desktop.Icons.Count;
                item.IconCountText = iconCount == 0 ? "空桌面" : iconCount + " 个图标";
                item.IsCurrent = string.Equals(desktop.Id, currentId, StringComparison.Ordinal);
                items.Add(item);
            }

            DesktopList.ItemsSource = items;
            DesktopCountText.Text = "共 " + config.Desktops.Count + " 个";
            SelectDesktopListItem(items);
        }

        private void SelectDesktopListItem(List<DesktopListItem> items)
        {
            object found = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, _selectedDesktopId, StringComparison.Ordinal))
                {
                    found = items[i];
                    break;
                }
            }
            DesktopList.SelectedItem = found;
        }

        private void RefreshActionButtons(AppConfig config)
        {
            if (config == null)
            {
                NewDesktopButton.IsEnabled = false;
                MoveUpButton.IsEnabled = false;
                MoveDownButton.IsEnabled = false;
                DeleteDesktopButton.IsEnabled = false;
                return;
            }
            int index = string.IsNullOrEmpty(_selectedDesktopId) ? -1 : config.IndexOfDesktop(_selectedDesktopId);
            bool hasSelection = index >= 0;
            NewDesktopButton.IsEnabled = true;
            MoveUpButton.IsEnabled = hasSelection && index > 0;
            MoveDownButton.IsEnabled = hasSelection && index < config.Desktops.Count - 1;
            DeleteDesktopButton.IsEnabled = hasSelection && config.Desktops.Count > 1;
        }

        private void RefreshWelcomeBanner(AppConfig config)
        {
            bool show = !_welcomeDismissed
                        && config.Options != null
                        && !config.Options.FirstRunDone;
            WelcomeBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshCurrentPage()
        {
            if (_page == SettingsPage.Desktops) RefreshDesktopPage();
            else if (_page == SettingsPage.General) RefreshGeneralPage();
            // 帮助页是静态内容，无需刷新
        }

        private void RefreshDesktopPage()
        {
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            BeginSuppress();
            try
            {
                int iconCount = desktop.Icons == null ? 0 : desktop.Icons.Count;
                string name = string.IsNullOrEmpty(desktop.Name) ? "（未命名桌面）" : desktop.Name;
                DesktopPageSubtitle.Text = "正在编辑「" + name + "」 · "
                                           + (iconCount == 0 ? "图标集合为空" : iconCount + " 个图标");

                if (!NameBox.IsKeyboardFocusWithin) NameBox.Text = desktop.Name ?? string.Empty;
                ShowNameBadgeCheck.IsChecked = desktop.ShowNameBadge;
                RefreshWallpaperControls(desktop);
                RefreshIconAppearanceControls();
                RefreshIconList(desktop);
                RefreshSwitchButtonState();
            }
            finally
            {
                EndSuppress();
            }
        }

        /// <summary>
        /// 同步「当前桌面」徽章与切换按钮的状态。独立于页面刷新调用，
        /// 保证托盘 / 快捷键 / 本窗口按钮触发的切换都能立即反映到界面上。
        /// </summary>
        private void RefreshSwitchButtonState()
        {
            if (_closed) return;
            try
            {
                if (_host == null || _host.Config == null)
                {
                    SwitchDesktopButton.IsEnabled = false;
                    SwitchDesktopButton.Content = "切换桌面";
                    return;
                }
                DesktopProfile selected = GetSelectedDesktop();
                DesktopProfile current = null;
                try { current = _host.CurrentDesktop; }
                catch { }
                bool isCurrent = selected == null
                                 || current == null
                                 || string.Equals(selected.Id, current.Id, StringComparison.Ordinal);
                SwitchDesktopButton.IsEnabled = !isCurrent;
                SwitchDesktopButton.Content = isCurrent ? "当前桌面" : "切换到该桌面";
                // “当前桌面”容易被理解为可点击，用 tooltip 明确语义
                SwitchDesktopButton.ToolTip = isCurrent
                                                  ? "左侧选中的桌面就是当前显示桌面，无需切换"
                                                  : "点击切换到左侧选中的桌面";
            }
            catch
            {
                // 界面状态刷新失败不应影响主流程
            }
        }

        private void RefreshWallpaperControls(DesktopProfile desktop)
        {
            RadioWallpaperSystem.IsChecked = desktop.WallpaperMode == WallpaperMode.System;
            RadioWallpaperImage.IsChecked = desktop.WallpaperMode == WallpaperMode.Image;
            RadioWallpaperSolid.IsChecked = desktop.WallpaperMode == WallpaperMode.SolidColor;

            RefreshWallpaperPanels(desktop);

            WallpaperPathBox.Text = string.IsNullOrEmpty(desktop.WallpaperPath)
                                        ? "（未选择图片）"
                                        : desktop.WallpaperPath;

            int fitIndex = (int)desktop.WallpaperFit;
            if (fitIndex < 0 || fitIndex > 4) fitIndex = 0;
            FitCombo.SelectedIndex = fitIndex;

            if (!ColorHexBox.IsKeyboardFocusWithin)
            {
                ColorHexBox.Text = NormalizeColorText(desktop.BackgroundColor);
                ColorHexBox.ClearValue(Control.BorderBrushProperty);
            }
            ColorPreview.Background = BrushFromHex(desktop.BackgroundColor);
        }

        /// <summary>只切换“跟随当前桌面壁纸 / 图片 / 纯色”三块面板的显示状态。</summary>
        private void RefreshWallpaperPanels(DesktopProfile desktop)
        {
            bool system = desktop.WallpaperMode == WallpaperMode.System;
            bool image = desktop.WallpaperMode == WallpaperMode.Image;
            bool solid = desktop.WallpaperMode == WallpaperMode.SolidColor;

            WallpaperSystemHint.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
            WallpaperImagePanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            WallpaperSolidPanel.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>刷新「图标外观」区域（图标大小 / 自动排列 / 对齐 Windows），从配置读取。</summary>
        private void RefreshIconAppearanceControls()
        {
            AppOptions options = EnsureOptions();
            if (options == null) return;
            int sizeIndex = IconSizeToIndex(options.IconSize);
            if (IconSizeCombo.SelectedIndex != sizeIndex) IconSizeCombo.SelectedIndex = sizeIndex;
            AutoArrangeCheck.IsChecked = options.AutoArrangeIcons;

            bool matchWindows = options.MatchWindowsIconMetrics;
            MatchWindowsIconsCheck.IsChecked = matchWindows;
            IconSizeCombo.IsEnabled = !matchWindows;
            IconSizeHint.Visibility = matchWindows ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshIconList(DesktopProfile desktop)
        {
            List<IconListItem> items = new List<IconListItem>();
            if (desktop.Icons != null)
            {
                for (int i = 0; i < desktop.Icons.Count; i++)
                {
                    IconEntry entry = desktop.Icons[i];
                    if (entry == null) continue;

                    IconListItem item = new IconListItem();
                    item.IconId = entry.Id;
                    item.DisplayName = string.IsNullOrEmpty(entry.DisplayName)
                                           ? (entry.TargetPath ?? string.Empty)
                                           : entry.DisplayName;
                    item.TargetPath = entry.TargetPath ?? string.Empty;
                    try
                    {
                        item.IconSource = IconService.GetIcon(entry.TargetPath, 32);
                    }
                    catch
                    {
                        item.IconSource = null;
                    }
                    items.Add(item);
                }
            }

            IconItems.ItemsSource = items;
            int count = desktop.Icons == null ? 0 : desktop.Icons.Count;
            IconCountText.Text = count + " 个";
            IconEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshGeneralPage()
        {
            if (_host == null) return;
            BeginSuppress();
            try
            {
                AppConfig config = null;
                try { config = _host.Config; }
                catch { }

                bool startWithWindows = config != null && config.Options != null && config.Options.StartWithWindows;
                StartWithWindowsCheck.IsChecked = startWithWindows;

                // SkipRestrictedPrompt=false（默认）时勾选“启动时检测并提示受限模式”
                bool promptRestricted = config == null || config.Options == null || !config.Options.SkipRestrictedPrompt;
                RestrictedPromptCheck.IsChecked = promptRestricted;

                bool paused = false;
                try { paused = _host.IsPaused; }
                catch { }
                PauseCheck.IsChecked = paused;
                PauseStatusText.Text = paused
                                           ? "当前状态：已暂停（所有窗口都显示）"
                                           : "当前状态：正在管理窗口";
                PauseStatusText.Foreground = paused ? WarningSolid : SecondarySolid;

                string dataDir = null;
                try { dataDir = _host.DataDirectory; }
                catch { }
                DataDirBox.Text = string.IsNullOrEmpty(dataDir) ? "（未知）" : dataDir;
            }
            finally
            {
                EndSuppress();
            }
        }

        private void RefreshNavVisuals()
        {
            ApplyNavVisual(NavGeneralButton, _page == SettingsPage.General);
            ApplyNavVisual(NavHelpButton, _page == SettingsPage.Help);
        }

        private static void ApplyNavVisual(Button button, bool active)
        {
            if (button == null) return;
            button.Background = active ? NavActiveBackground : Brushes.Transparent;
            button.Foreground = active ? AccentSolid : SecondarySolid;
        }

        private void ShowPage(SettingsPage page)
        {
            _page = page;
            DesktopPage.Visibility = page == SettingsPage.Desktops ? Visibility.Visible : Visibility.Collapsed;
            GeneralPage.Visibility = page == SettingsPage.General ? Visibility.Visible : Visibility.Collapsed;
            HelpPage.Visibility = page == SettingsPage.Help ? Visibility.Visible : Visibility.Collapsed;
            RefreshNavVisuals();
            RefreshCurrentPage();
        }

        // =====================================================================
        // host 事件 → 合并刷新
        // =====================================================================

        private void Host_StateChanged(object sender, EventArgs e)
        {
            if (_closed) return;
            // 桌面切换/配置变化事件不能因为当前处于重入保护而被丢弃：
            // 统一安排一次合并刷新，并立即同步切换按钮状态。
            ScheduleRefresh();
            if (Dispatcher.CheckAccess()) RefreshSwitchButtonState();
        }

        private void ScheduleRefresh()
        {
            if (_closed) return;
            if (_refreshQueued) return;
            _refreshQueued = true;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    _refreshQueued = false;
                    if (_refreshing)
                    {
                        // 正在刷新中，下一轮再补一次，避免状态更新被重入吞掉
                        ScheduleRefresh();
                        return;
                    }
                    RefreshSettings();
                }));
            }
            catch
            {
                _refreshQueued = false;
            }
        }

        // =====================================================================
        // 窗口 / 标题栏
        // =====================================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已松开等情况下 DragMove 会抛异常，忽略即可
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseToTray_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void SettingsWindow_Closing(object sender, CancelEventArgs e)
        {
            // 关闭按钮 / Alt+F4 只是隐藏到托盘，只有托盘“退出”或设置里的“退出程序”才真正退出
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void SettingsWindow_Closed(object sender, EventArgs e)
        {
            _closed = true;
            if (_host != null)
            {
                try
                {
                    _host.ConfigChanged -= Host_StateChanged;
                    _host.CurrentDesktopChanged -= Host_StateChanged;
                    _host.PauseStateChanged -= Host_StateChanged;
                }
                catch { }
            }
        }

        private void SettingsWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) ScheduleRefresh();
        }

        // =====================================================================
        // 侧栏：桌面列表与增删排序
        // =====================================================================

        private void DesktopList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || Suppressed) return;
            DesktopListItem item = DesktopList.SelectedItem as DesktopListItem;
            if (item == null) return;

            // 注意：真实鼠标点击时 PreviewMouseLeftButtonDown 会先把 _selectedDesktopId 更新为
            // 新选中的桌面，因此这里不能用“id 是否变化”来决定是否刷新，否则按钮状态会漏刷新。
            // 选中项变化必须：切回桌面页 → 刷新副标题/各控件 → 刷新“切换桌面”按钮。
            _selectedDesktopId = item.Id;
            ShowPage(SettingsPage.Desktops);
            RefreshActionButtons(_host != null ? _host.Config : null);
            RefreshSwitchButtonState();
        }

        /// <summary>
        /// 在「常规设置 / 帮助」页时，点击桌面列表（即使点击的是已选中的桌面）
        /// 也要切回桌面设置页；同时立即刷新切换按钮，保证鼠标点击后状态即时正确。
        /// </summary>
        private void DesktopList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_initialized) return;
            ListBoxItem container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (container == null || container.DataContext == null) return;
            DesktopListItem item = container.DataContext as DesktopListItem;
            if (item != null) _selectedDesktopId = item.Id;
            if (_page != SettingsPage.Desktops)
            {
                ShowPage(SettingsPage.Desktops);
                RefreshActionButtons(_host != null ? _host.Config : null);
            }
            // 鼠标按下即刷新按钮：避免“选中新桌面但按钮仍显示旧的当前桌面/禁用”的状态
            RefreshSwitchButtonState();
        }

        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null)
            {
                T typed = node as T;
                if (typed != null) return typed;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private void NewDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            AppConfig config = _host.Config;
            if (config.Desktops == null) config.Desktops = new List<DesktopProfile>();

            DesktopProfile desktop = new DesktopProfile();
            desktop.Name = CreateDesktopName(config);
            config.Desktops.Add(desktop);
            _selectedDesktopId = desktop.Id;

            NotifyConfigChanged();
            ShowPage(SettingsPage.Desktops);
            RefreshSettings();
        }

        /// <summary>生成不重复的「桌面 N」名称。</summary>
        private static string CreateDesktopName(AppConfig config)
        {
            int index = config.Desktops.Count + 1;
            while (true)
            {
                string candidate = "桌面 " + index;
                bool exists = false;
                for (int i = 0; i < config.Desktops.Count; i++)
                {
                    DesktopProfile desktop = config.Desktops[i];
                    if (desktop != null && string.Equals(desktop.Name, candidate, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) return candidate;
                index++;
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedDesktop(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedDesktop(1);
        }

        private void MoveSelectedDesktop(int delta)
        {
            if (_host == null || _host.Config == null) return;
            AppConfig config = _host.Config;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            int index = config.IndexOfDesktop(desktop.Id);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= config.Desktops.Count) return;

            DesktopProfile other = config.Desktops[target];
            config.Desktops[target] = desktop;
            config.Desktops[index] = other;
            _selectedDesktopId = desktop.Id;

            NotifyConfigChanged();
            RefreshSettings();
        }

        private void DeleteDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            AppConfig config = _host.Config;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            if (config.Desktops.Count <= 1)
            {
                ShowMessage("至少需要保留一个桌面，无法删除。", "删除桌面", MessageBoxImage.Information);
                return;
            }

            string name = string.IsNullOrEmpty(desktop.Name) ? "未命名桌面" : desktop.Name;
            MessageBoxResult result = MessageBox.Show(this,
                "确定要删除桌面「" + name + "」吗？\n\n该桌面上的图标和壁纸配置会被移除，但不会删除磁盘上的任何文件。",
                "删除桌面", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            int index = config.IndexOfDesktop(desktop.Id);
            int neighborIndex = index > 0 ? index - 1 : index + 1;
            if (neighborIndex < 0) neighborIndex = 0;
            if (neighborIndex >= config.Desktops.Count) neighborIndex = config.Desktops.Count - 1;
            DesktopProfile neighbor = neighborIndex >= 0 && neighborIndex < config.Desktops.Count
                                          ? config.Desktops[neighborIndex]
                                          : null;

            // 删除的是当前桌面时，先切换到相邻桌面，避免当前桌面悬空
            bool isCurrent = false;
            try
            {
                DesktopProfile current = _host.CurrentDesktop;
                isCurrent = current != null && string.Equals(current.Id, desktop.Id, StringComparison.Ordinal);
            }
            catch { }
            if (isCurrent && neighbor != null)
            {
                try { _host.RequestSwitch(neighbor.Id); }
                catch { }
            }

            config.Desktops.Remove(desktop);
            if (string.Equals(config.ActiveDesktopId, desktop.Id, StringComparison.Ordinal))
            {
                config.ActiveDesktopId = neighbor != null
                                             ? neighbor.Id
                                             : (config.Desktops.Count > 0 && config.Desktops[0] != null
                                                    ? config.Desktops[0].Id
                                                    : string.Empty);
            }
            _selectedDesktopId = neighbor != null ? neighbor.Id : null;

            NotifyConfigChanged();
            RefreshSettings();
        }

        // =====================================================================
        // 页面 A：桌面名称
        // =====================================================================

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitDesktopName();
                // 提交后把焦点移走，让侧栏名称立即刷新
                NameBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DesktopProfile desktop = GetSelectedDesktop();
                if (desktop != null)
                {
                    BeginSuppress();
                    try { NameBox.Text = desktop.Name ?? string.Empty; }
                    finally { EndSuppress(); }
                }
                NameBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitDesktopName();
        }

        private void CommitDesktopName()
        {
            if (!_initialized || _host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            string text = NameBox.Text == null ? string.Empty : NameBox.Text.Trim();
            if (text.Length == 0)
            {
                BeginSuppress();
                try { NameBox.Text = desktop.Name ?? string.Empty; }
                finally { EndSuppress(); }
                return;
            }
            if (string.Equals(text, desktop.Name, StringComparison.Ordinal)) return;

            desktop.Name = text;
            NotifyConfigChanged();
            RefreshSettings();
        }

        private void ShowNameBadge_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;
            desktop.ShowNameBadge = ShowNameBadgeCheck.IsChecked == true;
            NotifyConfigChanged();
        }

        // =====================================================================
        // 页面 A：壁纸
        // =====================================================================

        private void WallpaperMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            RadioButton radio = sender as RadioButton;
            if (radio == null || radio.IsChecked != true) return;

            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            string tag = radio.Tag as string;
            WallpaperMode mode = desktop.WallpaperMode;
            if (tag == "System") mode = WallpaperMode.System;
            else if (tag == "Image") mode = WallpaperMode.Image;
            else if (tag == "SolidColor") mode = WallpaperMode.SolidColor;
            else return;

            if (mode == desktop.WallpaperMode) return;

            desktop.WallpaperMode = mode;
            BeginSuppress();
            try { RefreshWallpaperPanels(desktop); }
            finally { EndSuppress(); }
            NotifyConfigChanged();
        }

        private void ChooseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "选择桌面壁纸";
            dialog.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有文件|*.*";
            dialog.CheckFileExists = true;
            dialog.Multiselect = false;
            bool? result = null;
            try { result = dialog.ShowDialog(this); }
            catch (Exception ex)
            {
                ShowError("打开文件对话框失败", ex);
                return;
            }
            if (result != true) return;

            desktop.WallpaperPath = dialog.FileName;
            desktop.WallpaperMode = WallpaperMode.Image;
            BeginSuppress();
            try { RefreshWallpaperControls(desktop); }
            finally { EndSuppress(); }
            NotifyConfigChanged();
            RefreshSettings();
        }

        private void FitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            int index = FitCombo.SelectedIndex;
            if (index < 0 || index > 4) return;
            WallpaperFit fit = WallpaperFit.Fill;
            switch (index)
            {
                case 0: fit = WallpaperFit.Fill; break;
                case 1: fit = WallpaperFit.Fit; break;
                case 2: fit = WallpaperFit.Stretch; break;
                case 3: fit = WallpaperFit.Center; break;
                case 4: fit = WallpaperFit.Tile; break;
            }
            if (fit == desktop.WallpaperFit) return;
            desktop.WallpaperFit = fit;
            NotifyConfigChanged();
        }

        private void ColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized || Suppressed || _refreshing) return;
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            string text = ColorHexBox.Text;
            Color color;
            if (!TryParseHexColor(text, out color))
            {
                // 非法输入不提交，只把边框标红提示
                if (!string.IsNullOrEmpty(text)) ColorHexBox.BorderBrush = DangerSolid;
                return;
            }
            ColorHexBox.ClearValue(Control.BorderBrushProperty);

            string normalized = NormalizeColorText(text);
            string currentColor = NormalizeColorText(desktop.BackgroundColor);
            if (string.Equals(normalized, currentColor, StringComparison.OrdinalIgnoreCase)) return;

            desktop.BackgroundColor = normalized;
            ColorPreview.Background = CreateFrozenBrush(color);
            NotifyConfigChanged();
        }

        private void PresetColor_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            Button button = sender as Button;
            if (button == null) return;
            string hex = button.Tag as string;
            if (string.IsNullOrEmpty(hex)) return;

            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            Color color;
            if (!TryParseHexColor(hex, out color)) return;
            string normalized = NormalizeColorText(hex);

            desktop.BackgroundColor = normalized;
            desktop.WallpaperMode = WallpaperMode.SolidColor; // 点预设颜色时自动切到纯色模式
            BeginSuppress();
            try
            {
                ColorHexBox.Text = normalized;
                ColorHexBox.ClearValue(Control.BorderBrushProperty);
                ColorPreview.Background = CreateFrozenBrush(color);
                RefreshWallpaperControls(desktop);
            }
            finally
            {
                EndSuppress();
            }
            NotifyConfigChanged();
        }

        // =====================================================================
        // 页面 A：桌面图标
        // =====================================================================

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;

            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "添加文件到桌面";
            dialog.Filter = "所有文件|*.*";
            dialog.Multiselect = true;
            dialog.CheckFileExists = true;
            bool? result = null;
            try { result = dialog.ShowDialog(this); }
            catch (Exception ex)
            {
                ShowError("打开文件对话框失败", ex);
                return;
            }
            if (result != true) return;
            AddPaths(dialog.FileNames);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            try
            {
                using (WinForms.FolderBrowserDialog dialog = new WinForms.FolderBrowserDialog())
                {
                    dialog.Description = "选择要添加到当前桌面的文件夹";
                    dialog.ShowNewFolderButton = false;
                    if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
                    AddPaths(new string[] { dialog.SelectedPath });
                }
            }
            catch (Exception ex)
            {
                ShowError("打开文件夹对话框失败", ex);
            }
        }

        private void ArrangeIcons_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;
            if (desktop.Icons == null || desktop.Icons.Count == 0)
            {
                ShowMessage("当前桌面还没有图标。", "整理图标", MessageBoxImage.Information);
                return;
            }
            // 即使关闭了「自动排列」，点“整理图标”也强制按当前图标参数整理一次
            IconLayout.ArrangeAll(desktop, ScreenLayout.Capture().LayoutArea, GetIconMetricsSafe());
            NotifyConfigChanged();
            RefreshSettings();
        }

        private void SortIconsByName_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;
            if (desktop.Icons == null || desktop.Icons.Count == 0)
            {
                ShowMessage("当前桌面还没有图标。", "按名称排序", MessageBoxImage.Information);
                return;
            }

            desktop.Icons.Sort(delegate(IconEntry left, IconEntry right)
            {
                string leftName = left == null
                                      ? string.Empty
                                      : (string.IsNullOrEmpty(left.DisplayName) ? left.TargetPath : left.DisplayName);
                string rightName = right == null
                                       ? string.Empty
                                       : (string.IsNullOrEmpty(right.DisplayName) ? right.TargetPath : right.DisplayName);
                return string.Compare(leftName ?? string.Empty, rightName ?? string.Empty,
                                      StringComparison.CurrentCultureIgnoreCase);
            });
            IconLayout.ArrangeAll(desktop, ScreenLayout.Capture().LayoutArea, GetIconMetricsSafe());
            NotifyConfigChanged();
            RefreshSettings();
        }

        private void IconSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            AppOptions options = EnsureOptions();
            if (options == null) return;
            int size = IndexToIconSize(IconSizeCombo.SelectedIndex);
            if (options.IconSize == size) return;
            options.IconSize = size;
            NotifyConfigChanged();
        }

        private void AutoArrangeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            AppOptions options = EnsureOptions();
            if (options == null) return;
            bool autoArrange = AutoArrangeCheck.IsChecked == true;
            if (options.AutoArrangeIcons == autoArrange) return;
            options.AutoArrangeIcons = autoArrange;
            NotifyConfigChanged();
        }

        private void MatchWindowsIconsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            AppOptions options = EnsureOptions();
            if (options == null) return;
            bool matchWindows = MatchWindowsIconsCheck.IsChecked == true;
            if (options.MatchWindowsIconMetrics == matchWindows) return;
            options.MatchWindowsIconMetrics = matchWindows;
            BeginSuppress();
            try
            {
                // 勾选“与 Windows 一致”时，自定义大小下拉灰显
                IconSizeCombo.IsEnabled = !matchWindows;
                IconSizeHint.Visibility = matchWindows ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                EndSuppress();
            }
            NotifyConfigChanged();
        }

        private void IconOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            IconListItem item = ResolveIconListItem(sender);
            if (item == null) return;
            string error = Launcher.OpenFileLocation(item.TargetPath);
            if (!string.IsNullOrEmpty(error))
            {
                ShowMessage("无法打开图标位置：" + error, "打开位置", MessageBoxImage.Warning);
            }
        }

        private void IconRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            IconListItem item = ResolveIconListItem(sender);
            if (item == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null || desktop.Icons == null) return;

            IconEntry entry = desktop.FindIcon(item.IconId);
            if (entry == null) return;
            desktop.Icons.Remove(entry); // 只从配置中移除，不动磁盘文件
            NotifyConfigChanged();
            RefreshSettings();
        }

        private static IconListItem ResolveIconListItem(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null) return null;
            return element.DataContext as IconListItem;
        }

        /// <summary>把一批文件/文件夹路径添加到当前选中的桌面（自动分配空位、跳过重复项）。</summary>
        private void AddPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;
            if (desktop.Icons == null) desktop.Icons = new List<IconEntry>();

            Rect area = ScreenLayout.Capture().LayoutArea;
            IconMetrics iconMetrics = GetIconMetricsSafe();
            int added = 0;
            int skipped = 0;

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                bool exists = false;
                try { exists = File.Exists(path) || Directory.Exists(path); }
                catch { exists = false; }
                if (!exists)
                {
                    skipped++;
                    continue;
                }

                bool duplicate = false;
                for (int i = 0; i < desktop.Icons.Count; i++)
                {
                    IconEntry old = desktop.Icons[i];
                    if (old != null && string.Equals(old.TargetPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    skipped++;
                    continue;
                }

                IconEntry entry = Launcher.CreateFromPath(path);
                if (entry == null)
                {
                    skipped++;
                    continue;
                }
                Point position = IconLayout.NextFreeCell(desktop, area, iconMetrics);
                entry.X = position.X;
                entry.Y = position.Y;
                desktop.Icons.Add(entry);
                added++;
            }

            if (added > 0)
            {
                NotifyConfigChanged();
                RefreshSettings();
            }
            else if (skipped > 0)
            {
                ShowMessage("没有添加任何图标：这些项目已经在该桌面中，或路径不存在。",
                            "添加图标", MessageBoxImage.Information);
            }
        }

        // =====================================================================
        // 页面 A：图标列表拖放
        // =====================================================================

        private void IconDropZone_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (CanAcceptDrop(e.Data)) IconDropZone.BorderBrush = AccentSolid;
        }

        private void IconDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (CanAcceptDrop(e.Data)) IconDropZone.BorderBrush = AccentSolid;
        }

        private void IconDropZone_DragLeave(object sender, DragEventArgs e)
        {
            RestoreDropZoneBorder();
        }

        private void IconDropZone_Drop(object sender, DragEventArgs e)
        {
            RestoreDropZoneBorder();
            if (!CanAcceptDrop(e.Data)) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) AddPaths(files);
            e.Handled = true;
        }

        private static bool CanAcceptDrop(IDataObject data)
        {
            return data != null && data.GetDataPresent(DataFormats.FileDrop);
        }

        private void RestoreDropZoneBorder()
        {
            IconDropZone.BorderBrush = _dropZoneNormalBorder;
        }

        // =====================================================================
        // 页面 B：常规设置
        // =====================================================================

        private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null) return;
            bool desired = StartWithWindowsCheck.IsChecked == true;
            try
            {
                _host.SetStartWithWindows(desired);
            }
            catch (Exception ex)
            {
                BeginSuppress();
                try { StartWithWindowsCheck.IsChecked = !desired; }
                finally { EndSuppress(); }
                ShowError("设置开机自动启动失败", ex);
                return;
            }
            RefreshSettings();
        }

        private void PauseCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null) return;
            bool desired = PauseCheck.IsChecked == true;
            try
            {
                _host.SetPaused(desired);
            }
            catch (Exception ex)
            {
                BeginSuppress();
                try { PauseCheck.IsChecked = !desired; }
                finally { EndSuppress(); }
                string hint = "\n\n如果目标窗口属于以管理员权限运行的程序，可能需要以管理员身份运行多桌面助手。";
                ShowMessage("切换暂停状态失败：\n" + ex.Message + hint, "暂停窗口管理", MessageBoxImage.Warning);
                return;
            }
            RefreshSettings();
        }

        private void RestrictedPromptCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || Suppressed || _host == null || _host.Config == null) return;
            AppOptions options = EnsureOptions();
            if (options == null) return;
            // 勾选“提示” = SkipRestrictedPrompt=false
            bool promptRestricted = RestrictedPromptCheck.IsChecked == true;
            bool skip = !promptRestricted;
            if (options.SkipRestrictedPrompt == skip) return;
            options.SkipRestrictedPrompt = skip;
            NotifyConfigChanged();
        }

        private void RestoreAll_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            try
            {
                _host.RestoreAllWindows();
            }
            catch (Exception ex)
            {
                ShowError("恢复窗口失败", ex);
                return;
            }
            ShowMessage("已恢复所有被隐藏的窗口。", "恢复窗口", MessageBoxImage.Information);
        }

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            string directory = null;
            try { directory = _host.DataDirectory; }
            catch { }
            if (string.IsNullOrEmpty(directory))
            {
                ShowMessage("数据目录未知。", "打开目录", MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(directory))
            {
                ShowMessage("数据目录不存在：" + directory, "打开目录", MessageBoxImage.Warning);
                return;
            }
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo("explorer.exe", "\"" + directory + "\"");
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                ShowError("打开数据目录失败", ex);
            }
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null)
            {
                _allowClose = true;
                try { Close(); }
                catch { }
                return;
            }

            MessageBoxResult result = MessageBox.Show(this,
                "退出多桌面助手后会立即恢复所有被隐藏的窗口，并从系统托盘移除图标。\n\n确定要退出吗？",
                "退出多桌面助手", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            _allowClose = true;
            try
            {
                _host.Shutdown();
            }
            catch (Exception ex)
            {
                ShowError("退出程序时发生错误", ex);
                try { Application.Current.Shutdown(); }
                catch { }
            }
        }

        // =====================================================================
        // 页面导航 / 欢迎条
        // =====================================================================

        private void NavGeneral_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(SettingsPage.General);
        }

        private void NavHelp_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(SettingsPage.Help);
        }

        private void WelcomeDismiss_Click(object sender, RoutedEventArgs e)
        {
            _welcomeDismissed = true;
            if (_host != null && _host.Config != null)
            {
                if (_host.Config.Options == null) _host.Config.Options = new AppOptions();
                if (!_host.Config.Options.FirstRunDone)
                {
                    _host.Config.Options.FirstRunDone = true;
                    NotifyConfigChanged();
                }
            }
            WelcomeBanner.Visibility = Visibility.Collapsed;
            RefreshSettings();
        }

        private void SwitchDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null || _host.Config == null) return;
            DesktopProfile desktop = GetSelectedDesktop();
            if (desktop == null) return;

            bool isCurrent = false;
            try
            {
                DesktopProfile current = _host.CurrentDesktop;
                isCurrent = current != null && string.Equals(current.Id, desktop.Id, StringComparison.Ordinal);
            }
            catch { }
            if (isCurrent) return;

            try
            {
                _host.RequestSwitch(desktop.Id);
            }
            catch (Exception ex)
            {
                string message = ex == null || string.IsNullOrEmpty(ex.Message) ? "未知错误" : ex.Message;
                ShowMessage("切换桌面失败：" + message, "切换桌面", MessageBoxImage.Warning);
                ScheduleRefresh();
                return;
            }

            // 控制器可能同步或异步完成切换，两个时机都补一次，确保按钮/徽章立即正确
            RefreshSwitchButtonState();
            ScheduleRefresh();
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    RefreshSwitchButtonState();
                    ScheduleRefresh();
                }));
            }
            catch
            {
            }
        }

        // =====================================================================
        // 工具方法
        // =====================================================================

        /// <summary>确保 Config.Options 不为 null（只改配置对象，保存由 host 负责）。</summary>
        private AppOptions EnsureOptions()
        {
            if (_host == null || _host.Config == null) return null;
            if (_host.Config.Options == null) _host.Config.Options = new AppOptions();
            return _host.Config.Options;
        }

        /// <summary>当前配置的图标大小（归一化为 32/48/64 之一）。</summary>
        private int GetConfiguredIconSize()
        {
            AppOptions options = EnsureOptions();
            if (options == null) return IconSizeValues[1];
            return IconSizeValues[IconSizeToIndex(options.IconSize)];
        }

        /// <summary>
        /// 取当前生效的图标栅格参数：优先 host.GetIconMetrics()（勾选“与 Windows 一致”时跟随系统），
        /// host 不可用或异常时回退到自定义图标大小。
        /// </summary>
        private IconMetrics GetIconMetricsSafe()
        {
            try
            {
                if (_host != null)
                {
                    IconMetrics metrics = _host.GetIconMetrics();
                    if (metrics != null) return metrics;
                }
            }
            catch
            {
            }
            return IconLayout.CreateMetrics(GetConfiguredIconSize());
        }

        /// <summary>配置中的图标大小 → 下拉框序号。</summary>
        private static int IconSizeToIndex(int iconSize)
        {
            if (iconSize <= 40) return 0;
            if (iconSize <= 56) return 1;
            return 2;
        }

        /// <summary>下拉框序号 → 图标大小像素值。</summary>
        private static int IndexToIconSize(int index)
        {
            if (index < 0 || index >= IconSizeValues.Length) return IconSizeValues[1];
            return IconSizeValues[index];
        }

        private bool Suppressed
        {
            get { return _suppressDepth > 0; }
        }

        private void BeginSuppress()
        {
            _suppressDepth++;
        }

        private void EndSuppress()
        {
            if (_suppressDepth > 0) _suppressDepth--;
        }

        private void NotifyConfigChanged()
        {
            if (_host == null) return;
            try
            {
                _host.NotifyConfigChanged();
            }
            catch (Exception ex)
            {
                ShowError("保存设置失败", ex);
            }
        }

        private void ShowError(string title, Exception ex)
        {
            string message = ex == null || string.IsNullOrEmpty(ex.Message) ? "未知错误" : ex.Message;
            ShowMessage(message, title, MessageBoxImage.Error);
        }

        private void ShowMessage(string message, string title, MessageBoxImage icon)
        {
            try
            {
                if (IsVisible) MessageBox.Show(this, message, title, MessageBoxButton.OK, icon);
                else MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            }
            catch
            {
                // 对话框失败（例如窗口销毁过程中）不应影响主流程
            }
        }

        private static string NormalizeColorText(string value)
        {
            Color color;
            if (!TryParseHexColor(value, out color))
            {
                return "#FF1B1F2A";
            }
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
        }

        private static bool TryParseHexColor(string text, out Color color)
        {
            color = Color.FromRgb(0x1B, 0x1F, 0x2A);
            if (string.IsNullOrEmpty(text)) return false;
            string value = text.Trim();
            if (!HexColorRegex.IsMatch(value)) return false;
            string hex = value.Substring(1);
            try
            {
                if (hex.Length == 6)
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    color = Color.FromRgb(r, g, b);
                    return true;
                }
                if (hex.Length == 8)
                {
                    byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }
            }
            catch
            {
                // 非法十六进制数字
            }
            return false;
        }

        private static Brush BrushFromHex(string hex)
        {
            Color color;
            if (TryParseHexColor(hex, out color)) return CreateFrozenBrush(color);
            return CreateFrozenBrush(Color.FromRgb(0x1B, 0x1F, 0x2A));
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
