using System;
using System.Runtime.InteropServices;

namespace MultiDesk.Native
{
    /// <summary>
    /// ITaskbarList：控制窗口是否在任务栏显示。
    /// 切换虚拟桌面时，被隐藏窗口的按钮通常会自动消失；但部分程序（自绘任务栏、
    /// 特殊 AppUserModelID）会残留按钮或运行指示点。显式 DeleteTab/AddTab 可以让
    /// 任务栏中显示的程序与当前桌面保持一致。
    /// </summary>
    public static class TaskbarList
    {
        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        private static ITaskbarList _taskbar;
        private static bool _failed;

        private static ITaskbarList Ensure()
        {
            if (_taskbar != null) return _taskbar;
            if (_failed) return null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
                _taskbar = (ITaskbarList)Activator.CreateInstance(type);
                _taskbar.HrInit();
                return _taskbar;
            }
            catch
            {
                _failed = true;
                _taskbar = null;
                return null;
            }
        }

        /// <summary>把窗口从任务栏移除（切换桌面时隐藏窗口后调用）。</summary>
        public static void DeleteTab(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                ITaskbarList list = Ensure();
                if (list != null) list.DeleteTab(hwnd);
            }
            catch
            {
            }
        }

        /// <summary>把窗口重新加入任务栏（切回桌面恢复窗口后调用）。</summary>
        public static void AddTab(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                ITaskbarList list = Ensure();
                if (list != null) list.AddTab(hwnd);
            }
            catch
            {
            }
        }
    }
}
