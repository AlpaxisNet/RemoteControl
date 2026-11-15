using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Extensions;

/// <summary>
/// The details of one package (<c>showDetails</c>, <c>TExtensions.cpp:1004-1042</c>), non-modal (D-19: the C++ ran a nested main loop):
/// id, version, state, runtime, entry, publisher, API revision, discovery mode, manifest file and folder; a check box per requested
/// capability (only the checked names are saved, at once); UI slots, themes, commands, read-only views and window actions; the
/// manifest or runtime error in full; Open and Close. It follows the package's changes and closes when the package goes away.
/// </summary>
internal sealed partial class ExtensionDetailsWindow : RcWindow
{
    private readonly ExtensionsHost _host;
    private readonly Dictionary<string, CheckBox> _capabilities = new(StringComparer.Ordinal);
    private ExtensionInfo _info;
    private bool _updating;

    /// <summary>For the XAML previewer only.</summary>
    public ExtensionDetailsWindow() : this(null!, null!, null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="host">The program's extensions.</param>
    /// <param name="services">The program's services.</param>
    /// <param name="info">The package.</param>
    public ExtensionDetailsWindow(ExtensionsHost host, AppServices services, ExtensionInfo info) : base(services)
    {
        InitializeComponent();
        _host = host;
        _info = info;
        if (info is null) return;
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "ExtensionDetails.Close");
        OpenButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) =>
        {
            if (!_host.OpenPackage(_info))
                UiSafe.Fire(() => Dialogs.ShowErrorAsync("Unable to open extension path", "The target path could not be opened:\n" + _info.Manifest.OpenPath, this), "ExtensionDetails.OpenFailed");
        }, "ExtensionDetails.Open");
        foreach (string capability in info.Manifest.RequestedCapabilities.Distinct(StringComparer.Ordinal))
        {
            var box = new CheckBox { Content = capability, Name = "Capability_" + capability };
            box.IsCheckedChanged += UiSafe.Handler<RoutedEventArgs>((_, _) => SaveApprovals(), "ExtensionDetails.Approve");
            _capabilities[capability] = box;
            CapabilityList.Children.Add(box);
        }
        NoCapabilities.IsVisible = _capabilities.Count == 0;
        CapabilityNote.IsVisible = _capabilities.Count > 0;
        Update(info);
    }

    /// <summary>The check box of a requested capability (tests).</summary>
    internal CheckBox? CapabilityBox(string capability) => _capabilities.GetValueOrDefault(capability);

    /// <summary>Shows a new snapshot of the package.</summary>
    internal void Update(ExtensionInfo info)
    {
        _info = info;
        ExtensionManifest manifest = info.Manifest;
        Title = manifest.Name.Length > 0 ? manifest.Name : manifest.Id;
        SummaryText.Text = SummaryOf(info);
        ContributionsText.Text = $"UI slots: {Join(manifest.UiContributions)}\nThemes: {Join(manifest.Themes)}\nCommands/shortcuts: {Join(manifest.Commands)}\nRead-only views: {Join(manifest.ReadOnlyViews)}";
        ActionsText.Text = manifest.WindowActions.Length == 0
            ? "none"
            : string.Join("\n", manifest.WindowActions.Select(action => DescribeAction(manifest, action)));
        string error = !manifest.IsValid ? "Manifest error: " + manifest.Error : info.Error.Length > 0 ? "Runtime error: " + info.Error : string.Empty;
        ErrorTitle.IsVisible = error.Length > 0;
        ErrorText.IsVisible = error.Length > 0;
        ErrorText.Text = error;
        OpenButton.IsEnabled = manifest.IsValid;
        _updating = true;
        try
        {
            foreach ((string name, CheckBox box) in _capabilities) box.IsChecked = info.ApprovedCapabilities.Contains(name);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The summary lines: those of the C++ dialog, then the API revision, the discovery mode, the manifest file and the folder.</summary>
    internal static string SummaryOf(ExtensionInfo info)
    {
        ExtensionManifest manifest = info.Manifest;
        string text = info.DetailsText.Replace("Status: " + info.StatusText, "Status: " + ExtensionRow.StateOf(info), StringComparison.Ordinal);
        string discovery = manifest.AutoDiscover ? "automatic discovery (the runtime finds the package's files)" : "entry file";
        text += $"\nAPI revision: {manifest.Api}\nDiscovery mode: {discovery}";
        if (manifest.ManifestFile.Length > 0) text += "\nManifest: " + manifest.ManifestFile;
        text += "\nFolder: " + manifest.Directory;
        return text;
    }

    private static string DescribeAction(ExtensionManifest manifest, ExtensionWindowAction action)
    {
        string placement = action.Placement == ExtensionActionPlacement.Bottom ? "bottom" : "top";
        string text = $"{action.Label} ({action.Id}) in {action.Kind}, {placement} bar";
        ExtensionIcon icon = manifest.ResolveIcon(action);
        if (icon.Kind == ExtensionIconKind.File) text += ", icon " + icon.Value;
        else if (icon.Kind == ExtensionIconKind.Named) text += ", icon name " + icon.Value;
        return text;
    }

    private static string Join(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);
        return joined.Length == 0 ? "none" : joined;
    }

    private void SaveApprovals()
    {
        if (_updating || IsClosed) return;
        IEnumerable<string> approved = _capabilities.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        ExtensionCallResult result = _host.SetApprovedCapabilities(_info.Id, approved);
        if (!result.Success) UiSafe.Fire(() => Dialogs.ShowErrorAsync("Extension Capabilities", result.Error, this), "ExtensionDetails.ApproveFailed");
    }
}
