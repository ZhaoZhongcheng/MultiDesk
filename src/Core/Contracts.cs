using System;

namespace MultiDesk.Core
{
    /// <summary>
    /// 由 AppController 实现，供桌面图层窗口（DesktopLayerWindow）与
    /// 设置窗口（SettingsWindow）调用。界面层只依赖该接口，不直接依赖引擎。
    /// 
    /// 约定：界面层可以直接修改 Config / DesktopProfile / IconEntry 对象，
    /// 修改完成后调用 NotifyConfigChanged() 让控制器保存配置并刷新所有界面。
    /// </summary>
    public interface IAppHost
    {
        /// <summary>当前配置对象（活引用）。</summary>
        AppConfig Config { get; }

        /// <summary>当前激活的桌面（永不返回 null，除非配置为空）。</summary>
        DesktopProfile CurrentDesktop { get; }

        /// <summary>当前桌面的序号（从 0 开始）。</summary>
        int CurrentDesktopIndex { get; }

        /// <summary>桌面总数。</summary>
        int DesktopCount { get; }

        /// <summary>是否已暂停窗口管理（暂停时所有窗口都显示）。</summary>
        bool IsPaused { get; }

        /// <summary>当前激活桌面发生变化（切换完成）。</summary>
        event EventHandler CurrentDesktopChanged;

        /// <summary>配置发生任何变化（图标、壁纸、桌面增删改名、选项）。</summary>
        event EventHandler ConfigChanged;

        /// <summary>暂停状态变化。</summary>
        event EventHandler PauseStateChanged;

        /// <summary>请求切换桌面（异步/立即均可）。</summary>
        void RequestSwitch(string desktopId);

        /// <summary>请求切换到指定序号桌面。</summary>
        void RequestSwitchIndex(int index);

        void RequestNext();

        void RequestPrevious();

        /// <summary>保存配置并刷新所有界面（界面改完 Config 后调用）。</summary>
        void SaveConfig();

        /// <summary>保存配置 + 通知刷新（等价 SaveConfig + 触发 ConfigChanged）。</summary>
        void NotifyConfigChanged();

        /// <summary>打开设置窗口（若已打开则置前）。</summary>
        void OpenSettings();

        /// <summary>暂停/恢复窗口管理。</summary>
        void SetPaused(bool paused);

        /// <summary>把所有被隐藏的窗口恢复显示（紧急恢复）。</summary>
        void RestoreAllWindows();

        /// <summary>设置开机自启（会写入注册表并在配置中记录）。</summary>
        void SetStartWithWindows(bool enabled);

        /// <summary>双击图标时调用：由控制器启动并处理窗口归属，返回错误信息（成功返回 null）。</summary>
        string LaunchIcon(IconEntry icon);

        /// <summary>在桌面空白处右键：弹出真正的 Windows 桌面右键菜单（屏幕坐标）。返回 false 表示当前环境不支持，可回退到程序自绘菜单。</summary>
        bool ShowDesktopContextMenu(int screenX, int screenY);

        /// <summary>
        /// 当前进程是否有权限操作系统桌面（隐藏真实桌面图标 / 转发原生右键菜单）。
        /// 受限启动（低完整性/沙箱/部分安全软件）时为 false，界面层应回退到内置方案。
        /// </summary>
        bool IsDesktopIntegrationAvailable { get; }

        /// <summary>当前生效的图标栅格度量（跟随 Windows 桌面或自定义大小）。</summary>
        IconMetrics GetIconMetrics();

        /// <summary>是否处于「原桌面（真实 Windows 桌面）」模式：窗口全部显示、系统桌面图标恢复、桌面图层隐藏。</summary>
        bool IsRealDesktopActive { get; }

        /// <summary>在原桌面与多桌面之间切换（不退出程序）。</summary>
        void ToggleRealDesktop();

        /// <summary>
        /// 为虚拟桌面上的图标显示 Windows 原生右键菜单（剪切/复制/重命名/共享/删除/属性/第三方扩展等）。
        /// ownerHwnd 为图层窗口句柄；返回 false 表示无法显示（如网址/目标不存在），界面层可回退自绘菜单。
        /// </summary>
        bool ShowIconContextMenu(IconEntry icon, IntPtr ownerHwnd, int screenX, int screenY);

        /// <summary>其它虚拟桌面中仍在后台运行的窗口数量（用于托盘提示"程序仍在运行"）。</summary>
        int HiddenWindowCount { get; }

        /// <summary>配置/日志所在目录。</summary>
        string DataDirectory { get; }

        /// <summary>退出程序（会恢复所有窗口并清理托盘图标）。</summary>
        void Shutdown();
    }
}
