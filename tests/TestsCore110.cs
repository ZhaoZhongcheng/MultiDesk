using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    /// <summary>
    /// Tests for the 1.1.0 feature set: config migration (IconSize/AutoArrangeIcons),
    /// IconMetrics based layout overloads, desktop-engine launch hints and the
    /// data directory resolver.
    /// </summary>
    public static class TestsCore110
    {
        private static Rect StandardArea()
        {
            return new Rect(0, 0, 1920, 1040);
        }

        private static DesktopProfile NewDesktop()
        {
            DesktopProfile desktop = new DesktopProfile();
            desktop.Id = "desktop-110";
            desktop.Icons = new List<IconEntry>();
            return desktop;
        }

        private static IconEntry AddIcon(DesktopProfile desktop, double x, double y)
        {
            IconEntry icon = new IconEntry();
            icon.X = x;
            icon.Y = y;
            desktop.Icons.Add(icon);
            return icon;
        }

        // ================= AppOptions / config migration =================

        [Test("1.1.0 config migration: v1 config gets IconSize=48 and AutoArrangeIcons=true")]
        public static void MigrateV1Options()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = new DesktopProfile();
            desktop.Id = "d1";
            desktop.Name = "One";
            config.Desktops.Add(desktop);
            config.Version = 1;
            // A 1.0 config file has no iconSize/autoArrangeIcons members: the
            // deserialized fields keep their CLR defaults (0 / false).
            config.Options = new AppOptions();
            config.Options.IconSize = 0;
            config.Options.AutoArrangeIcons = false;
            config.Options.StartWithWindows = true;

            config.Sanitize();

            Assert.Equal(AppConfig.CurrentVersion, config.Version, "v1 config is migrated to the current version");
            Assert.Equal(48, config.Options.IconSize, "old config gets the default icon size");
            Assert.True(config.Options.AutoArrangeIcons, "old config gets the default auto-arrange setting");
            Assert.True(config.Options.MatchWindowsIconMetrics, "old config gets the match-Windows-icon-metrics default");
            Assert.False(config.Options.SkipRestrictedPrompt, "restricted-mode prompt stays enabled by default");
            Assert.True(config.Options.StartWithWindows, "unrelated old options are preserved");
        }

        [Test("1.1.0 config migration: v2 keeps AutoArrangeIcons=false and valid IconSize")]
        public static void KeepV2Options()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = new DesktopProfile();
            desktop.Id = "d1";
            desktop.Name = "One";
            config.Desktops.Add(desktop);
            config.Version = 2;
            config.Options.IconSize = 32;
            config.Options.AutoArrangeIcons = false;

            config.Sanitize();

            Assert.Equal(AppConfig.CurrentVersion, config.Version, "v2 config is migrated to the current version");
            Assert.Equal(32, config.Options.IconSize, "valid icon size 32 is preserved");
            Assert.False(config.Options.AutoArrangeIcons, "explicit free-placement setting is preserved");
            Assert.True(config.Options.MatchWindowsIconMetrics, "v2 -> v3 migration enables match-Windows-icon-metrics");

            config.Options.IconSize = 64;
            config.Sanitize();
            Assert.Equal(64, config.Options.IconSize, "valid icon size 64 is preserved");
        }

        [Test("1.1.0 config migration: invalid IconSize falls back to 48")]
        public static void InvalidIconSizeFallback()
        {
            int[] invalidValues = new int[] { 0, -5, 33, 47, 100, 1000 };
            for (int i = 0; i < invalidValues.Length; i++)
            {
                AppConfig config = new AppConfig();
                DesktopProfile desktop = new DesktopProfile();
                desktop.Id = "d1";
                desktop.Name = "One";
                config.Desktops.Add(desktop);
                config.Version = 2;
                config.Options.IconSize = invalidValues[i];
                config.Sanitize();
                Assert.Equal(48, config.Options.IconSize,
                    "icon size " + invalidValues[i] + " must fall back to 48");
            }
        }

        // ================= IconMetrics =================

        [Test("IconMetrics: CreateMetrics maps icon sizes to cells and clamps invalid input")]
        public static void MetricsCreateMetrics()
        {
            IconMetrics large = IconLayout.CreateMetrics(64);
            Assert.Equal(64, large.IconSize, "icon size 64 stored");
            Assert.Near(112, large.CellWidth, 1e-9, "64px icon cell width = 64 + 48");
            Assert.Near(120, large.CellHeight, 1e-9, "64px icon cell height = 64 + 56");
            Assert.Near(24, large.MarginX, 1e-9, "margin X stays 24");
            Assert.Near(16, large.MarginY, 1e-9, "margin Y stays 16");

            IconMetrics small = IconLayout.CreateMetrics(32);
            Assert.Equal(32, small.IconSize, "icon size 32 stored");
            Assert.Near(80, small.CellWidth, 1e-9, "32px icon cell width");
            Assert.Near(88, small.CellHeight, 1e-9, "32px icon cell height");

            IconMetrics normal = IconLayout.CreateMetrics(48);
            Assert.Near(96, normal.CellWidth, 1e-9, "48px icon matches the legacy cell width");
            Assert.Near(104, normal.CellHeight, 1e-9, "48px icon matches the legacy cell height");

            Assert.Equal(48, IconLayout.CreateMetrics(0).IconSize, "0 falls back to 48");
            Assert.Equal(48, IconLayout.CreateMetrics(-10).IconSize, "negative falls back to 48");
            Assert.Equal(24, IconLayout.CreateMetrics(10).IconSize, "too small is clamped to 24");
            Assert.Equal(96, IconLayout.CreateMetrics(100).IconSize, "too large is clamped to 96");
            Assert.Equal(96, IconLayout.CreateMetrics(1000).IconSize, "far too large is clamped to 96");
        }

        [Test("IconMetrics: Default metrics match the legacy 48px constants")]
        public static void MetricsDefault()
        {
            IconMetrics metrics = IconLayout.Default;
            Assert.NotNull(metrics, "default metrics must not be null");
            Assert.Equal(48, metrics.IconSize, "default icon size");
            Assert.Near(IconLayout.CellWidth, metrics.CellWidth, 1e-9, "default cell width");
            Assert.Near(IconLayout.CellHeight, metrics.CellHeight, 1e-9, "default cell height");
            Assert.Near(IconLayout.MarginX, metrics.MarginX, 1e-9, "default margin X");
            Assert.Near(IconLayout.MarginY, metrics.MarginY, 1e-9, "default margin Y");
        }

        [Test("IconMetrics: grid columns/rows change with the icon size")]
        public static void MetricsColumnsAndRows()
        {
            Rect area = StandardArea();
            Assert.Equal(23, IconLayout.ColumnsFor(area, IconLayout.CreateMetrics(32)), "23 columns for 32px icons");
            Assert.Equal(19, IconLayout.ColumnsFor(area, IconLayout.CreateMetrics(48)), "19 columns for 48px icons");
            Assert.Equal(16, IconLayout.ColumnsFor(area, IconLayout.CreateMetrics(64)), "16 columns for 64px icons");

            Assert.Equal(11, IconLayout.RowsFor(area, IconLayout.CreateMetrics(32)), "11 rows for 32px icons");
            Assert.Equal(9, IconLayout.RowsFor(area, IconLayout.CreateMetrics(48)), "9 rows for 48px icons");
            Assert.Equal(8, IconLayout.RowsFor(area, IconLayout.CreateMetrics(64)), "8 rows for 64px icons");
        }

        [Test("IconMetrics: CellToPoint and PointToCell use the configured cell size")]
        public static void MetricsCellConversion()
        {
            Rect area = StandardArea();
            IconMetrics large = IconLayout.CreateMetrics(64);

            Point p = IconLayout.CellToPoint(area, 2, 1, large);
            Assert.Near(24 + 2 * 112, p.X, 1e-9, "cell (2,1) X for 64px metrics");
            Assert.Near(16 + 1 * 120, p.Y, 1e-9, "cell (2,1) Y for 64px metrics");

            int col;
            int row;
            IconLayout.PointToCell(area, p, large, out col, out row);
            Assert.Equal(2, col, "cell X round-trips");
            Assert.Equal(1, row, "cell Y round-trips");

            IconLayout.PointToCell(area, new Point(-500, -500), large, out col, out row);
            Assert.Equal(0, col, "negative X clamps to 0");
            Assert.Equal(0, row, "negative Y clamps to 0");

            IconLayout.PointToCell(area, new Point(99999, 99999), large, out col, out row);
            Assert.Equal(15, col, "far right clamps to the last 64px column");
            Assert.Equal(7, row, "far bottom clamps to the last 64px row");

            // Null metrics must fall back to the defaults.
            Point fallback = IconLayout.CellToPoint(area, 0, 0, null);
            Assert.Near(24, fallback.X, 1e-9, "null metrics falls back to default margin X");
            Assert.Near(16, fallback.Y, 1e-9, "null metrics falls back to default margin Y");
        }

        [Test("IconMetrics: ArrangeAll uses the configured metrics column-major order")]
        public static void MetricsArrangeAll()
        {
            Rect area = new Rect(0, 0, 184, 192); // 32px metrics: 2 columns x 2 rows
            IconMetrics metrics = IconLayout.CreateMetrics(32);
            Assert.Equal(2, IconLayout.ColumnsFor(area, metrics), "fixture area has two columns");
            Assert.Equal(2, IconLayout.RowsFor(area, metrics), "fixture area has two rows");

            DesktopProfile desktop = NewDesktop();
            for (int i = 0; i < 4; i++) AddIcon(desktop, 9999, 9999);
            IconLayout.ArrangeAll(desktop, area, metrics);

            Assert.Near(24, desktop.Icons[0].X, 1e-9, "icon 0 column 0");
            Assert.Near(16, desktop.Icons[0].Y, 1e-9, "icon 0 row 0");
            Assert.Near(24, desktop.Icons[1].X, 1e-9, "icon 1 column 0");
            Assert.Near(104, desktop.Icons[1].Y, 1e-9, "icon 1 row 1 (cell height 88)");
            Assert.Near(104, desktop.Icons[2].X, 1e-9, "icon 2 column 1 (cell width 80)");
            Assert.Near(16, desktop.Icons[2].Y, 1e-9, "icon 2 row 0");
            Assert.Near(104, desktop.Icons[3].X, 1e-9, "icon 3 column 1");
            Assert.Near(104, desktop.Icons[3].Y, 1e-9, "icon 3 row 1");
        }

        [Test("IconMetrics: PlaceIcon avoids collisions within the configured grid")]
        public static void MetricsPlaceIconCollision()
        {
            Rect area = new Rect(0, 0, 184, 192);
            IconMetrics metrics = IconLayout.CreateMetrics(32);
            DesktopProfile desktop = NewDesktop();
            AddIcon(desktop, 24, 16);
            IconEntry second = new IconEntry();

            Point placed = IconLayout.PlaceIcon(desktop, second, area, new Point(30, 20), metrics);
            Assert.Near(24, placed.X, 1e-9, "collision moves to the next free cell X");
            Assert.Near(104, placed.Y, 1e-9, "collision moves to the next free cell Y");

            Point free = IconLayout.PlaceIcon(desktop, second, area, new Point(100, 100), metrics);
            Assert.Near(104, free.X, 1e-9, "free cell snaps to 32px metrics X");
            Assert.Near(104, free.Y, 1e-9, "free cell snaps to 32px metrics Y");
        }

        [Test("IconMetrics: Normalize repairs NaN/out-of-range icons with custom metrics")]
        public static void MetricsNormalize()
        {
            Rect area = StandardArea();
            IconMetrics metrics = IconLayout.CreateMetrics(64);
            DesktopProfile desktop = NewDesktop();
            IconEntry nan = AddIcon(desktop, double.NaN, double.NaN);
            IconEntry outOfRange = AddIcon(desktop, 5000, 5000);
            IconEntry valid = AddIcon(desktop, 24, 16);

            IconLayout.Normalize(desktop, area, metrics);

            Assert.Near(24, valid.X, 1e-9, "valid icon keeps its cell X");
            Assert.Near(16, valid.Y, 1e-9, "valid icon keeps its cell Y");

            HashSet<string> cells = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < desktop.Icons.Count; i++)
            {
                IconEntry icon = desktop.Icons[i];
                Assert.NotNaN(icon.X, "normalized X must not be NaN with custom metrics");
                Assert.NotNaN(icon.Y, "normalized Y must not be NaN with custom metrics");
                Assert.InRange(icon.X, 24, 24 + 15 * 112, "normalized X stays inside the 64px grid");
                Assert.InRange(icon.Y, 16, 16 + 7 * 120, "normalized Y stays inside the 64px grid");
                int col;
                int row;
                IconLayout.PointToCell(area, new Point(icon.X, icon.Y), metrics, out col, out row);
                cells.Add(col + "," + row);
            }
            Assert.Equal(desktop.Icons.Count, cells.Count, "icons occupy distinct cells");
        }

        [Test("IconMetrics: NextFreeCell and IsOccupied honour custom metrics")]
        public static void MetricsNextFreeCellAndOccupied()
        {
            Rect area = new Rect(0, 0, 184, 192); // 2x2 cells for 32px metrics
            IconMetrics metrics = IconLayout.CreateMetrics(32);
            DesktopProfile desktop = NewDesktop();

            Point first = IconLayout.NextFreeCell(desktop, area, metrics);
            Assert.Near(24, first.X, 1e-9, "first free cell X");
            Assert.Near(16, first.Y, 1e-9, "first free cell Y");

            AddIcon(desktop, 24, 16);
            Assert.True(IconLayout.IsOccupied(desktop, null, 0, 0, area, metrics), "cell (0,0) occupied");
            Assert.False(IconLayout.IsOccupied(desktop, null, 1, 1, area, metrics), "cell (1,1) free");

            Point next = IconLayout.NextFreeCell(desktop, area, metrics);
            Assert.Near(24, next.X, 1e-9, "next free cell stays in column 0");
            Assert.Near(104, next.Y, 1e-9, "next free cell moves to row 1");
        }

        [Test("IconMetrics: NormalizeFree preserves free coordinates and only clamps to the screen")]
        public static void MetricsNormalizeFree()
        {
            Rect area = StandardArea();
            DesktopProfile desktop = NewDesktop();
            IconEntry free = AddIcon(desktop, 123.5, 77.5);
            IconEntry nan = AddIcon(desktop, double.NaN, double.NaN);
            IconEntry far = AddIcon(desktop, 5000, 5000);
            IconEntry negative = AddIcon(desktop, -50, -50);

            IconLayout.NormalizeFree(desktop, area);

            Assert.Near(123.5, free.X, 1e-9, "free position X is preserved (no grid snapping)");
            Assert.Near(77.5, free.Y, 1e-9, "free position Y is preserved (no grid snapping)");
            Assert.Near(24, nan.X, 1e-9, "NaN X falls back to the margin");
            Assert.NotNaN(nan.Y, "NaN Y is repaired");
            Assert.Near(area.Width - 48, far.X, 1e-9, "far right coordinate is clamped");
            Assert.Near(area.Height - 48, far.Y, 1e-9, "far bottom coordinate is clamped");
            Assert.Near(0, negative.X, 1e-9, "negative X is clamped to the area origin");
            Assert.Near(0, negative.Y, 1e-9, "negative Y is clamped to the area origin");
        }

        // ================= DesktopEngine launch hint =================

        private sealed class HintFixture
        {
            public AppConfig Config;
            public FakeWindowSystem Windows;
            public FakeJournal Journal;
            public DesktopEngine Engine;
        }

        private static HintFixture CreateHintFixture()
        {
            AppConfig config = new AppConfig();
            DesktopProfile first = new DesktopProfile();
            first.Id = "desk-a";
            first.Name = "A";
            DesktopProfile second = new DesktopProfile();
            second.Id = "desk-b";
            second.Name = "B";
            config.Desktops.Add(first);
            config.Desktops.Add(second);
            config.ActiveDesktopId = first.Id;
            config.Sanitize();

            HintFixture fixture = new HintFixture();
            fixture.Config = config;
            fixture.Windows = new FakeWindowSystem();
            fixture.Journal = new FakeJournal();
            fixture.Engine = new DesktopEngine(config, fixture.Windows, fixture.Journal, -1);
            fixture.Engine.Initialize("desk-a");
            return fixture;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field, "private field " + fieldName + " must exist");
            field.SetValue(target, value);
        }

        private static ManagedWindowInfo FindManaged(DesktopEngine engine, long handle)
        {
            List<ManagedWindowInfo> windows = engine.GetManagedWindows();
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].Handle.ToInt64() == handle) return windows[i];
            }
            return null;
        }

        [Test("DesktopEngine launch hint: registered process window joins the current desktop")]
        public static void LaunchHintNewWindowJoinsCurrentDesktop()
        {
            HintFixture fixture = CreateHintFixture();
            FakeWindow first = fixture.Windows.AddWindow(0x101, 91001);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo("desk-b"), "switch to desktop B");

            // The process already owns a window on desktop A (remembered per process).
            fixture.Engine.BeginLaunchHint();
            fixture.Engine.RegisterLaunchProcess(91001);

            FakeWindow second = fixture.Windows.AddWindow(0x102, 91001);
            fixture.Engine.Tick();

            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x102);
            Assert.NotNull(info, "new window of the launched process is managed");
            Assert.Equal("desk-b", info.DesktopId, "new window is forced onto the current desktop by the launch hint");
            Assert.True(second.IsVisible, "new window stays visible on the current desktop");
            Assert.False(first.IsVisible, "old window of the same process stays hidden on desktop B");
        }

        [Test("DesktopEngine launch hint: registered process is ignored for pid<=0")]
        public static void LaunchHintInvalidPid()
        {
            HintFixture fixture = CreateHintFixture();
            fixture.Engine.RegisterLaunchProcess(0);
            fixture.Engine.RegisterLaunchProcess(-5);
            fixture.Windows.AddWindow(0x103, 91002);
            fixture.Engine.Tick();
            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "invalid launch pids do not crash the engine");
        }

        [Test("DesktopEngine launch hint: foreground window from another desktop is pulled back")]
        public static void LaunchHintPullsForegroundWindow()
        {
            HintFixture fixture = CreateHintFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x201, 91010);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo("desk-b"), "switch to desktop B");
            Assert.False(window.IsVisible, "window of desktop A is hidden after the switch");

            int movedEvents = 0;
            fixture.Engine.LaunchHintMoved += delegate { movedEvents++; };
            fixture.Engine.BeginLaunchHint();
            fixture.Windows.Foreground = window.Handle;
            fixture.Engine.Tick();

            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x201);
            Assert.Equal("desk-b", info.DesktopId, "foreground window of another desktop is moved to the current desktop");
            Assert.True(window.IsVisible, "pulled-back window is shown again");
            Assert.Equal(1, movedEvents, "LaunchHintMoved event is raised once");
        }

        [Test("DesktopEngine launch hint: expired hint does not pull foreground windows")]
        public static void LaunchHintExpiredNoPull()
        {
            HintFixture fixture = CreateHintFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x301, 91020);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo("desk-b"), "switch to desktop B");
            Assert.False(window.IsVisible, "window hidden after switch");

            fixture.Engine.BeginLaunchHint();
            // Simulate an expired hint without waiting 10 seconds.
            SetPrivateField(fixture.Engine, "_launchHintUntil", DateTime.Now.AddSeconds(-5));
            fixture.Windows.Foreground = window.Handle;
            fixture.Engine.Tick();

            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x301);
            Assert.Equal("desk-a", info.DesktopId, "expired hint leaves the window on its own desktop");
            Assert.False(window.IsVisible, "expired hint does not show foreign-desktop windows");
        }

        [Test("DesktopEngine launch hint: expired process registration does not affect ownership")]
        public static void LaunchHintExpiredRegistration()
        {
            HintFixture fixture = CreateHintFixture();
            FakeWindow first = fixture.Windows.AddWindow(0x401, 91030);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo("desk-b"), "switch to desktop B");

            fixture.Engine.RegisterLaunchProcess(91030);
            Dictionary<int, DateTime> registrations =
                (Dictionary<int, DateTime>)typeof(DesktopEngine)
                    .GetField("_launchProcesses", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(fixture.Engine);
            registrations[91030] = DateTime.Now.AddSeconds(-5);
            SetPrivateField(fixture.Engine, "_launchHintUntil", DateTime.Now.AddSeconds(-5));

            FakeWindow second = fixture.Windows.AddWindow(0x402, 91030);
            fixture.Engine.Tick();

            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x402);
            Assert.Equal("desk-a", info.DesktopId, "expired registration follows the process desktop again");
            Assert.False(second.IsVisible, "expired registration window is hidden on desktop B");
            Assert.False(first.IsVisible, "old process window stays hidden");
        }

        // ================= DataDirectoryResolver =================

        [Test("DataDirectoryResolver: CanUse validates writable directories and rejects files")]
        public static void ResolverCanUse()
        {
            string dir = TestFiles.CreateTempDir("resolver-ok");
            try
            {
                string error;
                Assert.True(DataDirectoryResolver.CanUse(dir, out error), "writable temp dir is usable");
                Assert.Equal(string.Empty, error, "no error for a writable directory");

                string filePath = Path.Combine(dir, "not-a-directory.txt");
                File.WriteAllText(filePath, "x");
                string fileError;
                Assert.False(DataDirectoryResolver.CanUse(filePath, out fileError), "a file path is not usable");
                Assert.True(fileError.Length > 0, "an error description is returned");

                string emptyError;
                Assert.False(DataDirectoryResolver.CanUse(string.Empty, out emptyError), "empty path is not usable");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("DataDirectoryResolver: explicit writable data dir is returned as-is")]
        public static void ResolverExplicitDir()
        {
            string dir = TestFiles.CreateTempDir("resolver-explicit");
            try
            {
                string report;
                string resolved = DataDirectoryResolver.Resolve(dir, out report);
                Assert.Equal(Path.GetFullPath(dir), resolved, "explicit writable directory is returned");
                Assert.Contains(report, "指定的数据目录", "report mentions the explicit data directory");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("DataDirectoryResolver: explicit unwritable data dir throws a helpful error")]
        public static void ResolverExplicitUnwritable()
        {
            string dir = TestFiles.CreateTempDir("resolver-bad");
            try
            {
                string filePath = Path.Combine(dir, "blocking-file.txt");
                File.WriteAllText(filePath, "x");
                string report;
                bool threw = false;
                try
                {
                    DataDirectoryResolver.Resolve(filePath, out report);
                }
                catch (IOException ex)
                {
                    threw = true;
                    Assert.Contains(ex.Message, "不可写", "error message explains the problem");
                }
                Assert.True(threw, "explicit unwritable data dir must throw IOException");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("DataDirectoryResolver: PortableDataDir points next to the entry assembly")]
        public static void ResolverPortableDir()
        {
            string portable = DataDirectoryResolver.PortableDataDir();
            Assert.NotNull(portable, "portable data dir must be resolvable for the test runner");
            Assert.True(portable.EndsWith("MultiDesk-data", StringComparison.OrdinalIgnoreCase),
                "portable data dir ends with MultiDesk-data");
            string assemblyLocation = typeof(TestsCore110).Assembly.Location;
            string expected = Path.Combine(Path.GetDirectoryName(assemblyLocation), "MultiDesk-data");
            Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(portable),
                "portable data dir sits next to the entry assembly");
        }

        [Test("DataDirectoryResolver: automatic resolution picks a writable directory")]
        public static void ResolverAutomatic()
        {
            string report;
            string resolved = DataDirectoryResolver.Resolve(null, out report);
            try
            {
                Assert.NotNull(resolved, "automatic resolution returns a directory");
                Assert.True(resolved.Length > 0, "automatic resolution returns a non-empty path");
                Assert.Contains(report, "数据目录", "report describes the chosen directory");
                string error;
                Assert.True(DataDirectoryResolver.CanUse(resolved, out error), "chosen directory is writable");
            }
            finally
            {
                // Clean up the portable directory the resolver may have created next to TestRunner.exe.
                if (!string.IsNullOrEmpty(resolved) && resolved.EndsWith("MultiDesk-data", StringComparison.OrdinalIgnoreCase))
                {
                    TestFiles.DeleteQuietly(resolved);
                }
            }
        }

        // ================= Launcher / ProcessIntegrity smoke =================

        [Test("Launcher: Launch(icon, out process) reports validation errors without starting a process")]
        public static void LauncherOutProcessValidation()
        {
            System.Diagnostics.Process process = null;
            string nullError = Launcher.Launch(null, out process);
            Assert.Equal(TestData.ErrorIconNull, nullError, "null icon error message");
            Assert.Null(process, "no process for a null icon");

            IconEntry empty = new IconEntry();
            string emptyError = Launcher.Launch(empty, out process);
            Assert.Equal(TestData.ErrorTargetEmpty, emptyError, "empty target error message");
            Assert.Null(process, "no process for an empty target");
        }

        [Test("ProcessIntegrity: integrity check is stable and never throws")]
        public static void ProcessIntegritySmoke()
        {
            bool first = MultiDesk.Native.ProcessIntegrity.IsLowIntegrity();
            bool second = MultiDesk.Native.ProcessIntegrity.IsLowIntegrity();
            Assert.Equal(first, second, "integrity detection is deterministic");
        }
    }
}
