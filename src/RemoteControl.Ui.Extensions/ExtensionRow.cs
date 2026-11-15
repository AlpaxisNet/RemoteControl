using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Extensions;

/// <summary>What a row of the extensions window asks of the window.</summary>
internal interface IExtensionRowActions
{
    void RequestEnabled(ExtensionRow row, bool enabled);

    void RequestOutputTab(ExtensionRow row, bool enabled);

    void ShowDetails(ExtensionRow row);

    void Remove(ExtensionRow row);

    void Open(ExtensionRow row);
}

/// <summary>One package in the extensions window; updated in place when the manager reports a change (the selection stays).</summary>
internal sealed class ExtensionRow : INotifyPropertyChanged
{
    private readonly IExtensionRowActions _actions;
    private bool _updating;
    private bool _enabled;
    private bool _outputToTab;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _error = string.Empty;
    private string _state = string.Empty;
    private bool _isValid;
    private bool _canRemove;
    private bool _hasLog;

    public ExtensionRow(string id, IExtensionRowActions actions)
    {
        Id = id;
        _actions = actions;
        DetailsCommand = new RowCommand(() => _actions.ShowDetails(this), "extension details");
        RemoveCommand = new RowCommand(() => _actions.Remove(this), "remove extension");
        OpenCommand = new RowCommand(() => _actions.Open(this), "open extension");
        ToggleEnabledCommand = new RowCommand(() => Enabled = !Enabled, "toggle extension");
        ToggleOutputCommand = new RowCommand(() => OutputToTab = !OutputToTab, "toggle extension output tab");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name { get; private set; } = string.Empty;

    /// <summary><c>&lt;name&gt; &lt;version&gt; (&lt;state&gt;)</c>.</summary>
    public string Title { get => _title; private set => Set(ref _title, value); }

    /// <summary>The publisher and the id.</summary>
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    /// <summary>The load or runtime error, or empty.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary><c>running</c>, <c>starting</c>, <c>stopped</c>, <c>disabled</c>, <c>failed</c> or <c>invalid</c>.</summary>
    public string State { get => _state; private set => Set(ref _state, value); }

    public bool IsValid { get => _isValid; private set => Set(ref _isValid, value); }

    public bool CanRemove { get => _canRemove; private set => Set(ref _canRemove, value); }

    public bool HasLog { get => _hasLog; private set => Set(ref _hasLog, value); }

    public string RemoveTip => _canRemove ? "Remove extension" : "Installed with the program: remove it from the program's extensions folder";

    public string OutputTip => _outputToTab ? "Disable extension output tab" : "Enable extension output tab";

    public string EnableMenuText => _enabled ? "Disable" : "Enable";

    public string OutputMenuText => _outputToTab ? "Stop sending output to tab" : "Send output to tab";

    /// <summary>Whether the extension is enabled; set by the check box, which asks the manager.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Raise(nameof(EnableMenuText));
            if (!_updating) _actions.RequestEnabled(this, value);
        }
    }

    /// <summary>Whether the output goes to a tab; set by the toggle, which asks the manager.</summary>
    public bool OutputToTab
    {
        get => _outputToTab;
        set
        {
            if (!Set(ref _outputToTab, value)) return;
            Raise(nameof(OutputTip));
            Raise(nameof(OutputMenuText));
            if (!_updating) _actions.RequestOutputTab(this, value);
        }
    }

    public ICommand DetailsCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand ToggleEnabledCommand { get; }

    public ICommand ToggleOutputCommand { get; }

    /// <summary>The state word of a package: <c>invalid</c> for a manifest error, else the manager's.</summary>
    public static string StateOf(ExtensionInfo info) => info.Manifest.IsValid ? info.StatusText : "invalid";

    /// <summary>Shows a new snapshot of the package without asking anything of the manager.</summary>
    public void Update(ExtensionInfo info, bool canRemove)
    {
        _updating = true;
        try
        {
            ExtensionManifest manifest = info.Manifest;
            Name = manifest.Name;
            State = StateOf(info);
            Title = $"{manifest.Name} {manifest.Version} ({State})".Replace("  ", " ", StringComparison.Ordinal);
            Subtitle = manifest.Publisher.Length > 0 ? manifest.Publisher + " · " + manifest.Id : manifest.Id;
            Error = manifest.IsValid ? info.Error : "Invalid manifest: " + manifest.Error;
            IsValid = manifest.IsValid;
            HasLog = info.HasLog;
            Enabled = info.Enabled;
            OutputToTab = info.OutputToTab;
            if (Set(ref _canRemove, canRemove, nameof(CanRemove))) Raise(nameof(RemoveTip));
        }
        finally
        {
            _updating = false;
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>A button's or menu item's action, run inside <see cref="UiSafe"/>.</summary>
    private sealed class RowCommand(Action execute, string context) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => UiSafe.Run(execute, context);
    }
}
