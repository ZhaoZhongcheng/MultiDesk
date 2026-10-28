using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiDesk.Core;

namespace MultiDesk.UI
{
    /// <summary>
    /// 图标提取服务（共享给桌面图层与设置窗口）。
    ///
    /// 1.2.8 起增加「磁盘缓存 + 后台提取」：
    ///   * 内存缓存（进程内）+ 数据目录 icon-cache-v1 的 PNG 磁盘缓存：
    ///     启动时命中缓存可直接解码出图标，无需调用 Shell（冷机每个图标可省 0.1~3.6 秒）。
    ///   * 未命中时 TryGetCachedIcon 立即返回 null（调用方先显示占位图），
    ///     再由后台线程调用 ExtractIcon 提取（Shell 调用不阻塞 UI 线程）。
    ///   * 目标文件 mtime/大小变化后缓存自动失效；用户可在托盘菜单「刷新桌面图标」手动清空。
    ///
    /// 线程说明：
    ///   * GetIcon / TryGetCachedIcon 返回的 BitmapSource 均已 Freeze，可跨线程使用；
    ///     其中 GetIcon 会同步调用 Shell，只应在 UI 线程（设置窗口）使用。
    ///   * ExtractIcon 可在后台 STA 线程调用（内部处理内存/磁盘缓存，线程安全）。
    /// </summary>
    public static class IconService
    {
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> LargestByPath =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static IconCacheStore _diskCache;

        /// <summary>数据目录确定后调用：绑定图标磁盘缓存目录并清空进程内缓存。</summary>
        public static void Configure(string dataDir)
        {
            IconCacheStore store = null;
            if (!string.IsNullOrEmpty(dataDir))
            {
                try { store = new IconCacheStore(dataDir); }
                catch { store = null; }
            }
            lock (Cache)
            {
                _diskCache = store;
                Cache.Clear();
                LargestByPath.Clear();
            }
        }

        /// <summary>当前图标磁盘缓存目录（未配置时为空字符串）。</summary>
        public static string DiskCacheDirectory
        {
            get
            {
                IconCacheStore store = _diskCache;
                return store == null ? string.Empty : store.CacheDirectory;
            }
        }

        /// <summary>清空内存 + 磁盘缓存（托盘「刷新桌面图标」）。</summary>
        public static void ClearCache()
        {
            lock (Cache)
            {
                Cache.Clear();
                LargestByPath.Clear();
            }
            IconCacheStore store = _diskCache;
            if (store != null)
            {
                try { store.Clear(); }
                catch { }
            }
        }

        /// <summary>后台清理过期图标缓存（旧版本图标、已删除目标等），不阻塞启动。</summary>
        public static void PruneDiskCacheAsync()
        {
            IconCacheStore store = _diskCache;
            if (store == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { store.Prune(600, 400); }
                catch { }
            });
        }

        /// <summary>获取图标；只查内存 + 磁盘缓存，不调用 Shell（未命中返回 null，供后台加载）。</summary>
        public static ImageSource TryGetCachedIcon(Core.IconEntry icon, int pixelSize)
        {
            if (icon == null) return null;
            return TryGetCachedIcon(icon.TargetPath, pixelSize);
        }

        /// <summary>获取图标；只查内存 + 磁盘缓存，不调用 Shell（未命中返回 null）。</summary>
        public static ImageSource TryGetCachedIcon(string targetPath, int pixelSize)
        {
            if (string.IsNullOrEmpty(targetPath)) return null;
            if (pixelSize <= 0) pixelSize = 48;
            string key = MemoryKey(targetPath, pixelSize);
            lock (Cache)
            {
                ImageSource cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }
            IconCacheStore store = _diskCache;
            if (store != null)
            {
                byte[] data;
                if (store.TryRead(targetPath, pixelSize, out data))
                {
                    ImageSource decoded = DecodePng(data);
                    if (decoded != null)
                    {
                        Remember(key, targetPath, decoded);
                        return decoded;
                    }
                }
            }
            return null;
        }

        /// <summary>获取图标（同步）：内存 → 磁盘缓存 → Shell 提取；结果会写回缓存。</summary>
        public static ImageSource GetIcon(string path, int pixelSize)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (pixelSize <= 0) pixelSize = 48;
            string key = MemoryKey(path, pixelSize);
            lock (Cache)
            {
                ImageSource cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }

            IconCacheStore store = _diskCache;
            if (store != null)
            {
                byte[] data;
                if (store.TryRead(path, pixelSize, out data))
                {
                    ImageSource decoded = DecodePng(data);
                    if (decoded != null)
                    {
                        Remember(key, path, decoded);
                        return decoded;
                    }
                }
            }

            // 同一目标可能以不同尺寸被请求（例如桌面图层 96px、设置窗口 32px）：
            // 已有较大位图时直接复用，避免再次调用 Shell（Image 控件会自行缩放）。
            ImageSource anySize = GetLargestCached(path);
            if (anySize != null) return anySize;

            ImageSource result = LoadShellIcon(path, pixelSize);
            if (result != null)
            {
                Remember(key, path, result);
                SaveToDisk(path, pixelSize, result);
            }
            return result;
        }

        /// <summary>
        /// 从 Shell 提取图标（可在后台 STA 线程调用）：内存/磁盘缓存未命中时才会真正调用 Shell，
        /// 成功后写回内存 + 磁盘缓存。失败返回 null（调用方保留占位图）。
        /// </summary>
        public static ImageSource ExtractIcon(string targetPath, int pixelSize)
        {
            if (string.IsNullOrEmpty(targetPath)) return null;
            if (pixelSize <= 0) pixelSize = 48;

            ImageSource cached = TryGetCachedIcon(targetPath, pixelSize);
            if (cached != null) return cached;

            long startedTicks = DateTime.UtcNow.Ticks;
            ImageSource result = LoadShellIcon(targetPath, pixelSize);
            if (result != null)
            {
                Remember(MemoryKey(targetPath, pixelSize), targetPath, result);
                SaveToDisk(targetPath, pixelSize, result);
                long elapsedMs = (DateTime.UtcNow.Ticks - startedTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs > 200)
                {
                    Core.StartupProfiler.Mark("icon_shell_slow", targetPath + " " + elapsedMs.ToString() + "ms");
                }
            }
            return result;
        }

        public static ImageSource GetIconForEntry(Core.IconEntry icon, int pixelSize)
        {
            if (icon == null) return null;
            return GetIcon(icon.TargetPath, pixelSize);
        }

        private static string MemoryKey(string path, int pixelSize)
        {
            return path.ToLowerInvariant() + "|" + pixelSize.ToString();
        }

        private static void Remember(string key, string path, ImageSource source)
        {
            if (source == null) return;
            lock (Cache)
            {
                Cache[key] = source;
                ImageSource existing;
                if (!LargestByPath.TryGetValue(path, out existing))
                {
                    LargestByPath[path] = source;
                }
                else if (PixelWidthOf(source) > PixelWidthOf(existing))
                {
                    LargestByPath[path] = source;
                }
            }
        }

        private static ImageSource GetLargestCached(string path)
        {
            lock (Cache)
            {
                ImageSource existing;
                if (LargestByPath.TryGetValue(path, out existing)) return existing;
            }
            return null;
        }

        private static int PixelWidthOf(ImageSource source)
        {
            try
            {
                BitmapSource bitmap = source as BitmapSource;
                return bitmap == null ? 0 : bitmap.PixelWidth;
            }
            catch
            {
                return 0;
            }
        }

        private static void SaveToDisk(string path, int pixelSize, ImageSource source)
        {
            IconCacheStore store = _diskCache;
            if (store == null) return;
            try
            {
                byte[] data = EncodePng(source);
                if (data != null && data.Length > 0) store.TryWrite(path, pixelSize, data);
            }
            catch
            {
            }
        }

        private static byte[] EncodePng(ImageSource source)
        {
            try
            {
                BitmapSource bitmap = source as BitmapSource;
                if (bitmap == null) return null;
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (MemoryStream ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource DecodePng(byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0) return null;
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.StreamSource = new MemoryStream(data);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
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
