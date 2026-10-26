using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDesk.Native
{
    /// <summary>
    /// 与 Windows 桌面（Progman / SHELLDLL_DefView）交互的小工具：
    ///  * 转发右键，让桌面图层也能弹出真正的 Windows 桌面右键菜单（含第三方扩展项）；
    ///  * 隐藏/恢复系统真实桌面图标（否则会与程序自己的图层图标重复）。
    /// </summary>
    public static class DesktopShell
    {
        public const uint WM_CONTEXTMENU = 0x007B;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;

        /// <summary>找到桌面视图窗口 SHELLDLL_DefView（兼容 Progman 或 WorkerW 两种情况）。</summary>
        public static IntPtr FindDesktopDefView()
        {
            try
            {
                IntPtr progman = FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero) return defView;
                }
                IntPtr found = IntPtr.Zero;
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(256);
                        int n = GetClassName(hWnd, sb, sb.Capacity);
                        if (n > 0 && string.Equals(sb.ToString(), "WorkerW", StringComparison.Ordinal))
                        {
                            IntPtr defView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                            if (defView != IntPtr.Zero)
                            {
                                found = defView;
                                return false;
                            }
                        }
                    }
                    catch
                    {
                    }
                    return true;
                }, IntPtr.Zero);
                return found;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>找到桌面图标列表窗口 SysListView32。</summary>
        public static IntPtr FindDesktopListView()
        {
            try
            {
                IntPtr defView = FindDesktopDefView();
                if (defView == IntPtr.Zero) return IntPtr.Zero;
                return FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 在指定屏幕坐标弹出真正的 Windows 桌面右键菜单（查看/排序方式/刷新/新建/显示设置/个性化/第三方扩展）。
        /// 实现方式：把 WM_CONTEXTMENU 转发给桌面视图窗口。
        /// </summary>
        public static bool ShowDesktopContextMenu(int screenX, int screenY)
        {
            try
            {
                IntPtr defView = FindDesktopDefView();
                if (defView == IntPtr.Zero) return false;
                long packed = ((long)(screenY & 0xFFFF) << 16) | (uint)(screenX & 0xFFFF);
                return PostMessage(defView, WM_CONTEXTMENU, defView, new IntPtr(packed));
            }
            catch
            {
                return false;
            }
        }

        public static bool IsDesktopIconViewVisible()
        {
            IntPtr lv = FindDesktopListView();
            return lv != IntPtr.Zero && IsWindowVisible(lv);
        }
    }

    /// <summary>
    /// 系统真实桌面图标的隐藏/恢复管理器。
    /// 
    /// 程序运行期间隐藏系统桌面图标（避免与程序自己的桌面图层图标重复）；
    /// 正常退出时恢复；如果主进程崩溃，看门狗进程 / 下次启动会读取 marker 文件恢复。
    /// 如果用户本来就关闭了「显示桌面图标」，则不会改动也不会写 marker。
    /// </summary>
    public class DesktopIconViewManager
    {
        public const string MarkerFileName = "desktop-icons-hidden.marker";

        private readonly string _markerPath;
        private readonly Action<string> _log;
        private bool _hiddenByUs;
        private int _retryCount;

        public DesktopIconViewManager(string markerPath)
            : this(markerPath, null)
        {
        }

        public DesktopIconViewManager(string markerPath, Action<string> log)
        {
            _markerPath = markerPath;
            _log = log;
        }

        /// <summary>隐藏真实桌面图标；返回是否由本管理器隐藏（原本已隐藏则返回 false 且不写 marker）。</summary>
        public bool Hide()
        {
            if (_hiddenByUs) return true;
            try
            {
                IntPtr lv = DesktopShell.FindDesktopListView();
                if (lv == IntPtr.Zero)
                {
                    Log("desktop listview not found");
                    return false;
                }
                if (!DesktopShell.IsWindowVisible(lv)) return false; // 用户本来就隐藏了桌面图标
                bool ok = DesktopShell.ShowWindowAsync(lv, DesktopShell.SW_HIDE);
                if (!ok) DesktopShell.ShowWindow(lv, DesktopShell.SW_HIDE);
                _hiddenByUs = true;
                WriteMarker();
                Log("hide desktop icon view hwnd=0x" + lv.ToInt64().ToString("X") + " asyncOk=" + ok
                    + " visibleNow=" + DesktopShell.IsWindowVisible(lv));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>定时检查（资源管理器重启后图标视图会重新出现）。</summary>
        public void EnsureHidden()
        {
            if (!_hiddenByUs) return;
            try
            {
                IntPtr lv = DesktopShell.FindDesktopListView();
                if (lv == IntPtr.Zero) return;
                if (!DesktopShell.IsWindowVisible(lv)) return;
                _retryCount++;
                bool ok = DesktopShell.ShowWindowAsync(lv, DesktopShell.SW_HIDE);
                if (!ok) DesktopShell.ShowWindow(lv, DesktopShell.SW_HIDE);
                Log("desktop icons became visible again, re-hide attempt #" + _retryCount + " asyncOk=" + ok
                    + " visibleNow=" + DesktopShell.IsWindowVisible(lv));
            }
            catch
            {
            }
        }

        private void Log(string message)
        {
            try
            {
                if (_log != null) _log("[desktop-icons] " + message);
            }
            catch
            {
            }
        }

        /// <summary>恢复真实桌面图标（如果没有被本管理器隐藏则不动）。</summary>
        public void Restore()
        {
            if (!_hiddenByUs) return;
            try
            {
                IntPtr lv = DesktopShell.FindDesktopListView();
                if (lv != IntPtr.Zero && DesktopShell.IsWindow(lv)) DesktopShell.ShowWindowAsync(lv, DesktopShell.SW_SHOW);
            }
            catch
            {
            }
            _hiddenByUs = false;
            DeleteMarker();
        }

        /// <summary>
        /// 从 marker 文件恢复：用于程序启动时（上次崩溃）或看门狗进程。
        /// </summary>
        public static void RecoverFromMarker(string markerPath)
        {
            if (string.IsNullOrEmpty(markerPath)) return;
            try
            {
                if (!File.Exists(markerPath)) return;
                IntPtr lv = DesktopShell.FindDesktopListView();
                if (lv != IntPtr.Zero && DesktopShell.IsWindow(lv)) DesktopShell.ShowWindowAsync(lv, DesktopShell.SW_SHOW);
                File.Delete(markerPath);
            }
            catch
            {
            }
        }

        private void WriteMarker()
        {
            try
            {
                if (string.IsNullOrEmpty(_markerPath)) return;
                string dir = Path.GetDirectoryName(_markerPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_markerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch
            {
            }
        }

        private void DeleteMarker()
        {
            try
            {
                if (!string.IsNullOrEmpty(_markerPath) && File.Exists(_markerPath)) File.Delete(_markerPath);
            }
            catch
            {
            }
        }
    }
}
