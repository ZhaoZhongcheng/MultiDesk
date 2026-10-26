using System;
using System.Collections.Generic;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsModels
    {
        [Test("Models: IconEntry has safe defaults and a deep-ish Clone with a new Id")]
        public static void IconEntryDefaultsAndClone()
        {
            IconEntry icon = new IconEntry();
            Assert.NotNull(icon.Id, "Id default must not be null");
            Assert.Equal(32, icon.Id.Length, "Id default should be a GUID in N format");
            Assert.Equal(string.Empty, icon.DisplayName, "DisplayName default");
            Assert.Equal(string.Empty, icon.TargetPath, "TargetPath default");
            Assert.Equal(string.Empty, icon.Arguments, "Arguments default");
            Assert.Equal(string.Empty, icon.WorkingDirectory, "WorkingDirectory default");

            icon.DisplayName = TestData.ChineseIconName;
            icon.TargetPath = "C:\\Windows\\System32\\notepad.exe";
            icon.Arguments = "-n";
            icon.WorkingDirectory = "C:\\Windows";
            icon.X = 120;
            icon.Y = 224;

            IconEntry clone = icon.Clone();
            Assert.NotEqual(icon.Id, clone.Id, "Clone must generate a fresh Id");
            Assert.Equal(icon.DisplayName, clone.DisplayName, "Clone copies DisplayName");
            Assert.Equal(icon.TargetPath, clone.TargetPath, "Clone copies TargetPath");
            Assert.Equal(icon.Arguments, clone.Arguments, "Clone copies Arguments");
            Assert.Equal(icon.WorkingDirectory, clone.WorkingDirectory, "Clone copies WorkingDirectory");
            Assert.Near(120, clone.X, 1e-9, "Clone copies X");
            Assert.Near(224, clone.Y, 1e-9, "Clone copies Y");
        }

        [Test("Models: DesktopProfile has usable defaults")]
        public static void DesktopProfileDefaults()
        {
            DesktopProfile desktop = new DesktopProfile();
            Assert.NotNull(desktop.Id, "Id default must not be null");
            Assert.True(desktop.Id.Length > 0, "Id default must not be empty");
            Assert.True(!string.IsNullOrEmpty(desktop.Name), "Name default must not be empty");
            Assert.NotNull(desktop.Icons, "Icons list default must not be null");
            Assert.Equal(0, desktop.Icons.Count, "Icons list default must be empty");
            Assert.Equal(WallpaperMode.System, desktop.WallpaperMode, "WallpaperMode default");
            Assert.Equal(WallpaperFit.Fill, desktop.WallpaperFit, "WallpaperFit default");
            Assert.Equal(string.Empty, desktop.WallpaperPath, "WallpaperPath default");
            Assert.True(!string.IsNullOrEmpty(desktop.BackgroundColor), "BackgroundColor default must not be empty");
            Assert.True(desktop.ShowNameBadge, "ShowNameBadge default is true");
        }

        [Test("Models: AppOptions has usable defaults")]
        public static void AppOptionsDefaults()
        {
            AppOptions options = new AppOptions();
            Assert.False(options.StartWithWindows, "StartWithWindows default is false");
            Assert.True(options.ShowDesktopName, "ShowDesktopName default is true");
            Assert.False(options.FirstRunDone, "FirstRunDone default is false");
            Assert.Equal(48, options.IconSize, "IconSize default is 48");
            Assert.True(options.AutoArrangeIcons, "AutoArrangeIcons default is true (grid snap)");
            Assert.True(options.MatchWindowsIconMetrics, "MatchWindowsIconMetrics default is true (match real Windows desktop)");
            Assert.False(options.SkipRestrictedPrompt, "SkipRestrictedPrompt default is false (restricted-mode prompt is shown)");
            Assert.True(options.AutoElevateWhenRestricted, "AutoElevateWhenRestricted default is true (1.2.1 auto UAC restart)");
        }

        [Test("Models: CreateDefault returns two desktops with the first one active")]
        public static void CreateDefaultShape()
        {
            AppConfig config = AppConfig.CreateDefault();
            Assert.Equal(2, config.Desktops.Count, "default config has two desktops");
            Assert.Equal(TestData.ChineseWorkDesktop, config.Desktops[0].Name, "first default desktop name");
            Assert.Equal(TestData.ChineseGameDesktop, config.Desktops[1].Name, "second default desktop name");
            Assert.Equal(config.Desktops[0].Id, config.ActiveDesktopId, "first desktop is active by default");
            Assert.NotNull(config.Options, "default config must have options");
            Assert.False(config.Options.FirstRunDone, "first run flag starts as false");
            Assert.Equal(AppConfig.CurrentVersion, config.Version, "default config version");
        }

        [Test("Models: FindDesktop and IndexOfDesktop behave for hit/miss/null")]
        public static void FindDesktopAndIndexOf()
        {
            AppConfig config = AppConfig.CreateDefault();
            string id = config.Desktops[1].Id;
            Assert.Same(config.Desktops[1], config.FindDesktop(id), "FindDesktop returns the desktop instance");
            Assert.Equal(1, config.IndexOfDesktop(id), "IndexOfDesktop returns the list index");
            Assert.Null(config.FindDesktop("missing"), "unknown desktop id returns null");
            Assert.Null(config.FindDesktop(null), "null desktop id returns null");
            Assert.Null(config.FindDesktop(string.Empty), "empty desktop id returns null");
            Assert.Equal(-1, config.IndexOfDesktop("missing"), "unknown desktop index is -1");
            Assert.Null(config.FindDesktop(null), "null id is safe");
        }

        [Test("Models: GetActiveDesktop repairs a stale ActiveDesktopId on read")]
        public static void GetActiveDesktopRepairsStaleId()
        {
            AppConfig config = AppConfig.CreateDefault();
            config.ActiveDesktopId = "stale-id";
            DesktopProfile active = config.GetActiveDesktop();
            Assert.NotNull(active, "GetActiveDesktop must fall back to the first desktop");
            Assert.Same(config.Desktops[0], active, "fallback is the first desktop");
            Assert.Equal(config.Desktops[0].Id, config.ActiveDesktopId, "stale id is repaired in place");

            config.Desktops.Clear();
            Assert.Null(config.GetActiveDesktop(), "no desktops -> null active desktop");
        }
    }
}
