using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDesk.Core
{
    /// <summary>
    /// 移除「来自 Internet」的锁定标记（Mark of the Web / Zone.Identifier）。
    /// Windows 对带此标记的未签名程序会反复弹出「打开文件 - 安全警告 / 无法验证发布者」，
    /// 程序每次启动时自动清掉自身及同目录启动文件的标记，避免以后每次运行都弹窗。
    /// </summary>
    public static class FileUnblocker
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string fileName);

        /// <summary>移除单个文件的 Zone.Identifier 标记（不存在时静默忽略）。</summary>
        public static bool Unblock(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return DeleteFile(path + ":Zone.Identifier");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>移除程序自身与同目录常用启动文件的 Internet 锁定标记。</summary>
        public static void UnblockSelfAndSiblings()
        {
            try
            {
                string exePath = AutoStart.ExecutablePath;
                Unblock(exePath);
                string dir = System.IO.Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
                string[] names = new string[]
                {
                    "MultiDesk.exe", "多桌面助手.lnk", "启动多桌面助手.bat",
                    "卸载多桌面助手.lnk", "卸载多桌面助手.bat",
                    "解除安全警告.bat", "使用说明.txt", "app.ico"
                };
                for (int i = 0; i < names.Length; i++)
                {
                    try { Unblock(System.IO.Path.Combine(dir, names[i])); } catch { }
                }
            }
            catch
            {
            }
        }
    }
}
