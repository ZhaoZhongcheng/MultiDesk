using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace MultiDesk.Core
{
    /// <summary>开机自启动（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。</summary>
    public static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MultiDesk";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(ValueName);
                    return value != null && !string.IsNullOrEmpty(value.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ExecutablePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule.FileName;
                }
                catch
                {
                    return System.Reflection.Assembly.GetEntryAssembly().Location;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new InvalidOperationException("无法写入注册表启动项");
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + ExecutablePath + "\" --autostart");
                }
                else
                {
                    try { key.DeleteValue(ValueName, false); }
                    catch { }
                }
            }
        }
    }
}
