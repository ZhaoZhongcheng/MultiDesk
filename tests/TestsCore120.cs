using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using MultiDesk.Core;
using MultiDesk.Native;

namespace MultiDesk.Tests
{
    /// <summary>
    /// Tests for the configuration evolution across 1.1.0/1.2.0/1.2.1:
    /// version 4 migration (MatchWindowsIconMetrics / SkipRestrictedPrompt /
    /// AutoElevateWhenRestricted), the cached Windows desktop icon metrics,
    /// config-store round-trips and the real-Windows desktop icon metrics probe.
    /// </summary>
    public static class TestsCore120
    {
        private static AppConfig NewConfig(int version)
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = new DesktopProfile();
            desktop.Id = "d1";
            desktop.Name = "One";
            config.Desktops.Add(desktop);
            config.ActiveDesktopId = desktop.Id;
            config.Version = version;
            return config;
        }

        // ================= version 4 / options =================

        [Test("1.2.1 config: CurrentVersion is 4")]
        public static void CurrentVersionIsFour()
        {
            Assert.Equal(4, AppConfig.CurrentVersion, "1.2.1 uses config version 4");
        }

        [Test("1.2.1 config: AppOptions defaults for the new fields")]
        public static void NewOptionDefaults()
        {
            AppOptions options = new AppOptions();
            Assert.True(options.MatchWindowsIconMetrics,
                "match-Windows-icon-metrics defaults to true (1.2.0 aligns icons with the real desktop)");
            Assert.False(options.SkipRestrictedPrompt,
                "skip-restricted-prompt defaults to false (the prompt is shown)");
            Assert.True(options.AutoElevateWhenRestricted,
                "auto-elevate-when-restricted defaults to true (1.2.1 behaviour)");
            Assert.Equal(0, options.WindowsIconSize, "cached Windows icon size defaults to 0");
            Assert.Near(0, options.WindowsCellWidth, 1e-9, "cached Windows cell width defaults to 0");
            Assert.Near(0, options.WindowsCellHeight, 1e-9, "cached Windows cell height defaults to 0");
            Assert.Near(0, options.WindowsOriginX, 1e-9, "cached Windows origin X defaults to 0");
            Assert.Near(0, options.WindowsOriginY, 1e-9, "cached Windows origin Y defaults to 0");
        }

        [Test("1.2.1 config migration: v3 -> v4 enables AutoElevateWhenRestricted")]
        public static void MigrateV3ToV4()
        {
            AppConfig config = NewConfig(3);
            // A v3 config has no autoElevateWhenRestricted member: the deserialized
            // field keeps its CLR default (false).
            config.Options.AutoElevateWhenRestricted = false;
            config.Options.SkipRestrictedPrompt = true;
            config.Options.MatchWindowsIconMetrics = false;

            config.Sanitize();

            Assert.Equal(4, config.Version, "v3 config is migrated to version 4");
            Assert.True(config.Options.AutoElevateWhenRestricted,
                "v3 -> v4 migration enables automatic elevated restart");
            Assert.True(config.Options.SkipRestrictedPrompt,
                "v3 explicit skip-restricted-prompt is preserved");
            Assert.False(config.Options.MatchWindowsIconMetrics,
                "v3 explicit match-Windows setting is preserved");
        }

        [Test("1.2.1 config migration: v2 -> v4 applies both migrations")]
        public static void MigrateV2ToV4()
        {
            AppConfig config = NewConfig(2);
            config.Options.MatchWindowsIconMetrics = false;
            config.Options.AutoElevateWhenRestricted = false;
            config.Options.IconSize = 32;
            config.Options.AutoArrangeIcons = false;

            config.Sanitize();

            Assert.Equal(4, config.Version, "v2 config is migrated to version 4");
            Assert.True(config.Options.MatchWindowsIconMetrics, "v2 -> v4 enables Windows icon metrics");
            Assert.True(config.Options.AutoElevateWhenRestricted, "v2 -> v4 enables automatic elevated restart");
            Assert.False(config.Options.SkipRestrictedPrompt, "restricted-mode prompt stays enabled");
            Assert.Equal(32, config.Options.IconSize, "icon size is preserved");
            Assert.False(config.Options.AutoArrangeIcons, "free placement is preserved");
        }

        [Test("1.2.1 config migration: v1 -> v4 applies all migrations")]
        public static void MigrateV1ToV4()
        {
            AppConfig config = NewConfig(1);
            config.Options.IconSize = 0;
            config.Options.AutoArrangeIcons = false;
            config.Options.MatchWindowsIconMetrics = false;
            config.Options.AutoElevateWhenRestricted = false;

            config.Sanitize();

            Assert.Equal(4, config.Version, "v1 config is migrated to version 4");
            Assert.Equal(48, config.Options.IconSize, "v1 gets the default icon size");
            Assert.True(config.Options.AutoArrangeIcons, "v1 gets the default auto-arrange setting");
            Assert.True(config.Options.MatchWindowsIconMetrics, "v1 gets the Windows icon metrics default");
            Assert.True(config.Options.AutoElevateWhenRestricted, "v1 gets the auto-elevate default");
            Assert.False(config.Options.SkipRestrictedPrompt, "restricted-mode prompt stays enabled");
        }

        [Test("1.2.1 config: v4 keeps explicit Match/Skip/AutoElevate values")]
        public static void V4KeepsExplicitOptions()
        {
            AppConfig config = NewConfig(4);
            config.Options.MatchWindowsIconMetrics = false;
            config.Options.SkipRestrictedPrompt = true;
            config.Options.AutoElevateWhenRestricted = false;

            config.Sanitize();

            Assert.Equal(4, config.Version, "version stays 4");
            Assert.False(config.Options.MatchWindowsIconMetrics, "explicit custom metrics setting is preserved");
            Assert.True(config.Options.SkipRestrictedPrompt, "explicit skip-prompt setting is preserved");
            Assert.False(config.Options.AutoElevateWhenRestricted, "explicit auto-elevate=false is preserved");
        }

        [Test("1.2.1 config: v4 keeps default true values")]
        public static void V4KeepsDefaultsTrue()
        {
            AppConfig config = NewConfig(4);
            config.Options.MatchWindowsIconMetrics = true;
            config.Options.SkipRestrictedPrompt = false;
            config.Options.AutoElevateWhenRestricted = true;
            config.Sanitize();
            Assert.True(config.Options.MatchWindowsIconMetrics, "match-Windows stays true");
            Assert.False(config.Options.SkipRestrictedPrompt, "skip-prompt stays false");
            Assert.True(config.Options.AutoElevateWhenRestricted, "auto-elevate stays true");
        }

        // ================= ConfigStore round-trip / on-disk migrations =================

        [Test("1.2.1 ConfigStore: v4 save/load keeps all new options and cached metrics")]
        public static void ConfigStoreV4RoundTrip()
        {
            string dir = TestFiles.CreateTempDir("core121-roundtrip");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                AppConfig config = NewConfig(4);
                config.Options.MatchWindowsIconMetrics = false;
                config.Options.SkipRestrictedPrompt = true;
                config.Options.AutoElevateWhenRestricted = false;
                config.Options.IconSize = 64;
                config.Options.WindowsIconSize = 48;
                config.Options.WindowsCellWidth = 76.6667;
                config.Options.WindowsCellHeight = 98.0;
                config.Options.WindowsOriginX = 0.0;
                config.Options.WindowsOriginY = 3.3333;
                store.Save(config);

                AppConfig loaded = store.Load();
                Assert.NotNull(loaded, "loaded config must not be null");
                Assert.Equal(4, loaded.Version, "version 4 round-trips");
                Assert.False(loaded.Options.MatchWindowsIconMetrics, "MatchWindowsIconMetrics=false round-trips");
                Assert.True(loaded.Options.SkipRestrictedPrompt, "SkipRestrictedPrompt=true round-trips");
                Assert.False(loaded.Options.AutoElevateWhenRestricted, "AutoElevateWhenRestricted=false round-trips");
                Assert.Equal(64, loaded.Options.IconSize, "IconSize round-trips");
                Assert.Equal(48, loaded.Options.WindowsIconSize, "cached Windows icon size round-trips");
                Assert.Near(76.6667, loaded.Options.WindowsCellWidth, 1e-6, "cached cell width round-trips");
                Assert.Near(98.0, loaded.Options.WindowsCellHeight, 1e-6, "cached cell height round-trips");
                Assert.Near(3.3333, loaded.Options.WindowsOriginY, 1e-6, "cached origin Y round-trips");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("1.2.1 ConfigStore: loading an old v2 JSON file migrates all new options")]
        public static void ConfigStoreMigratesOldV2File()
        {
            string dir = TestFiles.CreateTempDir("core121-migrate-v2");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                string oldJson = "{\"version\":2,\"desktops\":[{\"id\":\"desk-old\",\"name\":\"Old\","
                    + "\"wallpaperMode\":2,\"wallpaperPath\":\"\",\"wallpaperFit\":0,"
                    + "\"backgroundColor\":\"#FF1B1F2A\",\"showBadge\":true,\"icons\":[]}],"
                    + "\"activeDesktopId\":\"desk-old\","
                    + "\"options\":{\"startWithWindows\":false,\"showDesktopName\":true,"
                    + "\"firstRunDone\":true,\"iconSize\":48,\"autoArrangeIcons\":true}}";
                TestFiles.WriteAllText(store.ConfigPath, oldJson);

                AppConfig loaded = store.Load();
                Assert.NotNull(loaded, "old v2 config loads");
                Assert.Equal(4, loaded.Version, "old v2 config is migrated to version 4");
                Assert.True(loaded.Options.MatchWindowsIconMetrics, "old config gets match-Windows icon metrics");
                Assert.True(loaded.Options.AutoElevateWhenRestricted, "old config gets auto-elevate-when-restricted");
                Assert.False(loaded.Options.SkipRestrictedPrompt, "old config keeps showing the restricted prompt");
                Assert.Equal(48, loaded.Options.IconSize, "old icon size is preserved");
                Assert.True(loaded.Options.AutoArrangeIcons, "old auto-arrange value is preserved");
                Assert.Equal(1, loaded.Desktops.Count, "old desktops are preserved");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("1.2.1 ConfigStore: loading an old v3 JSON file only applies the v3->v4 migration")]
        public static void ConfigStoreMigratesOldV3File()
        {
            string dir = TestFiles.CreateTempDir("core121-migrate-v3");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                string oldJson = "{\"version\":3,\"desktops\":[{\"id\":\"desk-v3\",\"name\":\"V3\","
                    + "\"wallpaperMode\":0,\"wallpaperPath\":\"\",\"wallpaperFit\":0,"
                    + "\"backgroundColor\":\"#FF1B1F2A\",\"showBadge\":true,\"icons\":[]}],"
                    + "\"activeDesktopId\":\"desk-v3\","
                    + "\"options\":{\"startWithWindows\":false,\"showDesktopName\":true,"
                    + "\"firstRunDone\":true,\"iconSize\":64,\"autoArrangeIcons\":false,"
                    + "\"matchWindowsIconMetrics\":false,\"skipRestrictedPrompt\":true}}";
                TestFiles.WriteAllText(store.ConfigPath, oldJson);

                AppConfig loaded = store.Load();
                Assert.NotNull(loaded, "old v3 config loads");
                Assert.Equal(4, loaded.Version, "old v3 config is migrated to version 4");
                Assert.False(loaded.Options.MatchWindowsIconMetrics, "v3 explicit match=false is preserved");
                Assert.True(loaded.Options.SkipRestrictedPrompt, "v3 explicit skip-prompt=true is preserved");
                Assert.True(loaded.Options.AutoElevateWhenRestricted, "v3 -> v4 migration enables auto-elevate");
                Assert.Equal(64, loaded.Options.IconSize, "v3 icon size is preserved");
                Assert.False(loaded.Options.AutoArrangeIcons, "v3 auto-arrange value is preserved");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        // ================= real Windows desktop metrics (smoke only) =================

        [Test("1.2.0 WindowsDesktopMetrics: probing the real desktop never throws")]
        public static void WindowsDesktopMetricsProbe()
        {
            IconMetrics metrics;
            bool ok = WindowsDesktopMetrics.TryGet(48, out metrics);
            if (ok)
            {
                Assert.NotNull(metrics, "successful probe returns metrics");
                Assert.InRange(metrics.IconSize, 24, 128, "probed icon size is sane");
                Assert.True(metrics.CellWidth >= 16, "probed cell width is sane");
                Assert.True(metrics.CellHeight >= 16, "probed cell height is sane");
                Assert.NotNaN(metrics.CellWidth, "cell width is not NaN");
                Assert.NotNaN(metrics.CellHeight, "cell height is not NaN");
                Assert.NotNaN(metrics.MarginX, "origin X is not NaN");
                Assert.NotNaN(metrics.MarginY, "origin Y is not NaN");
            }

            IconMetrics fallbackMetrics;
            bool okFallback = WindowsDesktopMetrics.TryGet(0, out fallbackMetrics);
            if (okFallback)
            {
                Assert.NotNull(fallbackMetrics, "fallback probe returns metrics");
                Assert.InRange(fallbackMetrics.IconSize, 24, 128, "fallback icon size is sane");
                Assert.True(fallbackMetrics.CellWidth >= 16, "fallback cell width is sane");
                Assert.True(fallbackMetrics.CellHeight >= 16, "fallback cell height is sane");
            }
        }

        [Test("1.2.0 WindowsDesktopMetrics: real desktop icon view visibility query is safe")]
        public static void DesktopIconViewVisibilitySmoke()
        {
            bool visible = DesktopShell.IsDesktopIconViewVisible();
            bool again = DesktopShell.IsDesktopIconViewVisible();
            Assert.Equal(visible, again, "desktop icon view visibility query is deterministic");
        }
    }
}
