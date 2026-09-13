using Frost.Engine.Diagnostics;
using Frost.Engine.Ipc;
using Frost.Shared.Ipc;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// Groups the tests that use real named pipes.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel, and these tests are timing-sensitive:
/// they connect over real pipes with real timeouts, and one of them deliberately
/// saturates the thread pool. Run in parallel they starve each other's async
/// continuations and fail in unrelated places. A shared collection makes them
/// sequential, which is what a transport test wants anyway.
/// </remarks>
[CollectionDefinition("ipc", DisableParallelization = true)]
public sealed class IpcTestCollection;

/// <summary>
/// End-to-end over a real named pipe: real server, real client, real framing.
/// </summary>
/// <remarks>
/// Named pipes work on Linux too (as Unix domain sockets), so unlike most of
/// Frost's Windows surface this channel is genuinely executed by the suite rather
/// than only type-checked.
/// </remarks>
[Collection("ipc")]
public sealed class IpcRoundTripTests
{
    /// <summary>Unique per test, so tests can run in parallel without colliding.</summary>
    private static string UniquePipeName() => $"frost-test-{Guid.NewGuid():N}";

    private sealed class Harness : IAsyncDisposable
    {
        internal required FakeEngineCommands Commands { get; init; }

        internal required IpcServer Server { get; init; }

        internal required IpcClient Client { get; init; }

        internal static async Task<Harness> StartAsync(FakeEngineCommands? commands = null)
        {
            var pipeName = UniquePipeName();
            var fake = commands ?? new FakeEngineCommands();
            var server = new IpcServer(fake, NullEngineLog.Instance, pipeName);
            server.Start();

            var client = new IpcClient(pipeName);
            await client.ConnectAsync();

            return new Harness { Commands = fake, Server = server, Client = client };
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StatusRoundTripsWithEveryFieldIntact()
    {
        await using var harness = await Harness.StartAsync();

        var status = await harness.Client.GetStatusAsync();

        Assert.True(status.IsArmed);
        Assert.Equal(31.5, status.BufferedSeconds);
        Assert.Equal(60, status.MaxClipSeconds);
        Assert.Equal(48_000_000, status.BufferBytes);
        Assert.Equal(1920, status.CaptureWidth);
        Assert.Equal(1080, status.CaptureHeight);
        Assert.Equal(60, status.CaptureFps);
        Assert.Equal("NVIDIA H.264 Encoder MFT", status.EncoderName);
        Assert.Equal("H264", status.Codec);
        Assert.Equal(12_400_000, status.BitsPerSecond);
        Assert.Equal(12_345, status.FramesCaptured);
        Assert.Equal(3, status.ClipsSaved);
        Assert.Equal(205.5, status.UptimeSeconds);
        Assert.False(status.IsRecordingSession);
    }

    [Fact]
    public async Task AClipRequestReachesTheEngine()
    {
        await using var harness = await Harness.StartAsync();

        await harness.Client.SaveClipAsync(TimeSpan.FromSeconds(30), "30s");

        var request = Assert.Single(harness.Commands.ClipRequests);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Duration);
        Assert.Equal("30s", request.Label);
    }

    [Fact]
    public async Task ARefusedClipSurfacesTheEnginesReason()
    {
        await using var harness = await Harness.StartAsync();
        harness.Commands.RefuseClipsBecause = "Nothing to clip yet — the buffer is still filling.";

        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => harness.Client.SaveClipAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("still filling", ex.Message);
        Assert.Empty(harness.Commands.ClipRequests);
    }

    [Fact]
    public async Task FullSessionRecordingTogglesAndReportsTheNewState()
    {
        await using var harness = await Harness.StartAsync();

        Assert.True(await harness.Client.ToggleFullSessionRecordingAsync());
        Assert.True(harness.Commands.IsRecording);
        Assert.True((await harness.Client.GetStatusAsync()).IsRecordingSession);

        Assert.False(await harness.Client.ToggleFullSessionRecordingAsync());
        Assert.False(harness.Commands.IsRecording);
    }

    [Fact]
    public async Task BookmarksReachTheEngine()
    {
        await using var harness = await Harness.StartAsync();

        await harness.Client.AddBookmarkAsync();
        await harness.Client.AddBookmarkAsync();

        Assert.Equal(2, harness.Commands.BookmarkCount);
    }

    [Fact]
    public async Task SettingsRoundTripThroughTheChannel()
    {
        await using var harness = await Harness.StartAsync();

        var fetched = await harness.Client.GetSettingsAsync();
        Assert.Equal(60, fetched.Capture.Fps);
        Assert.NotEmpty(fetched.Hotkeys);

        var changed = fetched with
        {
            Capture = fetched.Capture with { Fps = 144 },
            Appearance = fetched.Appearance with { AccentColor = "#FF8800" },
        };

        await harness.Client.ApplySettingsAsync(changed);

        var applied = Assert.Single(harness.Commands.AppliedSettings);
        Assert.Equal(144, applied.Capture.Fps);
        Assert.Equal("#FF8800", applied.Appearance.AccentColor);

        // And the hotkey list survived the trip in a usable form, not just as text.
        Assert.NotEmpty(applied.ResolveHotkeys());
        Assert.Equal("Alt+F10", applied.Hotkeys[1].Binding);
    }

    [Fact]
    public async Task DisplaysAndWindowsAndClipsRoundTrip()
    {
        await using var harness = await Harness.StartAsync();

        var displays = await harness.Client.ListDisplaysAsync();
        Assert.Equal(2, displays.Count);
        Assert.Equal("Dell AW3423DW", displays[0].FriendlyName);
        Assert.True(displays[0].IsPrimary);

        var windows = await harness.Client.ListWindowsAsync();
        Assert.Equal("Half-Life 2", Assert.Single(windows).Title);

        var clips = await harness.Client.ListClipsAsync();
        var clip = Assert.Single(clips);
        Assert.Equal(TimeSpan.FromSeconds(30), clip.Duration);
        Assert.Equal("the shot", Assert.Single(clip.Bookmarks).Label);
    }

    [Fact]
    public async Task NotificationsArriveWithoutBeingAskedFor()
    {
        await using var harness = await Harness.StartAsync();

        var received = new List<IpcMessage>();
        using var arrived = new SemaphoreSlim(0);
        harness.Client.NotificationReceived += message =>
        {
            lock (received)
            {
                received.Add(message);
            }

            arrived.Release();
        };

        var clip = harness.Commands.ListClips()[0];
        Assert.True(await harness.Server.NotifyAsync(IpcMessage.ClipSavedNotification(clip)));
        Assert.True(await harness.Server.NotifyAsync(
            IpcMessage.HotkeyFiredNotification("Alt+F10", "SaveClip", "30s")));
        Assert.True(await harness.Server.NotifyAsync(IpcMessage.RecordingStateNotification(true)));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), $"notification {i + 1} never arrived");
        }

        lock (received)
        {
            Assert.Equal(3, received.Count);
            Assert.Equal(IpcMessageKind.ClipSaved, received[0].Kind);
            Assert.Equal(clip.FilePath, received[0].Clip!.FilePath);
            Assert.Equal("Alt+F10", received[1].Hotkey!.Binding);
            Assert.True(received[2].IsRecordingSession);
        }
    }

    [Fact]
    public async Task ANotificationArrivingMidRequestIsNotMistakenForTheReply()
    {
        // The bug that makes hand-rolled request/response channels intermittently
        // return the wrong answer: matching by arrival order instead of by id.
        await using var harness = await Harness.StartAsync();

        var notifications = 0;
        harness.Client.NotificationReceived += _ => Interlocked.Increment(ref notifications);

        var noise = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await harness.Server.NotifyAsync(IpcMessage.RecordingStateNotification(i % 2 == 0));
            }
        });

        for (var i = 0; i < 50; i++)
        {
            var status = await harness.Client.GetStatusAsync();
            Assert.Equal(1920, status.CaptureWidth);
        }

        await noise;
        Assert.True(Volatile.Read(ref notifications) > 0, "no notifications were interleaved");
    }

    [Fact]
    public async Task ManyRequestsInFlightEachGetTheirOwnReply()
    {
        await using var harness = await Harness.StartAsync();

        var tasks = Enumerable.Range(0, 40)
            .Select(_ => harness.Client.GetStatusAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, status => Assert.Equal("H264", status.Codec));
        Assert.Equal(40, harness.Server.MessagesReceived);
    }

    [Fact]
    public async Task AHandlerThatThrowsBecomesAFailureNotADeadChannel()
    {
        await using var harness = await Harness.StartAsync();
        harness.Commands.ThrowFromHandlers = new InvalidOperationException("the encoder fell over");

        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => harness.Client.GetStatusAsync());
        Assert.Contains("the encoder fell over", ex.Message);
        Assert.Equal(1, harness.Server.HandlerFailures);

        // And the channel still works afterwards.
        harness.Commands.ThrowFromHandlers = null;
        Assert.True((await harness.Client.GetStatusAsync()).IsArmed);
    }

    [Fact]
    public async Task AMalformedRequestIsRefusedWithAReason()
    {
        await using var harness = await Harness.StartAsync();

        // RequestClip with no payload: the Engine must not dereference it.
        var reply = await harness.Client.RequestAsync(
            new IpcMessage { Kind = IpcMessageKind.RequestClip });

        Assert.Equal(IpcMessageKind.Failed, reply.Kind);
        Assert.Contains("no clip request payload", reply.Error);
        Assert.Empty(harness.Commands.ClipRequests);
    }

    [Fact]
    public async Task AProtocolVersionMismatchSaysToRestartFrost()
    {
        await using var harness = await Harness.StartAsync();

        var reply = await harness.Client.RequestAsync(
            new IpcMessage { Kind = IpcMessageKind.GetStatus, Version = 99 });

        Assert.Equal(IpcMessageKind.Failed, reply.Kind);
        Assert.Contains("Restart Frost", reply.Error);
    }

    [Fact]
    public async Task TheShellCannotSendRepliesOrNotifications()
    {
        await using var harness = await Harness.StartAsync();

        var reply = await harness.Client.RequestAsync(
            new IpcMessage { Kind = IpcMessageKind.ClipSaved, Clip = harness.Commands.ListClips()[0] });

        Assert.Equal(IpcMessageKind.Failed, reply.Kind);
        Assert.Contains("not a request", reply.Error);
    }

    [Fact]
    public async Task AbsentEngineFailsFastWithSomethingActionable()
    {
        await using var client = new IpcClient(UniquePipeName());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => client.GetStatusAsync());
        clock.Stop();

        Assert.Contains("engine is not running", ex.Message);
        Assert.True(
            clock.Elapsed < FrostIpc.ConnectTimeout + TimeSpan.FromSeconds(3),
            $"took {clock.Elapsed} to give up");
    }

    [Fact]
    public async Task TryConnectReportsFailureWithoutThrowing()
    {
        await using var client = new IpcClient(UniquePipeName());
        Assert.False(await client.TryConnectAsync());
    }

    [Fact]
    public async Task TheShellCanReconnectAfterTheEngineRestarts()
    {
        // An Engine update or crash-and-restart must not require restarting the
        // Shell.
        var pipeName = UniquePipeName();
        var commands = new FakeEngineCommands();

        var server = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        server.Start();

        await using var client = new IpcClient(pipeName);
        Assert.True((await client.GetStatusAsync()).IsArmed);

        var disconnects = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnects);

        await server.DisposeAsync();

        // Requests fail while the Engine is down.
        await Assert.ThrowsAsync<EngineUnavailableException>(() => client.GetStatusAsync());

        var replacement = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        replacement.Start();

        try
        {
            // And succeed again once it is back, without a new client.
            var recovered = false;
            for (var attempt = 0; attempt < 20 && !recovered; attempt++)
            {
                try
                {
                    recovered = (await client.GetStatusAsync()).IsArmed;
                }
                catch (EngineUnavailableException)
                {
                    await Task.Delay(100);
                }
            }

            Assert.True(recovered, "the client never reconnected to the replacement engine");
        }
        finally
        {
            await replacement.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheEngineSurvivesAShellThatDisappears()
    {
        // The whole point of the two-process split: the Shell dying must not
        // disturb the Engine.
        var pipeName = UniquePipeName();
        var commands = new FakeEngineCommands();
        await using var server = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        server.Start();

        for (var round = 0; round < 3; round++)
        {
            var client = new IpcClient(pipeName);
            await client.ConnectAsync();
            Assert.True((await client.GetStatusAsync()).IsArmed);

            // Kill it without any graceful shutdown.
            await client.DisposeAsync();
            await Task.Delay(50);
        }

        // A notification with nobody listening is a no-op, not a failure.
        Assert.False(await server.NotifyAsync(IpcMessage.RecordingStateNotification(true)));

        await using var survivor = new IpcClient(pipeName);
        Assert.True((await survivor.GetStatusAsync()).IsArmed);
        Assert.True(server.ClientsAccepted >= 4, $"only {server.ClientsAccepted} clients accepted");
    }

    [Fact]
    public async Task RequestsAfterDisposeAreRejectedCleanly()
    {
        await using var harness = await Harness.StartAsync();
        var client = harness.Client;
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetStatusAsync());
    }

    [Fact]
    public async Task StartingTheServerTwiceIsAnError()
    {
        await using var server = new IpcServer(
            new FakeEngineCommands(), NullEngineLog.Instance, UniquePipeName());

        server.Start();
        Assert.Throws<InvalidOperationException>(server.Start);
    }

    [Fact]
    public void NullDependenciesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new IpcServer(null!, NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new IpcServer(new FakeEngineCommands(), null!));
    }

    [Fact]
    public async Task ASettingsPayloadMissingWholeSectionsStillCrossesTheWire()
    {
        // Gotcha already paid for once: System.Text.Json's source generator does
        // not run property initialisers, so an absent section arrives as null.
        // The Engine must not dereference it.
        await using var harness = await Harness.StartAsync();

        var reply = await harness.Client.RequestAsync(new IpcMessage
        {
            Kind = IpcMessageKind.ApplySettings,
            Settings = new FrostSettings { Capture = null!, Hotkeys = null! },
        });

        Assert.Equal(IpcMessageKind.Acknowledged, reply.Kind);

        var applied = Assert.Single(harness.Commands.AppliedSettings);

        // Whatever arrived, normalising it produces something usable rather than
        // throwing.
        var corrections = new List<SettingsCorrection>();
        var normalised = SettingsStore.Normalise(applied, corrections);
        Assert.Equal(60, normalised.Capture.Fps);
        Assert.NotEmpty(normalised.ResolveHotkeys());
    }
}

[Collection("ipc")]
public sealed class IpcServerStartupTests
{
    [Fact]
    public async Task StartDoesNotReturnUntilTheShellCouldActuallyConnect()
    {
        // The Shell's usual sequence is "launch the Engine, then connect". If
        // Start() returned while the pipe was still being created, that first
        // connection would fail for no reason the user could understand.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var pipeName = $"frost-startup-{Guid.NewGuid():N}";
            await using var server = new IpcServer(
                new FakeEngineCommands(), NullEngineLog.Instance, pipeName);

            server.Start();

            await using var client = new IpcClient(pipeName);

            // No retry loop and no delay: if Start() is honest, this connects
            // first time, every time.
            await client.ConnectAsync();
            Assert.True((await client.GetStatusAsync()).IsArmed);
        }
    }

    [Fact]
    public async Task StartComesUpPromptlyEvenWhenTheThreadPoolIsSaturated()
    {
        // The failure that found the Start() bug: a Task.Run accept loop sits
        // queued behind pool work, so the pipe does not exist for seconds after
        // Start() returns. The loop is now LongRunning (its own thread) and
        // Start() waits for the pipe, so neither depends on the pool.
        //
        // This deliberately asserts only about the server. A client connect under
        // a starved pool measures the *client's* ability to schedule its own
        // continuations, which is a different thing and not what this guards.
        using var block = new ManualResetEventSlim(false);
        var hogs = Enumerable.Range(0, Environment.ProcessorCount * 4)
            .Select(_ => Task.Run(() => block.Wait(TimeSpan.FromSeconds(30))))
            .ToArray();

        try
        {
            var pipeName = $"frost-pressure-{Guid.NewGuid():N}";
            await using var server = new IpcServer(
                new FakeEngineCommands(), NullEngineLog.Instance, pipeName);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            server.Start();
            clock.Stop();

            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(2),
                $"Start() took {clock.Elapsed.TotalSeconds:F1}s with the pool saturated");
        }
        finally
        {
            block.Set();
            await Task.WhenAll(hogs);
        }
    }
}
