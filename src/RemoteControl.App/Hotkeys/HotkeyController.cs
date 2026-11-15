using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Hotkeys;

/// <summary>The outcome of registering a hotkey.</summary>
/// <param name="Success">Whether the hotkey is registered (or nothing was asked).</param>
/// <param name="Error">Why not.</param>
internal readonly record struct HotkeyResult(bool Success, string Error)
{
    /// <summary>Success.</summary>
    public static HotkeyResult Ok => new(true, string.Empty);

    /// <summary>A failure.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The result.</returns>
    public static HotkeyResult Fail(string error) => new(false, error);
}

/// <summary>A system's way of grabbing a key for the whole desktop; tests use a fake that grabs nothing.</summary>
internal interface IHotkeyBackend : IDisposable
{
    /// <summary>Why this desktop has no global hotkey, or null when it has one.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Raised when the hotkey was pressed, on any thread.</summary>
    event EventHandler? Pressed;

    /// <summary>Releases the current hotkey and grabs another one.</summary>
    /// <param name="hotkey">The hotkey, or null to release only.</param>
    /// <returns>Whether it was grabbed, and why not.</returns>
    Task<HotkeyResult> SetAsync(Hotkey? hotkey);
}

/// <summary>A desktop without a global hotkey (Wayland, macOS, no display).</summary>
/// <param name="reason">Why.</param>
internal sealed class NoHotkeyBackend(string reason) : IHotkeyBackend
{
    public string? UnsupportedReason { get; } = reason;

    public event EventHandler? Pressed
    {
        add { }
        remove { }
    }

    public Task<HotkeyResult> SetAsync(Hotkey? hotkey) => Task.FromResult(HotkeyResult.Ok);

    public void Dispose()
    {
    }
}

/// <summary>
/// The global visibility hotkey (option <c>globalhotkey</c>): registered at start, registered again whenever the option changes, released
/// at exit; pressing it shows or hides the program as a click on the tray icon does. A hotkey that cannot be grabbed (another program
/// owns it, an unknown key) is logged and shown once per text, never fatal (defect D4). UI thread only.
/// </summary>
internal sealed class HotkeyController : IDisposable
{
    private readonly AppServices _services;
    private readonly IHotkeyBackend _backend;
    private readonly Action _toggle;
    private readonly Func<string, Task> _showProblem;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly EventHandler<OptionsChangedEventArgs> _optionsChanged;
    private int _generation;
    private bool _disposed;

    /// <summary>Creates the controller and registers the option's hotkey.</summary>
    /// <param name="services">The program's services.</param>
    /// <param name="backend">The system's hotkey.</param>
    /// <param name="toggle">What pressing it does (on the UI thread).</param>
    /// <param name="showProblem">Shows a failure to the user (default: an error dialog).</param>
    public HotkeyController(AppServices services, IHotkeyBackend backend, Action toggle, Func<string, Task>? showProblem = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _showProblem = showProblem ?? (message => Dialogs.ShowErrorAsync("Global Hotkey", message));
        _backend.Pressed += OnPressed;
        _optionsChanged = (_, e) =>
        {
            if (e.Previous.GlobalHotkey == e.Current.GlobalHotkey) return;
            UiDispatch.Post(Apply, "global hotkey option");
        };
        _services.Options.Changed += _optionsChanged;
        Apply();
    }

    /// <summary>Why this desktop has no global hotkey, or null.</summary>
    public string? UnsupportedReason => _backend.UnsupportedReason;

    /// <summary>The last registration (tests).</summary>
    public Task<HotkeyResult> Current { get; private set; } = Task.FromResult(HotkeyResult.Ok);

    /// <summary>Registers the option's hotkey (releasing the one before).</summary>
    public void Apply()
    {
        if (_disposed) return;
        int generation = ++_generation;
        string text = _services.Options.Current.GlobalHotkey ?? string.Empty;
        Current = ApplyAsync(text, generation);
    }

    /// <summary>Releases the hotkey.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Options.Changed -= _optionsChanged;
        _backend.Pressed -= OnPressed;
        UiSafe.Run(_backend.Dispose, "release global hotkey");
    }

    private async Task<HotkeyResult> ApplyAsync(string text, int generation)
    {
        if (text.Trim().Length == 0)
        {
            return await SetAsync(null).ConfigureAwait(true);
        }
        if (_backend.UnsupportedReason is { } reason)
        {
            _services.Log.Info("The global hotkey " + text + " is not used: " + reason);
            return HotkeyResult.Fail(reason);
        }
        Hotkey? hotkey = Hotkey.Parse(text);
        HotkeyResult result = hotkey is null
            ? HotkeyResult.Fail("\"" + text + "\" names no key.")
            : await SetAsync(hotkey).ConfigureAwait(true);
        if (hotkey is null) await SetAsync(null).ConfigureAwait(true);
        if (!result.Success && generation == _generation && !_disposed)
        {
            _services.Log.Warn("The global hotkey " + text + " could not be registered: " + result.Error);
            if (_reported.Add(text))
                await UiSafe.RunAsync(() => _showProblem("The global hotkey \"" + text + "\" could not be registered.\n\n" + result.Error), "global hotkey problem").ConfigureAwait(true);
        }
        return result;
    }

    private async Task<HotkeyResult> SetAsync(Hotkey? hotkey)
    {
        try
        {
            return await _backend.SetAsync(hotkey).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _services.Log.Error("The global hotkey failed.", exception);
            return HotkeyResult.Fail(exception.Message);
        }
    }

    private void OnPressed(object? sender, EventArgs e) => UiDispatch.Post(() =>
    {
        if (!_disposed) _toggle();
    }, "global hotkey");
}
