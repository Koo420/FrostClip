using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Frost.Shared.Ipc;

/// <summary>Thrown when the Engine is not reachable.</summary>
public sealed class EngineUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The Shell's side of the Engine↔Shell channel.
/// </summary>
/// <remarks>
/// <para>Lives in Frost.Shared rather than Frost.Shell because it is transport,
/// not UI — which also means it can be tested against a real server on any
/// platform instead of only on a machine that can build WinUI.</para>
///
/// <para><b>The Engine may not be running, and that is not an error state to
/// crash on.</b> Every call either succeeds or throws
/// <see cref="EngineUnavailableException"/> quickly, so the Shell can show
/// "Engine not running" and offer to start it. Nothing here blocks the UI
/// thread indefinitely: the connect and the request both have timeouts.</para>
///
/// <para>Notifications arrive on a background reader and are handed to
/// <see cref="NotificationReceived"/>, which the Shell marshals onto its own
/// dispatcher. Replies are matched to requests by correlation id, so a
/// notification arriving in the middle of a request does not get mistaken for
/// its reply — the bug that makes hand-rolled request/response channels
/// intermittently return the wrong answer.</para>
/// </remarks>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private long _nextCorrelationId;
    private bool _disposed;

    public IpcClient(string? pipeName = null) => _pipeName = pipeName ?? FrostIpc.PipeName;

    /// <summary>Unsolicited messages from the Engine: clip saved, hotkey fired, state changed.</summary>
    public event Action<IpcMessage>? NotificationReceived;

    /// <summary>
    /// Raised when the connection drops, so the Shell can show "Engine not
    /// running" rather than a stale status.
    /// </summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Connects, or throws <see cref="EngineUnavailableException"/> if the Engine
    /// is not there within <see cref="FrostIpc.ConnectTimeout"/>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await TearDownAsync().ConfigureAwait(false);

            var pipe = new NamedPipeClientStream(
                serverName: ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _shutdown.Token);
                timeout.CancelAfter(FrostIpc.ConnectTimeout);

                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);

                // The overwhelmingly likely cause, and the one the user can act
                // on, is that the Engine is not running.
                throw new EngineUnavailableException(
                    "Frost's engine is not running. Start it from the tray, or restart Frost.", ex);
            }

            _pipe = pipe;
            _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects if needed, returning false instead of throwing.</summary>
    public async Task<bool> TryConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (EngineUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a request and waits for its reply.
    /// </summary>
    /// <exception cref="EngineUnavailableException">
    /// The Engine is not connected, went away mid-request, or did not reply
    /// within <see cref="FrostIpc.RequestTimeout"/>.
    /// </exception>
    public async Task<IpcMessage> RequestAsync(
        IpcMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await ConnectAsync(cancellationToken).ConfigureAwait(false);

        var pipe = _pipe ?? throw new EngineUnavailableException("Not connected to the engine.");
        var correlationId = Interlocked.Increment(ref _nextCorrelationId);
        var message = request with { CorrelationId = correlationId };

        var completion = new TaskCompletionSource<IpcMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[correlationId] = completion;

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(pipe, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token);
            timeout.CancelAfter(FrostIpc.RequestTimeout);

            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EngineUnavailableException(
                $"Frost's engine did not answer within {FrostIpc.RequestTimeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new EngineUnavailableException("The connection to Frost's engine was lost.", ex);
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Asks for the Engine's current status.</summary>
    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.GetStatus, 0), cancellationToken).ConfigureAwait(false);

        return reply.Kind == IpcMessageKind.Status && reply.Status is not null
            ? reply.Status
            : throw new EngineUnavailableException(reply.Error ?? "The engine returned no status.");
    }

    /// <summary>Asks the Engine to save the trailing <paramref name="duration"/>.</summary>
    public async Task SaveClipAsync(
        TimeSpan duration, string? label = null, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.ClipRequestFor(duration.TotalSeconds, label, 0),
            cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
    }

    /// <summary>Starts or stops full-session recording; returns the new state.</summary>
    public async Task<bool> ToggleFullSessionRecordingAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.ToggleFullSessionRecording, 0),
            cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
        return reply.IsRecordingSession ?? false;
    }

    /// <summary>Marks the current moment in the session recording.</summary>
    public async Task AddBookmarkAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.AddBookmark, 0), cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
    }

    public async Task<Settings.FrostSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.GetSettings, 0), cancellationToken).ConfigureAwait(false);

        return reply.Kind == IpcMessageKind.Settings && reply.Settings is not null
            ? reply.Settings
            : throw new EngineUnavailableException(reply.Error ?? "The engine returned no settings.");
    }

    public async Task ApplySettingsAsync(
        Settings.FrostSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var reply = await RequestAsync(
            new IpcMessage { Kind = IpcMessageKind.ApplySettings, Settings = settings },
            cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
    }

    public async Task<IReadOnlyList<DisplayDescriptor>> ListDisplaysAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.ListDisplays, 0), cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
        return reply.Displays ?? [];
    }

    public async Task<IReadOnlyList<WindowDescriptor>> ListWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.ListWindows, 0), cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
        return reply.Windows ?? [];
    }

    public async Task<IReadOnlyList<Clips.ClipMetadata>> ListClipsAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(
            IpcMessage.Request(IpcMessageKind.ListClips, 0), cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(reply);
        return reply.Clips ?? [];
    }

    private static void ThrowIfFailed(IpcMessage reply)
    {
        if (reply.Kind == IpcMessageKind.Failed)
        {
            throw new EngineUnavailableException(reply.Error ?? "The engine refused the request.");
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        Exception? fault = null;

        try
        {
            while (pipe is not null && pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var message = await IpcFraming.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);

                if (message is null)
                {
                    break;
                }

                if (message.CorrelationId != 0 &&
                    _pending.TryRemove(message.CorrelationId, out var completion))
                {
                    completion.TrySetResult(message);
                    continue;
                }

                // Correlation id of zero, or one nobody is waiting on: a
                // notification, or a reply whose caller already timed out.
                if (message.IsNotification)
                {
                    RaiseNotification(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            FailPending(fault);

            if (!cancellationToken.IsCancellationRequested)
            {
                Disconnected?.Invoke(fault);
            }
        }
    }

    private void RaiseNotification(IpcMessage message)
    {
        try
        {
            NotificationReceived?.Invoke(message);
        }
        catch (Exception)
        {
            // A handler throwing must not kill the reader and take the whole
            // channel down with it; the Shell would then look frozen.
        }
    }

    /// <summary>
    /// Completes every waiting request with a failure.
    /// </summary>
    /// <remarks>
    /// Without this, a request in flight when the Engine exits would sit until its
    /// timeout, which from the Shell's side is ten seconds of a spinner for
    /// something already known to be dead.
    /// </remarks>
    private void FailPending(Exception? fault)
    {
        foreach (var (id, completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(new EngineUnavailableException(
                    "The connection to Frost's engine was lost.", fault));
            }
        }
    }

    private async ValueTask TearDownAsync()
    {
        var pipe = _pipe;
        _pipe = null;

        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        var readLoop = _readLoop;
        _readLoop = null;

        if (readLoop is not null)
        {
            try
            {
                await readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                // The reader is wedged on a dead pipe; it is a background task and
                // the pipe is disposed, so let it go.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await TearDownAsync().ConfigureAwait(false);

        FailPending(null);

        _shutdown.Dispose();
        _writeGate.Dispose();
        _connectGate.Dispose();
    }
}
