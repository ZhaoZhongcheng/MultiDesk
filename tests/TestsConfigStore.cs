using System;
using System.Collections.Generic;
using System.IO;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsConfigStore
    {
        private static AppConfig BuildRichConfig()
        {
            AppConfig config = new AppConfig();

            DesktopProfile work = new DesktopProfile();
            work.Id = "desk-work-cn";
            work.Name = TestData.ChineseWorkDesktop;
            work.WallpaperMode = WallpaperMode.SolidColor;
            work.WallpaperPath = string.Empty;
            work.WallpaperFit = WallpaperFit.Center;
            work.BackgroundColor = "#FF102030";
            work.ShowNameBadge = false;

            IconEntry notepad = new IconEntry();
            notepad.Id = "icon-notepad";
            notepad.DisplayName = TestData.ChineseIconName;
            notepad.TargetPath = "C:\\Windows\\System32\\notepad.exe";
            notepad.Arguments = "-n";
            notepad.WorkingDirectory = "C:\\Windows";
            notepad.X = 120;
            notepad.Y = 224;
            work.Icons.Add(notepad);

            IconEntry folder = new IconEntry();
            folder.Id = "icon-folder";
            folder.DisplayName = "Second icon";
            folder.TargetPath = "C:\\Windows";
            folder.X = 24;
            folder.Y = 16;
            work.Icons.Add(folder);

            DesktopProfile game = new DesktopProfile();
            game.Id = "desk-game-cn";
            game.Name = TestData.ChineseGameDesktop;
            game.WallpaperMode = WallpaperMode.Image;
            game.WallpaperPath = TestData.ChineseWallpaperPath;
            game.WallpaperFit = WallpaperFit.Stretch;
            game.BackgroundColor = "#FF000000";
            game.ShowNameBadge = true;

            config.Desktops.Add(work);
            config.Desktops.Add(game);
            config.ActiveDesktopId = game.Id;
            config.Options.StartWithWindows = true;
            config.Options.ShowDesktopName = false;
            config.Options.FirstRunDone = true;
            config.Options.IconSize = 64;
            config.Options.AutoArrangeIcons = false;
            config.Options.MatchWindowsIconMetrics = false;
            config.Options.SkipRestrictedPrompt = true;
            config.Options.AutoElevateWhenRestricted = false;
            config.Options.WindowsIconSize = 48;
            config.Options.WindowsCellWidth = 76.6667;
            config.Options.WindowsCellHeight = 98.0;
            config.Options.WindowsOriginX = 0.0;
            config.Options.WindowsOriginY = 3.3333;
            return config;
        }

        [Test("ConfigStore: DefaultDataDir is a non-empty MultiDesk folder and is used as fallback")]
        public static void DefaultDataDirIsUsable()
        {
            string defaultDir = ConfigStore.DefaultDataDir();
            Assert.NotNull(defaultDir, "DefaultDataDir must not be null");
            Assert.True(defaultDir.Length > 0, "DefaultDataDir must not be empty");
            Assert.Contains(defaultDir, "MultiDesk", "DefaultDataDir must contain the application name");

            ConfigStore fromNull = new ConfigStore(null);
            Assert.Equal(defaultDir, fromNull.DataDir, "null data dir falls back to the default");
            ConfigStore fromEmpty = new ConfigStore(string.Empty);
            Assert.Equal(defaultDir, fromEmpty.DataDir, "empty data dir falls back to the default");
            Assert.Equal(Path.Combine(defaultDir, "config.json"), fromEmpty.ConfigPath, "config path layout");
            Assert.Equal(Path.Combine(defaultDir, "config.json.bak"), fromEmpty.BackupPath, "backup path layout");
        }

        [Test("ConfigStore: save/load round-trip keeps Chinese text, icons, positions and wallpapers")]
        public static void SaveLoadRoundTrip()
        {
            string dir = TestFiles.CreateTempDir("roundtrip");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                Assert.False(store.Exists, "no config file before the first save");
                store.Save(BuildRichConfig());
                Assert.True(store.Exists, "Exists must be true after save");
                Assert.True(File.Exists(store.ConfigPath), "config.json must exist after save");

                AppConfig loaded = store.Load();
                Assert.NotNull(loaded, "loaded config must not be null");
                Assert.Equal(AppConfig.CurrentVersion, loaded.Version, "version round-trips");
                Assert.Equal(2, loaded.Desktops.Count, "desktop count round-trips");

                DesktopProfile work = loaded.Desktops[0];
                Assert.Equal("desk-work-cn", work.Id, "desktop id round-trips");
                Assert.Equal(TestData.ChineseWorkDesktop, work.Name, "Chinese desktop name round-trips");
                Assert.Equal(WallpaperMode.SolidColor, work.WallpaperMode, "wallpaper mode round-trips");
                Assert.Equal(WallpaperFit.Center, work.WallpaperFit, "wallpaper fit round-trips");
                Assert.Equal("#FF102030", work.BackgroundColor, "background color round-trips");
                Assert.False(work.ShowNameBadge, "show badge round-trips");
                Assert.Equal(2, work.Icons.Count, "icon count round-trips");

                IconEntry notepad = work.Icons[0];
                Assert.Equal("icon-notepad", notepad.Id, "icon id round-trips");
                Assert.Equal(TestData.ChineseIconName, notepad.DisplayName, "Chinese icon name round-trips");
                Assert.Equal("C:\\Windows\\System32\\notepad.exe", notepad.TargetPath, "icon target round-trips");
                Assert.Equal("-n", notepad.Arguments, "icon arguments round-trip");
                Assert.Equal("C:\\Windows", notepad.WorkingDirectory, "icon working dir round-trips");
                Assert.Near(120, notepad.X, 1e-9, "icon X round-trips");
                Assert.Near(224, notepad.Y, 1e-9, "icon Y round-trips");

                IconEntry folder = work.Icons[1];
                Assert.Equal("C:\\Windows", folder.TargetPath, "folder target round-trips");
                Assert.Near(24, folder.X, 1e-9, "second icon X round-trips");
                Assert.Near(16, folder.Y, 1e-9, "second icon Y round-trips");

                DesktopProfile game = loaded.Desktops[1];
                Assert.Equal("desk-game-cn", game.Id, "second desktop id round-trips");
                Assert.Equal(TestData.ChineseGameDesktop, game.Name, "second Chinese name round-trips");
                Assert.Equal(WallpaperMode.Image, game.WallpaperMode, "image wallpaper mode round-trips");
                Assert.Equal(TestData.ChineseWallpaperPath, game.WallpaperPath, "Chinese wallpaper path round-trips");
                Assert.Equal(WallpaperFit.Stretch, game.WallpaperFit, "stretch fit round-trips");
                Assert.Equal(0, game.Icons.Count, "empty desktop icon list round-trips");

                Assert.Equal("desk-game-cn", loaded.ActiveDesktopId, "active desktop id round-trips");
                Assert.True(loaded.Options.StartWithWindows, "option StartWithWindows round-trips");
                Assert.False(loaded.Options.ShowDesktopName, "option ShowDesktopName round-trips");
                Assert.True(loaded.Options.FirstRunDone, "option FirstRunDone round-trips");
                Assert.Equal(64, loaded.Options.IconSize, "option IconSize round-trips");
                Assert.False(loaded.Options.AutoArrangeIcons, "option AutoArrangeIcons round-trips");
                Assert.False(loaded.Options.MatchWindowsIconMetrics, "option MatchWindowsIconMetrics round-trips");
                Assert.True(loaded.Options.SkipRestrictedPrompt, "option SkipRestrictedPrompt round-trips");
                Assert.False(loaded.Options.AutoElevateWhenRestricted, "option AutoElevateWhenRestricted round-trips");
                Assert.Equal(48, loaded.Options.WindowsIconSize, "cached Windows icon size round-trips");
                Assert.Near(76.6667, loaded.Options.WindowsCellWidth, 1e-6, "cached Windows cell width round-trips");
                Assert.Near(98.0, loaded.Options.WindowsCellHeight, 1e-6, "cached Windows cell height round-trips");
                Assert.Near(3.3333, loaded.Options.WindowsOriginY, 1e-6, "cached Windows origin Y round-trips");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("ConfigStore: missing file loads the default config without writing to disk")]
        public static void MissingFileLoadsDefault()
        {
            string dir = TestFiles.CreateTempDir("missing");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                AppConfig config = store.Load();
                Assert.NotNull(config, "load of a missing file returns a default config");
                Assert.Equal(2, config.Desktops.Count, "default config has two desktops");
                Assert.Equal(TestData.ChineseWorkDesktop, config.Desktops[0].Name, "default work desktop name");
                Assert.Equal(TestData.ChineseGameDesktop, config.Desktops[1].Name, "default game desktop name");
                Assert.True(!string.IsNullOrEmpty(config.ActiveDesktopId), "default config has an active desktop");
                Assert.False(store.Exists, "Load must not create the config file");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("ConfigStore: corrupted JSON falls back to the default config")]
        public static void CorruptJsonFallsBackToDefault()
        {
            string dir = TestFiles.CreateTempDir("corrupt");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                TestFiles.WriteAllText(store.ConfigPath, "{ \"desktops\": [ this is not valid json");
                AppConfig config = store.Load();
                Assert.NotNull(config, "corrupted config still returns a config object");
                Assert.Equal(2, config.Desktops.Count, "corrupted config falls back to the defaults");
                Assert.True(!string.IsNullOrEmpty(config.ActiveDesktopId), "fallback config has an active desktop");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("ConfigStore: previous version backup is restored when config.json is corrupted")]
        public static void CorruptJsonUsesBackup()
        {
            string dir = TestFiles.CreateTempDir("backup");
            try
            {
                ConfigStore store = new ConfigStore(dir);

                AppConfig first = BuildRichConfig();
                first.Desktops[0].Name = "Version-One";
                store.Save(first);

                AppConfig second = BuildRichConfig();
                second.Desktops[0].Name = "Version-Two";
                store.Save(second);

                Assert.True(File.Exists(store.BackupPath), "second save must keep a backup");
                Assert.Equal("Version-Two", store.Load().Desktops[0].Name, "current file wins when valid");

                TestFiles.WriteAllText(store.ConfigPath, "### definitely not json ###");
                AppConfig recovered = store.Load();
                Assert.NotNull(recovered, "recovered config must not be null");
                Assert.Equal("Version-One", recovered.Desktops[0].Name, "backup content must be used");
                Assert.Equal(2, recovered.Desktops.Count, "backup round-trips the desktop count");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("ConfigStore: Save(null) throws ArgumentNullException")]
        public static void SaveNullThrows()
        {
            string dir = TestFiles.CreateTempDir("savenull");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                bool threw = false;
                try
                {
                    store.Save(null);
                }
                catch (ArgumentNullException)
                {
                    threw = true;
                }
                Assert.True(threw, "Save(null) must throw ArgumentNullException");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }

        [Test("ConfigStore: Save creates a missing (nested) data directory")]
        public static void SaveCreatesNestedDirectory()
        {
            string root = TestFiles.CreateTempDir("nested");
            try
            {
                string nested = Path.Combine(Path.Combine(root, "level1"), "level2");
                ConfigStore store = new ConfigStore(nested);
                Assert.False(Directory.Exists(nested), "nested data dir must not exist yet");
                store.Save(BuildRichConfig());
                Assert.True(Directory.Exists(nested), "Save must create the data directory");
                Assert.True(File.Exists(store.ConfigPath), "config file exists in the created directory");
            }
            finally
            {
                TestFiles.DeleteQuietly(root);
            }
        }

        [Test("ConfigStore: overwrite keeps the latest content readable")]
        public static void OverwriteKeepsLatestContent()
        {
            string dir = TestFiles.CreateTempDir("overwrite");
            try
            {
                ConfigStore store = new ConfigStore(dir);
                AppConfig config = BuildRichConfig();
                store.Save(config);
                config.Desktops[0].Name = "Renamed-Later";
                config.Desktops[0].Icons[0].X = 504;
                store.Save(config);

                AppConfig loaded = store.Load();
                Assert.Equal("Renamed-Later", loaded.Desktops[0].Name, "latest name is readable");
                Assert.Near(504, loaded.Desktops[0].Icons[0].X, 1e-9, "latest icon position is readable");
            }
            finally
            {
                TestFiles.DeleteQuietly(dir);
            }
        }
    }
}
