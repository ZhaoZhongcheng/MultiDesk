using System;
using System.Collections.Generic;
using System.Windows;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsMisc
    {
        [Test("Launcher: CreateFromPath strips the extension from a file path")]
        public static void CreateFromPathStripsExtension()
        {
            IconEntry icon = Launcher.CreateFromPath("C:\\Windows\\System32\\notepad.exe");
            Assert.Equal("notepad", icon.DisplayName, "display name is the file name without extension");
            Assert.Equal("C:\\Windows\\System32\\notepad.exe", icon.TargetPath, "target path is kept verbatim");
            Assert.True(!string.IsNullOrEmpty(icon.Id), "a new icon gets an id");
        }

        [Test("Launcher: CreateFromPath only strips the last extension")]
        public static void CreateFromPathKeepsInnerDots()
        {
            IconEntry icon = Launcher.CreateFromPath("C:\\tmp\\report.final.pdf");
            Assert.Equal("report.final", icon.DisplayName, "only the last extension is removed");
        }

        [Test("Launcher: CreateFromPath works for directories and Chinese names")]
        public static void CreateFromPathDirectoryAndChinese()
        {
            IconEntry folder = Launcher.CreateFromPath("C:\\Windows\\System32");
            Assert.Equal("System32", folder.DisplayName, "directory name without extension");

            IconEntry chinese = Launcher.CreateFromPath("C:\\Users\\me\\Desktop\\\u6587\u6863.docx");
            Assert.Equal("\u6587\u6863", chinese.DisplayName, "Chinese file names are preserved");
            Assert.Equal("C:\\Users\\me\\Desktop\\\u6587\u6863.docx", chinese.TargetPath, "Chinese path is preserved");
        }

        [Test("Launcher: CreateFromPath tolerates empty and null paths")]
        public static void CreateFromPathEdgeCases()
        {
            IconEntry empty = Launcher.CreateFromPath(string.Empty);
            Assert.NotNull(empty, "empty path still returns an icon");
            Assert.Equal(string.Empty, empty.TargetPath, "empty target path");
            Assert.Equal(string.Empty, empty.DisplayName, "empty display name");

            IconEntry nullPath = Launcher.CreateFromPath(null);
            Assert.NotNull(nullPath, "null path still returns an icon");
            Assert.Null(nullPath.TargetPath, "null target path is preserved (Sanitize repairs it later)");
        }

        [Test("Launcher: Launch reports empty inputs without starting a process")]
        public static void LaunchValidation()
        {
            Assert.Equal(TestData.ErrorIconNull, Launcher.Launch(null), "null icon error message");
            Assert.Equal(TestData.ErrorTargetEmpty, Launcher.Launch(new IconEntry()), "empty target error message");
        }

        [Test("Launcher: OpenFileLocation validates its input")]
        public static void OpenFileLocationValidation()
        {
            Assert.Equal(TestData.ErrorPathEmpty, Launcher.OpenFileLocation(null), "null path error message");
            Assert.Equal(TestData.ErrorPathEmpty, Launcher.OpenFileLocation(string.Empty), "empty path error message");

            string missing = "C:\\__multidesk_missing_path__\\nothing.exe";
            string message = Launcher.OpenFileLocation(missing);
            Assert.NotNull(message, "missing path returns an error message");
            Assert.Contains(message, missing, "error message mentions the missing path");
        }

        [Test("JsonIo: Deserialize returns null for null/empty input")]
        public static void JsonIoEmptyInput()
        {
            Assert.Null(JsonIo.Deserialize<AppConfig>(null), "null json returns null");
            Assert.Null(JsonIo.Deserialize<AppConfig>(string.Empty), "empty json returns null");
        }

        [Test("JsonIo: config JSON round-trip keeps the desktops")]
        public static void JsonIoRoundTrip()
        {
            AppConfig config = AppConfig.CreateDefault();
            string json = JsonIo.Serialize(config);
            Assert.NotNull(json, "serialized config");
            Assert.True(json.Length > 0, "serialized config is not empty");
            AppConfig restored = JsonIo.Deserialize<AppConfig>(json);
            Assert.NotNull(restored, "deserialized config");
            Assert.Equal(2, restored.Desktops.Count, "desktop count round-trips");
            Assert.Equal(TestData.ChineseWorkDesktop, restored.Desktops[0].Name, "Chinese name round-trips");
            Assert.Equal(config.ActiveDesktopId, restored.ActiveDesktopId, "active desktop round-trips");
        }

        [Test("JsonIo: invalid JSON throws (ConfigStore is responsible for recovery)")]
        public static void JsonIoInvalidJsonThrows()
        {
            bool threw = false;
            try
            {
                JsonIo.Deserialize<AppConfig>("{ this is not json");
            }
            catch (Exception)
            {
                threw = true;
            }
            Assert.True(threw, "invalid json must surface an exception to the caller");
        }

        [Test("DesktopImporter: GetDesktopFolderPaths returns existing directories only")]
        public static void DesktopImporterPaths()
        {
            List<string> paths = DesktopImporter.GetDesktopFolderPaths();
            Assert.NotNull(paths, "path list is never null");
            for (int i = 0; i < paths.Count; i++)
            {
                Assert.True(System.IO.Directory.Exists(paths[i]), "each returned path exists: " + paths[i]);
            }
        }

        [Test("DesktopImporter: ImportExistingIcons with a null desktop returns zero")]
        public static void DesktopImporterNullDesktop()
        {
            Assert.Equal(0, DesktopImporter.ImportExistingIcons(null, new Rect(0, 0, 1920, 1040)),
                "null desktop imports nothing");
        }

        [Test("ScreenLayout: defaults are a sane 1920x1080 layout")]
        public static void ScreenLayoutDefaults()
        {
            ScreenLayout layout = new ScreenLayout();
            Assert.Near(1920, layout.VirtualScreen.Width, 1e-9, "default virtual screen width");
            Assert.Near(1080, layout.VirtualScreen.Height, 1e-9, "default virtual screen height");
            Assert.Near(1920, layout.PrimaryWorkArea.Width, 1e-9, "default work area width");
            Assert.Near(1040, layout.PrimaryWorkArea.Height, 1e-9, "default work area height");
            Assert.Equal(layout.PrimaryWorkArea, layout.LayoutArea, "LayoutArea maps to the primary work area");
        }

        [Test("ScreenLayout: Capture never returns an empty layout")]
        public static void ScreenLayoutCaptureIsSane()
        {
            ScreenLayout captured = ScreenLayout.Capture();
            Assert.NotNull(captured, "Capture returns a layout");
            Assert.True(captured.VirtualScreen.Width >= 1, "virtual screen width is positive");
            Assert.True(captured.VirtualScreen.Height >= 1, "virtual screen height is positive");
            Assert.True(captured.PrimaryWorkArea.Width >= 1, "work area width is positive");
            Assert.True(captured.PrimaryWorkArea.Height >= 1, "work area height is positive");
            Assert.True(captured.PrimaryWorkArea.Width <= captured.VirtualScreen.Width + 1,
                "work area fits inside the virtual screen width");
            Assert.True(captured.PrimaryWorkArea.Height <= captured.VirtualScreen.Height + 1,
                "work area fits inside the virtual screen height");
        }

        [Test("ScreenLayout: EqualsTo compares both rectangles and null")]
        public static void ScreenLayoutEqualsTo()
        {
            ScreenLayout first = new ScreenLayout();
            ScreenLayout second = new ScreenLayout();
            Assert.True(first.EqualsTo(second), "equal layouts compare equal");
            Assert.False(first.EqualsTo(null), "null layout is never equal");

            second.PrimaryWorkArea = new Rect(0, 0, 100, 100);
            Assert.False(first.EqualsTo(second), "different work areas compare different");
        }
    }
}
