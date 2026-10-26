using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MultiDesk.Core;

namespace MultiDesk.UI
{
    /// <summary>
    /// 壁纸解析与图像加载服务（桌面图层专用）。
    ///
    /// 职责：
    ///   * 读取 Windows 当前系统壁纸（HKCU\Control Panel\Desktop 的 WallPaper /
    ///     WallpaperStyle / TileWallpaper），映射为 WallpaperFit；
    ///   * 按 WallpaperMode（系统 / 图片 / 纯色）生成冻结的 Brush；
    ///   * 图片按「路径 + 解码宽度」缓存；切换桌面时调用 ClearCache() 释放旧图，
    ///     避免多张 4K 壁纸同时常驻内存；
    ///   * 任何读取/解码失败都退回深色渐变，绝不抛异常给调用方。
    ///
    /// 线程约定：本类使用 BitmapImage / Freezable，只能在 UI 线程调用。
    /// </summary>
    public static class WallpaperService
    {
        /// <summary>缓存上限，超过后整体清空（同一时刻只应有一张当前桌面壁纸）。</summary>
        private const int MaxCacheEntries = 3;

        /// <summary>解码宽度上限，避免超大图占用过多内存（约 4K 屏宽）。</summary>
        private const int MaxDecodeWidth = 4096;

        private const string DesktopRegistryPath = "Control Panel\\Desktop";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, BitmapImage> ImageCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        // ================= 公开 API =================

        /// <summary>根据桌面的壁纸配置生成可用于填充整个图层的画刷（已冻结）。</summary>
        public static Brush CreateBrush(DesktopProfile desktop, Rect virtualScreen, double dpiScale)
        {
            if (desktop == null) return CreateFallbackBrush();
            try
            {
                switch (desktop.WallpaperMode)
                {
                    case WallpaperMode.Image:
                        return CreateImageModeBrush(desktop, virtualScreen, dpiScale);
                    case WallpaperMode.SolidColor:
                        return CreateSolidColorBrush(desktop.BackgroundColor);
                    default:
                        return CreateSystemModeBrush(virtualScreen, dpiScale);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 生成壁纸画刷失败: " + ex);
                return CreateFallbackBrush();
            }
        }

        /// <summary>深色渐变兜底画刷（壁纸缺失 / 读取出错时使用）。</summary>
        public static Brush CreateFallbackBrush()
        {
            try
            {
                LinearGradientBrush brush = new LinearGradientBrush();
                brush.StartPoint = new Point(0.0, 0.0);
                brush.EndPoint = new Point(1.0, 1.0);
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0x2A, 0x3B), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x13, 0x17, 0x21), 0.55));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x08, 0x0A, 0x0F), 1.0));
                brush.Freeze();
                return brush;
            }
            catch
            {
                return Brushes.Black;
            }
        }

        /// <summary>释放已解码的壁纸缓存（切换桌面时调用）。</summary>
        public static void ClearCache()
        {
            try
            {
                lock (SyncRoot)
                {
                    ImageCache.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 清理缓存失败: " + ex.Message);
            }
        }

        /// <summary>解析 "#AARRGGBB" / "#RRGGBB" 颜色，失败返回 fallback。</summary>
        public static Color ParseColor(string text, Color fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return fallback;
                string value = text.Trim();
                if (value.Length == 0) return fallback;
                if (value[0] != '#') value = "#" + value;
                object converted = ColorConverter.ConvertFromString(value);
                if (converted is Color) return (Color)converted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 颜色解析失败: " + text + " / " + ex.Message);
            }
            return fallback;
        }

        /// <summary>读取系统当前壁纸文件路径；为空 / 不存在返回 null。</summary>
        public static string GetSystemWallpaperPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(DesktopRegistryPath, false))
                {
                    if (key == null) return null;
                    string path = key.GetValue("WallPaper", null) as string;
                    if (string.IsNullOrEmpty(path)) return null;
                    path = Environment.ExpandEnvironmentVariables(path);
                    path = path.Trim().Trim('"');
                    if (path.Length == 0) return null;
                    if (!SafeFileExists(path)) return null;
                    return path;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 读取系统壁纸路径失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 读取系统壁纸填充方式：
        /// WallpaperStyle 10→Fill、6→Fit、2→Stretch、0→TileWallpaper=1 则 Tile 否则 Center。
        /// </summary>
        public static WallpaperFit GetSystemWallpaperFit()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(DesktopRegistryPath, false))
                {
                    if (key == null) return WallpaperFit.Fill;
                    string style = ToRegistryString(key.GetValue("WallpaperStyle", null));
                    string tile = ToRegistryString(key.GetValue("TileWallpaper", null));
                    if (style == "10") return WallpaperFit.Fill;
                    if (style == "6") return WallpaperFit.Fit;
                    if (style == "2") return WallpaperFit.Stretch;
                    if (style == "22") return WallpaperFit.Fill; // 跨屏（Span），按填充处理
                    if (tile == "1") return WallpaperFit.Tile;
                    return WallpaperFit.Center;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 读取壁纸样式失败: " + ex.Message);
                return WallpaperFit.Fill;
            }
        }

        // ================= 各模式实现 =================

        private static Brush CreateSystemModeBrush(Rect virtualScreen, double dpiScale)
        {
            string path = GetSystemWallpaperPath();
            if (string.IsNullOrEmpty(path)) return CreateFallbackBrush();
            BitmapImage image = LoadImage(path, virtualScreen, dpiScale);
            if (image == null) return CreateFallbackBrush();
            return CreateImageBrush(image, GetSystemWallpaperFit(), dpiScale);
        }

        private static Brush CreateImageModeBrush(DesktopProfile desktop, Rect virtualScreen, double dpiScale)
        {
            string path = desktop.WallpaperPath;
            if (string.IsNullOrEmpty(path)) return CreateFallbackBrush();
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 展开壁纸路径失败: " + ex.Message);
                return CreateFallbackBrush();
            }
            if (!SafeFileExists(path)) return CreateFallbackBrush();
            BitmapImage image = LoadImage(path, virtualScreen, dpiScale);
            if (image == null) return CreateFallbackBrush();
            return CreateImageBrush(image, desktop.WallpaperFit, dpiScale);
        }

        private static Brush CreateSolidColorBrush(string colorText)
        {
            Color baseColor = ParseColor(colorText, Color.FromRgb(0x1B, 0x1F, 0x2A));
            // 轻微的斜向渐变，让纯色壁纸不至于太呆板
            Color light = ShiftColor(baseColor, 0.10);
            Color dark = ShiftColor(baseColor, -0.12);
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0.0, 0.0);
            brush.EndPoint = new Point(1.0, 1.0);
            brush.GradientStops.Add(new GradientStop(light, 0.0));
            brush.GradientStops.Add(new GradientStop(baseColor, 0.5));
            brush.GradientStops.Add(new GradientStop(dark, 1.0));
            brush.Freeze();
            return brush;
        }

        /// <summary>按 WallpaperFit 生成图片画刷。</summary>
        private static Brush CreateImageBrush(BitmapImage image, WallpaperFit fit, double dpiScale)
        {
            double scale = dpiScale > 0.01 ? dpiScale : 1.0;
            ImageBrush brush = new ImageBrush();
            brush.ImageSource = image;
            brush.Stretch = Stretch.UniformToFill;
            brush.AlignmentX = AlignmentX.Center;
            brush.AlignmentY = AlignmentY.Center;

            switch (fit)
            {
                case WallpaperFit.Fit:
                    brush.Stretch = Stretch.Uniform;
                    break;
                case WallpaperFit.Stretch:
                    brush.Stretch = Stretch.Fill;
                    break;
                case WallpaperFit.Center:
                    brush.Stretch = Stretch.None;
                    break;
                case WallpaperFit.Tile:
                    brush.Stretch = Stretch.Fill;
                    brush.TileMode = TileMode.Tile;
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    double tileWidth = image.PixelWidth > 1 ? image.PixelWidth / scale : 256.0;
                    double tileHeight = image.PixelHeight > 1 ? image.PixelHeight / scale : 256.0;
                    if (tileWidth < 16.0) tileWidth = 16.0;
                    if (tileHeight < 16.0) tileHeight = 16.0;
                    brush.Viewport = new Rect(0.0, 0.0, tileWidth, tileHeight);
                    brush.AlignmentX = AlignmentX.Left;
                    brush.AlignmentY = AlignmentY.Top;
                    break;
                default: // Fill
                    break;
            }

            brush.Freeze();
            return brush;
        }

        // ================= 图片解码与缓存 =================

        private static BitmapImage LoadImage(string path, Rect virtualScreen, double dpiScale)
        {
            if (!SafeFileExists(path)) return null;
            try
            {
                int decodeWidth = ComputeDecodeWidth(path, virtualScreen, dpiScale);
                string key = path.ToLowerInvariant() + "|" + decodeWidth.ToString(CultureInfo.InvariantCulture);
                lock (SyncRoot)
                {
                    BitmapImage cached;
                    if (ImageCache.TryGetValue(key, out cached)) return cached;
                }

                BitmapImage image = LoadImageCore(path, decodeWidth);
                if (image == null) return null;
                lock (SyncRoot)
                {
                    if (ImageCache.Count >= MaxCacheEntries) ImageCache.Clear();
                    ImageCache[key] = image;
                }
                return image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 加载壁纸失败: " + path + " / " + ex.Message);
                return null;
            }
        }

        private static BitmapImage LoadImageCore(string path, int decodeWidth)
        {
            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 壁纸解码失败: " + path + " / " + ex.Message);
                return null;
            }
        }

        /// <summary>计算解码宽度：虚拟屏幕像素宽（不超过原图宽度，也不超过上限）。</summary>
        private static int ComputeDecodeWidth(string path, Rect virtualScreen, double dpiScale)
        {
            double scale = dpiScale > 0.01 ? dpiScale : 1.0;
            double screenWidthDip = virtualScreen.Width > 1.0 ? virtualScreen.Width : 1920.0;
            double screenPixels = screenWidthDip * scale;
            int target = (int)Math.Round(screenPixels);
            if (target < 1280) target = 1280;
            if (target > MaxDecodeWidth) target = MaxDecodeWidth;
            try
            {
                int sourceWidth = ReadImagePixelWidth(path);
                if (sourceWidth > 0 && sourceWidth < target) target = sourceWidth;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 读取图片尺寸失败: " + path + " / " + ex.Message);
            }
            return target;
        }

        /// <summary>只读文件头拿到原始像素宽度（避免为了知道尺寸而整图解码）。</summary>
        private static int ReadImagePixelWidth(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.None);
                    if (decoder != null && decoder.Frames.Count > 0) return decoder.Frames[0].PixelWidth;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WallpaperService] 探测图片尺寸失败: " + path + " / " + ex.Message);
            }
            return 0;
        }

        // ================= 小工具 =================

        private static bool SafeFileExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static string ToRegistryString(object value)
        {
            if (value == null) return string.Empty;
            try
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Color ShiftColor(Color color, double amount)
        {
            return Color.FromArgb(color.A, ShiftChannel(color.R, amount), ShiftChannel(color.G, amount), ShiftChannel(color.B, amount));
        }

        private static byte ShiftChannel(byte channel, double amount)
        {
            double value = channel + 255.0 * amount;
            if (value < 0.0) value = 0.0;
            if (value > 255.0) value = 255.0;
            return (byte)Math.Round(value);
        }
    }
}
