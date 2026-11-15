using Avalonia.Controls;

namespace RemoteControl.Ui.Shell;

/// <summary>
/// The windows that belong to one session: they are closed when the session ends, so no window can outlive the connection it
/// talks to. Used on the UI thread only.
/// </summary>
public sealed class SessionWindows
{
    private readonly Dictionary<string, Window> _single = new(StringComparer.Ordinal);
    private readonly List<Window> _all = [];
    private bool _closing;

    /// <summary>The open windows of the session.</summary>
    public IReadOnlyList<Window> All => _all;

    /// <summary>Raised when a window was added or closed.</summary>
    public event EventHandler? Changed;

    /// <summary>The window open under a key, or null.</summary>
    /// <param name="key">The key given to <see cref="ShowSingle"/>.</param>
    /// <returns>The window, or null.</returns>
    public Window? Find(string key) => key is not null && _single.TryGetValue(key, out Window? window) ? window : null;

    /// <summary>
    /// Shows the one window a session has under a key: the existing one is brought to the front, otherwise it is created, shown
    /// and remembered until it closes.
    /// </summary>
    /// <param name="key">What the window is (a tool name, or a tool name with the thing it shows: <c>"pm:12"</c>).</param>
    /// <param name="create">Makes the window; it is not shown yet.</param>
    /// <returns>The window, or null while the session's windows are being closed.</returns>
    public Window? ShowSingle(string key, Func<Window> create)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(create);
        if (_closing) return null;
        if (_single.TryGetValue(key, out Window? existing))
        {
            if (!existing.IsVisible) existing.Show();
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        Window window = create();
        _single[key] = window;
        window.Closed += (_, _) =>
        {
            if (_single.TryGetValue(key, out Window? current) && ReferenceEquals(current, window)) _single.Remove(key);
        };
        Track(window);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Makes a window one of the session's: it is closed when the session ends. A window may be tracked before or after it is shown.</summary>
    /// <param name="window">The window.</param>
    public void Track(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_closing || _all.Contains(window)) return;
        _all.Add(window);
        window.Closed += (_, _) =>
        {
            if (_all.Remove(window)) Changed?.Invoke(this, EventArgs.Empty);
        };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes every window of the session. Windows opened while this runs are refused.</summary>
    public void CloseAll()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            foreach (Window window in _all.ToArray())
            {
                try
                {
                    window.Close();
                }
                catch (InvalidOperationException)
                {
                    // A window that is already closing: it leaves the list through its Closed event.
                }
            }
            _all.Clear();
            _single.Clear();
        }
        finally
        {
            _closing = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
