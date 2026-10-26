using System;
using System.Collections.Generic;
using System.IO;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    /// <summary>
    /// Test data constants. Non-ASCII text is written with C# \u escapes so the test
    /// sources stay readable for every editor/code-page combination used by this project.
    /// </summary>
    public static class TestData
    {
        // "gong zuo zhuo mian" = Work desktop name
        public const string ChineseWorkDesktop = "\u5DE5\u4F5C\u684C\u9762";
        // "you xi zhuo mian" = Game desktop name
        public const string ChineseGameDesktop = "\u6E38\u620F\u684C\u9762";
        // "zhong wen tu biao" = Chinese icon name
        public const string ChineseIconName = "\u4E2D\u6587\u56FE\u6807";
        // "bi zhi / feng jing .jpg" = wallpaper path with Chinese folders
        public const string ChineseWallpaperPath = "C:\\\u58C1\u7EB8\\\u98CE\u666F.jpg";

        // Launcher error strings ("tu biao wei kong" etc.).
        public const string ErrorIconNull = "\u56FE\u6807\u4E3A\u7A7A";
        public const string ErrorTargetEmpty = "\u56FE\u6807\u76EE\u6807\u4E3A\u7A7A";
        public const string ErrorPathEmpty = "\u8DEF\u5F84\u4E3A\u7A7A";

        public const string WorkDesktopId = "desk-work";
        public const string GameDesktopId = "desk-game";
    }

    /// <summary>Filesystem helpers for tests: private temp directories, deleted afterwards.</summary>
    public static class TestFiles
    {
        private const string FallbackFolderName = "test-temp";

        /// <summary>
        /// Creates a unique writable directory. Tries the system temp directory first and
        /// falls back to a folder next to the test binary, because sandboxed/CI environments
        /// may deny writes to %TEMP% while the build output folder stays writable.
        /// </summary>
        public static string CreateTempDir(string tag)
        {
            string unique = tag + "-" + Guid.NewGuid().ToString("N");
            try
            {
                string systemDir = Path.Combine(Path.GetTempPath(), "MultiDeskTests-" + unique);
                Directory.CreateDirectory(systemDir);
                return systemDir;
            }
            catch (Exception)
            {
                // Fall through to the assembly-relative folder.
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir)) baseDir = Directory.GetCurrentDirectory();
            string fallbackRoot = Path.Combine(baseDir, FallbackFolderName);
            Directory.CreateDirectory(fallbackRoot);
            string fallbackDir = Path.Combine(fallbackRoot, unique);
            Directory.CreateDirectory(fallbackDir);
            return fallbackDir;
        }

        public static void DeleteQuietly(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Temporary leftovers must never make a test fail.
            }
            try
            {
                string parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent)
                    && string.Equals(Path.GetFileName(parent), FallbackFolderName, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(parent)
                    && Directory.GetFileSystemEntries(parent).Length == 0)
                {
                    Directory.Delete(parent);
                }
            }
            catch
            {
            }
        }

        public static void WriteAllText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }

        public static string ReadAllText(string path)
        {
            return File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
    }

    /// <summary>In-memory window used by <see cref="FakeWindowSystem"/>.</summary>
    public sealed class FakeWindow
    {
        public IntPtr Handle;
        public int ProcessId;
        public string ClassName = "FakeAppWindow";
        public string Title = "Fake window";
        public IntPtr Owner = IntPtr.Zero;
        public bool IsVisible = true;
        public bool IsMinimized;
        public bool IsMaximized;
        public bool IsCloaked;
        public bool HasVisibleSize = true;
        public bool Destroyed;
        public int HideCount;
        public int ShowCount;
        public int ForegroundCount;
    }

    /// <summary>
    /// Deterministic fake implementation of <see cref="IWindowSystem"/>.
    /// No real HWND is ever touched, so tests never disturb the user's desktop.
    /// </summary>
    public sealed class FakeWindowSystem : IWindowSystem
    {
        private readonly Dictionary<long, FakeWindow> _byHandle = new Dictionary<long, FakeWindow>();
        private readonly List<FakeWindow> _order = new List<FakeWindow>();

        public IntPtr Foreground = IntPtr.Zero;
        public int EnumerateCount;
        public readonly List<string> Operations = new List<string>();

        public FakeWindow AddWindow(long handle, int processId)
        {
            FakeWindow window = new FakeWindow();
            window.Handle = new IntPtr(handle);
            window.ProcessId = processId;
            _byHandle[handle] = window;
            _order.Add(window);
            return window;
        }

        public FakeWindow Get(long handle)
        {
            FakeWindow window;
            _byHandle.TryGetValue(handle, out window);
            return window;
        }

        public FakeWindow Get(IntPtr handle)
        {
            return Get(handle.ToInt64());
        }

        public bool RemoveWindow(long handle)
        {
            FakeWindow window = Get(handle);
            if (window == null) return false;
            window.Destroyed = true;
            _byHandle.Remove(handle);
            _order.Remove(window);
            return true;
        }

        /// <summary>Simulates the application showing one of its windows outside the engine.</summary>
        public void SimulateAppShow(long handle)
        {
            FakeWindow window = Get(handle);
            if (window == null) return;
            window.IsVisible = true;
            window.ShowCount++;
            Operations.Add("app-show:" + handle);
        }

        public IList<WindowInfo> EnumerateWindows()
        {
            EnumerateCount++;
            List<WindowInfo> list = new List<WindowInfo>();
            for (int i = 0; i < _order.Count; i++)
            {
                FakeWindow window = _order[i];
                if (window.Destroyed) continue;
                list.Add(ToWindowInfo(window));
            }
            return list;
        }

        private static WindowInfo ToWindowInfo(FakeWindow window)
        {
            WindowInfo info = new WindowInfo();
            info.Handle = window.Handle;
            info.ProcessId = window.ProcessId;
            info.ClassName = window.ClassName;
            info.Title = window.Title;
            info.Owner = window.Owner;
            info.IsVisible = window.IsVisible;
            info.IsMinimized = window.IsMinimized;
            info.IsMaximized = window.IsMaximized;
            info.IsCloaked = window.IsCloaked;
            info.HasVisibleSize = window.HasVisibleSize;
            return info;
        }

        public bool IsWindow(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window != null && !window.Destroyed;
        }

        public bool IsVisible(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window != null && window.IsVisible;
        }

        public bool IsMinimized(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window != null && window.IsMinimized;
        }

        public bool IsMaximized(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window != null && window.IsMaximized;
        }

        public IntPtr GetOwner(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window == null ? IntPtr.Zero : window.Owner;
        }

        public int GetProcessId(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window == null ? 0 : window.ProcessId;
        }

        public string GetClassName(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window == null ? string.Empty : window.ClassName;
        }

        public string GetTitle(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            return window == null ? string.Empty : window.Title;
        }

        public IntPtr GetForegroundWindow()
        {
            return Foreground;
        }

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            if (window == null || window.Destroyed) return false;
            Foreground = hwnd;
            window.ForegroundCount++;
            Operations.Add("foreground:" + hwnd.ToInt64());
            return true;
        }

        public void HideWindow(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            if (window == null) return;
            window.IsVisible = false;
            window.HideCount++;
            Operations.Add("hide:" + hwnd.ToInt64());
        }

        public void ShowWindow(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            if (window == null) return;
            window.IsVisible = true;
            window.ShowCount++;
            Operations.Add("show:" + hwnd.ToInt64());
        }

        public void MinimizeWindow(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            if (window == null) return;
            window.IsMinimized = true;
            Operations.Add("minimize:" + hwnd.ToInt64());
        }

        public void MaximizeWindow(IntPtr hwnd)
        {
            FakeWindow window = Get(hwnd);
            if (window == null) return;
            window.IsMaximized = true;
            window.IsMinimized = false;
            Operations.Add("maximize:" + hwnd.ToInt64());
        }
    }

    /// <summary>In-memory <see cref="IWindowJournal"/> implementation.</summary>
    public sealed class FakeJournal : IWindowJournal
    {
        public SessionJournal LastSaved;
        public int SaveCount;
        public int ClearCount;

        public string FilePath
        {
            get { return "<memory>"; }
        }

        public SessionJournal Load()
        {
            return LastSaved;
        }

        public void Save(SessionJournal journal)
        {
            LastSaved = journal;
            SaveCount++;
        }

        public void Clear()
        {
            LastSaved = null;
            ClearCount++;
        }
    }
}
