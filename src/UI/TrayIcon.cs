using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using MultiDesk.Core;
using WinForms = System.Windows.Forms;

namespace MultiDesk.UI
{
    /// <summary>
    /// 系统托盘图标与右键菜单。菜单在每次打开前动态重建，保证桌面列表/暂停状态实时。
    /// </summary>
    public class TrayIcon : IDisposable
    {
        private readonly AppController _controller;
        private WinForms.NotifyIcon _notifyIcon;
        private WinForms.ContextMenuStrip _menu;
        private bool _disposed;

        public TrayIcon(AppController controller)
        {
            _controller = controller;
            _menu = new WinForms.ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Opening += delegate { RebuildMenu(); };
            _menu.RenderMode = WinForms.ToolStripRenderMode.System;

            _notifyIcon = new WinForms.NotifyIcon();
            _notifyIcon.Text = "多桌面助手";
            _notifyIcon.Icon = LoadTrayIcon();
            _notifyIcon.Visible = true;
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.DoubleClick += delegate { SafeInvoke(delegate { _controller.OpenSettings(); }); };
            Refresh();
        }

        private static Icon LoadTrayIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(AutoStart.ExecutablePath);
                if (icon != null) return icon;
            }
            catch
            {
            }
            return SystemIcons.Application;
        }

        public void Refresh()
        {
            if (_disposed || _notifyIcon == null) return;
            try
            {
                DesktopProfile d = _controller.CurrentDesktop;
                string name = d == null ? "?" : d.Name;
                int index = _controller.CurrentDesktopIndex;
                string text = "多桌面助手 · 当前：" + name + (index >= 0 ? "（第 " + (index + 1) + " 个桌面）" : "");
                if (_controller.IsPaused) text += " · 已暂停管理";
                if (_controller.IsRealDesktopActive) text += " · 原桌面";
                else
                {
                    int background = _controller.HiddenWindowCount;
                    if (background > 0) text += " · " + background + " 个窗口后台运行";
                }
                if (text.Length > 63) text = text.Substring(0, 60) + "...";
                _notifyIcon.Text = text;
            }
            catch
            {
            }
        }

        public void ShowBalloon(string title, string message)
        {
            if (_disposed || _notifyIcon == null) return;
            try
            {
                _notifyIcon.ShowBalloonTip(3000, title, message, WinForms.ToolTipIcon.Info);
            }
            catch
            {
            }
        }

        private void RebuildMenu()
        {
            _menu.Items.Clear();
            try
            {
                DesktopProfile current = _controller.CurrentDesktop;
                string header = "多桌面助手" + (current == null ? "" : " · " + current.Name);
                WinForms.ToolStripMenuItem titleItem = new WinForms.ToolStripMenuItem(header);
                titleItem.Enabled = false;
                _menu.Items.Add(titleItem);
                _menu.Items.Add(new WinForms.ToolStripSeparator());

                WinForms.ToolStripMenuItem realDesktop = new WinForms.ToolStripMenuItem(
                    _controller.IsRealDesktopActive ? "返回多桌面" : "回到原桌面（真实桌面）");
                realDesktop.Checked = _controller.IsRealDesktopActive;
                realDesktop.Click += delegate { SafeInvoke(delegate { _controller.ToggleRealDesktop(); }); };
                _menu.Items.Add(realDesktop);
                _menu.Items.Add(new WinForms.ToolStripSeparator());

                WinForms.ToolStripMenuItem pinnedMenu = new WinForms.ToolStripMenuItem("跨桌面显示的程序");
                try
                {
                    IntPtr fgWindow = _controller.GetForegroundManagedWindow();
                    string fgProcess = fgWindow == IntPtr.Zero ? string.Empty : _controller.GetProcessNameForWindow(fgWindow);
                    bool fgPinned = !string.IsNullOrEmpty(fgProcess) && _controller.IsProcessAlwaysVisible(fgProcess);
                    WinForms.ToolStripMenuItem pinCurrent = new WinForms.ToolStripMenuItem(
                        string.IsNullOrEmpty(fgProcess)
                            ? "固定当前窗口到所有桌面（请先激活程序窗口）"
                            : (fgPinned ? "取消固定当前窗口：" + fgProcess : "固定当前窗口到所有桌面：" + fgProcess));
                    pinCurrent.Enabled = !string.IsNullOrEmpty(fgProcess);
                    pinCurrent.Click += delegate { SafeInvoke(delegate { _controller.ToggleForegroundWindowAlwaysVisible(); }); };
                    pinnedMenu.DropDownItems.Add(pinCurrent);

                    IList<string> pinned = _controller.GetAlwaysVisibleProcesses();
                    if (pinned != null && pinned.Count > 0)
                    {
                        pinnedMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
                        for (int i = 0; i < pinned.Count; i++)
                        {
                            string name = pinned[i];
                            WinForms.ToolStripMenuItem item = new WinForms.ToolStripMenuItem(name);
                            item.Checked = true;
                            item.ToolTipText = "点击取消跨桌面显示";
                            item.Click += delegate(object sender, EventArgs e)
                            {
                                WinForms.ToolStripMenuItem mi = sender as WinForms.ToolStripMenuItem;
                                if (mi != null) SafeInvoke(delegate { _controller.SetProcessAlwaysVisible((string)mi.Tag, false); });
                            };
                            item.Tag = name;
                            pinnedMenu.DropDownItems.Add(item);
                        }
                    }
                    else
                    {
                        WinForms.ToolStripMenuItem empty = new WinForms.ToolStripMenuItem("（暂无，固定后该程序的窗口会在所有桌面显示）");
                        empty.Enabled = false;
                        pinnedMenu.DropDownItems.Add(empty);
                    }
                }
                catch
                {
                }
                pinnedMenu.Visible = _controller.IsWindowIsolationEnabled;
                _menu.Items.Add(pinnedMenu);
                _menu.Items.Add(new WinForms.ToolStripSeparator());

                if (_controller.Config != null && _controller.Config.Desktops != null)
                {
                    for (int i = 0; i < _controller.Config.Desktops.Count; i++)
                    {
                        DesktopProfile d = _controller.Config.Desktops[i];
                        if (d == null) continue;
                        string text = (i + 1) + ". " + d.Name;
                        WinForms.ToolStripMenuItem item = new WinForms.ToolStripMenuItem(text);
                        item.Checked = (current != null && string.Equals(d.Id, current.Id, StringComparison.Ordinal));
                        item.Tag = d.Id;
                        item.Click += delegate(object sender, EventArgs e)
                        {
                            WinForms.ToolStripMenuItem mi = sender as WinForms.ToolStripMenuItem;
                            if (mi != null && mi.Tag is string) SafeInvoke(delegate { _controller.RequestSwitch((string)mi.Tag); });
                        };
                        _menu.Items.Add(item);
                    }
                }

                _menu.Items.Add(new WinForms.ToolStripSeparator());
                WinForms.ToolStripMenuItem prev = new WinForms.ToolStripMenuItem("上一个桌面");
                prev.ShortcutKeyDisplayString = "Ctrl+Alt+←";
                prev.Click += delegate { SafeInvoke(delegate { _controller.RequestPrevious(); }); };
                _menu.Items.Add(prev);
                WinForms.ToolStripMenuItem next = new WinForms.ToolStripMenuItem("下一个桌面");
                next.ShortcutKeyDisplayString = "Ctrl+Alt+→";
                next.Click += delegate { SafeInvoke(delegate { _controller.RequestNext(); }); };
                _menu.Items.Add(next);

                WinForms.ToolStripMenuItem isolation = new WinForms.ToolStripMenuItem("窗口隔离（切换桌面时隐藏其它桌面窗口）");
                isolation.Checked = _controller.IsWindowIsolationEnabled;
                isolation.Click += delegate { SafeInvoke(delegate { _controller.SetWindowIsolation(!_controller.IsWindowIsolationEnabled); }); };
                _menu.Items.Add(isolation);
                _menu.Items.Add(new WinForms.ToolStripSeparator());

                IntPtr fg = _controller.GetForegroundManagedWindow();
                WinForms.ToolStripMenuItem moveItem = new WinForms.ToolStripMenuItem("将当前窗口移到");
                moveItem.Enabled = fg != IntPtr.Zero;
                if (_controller.Config != null && _controller.Config.Desktops != null)
                {
                    foreach (DesktopProfile d in _controller.Config.Desktops)
                    {
                        if (d == null) continue;
                        string desktopId = d.Id;
                        WinForms.ToolStripMenuItem mi = new WinForms.ToolStripMenuItem(d.Name);
                        mi.Enabled = fg != IntPtr.Zero && (current == null || !string.Equals(desktopId, current.Id, StringComparison.Ordinal));
                        mi.Click += delegate
                        {
                            if (fg != IntPtr.Zero) SafeInvoke(delegate { _controller.MoveForegroundWindowTo(desktopId); });
                        };
                        moveItem.DropDownItems.Add(mi);
                    }
                }
                moveItem.Visible = _controller.IsWindowIsolationEnabled;
                _menu.Items.Add(moveItem);
                _menu.Items.Add(new WinForms.ToolStripSeparator());

                WinForms.ToolStripMenuItem pause = new WinForms.ToolStripMenuItem("暂停窗口管理");
                pause.Checked = _controller.IsPaused;
                pause.Visible = _controller.IsWindowIsolationEnabled;
                pause.Click += delegate { SafeInvoke(delegate { _controller.SetPaused(!_controller.IsPaused); }); };
                _menu.Items.Add(pause);

                WinForms.ToolStripMenuItem restore = new WinForms.ToolStripMenuItem("恢复所有窗口");
                restore.ShortcutKeyDisplayString = "Ctrl+Alt+Shift+R";
                restore.Click += delegate { SafeInvoke(delegate { _controller.RestoreAllWindows(); }); };
                _menu.Items.Add(restore);

                if (!_controller.IsDesktopIntegrationAvailable)
                {
                    WinForms.ToolStripMenuItem relaunch = new WinForms.ToolStripMenuItem("以完整权限重启（推荐）");
                    relaunch.Font = new Font(relaunch.Font, FontStyle.Bold);
                    relaunch.Click += delegate { SafeInvoke(delegate { _controller.RestartElevated(); }); };
                    _menu.Items.Add(relaunch);
                }

                _menu.Items.Add(new WinForms.ToolStripSeparator());
                WinForms.ToolStripMenuItem settings = new WinForms.ToolStripMenuItem("打开设置");
                settings.ShortcutKeyDisplayString = "Ctrl+Alt+S";
                settings.Font = new Font(settings.Font, FontStyle.Bold);
                settings.Click += delegate { SafeInvoke(delegate { _controller.OpenSettings(); }); };
                _menu.Items.Add(settings);

                WinForms.ToolStripMenuItem dataDir = new WinForms.ToolStripMenuItem("打开数据目录");
                dataDir.Click += delegate
                {
                    try { Process.Start("explorer.exe", "\"" + _controller.DataDirectory + "\""); }
                    catch { }
                };
                _menu.Items.Add(dataDir);

                _menu.Items.Add(new WinForms.ToolStripSeparator());
                WinForms.ToolStripMenuItem exit = new WinForms.ToolStripMenuItem("退出多桌面助手");
                exit.Click += delegate { SafeInvoke(delegate { _controller.Shutdown(); }); };
                _menu.Items.Add(exit);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TrayIcon] menu build failed: " + ex.Message);
            }
        }

        private void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TrayIcon] action failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
            catch
            {
            }
            try
            {
                if (_menu != null)
                {
                    _menu.Dispose();
                    _menu = null;
                }
            }
            catch
            {
            }
        }
    }
}
