using System;
using Microsoft.Win32;
using MultiDesk.Core;
using System.Runtime.InteropServices;

namespace MultiDesk.Native
{
    /// <summary>
    /// 让 Windows 11 任务栏托盘区的本程序图标默认显示（而不是被折叠进「隐藏的图标」浮层）。
    /// Windows 11 把通知区域图标设置保存在 HKCU\Control Panel\NotifyIconSettings 下，
    /// 每个图标一个子键，包含 ExecutablePath / IsPromoted 等值。把本程序的 IsPromoted 置 1，
    /// 再通知 Shell 刷新即可。
    /// </summary>
    public static class TrayIconPromoter
    {
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private const int ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;
        private const uint ShcnfFlush = 0x1000;

        /// <summary>尝试把本程序的托盘图标设为「在任务栏显示」。失败时静默忽略。</summary>
        public static void Promote()
        {
            try
            {
                string exePath = AutoStart.ExecutablePath;
                if (string.IsNullOrEmpty(exePath)) return;

                bool changed = false;
                using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\NotifyIconSettings", true))
                {
                    if (settings == null) return;
                    string[] names = settings.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        try
                        {
                            using (RegistryKey item = settings.OpenSubKey(names[i], true))
                            {
                                if (item == null) continue;
                                string path = item.GetValue("ExecutablePath") as string;
                                if (string.IsNullOrEmpty(path)) continue;
                                if (!string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                                object promoted = item.GetValue("IsPromoted");
                                int value = 0;
                                if (promoted is int) value = (int)promoted;
                                else if (promoted != null) int.TryParse(Convert.ToString(promoted), out value);
                                if (value != 1)
                                {
                                    item.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                    changed = true;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                if (changed)
                {
                    // 通知 Shell：通知区域设置已改变
                    SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
            }
        }
    }
}
