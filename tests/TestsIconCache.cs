using System;
using System.IO;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    /// <summary>1.2.8 图标磁盘缓存（IconCacheStore）的单元测试。</summary>
    public static class TestsIconCache
    {
        [Test("图标缓存：写入后可读取")]
        public static void WriteThenRead()
        {
            string root = TestFiles.CreateTempDir("icon-cache");
            try
            {
                string target = Path.Combine(root, "sample.lnk");
                File.WriteAllText(target, "dummy");
                IconCacheStore store = new IconCacheStore(Path.Combine(root, "data"));
                byte[] payload = new byte[] { 1, 2, 3, 4, 5 };

                Assert.True(store.TryWrite(target, 96, payload), "写入缓存应成功");
                byte[] read;
                Assert.True(store.TryRead(target, 96, out read), "读取缓存应成功");
                Assert.NotNull(read, "读取结果不应为 null");
                Assert.Equal(Convert.ToBase64String(payload), Convert.ToBase64String(read), "读出的字节应与写入一致");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }

        [Test("图标缓存：不同像素尺寸互不影响")]
        public static void MissForDifferentPixelSize()
        {
            string root = TestFiles.CreateTempDir("icon-cache");
            try
            {
                string target = Path.Combine(root, "sample.exe");
                File.WriteAllText(target, "dummy");
                IconCacheStore store = new IconCacheStore(Path.Combine(root, "data"));
                Assert.True(store.TryWrite(target, 96, new byte[] { 9, 9, 9 }), "写入缓存应成功");
                byte[] read;
                Assert.False(store.TryRead(target, 48, out read), "不同尺寸不应命中缓存");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }

        [Test("图标缓存：目标文件变化后自动失效")]
        public static void InvalidatesWhenTargetChanges()
        {
            string root = TestFiles.CreateTempDir("icon-cache");
            try
            {
                string target = Path.Combine(root, "sample.exe");
                File.WriteAllText(target, "dummy");
                IconCacheStore store = new IconCacheStore(Path.Combine(root, "data"));
                Assert.True(store.TryWrite(target, 96, new byte[] { 7, 7 }), "写入缓存应成功");

                File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(5));
                byte[] read;
                Assert.False(store.TryRead(target, 96, out read), "目标文件 mtime 变化后应失效");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }

        [Test("图标缓存：Clear 后不再命中")]
        public static void ClearRemovesCache()
        {
            string root = TestFiles.CreateTempDir("icon-cache");
            try
            {
                string target = Path.Combine(root, "sample.txt");
                File.WriteAllText(target, "dummy");
                IconCacheStore store = new IconCacheStore(Path.Combine(root, "data"));
                Assert.True(store.TryWrite(target, 96, new byte[] { 1 }), "写入缓存应成功");
                store.Clear();
                byte[] read;
                Assert.False(store.TryRead(target, 96, out read), "Clear 后不应命中");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }

        [Test("图标缓存：key 稳定且与路径大小写无关")]
        public static void BuildKeyIsStableAndCaseInsensitive()
        {
            string path = Path.Combine(Path.GetTempPath(), "MultiDeskKeyTest", "Sample.EXE");
            string a = IconCacheStore.BuildKey(path, 96);
            string b = IconCacheStore.BuildKey(path, 96);
            string c = IconCacheStore.BuildKey(path.ToUpperInvariant(), 96);
            string d = IconCacheStore.BuildKey(path, 48);
            Assert.NotNull(a, "key 不应为 null");
            Assert.Equal(a, b, "同一输入 key 应稳定");
            Assert.Equal(a, c, "Windows 路径大小写不同应得到相同 key");
            Assert.NotEqual(a, d, "不同像素尺寸 key 应不同");
        }

        [Test("图标缓存：Prune 会清理旧文件")]
        public static void PruneRemovesOldFiles()
        {
            string root = TestFiles.CreateTempDir("icon-cache");
            try
            {
                IconCacheStore store = new IconCacheStore(Path.Combine(root, "data"));
                for (int i = 0; i < 25; i++)
                {
                    string target = Path.Combine(root, "missing-" + i.ToString() + ".lnk");
                    Assert.True(store.TryWrite(target, 96, new byte[] { 1, 2, 3 }), "写入缓存应成功");
                }
                string[] before = Directory.GetFiles(store.CacheDirectory, "*.png");
                Assert.Equal(25, before.Length, "应有 25 个缓存文件");
                store.Prune(20, 10);
                string[] after = Directory.GetFiles(store.CacheDirectory, "*.png");
                Assert.True(after.Length < before.Length, "Prune 应删除部分文件");
                Assert.True(after.Length <= 20, "Prune 后文件数应不超过上限");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }

        [Test("图标缓存：超过保留天数的旧缓存视为过期")]
        public static void ExpiredEntryIsTreatedAsMiss()
        {
            string root = TestFiles.CreateTempDir("icon-cache");
            try
            {
                string target = Path.Combine(root, "sample.exe");
                File.WriteAllText(target, "dummy");
                IconCacheStore store = new IconCacheStore(Path.Combine(root, "data"));
                Assert.True(store.TryWrite(target, 96, new byte[] { 4, 5, 6 }), "写入缓存应成功");

                string[] files = Directory.GetFiles(store.CacheDirectory, "*.png");
                Assert.Equal(1, files.Length, "应有 1 个缓存文件");
                File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddDays(-(IconCacheStore.MaxAgeDays + 1)));

                byte[] read;
                Assert.False(store.TryRead(target, 96, out read), "过期缓存不应命中");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }
    }
}
