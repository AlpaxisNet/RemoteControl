using RemoteControl.Core;
using Tmds.DBus.Protocol;

namespace RemoteControl.App.Tray;

/// <summary>
/// Whether a tray icon is really shown on this desktop. Windows and macOS always show one. On Linux a tray icon is a
/// StatusNotifierItem, which a desktop only shows while a StatusNotifier watcher owns the name <c>org.kde.StatusNotifierWatcher</c> on
/// the session bus (what <c>TStatusNotifier.cpp:204-208</c> asked): the bus is asked <c>NameHasOwner</c> at start and every 15 seconds
/// (a panel can start or stop). Until the first answer, and whenever the bus cannot be asked within two seconds, the answer is
/// <c>false</c>: a main window then asks before signing out instead of hiding where nobody can get it back.
/// </summary>
internal sealed class TrayAvailability : IDisposable
{
    /// <summary>The name a StatusNotifier watcher owns.</summary>
    public const string WatcherName = "org.kde.StatusNotifierWatcher";

    /// <summary>How often the bus is asked again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long one question may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly CancellationTokenSource _stop = new();
    private readonly AppLog _log;
    private readonly TimeSpan _timeout;
    private volatile bool _available;
    private Task _loop = Task.CompletedTask;

    /// <summary>Creates the check; <see cref="Start"/> begins asking.</summary>
    /// <param name="log">Where failures are logged (once).</param>
    /// <param name="probe">Asks whether a watcher runs (the session bus by default; tests give their own).</param>
    /// <param name="timeout">How long one question may take (<see cref="Timeout"/> by default).</param>
    public TrayAvailability(AppLog log, Func<CancellationToken, Task<bool>>? probe = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? Timeout;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _probe = probe ?? HasStatusNotifierWatcherAsync;
    }

    /// <summary>Whether the platform always shows a tray icon (Windows, macOS).</summary>
    public static bool AlwaysAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Whether the tray icon is shown, as far as is known now.</summary>
    public bool IsAvailable => AlwaysAvailable || _available;

    /// <summary>Raised (on any thread) when <see cref="IsAvailable"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts asking (nothing on Windows and macOS).</summary>
    public void Start()
    {
        if (AlwaysAvailable || !_loop.IsCompleted) return;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>Asks once and remembers the answer.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The answer.</returns>
    internal async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        bool available;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_timeout);
            Task<bool> probe = _probe(limit.Token);
            Task finished = await Task.WhenAny(probe, Task.Delay(_timeout, cancellationToken)).ConfigureAwait(false);
            available = ReferenceEquals(finished, probe) && probe.IsCompletedSuccessfully && probe.Result;
            if (!ReferenceEquals(finished, probe)) _ = probe.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested) _log.Info("The session bus could not be asked for a tray: " + exception.Message);
            available = false;
        }
        bool changed = available != _available;
        _available = available;
        if (changed)
        {
            _log.Info(available ? "A StatusNotifier watcher runs: the tray icon is shown." : "No StatusNotifier watcher runs: there is no tray icon.");
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _log.Error("A tray availability handler failed.", exception);
            }
        }
        return available;
    }

    /// <summary>Stops asking.</summary>
    public void Dispose()
    {
        // Not disposed: the loop may still be between two awaits; cancelling ends it.
        _stop.Cancel();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(RecheckInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Asks the session bus whether <see cref="WatcherName"/> has an owner.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Whether a watcher runs.</returns>
    internal static async Task<bool> HasStatusNotifierWatcherAsync(CancellationToken cancellationToken)
    {
        string? address = Address.Session;
        if (string.IsNullOrEmpty(address)) return false;
        using var connection = new Connection(address);
        await connection.ConnectAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        MessageBuffer message = NameHasOwnerMessage(connection);
        return await connection.CallMethodAsync(message, static (Message reply, object? _) => reply.GetBodyReader().ReadBool(), null).ConfigureAwait(false);
    }

    private static MessageBuffer NameHasOwnerMessage(Connection connection)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
        writer.WriteString(WatcherName);
        return writer.CreateMessage();
    }
}
