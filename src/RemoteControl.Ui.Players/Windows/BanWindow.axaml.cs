using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Edit Access of &lt;acc&gt; (computer: &lt;id&gt;)" (<c>TLocalBanWindow.cpp</c>), one per session: Local Ban, Global Ban, Computer
/// Ban and Global Computer Ban with the new ban type, only Local Ban with the legacy one. Each tab: "Banned for" and the ban type,
/// "Ban time left", "Reset ban time", "Reason for update" and Apply, which sends with the connection in use then and hides the
/// window. Ban History, Staff Activity, Close. Closing only hides it.
/// </summary>
internal sealed partial class BanWindow : RcWindow
{
    internal static readonly string[] ScopeTitles = { "Local Ban", "Global Ban", "Computer Ban", "Global Computer Ban" };

    private readonly PlayersSession? _owner;
    private readonly Scope[] _scopes = new Scope[4];

    /// <summary>For the designer.</summary>
    public BanWindow() : this(null)
    {
    }

    /// <summary>Creates the window (empty until <see cref="Load"/>).</summary>
    /// <param name="owner">The session.</param>
    public BanWindow(PlayersSession? owner) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        HideOnClose = true;
        for (int index = 0; index < 4; index++)
        {
            _scopes[index] = new Scope(this, index);
            Scopes.Items.Add(_scopes[index].Tab);
        }
        HistoryButton.Click += On.ClickAsync(() => RequestAsync(history: true), "Ban.History");
        ActivityButton.Click += On.ClickAsync(() => RequestAsync(history: false), "Ban.Activity");
        CloseButton.Click += On.Click(HideAndRemember, "Ban.Close");
        if (owner is not null)
        {
            EventHandler types = UiSafe.Handler((_, _) => RefreshTypes(), "Ban.Types");
            owner.BanTypesChanged += types;
            OnClosedOnce(() => owner.BanTypesChanged -= types);
        }
    }

    /// <summary>The account it shows.</summary>
    public string Account { get; private set; } = string.Empty;

    /// <summary>The computer id, or empty.</summary>
    public string ComputerId { get; private set; } = string.Empty;

    /// <summary>Whether it uses the new ban type (four tabs, <c>setban</c>).</summary>
    public bool UseNewBanType { get; private set; } = true;

    /// <summary>A tab (0 local, 1 global, 2 computer, 3 global computer) (for tests).</summary>
    internal Scope ScopeAt(int index) => _scopes[index];

    /// <summary>Shows the ban data the server sent (<c>TLocalBanWindow::open</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="computerId">The computer id, or empty.</param>
    /// <param name="details">The details: lister ban lines (new) or <c>banned=0|1</c> (legacy).</param>
    /// <param name="useNewBanType">Whether the new ban type is used.</param>
    public void Load(string account, string computerId, string details, bool useNewBanType)
    {
        Account = account ?? string.Empty;
        ComputerId = computerId ?? string.Empty;
        UseNewBanType = useNewBanType;
        Title = "Edit Access of " + Account + (ComputerId.Length == 0 ? string.Empty : " (computer: " + ComputerId + ")");
        for (int index = 0; index < 4; index++)
        {
            Scope scope = _scopes[index];
            scope.Target = index < 2 ? Account : ComputerId.Length == 0 ? string.Empty : "pc:" + ComputerId;
            scope.Tab.IsVisible = useNewBanType ? scope.Target.Length > 0 : index == 0 && Account.Length > 0;
            scope.Reset(_owner?.BanTypes ?? Array.Empty<BanType>());
        }
        if (!useNewBanType)
        {
            _scopes[0].SetBanned(BanTexts.ParseLegacyBanned(details));
        }
        else
        {
            foreach (BanRecord ban in BanTexts.ParseBans(details))
            {
                Scope scope = _scopes[ban.Scope];
                if (scope.Target.Length == 0) continue;
                scope.Apply(ban);
            }
        }
        Scope? first = _scopes.FirstOrDefault(scope => scope.Tab.IsVisible);
        if (first is not null) Scopes.SelectedItem = first.Tab;
        ShowAndActivate();
    }

    private void RefreshTypes()
    {
        foreach (Scope scope in _scopes) scope.SetTypes(_owner?.BanTypes ?? Array.Empty<BanType>());
    }

    private async Task RequestAsync(bool history)
    {
        if (_owner is null) return;
        string target = history ? (Account.Length > 0 ? Account : ComputerId.Length > 0 ? "pc:" + ComputerId : string.Empty) : Account;
        if (target.Length == 0) return;
        await _owner.SendAsync(this, session => history ? session.RequestBanHistoryAsync(target) : session.RequestStaffActivityAsync(target)).ConfigureAwait(true);
    }

    /// <summary>Sends a tab's ban (new: <c>setban</c>; legacy: the ban packet) and hides the window.</summary>
    /// <param name="index">The tab.</param>
    /// <returns>Whether it was sent.</returns>
    internal async Task<bool> ApplyAsync(int index)
    {
        if (_owner is null) return false;
        Scope scope = _scopes[index];
        if (scope.Target.Length == 0) return false;
        bool banned = scope.Banned.IsChecked == true;
        string reason = scope.Reason.Text ?? string.Empty;
        bool sent;
        if (!UseNewBanType)
        {
            if (index != 0 || Account.Length == 0) return false;
            string account = Account;
            sent = await _owner.SendAsync(this, session => session.SetLegacyPlayerBanAsync(account, banned, reason)).ConfigureAwait(true);
        }
        else
        {
            string target = scope.Target;
            string world = (index & 1) == 0 ? "local" : "all";
            string type = (scope.Type.SelectedItem as BanType)?.Name ?? string.Empty;
            string release = scope.ResetTime.IsChecked == true ? string.Empty : scope.ReleaseTime;
            sent = await _owner.SendAsync(this, session => session.SetBanAsync(target, world, banned, type, release, reason)).ConfigureAwait(true);
        }
        if (sent) HideAndRemember();
        return sent;
    }

    /// <summary>One tab of the window.</summary>
    internal sealed class Scope
    {
        private readonly BanWindow _window;
        private readonly Image _icon = new() { Width = 16, Height = 16 };

        public Scope(BanWindow window, int index)
        {
            _window = window;
            Index = index;
            Banned = new CheckBox { Content = "Banned for", Name = "Banned" + index, Margin = new Thickness(5, 0) };
            Type = new ComboBox { Name = "BanType" + index, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 200 };
            TimeLeft = new TextBlock { Text = "Ban time left: -", VerticalAlignment = VerticalAlignment.Center, Name = "TimeLeft" + index };
            ResetTime = new CheckBox { Content = "Reset ban time", Name = "ResetTime" + index, Margin = new Thickness(5, 0) };
            Reason = new TextBox { Name = "Reason" + index };
            ApplyButton = new Button { Content = "Apply", Name = "Apply" + index, MinWidth = 70, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            ApplyButton.Click += On.ClickAsync(() => _window.ApplyAsync(Index), "Ban.Apply");
            Banned.IsCheckedChanged += UiSafe.Handler<Avalonia.Interactivity.RoutedEventArgs>((_, _) => Update(), "Ban.Banned");
            Type.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => Update(), "Ban.Type");
            ResetTime.IsCheckedChanged += UiSafe.Handler<Avalonia.Interactivity.RoutedEventArgs>((_, _) => Update(), "Ban.Reset");

            var banRow = new DockPanel();
            DockPanel.SetDock(Banned, Dock.Left);
            banRow.Children.Add(Banned);
            banRow.Children.Add(Type);
            var timeRow = new DockPanel();
            DockPanel.SetDock(ResetTime, Dock.Right);
            timeRow.Children.Add(ResetTime);
            timeRow.Children.Add(TimeLeft);
            var reasonRow = new DockPanel();
            var reasonLabel = new TextBlock { Text = "Reason for update:", MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(reasonLabel, Dock.Left);
            reasonRow.Children.Add(reasonLabel);
            reasonRow.Children.Add(Reason);
            var page = new StackPanel { Margin = new Thickness(5), Spacing = 6 };
            page.Children.Add(banRow);
            page.Children.Add(timeRow);
            page.Children.Add(reasonRow);
            page.Children.Add(ApplyButton);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            header.Children.Add(_icon);
            header.Children.Add(new TextBlock { Text = ScopeTitles[index], VerticalAlignment = VerticalAlignment.Center });
            Tab = new TabItem { Header = header, Content = page, Name = "Scope" + index, FontSize = 13 };
            UpdateIcon();
        }

        public int Index { get; }

        public TabItem Tab { get; }

        public CheckBox Banned { get; }

        public ComboBox Type { get; }

        public TextBlock TimeLeft { get; }

        public CheckBox ResetTime { get; }

        public TextBox Reason { get; }

        public Button ApplyButton { get; }

        public string Target { get; set; } = string.Empty;

        public string ReleaseTime { get; private set; } = string.Empty;

        public void Reset(IReadOnlyList<BanType> types)
        {
            bool enabled = Target.Length > 0;
            Banned.IsEnabled = Type.IsEnabled = ResetTime.IsEnabled = Reason.IsEnabled = enabled;
            Banned.IsChecked = false;
            ResetTime.IsChecked = false;
            Reason.Text = string.Empty;
            ReleaseTime = string.Empty;
            Type.ItemsSource = types.ToArray();
            Type.SelectedIndex = types.Count == 0 ? -1 : 0;
            Update();
        }

        public void SetTypes(IReadOnlyList<BanType> types)
        {
            string? selected = (Type.SelectedItem as BanType)?.Name;
            BanType[] items = types.ToArray();
            Type.ItemsSource = items;
            int index = selected is null ? -1 : Array.FindIndex(items, type => type.Name == selected);
            Type.SelectedIndex = index >= 0 ? index : items.Length == 0 ? -1 : 0;
            Update();
        }

        public void SetBanned(bool banned)
        {
            Banned.IsChecked = banned;
            Update();
        }

        public void Apply(BanRecord ban)
        {
            Banned.IsChecked = true;
            if (Type.ItemsSource is BanType[] items)
            {
                int index = Array.FindIndex(items, type => type.Name == ban.BanType);
                if (index >= 0) Type.SelectedIndex = index;
            }
            ReleaseTime = ban.ReleaseTime;
            Reason.Text = ban.Reason;
            Update();
        }

        private void Update()
        {
            long seconds = (Type.SelectedItem as BanType)?.Seconds ?? 0;
            TimeLeft.Text = "Ban time left: " + (Banned.IsChecked == true ? BanTexts.TimeLeft(ResetTime.IsChecked == true ? null : ReleaseTime, seconds, DateTimeOffset.UtcNow) : "-");
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (_window.Services is { } services) _icon.Source = services.Assets.BanIcon(Banned.IsChecked == true);
        }
    }
}
