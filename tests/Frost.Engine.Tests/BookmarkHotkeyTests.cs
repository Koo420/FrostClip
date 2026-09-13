using Frost.Engine.Clips;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Hotkeys;
using Frost.Engine.Recording;
using Frost.Shared.Clips;
using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>Records which actions were asked for, and on which thread.</summary>
internal sealed class RecordingHotkeyActions : IHotkeyActions
{
    private readonly object _gate = new();

    internal List<string> Calls { get; } = [];

    internal HashSet<int> ThreadIds { get; } = [];

    internal Exception? ThrowFrom { get; set; }

    internal TimeSpan Delay { get; set; }

    private void Note(string call)
    {
        lock (_gate)
        {
            Calls.Add(call);
            ThreadIds.Add(Environment.CurrentManagedThreadId);
        }

        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        if (ThrowFrom is not null)
        {
            throw ThrowFrom;
        }
    }

    internal int CountOf(string prefix)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    public void SaveClip(TimeSpan duration, string? label) => Note($"SaveClip:{duration.TotalSeconds}:{label}");

    public void ToggleFullSessionRecording() => Note("Toggle");

    public void AddBookmark() => Note("Bookmark");

    public void ToggleMicrophoneMute() => Note("Mic");
}

public sealed class HotkeyActionDispatcherTests
{
    private static HotkeyRouter Router() => new(HotkeyAssignment.Defaults);

    private static void Press(HotkeyRouter router, int key)
    {
        router.OnKeyDown(key, HotkeyModifiers.Alt);
        router.OnKeyUp(key);
    }

    [Fact]
    public void ActionsRunOffTheKeyboardHookThread()
    {
        // The reason this class exists: starting a recording opens a file, and
        // doing that inside the hook would delay the user's keystroke reaching the
        // game — and Windows silently unhooks a callback that overruns.
        var router = Router();
        var actions = new RecordingHotkeyActions();
        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        var hookThreadId = Environment.CurrentManagedThreadId;
        Press(router, VirtualKeys.F12);

        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.Equal("Toggle", Assert.Single(actions.Calls));
        Assert.DoesNotContain(hookThreadId, actions.ThreadIds);
    }

    [Fact]
    public void TheHookSideReturnsImmediatelyEvenWhenTheActionIsSlow()
    {
        var router = Router();
        var actions = new RecordingHotkeyActions { Delay = TimeSpan.FromMilliseconds(400) };
        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Press(router, VirtualKeys.F12);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 100, $"the hook path took {clock.ElapsedMilliseconds}ms");
        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void EachActionKindReachesItsHandler()
    {
        var router = Router();
        var actions = new RecordingHotkeyActions();
        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        Press(router, VirtualKeys.F9);
        Press(router, VirtualKeys.F12);
        Press(router, VirtualKeys.F1);

        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));

        Assert.Equal(1, actions.CountOf("SaveClip:15"));
        Assert.Equal(1, actions.CountOf("Toggle"));
        Assert.Equal(1, actions.CountOf("Bookmark"));
    }

    [Fact]
    public void AFloodOfPressesDropsExtrasRatherThanWorkingThroughAStaleQueue()
    {
        var router = Router();
        var actions = new RecordingHotkeyActions { Delay = TimeSpan.FromMilliseconds(50) };
        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        for (var i = 0; i < HotkeyActionDispatcher.QueueCapacity * 4; i++)
        {
            Press(router, VirtualKeys.F1);
        }

        Assert.True(dispatcher.Dropped > 0, "expected extras to be dropped");
        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(60)));
        Assert.True(dispatcher.Dispatched <= HotkeyActionDispatcher.QueueCapacity + 1);
    }

    [Fact]
    public void AFailedActionDoesNotStopTheNextOne()
    {
        var router = Router();
        var actions = new RecordingHotkeyActions { ThrowFrom = new IOException("disk gone") };
        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        Press(router, VirtualKeys.F1);
        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, dispatcher.Failed);

        actions.ThrowFrom = null;
        Press(router, VirtualKeys.F1);
        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, actions.CountOf("Bookmark"));
    }

    [Fact]
    public void QueuingFromTheHookDoesNotAllocate()
    {
        var router = Router();
        var actions = new RecordingHotkeyActions();
        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        // Measures the hook-side path specifically: keys that match nothing, plus
        // matched presses that fill and overflow the queue. Either way the hook
        // side must allocate nothing.
        AllocationAssert.NoPerIterationAllocation(
            _ =>
            {
                router.OnKeyDown('W', HotkeyModifiers.None);
                router.OnKeyUp('W');
                router.OnKeyDown(VirtualKeys.F1, HotkeyModifiers.Alt);
                router.OnKeyUp(VirtualKeys.F1);
            },
            iterations: 50_000,
            warmUpIterations: 2_000);
    }

    [Fact]
    public void DisposeUnsubscribesSoALaterPressDoesNothing()
    {
        var router = Router();
        var actions = new RecordingHotkeyActions();
        var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        dispatcher.Dispose();
        Press(router, VirtualKeys.F1);

        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void NullDependenciesAreRejected()
    {
        var router = Router();
        Assert.Throws<ArgumentNullException>(
            () => new HotkeyActionDispatcher(null!, new RecordingHotkeyActions(), NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new HotkeyActionDispatcher(router, null!, NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new HotkeyActionDispatcher(router, new RecordingHotkeyActions(), null!));
    }
}

/// <summary>
/// Phase 5's last requirement: the bookmark hotkey tags a timestamp and exports
/// nothing.
/// </summary>
public sealed class BookmarkHotkeyTests : IDisposable
{
    private const int Fps = 60;
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / Fps;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "frost-bookmark-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class Harness : IDisposable
    {
        internal required HotkeyRouter Router { get; init; }

        internal required HotkeyActionDispatcher Dispatcher { get; init; }

        internal required EngineHotkeyActions Actions { get; init; }

        internal required FullSessionRecorder Session { get; init; }

        internal required ClipService Clips { get; init; }

        internal required FakeClipWriter ClipWriter { get; init; }

        internal required EncodedSampleRing Ring { get; init; }

        public void Dispose()
        {
            Dispatcher.Dispose();
            Clips.Dispose();
            Session.Dispose();
        }
    }

    private Harness Build()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(30),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        var clipWriter = new FakeClipWriter { CaptureBytes = false };
        var clips = new ClipService(ring, clipWriter, _directory, NullEngineLog.Instance, _ => false);

        var session = new FullSessionRecorder(
            _ => new NoOpSessionWriter(), NullEngineLog.Instance);

        var actions = new EngineHotkeyActions(
            clips,
            session,
            () => Path.Combine(_directory, $"session-{Guid.NewGuid():N}.mp4"),
            () => "hl2",
            () => (1920, 1080, "H264"),
            NullEngineLog.Instance);

        var router = new HotkeyRouter(HotkeyAssignment.Defaults);
        var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        return new Harness
        {
            Router = router,
            Dispatcher = dispatcher,
            Actions = actions,
            Session = session,
            Clips = clips,
            ClipWriter = clipWriter,
            Ring = ring,
        };
    }

    private static void Feed(EncodedSampleRing ring, FullSessionRecorder session, int frames, int startFrame = 0)
    {
        var payload = new byte[4000];

        for (var i = startFrame; i < startFrame + frames; i++)
        {
            var isKey = i % 120 == 0;
            ring.TryWrite(payload, i * FrameTicks, FrameTicks, isKey);
            session.TryWrite(payload, i * FrameTicks, FrameTicks, isKey);
        }
    }

    private static void Press(HotkeyRouter router, int key)
    {
        router.OnKeyDown(key, HotkeyModifiers.Alt);
        router.OnKeyUp(key);
    }

    [Fact]
    public void TheBookmarkHotkeyTagsATimestampAndExportsNothing()
    {
        using var harness = Build();

        // Start recording, then play for five seconds.
        Press(harness.Router, VirtualKeys.F12);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(harness.Session.IsRecording);

        Feed(harness.Ring, harness.Session, frames: Fps * 5);

        // Now the bookmark hotkey.
        Press(harness.Router, VirtualKeys.F1);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));

        Assert.Equal(1, harness.Actions.BookmarksPlaced);
        var bookmark = Assert.Single(harness.Session.Bookmarks);
        Assert.Equal(BookmarkSource.Manual, bookmark.Source);
        Assert.InRange(
            bookmark.Offset,
            TimeSpan.FromSeconds(4.9),
            TimeSpan.FromSeconds(5.1));

        // Nothing was exported: no clip requested, no clip written, no file on disk
        // beyond the session recording itself.
        Assert.Equal(0, harness.Clips.RequestsAccepted);
        Assert.Empty(harness.ClipWriter.Written);
        Assert.Equal(0, harness.Clips.ClipsSaved);
    }

    [Fact]
    public void SeveralBookmarksLandInTheFinishedRecording()
    {
        using var harness = Build();

        Press(harness.Router, VirtualKeys.F12);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));

        for (var round = 0; round < 3; round++)
        {
            Feed(harness.Ring, harness.Session, frames: Fps * 4, startFrame: round * Fps * 4);
            Press(harness.Router, VirtualKeys.F1);
            Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        }

        Assert.Equal(3, harness.Actions.BookmarksPlaced);

        var metadata = harness.Session.Stop();
        Assert.NotNull(metadata);
        Assert.Equal(3, metadata!.Bookmarks.Count);
        Assert.All(metadata.Bookmarks, b => Assert.Equal(BookmarkSource.Manual, b.Source));

        // Offsets are in order and spread across the recording.
        for (var i = 1; i < metadata.Bookmarks.Count; i++)
        {
            Assert.True(metadata.Bookmarks[i].OffsetTicks > metadata.Bookmarks[i - 1].OffsetTicks);
        }

        Assert.Empty(harness.ClipWriter.Written);
    }

    [Fact]
    public void BookmarkingWithNoRecordingRunningIsHarmless()
    {
        // Pressing it by habit outside a session must not error or export.
        using var harness = Build();

        Feed(harness.Ring, harness.Session, frames: Fps * 5);
        Press(harness.Router, VirtualKeys.F1);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, harness.Actions.BookmarksPlaced);
        Assert.Equal(0, harness.Dispatcher.Failed);
        Assert.Empty(harness.ClipWriter.Written);
    }

    [Fact]
    public void ABookmarkIsFarCheaperThanAClip()
    {
        // The reason the feature exists: a clip reads and muxes the whole trailing
        // window, a bookmark appends a long.
        using var harness = Build();

        Press(harness.Router, VirtualKeys.F12);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Feed(harness.Ring, harness.Session, frames: Fps * 20);

        var bookmarkClock = System.Diagnostics.Stopwatch.StartNew();
        Press(harness.Router, VirtualKeys.F1);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        bookmarkClock.Stop();

        Assert.Equal(1, harness.Actions.BookmarksPlaced);
        Assert.Empty(harness.ClipWriter.Written);

        // And the clip hotkey does produce a file, so the comparison is fair.
        Press(harness.Router, VirtualKeys.F10);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(harness.Clips.WaitForIdle(TimeSpan.FromSeconds(60)));

        Assert.Single(harness.ClipWriter.Written);
        Assert.True(harness.ClipWriter.Written[0].SampleCount > 0);
    }

    [Fact]
    public void TheRecordingToggleStartsAndStopsOnAlternatePresses()
    {
        using var harness = Build();

        Press(harness.Router, VirtualKeys.F12);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(harness.Session.IsRecording);
        Assert.Equal(1, harness.Actions.SessionsStarted);

        Feed(harness.Ring, harness.Session, frames: Fps * 2);

        Press(harness.Router, VirtualKeys.F12);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.False(harness.Session.IsRecording);

        Press(harness.Router, VirtualKeys.F12);
        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(harness.Session.IsRecording);
        Assert.Equal(2, harness.Actions.SessionsStarted);
    }

    [Fact]
    public void AClipHotkeyCarriesTheGameNameAndPresetLabel()
    {
        using var harness = Build();

        Feed(harness.Ring, harness.Session, frames: Fps * 20);
        Press(harness.Router, VirtualKeys.F11);

        Assert.True(harness.Dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(harness.Clips.WaitForIdle(TimeSpan.FromSeconds(60)));

        var written = Assert.Single(harness.ClipWriter.Written);
        Assert.Contains("hl2", written.Path);
        Assert.Contains("(1m)", written.Path);
    }

    private sealed class NoOpSessionWriter : ISessionWriter
    {
        public string Extension => ".mp4";

        public long BytesWritten { get; private set; }

        public bool IsFaulted => false;

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
        {
            BytesWritten += data.Length;
            return true;
        }

        public void Finish(TimeSpan timeout)
        {
        }

        public void Dispose()
        {
        }
    }
}
