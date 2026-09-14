using System.IO.Pipes;
using Frost.Engine.Diagnostics;
using Frost.Shared.Ipc;

namespace Frost.Engine.Ipc;

/// <summary>
/// The Engine's side of the Engine↔Shell channel.
/// </summary>
/// <remarks>
/// <para><b>The contract that matters:</b> nothing the Shell does can affect a
/// recording. The Shell can be closed, hung, killed mid-message, or never start
/// at all, and capture and encode carry on. So every client interaction is
/// wrapped: a dropped connection is an expected event that logs at debug and
/// goes back to waiting, and a handler that throws becomes a
/// <see cref="IpcMessageKind.Failed"/> reply rather than an unhandled exception
/// on a background thread.</para>
///
/// <para>One client at a time, because there is only ever one Shell. A second
/// connection attempt waits rather than being refused, so a Shell restarting
/// faster than the old connection tears down still gets in.</para>
///
/// <para>On Windows the pipe is ACL'd to the current user. The default DACL for a
/// named pipe grants read access more widely than is wanted for a channel that
/// carries settings and can be told to write files.</para>
/// </remarks>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IEngineCommands _commands;
    private readonly IEngineLog _log;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // Signalled once the pipe genuinely exists, so Start() does not return
    // before a client could connect.
    private readonly ManualResetEventSlim _listening = new(false);

    private Exception? _startupFailure;
    private Task? _acceptLoop;
    private NamedPipeServerStream? _connection;
    private long _messagesReceived;
    private long _messagesSent;
    private long _clientsAccepted;
    private long _handlerFailures;
    private bool _disposed;

    public IpcServer(IEngineCommands commands, IEngineLog log, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(log);

        _commands = commands;
        _log = log;
        _pipeName = pipeName ?? FrostIpc.PipeName;
    }

    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    public long MessagesSent => Interlocked.Read(ref _messagesSent);

    /// <summary>How many times a Shell has connected. Reconnects count again.</summary>
    public long ClientsAccepted => Interlocked.Read(ref _clientsAccepted);

    /// <summary>Requests whose handler threw and became a Failed reply.</summary>
    public long HandlerFailures => Interlocked.Read(ref _handlerFailures);

    /// <summary>Whether a Shell is currently connected.</summary>
    public bool HasClient => _connection?.IsConnected == true;

    /// <summary>How long <see cref="Start"/> waits for the pipe to come up.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Starts listening. Returns only once the pipe exists.
    /// </summary>
    /// <remarks>
    /// Waiting for the pipe matters: the Shell's usual sequence is "launch the
    /// Engine, then connect", and if <c>Start</c> returned while the pipe was
    /// still being created the first connection would fail for no reason the user
    /// could understand. The accept loop signals as soon as the pipe is up.
    /// </remarks>
    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The IPC server is already started.");
        }

        // Long-running so it does not consume a pooled thread for the life of the
        // process, and does not queue behind other pool work at startup.
        _acceptLoop = Task.Factory.StartNew(
            () => AcceptLoopAsync(_shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();

        if (!_listening.Wait(StartupTimeout))
        {
            throw new InvalidOperationException(
                $"The IPC pipe '{_pipeName}' did not come up within {StartupTimeout.TotalSeconds:F0}s.");
        }

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException(
                $"Could not create the IPC pipe '{_pipeName}'. The Shell will not be able to connect.",
                _startupFailure);
        }

        _log.Info($"IPC server listening on '{_pipeName}'.");
    }

    /// <summary>
    /// Pushes a notification to the connected Shell, if there is one.
    /// </summary>
    /// <remarks>
    /// Fire and forget by design: a clip has been saved whether or not the Shell
    /// is listening, and the Engine must never wait on the UI. Returns false when
    /// there was nobody to tell.
    /// </remarks>
    public async Task<bool> NotifyAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var connection = _connection;
        if (connection is null || !connection.IsConnected)
        {
            return false;
        }

        try
        {
            return await SendAsync(connection, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            _log.Debug($"Notification dropped: the Shell went away ({ex.GetType().Name}).");
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = CreatePipe(_pipeName);

                // The pipe exists from here on, so Start() may return.
                _listening.Set();

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                _connection = server;
                Interlocked.Increment(ref _clientsAccepted);
                _log.Info("Shell connected.");

                await ServeAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsExpectedDisconnect(ex))
            {
                // Routine: the Shell was closed or killed. Not worth a warning.
                _log.Debug($"Shell disconnected ({ex.GetType().Name}).");
            }
            catch (Exception ex)
            {
                if (server is null)
                {
                    // The pipe itself could not be created. Record it so Start()
                    // can report something useful rather than just timing out.
                    _startupFailure ??= ex;
                    _listening.Set();
                }

                _log.Warn("The IPC connection failed; waiting for the Shell to reconnect.", ex);

                // Do not spin if the pipe itself cannot be created (a name
                // collision, a permissions problem): back off so a broken IPC
                // channel does not burn a core while the Engine keeps recording.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                _connection = null;

                if (server is not null)
                {
                    await DisposePipeAsync(server).ConfigureAwait(false);
                }
            }
        }

        _log.Info(
            $"IPC server stopped after {ClientsAccepted} client(s), " +
            $"{MessagesReceived} received / {MessagesSent} sent.");
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        while (server.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            IpcMessage? request;

            try
            {
                request = await IpcFraming.ReadAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (IpcProtocolException ex)
            {
                // The stream is no longer trustworthy: a bad length prefix means
                // we do not know where the next frame starts. Drop the
                // connection and let the Shell reconnect cleanly.
                _log.Warn($"Dropping the Shell connection: {ex.Message}");
                return;
            }

            if (request is null)
            {
                // Clean end of stream: the Shell closed the pipe.
                return;
            }

            Interlocked.Increment(ref _messagesReceived);

            var reply = BuildReply(request);
            if (reply is null)
            {
                continue;
            }

            if (!await SendAsync(server, reply, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Turns a request into its reply, converting every failure into a
    /// <see cref="IpcMessageKind.Failed"/> the Shell can display.
    /// </summary>
    private IpcMessage? BuildReply(IpcMessage request)
    {
        if (!request.Validate(out var reason))
        {
            _log.Warn($"Rejected an IPC message: {reason}");
            return IpcMessage.Failure(reason!, request.CorrelationId);
        }

        if (!request.IsRequest)
        {
            // The Shell has no business sending replies or notifications; say so
            // rather than silently ignoring it, so a protocol mistake is visible.
            return IpcMessage.Failure(
                $"{request.Kind} is not a request the Engine accepts.", request.CorrelationId);
        }

        try
        {
            return Dispatch(request);
        }
        catch (Exception ex)
        {
            // A handler throwing must not take down the IPC thread, and must not
            // leave the Shell waiting for a reply that never comes.
            Interlocked.Increment(ref _handlerFailures);
            _log.Error($"Handling {request.Kind} threw.", ex);
            return IpcMessage.Failure(ex.Message, request.CorrelationId);
        }
    }

    private IpcMessage Dispatch(IpcMessage request)
    {
        var id = request.CorrelationId;

        switch (request.Kind)
        {
            case IpcMessageKind.GetStatus:
                return IpcMessage.StatusReply(_commands.GetStatus(), id);

            case IpcMessageKind.RequestClip:
            {
                var duration = TimeSpan.FromSeconds(request.ClipRequest!.Seconds);
                return _commands.TryRequestClip(duration, request.ClipRequest.Label, out var error)
                    ? IpcMessage.Ack(id)
                    : IpcMessage.Failure(error ?? "The clip could not be saved.", id);
            }

            case IpcMessageKind.ToggleFullSessionRecording:
            {
                var recording = _commands.ToggleFullSessionRecording(out var error);

                if (error is not null)
                {
                    return IpcMessage.Failure(error, id);
                }

                return new IpcMessage
                {
                    Kind = IpcMessageKind.Acknowledged,
                    CorrelationId = id,
                    IsRecordingSession = recording,
                };
            }

            case IpcMessageKind.AddBookmark:
                return _commands.TryAddBookmark(out var bookmarkError)
                    ? IpcMessage.Ack(id)
                    : IpcMessage.Failure(bookmarkError ?? "The bookmark could not be added.", id);

            case IpcMessageKind.GetSettings:
                return IpcMessage.SettingsReply(_commands.GetSettings(), id);

            case IpcMessageKind.ApplySettings:
                return _commands.TryApplySettings(request.Settings!, out var settingsError)
                    ? IpcMessage.Ack(id)
                    : IpcMessage.Failure(settingsError ?? "The settings could not be applied.", id);

            case IpcMessageKind.ListDisplays:
                return new IpcMessage
                {
                    Kind = IpcMessageKind.Displays,
                    CorrelationId = id,
                    Displays = [.. _commands.ListDisplays()],
                };

            case IpcMessageKind.ListWindows:
                return new IpcMessage
                {
                    Kind = IpcMessageKind.Windows,
                    CorrelationId = id,
                    Windows = [.. _commands.ListWindows()],
                };

            case IpcMessageKind.ListClips:
                return new IpcMessage
                {
                    Kind = IpcMessageKind.Clips,
                    CorrelationId = id,
                    Clips = [.. _commands.ListClips()],
                };

            default:
                return IpcMessage.Failure($"{request.Kind} is not implemented.", id);
        }
    }

    /// <summary>
    /// Writes a message, serialised against notifications racing replies.
    /// </summary>
    /// <remarks>
    /// Two writers on one pipe would interleave bytes and corrupt both frames:
    /// the serve loop writes replies while a clip-saved notification can arrive
    /// from the clip thread at any moment.
    /// </remarks>
    private async Task<bool> SendAsync(
        NamedPipeServerStream server, IpcMessage message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!server.IsConnected)
            {
                return false;
            }

            await IpcFraming.WriteAsync(server, message, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _messagesSent);
            return true;
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            _log.Debug($"Write dropped: the Shell went away ({ex.GetType().Name}).");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Creates the listening pipe.
    /// </summary>
    /// <remarks>
    /// <para><see cref="PipeOptions.CurrentUserOnly"/> is what restricts the
    /// channel, and it is deliberately the only mechanism used. The default DACL
    /// on a named pipe is more generous than a channel that carries settings and
    /// accepts "write a file here" should be, and this option replaces it with
    /// one granting the creating user alone.</para>
    ///
    /// <para>It also does something on the <i>client</i> side that an ACL cannot:
    /// <see cref="IpcClient"/> passes the same option, which makes .NET verify
    /// that the pipe it connected to is owned by the same user. Without that
    /// check, any process on the machine could create a pipe of this name first
    /// and receive the Shell's traffic. So the option is load-bearing at both
    /// ends and must stay on both.</para>
    ///
    /// <para>An earlier version also built a <c>PipeSecurity</c> ACL and passed it
    /// to <c>NamedPipeServerStreamAcl.Create</c>. That combination is illegal -
    /// .NET throws <see cref="ArgumentException"/> because the explicit ACL and
    /// <c>CurrentUserOnly</c> are two ways to express the same thing - and the
    /// Engine could not open its pipe at all on Windows. It was not caught here
    /// because the ACL path sat behind <c>#if WINDOWS</c>: it compiled on the
    /// build host and never ran, so every IPC test exercised the other branch.
    /// There is now one code path for both platforms, which is the point - the
    /// round-trip tests now cover the code that actually ships.</para>
    /// </remarks>
    private static NamedPipeServerStream CreatePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// Whether an exception is just the Shell going away rather than a fault.
    /// </summary>
    /// <remarks>
    /// Closing the Shell window is the normal case, and it surfaces as any of
    /// these depending on timing. Logging it as an error would train users to
    /// ignore the log.
    /// </remarks>
    private static bool IsExpectedDisconnect(Exception ex) =>
        ex is IOException or ObjectDisposedException or InvalidOperationException;

    private static async ValueTask DisposePipeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            // Already gone.
        }

        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _log.Warn("The IPC accept loop did not stop within 5s.");
            }
        }

        _shutdown.Dispose();
        _writeGate.Dispose();
        _listening.Dispose();
    }
}
