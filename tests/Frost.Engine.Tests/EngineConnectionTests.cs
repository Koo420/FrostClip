using Frost.Engine.Diagnostics;
using Frost.Engine.Ipc;
using Frost.Shared.Ipc;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The Shell's view of the Engine, over a real pipe.
/// </summary>
[Collection("ipc")]
public sealed class EngineConnectionTests
{
    private static string UniquePipeName() => $"frost-conn-{Guid.NewGuid():N}";

    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits for a condition. These timeouts assert <i>that</i> something
    /// happens, never how fast, so they are generous: the build host is shared and
    /// a loaded machine has been observed running the suite three times slower
    /// than usual. Tests that genuinely assert speed (Start() coming up promptly,
    /// Request() not blocking) keep tight bounds.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task PollsStatusWhileTheEngineIsUp()
    {
        var pipeName = UniquePipeName();
        var commands = new FakeEngineCommands();
        await using var server = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        server.Start();

        await using var connection = new EngineConnection(pipeName, FastPoll);

        var updates = 0;
        connection.StatusUpdated += _ => Interlocked.Increment(ref updates);
        connection.Start();

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref updates) >= 3, TimeSpan.FromSeconds(45)));
        Assert.True(connection.IsConnected);
        Assert.Equal(1920, connection.Status!.CaptureWidth);
    }

    [Fact]
    public async Task AnAbsentEngineIsAConnectionStateNotAnException()
    {
        // The Shell must be able to open before the Engine does, and just show
        // "engine not running".
        await using var connection = new EngineConnection(UniquePipeName(), FastPoll);
        connection.Start();

        Assert.True(await WaitUntilAsync(() => connection.ConnectAttempts >= 2, TimeSpan.FromSeconds(45)));
        Assert.False(connection.IsConnected);
        Assert.Null(connection.Status);
    }

    [Fact]
    public async Task ConnectsOnceTheEngineAppears()
    {
        // The Shell was opened first; the Engine starts a moment later.
        var pipeName = UniquePipeName();
        await using var connection = new EngineConnection(pipeName, FastPoll);

        var states = new List<bool>();
        connection.ConnectionChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };

        connection.Start();
        Assert.True(await WaitUntilAsync(() => connection.ConnectAttempts >= 1, TimeSpan.FromSeconds(45)));
        Assert.False(connection.IsConnected);

        await using var server = new IpcServer(new FakeEngineCommands(), NullEngineLog.Instance, pipeName);
        server.Start();

        Assert.True(await WaitUntilAsync(() => connection.IsConnected, TimeSpan.FromSeconds(45)));

        lock (states)
        {
            Assert.Contains(true, states);
        }
    }

    [Fact]
    public async Task RecoversWhenTheEngineRestarts()
    {
        // An Engine crash or update must not require restarting the Shell.
        var pipeName = UniquePipeName();
        var commands = new FakeEngineCommands();

        var server = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        server.Start();

        await using var connection = new EngineConnection(pipeName, FastPoll);
        connection.Start();

        Assert.True(await WaitUntilAsync(() => connection.IsConnected, TimeSpan.FromSeconds(45)));

        await server.DisposeAsync();
        Assert.True(await WaitUntilAsync(() => !connection.IsConnected, TimeSpan.FromSeconds(45)));
        Assert.Null(connection.Status);

        await using var replacement = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        replacement.Start();

        // Wait for a status, not just for the connection: the connection comes up
        // before the first poll completes, so asserting on Status immediately is a
        // race in the test rather than a product defect.
        Assert.True(await WaitUntilAsync(
            () => connection.IsConnected && connection.Status is not null, TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public async Task PushedNotificationsReachTheShell()
    {
        var pipeName = UniquePipeName();
        var commands = new FakeEngineCommands();
        await using var server = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        server.Start();

        await using var connection = new EngineConnection(pipeName, FastPoll);

        var notifications = new List<IpcMessage>();
        connection.NotificationReceived += message =>
        {
            lock (notifications)
            {
                notifications.Add(message);
            }
        };

        connection.Start();
        Assert.True(await WaitUntilAsync(() => connection.IsConnected, TimeSpan.FromSeconds(45)));

        await server.NotifyAsync(IpcMessage.ClipSavedNotification(commands.ListClips()[0]));

        Assert.True(await WaitUntilAsync(
            () => { lock (notifications) { return notifications.Count > 0; } },
            TimeSpan.FromSeconds(45)));

        lock (notifications)
        {
            Assert.Equal(IpcMessageKind.ClipSaved, notifications[0].Kind);
        }
    }

    [Fact]
    public async Task EventsGoThroughTheSuppliedDispatcher()
    {
        // In the Shell this is dispatcherQueue.TryEnqueue; touching UI state from
        // the polling thread would be a crash waiting to happen.
        var pipeName = UniquePipeName();
        await using var server = new IpcServer(new FakeEngineCommands(), NullEngineLog.Instance, pipeName);
        server.Start();

        var posted = 0;
        await using var connection = new EngineConnection(
            pipeName, FastPoll, post: action =>
            {
                Interlocked.Increment(ref posted);
                action();
            });

        var updates = 0;
        connection.StatusUpdated += _ => Interlocked.Increment(ref updates);
        connection.Start();

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref updates) >= 2, TimeSpan.FromSeconds(45)));
        Assert.True(Volatile.Read(ref posted) >= Volatile.Read(ref updates));
    }

    [Fact]
    public async Task AHandlerThatThrowsDoesNotStopThePolling()
    {
        // A binding error in the Shell must not freeze its view of the Engine.
        var pipeName = UniquePipeName();
        await using var server = new IpcServer(new FakeEngineCommands(), NullEngineLog.Instance, pipeName);
        server.Start();

        await using var connection = new EngineConnection(pipeName, FastPoll);

        var raised = 0;
        connection.StatusUpdated += _ =>
        {
            Interlocked.Increment(ref raised);
            throw new InvalidOperationException("a binding blew up");
        };

        connection.Start();
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref raised) >= 4, TimeSpan.FromSeconds(45)));
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task CommandsRunWhenConnectedAndReportFailureWhenNot()
    {
        var pipeName = UniquePipeName();
        var commands = new FakeEngineCommands();
        await using var server = new IpcServer(commands, NullEngineLog.Instance, pipeName);
        server.Start();

        await using var connection = new EngineConnection(pipeName, FastPoll);
        connection.Start();
        Assert.True(await WaitUntilAsync(() => connection.IsConnected, TimeSpan.FromSeconds(45)));

        Assert.True(await connection.TryInvokeAsync(
            client => client.SaveClipAsync(TimeSpan.FromSeconds(30), "30s")));
        Assert.Single(commands.ClipRequests);

        var (succeeded, status) = await connection.TryInvokeAsync(client => client.GetStatusAsync());
        Assert.True(succeeded);
        Assert.Equal(60, status!.CaptureFps);

        // A refusal from the Engine is a false, not an exception into the UI.
        commands.RefuseClipsBecause = "buffer still filling";
        Assert.False(await connection.TryInvokeAsync(
            client => client.SaveClipAsync(TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public async Task CommandsBeforeAnyConnectionReportFailureRatherThanThrowing()
    {
        await using var connection = new EngineConnection(UniquePipeName(), FastPoll);

        Assert.False(await connection.TryInvokeAsync(
            client => client.SaveClipAsync(TimeSpan.FromSeconds(30))));

        var (succeeded, status) = await connection.TryInvokeAsync(client => client.GetStatusAsync());
        Assert.False(succeeded);
        Assert.Null(status);
    }

    [Fact]
    public async Task StartingTwiceIsAnError()
    {
        await using var connection = new EngineConnection(UniquePipeName(), FastPoll);
        connection.Start();
        Assert.Throws<InvalidOperationException>(connection.Start);
    }

    [Fact]
    public async Task NonPositivePollIntervalsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineConnection(pollInterval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EngineConnection(pollInterval: TimeSpan.FromSeconds(-1)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NullOperationsAreRejected()
    {
        await using var connection = new EngineConnection(UniquePipeName(), FastPoll);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => connection.TryInvokeAsync((Func<IpcClient, Task>)null!));
    }

    [Fact]
    public async Task BackoffIsBoundedSoAReturningEngineIsNoticedPromptly()
    {
        Assert.True(EngineConnection.MaxRetryDelay <= TimeSpan.FromSeconds(5));
        Assert.True(EngineConnection.InitialRetryDelay < EngineConnection.MaxRetryDelay);
        await Task.CompletedTask;
    }
}
