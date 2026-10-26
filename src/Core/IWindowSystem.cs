using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace MultiDesk.Core
{
    /// <summary>一个顶层窗口的只读快照。</summary>
    public class WindowInfo
    {
        public IntPtr Handle;
        public int ProcessId;
        public string ClassName;
        public string Title;
        public IntPtr Owner;
        public bool IsVisible;
        public bool IsMinimized;
        public bool IsMaximized;
        public bool IsCloaked;
        public bool HasVisibleSize;
    }

    /// <summary>被本程序管理的窗口。</summary>
    public class ManagedWindowInfo
    {
        public IntPtr Handle;
        public int ProcessId;
        public string Title;
        public string ClassName;
        public string DesktopId;
        public bool IsVisible;
        public bool HiddenByUs;
        public bool WasMinimized;
        public bool WasMaximized;
        public bool WasForeground;
        public long ProcessStartTicks;
    }

    /// <summary>
    /// Win32 窗口操作抽象层。真实实现见 Native/Win32WindowSystem.cs；
    /// 单元测试可注入假实现。
    /// </summary>
    public interface IWindowSystem
    {
        /// <summary>枚举所有顶层窗口（包括不可见的）。</summary>
        IList<WindowInfo> EnumerateWindows();

        bool IsWindow(IntPtr hwnd);
        bool IsVisible(IntPtr hwnd);
        bool IsMinimized(IntPtr hwnd);
        bool IsMaximized(IntPtr hwnd);

        IntPtr GetOwner(IntPtr hwnd);
        int GetProcessId(IntPtr hwnd);
        string GetClassName(IntPtr hwnd);
        string GetTitle(IntPtr hwnd);

        IntPtr GetForegroundWindow();
        bool SetForegroundWindow(IntPtr hwnd);

        void HideWindow(IntPtr hwnd);
        void ShowWindow(IntPtr hwnd);
        void MinimizeWindow(IntPtr hwnd);
        void MaximizeWindow(IntPtr hwnd);
    }

    /// <summary>会话日志：记录「被我们隐藏」的窗口，供崩溃后恢复（主进程与看门狗共用）。</summary>
    [DataContract]
    public class JournalWindow
    {
        [DataMember(Name = "pid", Order = 0)] public int ProcessId;
        [DataMember(Name = "hwnd", Order = 1)] public long Handle;
        [DataMember(Name = "pstart", Order = 2)] public long ProcessStartTicks;
        [DataMember(Name = "desktop", Order = 3)] public string DesktopId;
        [DataMember(Name = "min", Order = 4)] public bool WasMinimized;
        [DataMember(Name = "max", Order = 5)] public bool WasMaximized;
        [DataMember(Name = "fg", Order = 6)] public bool WasForeground;
        [DataMember(Name = "title", Order = 7)] public string Title;
    }

    [DataContract]
    public class SessionJournal
    {
        [DataMember(Name = "version", Order = 0)] public int Version;
        [DataMember(Name = "boot", Order = 1)] public long BootMarker;
        [DataMember(Name = "windows", Order = 2)] public List<JournalWindow> Windows;

        public SessionJournal()
        {
            Version = 1;
            Windows = new List<JournalWindow>();
        }

        /// <summary>本次开机的近似标记（用于避免重启后误恢复旧句柄）。</summary>
        public static long CurrentBootMarker()
        {
            int uptimeMs = Environment.TickCount;
            DateTime boot = DateTime.UtcNow.AddMilliseconds(-uptimeMs);
            return boot.Ticks / TimeSpan.TicksPerMinute;
        }

        public bool IsFromCurrentBoot()
        {
            long now = CurrentBootMarker();
            // 允许 2 分钟误差（TickCount 精度/时间调整）
            return Math.Abs(now - BootMarker) <= 2;
        }
    }

    /// <summary>会话日志存储抽象（真实实现写 %LOCALAPPDATA%\MultiDesk\session.json）。</summary>
    public interface IWindowJournal
    {
        string FilePath { get; }
        SessionJournal Load();
        void Save(SessionJournal journal);
        void Clear();
    }

    /// <summary>把会话日志里的窗口恢复显示（可被看门狗进程复用）。</summary>
    public static class JournalRestorer
    {
        /// <summary>返回成功恢复的窗口数量。</summary>
        public static int Restore(SessionJournal journal, IWindowSystem ws, Action<string> log)
        {
            if (journal == null || journal.Windows == null) return 0;
            int restored = 0;
            foreach (JournalWindow jw in journal.Windows)
            {
                if (jw == null) continue;
                IntPtr hwnd = new IntPtr(jw.Handle);
                try
                {
                    if (!ws.IsWindow(hwnd)) continue;
                    if (ws.GetProcessId(hwnd) != jw.ProcessId) continue;
                    if (!ws.IsVisible(hwnd))
                    {
                        ws.ShowWindow(hwnd);
                        if (jw.WasMaximized) ws.MaximizeWindow(hwnd);
                        else if (jw.WasMinimized) ws.MinimizeWindow(hwnd);
                        restored++;
                        if (log != null) log(string.Format("restored hwnd=0x{0:X} pid={1} title={2}", jw.Handle, jw.ProcessId, jw.Title));
                    }
                }
                catch
                {
                }
            }
            return restored;
        }
    }
}
