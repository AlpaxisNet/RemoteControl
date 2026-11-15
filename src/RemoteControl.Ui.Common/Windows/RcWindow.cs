using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The base class of every window of the program. It carries the application icon, takes the theme (through the application's
/// resources and styles), and releases what it holds when it closes (rule 5): disposables given to <see cref="Track{T}"/>, actions
/// given to <see cref="OnClosedOnce"/>, event subscriptions made with <see cref="Listen(Action{EventHandler}, Action{EventHandler}, EventHandler, string?)"/>
/// and timers made with <see cref="Every"/>. An event that arrives after the window closed is ignored. A window with
/// <see cref="HideOnClose"/> hides instead of closing when the user closes it and comes back at the same place and size.
/// </summary>
/// <example>
/// <code>
/// public sealed partial class PlayerListWindow : RcWindow
/// {
///     public PlayerListWindow(AppServices services, Session session) : base(services)
///     {
///         InitializeComponent();
///         HideOnClose = true;
///         Listen&lt;PlayerEventArgs&gt;(h =&gt; session.PlayerChanged += h, h =&gt; session.PlayerChanged -= h, OnPlayerChanged);
///         Every(TimeSpan.FromMilliseconds(500), BlinkPmIcons, "PlayerList.Blink");
///         Track(new FileSystemWatcher(folder));
///         refresh.Click += UiSafe.Handler((_, _) =&gt; Refresh(), "PlayerList.Refresh");
///     }
/// }
/// ...
/// playerList.ShowAndActivate();
/// </code>
/// </example>
public class RcWindow : Window
{
    private static readonly List<RcWindow> s_open = new();
    private readonly List<IDisposable> _tracked = new();
    private readonly List<Action> _onClosed = new();
    private readonly CancellationTokenSource _closedSource = new();
    private PixelPoint? _savedPosition;
    private Size? _savedSize;
    private bool _registered;

    /// <summary>Creates a window without services (designer, simple dialogs); it takes <see cref="DefaultIcon"/>.</summary>
    public RcWindow() : this(null)
    {
    }

    /// <summary>Creates a window.</summary>
    /// <param name="services">The application services; their application icon is used.</param>
    public RcWindow(AppServices? services)
    {
        Services = services;
        WindowIcon? icon = services?.Assets.AppIcon ?? DefaultIcon;
        if (icon is not null) Icon = icon;
    }

    /// <summary>The icon of windows created without services (set by <see cref="AppServices.Create"/>).</summary>
    public static WindowIcon? DefaultIcon { get; set; }

    /// <summary>Every window of this type that was shown and has not closed yet (hidden ones included), in the order they opened.</summary>
    public static IReadOnlyList<RcWindow> OpenWindows
    {
        get { lock (s_open) return s_open.ToArray(); }
    }

    /// <summary>The application services, or <c>null</c>.</summary>
    public AppServices? Services { get; }

    /// <summary>Whether the window has closed (it cannot be shown again).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Cancelled when the window closes: pass it to asynchronous work the window started.</summary>
    public CancellationToken ClosedToken => _closedSource.Token;

    /// <summary>When set, the user closing the window hides it (position and size remembered); <see cref="Window.Close()"/> from code still closes it.</summary>
    public bool HideOnClose { get; set; }

    /// <summary>Keeps <paramref name="disposable"/> until the window closes, then disposes it (at once when already closed).</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="disposable">A subscription, timer or other resource.</param>
    /// <returns>The same object.</returns>
    public T Track<T>(T disposable) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(disposable);
        if (IsClosed)
        {
            DisposeQuietly(disposable);
            return disposable;
        }
        _tracked.Add(disposable);
        return disposable;
    }

    /// <summary>Runs <paramref name="action"/> once when the window closes (at once when already closed); an exception is reported.</summary>
    /// <param name="action">The code.</param>
    public void OnClosedOnce(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsClosed) UiSafe.Run(action, GetType().Name + ".OnClosedOnce");
        else _onClosed.Add(action);
    }

    /// <summary>
    /// Subscribes to a plain .NET event until the window closes. The handler runs on the UI thread (posted there when the event is
    /// raised on another thread), not at all after the window closed, and inside <see cref="UiSafe"/>.
    /// </summary>
    /// <param name="add">Adds the handler, for example <c>h =&gt; session.Disconnected += h</c>.</param>
    /// <param name="remove">Removes it, for example <c>h =&gt; session.Disconnected -= h</c>.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="context">What it does (for the log); the window type and "event" by default.</param>
    /// <returns>The subscription; dispose it to unsubscribe early.</returns>
    public IDisposable Listen(Action<EventHandler> add, Action<EventHandler> remove, EventHandler handler, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(handler);
        string name = context ?? GetType().Name + " event";
        EventHandler wrapper = (sender, args) => Deliver(() => handler(sender, args), name);
        add(wrapper);
        return Track(new Subscription(() => remove(wrapper)));
    }

    /// <summary>Subscribes to a plain .NET event with event data until the window closes (see the other overload).</summary>
    /// <typeparam name="TArgs">The event data type.</typeparam>
    /// <param name="add">Adds the handler.</param>
    /// <param name="remove">Removes it.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="context">What it does (for the log).</param>
    /// <returns>The subscription; dispose it to unsubscribe early.</returns>
    public IDisposable Listen<TArgs>(Action<EventHandler<TArgs>> add, Action<EventHandler<TArgs>> remove, EventHandler<TArgs> handler, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(handler);
        string name = context ?? GetType().Name + " event";
        EventHandler<TArgs> wrapper = (sender, args) => Deliver(() => handler(sender, args), name);
        add(wrapper);
        return Track(new Subscription(() => remove(wrapper)));
    }

    /// <summary>A repeating timer that stops when the window closes (<see cref="UiDispatch.Repeat"/>).</summary>
    /// <param name="interval">The time between ticks.</param>
    /// <param name="tick">The code.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The timer.</returns>
    public RepeatingTimer Every(TimeSpan interval, Action tick, string? context = null) =>
        Track(UiDispatch.Repeat(interval, tick, context ?? GetType().Name + " timer", this));

    /// <summary>
    /// Shows the window (at its remembered position and size when it was hidden), restores it when minimized and brings it to the
    /// front. Does nothing on a closed window.
    /// </summary>
    /// <param name="owner">The owner window for a first show, or <c>null</c>.</param>
    public void ShowAndActivate(Window? owner = null)
    {
        if (IsClosed) return;
        UiSafe.Run(() =>
        {
            if (!IsVisible)
            {
                if (_savedPosition is { } position)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = position;
                }
                if (_savedSize is { } size)
                {
                    Width = size.Width;
                    Height = size.Height;
                }
                if (owner is not null && !ReferenceEquals(owner, this) && owner.IsVisible) Show(owner);
                else Show();
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }, GetType().Name + ".ShowAndActivate");
    }

    /// <summary>Hides the window and remembers its position and size for <see cref="ShowAndActivate"/>.</summary>
    public void HideAndRemember()
    {
        if (IsClosed || !IsVisible) return;
        _savedPosition = Position;
        _savedSize = ClientSize;
        Hide();
    }

    /// <summary>The position remembered by <see cref="HideAndRemember"/>, or <c>null</c>.</summary>
    public PixelPoint? RememberedPosition => _savedPosition;

    /// <summary>The size remembered by <see cref="HideAndRemember"/>, or <c>null</c>.</summary>
    public Size? RememberedSize => _savedSize;

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        lock (s_open)
        {
            if (!_registered && !IsClosed)
            {
                s_open.Add(this);
                _registered = true;
            }
        }
        base.OnOpened(e);
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && HideOnClose && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            HideAndRemember();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        Unregister();
        try
        {
            _closedSource.Cancel();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("A cancellation callback of " + GetType().Name + " failed.", exception);
        }
        for (int index = _tracked.Count - 1; index >= 0; index--) DisposeQuietly(_tracked[index]);
        _tracked.Clear();
        Action[] actions = _onClosed.ToArray();
        _onClosed.Clear();
        foreach (Action action in actions) UiSafe.Run(action, GetType().Name + ".OnClosedOnce");
        base.OnClosed(e);
    }

    /// <summary>How many disposables and close actions the window still holds (tests).</summary>
    internal int HeldCount => _tracked.Count + _onClosed.Count;

    private void Deliver(Action action, string context)
    {
        if (IsClosed) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            UiSafe.Run(action, context);
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsClosed) UiSafe.Run(action, context);
        });
    }

    private void Unregister()
    {
        lock (s_open)
        {
            if (!_registered) return;
            s_open.Remove(this);
            _registered = false;
        }
    }

    private void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("Releasing a resource of " + GetType().Name + " failed.", exception);
        }
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
