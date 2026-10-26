using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDesk.Native
{
    /// <summary>user32 / dwmapi / kernel32 的 P/Invoke 声明。</summary>
    internal static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // ------ 常量 ------
        public const uint GW_OWNER = 4;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_MAXIMIZE = 3;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        public const int DWMWA_CLOAKED = 14;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;

        public static string GetClassNameString(IntPtr hWnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                int n = GetClassName(hWnd, sb, sb.Capacity);
                if (n <= 0) return string.Empty;
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string GetTitleString(IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return string.Empty;
                StringBuilder sb = new StringBuilder(len + 1);
                int n = GetWindowText(hWnd, sb, sb.Capacity);
                if (n <= 0) return string.Empty;
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>可靠地把窗口带到前台（失败时尝试 AttachThreadInput 技巧）。</summary>
        public static bool ForceForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == hWnd) return true;
                uint fgThread = 0;
                uint curThread = GetCurrentThreadId();
                if (fg != IntPtr.Zero)
                {
                    uint pid;
                    fgThread = GetWindowThreadProcessId(fg, out pid);
                }
                bool attached = false;
                if (fgThread != 0 && fgThread != curThread)
                {
                    try { attached = AttachThreadInput(fgThread, curThread, true); }
                    catch { attached = false; }
                }
                bool ok = SetForegroundWindow(hWnd);
                if (attached)
                {
                    try { AttachThreadInput(fgThread, curThread, false); }
                    catch { }
                }
                return ok;
            }
            catch
            {
                return false;
            }
        }
    }
}
