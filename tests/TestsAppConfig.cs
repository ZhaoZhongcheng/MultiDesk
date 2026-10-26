using System;
using System.Collections.Generic;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsAppConfig
    {
        private static DesktopProfile NewDesktop(string id, string name)
        {
            DesktopProfile desktop = new DesktopProfile();
            desktop.Id = id;
            desktop.Name = name;
            return desktop;
        }

        [Test("AppConfig.Sanitize: empty desktop list is repaired to at least one desktop")]
        public static void SanitizeEmptyDesktopList()
        {
            AppConfig config = new AppConfig();
            config.Desktops.Clear();
            config.ActiveDesktopId = string.Empty;
            config.Sanitize();
            Assert.Equal(1, config.Desktops.Count, "at least one desktop must remain");
            Assert.NotNull(config.Desktops[0], "synthesized desktop must not be null");
            Assert.True(!string.IsNullOrEmpty(config.Desktops[0].Id), "synthesized desktop must have an id");
            Assert.True(!string.IsNullOrEmpty(config.Desktops[0].Name), "synthesized desktop must have a name");
            Assert.Equal(config.Desktops[0].Id, config.ActiveDesktopId, "active id must point at the desktop");
            Assert.NotNull(config.Desktops[0].Icons, "synthesized desktop must have an icon list");
        }

        [Test("AppConfig.Sanitize: null desktop entries are removed")]
        public static void SanitizeRemovesNullDesktops()
        {
            AppConfig config = new AppConfig();
            config.Desktops.Add(NewDesktop("d1", "One"));
            config.Desktops.Add(null);
            config.Desktops.Add(NewDesktop("d2", "Two"));
            config.Sanitize();
            Assert.Equal(2, config.Desktops.Count, "null desktop entries must be dropped");
            Assert.Equal("d1", config.Desktops[0].Id, "first desktop preserved");
            Assert.Equal("d2", config.Desktops[1].Id, "third desktop preserved at index 1");
            Assert.Equal("d1", config.ActiveDesktopId, "active id points at the first desktop");
        }

        [Test("AppConfig.Sanitize: duplicate desktop ids are made unique")]
        public static void SanitizeFixesDuplicateIds()
        {
            AppConfig config = new AppConfig();
            config.Desktops.Add(NewDesktop("dup", "One"));
            config.Desktops.Add(NewDesktop("dup", "Two"));
            config.ActiveDesktopId = "dup";
            config.Sanitize();
            Assert.Equal(2, config.Desktops.Count, "both desktops kept");
            Assert.Equal("dup", config.Desktops[0].Id, "first occurrence keeps the original id");
            Assert.True(config.Desktops[1].Id.Length > 0, "second desktop gets a generated id");
            Assert.NotEqual("dup", config.Desktops[1].Id, "second id must differ");
            Assert.NotEqual(config.Desktops[0].Id, config.Desktops[1].Id, "ids must be unique");
            Assert.Equal(config.Desktops[0].Id, config.ActiveDesktopId, "active id resolves to the first desktop");
        }

        [Test("AppConfig.Sanitize: empty desktop ids are generated")]
        public static void SanitizeFixesEmptyIds()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = NewDesktop(string.Empty, "NoId");
            config.Desktops.Add(desktop);
            config.ActiveDesktopId = string.Empty;
            config.Sanitize();
            Assert.True(!string.IsNullOrEmpty(desktop.Id), "empty id must be replaced");
            Assert.Equal(32, desktop.Id.Length, "generated id is a GUID in N format");
            Assert.Equal(desktop.Id, config.ActiveDesktopId, "active id follows the repaired desktop");
        }

        [Test("AppConfig.Sanitize: unknown ActiveDesktopId is repaired")]
        public static void SanitizeFixesUnknownActiveId()
        {
            AppConfig config = new AppConfig();
            config.Desktops.Add(NewDesktop("real-1", "One"));
            config.Desktops.Add(NewDesktop("real-2", "Two"));
            config.ActiveDesktopId = "does-not-exist";
            config.Sanitize();
            Assert.Equal("real-1", config.ActiveDesktopId, "active id must fall back to the first desktop");
        }

        [Test("AppConfig.Sanitize: null/empty ActiveDesktopId is repaired")]
        public static void SanitizeFixesEmptyActiveId()
        {
            AppConfig config = new AppConfig();
            config.Desktops.Add(NewDesktop("real-1", "One"));
            config.ActiveDesktopId = null;
            config.Sanitize();
            Assert.Equal("real-1", config.ActiveDesktopId, "null active id must be repaired");

            config.ActiveDesktopId = string.Empty;
            config.Sanitize();
            Assert.Equal("real-1", config.ActiveDesktopId, "empty active id must be repaired");
        }

        [Test("AppConfig.Sanitize: missing Options object is recreated")]
        public static void SanitizeCreatesOptions()
        {
            AppConfig config = new AppConfig();
            config.Desktops.Add(NewDesktop("d1", "One"));
            config.Options = null;
            config.Sanitize();
            Assert.NotNull(config.Options, "Options must be recreated");
            Assert.True(config.Options.ShowDesktopName, "recreated options use sane defaults");
            Assert.False(config.Options.FirstRunDone, "recreated FirstRunDone defaults to false");
        }

        [Test("AppConfig.Sanitize: version is normalized to the current version")]
        public static void SanitizeFixesVersion()
        {
            Assert.Equal(4, AppConfig.CurrentVersion, "1.2.1 uses config version 4");

            AppConfig zero = new AppConfig();
            zero.Desktops.Add(NewDesktop("d1", "One"));
            zero.Version = 0;
            zero.Sanitize();
            Assert.Equal(AppConfig.CurrentVersion, zero.Version, "version 0 is repaired to the current version");

            AppConfig negative = new AppConfig();
            negative.Desktops.Add(NewDesktop("d1", "One"));
            negative.Version = -7;
            negative.Sanitize();
            Assert.Equal(AppConfig.CurrentVersion, negative.Version, "negative version is repaired");

            AppConfig legacy = new AppConfig();
            legacy.Desktops.Add(NewDesktop("d1", "One"));
            legacy.Version = 1;
            legacy.Sanitize();
            Assert.Equal(AppConfig.CurrentVersion, legacy.Version, "v1 config is migrated to the current version");

            AppConfig future = new AppConfig();
            future.Desktops.Add(NewDesktop("d1", "One"));
            future.Version = 99;
            future.Sanitize();
            Assert.Equal(AppConfig.CurrentVersion, future.Version,
                "an unknown future version is normalized to the current version (data repaired by Sanitize)");
        }

        [Test("AppConfig.Sanitize: blank desktop fields are repaired")]
        public static void SanitizeRepairsDesktopFields()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = NewDesktop("d1", string.Empty);
            desktop.BackgroundColor = string.Empty;
            desktop.WallpaperPath = null;
            config.Desktops.Add(desktop);
            config.Sanitize();
            Assert.True(!string.IsNullOrEmpty(desktop.Name), "empty desktop name must be replaced");
            Assert.Equal("#FF1B1F2A", desktop.BackgroundColor, "empty background color gets the default");
            Assert.NotNull(desktop.WallpaperPath, "null wallpaper path becomes an empty string");
            Assert.Equal(string.Empty, desktop.WallpaperPath, "wallpaper path is an empty string");
        }

        [Test("AppConfig.Sanitize: null icon list is created and null icon entries removed")]
        public static void SanitizeIconList()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = NewDesktop("d1", "One");
            desktop.Icons = null;
            config.Desktops.Add(desktop);
            config.Sanitize();
            Assert.NotNull(desktop.Icons, "null icon list must be created");
            Assert.Equal(0, desktop.Icons.Count, "created icon list starts empty");

            desktop.Icons.Add(null);
            IconEntry valid = new IconEntry();
            valid.TargetPath = "C:\\x.txt";
            desktop.Icons.Add(valid);
            config.Sanitize();
            Assert.Equal(1, desktop.Icons.Count, "null icon entries must be removed");
            Assert.Same(valid, desktop.Icons[0], "valid icon entry is preserved");
        }

        [Test("AppConfig.Sanitize: icon fields with empty values are repaired")]
        public static void SanitizeRepairsIconFields()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = NewDesktop("d1", "One");
            IconEntry icon = new IconEntry();
            icon.Id = string.Empty;
            icon.DisplayName = string.Empty;
            icon.TargetPath = "C:\\Windows\\System32\\calc.exe";
            icon.Arguments = null;
            icon.WorkingDirectory = null;
            desktop.Icons.Add(icon);
            config.Desktops.Add(desktop);
            config.Sanitize();
            Assert.True(!string.IsNullOrEmpty(icon.Id), "empty icon id must be generated");
            Assert.Equal(icon.TargetPath, icon.DisplayName, "empty display name falls back to the target path");
            Assert.NotNull(icon.Arguments, "null arguments become an empty string");
            Assert.Equal(string.Empty, icon.Arguments, "arguments are an empty string");
            Assert.NotNull(icon.WorkingDirectory, "null working directory becomes an empty string");
            Assert.Equal(string.Empty, icon.WorkingDirectory, "working directory is an empty string");
            Assert.Equal("C:\\Windows\\System32\\calc.exe", icon.TargetPath, "target path is preserved");
        }

        [Test("AppConfig.Sanitize: null target path and null name become empty strings")]
        public static void SanitizeHandlesNullStrings()
        {
            AppConfig config = new AppConfig();
            DesktopProfile desktop = NewDesktop("d1", "One");
            IconEntry icon = new IconEntry();
            icon.TargetPath = null;
            icon.DisplayName = null;
            desktop.Icons.Add(icon);
            config.Desktops.Add(desktop);
            config.Sanitize();
            Assert.NotNull(icon.TargetPath, "null target path must be repaired");
            Assert.Equal(string.Empty, icon.TargetPath, "target path becomes an empty string");
        }

        [Test("AppConfig.Sanitize: running it twice is idempotent")]
        public static void SanitizeIsIdempotent()
        {
            AppConfig config = AppConfig.CreateDefault();
            config.Desktops.Add(null);
            config.Desktops.Add(NewDesktop(string.Empty, string.Empty));
            config.Desktops.Add(NewDesktop("second", "Second"));
            config.ActiveDesktopId = "missing";
            config.Options = null;
            config.Version = 0;
            config.Sanitize();

            string firstId = config.Desktops[0].Id;
            string secondId = config.Desktops[1].Id;
            string activeId = config.ActiveDesktopId;
            int count = config.Desktops.Count;
            int version = config.Version;

            config.Sanitize();
            Assert.Equal(count, config.Desktops.Count, "desktop count is stable");
            Assert.Equal(firstId, config.Desktops[0].Id, "first id is stable");
            Assert.Equal(secondId, config.Desktops[1].Id, "second id is stable");
            Assert.Equal(activeId, config.ActiveDesktopId, "active id is stable");
            Assert.Equal(version, config.Version, "version is stable");
            Assert.NotNull(config.Options, "options stay repaired");
        }
    }
}
