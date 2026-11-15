using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Start;

/// <summary>A theme of the theme picker.</summary>
/// <param name="Id">The theme id.</param>
/// <param name="Text">Its name.</param>
internal sealed record ThemeChoice(string Id, string Text)
{
    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// The list-server settings, titled "RC settings" as in the C++ (<c>TServerList::openListServerSettings</c>): the saved profiles with
/// new, delete, move up and move down; name, host and port of the selected profile, saved 400 ms after the last change (as the C++
/// did) through <see cref="ListServerProfileStore"/>, which checks them (name and host given, port 1 to 65535, no duplicate name); the
/// built-in Alpaxis profile, which can be neither edited, moved nor deleted; and the theme picker with Close. Choosing a profile makes
/// it the one new accounts are associated with. The window is never modal (D16).
/// </summary>
internal sealed partial class ListServerSettingsWindow : RcWindow
{
    /// <summary>How long after the last keystroke an edit is saved.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>The order of the C++ theme picker.</summary>
    internal static readonly string[] ThemeOrder =
        ["system", "dark", "dracula", "material", "ayu-mirage", "nord", "monokai", "one-dark", "tokyo-night", "gruvbox", "solarized", "catppuccin", "light"];

    private readonly StartController _controller;
    private readonly AppServices _services;
    private readonly Debouncer _save;
    private string? _editingName;
    private bool _updating;

    /// <summary>For the XAML previewer only.</summary>
    public ListServerSettingsWindow() : this(null!, null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="controller">The controller.</param>
    /// <param name="services">The program's services.</param>
    public ListServerSettingsWindow(StartController controller, AppServices services) : base(services)
    {
        InitializeComponent();
        _controller = controller;
        _services = services;
        ProfileCombo.ItemsSource = Choices;
        _save = Track(UiDispatch.Debounce(SaveDelay, ApplyEdit, "RcSettings.Save", this));
        if (services is null) return;

        foreach (string id in ThemeOrder) Themes.Add(new ThemeChoice(id, ThemeManager.DisplayName(id)));
        ThemeCombo.ItemsSource = Themes;
        SelectTheme(services.Options.Current.Theme);

        NewProfileButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => NewProfile(), "RcSettings.New");
        DeleteProfileButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => DeleteSelected(), "RcSettings.Delete");
        MoveUpButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => MoveSelected(-1), "RcSettings.Up");
        MoveDownButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => MoveSelected(1), "RcSettings.Down");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "RcSettings.Close");
        ProfileCombo.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => OnProfilePicked(), "RcSettings.Picked");
        ThemeCombo.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => OnThemePicked(), "RcSettings.Theme");
        foreach (TextBox field in new[] { NameField, HostField, PortField })
            field.PropertyChanged += (_, e) =>
            {
                // TextChanged is raised later than the change; the property change is not, so programmatic fills are told apart.
                if (e.Property == TextBox.TextProperty && !_updating) UiSafe.Run(_save.Trigger, "RcSettings.Edited");
            };
        Listen(h => services.Profiles.Changed += h, h => services.Profiles.Changed -= h, (_, _) => { if (!_updating) Reload(_editingName); }, "RcSettings.ProfilesChanged");
        Listen<OptionsChangedEventArgs>(h => services.Options.Changed += h, h => services.Options.Changed -= h, (_, e) =>
        {
            if (e.Contains(OptionKeys.Theme)) SelectTheme(e.Current.Theme);
        }, "RcSettings.OptionsChanged");

        Reload(controller.CurrentProfile.Name);
    }

    /// <summary>The profiles of the combo (built-in first).</summary>
    internal ObservableCollection<ProfileChoice> Choices { get; } = new();

    /// <summary>The themes of the picker.</summary>
    internal ObservableCollection<ThemeChoice> Themes { get; } = new();

    /// <summary>The profile edited, or null for a new one that is not saved yet.</summary>
    internal string? EditingName => _editingName;

    /// <summary>The message under the fields ("Saved.", the reason a change was refused).</summary>
    internal string Status => StatusText.Text ?? string.Empty;

    /// <summary>Whether an edit waits to be saved.</summary>
    internal bool IsSavePending => _save.IsPending;

    /// <summary>Saves a waiting edit now.</summary>
    internal void FlushPendingSave() => _save.Flush();

    /// <summary>Selects a profile by name.</summary>
    /// <param name="name">The profile name.</param>
    internal void SelectProfile(string name)
    {
        int index = IndexOf(name);
        if (index >= 0) ProfileCombo.SelectedIndex = index;
    }

    /// <summary>Starts a new profile (the "+" button): empty name and host, port 14922.</summary>
    internal void NewProfile()
    {
        _save.Flush();
        _updating = true;
        try
        {
            ProfileCombo.SelectedIndex = -1;
            _editingName = null;
            NameField.Text = string.Empty;
            HostField.Text = string.Empty;
            PortField.Text = ListServerProfile.DefaultPort.ToString(CultureInfo.InvariantCulture);
            SetFieldsEnabled(true);
        }
        finally
        {
            _updating = false;
        }
        ShowStatus(string.Empty);
        UpdateButtons();
        NameField.Focus();
    }

    /// <summary>Fills the fields as a user would type them (tests); the save follows after <see cref="SaveDelay"/> or <see cref="FlushPendingSave"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="host">The host.</param>
    /// <param name="port">The port text.</param>
    internal void Edit(string name, string host, string port)
    {
        NameField.Text = name;
        HostField.Text = host;
        PortField.Text = port;
    }

    /// <summary>Deletes the selected profile (refused for the built-in one).</summary>
    internal void DeleteSelected()
    {
        _save.Cancel();
        if (ProfileCombo.SelectedItem is not ProfileChoice { Profile: { } profile }) return;
        int index = ProfileCombo.SelectedIndex;
        ProfileChangeStatus status = _services.Profiles.Remove(profile.Name);
        if (status != ProfileChangeStatus.Saved)
        {
            ShowStatus(Describe(status, profile.Name), error: true);
            return;
        }
        IReadOnlyList<ListServerProfile> profiles = _services.Profiles.Profiles;
        ListServerProfile next = profiles[Math.Clamp(index, 0, profiles.Count - 1)];
        Reload(next.Name);
        ShowStatus("Removed.");
    }

    /// <summary>Moves the selected profile up or down among the user's profiles (the built-in one stays first).</summary>
    /// <param name="direction">-1 up, 1 down.</param>
    internal void MoveSelected(int direction)
    {
        _save.Flush();
        if (ProfileCombo.SelectedItem is not ProfileChoice { Profile: { } profile }) return;
        int userIndex = -1;
        IReadOnlyList<ListServerProfile> user = _services.Profiles.UserProfiles;
        for (int index = 0; index < user.Count; index++)
            if (string.Equals(user[index].Name, profile.Name, StringComparison.OrdinalIgnoreCase)) userIndex = index;
        ProfileChangeStatus status = userIndex < 0 ? ProfileChangeStatus.BuiltIn : _services.Profiles.Move(profile.Name, userIndex + direction);
        if (status != ProfileChangeStatus.Saved)
        {
            ShowStatus(Describe(status, profile.Name), error: true);
            return;
        }
        Reload(profile.Name);
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) _save.Flush();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _controller?.OnListServerSettingsClosed(this);
    }

    private void Reload(string? selectName)
    {
        IReadOnlyList<ListServerProfile> profiles = _services.Profiles.Profiles;
        _updating = true;
        try
        {
            Choices.Clear();
            foreach (ListServerProfile profile in profiles) Choices.Add(new ProfileChoice(AccountPresentation.ProfileText(profile), profile.Association, profile));
        }
        finally
        {
            _updating = false;
        }
        int index = selectName is null ? -1 : IndexOf(selectName);
        if (index < 0 && selectName is not null) index = 0;
        if (index >= 0)
        {
            if (ProfileCombo.SelectedIndex == index) OnProfilePicked();
            else ProfileCombo.SelectedIndex = index;
        }
        UpdateButtons();
    }

    private void OnProfilePicked()
    {
        if (_updating || ProfileCombo.SelectedItem is not ProfileChoice { Profile: { } profile }) return;
        if (_save.IsPending)
        {
            // The edit of the profile shown before is saved first (the fields still hold it), then the profile picked is shown. Both
            // change the selection, which cannot be done inside this selection handler: they run right after it.
            _save.Cancel();
            string picked = profile.Name;
            UiDispatch.Post(() =>
            {
                if (IsClosed) return;
                ApplyEdit();
                if (IndexOf(picked) is var index and >= 0 && ProfileCombo.SelectedIndex != index) ProfileCombo.SelectedIndex = index;
                else OnProfilePicked();
            }, "RcSettings.SaveBeforePick");
            return;
        }
        _updating = true;
        try
        {
            _editingName = profile.Name;
            NameField.Text = profile.Name;
            HostField.Text = profile.Host;
            PortField.Text = profile.Port.ToString(CultureInfo.InvariantCulture);
            SetFieldsEnabled(!profile.IsBuiltIn);
        }
        finally
        {
            _updating = false;
        }
        _controller.CurrentProfile = profile;
        ShowStatus(profile.IsBuiltIn ? $"The {ListServerProfile.DefaultName} profile is built in and cannot be changed." : string.Empty);
        UpdateButtons();
    }

    private void ApplyEdit()
    {
        if (IsClosed && _services is null) return;
        if (_editingName is not null && string.Equals(_editingName, ListServerProfile.DefaultName, StringComparison.OrdinalIgnoreCase)) return;
        string name = (NameField.Text ?? string.Empty).Trim();
        string host = (HostField.Text ?? string.Empty).Trim();
        string portText = (PortField.Text ?? string.Empty).Trim();
        if (name.Length == 0 || host.Length == 0 || portText.Length == 0)
        {
            ShowStatus("Complete the name, host, and port.", error: true);
            return;
        }
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || !ListServerProfile.IsValidPort(port))
        {
            ShowStatus("The port must be a number from 1 to 65535.", error: true);
            return;
        }
        var profile = new ListServerProfile(name, host, port);
        ProfileChangeStatus status;
        _updating = true;
        try
        {
            status = _editingName is null ? _services.Profiles.Add(profile) : _services.Profiles.Update(_editingName, profile);
        }
        finally
        {
            _updating = false;
        }
        if (status != ProfileChangeStatus.Saved)
        {
            ShowStatus(Describe(status, name), error: true);
            return;
        }
        _editingName = profile.Name;
        Reload(profile.Name);
        _controller.CurrentProfile = profile;
        ShowStatus("Saved.");
    }

    private string Describe(ProfileChangeStatus status, string name) => status switch
    {
        ProfileChangeStatus.Invalid => "Complete the name, host, and port (no tabs or line breaks).",
        ProfileChangeStatus.DuplicateName => $"There is already a profile named \"{name}\".",
        ProfileChangeStatus.BuiltIn => $"The {ListServerProfile.DefaultName} profile is built in and cannot be changed, moved or deleted.",
        ProfileChangeStatus.NotFound => $"There is no profile named \"{name}\" any more.",
        ProfileChangeStatus.Refused => "The profiles file could not be read and is kept as it is, so changes are not saved.",
        ProfileChangeStatus.Failed => _services.Profiles.LastError ?? "The profiles could not be saved.",
        _ => string.Empty,
    };

    private void SelectTheme(string id)
    {
        string normalized = ThemeManager.Normalize(id);
        _updating = true;
        try
        {
            ThemeCombo.SelectedIndex = Array.IndexOf(ThemeOrder, normalized);
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnThemePicked()
    {
        if (_updating || ThemeCombo.SelectedItem is not ThemeChoice choice) return;
        _controller.ChooseTheme(choice.Id, this);
    }

    private int IndexOf(string name)
    {
        for (int index = 0; index < Choices.Count; index++)
            if (Choices[index].Profile is { } profile && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private void SetFieldsEnabled(bool enabled)
    {
        NameField.IsEnabled = enabled;
        HostField.IsEnabled = enabled;
        PortField.IsEnabled = enabled;
    }

    private void UpdateButtons()
    {
        ListServerProfile? selected = (ProfileCombo.SelectedItem as ProfileChoice)?.Profile;
        bool user = selected is { IsBuiltIn: false };
        DeleteProfileButton.IsEnabled = user;
        IReadOnlyList<ListServerProfile> users = _services.Profiles.UserProfiles;
        int index = -1;
        for (int position = 0; selected is not null && position < users.Count; position++)
            if (string.Equals(users[position].Name, selected.Name, StringComparison.OrdinalIgnoreCase)) index = position;
        MoveUpButton.IsEnabled = user && index > 0;
        MoveDownButton.IsEnabled = user && index >= 0 && index < users.Count - 1;
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Classes.Set("error", error);
        StatusText.Classes.Set("dim", !error);
    }
}
