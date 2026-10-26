using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MultiDesk.Core;

namespace MultiDesk.Native
{
    /// <summary>
    /// 读取真实 Windows 桌面（SysListView32）的图标栅格度量，让虚拟桌面图标
    /// 与系统原桌面在大小、间距、起始位置上一致。
    ///
    /// 做法：读取 HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop\IconSize 得到图标大小，
    /// 再通过跨进程内存（VirtualAllocEx + LVM_GETITEMPOSITION + ReadProcessMemory）
    /// 读取真实桌面图标位置，推导水平/垂直步距与原点。
    /// 读取失败（低权限、explorer 未运行等）时调用方回退到默认栅格。
    /// </summary>
    public static class WindowsDesktopMetrics
    {
        private const int LvmGetItemCount = 0x1004;
        private const int LvmGetItemPosition = 0x1010;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint PageReadWrite = 0x04;
        private const uint MemRelease = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, ref POINT buffer, UIntPtr size, out UIntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, out POINT buffer, UIntPtr size, out UIntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        /// <summary>读取 Windows 桌面图标度量；失败返回 false。</summary>
        public static bool TryGet(int fallbackIconSize, out IconMetrics metrics)
        {
            metrics = null;
            try
            {
                IntPtr listView = DesktopShell.FindDesktopListView();
                if (listView == IntPtr.Zero) return false;

                int iconSize = ReadShellIconSize(fallbackIconSize);
                uint dpi = 96;
                try { dpi = GetDpiForWindow(listView); } catch { dpi = 96; }
                if (dpi < 72 || dpi > 480) dpi = 96;
                double scale = dpi / 96.0;

                int count = (int)SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0) return false;
                int sample = Math.Min(count, 24);

                List<double> xs = new List<double>();
                List<double> ys = new List<double>();
                if (!ReadItemPositions(listView, sample, xs, ys)) return false;
                if (xs.Count < 3 && ys.Count < 3) return false;

                double cellW = SmallestPositiveStep(xs);
                double cellH = SmallestPositiveStep(ys);
                if (cellW < 16 || cellH < 16) return false;

                double originX = double.MaxValue;
                double originY = double.MaxValue;
                for (int i = 0; i < xs.Count; i++) originX = Math.Min(originX, xs[i]);
                for (int i = 0; i < ys.Count; i++) originY = Math.Min(originY, ys[i]);
                if (originX == double.MaxValue) originX = 0;
                if (originY == double.MaxValue) originY = 0;

                // 物理像素 → WPF 逻辑像素（DIP）
                metrics = new IconMetrics(
                    iconSize,
                    cellW / scale,
                    cellH / scale,
                    originX / scale,
                    originY / scale);
                return true;
            }
            catch
            {
                metrics = null;
                return false;
            }
        }

        private static bool ReadItemPositions(IntPtr listView, int sample, List<double> xs, List<double> ys)
        {
            uint processId;
            GetWindowThreadProcessId(listView, out processId);
            if (processId == 0) return false;

            IntPtr process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
                false, (int)processId);
            if (process == IntPtr.Zero) return false;

            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(16), MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                CloseHandle(process);
                return false;
            }

            try
            {
                for (int i = 0; i < sample; i++)
                {
                    POINT zero = new POINT();
                    UIntPtr written;
                    WriteProcessMemory(process, remote, ref zero, new UIntPtr(8), out written);
                    IntPtr result = SendMessage(listView, LvmGetItemPosition, new IntPtr(i), remote);
                    if (result == IntPtr.Zero) continue;
                    POINT position;
                    UIntPtr read;
                    if (!ReadProcessMemory(process, remote, out position, new UIntPtr(8), out read)) continue;
                    if (position.X < 0 || position.Y < 0) continue;
                    xs.Add(position.X);
                    ys.Add(position.Y);
                }
                return xs.Count > 0 || ys.Count > 0;
            }
            finally
            {
                VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
                CloseHandle(process);
            }
        }

        private static double SmallestPositiveStep(List<double> values)
        {
            if (values == null || values.Count < 2) return 0;
            List<double> sorted = new List<double>(values);
            sorted.Sort();
            double best = 0;
            for (int i = 1; i < sorted.Count; i++)
            {
                double diff = sorted[i] - sorted[i - 1];
                if (diff < 2) continue;
                if (best == 0 || diff < best) best = diff;
            }
            return best;
        }

        private static int ReadShellIconSize(int fallback)
        {
            int size = 0;
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags\1\Desktop",
                    "IconSize", null);
                if (value != null)
                {
                    if (value is int)
                    {
                        size = (int)value;
                    }
                    else
                    {
                        int parsed;
                        if (int.TryParse(Convert.ToString(value), out parsed)) size = parsed;
                    }
                }
            }
            catch
            {
            }
            if (size < 24 || size > 128) size = fallback;
            if (size < 24) size = 48;
            return size;
        }
    }
}
