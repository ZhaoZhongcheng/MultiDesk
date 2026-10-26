using System;
using System.Collections.Generic;
using MultiDesk.Core;

namespace MultiDesk.Native
{
    /// <summary>基于 Win32 API 的窗口系统实现。</summary>
    public class Win32WindowSystem : IWindowSystem
    {
        public IList<WindowInfo> EnumerateWindows()
        {
            List<WindowInfo> list = new List<WindowInfo>(64);
            try
            {
                NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (!NativeMethods.IsWindow(hWnd)) return true;
                        WindowInfo w = new WindowInfo();
                        w.Handle = hWnd;

                        uint pid = 0;
                        NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                        w.ProcessId = (int)pid;

                        w.ClassName = NativeMethods.GetClassNameString(hWnd);
                        w.Title = NativeMethods.GetTitleString(hWnd);
                        w.Owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
                        w.IsVisible = NativeMethods.IsWindowVisible(hWnd);
                        try
                        {
                            w.IsMinimized = NativeMethods.IsIconic(hWnd);
                            w.IsMaximized = NativeMethods.IsZoomed(hWnd);
                        }
                        catch
                        {
                        }

                        try
                        {
                            int cloaked = 0;
                            int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
                            w.IsCloaked = (hr == 0) && (cloaked != 0);
                        }
                        catch
                        {
                            w.IsCloaked = false;
                        }

                        try
                        {
                            NativeMethods.RECT rect;
                            bool ok = NativeMethods.GetWindowRect(hWnd, out rect);
                            w.HasVisibleSize = ok && (rect.Right - rect.Left) > 0 && (rect.Bottom - rect.Top) > 0;
                        }
                        catch
                        {
                            w.HasVisibleSize = true;
                        }

                        list.Add(w);
                    }
                    catch
                    {
                        // 跳过异常窗口，继续枚举
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }
            return list;
        }

        public bool IsWindow(IntPtr hwnd)
        {
            try { return NativeMethods.IsWindow(hwnd); }
            catch { return false; }
        }

        public bool IsVisible(IntPtr hwnd)
        {
            try { return NativeMethods.IsWindowVisible(hwnd); }
            catch { return false; }
        }

        public bool IsMinimized(IntPtr hwnd)
        {
            try { return NativeMethods.IsIconic(hwnd); }
            catch { return false; }
        }

        public bool IsMaximized(IntPtr hwnd)
        {
            try { return NativeMethods.IsZoomed(hwnd); }
            catch { return false; }
        }

        public IntPtr GetOwner(IntPtr hwnd)
        {
            try { return NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER); }
            catch { return IntPtr.Zero; }
        }

        public int GetProcessId(IntPtr hwnd)
        {
            try
            {
                uint pid = 0;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                return (int)pid;
            }
            catch
            {
                return 0;
            }
        }

        public string GetClassName(IntPtr hwnd)
        {
            return NativeMethods.GetClassNameString(hwnd);
        }

        public string GetTitle(IntPtr hwnd)
        {
            return NativeMethods.GetTitleString(hwnd);
        }

        public IntPtr GetForegroundWindow()
        {
            try { return NativeMethods.GetForegroundWindow(); }
            catch { return IntPtr.Zero; }
        }

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            return NativeMethods.ForceForegroundWindow(hwnd);
        }

        public void HideWindow(IntPtr hwnd)
        {
            try { NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_HIDE); }
            catch { }
            // 让任务栏按钮与当前桌面一致（部分程序自绘任务栏/特殊 AppUserModelID 会残留按钮）
            TaskbarList.DeleteTab(hwnd);
        }

        public void ShowWindow(IntPtr hwnd)
        {
            // 不激活地恢复显示，避免批量恢复窗口时焦点闪烁；前台窗口由引擎最后单独设置。
            try
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }
            catch { }
            TaskbarList.AddTab(hwnd);
        }

        public void MinimizeWindow(IntPtr hwnd)
        {
            try { NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE); }
            catch { }
        }

        public void MaximizeWindow(IntPtr hwnd)
        {
            try { NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MAXIMIZE); }
            catch { }
        }
    }
}
