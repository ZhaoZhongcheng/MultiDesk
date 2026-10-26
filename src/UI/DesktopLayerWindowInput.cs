using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDesk.Core;

namespace MultiDesk.UI
{
    /// <summary>
    /// DesktopLayerWindow 的鼠标 / 拖放交互部分（partial 实现，与主文件同一类）。
    ///
    /// 交互约定：
    ///   * 左键单击图标 → 选中；双击同一图标（420ms 内）→ 启动；
    ///   * 左键按住拖动超过 4 DIP → 拖动图标，松手后吸附到栅格并保存；
    ///   * 空白处单击 → 取消选中；
    ///   * 右键图标 / 空白 → 弹出窗口内自绘菜单；
    ///   * 从资源管理器拖入文件 / 文件夹 → 生成图标并按落点布局。
    /// </summary>
    public partial class DesktopLayerWindow
    {
        /// <summary>拖动判定阈值（DIP）。</summary>
        private const double DragThreshold = 4.0;

        /// <summary>双击判定间隔（毫秒）。</summary>
        private const double DoubleClickMilliseconds = 420.0;

        /// <summary>菜单打开时用于检测「点击窗口外部」的轮询间隔（毫秒）。</summary>
        private const double MenuWatchMilliseconds = 200.0;

        // 拖动状态
        private DesktopLayerIconView _dragView;
        private IconEntry _dragIcon;
        private Point _dragStartPoint;
        private Point _dragGrabOffset;
        private bool _dragging;

        // 双击判定
        private string _lastClickIconId;
        private DateTime _lastClickTime = DateTime.MinValue;

        private void AttachInputHandlers()
        {
            try
            {
                MouseLeftButtonDown += WindowMouseLeftButtonDownHandler;
                MouseMove += WindowMouseMoveHandler;
                MouseLeftButtonUp += WindowMouseLeftButtonUpHandler;
                MouseRightButtonDown += WindowMouseRightButtonDownHandler;
                LostMouseCapture += WindowLostMouseCaptureHandler;
                SizeChanged += WindowSizeChangedHandler;
                DragEnter += WindowDragEnterHandler;
                DragOver += WindowDragOverHandler;
                DragLeave += WindowDragLeaveHandler;
                Drop += WindowDropHandler;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 注册输入事件失败: " + ex);
            }
        }

        // ================= 鼠标左键：选中 / 双击 / 拖动 =================

        private void WindowMouseLeftButtonDownHandler(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_menu != null && _menu.IsOpen) return; // 菜单层自己处理点击
                Point position = e.GetPosition(RootGrid);
                DesktopLayerIconView view = FindIconView(e.OriginalSource as DependencyObject);
                if (view == null || view.Entry == null)
                {
                    // 点击空白：取消选中、重置双击判定
                    SelectIcon(null);
                    _lastClickIconId = null;
                    _lastClickTime = DateTime.MinValue;
                    return;
                }

                IconEntry icon = view.Entry;
                SelectIcon(icon.Id);
                _dragView = view;
                _dragIcon = icon;
                _dragStartPoint = position;
                _dragGrabOffset = e.GetPosition(view);
                _dragging = false;
                try
                {
                    RootGrid.CaptureMouse();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 捕获鼠标失败: " + ex.Message);
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 左键按下处理失败: " + ex);
            }
        }

        private void WindowMouseMoveHandler(object sender, MouseEventArgs e)
        {
            try
            {
                if (_dragView == null || e.LeftButton != MouseButtonState.Pressed) return;
                Point position = e.GetPosition(RootGrid);

                if (!_dragging)
                {
                    double dx = position.X - _dragStartPoint.X;
                    double dy = position.Y - _dragStartPoint.Y;
                    if (dx * dx + dy * dy < DragThreshold * DragThreshold) return;
                    _dragging = true;
                    _dragView.SetSelected(false);
                    _dragView.SetHover(false);
                    _dragView.SetDragging(true);
                    _dragView.Cursor = Cursors.SizeAll;
                }

                double left = position.X - _dragGrabOffset.X;
                double top = position.Y - _dragGrabOffset.Y;
                Canvas.SetLeft(_dragView, left);
                Canvas.SetTop(_dragView, top);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 拖动处理失败: " + ex);
            }
        }

        private void WindowMouseLeftButtonUpHandler(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_dragView == null) return;
                DesktopLayerIconView view = _dragView;
                IconEntry icon = _dragIcon;
                bool wasDragging = _dragging;

                _dragView = null;
                _dragIcon = null;
                _dragging = false;
                try
                {
                    RootGrid.ReleaseMouseCapture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 释放鼠标失败: " + ex.Message);
                }
                view.SetDragging(false);
                view.Cursor = null;

                if (icon == null)
                {
                    e.Handled = true;
                    return;
                }

                if (wasDragging)
                {
                    // 拖动结束：网格模式吸附避让，自由模式按鼠标绝对坐标放置（夹在屏幕内）
                    Point drop = new Point(Canvas.GetLeft(view), Canvas.GetTop(view));
                    if (double.IsNaN(drop.X)) drop.X = icon.X;
                    if (double.IsNaN(drop.Y)) drop.Y = icon.Y;
                    DesktopProfile desktop = GetCurrentDesktop();
                    if (desktop != null)
                    {
                        IconMetrics metrics = GetIconMetrics();
                        bool autoArrange = IsAutoArrangeIcons();
                        Point placed;
                        if (autoArrange)
                        {
                            placed = IconLayout.PlaceIcon(desktop, icon, LayoutArea, drop, metrics);
                        }
                        else
                        {
                            placed = ClampIconPosition(drop, LayoutArea, metrics);
                        }
                        icon.X = placed.X;
                        icon.Y = placed.Y;
                        Canvas.SetLeft(view, placed.X);
                        Canvas.SetTop(view, placed.Y);
                        SelectIcon(icon.Id);
                        NotifyHostChanged();
                    }
                }
                else
                {
                    // 单击 / 双击判定
                    DateTime now = DateTime.UtcNow;
                    bool sameIcon = !string.IsNullOrEmpty(icon.Id) &&
                                    string.Equals(_lastClickIconId, icon.Id, StringComparison.Ordinal);
                    double elapsed = (now - _lastClickTime).TotalMilliseconds;
                    if (sameIcon && elapsed <= DoubleClickMilliseconds)
                    {
                        _lastClickIconId = null;
                        _lastClickTime = DateTime.MinValue;
                        SelectIcon(icon.Id);
                        LaunchIcon(icon);
                    }
                    else
                    {
                        _lastClickIconId = icon.Id;
                        _lastClickTime = now;
                        SelectIcon(icon.Id);
                    }
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 左键抬起处理失败: " + ex);
            }
        }

        private void WindowLostMouseCaptureHandler(object sender, MouseEventArgs e)
        {
            try
            {
                if (_dragView == null) return;
                // 拖动过程中意外丢失捕获：把图标放回原位，保持配置与界面一致
                DesktopLayerIconView view = _dragView;
                IconEntry icon = _dragIcon;
                _dragView = null;
                _dragIcon = null;
                _dragging = false;
                view.SetDragging(false);
                view.Cursor = null;
                if (icon != null)
                {
                    Canvas.SetLeft(view, icon.X);
                    Canvas.SetTop(view, icon.Y);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 丢失鼠标捕获处理失败: " + ex.Message);
            }
        }

        // ================= 鼠标右键：图标自绘菜单 / 空白处功能菜单 =================

        private void WindowMouseRightButtonDownHandler(object sender, MouseButtonEventArgs e)
        {
            try
            {
                CloseMenu();
                Point position = e.GetPosition(RootGrid);
                DesktopLayerIconView view = FindIconView(e.OriginalSource as DependencyObject);
                if (view != null && view.Entry != null)
                {
                    // 图标右键（1.2.4）：优先交给控制器调用 Windows 原生文件右键菜单
                    // （剪切/复制/重命名/共享/删除/属性/第三方扩展…）；
                    // 返回 false（网址、目标不存在、受限模式等）时回退到窗口内自绘菜单。
                    SelectIcon(view.Entry.Id);
                    if (!TryShowNativeIconContextMenu(view.Entry, position))
                    {
                        ShowIconMenu(view.Entry, position);
                    }
                }
                else
                {
                    // 1.2.0：空白处右键改为「功能化自绘菜单」，查看/排序/刷新/新建只作用于当前虚拟桌面；
                    // 真正的 Windows 原生菜单收进菜单末尾的「更多选项（Windows 原生菜单）…」。
                    SelectIcon(null);
                    ShowDesktopFunctionMenu(position);
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 右键菜单处理失败: " + ex);
            }
        }

        /// <summary>
        /// 图标右键优先调用控制器实现的原生文件右键菜单（1.2.4）：
        /// owner 传图层窗口句柄，屏幕坐标用 PointToScreen 转换；
        /// 返回 false 表示控制器无法弹出（网址/目标不存在/受限模式/无桌面权限），调用方回退自绘菜单。
        /// </summary>
        private bool TryShowNativeIconContextMenu(IconEntry icon, Point position)
        {
            try
            {
                if (_host == null || icon == null) return false;
                IntPtr owner = IntPtr.Zero;
                try
                {
                    owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 获取图层窗口句柄失败: " + ex.Message);
                }
                Point screen = PointToScreen(position);
                int screenX = (int)Math.Round(screen.X);
                int screenY = (int)Math.Round(screen.Y);
                return _host.ShowIconContextMenu(icon, owner, screenX, screenY);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 打开原生图标右键菜单失败: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 把空白处右键转发给 Lead 控制器（控制器再转发给真实桌面 DefView）。
        /// 使用 Win32 GetCursorPos 取屏幕物理像素坐标，避免 DPI 换算误差。
        /// 返回 false 表示当前环境无法弹出原生菜单（低完整性/沙箱），调用方应回退到内置菜单。
        /// </summary>
        private bool TryShowNativeDesktopContextMenu()
        {
            try
            {
                if (_host == null) return false;
                int screenX;
                int screenY;
                POINT cursor;
                if (GetCursorPos(out cursor))
                {
                    screenX = cursor.X;
                    screenY = cursor.Y;
                }
                else
                {
                    // 兜底：用窗口内坐标换算回屏幕物理像素
                    Point position = Mouse.GetPosition(RootGrid);
                    double dpi = GetDpiScale();
                    screenX = (int)Math.Round((SystemParameters.VirtualScreenLeft + position.X) * dpi);
                    screenY = (int)Math.Round((SystemParameters.VirtualScreenTop + position.Y) * dpi);
                }
                return _host.ShowDesktopContextMenu(screenX, screenY);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 转发桌面右键菜单失败: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 桌面空白处的功能化自绘菜单（1.2.0）：
        /// 查看 ▸（图标大小/自动排列/显示桌面名称）、排序方式 ▸、刷新、新建 ▸、
        /// 桌面列表、切换上一/下一桌面、打开设置、恢复所有窗口、
        /// 末尾「更多选项（Windows 原生菜单）…」作为第三方扩展入口。
        /// 这些操作全部只作用于当前虚拟桌面，不会修改真实系统桌面。
        /// </summary>
        private void ShowDesktopFunctionMenu(Point position)
        {
            try
            {
                if (_menu == null) return;
                AppConfig config = GetConfig();
                AppOptions options = config == null ? null : config.Options;
                bool autoArrange = options == null || options.AutoArrangeIcons;
                bool showDesktopName = options == null || options.ShowDesktopName;
                int currentSize = GetIconMetrics().IconSize;

                _menu.Begin();

                // 1.2.4：最上方「回到原桌面 / 返回多桌面」开关（不关闭程序即可看到真实桌面）
                bool realDesktopActive = IsRealDesktopActiveSafe();
                _menu.AddItem(realDesktopActive ? "返回多桌面" : "回到原桌面（真实桌面）",
                    delegate { ToggleRealDesktopSafe(); }, false, true);
                _menu.AddSeparator();

                // 查看 ▸
                _menu.AddSubmenu("查看", delegate(DesktopLayerMenu sub)
                {
                    sub.AddHeader("图标大小");
                    sub.AddItem("小图标 (32)", delegate { SetOptionIconSize(32); }, currentSize == 32, true);
                    sub.AddItem("中图标 (48)", delegate { SetOptionIconSize(48); }, currentSize == 48, true);
                    sub.AddItem("大图标 (64)", delegate { SetOptionIconSize(64); }, currentSize == 64, true);
                    sub.AddSeparator();
                    sub.AddItem("自动排列图标", delegate { ToggleAutoArrangeIcons(); }, autoArrange, true);
                    sub.AddItem("显示桌面名称", delegate { ToggleShowDesktopName(); }, showDesktopName, true);
                });

                // 排序方式 ▸
                _menu.AddSubmenu("排序方式", delegate(DesktopLayerMenu sub)
                {
                    sub.AddItem("名称", delegate { SortCurrentDesktopIcons(0); }, false, true);
                    sub.AddItem("类型", delegate { SortCurrentDesktopIcons(1); }, false, true);
                    sub.AddItem("大小", delegate { SortCurrentDesktopIcons(2); }, false, true);
                    sub.AddItem("修改日期", delegate { SortCurrentDesktopIcons(3); }, false, true);
                });

                _menu.AddItem("刷新", delegate { RefreshSurface(); }, false, true);

                // 新建 ▸（创建到数据目录 DesktopItems，只加入当前虚拟桌面）
                _menu.AddSubmenu("新建", delegate(DesktopLayerMenu sub)
                {
                    sub.AddItem("文件夹", delegate { CreateNewDesktopItem(true); }, false, true);
                    sub.AddItem("文本文档", delegate { CreateNewDesktopItem(false); }, false, true);
                });

                _menu.AddSeparator();

                // 桌面列表
                if (config != null && config.Desktops != null && config.Desktops.Count > 0)
                {
                    _menu.AddHeader("桌面列表");
                    int currentIndex = -1;
                    try
                    {
                        if (_host != null) currentIndex = _host.CurrentDesktopIndex;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[DesktopLayer] 读取当前桌面序号失败: " + ex.Message);
                    }
                    for (int i = 0; i < config.Desktops.Count; i++)
                    {
                        DesktopProfile desktop = config.Desktops[i];
                        if (desktop == null) continue;
                        string desktopId = desktop.Id;
                        string desktopName = string.IsNullOrEmpty(desktop.Name) ? "桌面" : desktop.Name;
                        string label = (i + 1) + "  " + desktopName;
                        bool isCurrent = (i == currentIndex);
                        _menu.AddItem(label, delegate
                        {
                            if (_host != null) _host.RequestSwitch(desktopId);
                        }, isCurrent, true);
                    }
                }

                _menu.AddSeparator();
                _menu.AddItem("切换到上一桌面", delegate
                {
                    if (_host != null) _host.RequestPrevious();
                }, false, true);
                _menu.AddItem("切换到下一桌面", delegate
                {
                    if (_host != null) _host.RequestNext();
                }, false, true);
                _menu.AddSeparator();
                _menu.AddItem("打开设置", delegate
                {
                    if (_host != null) _host.OpenSettings();
                }, false, true);
                _menu.AddItem("恢复所有窗口", delegate
                {
                    if (_host != null) _host.RestoreAllWindows();
                }, false, true);
                _menu.AddSeparator();
                _menu.AddItem("更多选项（Windows 原生菜单）…", delegate { ShowNativeDesktopMenuFromItem(); }, false, true);

                _menu.ShowAt(position);
                StartMenuWatchdog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 构建桌面功能菜单失败: " + ex);
            }
        }

        // ================= 桌面功能菜单命令实现 =================

        /// <summary>当前是否处于「原桌面模式」（host 不可用时按 false 处理）。</summary>
        private bool IsRealDesktopActiveSafe()
        {
            try
            {
                return _host != null && _host.IsRealDesktopActive;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 读取原桌面模式状态失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>请求控制器切换「原桌面模式 / 多桌面模式」（控制器会同步调用 SetRealDesktopMode）。</summary>
        private void ToggleRealDesktopSafe()
        {
            try
            {
                if (_host != null) _host.ToggleRealDesktop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 切换原桌面模式失败: " + ex);
            }
        }

        private AppOptions GetOptions()
        {
            AppConfig config = GetConfig();
            return config == null ? null : config.Options;
        }

        /// <summary>查看 ▸ 图标大小：只改当前配置，保存并刷新（由控制器/图层按新尺寸重排）。</summary>
        private void SetOptionIconSize(int size)
        {
            try
            {
                AppOptions options = GetOptions();
                if (options == null) return;
                if (size != 32 && size != 48 && size != 64) return;
                if (options.IconSize == size) return;
                options.IconSize = size;
                NotifyHostChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 设置图标大小失败: " + ex);
            }
        }

        /// <summary>查看 ▸ 自动排列图标开关。</summary>
        private void ToggleAutoArrangeIcons()
        {
            try
            {
                AppOptions options = GetOptions();
                if (options == null) return;
                options.AutoArrangeIcons = !options.AutoArrangeIcons;
                NotifyHostChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 切换自动排列失败: " + ex);
            }
        }

        /// <summary>查看 ▸ 显示桌面名称开关（控制右下角桌面名徽章）。</summary>
        private void ToggleShowDesktopName()
        {
            try
            {
                AppOptions options = GetOptions();
                if (options == null) return;
                options.ShowDesktopName = !options.ShowDesktopName;
                NotifyHostChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 切换显示桌面名称失败: " + ex);
            }
        }

        /// <summary>排序方式 ▸：0=名称 1=类型 2=大小 3=修改日期，排序后整理图标。</summary>
        private void SortCurrentDesktopIcons(int mode)
        {
            try
            {
                DesktopProfile desktop = GetCurrentDesktop();
                if (desktop == null || desktop.Icons == null || desktop.Icons.Count < 2) return;

                if (mode == 1)
                {
                    desktop.Icons.Sort(delegate(IconEntry a, IconEntry b)
                    {
                        int result = string.Compare(GetSortTypeKey(a), GetSortTypeKey(b), StringComparison.OrdinalIgnoreCase);
                        if (result != 0) return result;
                        return string.Compare(GetSortName(a), GetSortName(b), StringComparison.OrdinalIgnoreCase);
                    });
                }
                else if (mode == 2)
                {
                    desktop.Icons.Sort(delegate(IconEntry a, IconEntry b)
                    {
                        long sizeA = GetSortSize(a);
                        long sizeB = GetSortSize(b);
                        if (sizeA != sizeB) return sizeA < sizeB ? -1 : 1;
                        return string.Compare(GetSortName(a), GetSortName(b), StringComparison.OrdinalIgnoreCase);
                    });
                }
                else if (mode == 3)
                {
                    desktop.Icons.Sort(delegate(IconEntry a, IconEntry b)
                    {
                        DateTime timeA = GetSortTime(a);
                        DateTime timeB = GetSortTime(b);
                        if (timeA != timeB) return timeA < timeB ? -1 : 1;
                        return string.Compare(GetSortName(a), GetSortName(b), StringComparison.OrdinalIgnoreCase);
                    });
                }
                else
                {
                    desktop.Icons.Sort(delegate(IconEntry a, IconEntry b)
                    {
                        return string.Compare(GetSortName(a), GetSortName(b), StringComparison.OrdinalIgnoreCase);
                    });
                }

                ArrangeIcons();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 排序图标失败: " + ex);
            }
        }

        private static string GetSortName(IconEntry icon)
        {
            if (icon == null) return string.Empty;
            if (!string.IsNullOrEmpty(icon.DisplayName)) return icon.DisplayName;
            return icon.TargetPath == null ? string.Empty : icon.TargetPath;
        }

        private static string GetSortTypeKey(IconEntry icon)
        {
            try
            {
                if (icon == null || string.IsNullOrEmpty(icon.TargetPath)) return string.Empty;
                if (System.IO.Directory.Exists(icon.TargetPath)) return "0";   // 文件夹排在前面
                string extension = System.IO.Path.GetExtension(icon.TargetPath);
                return extension == null ? string.Empty : extension.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static long GetSortSize(IconEntry icon)
        {
            try
            {
                if (icon == null || string.IsNullOrEmpty(icon.TargetPath)) return -2;
                if (System.IO.Directory.Exists(icon.TargetPath)) return -1;    // 文件夹排在前面
                if (!System.IO.File.Exists(icon.TargetPath)) return -2;
                return new System.IO.FileInfo(icon.TargetPath).Length;
            }
            catch
            {
                return -2;
            }
        }

        private static DateTime GetSortTime(IconEntry icon)
        {
            try
            {
                if (icon == null || string.IsNullOrEmpty(icon.TargetPath)) return DateTime.MinValue;
                if (System.IO.Directory.Exists(icon.TargetPath)) return System.IO.Directory.GetLastWriteTimeUtc(icon.TargetPath);
                if (System.IO.File.Exists(icon.TargetPath)) return System.IO.File.GetLastWriteTimeUtc(icon.TargetPath);
            }
            catch
            {
            }
            return DateTime.MinValue;
        }

        /// <summary>
        /// 新建 ▸：在 host.DataDirectory\DesktopItems\ 下创建真实文件夹/文本文档（带序号避免重名），
        /// 再作为图标加入当前虚拟桌面。不会在真实系统桌面创建任何文件，不影响原桌面。
        /// </summary>
        private void CreateNewDesktopItem(bool folder)
        {
            try
            {
                DesktopProfile desktop = GetCurrentDesktop();
                if (desktop == null) return;
                if (desktop.Icons == null) desktop.Icons = new System.Collections.Generic.List<IconEntry>();

                string root = null;
                try
                {
                    if (_host != null) root = _host.DataDirectory;
                }
                catch
                {
                }
                if (string.IsNullOrEmpty(root)) root = System.IO.Path.GetTempPath();
                string itemsDir = System.IO.Path.Combine(root, "DesktopItems");
                System.IO.Directory.CreateDirectory(itemsDir);

                string baseName = folder ? "新建文件夹" : "新建文本文档";
                string chosenName = null;
                string chosenPath = null;
                for (int i = 1; i < 1000; i++)
                {
                    string candidate = i == 1 ? baseName : baseName + " (" + i + ")";
                    string candidatePath = System.IO.Path.Combine(itemsDir, folder ? candidate : candidate + ".txt");
                    bool exists = folder
                        ? System.IO.Directory.Exists(candidatePath)
                        : System.IO.File.Exists(candidatePath);
                    if (!exists)
                    {
                        chosenName = candidate;
                        chosenPath = candidatePath;
                        break;
                    }
                }
                if (chosenPath == null) return;

                if (folder) System.IO.Directory.CreateDirectory(chosenPath);
                else System.IO.File.WriteAllText(chosenPath, string.Empty);

                IconEntry icon = Launcher.CreateFromPath(chosenPath);
                PlaceNewIcon(desktop, icon);
                desktop.Icons.Add(icon);
                NotifyHostChanged();
                ShowToast(folder ? ("已新建文件夹「" + chosenName + "」") : ("已新建文本文档「" + chosenName + "」"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 新建桌面项目失败: " + ex);
                ShowToast("新建失败：" + ex.Message);
            }
        }

        /// <summary>给新图标找一个位置：网格模式用最近空格，自由模式放在屏幕中部偏左并依次错开。</summary>
        private void PlaceNewIcon(DesktopProfile desktop, IconEntry icon)
        {
            if (desktop == null || icon == null) return;
            Rect area = LayoutArea;
            IconMetrics metrics = GetIconMetrics();
            if (IsAutoArrangeIcons())
            {
                Point desired = IconLayout.NextFreeCell(desktop, area, metrics);
                Point placed = IconLayout.PlaceIcon(desktop, icon, area, desired, metrics);
                icon.X = placed.X;
                icon.Y = placed.Y;
            }
            else
            {
                int count = desktop.Icons == null ? 0 : desktop.Icons.Count;
                Point desired = new Point(
                    area.X + area.Width * 0.42 + (count % 6) * 26.0,
                    area.Y + area.Height * 0.30 + (count % 6) * 26.0);
                Point placed = ClampIconPosition(desired, area, metrics);
                icon.X = placed.X;
                icon.Y = placed.Y;
            }
        }

        /// <summary>菜单末尾的「更多选项（Windows 原生菜单）…」：转发原生菜单，失败时给出提示。</summary>
        private void ShowNativeDesktopMenuFromItem()
        {
            try
            {
                if (TryShowNativeDesktopContextMenu()) return;
                if (IsDesktopIntegrationAvailable())
                {
                    ShowToast("未能打开 Windows 原生菜单");
                }
                else
                {
                    ShowToast("当前为受限启动模式：Windows 原生菜单不可用。可右键托盘图标 →「以完整权限重启」恢复全部功能（会弹一次 UAC）");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 打开原生桌面菜单失败: " + ex);
            }
        }

        private void ShowIconMenu(IconEntry icon, Point position)
        {
            try
            {
                if (_menu == null) return;
                IconEntry target = icon;
                bool hasTarget = !string.IsNullOrEmpty(icon.TargetPath);
                _menu.Begin();
                _menu.AddItem("打开", delegate { LaunchIcon(target); }, false, true);
                _menu.AddItem("打开文件所在位置", delegate { OpenIconLocation(target); }, false, hasTarget);
                _menu.AddSeparator();
                _menu.AddItem("从本桌面移除", delegate { RemoveIconFromDesktop(target); }, false, true);
                _menu.AddItem("取消", delegate { }, false, true);
                _menu.ShowAt(position);
                StartMenuWatchdog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 构建图标菜单失败: " + ex);
            }
        }

        private void CloseMenu()
        {
            try
            {
                if (_menu != null) _menu.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 关闭菜单失败: " + ex.Message);
            }
            try
            {
                _menuWatchTimer.Stop();
            }
            catch
            {
            }
        }

        private void MenuClosedHandler(object sender, EventArgs e)
        {
            try
            {
                _menuWatchTimer.Stop();
            }
            catch
            {
            }
        }

        private void StartMenuWatchdog()
        {
            try
            {
                _menuWatchTimer.Stop();
                _menuWatchTimer.Interval = TimeSpan.FromMilliseconds(MenuWatchMilliseconds);
                _menuWatchTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 启动菜单监视失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 图层窗口永不激活，菜单打开时点击其他程序不会收到 WPF 事件，
        /// 因此用轻量轮询（仅菜单打开期间）检测「窗口外的鼠标点击」并关闭菜单。
        /// </summary>
        private void MenuWatchTimerTick(object sender, EventArgs e)
        {
            try
            {
                if (_menu == null || !_menu.IsOpen)
                {
                    _menuWatchTimer.Stop();
                    return;
                }
                bool pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
                if (!pressed) return;
                if (_hwnd == IntPtr.Zero) return;
                POINT point;
                if (!GetCursorPos(out point)) return;
                RECT rect;
                if (!GetWindowRect(_hwnd, out rect)) return;
                bool inside = point.X >= rect.Left && point.X <= rect.Right &&
                              point.Y >= rect.Top && point.Y <= rect.Bottom;
                if (!inside) CloseMenu();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 菜单监视处理失败: " + ex.Message);
            }
        }

        // ================= 菜单命令实现 =================

        /// <summary>
        /// 启动图标：优先走 host.LaunchIcon（控制器负责窗口归属/激活，
        /// 解决「新建桌面上的图标双击打不开」的问题）；host 为空时回退到 Launcher。
        /// 返回错误信息时显示 3 秒 Toast。
        /// </summary>
        private void LaunchIcon(IconEntry icon)
        {
            if (icon == null) return;
            string error = null;
            try
            {
                if (_host != null) error = _host.LaunchIcon(icon);
                else error = Launcher.Launch(icon);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (!string.IsNullOrEmpty(error))
            {
                ShowToast("无法打开「" + SafeIconName(icon) + "」：" + error);
            }
        }

        private void OpenIconLocation(IconEntry icon)
        {
            if (icon == null) return;
            string error = null;
            try
            {
                error = Launcher.OpenFileLocation(icon.TargetPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (!string.IsNullOrEmpty(error))
            {
                ShowToast("无法打开位置：" + error);
            }
        }

        private void RemoveIconFromDesktop(IconEntry icon)
        {
            if (icon == null) return;
            DesktopProfile desktop = GetCurrentDesktop();
            if (desktop == null || desktop.Icons == null) return;
            bool removed = desktop.Icons.Remove(icon);
            if (!removed) return;
            if (!string.IsNullOrEmpty(_selectedIconId) &&
                string.Equals(_selectedIconId, icon.Id, StringComparison.Ordinal))
            {
                _selectedIconId = null;
            }
            NotifyHostChanged();
        }

        /// <summary>整理图标：按当前图标尺寸把当前桌面图标排成列优先网格（受限模式回退菜单用）。</summary>
        private void ArrangeIcons()
        {
            DesktopProfile desktop = GetCurrentDesktop();
            if (desktop == null) return;
            try
            {
                IconMetrics metrics = GetIconMetrics();
                IconLayout.ArrangeAll(desktop, LayoutArea, metrics);
                NotifyHostChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 整理图标失败: " + ex);
            }
        }

        private static string SafeIconName(IconEntry icon)
        {
            if (icon == null) return "图标";
            if (!string.IsNullOrEmpty(icon.DisplayName)) return icon.DisplayName;
            if (!string.IsNullOrEmpty(icon.TargetPath))
            {
                try
                {
                    return System.IO.Path.GetFileName(icon.TargetPath);
                }
                catch
                {
                }
            }
            return "图标";
        }

        // ================= 从资源管理器拖入文件 =================

        private void WindowDragEnterHandler(object sender, DragEventArgs e)
        {
            HandleDragOver(e);
        }

        private void WindowDragOverHandler(object sender, DragEventArgs e)
        {
            HandleDragOver(e);
        }

        private void HandleDragOver(DragEventArgs e)
        {
            try
            {
                if (IsFileDrop(e))
                {
                    e.Effects = DragDropEffects.Copy;
                    DropHighlight.Visibility = Visibility.Visible;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                    DropHighlight.Visibility = Visibility.Collapsed;
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] DragOver 处理失败: " + ex.Message);
            }
        }

        private void WindowDragLeaveHandler(object sender, DragEventArgs e)
        {
            try
            {
                DropHighlight.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] DragLeave 处理失败: " + ex.Message);
            }
        }

        private void WindowDropHandler(object sender, DragEventArgs e)
        {
            try
            {
                DropHighlight.Visibility = Visibility.Collapsed;
                if (!IsFileDrop(e)) return;
                string[] paths = null;
                try
                {
                    paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 读取拖入数据失败: " + ex.Message);
                }
                if (paths == null || paths.Length == 0) return;
                Point dropPoint = e.GetPosition(IconCanvas);
                ImportDroppedPaths(paths, dropPoint);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] Drop 处理失败: " + ex);
            }
        }

        private static bool IsFileDrop(DragEventArgs e)
        {
            try
            {
                return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            }
            catch
            {
                return false;
            }
        }

        private void ImportDroppedPaths(string[] paths, Point dropPoint)
        {
            DesktopProfile desktop = GetCurrentDesktop();
            if (desktop == null) return;
            if (desktop.Icons == null) desktop.Icons = new System.Collections.Generic.List<IconEntry>();

            Rect area = LayoutArea;
            IconMetrics metrics = GetIconMetrics();
            bool autoArrange = IsAutoArrangeIcons();
            // 以落点为图标中心：网格模式依次向下排布并在冲突时找空位；自由模式允许重叠
            double startX = dropPoint.X - metrics.CellWidth / 2.0;
            double startY = dropPoint.Y - metrics.CellHeight / 2.0;
            int added = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                bool exists = false;
                try
                {
                    exists = System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 检查拖入路径失败: " + ex.Message);
                }
                if (!exists) continue;

                try
                {
                    IconEntry icon = Launcher.CreateFromPath(path);
                    Point desired;
                    if (autoArrange)
                    {
                        desired = new Point(startX, startY + added * metrics.CellHeight);
                    }
                    else
                    {
                        desired = new Point(startX + added * 24.0, startY + added * 24.0);
                    }
                    Point placed;
                    if (autoArrange)
                    {
                        placed = IconLayout.PlaceIcon(desktop, icon, area, desired, metrics);
                    }
                    else
                    {
                        placed = ClampIconPosition(desired, area, metrics);
                    }
                    icon.X = placed.X;
                    icon.Y = placed.Y;
                    desktop.Icons.Add(icon);
                    added++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[DesktopLayer] 添加拖入图标失败: " + ex.Message);
                }
            }

            if (added > 0)
            {
                string name = string.IsNullOrEmpty(desktop.Name) ? "当前桌面" : desktop.Name;
                ShowToast("已添加 " + added + " 个图标到「" + name + "」");
                NotifyHostChanged();
            }
            else
            {
                ShowToast("拖入的内容不是有效的文件或文件夹");
            }
        }

        // ================= 命中测试辅助 =================

        /// <summary>从命中的可视元素向上找图标单元格元素（点击 Image/TextBlock 也能命中）。</summary>
        private static DesktopLayerIconView FindIconView(DependencyObject source)
        {
            try
            {
                DependencyObject current = source;
                int guard = 0;
                while (current != null && guard < 64)
                {
                    guard++;
                    DesktopLayerIconView view = current as DesktopLayerIconView;
                    if (view != null) return view;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DesktopLayer] 命中测试失败: " + ex.Message);
            }
            return null;
        }
    }
}
