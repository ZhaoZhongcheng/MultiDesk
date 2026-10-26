using System;
using System.Runtime.InteropServices;

namespace MultiDesk.Native
{
    /// <summary>
    /// 检测当前进程是否运行在低完整性级别（Low Integrity）。
    /// 从聊天软件/沙箱/受限启动器里启动的程序常常是 Low，此时无法操作
    /// 资源管理器（Medium）里的桌面窗口，需要回退到内置方案。
    /// </summary>
    public static class ProcessIntegrity
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const int TokenIntegrityLevel = 25;
        private const uint TokenQuery = 0x0008;
        private const int LowIntegrityRid = 0x1000;

        public static bool IsLowIntegrity()
        {
            try
            {
                IntPtr token = IntPtr.Zero;
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return false;
                try
                {
                    int length = 0;
                    GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                    if (length <= 0) return false;
                    IntPtr buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        int returned;
                        if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out returned)) return false;
                        IntPtr sid = Marshal.ReadIntPtr(buffer);
                        if (sid == IntPtr.Zero) return false;
                        int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, 0));
                        return rid == LowIntegrityRid || rid == 0x0000; // 0x1000 = Low
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
