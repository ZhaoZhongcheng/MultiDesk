using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace MultiDesk.Core
{
    /// <summary>壁纸来源。</summary>
    public enum WallpaperMode
    {
        System = 0,
        Image = 1,
        SolidColor = 2
    }

    /// <summary>壁纸填充方式。</summary>
    public enum WallpaperFit
    {
        Fill = 0,
        Fit = 1,
        Stretch = 2,
        Center = 3,
        Tile = 4
    }

    /// <summary>桌面上的一个图标（可以指向文件、文件夹、快捷方式或网址）。</summary>
    [DataContract]
    public class IconEntry
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id;

        [DataMember(Name = "name", Order = 1)]
        public string DisplayName;

        [DataMember(Name = "target", Order = 2)]
        public string TargetPath;

        [DataMember(Name = "args", Order = 3)]
        public string Arguments;

        [DataMember(Name = "workdir", Order = 4)]
        public string WorkingDirectory;

        /// <summary>图标左上角在「虚拟屏幕坐标系」中的 X（WPF 逻辑像素）。</summary>
        [DataMember(Name = "x", Order = 5)]
        public double X;

        /// <summary>图标左上角在「虚拟屏幕坐标系」中的 Y（WPF 逻辑像素）。</summary>
        [DataMember(Name = "y", Order = 6)]
        public double Y;

        public IconEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            DisplayName = string.Empty;
            TargetPath = string.Empty;
            Arguments = string.Empty;
            WorkingDirectory = string.Empty;
        }

        public IconEntry Clone()
        {
            IconEntry c = new IconEntry();
            c.Id = Guid.NewGuid().ToString("N");
            c.DisplayName = DisplayName;
            c.TargetPath = TargetPath;
            c.Arguments = Arguments;
            c.WorkingDirectory = WorkingDirectory;
            c.X = X;
            c.Y = Y;
            return c;
        }
    }

    /// <summary>一个虚拟桌面：独立的图标集合与壁纸。</summary>
    [DataContract]
    public class DesktopProfile
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id;

        [DataMember(Name = "name", Order = 1)]
        public string Name;

        [DataMember(Name = "wallpaperMode", Order = 2)]
        public WallpaperMode WallpaperMode;

        [DataMember(Name = "wallpaperPath", Order = 3)]
        public string WallpaperPath;

        [DataMember(Name = "wallpaperFit", Order = 4)]
        public WallpaperFit WallpaperFit;

        /// <summary>纯色壁纸颜色，形如 #FF1B1F2A。</summary>
        [DataMember(Name = "backgroundColor", Order = 5)]
        public string BackgroundColor;

        [DataMember(Name = "showBadge", Order = 6)]
        public bool ShowNameBadge;

        [DataMember(Name = "icons", Order = 7)]
        public List<IconEntry> Icons;

        public DesktopProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "新桌面";
            WallpaperMode = WallpaperMode.System;
            WallpaperPath = string.Empty;
            WallpaperFit = WallpaperFit.Fill;
            BackgroundColor = "#FF1B1F2A";
            ShowNameBadge = true;
            Icons = new List<IconEntry>();
        }

        public IconEntry FindIcon(string iconId)
        {
            if (Icons == null || string.IsNullOrEmpty(iconId)) return null;
            for (int i = 0; i < Icons.Count; i++)
            {
                if (Icons[i] != null && string.Equals(Icons[i].Id, iconId, StringComparison.Ordinal))
                {
                    return Icons[i];
                }
            }
            return null;
        }
    }

    /// <summary>全局选项。</summary>
    [DataContract]
    public class AppOptions
    {
        [DataMember(Name = "startWithWindows", Order = 0)]
        public bool StartWithWindows;

        [DataMember(Name = "showDesktopName", Order = 1)]
        public bool ShowDesktopName;

        [DataMember(Name = "firstRunDone", Order = 2)]
        public bool FirstRunDone;

        /// <summary>桌面图标大小（像素，32/48/64）。</summary>
        [DataMember(Name = "iconSize", Order = 3)]
        public int IconSize;

        /// <summary>true=网格自动对齐（默认）；false=自由摆放，可重叠。</summary>
        [DataMember(Name = "autoArrangeIcons", Order = 4)]
        public bool AutoArrangeIcons;

        /// <summary>true（默认）=图标大小/间距与 Windows 真实桌面保持一致；false=使用自定义大小。</summary>
        [DataMember(Name = "matchWindowsIconMetrics", Order = 5)]
        public bool MatchWindowsIconMetrics;

        /// <summary>true=从受限模式启动时不再弹「以完整权限重启」提示（静默使用回退模式）。</summary>
        [DataMember(Name = "skipRestrictedPrompt", Order = 6)]
        public bool SkipRestrictedPrompt;

        // 缓存上一次从真实 Windows 桌面读取到的图标度量。
        // 受限启动（低权限）时无法读取真实桌面，可用缓存保持图标大小/间距与原桌面一致。
        [DataMember(Name = "windowsIconSize", Order = 7)]
        public int WindowsIconSize;

        [DataMember(Name = "windowsCellWidth", Order = 8)]
        public double WindowsCellWidth;

        [DataMember(Name = "windowsCellHeight", Order = 9)]
        public double WindowsCellHeight;

        [DataMember(Name = "windowsOriginX", Order = 10)]
        public double WindowsOriginX;

        [DataMember(Name = "windowsOriginY", Order = 11)]
        public double WindowsOriginY;

        /// <summary>以受限模式启动时，若用户已选择「不再提示」，是否自动尝试以完整权限重启（会弹 UAC 确认框）。</summary>
        [DataMember(Name = "autoElevateWhenRestricted", Order = 12)]
        public bool AutoElevateWhenRestricted;

        /// <summary>跨桌面显示的程序（进程名，不区分大小写）：这些程序的窗口在所有虚拟桌面上都保持显示。</summary>
        [DataMember(Name = "alwaysVisibleProcesses", Order = 13)]
        public List<string> AlwaysVisibleProcesses;

        /// <summary>
        /// true=切换桌面时隐藏其它桌面的窗口（窗口隔离，旧行为）；
        /// false（默认）=切换桌面只切换桌面图标/壁纸，完全不影响任何程序窗口、任务栏、托盘状态。
        /// </summary>
        [DataMember(Name = "isolateWindows", Order = 14)]
        public bool IsolateWindows;

        public AppOptions()
        {
            ShowDesktopName = true;
            FirstRunDone = false;
            IconSize = 48;
            AutoArrangeIcons = true;
            MatchWindowsIconMetrics = true;
            SkipRestrictedPrompt = false;
            AutoElevateWhenRestricted = true;
            AlwaysVisibleProcesses = new List<string>();
            IsolateWindows = false;
        }
    }

    /// <summary>配置文件根对象。</summary>
    [DataContract]
    public class AppConfig
    {
        public const int CurrentVersion = 4;

        [DataMember(Name = "version", Order = 0)]
        public int Version;

        [DataMember(Name = "desktops", Order = 1)]
        public List<DesktopProfile> Desktops;

        [DataMember(Name = "activeDesktopId", Order = 2)]
        public string ActiveDesktopId;

        [DataMember(Name = "options", Order = 3)]
        public AppOptions Options;

        public AppConfig()
        {
            Version = CurrentVersion;
            Desktops = new List<DesktopProfile>();
            ActiveDesktopId = string.Empty;
            Options = new AppOptions();
        }

        public static AppConfig CreateDefault()
        {
            AppConfig cfg = new AppConfig();
            DesktopProfile work = new DesktopProfile();
            work.Name = "工作桌面";
            DesktopProfile game = new DesktopProfile();
            game.Name = "游戏桌面";
            cfg.Desktops.Add(work);
            cfg.Desktops.Add(game);
            cfg.ActiveDesktopId = work.Id;
            cfg.Options.FirstRunDone = false;
            return cfg;
        }

        public DesktopProfile FindDesktop(string desktopId)
        {
            if (Desktops == null || string.IsNullOrEmpty(desktopId)) return null;
            for (int i = 0; i < Desktops.Count; i++)
            {
                if (Desktops[i] != null && string.Equals(Desktops[i].Id, desktopId, StringComparison.Ordinal))
                {
                    return Desktops[i];
                }
            }
            return null;
        }

        public int IndexOfDesktop(string desktopId)
        {
            if (Desktops == null) return -1;
            for (int i = 0; i < Desktops.Count; i++)
            {
                if (Desktops[i] != null && string.Equals(Desktops[i].Id, desktopId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        public DesktopProfile GetActiveDesktop()
        {
            DesktopProfile d = FindDesktop(ActiveDesktopId);
            if (d == null && Desktops != null && Desktops.Count > 0)
            {
                d = Desktops[0];
                if (d != null) ActiveDesktopId = d.Id;
            }
            return d;
        }

        /// <summary>修复从磁盘读到的损坏/旧版本数据，保证引用不变式。</summary>
        public void Sanitize()
        {
            int loadedVersion = Version;
            if (loadedVersion <= 0) loadedVersion = CurrentVersion;
            bool migrateFromV1 = loadedVersion < 2;
            bool migrateFromV2 = loadedVersion < 3;
            bool migrateFromV3 = loadedVersion < 4;

            if (Desktops == null) Desktops = new List<DesktopProfile>();
            // 去掉 null 元素
            for (int i = Desktops.Count - 1; i >= 0; i--)
            {
                if (Desktops[i] == null) Desktops.RemoveAt(i);
            }
            if (Desktops.Count == 0)
            {
                DesktopProfile d = new DesktopProfile();
                d.Name = "桌面 1";
                Desktops.Add(d);
            }
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (DesktopProfile d in Desktops)
            {
                if (string.IsNullOrEmpty(d.Id)) d.Id = Guid.NewGuid().ToString("N");
                while (!ids.Add(d.Id))
                {
                    d.Id = Guid.NewGuid().ToString("N");
                }
                if (string.IsNullOrEmpty(d.Name)) d.Name = "桌面";
                if (d.Icons == null) d.Icons = new List<IconEntry>();
                HashSet<string> iconIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = d.Icons.Count - 1; i >= 0; i--)
                {
                    IconEntry ic = d.Icons[i];
                    if (ic == null)
                    {
                        d.Icons.RemoveAt(i);
                        continue;
                    }
                    if (string.IsNullOrEmpty(ic.Id)) ic.Id = Guid.NewGuid().ToString("N");
                    while (!iconIds.Add(ic.Id))
                    {
                        ic.Id = Guid.NewGuid().ToString("N");
                    }
                    if (ic.TargetPath == null) ic.TargetPath = string.Empty;
                    if (ic.Arguments == null) ic.Arguments = string.Empty;
                    if (ic.WorkingDirectory == null) ic.WorkingDirectory = string.Empty;
                    if (string.IsNullOrEmpty(ic.DisplayName)) ic.DisplayName = ic.TargetPath;
                    if (string.IsNullOrEmpty(ic.DisplayName)) ic.DisplayName = "图标";
                }
                if (string.IsNullOrEmpty(d.BackgroundColor)) d.BackgroundColor = "#FF1B1F2A";
                if (string.IsNullOrEmpty(d.WallpaperPath)) d.WallpaperPath = string.Empty;
            }
            if (string.IsNullOrEmpty(ActiveDesktopId) || FindDesktop(ActiveDesktopId) == null)
            {
                ActiveDesktopId = Desktops[0].Id;
            }
            if (Options == null) Options = new AppOptions();
            if (migrateFromV1)
            {
                // 1.0 配置没有这些字段，使用新默认值
                Options.IconSize = 48;
                Options.AutoArrangeIcons = true;
            }
            else
            {
                if (Options.IconSize != 32 && Options.IconSize != 48 && Options.IconSize != 64)
                {
                    Options.IconSize = 48;
                }
            }
            if (migrateFromV2)
            {
                // 1.1 及更早版本使用固定 96x104 栅格，1.2 起默认对齐真实 Windows 桌面
                Options.MatchWindowsIconMetrics = true;
            }
            if (migrateFromV3)
            {
                // 1.2.1 起：受限启动（聊天窗口/沙箱）时默认自动尝试以完整权限重启
                Options.AutoElevateWhenRestricted = true;
            }
            if (Options.AlwaysVisibleProcesses == null)
            {
                Options.AlwaysVisibleProcesses = new List<string>();
            }
            else
            {
                HashSet<string> pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = Options.AlwaysVisibleProcesses.Count - 1; i >= 0; i--)
                {
                    string name = Options.AlwaysVisibleProcesses[i];
                    if (string.IsNullOrEmpty(name) || !pinned.Add(name.Trim()))
                    {
                        Options.AlwaysVisibleProcesses.RemoveAt(i);
                    }
                }
            }
            Version = CurrentVersion;
        }
    }
}
