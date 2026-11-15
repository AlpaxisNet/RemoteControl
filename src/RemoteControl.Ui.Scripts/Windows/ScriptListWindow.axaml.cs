using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The weapon list ("Weapon/GUI-Script List") or the class list ("Classes") (C++ <c>TScriptList</c>): the names sorted, type-to-search,
/// Edit (or double-click), Add, Delete with confirmation, Close (hides). Updated by row from the session's events, so the selection
/// stays. Needs the NC; without it the list says so and fills in when the NC connects.
/// </summary>
internal sealed partial class ScriptListWindow : RcWindow
{
    private const int PollAttempts = 20;
    private readonly SessionContext _context;
    private readonly bool _classes;
    private readonly ListBox _list;
    private readonly TextBlock _notConnected;
    private readonly DispatcherTimer _poll;
    private string? _pendingCreate;
    private string? _pendingDelete;
    private int _pollCount;

    public ScriptListWindow() : this(null!, false)
    {
    }

    public ScriptListWindow(SessionContext context, bool classes) : base(context?.App)
    {
        AvaloniaXamlLoader.Load(this);
        _context = context!;
        _classes = classes;
        HideOnClose = true;
        string baseTitle = classes ? "Classes" : "Weapon/GUI-Script List";
        Title = _context.ServerName.Length == 0 ? baseTitle : baseTitle + " - " + _context.ServerName;
        _list = this.FindControl<ListBox>("ScriptList")!;
        _notConnected = this.FindControl<TextBlock>("NotConnectedText")!;
        _list.ItemsSource = Names;
        Track(ListSearch.Attach(_list, item => item as string));
        _list.DoubleTapped += UiSafe.AsyncHandler<TappedEventArgs>((_, _) => EditAsync(), "script list double-click");
        _list.KeyDown += UiSafe.AsyncHandler<KeyEventArgs>(async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await EditAsync().ConfigureAwait(true);
        }, "script list enter");
        this.FindControl<Button>("EditButton")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => EditAsync(), "script list edit");
        this.FindControl<Button>("AddButton")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => AddAsync(), "script list add");
        this.FindControl<Button>("DeleteButton")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => DeleteAsync(), "script list delete");
        this.FindControl<Button>("CloseButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => HideAndRemember(), "script list close");
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => UiSafe.Run(OnPoll, "script list poll");
        OnClosedOnce(_poll.Stop);
        Track(_context.Attach(session =>
        {
            EventHandler<ValueEventArgs<int>> list = (_, _) => UiSafe.Run(OnWeaponList, "weapon list");
            EventHandler<ValueEventArgs<string>> added = (_, e) => UiSafe.Run(() => OnClassAdded(e.Value), "class added");
            EventHandler<ValueEventArgs<string>> deleted = (_, e) => UiSafe.Run(() => Remove(e.Value), "class deleted");
            EventHandler ncConnected = (_, _) => UiSafe.Run(Refresh, "script list NC connected");
            EventHandler<DisconnectedEventArgs> ncDown = (_, _) => UiSafe.Run(UpdateConnectionState, "script list NC down");
            session.WeaponListReceived += list;
            session.ClassAdded += added;
            session.ClassDeleted += deleted;
            session.NcConnected += ncConnected;
            session.NcDisconnected += ncDown;
            Dispatcher.UIThread.Post(() => UiSafe.Run(Refresh, "script list refresh"));
            return new Unsubscriber(() =>
            {
                session.WeaponListReceived -= list;
                session.ClassAdded -= added;
                session.ClassDeleted -= deleted;
                session.NcConnected -= ncConnected;
                session.NcDisconnected -= ncDown;
            });
        }));
    }

    /// <summary>The names shown, sorted.</summary>
    internal ObservableCollection<string> Names { get; } = [];

    /// <summary>The list.</summary>
    internal ListBox List => _list;

    /// <summary>Whether the NC is missing.</summary>
    internal bool ShowsNotConnected => _notConnected.IsVisible;

    private string Noun => _classes ? "Class" : "Weapon/GUI Script";

    /// <summary>Shows the cache and, for weapons, asks for the list.</summary>
    internal void Refresh()
    {
        UpdateConnectionState();
        IRcSession session = _context.Session;
        if (!session.IsNcAuthenticated) return;
        Sync(_classes ? session.Classes.Select(item => item.Name) : session.Weapons.Select(item => item.Name));
        if (!_classes) UiSafe.Fire(() => session.RequestWeaponListAsync(), "request weapon list");
    }

    private void UpdateConnectionState()
    {
        bool connected = _context.Session.IsNcAuthenticated;
        _notConnected.IsVisible = !connected;
        _list.IsVisible = connected;
        foreach (string name in new[] { "EditButton", "AddButton", "DeleteButton" }) this.FindControl<Button>(name)!.IsEnabled = connected;
    }

    private void OnWeaponList()
    {
        if (_classes) return;
        UpdateConnectionState();
        IReadOnlyList<RcWeapon> weapons = _context.Session.Weapons;
        Sync(weapons.Select(item => item.Name));
        if (_pendingCreate is { } created && weapons.Any(item => item.Name == created))
        {
            _pendingCreate = null;
            ScriptsSession.Open(_context, new WeaponTarget(created));
        }
        if (_pendingDelete is { } deleted && weapons.All(item => item.Name != deleted)) _pendingDelete = null;
        if (_pendingCreate is null && _pendingDelete is null) _poll.Stop();
    }

    private void OnClassAdded(string name)
    {
        if (!_classes) return;
        Insert(name);
    }

    private void OnPoll()
    {
        if ((_pendingCreate is null && _pendingDelete is null) || ++_pollCount > PollAttempts)
        {
            _poll.Stop();
            _pendingCreate = null;
            _pendingDelete = null;
            return;
        }
        UiSafe.Fire(() => _context.Session.RequestWeaponListAsync(), "poll weapon list");
    }

    private void StartPoll()
    {
        _pollCount = 0;
        _poll.Stop();
        _poll.Start();
        UiSafe.Fire(() => _context.Session.RequestWeaponListAsync(), "request weapon list");
    }

    private void Sync(IEnumerable<string> source)
    {
        var wanted = new HashSet<string>(source.Where(name => name.Length > 0), StringComparer.Ordinal);
        for (int index = Names.Count - 1; index >= 0; index--) if (!wanted.Contains(Names[index])) Names.RemoveAt(index);
        foreach (string name in wanted) Insert(name);
    }

    private void Insert(string name)
    {
        if (name.Length == 0 || Names.Contains(name)) return;
        int index = 0;
        while (index < Names.Count && StringComparer.OrdinalIgnoreCase.Compare(Names[index], name) < 0) index++;
        Names.Insert(index, name);
    }

    private void Remove(string name)
    {
        if (!_classes) return;
        Names.Remove(name);
    }

    internal Task EditAsync()
    {
        if (_list.SelectedItem is not string name || !_context.Session.IsNcAuthenticated) return Task.CompletedTask;
        ScriptsSession.Open(_context, _classes ? new ClassTarget(name) : new WeaponTarget(name));
        return Task.CompletedTask;
    }

    internal async Task AddAsync()
    {
        FormField[] fields = _classes ? [new FormField("Name", "Name:")] : [new FormField("Name", "Name:"), new FormField("Icon", "Icon:")];
        IReadOnlyDictionary<string, string>? answer = await FormDialog.AskAsync(this, _context.App, _classes ? "Add Class" : "Add Weapon/GUI Script", fields, "Add").ConfigureAwait(true);
        if (answer is null || IsClosed) return;
        string name = answer["Name"].Trim();
        if (name.Length == 0) return;
        IRcSession session = _context.Session;
        if (!session.IsNcAuthenticated)
        {
            await Dialogs.ShowErrorAsync("Not Connected", "The NPC-Control (NC) is not connected.", this).ConfigureAwait(true);
            return;
        }
        if (_classes)
        {
            bool exists = session.Classes.Any(item => item.Name == name);
            ScriptsSession.Open(_context, new ClassTarget(name) { IsNew = !exists });
            return;
        }
        if (await session.AddWeaponAsync(name, answer["Icon"].Trim(), string.Empty).ConfigureAwait(true))
        {
            _pendingCreate = name;
            StartPoll();
        }
        else
        {
            await Dialogs.ShowErrorAsync("Add Weapon/GUI Script", "The weapon could not be added: " + (session.LastError ?? "the connection refused it."), this).ConfigureAwait(true);
        }
    }

    internal async Task DeleteAsync()
    {
        if (_list.SelectedItem is not string name) return;
        if (!await Dialogs.ConfirmAsync("Delete", "Delete " + Noun + " " + name + "?", this).ConfigureAwait(true) || IsClosed) return;
        IRcSession session = _context.Session;
        if (!session.IsNcAuthenticated) return;
        bool sent = _classes ? await session.DeleteClassAsync(name).ConfigureAwait(true) : await session.DeleteWeaponAsync(name).ConfigureAwait(true);
        if (sent && !_classes)
        {
            _pendingDelete = name;
            StartPoll();
        }
    }
}
