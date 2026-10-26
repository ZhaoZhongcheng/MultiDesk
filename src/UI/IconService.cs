using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiDesk.UI
{
    /// <summary>
    /// 图标提取服务（共享给桌面图层与设置窗口）。
    /// 使用 IShellItemImageFactory 获取文件/文件夹/快捷方式的真实图标，
    /// 失败时退回 SHGetFileInfo，再失败返回 null（调用方可画占位图）。
    /// 所有方法只能从 UI 线程调用；结果做了缓存。
    /// </summary>
    public static class IconService
    {
        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>获取图标；path 不存在/为空时返回 null。</summary>
        public static ImageSource GetIcon(string path, int pixelSize)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (pixelSize <= 0) pixelSize = 48;
            string key = path.ToLowerInvariant() + "|" + pixelSize;
            lock (Cache)
            {
                ImageSource cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }
            ImageSource result = LoadShellIcon(path, pixelSize);
            lock (Cache)
            {
                Cache[key] = result;
            }
            return result;
        }

        public static ImageSource GetIconForEntry(Core.IconEntry icon, int pixelSize)
        {
            if (icon == null) return null;
            ImageSource src = GetIcon(icon.TargetPath, pixelSize);
            return src;
        }

        private static ImageSource LoadShellIcon(string path, int pixelSize)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                IShellItemImageFactory factory = null;
                try
                {
                    Guid iid = typeof(IShellItemImageFactory).GUID;
                    SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                }
                catch
                {
                    factory = null;
                }
                if (factory != null)
                {
                    SIZE size = new SIZE();
                    size.cx = pixelSize;
                    size.cy = pixelSize;
                    int hr = factory.GetImage(size, SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK, out hBitmap);
                    if (hr == 0 && hBitmap != IntPtr.Zero)
                    {
                        BitmapSource bmp = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            }

            // 回退：SHGetFileInfo
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
                uint attrs = FILE_ATTRIBUTE_NORMAL;
                if (Directory.Exists(path)) attrs = FILE_ATTRIBUTE_DIRECTORY;
                IntPtr res = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                        info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    DestroyIcon(info.hIcon);
                    return bmp;
                }
                if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon);
            }
            catch
            {
            }
            return null;
        }

        // ---------------- Win32 / COM ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory shellItem);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
