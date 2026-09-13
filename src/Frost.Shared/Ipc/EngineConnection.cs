namespace Frost.Shared.Ipc;

/// <summary>
/// A live, self-healing connection to the Engine, for the Shell to bind to.
/// </summary>
/// <remarks>
/// <para>The Shell's view of the Engine has to survive the Engine not being
/// there: it may not have started yet, it may be mid-update, it may have
/// crashed. So this polls status, reconnects with backoff, and reports the
/// connection state as an ordinary UI state rather than an exception. The Shell
/// binds to <see cref="Status"/> and <see cref="IsConnected"/> and never has to
/// think about pipes.</para>
///
/// <para>The one genuinely UI-specific concern — getting events onto the
/// dispatcher thread — is a delegate the caller supplies
/// (<c>dispatcherQueue.TryEnqueue</c> in WinUI). Everything else lives here,
/// which is also why it can be tested on any platform instead of only on a
/// machine that can build WinUI.</para>
///
/// <para>Polling, not pushing, for status: it is a handful of bytes a second,
/// the Shell is only open when the user is looking at it, and it means a missed
/// notification cannot leave the UI showing something stale forever. Events the
/// user must not miss — a clip being saved — are pushed.</para>
/// </remarks>
public sealed class EngineConnection : IAsyncDisposable
{
    private readonly string? _pipeName;
    private readonly TimeSpan _pollInterval;
    private readonly Action<Action>? _post;
    private readonly CancellationTokenSource _shutdown = new();

    private IpcClient? _client;
    private Task? _loop;
    private EngineStatus? _status;
    private bool _connected;
    private bool _disposed;

    /// <param name="pipeName">Defaults to <see cref="FrostIpc.PipeName"/>.</param>
    /// <param name="pollInterval">
    /// How often to refresh status. One second is plenty: the numbers the Shell
    /// shows (buffered seconds, frame counts) do not need to be smoother than a
    /// person can read.
    /// </param>
    /// <param name="post">
    /// Marshals a callback onto the UI thread. When null, events are raised on the
    /// background loop — fine for tests and headless use.
    /// </param>
    public EngineConnection(
        string? pipeName = null,
        TimeSpan? pollInterval = null,
        Action<Action>? post = null)
    {
        _pipeName = pipeName;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _post = post;

        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), _pollInterval, "Must be positive.");
        }
    }

    /// <summary>First backoff delay after a failed connection attempt.</summary>
    public static TimeSpan InitialRetryDelay => TimeSpan.FromMilliseconds(500);

    /// <summary>Ceiling on the backoff, so a long-absent Engine is still noticed promptly.</summary>
    public static TimeSpan MaxRetryDelay => TimeSpan.FromSeconds(5);

    /// <summary>Latest status, or null while the Engine is unreachable.</summary>
    public EngineStatus? Status => _status;

    public bool IsConnected => _connected;

    /// <summary>Raised whenever a fresh status arrives.</summary>
    public event Action<EngineStatus>? StatusUpdated;

    /// <summary>Raised when the connection comes up or goes down.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Raised for pushed messages: clip saved, clip failed, hotkey fired.</summary>
    public event Action<IpcMessage>? NotificationReceived;

    /// <summary>Connection attempts made, for diagnostics and tests.</summary>
    public int ConnectAttempts { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_loop is not null)
        {
            throw new InvalidOperationException("The engine connection is already started.");
        }

        _loop = Task.Run(() => RunAsync(_shutdown.Token));
    }

    /// <summary>
    /// Runs an operation against the Engine, or returns false if it is not
    /// reachable.
    /// </summary>
    /// <remarks>
    /// The shape every Shell command wants: a button that saves a clip should
    /// grey out or show "engine not running", not throw into the UI's unhandled
    /// exception handler.
    /// </remarks>
    public async Task<bool> TryInvokeAsync(
        Func<IpcClient, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var client = _client;
        if (client is null)
        {
            return false;
        }

        try
        {
            await operation(client).ConfigureAwait(false);
            return true;
        }
        catch (EngineUnavailableException)
        {
            return false;
        }
        finally
        {
            _ = cancellationToken;
        }
    }

    /// <summary>As <see cref="TryInvokeAsync(Func{IpcClient, Task}, CancellationToken)"/>, with a result.</summary>
    public async Task<(bool Succeeded, T? Result)> TryInvokeAsync<T>(
        Func<IpcClient, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var client = _client;
        if (client is null)
        {
            return (false, default);
        }

        try
        {
            return (true, await operation(client).ConfigureAwait(false));
        }
        catch (EngineUnavailableException)
        {
            return (false, default);
        }
        finally
        {
            _ = cancellationToken;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = InitialRetryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await DelayAsync(retryDelay, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                // Back off, but not past the ceiling: the Shell should notice the
                // Engine returning within a few seconds, not a few minutes.
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                continue;
            }

            retryDelay = InitialRetryDelay;

            try
            {
                var status = await _client!.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                _status = status;
                Raise(() => StatusUpdated?.Invoke(status));
            }
            catch (EngineUnavailableException)
            {
                SetConnected(false);
                _status = null;
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!await DelayAsync(_pollInterval, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && _client.IsConnected)
        {
            return true;
        }

        await TearDownClientAsync().ConfigureAwait(false);

        var client = new IpcClient(_pipeName);
        client.NotificationReceived += OnNotification;
        client.Disconnected += _ =>
        {
            SetConnected(false);
            _status = null;
        };

        ConnectAttempts++;

        if (!await client.TryConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            SetConnected(false);
            return false;
        }

        _client = client;
        SetConnected(true);
        return true;
    }

    private void OnNotification(IpcMessage message) => Raise(() => NotificationReceived?.Invoke(message));

    private void SetConnected(bool connected)
    {
        if (_connected == connected)
        {
            return;
        }

        _connected = connected;
        Raise(() => ConnectionChanged?.Invoke(connected));
    }

    /// <summary>
    /// Raises an event, on the UI thread when a poster was supplied.
    /// </summary>
    /// <remarks>
    /// A handler throwing must not kill the polling loop — that would freeze the
    /// Shell's view of the Engine with no indication why.
    /// </remarks>
    private void Raise(Action action)
    {
        try
        {
            if (_post is null)
            {
                action();
            }
            else
            {
                _post(action);
            }
        }
        catch (Exception)
        {
            // Swallowed deliberately; see above.
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async ValueTask TearDownClientAsync()
    {
        var client = _client;
        _client = null;

        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
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

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Background loop; the client is about to be disposed anyway.
            }
        }

        await TearDownClientAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
