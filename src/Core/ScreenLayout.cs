using System;
using System.Windows;

namespace MultiDesk.Core
{
    /// <summary>
    /// 屏幕布局信息（WPF 逻辑像素）。
    ///  * VirtualScreen：整块虚拟屏幕，坐标原点 (0,0)，尺寸为所有显示器合并后的大小。
    ///    桌面图层窗口的 Left/Top 设为 SystemParameters.VirtualScreenLeft/Top，
    ///    因此窗口客户区坐标与本对象中的坐标一致。
    ///  * PrimaryWorkArea：主显示器工作区（去掉任务栏），用于桌面图标排版。
    /// </summary>
    public class ScreenLayout
    {
        public Rect VirtualScreen;
        public Rect PrimaryWorkArea;

        public ScreenLayout()
        {
            VirtualScreen = new Rect(0, 0, 1920, 1080);
            PrimaryWorkArea = new Rect(0, 0, 1920, 1040);
        }

        public static ScreenLayout Capture()
        {
            ScreenLayout layout = new ScreenLayout();
            try
            {
                double vsLeft = SystemParameters.VirtualScreenLeft;
                double vsTop = SystemParameters.VirtualScreenTop;
                double vsWidth = SystemParameters.VirtualScreenWidth;
                double vsHeight = SystemParameters.VirtualScreenHeight;
                layout.VirtualScreen = new Rect(0, 0, Math.Max(1, vsWidth), Math.Max(1, vsHeight));

                Rect wa = SystemParameters.WorkArea; // 主显示器工作区（主显示器坐标系）
                // 转换到「虚拟屏幕左上角为原点」的图层坐标
                layout.PrimaryWorkArea = new Rect(wa.X - vsLeft, wa.Y - vsTop, Math.Max(1, wa.Width), Math.Max(1, wa.Height));
            }
            catch
            {
            }
            return layout;
        }

        public Rect LayoutArea
        {
            get { return PrimaryWorkArea; }
        }

        public bool EqualsTo(ScreenLayout other)
        {
            if (other == null) return false;
            return VirtualScreen == other.VirtualScreen && PrimaryWorkArea == other.PrimaryWorkArea;
        }
    }
}
