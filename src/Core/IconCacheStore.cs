using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MultiDesk.Core
{
    /// <summary>
    /// 桌面图标位图磁盘缓存（1.2.8）：把 Shell 图标提取结果持久化，跨启动复用。
    ///
    /// 背景：启动时逐个调用 IShellItemImageFactory 提取图标，冷机/登录风暴期单个图标
    /// 可达数百毫秒，几十个图标会占满启动关键路径。缓存后启动只需读小 PNG 解码。
    ///
    /// 设计：
    ///   * 每个图标一个 PNG 文件，文件名 = SHA1(缓存版本 + 像素尺寸 + 目标路径 + 文件时间戳/大小)。
    ///   * 目标文件（exe/lnk/文件夹）被替换、快捷方式被修改后，mtime/大小变化 → 自动 miss 并重新提取。
    ///   * 纯文件 IO，不依赖 WPF，可单元测试；写入用临时文件 + 原子移动，可被多个线程并发调用。
    ///   * 缓存位于「数据目录\icon-cache-v1」，可随数据目录整体迁移/删除（删除后自动重建）。
    /// </summary>
    public sealed class IconCacheStore
    {
        /// <summary>缓存目录名（版本号变化时自动换目录，避免读取旧格式）。</summary>
        public const string CacheDirName = "icon-cache-v1";

        /// <summary>
        /// 缓存最长保留天数：超过后视为过期并重新提取。
        /// 兜底 .lnk 指向的程序更新了图标、但快捷方式文件本身 mtime 未变的情况。
        /// </summary>
        public const int MaxAgeDays = 30;

        private readonly string _cacheDir;
        private readonly object _sync = new object();

        public IconCacheStore(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) throw new ArgumentNullException("dataDir");
            _cacheDir = Path.Combine(dataDir, CacheDirName);
        }

        /// <summary>缓存目录完整路径。</summary>
        public string CacheDirectory
        {
            get { return _cacheDir; }
        }

        /// <summary>
        /// 计算缓存 key：同一目标文件（路径 + 像素尺寸 + mtime + 大小）得到相同 key；
        /// 目标文件变化后 key 变化，旧缓存自然失效（由 Prune 清理）。
        /// </summary>
        public static string BuildKey(string targetPath, int pixelSize)
        {
            if (string.IsNullOrEmpty(targetPath)) targetPath = string.Empty;
            string raw = "v1|" + pixelSize.ToString() + "|" + targetPath.ToLowerInvariant() + "|" + DescribeStamp(targetPath);
            try
            {
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                // 理论上 SHA1 不会失败；兜底用可读字符串（仅保证不抛异常，稳定性次要）。
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                    .Replace('/', '_').Replace('+', '-').TrimEnd('=');
            }
        }

        /// <summary>读取缓存位图字节；未命中/损坏/空文件/过期返回 false。</summary>
        public bool TryRead(string targetPath, int pixelSize, out byte[] data)
        {
            data = null;
            try
            {
                string path = PathFor(BuildKey(targetPath, pixelSize));
                if (!File.Exists(path)) return false;
                // 过期兜底：.lnk 指向的程序更新后图标可能变化，但快捷方式本身 mtime 不变。
                try
                {
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays > MaxAgeDays)
                    {
                        TryDelete(path);
                        return false;
                    }
                }
                catch
                {
                }
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes == null || bytes.Length == 0)
                {
                    TryDelete(path);
                    return false;
                }
                data = bytes;
                return true;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        /// <summary>写入缓存位图字节（原子写入；失败静默忽略，不影响主流程）。</summary>
        public bool TryWrite(string targetPath, int pixelSize, byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            try
            {
                string path = PathFor(BuildKey(targetPath, pixelSize));
                lock (_sync)
                {
                    Directory.CreateDirectory(_cacheDir);
                    string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(tmp, data);
                    if (File.Exists(path)) TryDelete(path);
                    File.Move(tmp, path);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>删除全部缓存（用户点「刷新桌面图标」或图标尺寸变化时使用）。</summary>
        public void Clear()
        {
            try
            {
                lock (_sync)
                {
                    if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, true);
                }
            }
            catch
            {
            }
        }

        /// <summary>按最后写入时间清理旧缓存：文件数超过 maxFiles 时只保留最新的 keepFiles 个。</summary>
        public void Prune(int maxFiles, int keepFiles)
        {
            if (maxFiles <= 0) maxFiles = 600;
            if (keepFiles <= 0) keepFiles = 400;
            try
            {
                lock (_sync)
                {
                    if (!Directory.Exists(_cacheDir)) return;
                    string[] files = Directory.GetFiles(_cacheDir, "*.png");
                    if (files.Length <= maxFiles) return;
                    Array.Sort(files, CompareByLastWriteAsc);
                    int removeCount = files.Length - Math.Max(16, keepFiles);
                    for (int i = 0; i < removeCount && i < files.Length; i++) TryDelete(files[i]);
                }
            }
            catch
            {
            }
        }

        private static int CompareByLastWriteAsc(string a, string b)
        {
            try
            {
                return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
            }
            catch
            {
                return 0;
            }
        }

        private string PathFor(string key)
        {
            return Path.Combine(_cacheDir, key + ".png");
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static string DescribeStamp(string targetPath)
        {
            try
            {
                if (Directory.Exists(targetPath))
                {
                    DirectoryInfo dir = new DirectoryInfo(targetPath);
                    return "d:" + dir.LastWriteTimeUtc.Ticks.ToString();
                }
                if (File.Exists(targetPath))
                {
                    FileInfo file = new FileInfo(targetPath);
                    return "f:" + file.LastWriteTimeUtc.Ticks.ToString() + ":" + file.Length.ToString();
                }
            }
            catch
            {
            }
            return "missing";
        }
    }
}
