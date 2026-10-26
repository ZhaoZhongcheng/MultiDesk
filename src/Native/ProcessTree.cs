using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDesk.Native
{
    /// <summary>查询父进程与进程名（用于日志诊断程序是从资源管理器、聊天软件还是沙箱启动的）。</summary>
    public static class ProcessTree
    {
        private const int Th32csSnapProcess = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public int dwSize;
            public int cntUsage;
            public int th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID;
            public int cntThreads;
            public int th32ParentProcessID;
            public int pcPriClassBase;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>返回当前进程的父进程名称（失败返回空字符串）。</summary>
        public static string GetParentProcessName()
        {
            try
            {
                int currentId = Process.GetCurrentProcess().Id;
                IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return string.Empty;
                try
                {
                    PROCESSENTRY32 entry = new PROCESSENTRY32();
                    entry.dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32));
                    int parentId = 0;
                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            if (entry.th32ProcessID == currentId)
                            {
                                parentId = entry.th32ParentProcessID;
                                break;
                            }
                        }
                        while (Process32Next(snapshot, ref entry));
                    }
                    if (parentId <= 0) return string.Empty;
                    entry = new PROCESSENTRY32();
                    entry.dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32));
                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            if (entry.th32ProcessID == parentId)
                            {
                                return string.IsNullOrEmpty(entry.szExeFile) ? ("pid " + parentId) : entry.szExeFile;
                            }
                        }
                        while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        /// <summary>返回当前进程的父进程 PID（失败返回 0）。</summary>
        public static int GetParentProcessId()
        {
            try
            {
                int currentId = Process.GetCurrentProcess().Id;
                IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;
                try
                {
                    PROCESSENTRY32 entry = new PROCESSENTRY32();
                    entry.dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32));
                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            if (entry.th32ProcessID == currentId) return entry.th32ParentProcessID;
                        }
                        while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }
            }
            catch
            {
            }
            return 0;
        }
    }
}
