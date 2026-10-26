using System;

namespace MultiDesk.Core
{
    /// <summary>桌面图层窗口需要实现的接口（由 AppController 调用）。</summary>
    public interface IDesktopSurface
    {
        /// <summary>重新读取 host.Config / host.CurrentDesktop 并刷新绘制。</summary>
        void RefreshSurface();

        /// <summary>
        /// 切换桌面时：在隐藏旧桌面窗口之前，先把图层内容（壁纸/图标/徽章）切到目标桌面并
        /// 强制渲染一次，避免切换过程中出现图标/壁纸闪烁。
        /// </summary>
        void PrepareDesktop(string desktopId);

        /// <summary>切换桌面时显示桌面名称提示（OSD）。</summary>
        void ShowSwitchOsd(string desktopName);

        /// <summary>进入/退出「原桌面（真实桌面）」模式：隐藏/显示桌面图层。</summary>
        void SetRealDesktopMode(bool active);

        /// <summary>退出前清理（隐藏/关闭窗口）。</summary>
        void PrepareForExit();
    }

    /// <summary>设置窗口需要实现的接口（由 AppController 调用）。</summary>
    public interface ISettingsSurface
    {
        /// <summary>刷新界面数据（配置变化后调用）。</summary>
        void RefreshSettings();

        /// <summary>窗口当前是否打开。</summary>
        bool IsOpen { get; }
    }
}
