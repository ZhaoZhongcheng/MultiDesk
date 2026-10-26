using System;
using System.Collections.Generic;
using MultiDesk.Core;

namespace MultiDesk.Tests
{
    public static class TestsDesktopEngine
    {
        private sealed class Fixture
        {
            public AppConfig Config;
            public FakeWindowSystem Windows;
            public FakeJournal Journal;
            public DesktopEngine Engine;
        }

        private static Fixture CreateFixture(bool initialize)
        {
            AppConfig config = new AppConfig();
            DesktopProfile work = new DesktopProfile();
            work.Id = TestData.WorkDesktopId;
            work.Name = TestData.ChineseWorkDesktop;
            DesktopProfile game = new DesktopProfile();
            game.Id = TestData.GameDesktopId;
            game.Name = TestData.ChineseGameDesktop;
            config.Desktops.Add(work);
            config.Desktops.Add(game);
            config.ActiveDesktopId = work.Id;
            config.Sanitize();

            Fixture fixture = new Fixture();
            fixture.Config = config;
            fixture.Windows = new FakeWindowSystem();
            fixture.Journal = new FakeJournal();
            fixture.Engine = new DesktopEngine(config, fixture.Windows, fixture.Journal, -1);
            if (initialize)
            {
                fixture.Engine.Initialize(TestData.WorkDesktopId);
            }
            return fixture;
        }

        private static Fixture CreateFixture()
        {
            return CreateFixture(true);
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

        [Test("DesktopEngine: Initialize adopts all existing visible windows into the active desktop")]
        public static void InitializeAdoptsExistingVisibleWindows()
        {
            Fixture fixture = CreateFixture(false);
            FakeWindow first = fixture.Windows.AddWindow(0x2001, 90002);
            FakeWindow second = fixture.Windows.AddWindow(0x2002, 90003);
            FakeWindow hidden = fixture.Windows.AddWindow(0x2003, 90004);
            hidden.IsVisible = false;

            fixture.Engine.Initialize(TestData.WorkDesktopId);

            Assert.Equal(2, fixture.Engine.ManagedWindowCount, "only visible windows are adopted");
            Assert.Equal(TestData.WorkDesktopId, FindManaged(fixture.Engine, 0x2001).DesktopId, "first window desktop");
            Assert.Equal(TestData.WorkDesktopId, FindManaged(fixture.Engine, 0x2002).DesktopId, "second window desktop");
            Assert.Null(FindManaged(fixture.Engine, 0x2003), "hidden window is not adopted");
            Assert.True(first.IsVisible, "adopted windows stay visible");
            Assert.True(second.IsVisible, "adopted windows stay visible");
            Assert.False(hidden.IsVisible, "hidden window stays hidden");
        }

        [Test("DesktopEngine: Initialize falls back to the first desktop for an unknown id")]
        public static void InitializeFallsBackToFirstDesktop()
        {
            Fixture fixture = CreateFixture(false);
            fixture.Windows.AddWindow(0x2101, 90005);
            fixture.Engine.Initialize("no-such-desktop");
            Assert.Equal(TestData.WorkDesktopId, fixture.Engine.CurrentDesktopId, "current desktop falls back");
            Assert.Equal(TestData.WorkDesktopId, FindManaged(fixture.Engine, 0x2101).DesktopId, "window follows fallback");
        }

        [Test("DesktopEngine: a new visible window joins the current desktop")]
        public static void NewWindowJoinsCurrentDesktop()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x1001, 90001);
            fixture.Engine.Tick();

            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "new window is managed");
            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x1001);
            Assert.NotNull(info, "window must be tracked");
            Assert.Equal(TestData.WorkDesktopId, info.DesktopId, "window belongs to the current desktop");
            Assert.True(info.IsVisible, "window stays visible on its own desktop");
            Assert.False(info.HiddenByUs, "window is not flagged as hidden");
            Assert.Equal(0, window.HideCount, "no hide call for a same-desktop window");
        }

        [Test("DesktopEngine: hidden new windows are only adopted once they become visible")]
        public static void HiddenWindowAdoptedWhenShown()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x1101, 90006);
            window.IsVisible = false;
            fixture.Engine.Tick();
            Assert.Equal(0, fixture.Engine.ManagedWindowCount, "hidden window is not adopted");

            fixture.Windows.SimulateAppShow(0x1101);
            fixture.Engine.Tick();
            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "visible window is adopted");
            Assert.Equal(TestData.WorkDesktopId, FindManaged(fixture.Engine, 0x1101).DesktopId,
                "late window joins the current desktop");
        }

        [Test("DesktopEngine: SwitchTo hides old desktop windows and restores the target desktop")]
        public static void SwitchToHidesOldAndShowsTarget()
        {
            Fixture fixture = CreateFixture();
            FakeWindow workWindow = fixture.Windows.AddWindow(0x3001, 90010);
            fixture.Engine.Tick();
            FakeWindow gameWindow = fixture.Windows.AddWindow(0x3002, 90011);
            fixture.Engine.Tick();

            Assert.True(fixture.Engine.MoveWindowToDesktop(gameWindow.Handle, TestData.GameDesktopId),
                "moving a window to another desktop must succeed");
            Assert.False(gameWindow.IsVisible, "moving a window to a non-active desktop hides it");

            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch to the game desktop");
            Assert.Equal(TestData.GameDesktopId, fixture.Engine.CurrentDesktopId, "engine current desktop updated");
            Assert.Equal(TestData.GameDesktopId, fixture.Config.ActiveDesktopId, "config active desktop updated");
            Assert.False(workWindow.IsVisible, "old desktop window is hidden");
            Assert.True(workWindow.HideCount > 0, "old desktop window received a Hide call");
            Assert.True(gameWindow.IsVisible, "target desktop window is restored");
            Assert.True(FindManaged(fixture.Engine, 0x3001).HiddenByUs, "hidden window is flagged");
            Assert.False(FindManaged(fixture.Engine, 0x3002).HiddenByUs, "shown window is not flagged");

            Assert.True(fixture.Engine.SwitchTo(TestData.WorkDesktopId), "switch back to the work desktop");
            Assert.True(workWindow.IsVisible, "work window is restored on switch back");
            Assert.False(gameWindow.IsVisible, "game window is hidden on switch back");
            Assert.Equal(TestData.WorkDesktopId, fixture.Config.ActiveDesktopId, "active desktop follows switch back");
        }

        [Test("DesktopEngine: switching to the current desktop is a no-op, unknown desktops fail")]
        public static void SwitchToEdgeCases()
        {
            Fixture fixture = CreateFixture();
            int switched = 0;
            fixture.Engine.DesktopSwitched += delegate { switched++; };
            FakeWindow window = fixture.Windows.AddWindow(0x3101, 90012);
            fixture.Engine.Tick();

            Assert.False(fixture.Engine.SwitchTo("no-such-desktop"), "unknown desktop id returns false");
            Assert.Equal(TestData.WorkDesktopId, fixture.Engine.CurrentDesktopId, "current desktop unchanged");
            Assert.Equal(0, switched, "no switch event for an unknown desktop");

            Assert.True(fixture.Engine.SwitchTo(TestData.WorkDesktopId), "switching to the current desktop returns true");
            Assert.Equal(0, switched, "no switch event for the current desktop");
            Assert.True(window.IsVisible, "window stays visible");
        }

        [Test("DesktopEngine: a new owned window follows its owner's desktop")]
        public static void OwnedWindowFollowsOwnerDesktop()
        {
            Fixture fixture = CreateFixture();
            FakeWindow owner = fixture.Windows.AddWindow(0x4001, 90020);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away from the owner desktop");
            Assert.False(owner.IsVisible, "owner window is hidden after the switch");

            FakeWindow child = fixture.Windows.AddWindow(0x4002, 90021);
            child.Owner = owner.Handle;
            fixture.Engine.Tick();

            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x4002);
            Assert.NotNull(info, "owned window is managed");
            Assert.Equal(TestData.WorkDesktopId, info.DesktopId, "owned window follows the owner desktop");
            Assert.False(child.IsVisible, "owned window on a hidden desktop is hidden immediately");
        }

        [Test("DesktopEngine: a new window of an already-seen process follows that process desktop")]
        public static void SameProcessWindowFollowsProcessDesktop()
        {
            Fixture fixture = CreateFixture();
            FakeWindow first = fixture.Windows.AddWindow(0x4101, 90030);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away from the first window desktop");

            FakeWindow second = fixture.Windows.AddWindow(0x4102, 90030);
            fixture.Engine.Tick();

            ManagedWindowInfo info = FindManaged(fixture.Engine, 0x4102);
            Assert.NotNull(info, "second window of the same process is managed");
            Assert.Equal(TestData.WorkDesktopId, info.DesktopId, "second window follows the process desktop");
            Assert.False(second.IsVisible, "second window is hidden together with its process group");
            Assert.False(first.IsVisible, "first window stays hidden");
        }

        [Test("DesktopEngine: a foreign-desktop window re-shown by the app is hidden again on Tick")]
        public static void ReshownForeignWindowIsHiddenAgain()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x5001, 90040);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away");
            Assert.False(window.IsVisible, "window hidden after the switch");

            fixture.Windows.SimulateAppShow(window.Handle.ToInt64());
            Assert.True(window.IsVisible, "test simulates the application re-showing its window");
            int hidesBefore = window.HideCount;

            fixture.Engine.Tick();
            Assert.False(window.IsVisible, "engine hides the foreign-desktop window again");
            Assert.True(window.HideCount > hidesBefore, "HideWindow called again");
        }

        [Test("DesktopEngine: pause restores every window, resume isolates desktops again")]
        public static void PauseShowsAllAndResumeReisolates()
        {
            Fixture fixture = CreateFixture();
            FakeWindow workWindow = fixture.Windows.AddWindow(0x6001, 90050);
            fixture.Engine.Tick();
            FakeWindow gameWindow = fixture.Windows.AddWindow(0x6002, 90051);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.MoveWindowToDesktop(gameWindow.Handle, TestData.GameDesktopId),
                "move the second window to the game desktop");
            Assert.False(gameWindow.IsVisible, "moved window is hidden");

            fixture.Engine.SetPaused(true);
            Assert.True(fixture.Engine.Paused, "engine reports paused");
            Assert.True(gameWindow.IsVisible, "pause restores hidden windows");
            Assert.True(workWindow.IsVisible, "pause keeps visible windows visible");
            Assert.False(FindManaged(fixture.Engine, 0x6002).HiddenByUs, "pause clears the hidden flag");

            fixture.Engine.Tick();
            Assert.True(gameWindow.IsVisible, "tick while paused never hides windows");
            Assert.True(workWindow.IsVisible, "tick while paused keeps the active window");

            fixture.Engine.SetPaused(false);
            Assert.False(fixture.Engine.Paused, "engine reports resumed");
            fixture.Engine.Tick();
            Assert.False(gameWindow.IsVisible, "resume hides the non-active desktop again");
            Assert.True(workWindow.IsVisible, "resume keeps the active desktop visible");
        }

        [Test("DesktopEngine: RestoreAllWindows shows every hidden window")]
        public static void RestoreAllWindowsShowsHidden()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x7001, 90060);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away");
            Assert.False(window.IsVisible, "window hidden");
            Assert.NotNull(fixture.Journal.LastSaved, "hidden window is journaled");

            fixture.Engine.RestoreAllWindows();
            Assert.True(window.IsVisible, "RestoreAllWindows shows the hidden window");
            Assert.False(FindManaged(fixture.Engine, 0x7001).HiddenByUs, "hidden flag cleared");
            Assert.True(window.ShowCount > 0, "ShowWindow was called");
            Assert.Null(fixture.Journal.LastSaved, "journal is cleared once nothing is hidden");
        }

        [Test("DesktopEngine: Shutdown restores all windows")]
        public static void ShutdownRestoresAll()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x7101, 90061);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away");
            Assert.False(window.IsVisible, "window hidden");

            fixture.Engine.Shutdown();
            Assert.True(window.IsVisible, "Shutdown restores hidden windows so user apps are not lost");
        }

        [Test("DesktopEngine: ForgetWindow restores and drops management")]
        public static void ForgetWindowRestoresAndDrops()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0x8001, 90070);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away");
            Assert.False(window.IsVisible, "window hidden");
            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "window managed");

            fixture.Engine.ForgetWindow(window.Handle);
            Assert.True(window.IsVisible, "forget restores a window that we hid");
            Assert.Equal(0, fixture.Engine.ManagedWindowCount, "window is no longer managed");
            Assert.Null(FindManaged(fixture.Engine, 0x8001), "window lookup returns null");
        }

        [Test("DesktopEngine: windows of the own process are ignored")]
        public static void OwnProcessWindowsAreIgnored()
        {
            Fixture fixture = CreateFixture(false);
            AppConfig config = fixture.Config;
            FakeWindowSystem windows = new FakeWindowSystem();
            FakeJournal journal = new FakeJournal();
            DesktopEngine engine = new DesktopEngine(config, windows, journal, 4242);
            engine.Initialize(TestData.WorkDesktopId);

            windows.AddWindow(0x9001, 4242);
            windows.AddWindow(0x9002, 4343);
            engine.Tick();

            Assert.Equal(1, engine.ManagedWindowCount, "only the foreign process window is managed");
            Assert.Null(FindManaged(engine, 0x9001), "own process window is skipped");
            Assert.NotNull(FindManaged(engine, 0x9002), "foreign process window is managed");
        }

        [Test("DesktopEngine: shell, cloaked, sizeless and classless windows are skipped")]
        public static void UnmanageableWindowsAreSkipped()
        {
            Fixture fixture = CreateFixture();
            FakeWindow shell = fixture.Windows.AddWindow(0xA001, 90080);
            shell.ClassName = "Progman";
            FakeWindow cloaked = fixture.Windows.AddWindow(0xA002, 90081);
            cloaked.IsCloaked = true;
            FakeWindow sizeless = fixture.Windows.AddWindow(0xA003, 90082);
            sizeless.HasVisibleSize = false;
            FakeWindow classless = fixture.Windows.AddWindow(0xA004, 90083);
            classless.ClassName = string.Empty;
            FakeWindow normal = fixture.Windows.AddWindow(0xA005, 90084);

            fixture.Engine.Tick();

            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "only the normal window is managed");
            Assert.NotNull(FindManaged(fixture.Engine, 0xA005), "normal window is managed");
            Assert.Null(FindManaged(fixture.Engine, 0xA001), "shell window is skipped");
            Assert.Null(FindManaged(fixture.Engine, 0xA002), "cloaked window is skipped");
            Assert.Null(FindManaged(fixture.Engine, 0xA003), "window without visible size is skipped");
            Assert.Null(FindManaged(fixture.Engine, 0xA004), "window without class name is skipped");
        }

        [Test("DesktopEngine: WindowFilter can exclude windows from management")]
        public static void WindowFilterExcludesWindows()
        {
            Fixture fixture = CreateFixture();
            fixture.Engine.WindowFilter = delegate(WindowInfo info) { return info.ProcessId != 90090; };
            fixture.Windows.AddWindow(0xB001, 90090);
            fixture.Windows.AddWindow(0xB002, 90091);
            fixture.Engine.Tick();

            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "filtered window is not managed");
            Assert.Null(FindManaged(fixture.Engine, 0xB001), "filtered window is absent");
            Assert.NotNull(FindManaged(fixture.Engine, 0xB002), "passing window is managed");
        }

        [Test("DesktopEngine: popups owned by the shell are not managed")]
        public static void ShellOwnedPopupIsIgnored()
        {
            Fixture fixture = CreateFixture();
            FakeWindow shell = fixture.Windows.AddWindow(0xC001, 90092);
            shell.ClassName = "Progman";
            FakeWindow menu = fixture.Windows.AddWindow(0xC002, 90093);
            menu.Owner = shell.Handle;
            menu.ClassName = "FakeContextMenu";

            fixture.Engine.Tick();

            Assert.Equal(0, fixture.Engine.ManagedWindowCount, "shell and shell-owned popup are ignored");
        }

        [Test("DesktopEngine: hidden windows are journaled with the current boot marker")]
        public static void JournalRecordsHiddenWindows()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0xD001, 90100);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away");

            SessionJournal journal = fixture.Journal.LastSaved;
            Assert.NotNull(journal, "journal exists while a window is hidden");
            Assert.Equal(1, journal.Windows.Count, "one hidden window is journaled");
            JournalWindow entry = journal.Windows[0];
            Assert.Equal(window.Handle.ToInt64(), entry.Handle, "journaled handle");
            Assert.Equal(90100, entry.ProcessId, "journaled process id");
            Assert.Equal(TestData.WorkDesktopId, entry.DesktopId, "journaled desktop id");
            Assert.True(journal.IsFromCurrentBoot(), "journal carries the current boot marker");
        }

        [Test("DesktopEngine: journal writes can be disabled")]
        public static void JournalDisabledDoesNotWrite()
        {
            Fixture fixture = CreateFixture();
            fixture.Engine.JournalEnabled = false;
            FakeWindow window = fixture.Windows.AddWindow(0xD101, 90101);
            fixture.Engine.Tick();
            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "switch away");
            Assert.False(window.IsVisible, "window is still hidden without journaling");
            Assert.Null(fixture.Journal.LastSaved, "no journal is written when disabled");
            Assert.Equal(0, fixture.Journal.SaveCount, "journal Save not called");
        }

        [Test("DesktopEngine: RecoverFromPreviousSession restores matching pids and clears the journal")]
        public static void RecoverFromPreviousSession()
        {
            Fixture fixture = CreateFixture(false);
            FakeWindow matching = fixture.Windows.AddWindow(0xE001, 90200);
            matching.IsVisible = false;
            FakeWindow pidMismatch = fixture.Windows.AddWindow(0xE002, 90201);
            pidMismatch.IsVisible = false;
            FakeWindow alreadyVisible = fixture.Windows.AddWindow(0xE003, 90202);

            SessionJournal journal = new SessionJournal();
            journal.BootMarker = SessionJournal.CurrentBootMarker();
            journal.Windows.Add(MakeJournalWindow(0xE001, 90200));
            journal.Windows.Add(MakeJournalWindow(0xE002, 99999)); // stale pid: must not be restored
            journal.Windows.Add(MakeJournalWindow(0xE003, 90202)); // already visible: not counted
            fixture.Journal.LastSaved = journal;

            int restored = fixture.Engine.RecoverFromPreviousSession();

            Assert.Equal(1, restored, "only the pid-matching hidden window is restored");
            Assert.True(matching.IsVisible, "matching window restored");
            Assert.False(pidMismatch.IsVisible, "pid mismatch is not touched");
            Assert.True(alreadyVisible.IsVisible, "already visible window stays visible");
            Assert.Null(fixture.Journal.LastSaved, "journal is cleared after recovery");
        }

        [Test("DesktopEngine: RecoverFromPreviousSession ignores journals from another boot")]
        public static void RecoverIgnoresStaleJournal()
        {
            Fixture fixture = CreateFixture(false);
            FakeWindow window = fixture.Windows.AddWindow(0xE101, 90210);
            window.IsVisible = false;

            SessionJournal journal = new SessionJournal();
            journal.BootMarker = SessionJournal.CurrentBootMarker() - 120; // about two hours ago
            journal.Windows.Add(MakeJournalWindow(0xE101, 90210));
            fixture.Journal.LastSaved = journal;

            int restored = fixture.Engine.RecoverFromPreviousSession();

            Assert.Equal(0, restored, "stale journal restores nothing");
            Assert.False(window.IsVisible, "stale journal must not touch windows");
            Assert.NotNull(fixture.Journal.LastSaved, "stale journal is kept for diagnosis");
        }

        [Test("DesktopEngine: MoveWindowToDesktop validates desktop and window ids")]
        public static void MoveWindowToDesktopValidates()
        {
            Fixture fixture = CreateFixture();
            FakeWindow window = fixture.Windows.AddWindow(0xF001, 90300);
            fixture.Engine.Tick();

            Assert.False(fixture.Engine.MoveWindowToDesktop(window.Handle, "no-such-desktop"),
                "unknown desktop is rejected");
            Assert.False(fixture.Engine.MoveWindowToDesktop(new IntPtr(0xDEAD), TestData.GameDesktopId),
                "unknown window is rejected");
            Assert.True(window.IsVisible, "rejected moves do not change visibility");

            Assert.True(fixture.Engine.MoveWindowToDesktop(window.Handle, TestData.GameDesktopId),
                "move to a non-active desktop succeeds");
            Assert.False(window.IsVisible, "moved-away window is hidden");
            Assert.True(fixture.Engine.MoveWindowToDesktop(window.Handle, TestData.WorkDesktopId),
                "move back to the active desktop succeeds");
            Assert.True(window.IsVisible, "window is shown again on the active desktop");
        }

        [Test("DesktopEngine: DesktopSwitched fires once per real switch")]
        public static void DesktopSwitchedEventIsRaised()
        {
            Fixture fixture = CreateFixture();
            int switched = 0;
            fixture.Engine.DesktopSwitched += delegate { switched++; };
            fixture.Windows.AddWindow(0xF101, 90301);
            fixture.Engine.Tick();

            Assert.True(fixture.Engine.SwitchTo(TestData.GameDesktopId), "first switch");
            Assert.Equal(1, switched, "one event for the first switch");
            Assert.True(fixture.Engine.SwitchTo(TestData.WorkDesktopId), "second switch");
            Assert.Equal(2, switched, "one event for the second switch");
        }

        [Test("DesktopEngine: WindowsChanged fires when the managed set grows")]
        public static void WindowsChangedEventIsRaised()
        {
            Fixture fixture = CreateFixture();
            int changed = 0;
            fixture.Engine.WindowsChanged += delegate { changed++; };
            fixture.Windows.AddWindow(0xF201, 90302);
            fixture.Engine.Tick();
            Assert.True(changed > 0, "WindowsChanged is raised when a new window is adopted");
        }

        [Test("DesktopEngine: destroyed windows are dropped from management")]
        public static void DeadWindowsAreDropped()
        {
            Fixture fixture = CreateFixture();
            fixture.Windows.AddWindow(0xF301, 90303);
            fixture.Engine.Tick();
            Assert.Equal(1, fixture.Engine.ManagedWindowCount, "window managed before destruction");

            fixture.Windows.RemoveWindow(0xF301);
            fixture.Engine.Tick();
            Assert.Equal(0, fixture.Engine.ManagedWindowCount, "destroyed window is forgotten");
        }

        [Test("DesktopEngine: constructor rejects null dependencies")]
        public static void ConstructorRejectsNulls()
        {
            AppConfig config = AppConfig.CreateDefault();
            FakeWindowSystem windows = new FakeWindowSystem();
            FakeJournal journal = new FakeJournal();
            bool threw = false;
            try
            {
                new DesktopEngine(null, windows, journal, -1);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }
            Assert.True(threw, "null config must throw ArgumentNullException");

            threw = false;
            try
            {
                new DesktopEngine(config, null, journal, -1);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }
            Assert.True(threw, "null window system must throw ArgumentNullException");

            DesktopEngine engine = new DesktopEngine(config, windows, null, -1);
            Assert.NotNull(engine, "null journal is allowed (journaling disabled)");
            Assert.Equal(0, config.IndexOfDesktop(config.ActiveDesktopId),
                "default config activates the first desktop");
        }

        private static JournalWindow MakeJournalWindow(long handle, int processId)
        {
            JournalWindow entry = new JournalWindow();
            entry.Handle = handle;
            entry.ProcessId = processId;
            return entry;
        }
    }
}
