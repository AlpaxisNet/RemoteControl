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
    private int _loads;

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

    /// <summary>The clock that counts down the seconds left of a ban that came (tests move it).</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Shows the ban data the server sent (<c>TLocalBanWindow::open</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="computerId">The computer id, or empty.</param>
    /// <param name="details">The details: lister ban lines (new) or <c>banned=0|1</c> (legacy).</param>
    /// <param name="useNewBanType">Whether the new ban type is used.</param>
    public void Load(string account, string computerId, string details, bool useNewBanType)
    {
        _loads++;
        Account = account ?? string.Empty;
        // Alpaxis sends -1 when the account is not online here: then there is no computer to ban.
        ComputerId = BanTexts.IsComputerId(computerId) ? computerId : string.Empty;
        // A guest of Alpaxis is "pc:<id>": it has only its computer, whose bans belong on the computer tabs.
        bool guest = useNewBanType && Account.StartsWith("pc:", StringComparison.Ordinal) && BanTexts.IsComputerId(Account[3..]);
        if (guest && ComputerId.Length == 0) ComputerId = Account[3..];
        UseNewBanType = useNewBanType;
        Title = "Edit Access of " + Account + (ComputerId.Length == 0 || guest ? string.Empty : " (computer: " + ComputerId + ")");
        for (int index = 0; index < 4; index++)
        {
            Scope scope = _scopes[index];
            scope.Target = index < 2 ? (guest ? string.Empty : Account) : ComputerId.Length == 0 ? string.Empty : "pc:" + ComputerId;
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
        int loads = _loads;
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
            var chosen = scope.Type.SelectedItem as BanType;
            string type = chosen?.Name ?? string.Empty;
            bool reset = scope.ResetTime.IsChecked == true;
            // A ban that is set carries its length: Alpaxis takes a setban without releasetime as a ban without an end, and counts the
            // length from the moment it arrives, so a ban that is kept sends the seconds that are left now. A lift keeps what it sent
            // before (nothing to time).
            bool ended = false;
            string release = banned ? BanTexts.ReleaseToSend(scope.ReleaseTime, reset, chosen?.Seconds ?? 0, scope.ElapsedSeconds(), out ended) : reset ? string.Empty : scope.ReleaseTime;
            if (banned && ended)
            {
                string again = "The ban of " + target + " has ended since this window showed it.\n\nBan " + target + " again for " + (chosen?.Label ?? "(no type)") + "?";
                if (!await Dialogs.ConfirmAsync("Edit Access", again, this, "Ban again").ConfigureAwait(true) || IsClosed) return false;
            }

            if (banned && release.Length == 0)
            {
                string question = "The length of the ban type \"" + (type.Length == 0 ? "(none)" : type) + "\" is not known, so the server would ban " + target + " without an end.\n\nBan without an end?";
                if (!await Dialogs.ConfirmAsync("Edit Access", question, this, "Ban without an end").ConfigureAwait(true) || IsClosed) return false;
            }

            sent = await _owner.SendAsync(this, session => session.SetBanAsync(target, world, banned, type, release, reason)).ConfigureAwait(true);
        }
        // Data that arrived while a question was open (another account) stays on screen.
        if (sent && loads == _loads) HideAndRemember();
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

        /// <summary>The type of the ban that came, or null for a tab without one.</summary>
        public string? ReceivedType { get; private set; }

        /// <summary>When the ban came, or null.</summary>
        public DateTimeOffset? LoadedAt { get; private set; }

        /// <summary>The seconds since the ban came (0 without one).</summary>
        public long ElapsedSeconds() => LoadedAt is { } at ? (long)Math.Max(0, (_window.Now() - at).TotalSeconds) : 0;

        public void Reset(IReadOnlyList<BanType> types)
        {
            bool enabled = Target.Length > 0;
            Banned.IsEnabled = Type.IsEnabled = ResetTime.IsEnabled = Reason.IsEnabled = enabled;
            Banned.IsChecked = false;
            ResetTime.IsChecked = false;
            Reason.Text = string.Empty;
            ReleaseTime = string.Empty;
            ReceivedType = null;
            LoadedAt = null;
            Type.ItemsSource = types.ToArray();
            Type.SelectedIndex = types.Count == 0 ? -1 : 0;
            Update();
        }

        public void SetTypes(IReadOnlyList<BanType> types)
        {
            var current = Type.SelectedItem as BanType;
            BanType[] items = types.ToArray();
            if (ReceivedType is { } received && (current is null || current.NotListed))
            {
                // The ban's own type, now that the list is there (or still not in it).
                Select(received, items);
                Update();
                return;
            }

            Type.ItemsSource = items;
            int index = current is null ? -1 : Array.FindIndex(items, type => type.Name == current.Name);
            Type.SelectedIndex = index >= 0 ? index : items.Length == 0 ? -1 : 0;
            Update();
        }

        /// <summary>Selects a type by name; one that is not in the list is shown first, as it is, so that applying keeps it.</summary>
        private void Select(string name, BanType[] listed)
        {
            int index = Array.FindIndex(listed, type => type.Name == name);
            if (index >= 0)
            {
                Type.ItemsSource = listed;
                Type.SelectedIndex = index;
                return;
            }

            Type.ItemsSource = new[] { new BanType(name, 0) { NotListed = true } }.Concat(listed).ToArray();
            Type.SelectedIndex = 0;
        }

        public void SetBanned(bool banned)
        {
            Banned.IsChecked = banned;
            Update();
        }

        public void Apply(BanRecord ban)
        {
            Banned.IsChecked = true;
            ReceivedType = ban.BanType;
            LoadedAt = _window.Now();
            BanType[] listed = (Type.ItemsSource as BanType[] ?? Array.Empty<BanType>()).Where(type => !type.NotListed).ToArray();
            Select(ban.BanType, listed);
            ReleaseTime = ban.ReleaseTime;
            Reason.Text = ban.Reason;
            Update();
        }

        private void Update()
        {
            long seconds = (Type.SelectedItem as BanType)?.Seconds ?? 0;
            (string left, bool ended) = BanTexts.Remaining(ReleaseTime, ElapsedSeconds());
            string? shown = ResetTime.IsChecked == true || ended ? null : left;
            TimeLeft.Text = "Ban time left: " + (Banned.IsChecked == true ? BanTexts.TimeLeft(shown, seconds, _window.Now()) : "-");
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            if (_window.Services is { } services) _icon.Source = services.Assets.BanIcon(Banned.IsChecked == true);
        }
    }
}
