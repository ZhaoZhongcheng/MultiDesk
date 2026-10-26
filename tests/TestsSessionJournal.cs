using System;
using System.Collections.Generic;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsSessionJournal
    {
        private static JournalWindow MakeWindow(long handle, int processId, string title)
        {
            JournalWindow entry = new JournalWindow();
            entry.Handle = handle;
            entry.ProcessId = processId;
            entry.Title = title;
            return entry;
        }

        [Test("SessionJournal: defaults are version 1 with an empty window list")]
        public static void JournalDefaults()
        {
            SessionJournal journal = new SessionJournal();
            Assert.Equal(1, journal.Version, "journal version default");
            Assert.NotNull(journal.Windows, "window list default is not null");
            Assert.Equal(0, journal.Windows.Count, "window list default is empty");
            Assert.False(journal.IsFromCurrentBoot(), "an unset boot marker is not from the current boot");
        }

        [Test("SessionJournal: CurrentBootMarker is positive and stable within a minute")]
        public static void BootMarkerBasics()
        {
            long first = SessionJournal.CurrentBootMarker();
            long second = SessionJournal.CurrentBootMarker();
            Assert.Greater(first, 0, "boot marker must be positive");
            long delta = Math.Abs(second - first);
            Assert.True(delta <= 1, "two calls within the same minute may differ by at most 1, actual delta " + delta);
        }

        [Test("SessionJournal: IsFromCurrentBoot tolerates a small clock drift")]
        public static void IsFromCurrentBootTolerance()
        {
            long marker = SessionJournal.CurrentBootMarker();

            SessionJournal exact = new SessionJournal();
            exact.BootMarker = marker;
            Assert.True(exact.IsFromCurrentBoot(), "exact marker matches the current boot");

            SessionJournal oneMinute = new SessionJournal();
            oneMinute.BootMarker = marker - 1;
            Assert.True(oneMinute.IsFromCurrentBoot(), "one minute drift is tolerated");

            SessionJournal threeMinutes = new SessionJournal();
            threeMinutes.BootMarker = marker - 3;
            Assert.False(threeMinutes.IsFromCurrentBoot(), "three minutes drift is rejected");

            SessionJournal future = new SessionJournal();
            future.BootMarker = marker + 4;
            Assert.False(future.IsFromCurrentBoot(), "far future marker is rejected");
        }

        [Test("JournalRestorer: only restores hidden windows whose pid still matches")]
        public static void RestorerRestoresMatchingPids()
        {
            FakeWindowSystem windows = new FakeWindowSystem();
            FakeWindow matching = windows.AddWindow(0x110001, 80001);
            matching.IsVisible = false;
            FakeWindow pidChanged = windows.AddWindow(0x110002, 80002);
            pidChanged.IsVisible = false;
            FakeWindow visibleAlready = windows.AddWindow(0x110003, 80003);

            SessionJournal journal = new SessionJournal();
            journal.Windows.Add(MakeWindow(0x110001, 80001, "matching"));
            journal.Windows.Add(MakeWindow(0x110002, 99999, "pid changed"));
            journal.Windows.Add(MakeWindow(0x110003, 80003, "already visible"));

            List<string> messages = new List<string>();
            int restored = JournalRestorer.Restore(journal, windows, delegate(string message) { messages.Add(message); });

            Assert.Equal(1, restored, "only one window needs restoring");
            Assert.True(matching.IsVisible, "matching hidden window is restored");
            Assert.False(pidChanged.IsVisible, "window with a different pid is not restored");
            Assert.True(visibleAlready.IsVisible, "already visible window is untouched");
            Assert.Equal(1, messages.Count, "one restore message logged");
        }

        [Test("JournalRestorer: null input is safe and returns zero")]
        public static void RestorerNullSafety()
        {
            FakeWindowSystem windows = new FakeWindowSystem();
            Assert.Equal(0, JournalRestorer.Restore(null, windows, null), "null journal restores nothing");

            SessionJournal journal = new SessionJournal();
            journal.Windows = null;
            Assert.Equal(0, JournalRestorer.Restore(journal, windows, null), "null window list restores nothing");
        }

        [Test("JournalRestorer: handles that no longer exist are skipped")]
        public static void RestorerSkipsDeadHandles()
        {
            FakeWindowSystem windows = new FakeWindowSystem();
            FakeWindow destroyed = windows.AddWindow(0x110004, 80004);
            destroyed.IsVisible = false;
            windows.RemoveWindow(0x110004);

            SessionJournal journal = new SessionJournal();
            journal.Windows.Add(MakeWindow(0x110004, 80004, "destroyed"));
            journal.Windows.Add(MakeWindow(0x110005, 80005, "never existed"));
            journal.Windows.Add(null);

            int restored = JournalRestorer.Restore(journal, windows, null);
            Assert.Equal(0, restored, "dead handles are not restored");
        }

        [Test("JournalRestorer: works without a log callback")]
        public static void RestorerWithoutLogCallback()
        {
            FakeWindowSystem windows = new FakeWindowSystem();
            FakeWindow window = windows.AddWindow(0x110006, 80006);
            window.IsVisible = false;
            SessionJournal journal = new SessionJournal();
            journal.Windows.Add(MakeWindow(0x110006, 80006, "no log"));
            Assert.Equal(1, JournalRestorer.Restore(journal, windows, null), "restore works with a null log callback");
            Assert.True(window.IsVisible, "window restored");
        }

        [Test("SessionJournal: JSON round-trip keeps boot marker and window entries")]
        public static void JournalJsonRoundTrip()
        {
            SessionJournal journal = new SessionJournal();
            journal.BootMarker = SessionJournal.CurrentBootMarker();
            journal.Windows.Add(MakeWindow(0x110007, 80007, "hello"));
            JournalWindow second = MakeWindow(0x110008, 80008, "\u4e2d\u6587\u6807\u9898");
            second.DesktopId = TestData.WorkDesktopId;
            second.WasMinimized = true;
            second.WasForeground = true;
            journal.Windows.Add(second);

            string json = JsonIo.Serialize(journal);
            Assert.NotNull(json, "serialized journal");
            Assert.True(json.Length > 0, "serialized journal is not empty");
            SessionJournal restored = JsonIo.Deserialize<SessionJournal>(json);

            Assert.NotNull(restored, "deserialized journal");
            Assert.Equal(journal.BootMarker, restored.BootMarker, "boot marker round-trips");
            Assert.Equal(2, restored.Windows.Count, "window count round-trips");
            Assert.Equal(0x110007L, restored.Windows[0].Handle, "handle round-trips");
            Assert.Equal(80007, restored.Windows[0].ProcessId, "process id round-trips");
            Assert.Equal("hello", restored.Windows[0].Title, "title round-trips");
            Assert.Equal("\u4e2d\u6587\u6807\u9898", restored.Windows[1].Title, "Chinese title round-trips");
            Assert.Equal(TestData.WorkDesktopId, restored.Windows[1].DesktopId, "desktop id round-trips");
            Assert.True(restored.Windows[1].WasMinimized, "minimized flag round-trips");
            Assert.True(restored.Windows[1].WasForeground, "foreground flag round-trips");
        }
    }
}
