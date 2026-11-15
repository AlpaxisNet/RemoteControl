using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Scripts;

/// <summary>One NPC of the list.</summary>
internal sealed class NpcRow
{
    public NpcRow(RcNpc npc)
    {
        Id = npc.Id;
        Name = npc.Name;
        Type = npc.Type;
        Level = npc.Level;
    }

    public int Id { get; }

    public string Name { get; }

    public string Type { get; }

    public string Level { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The NPC list ("NPCs", C++ <c>TNPCList</c>): ID, Name, Type and Level, sorted by a column (click its header), type-to-search by
/// name, Refresh, Add (the Add NPC dialog), Delete with confirmation, Close (hides), double-click to edit the script, and the context
/// menu Edit Script, Edit Flags, View Attributes, Reset, Delete, Warp. The flags and attributes windows ask for their own text and
/// accept only the answer to that request, so the sync engine's flag pulls never open windows (review defect D-13).
/// </summary>
internal sealed partial class NpcListWindow : RcWindow
{
    private static readonly string[] NpcTypes = ["OBJECT", "ANIMAL", "POLICE", "HORSE", "BOAT", "HOUSEC"];
    private readonly SessionContext _context;
    private readonly ListBox _list;
    private readonly TextBlock _notConnected;
    private int _sortColumn;
    private bool _descending;
    private int? _pendingCreate;
    private string _addType = "OBJECT";
    private string _addLevel = "onlinestartlocal.nw";
    private string _addX = "30.5";
    private string _addY = "30";

    public NpcListWindow() : this(null!)
    {
    }

    public NpcListWindow(SessionContext context) : base(context?.App)
    {
        AvaloniaXamlLoader.Load(this);
        _context = context!;
        HideOnClose = true;
        Title = _context.ServerName.Length == 0 ? "NPCs" : "NPCs - " + _context.ServerName;
        _list = this.FindControl<ListBox>("NpcList")!;
        _notConnected = this.FindControl<TextBlock>("NotConnectedText")!;
        _list.ItemsSource = Rows;
        Track(ListSearch.Attach(_list, item => (item as NpcRow)?.Name));
        _list.DoubleTapped += UiSafe.Handler<TappedEventArgs>((_, _) => EditScript(), "npc double-click");
        this.FindControl<Button>("RefreshButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Refresh(), "npc refresh");
        this.FindControl<Button>("AddButton")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => AddAsync(), "npc add");
        this.FindControl<Button>("DeleteButton")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => DeleteAsync(), "npc delete");
        this.FindControl<Button>("CloseButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => HideAndRemember(), "npc close");
        this.FindControl<MenuItem>("EditScriptItem")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => EditScript(), "npc edit script");
        this.FindControl<MenuItem>("EditFlagsItem")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => EditFlags(), "npc edit flags");
        this.FindControl<MenuItem>("ViewAttributesItem")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => ViewAttributes(), "npc attributes");
        this.FindControl<MenuItem>("ResetItem")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ResetAsync(), "npc reset");
        this.FindControl<MenuItem>("DeleteItem")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => DeleteAsync(), "npc delete");
        this.FindControl<MenuItem>("WarpItem")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => WarpAsync(), "npc warp");
        string[] headers = ["SortId", "SortName", "SortType", "SortLevel"];
        for (int index = 0; index < headers.Length; index++)
        {
            int column = index;
            this.FindControl<Button>(headers[index])!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => SortBy(column), "npc sort");
        }
        Track(_context.Attach(session =>
        {
            EventHandler<NpcEventArgs> added = (_, e) => UiSafe.Run(() => OnAdded(e.Id), "npc added");
            EventHandler<NpcEventArgs> deleted = (_, e) => UiSafe.Run(() => OnDeleted(e.Id), "npc deleted");
            EventHandler ncConnected = (_, _) => UiSafe.Run(Refresh, "npc NC connected");
            EventHandler<DisconnectedEventArgs> ncDown = (_, _) => UiSafe.Run(UpdateConnectionState, "npc NC down");
            session.NpcAdded += added;
            session.NpcDeleted += deleted;
            session.NcConnected += ncConnected;
            session.NcDisconnected += ncDown;
            Dispatcher.UIThread.Post(() => UiSafe.Run(Refresh, "npc refresh"));
            return new Unsubscriber(() =>
            {
                session.NpcAdded -= added;
                session.NpcDeleted -= deleted;
                session.NcConnected -= ncConnected;
                session.NcDisconnected -= ncDown;
            });
        }));
    }

    /// <summary>The rows shown, sorted.</summary>
    internal ObservableCollection<NpcRow> Rows { get; } = [];

    /// <summary>The list.</summary>
    internal ListBox List => _list;

    /// <summary>Whether the NC is missing.</summary>
    internal bool ShowsNotConnected => _notConnected.IsVisible;

    private NpcRow? Selected => _list.SelectedItem as NpcRow;

    /// <summary>Shows the session's NPCs again, keeping the selection.</summary>
    internal void Refresh()
    {
        UpdateConnectionState();
        if (!_context.Session.IsNcAuthenticated) return;
        IReadOnlyList<RcNpc> npcs = _context.Session.Npcs;
        var ids = new HashSet<int>(npcs.Select(npc => npc.Id));
        for (int index = Rows.Count - 1; index >= 0; index--) if (!ids.Contains(Rows[index].Id)) Rows.RemoveAt(index);
        foreach (RcNpc npc in npcs) Upsert(npc);
    }

    private void UpdateConnectionState()
    {
        bool connected = _context.Session.IsNcAuthenticated;
        _notConnected.IsVisible = !connected;
        _list.IsVisible = connected;
        foreach (string name in new[] { "RefreshButton", "AddButton", "DeleteButton" }) this.FindControl<Button>(name)!.IsEnabled = connected;
    }

    private void OnAdded(int id)
    {
        UpdateConnectionState();
        RcNpc? npc = _context.Session.Npcs.FirstOrDefault(item => item.Id == id);
        if (npc is not null) Upsert(npc);
        if (_pendingCreate == id)
        {
            _pendingCreate = null;
            ScriptsSession.Open(_context, new NpcScriptTarget(id, npc?.Name ?? string.Empty));
        }
    }

    private void OnDeleted(int id)
    {
        for (int index = Rows.Count - 1; index >= 0; index--) if (Rows[index].Id == id) Rows.RemoveAt(index);
    }

    private void Upsert(RcNpc npc)
    {
        var row = new NpcRow(npc);
        int existing = IndexOf(npc.Id);
        if (existing >= 0)
        {
            NpcRow old = Rows[existing];
            if (old.Name == row.Name && old.Type == row.Type && old.Level == row.Level) return;
            bool selected = ReferenceEquals(_list.SelectedItem, old);
            Rows.RemoveAt(existing);
            Insert(row);
            if (selected) _list.SelectedItem = row;
            return;
        }
        Insert(row);
    }

    private int IndexOf(int id)
    {
        for (int index = 0; index < Rows.Count; index++) if (Rows[index].Id == id) return index;
        return -1;
    }

    private void Insert(NpcRow row)
    {
        int index = 0;
        while (index < Rows.Count && Compare(Rows[index], row) <= 0) index++;
        Rows.Insert(index, row);
    }

    private int Compare(NpcRow left, NpcRow right)
    {
        int result = _sortColumn switch
        {
            1 => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
            2 => StringComparer.OrdinalIgnoreCase.Compare(left.Type, right.Type),
            3 => StringComparer.OrdinalIgnoreCase.Compare(left.Level, right.Level),
            _ => 0,
        };
        if (result == 0) result = left.Id.CompareTo(right.Id);
        return _descending ? -result : result;
    }

    /// <summary>Sorts by a column (0 ID, 1 Name, 2 Type, 3 Level); the same column again reverses the order.</summary>
    internal void SortBy(int column)
    {
        _descending = column == _sortColumn && !_descending;
        _sortColumn = column;
        object? selected = _list.SelectedItem;
        NpcRow[] sorted = Rows.ToArray();
        Array.Sort(sorted, Compare);
        for (int target = 0; target < sorted.Length; target++)
        {
            int current = Rows.IndexOf(sorted[target]);
            if (current != target) Rows.Move(current, target);
        }
        _list.SelectedItem = selected;
    }

    internal void EditScript()
    {
        if (Selected is not { } row || !_context.Session.IsNcAuthenticated) return;
        ScriptsSession.Open(_context, new NpcScriptTarget(row.Id, row.Name));
    }

    internal void EditFlags()
    {
        if (Selected is not { } row || !_context.Session.IsNcAuthenticated) return;
        ScriptsSession.Open(_context, new NpcFlagsTarget(row.Id, row.Name));
    }

    internal void ViewAttributes()
    {
        if (Selected is not { } row || !_context.Session.IsNcAuthenticated) return;
        ScriptsSession.Open(_context, new NpcAttributesTarget(row.Id));
    }

    internal async Task ResetAsync()
    {
        if (Selected is not { } row) return;
        await _context.Session.ResetNpcAsync(row.Id).ConfigureAwait(true);
    }

    internal async Task DeleteAsync()
    {
        if (Selected is not { } row) return;
        if (!await Dialogs.ConfirmAsync("Delete NPC", "Delete NPC " + row.Id + "?", this).ConfigureAwait(true) || IsClosed) return;
        await _context.Session.DeleteNpcAsync(row.Id).ConfigureAwait(true);
    }

    internal async Task WarpAsync()
    {
        if (Selected is not { } row) return;
        FormField[] fields = [new FormField("Level", "Level:"), new FormField("X", "X:", "0"), new FormField("Y", "Y:", "0")];
        IReadOnlyDictionary<string, string>? answer = await FormDialog.AskAsync(this, _context.App, "Warp NPC", fields, "Warp").ConfigureAwait(true);
        if (answer is null || IsClosed) return;
        await _context.Session.WarpNpcAsync(row.Id, ParseNumber(answer["X"]), ParseNumber(answer["Y"]), answer["Level"].Trim()).ConfigureAwait(true);
    }

    internal async Task AddAsync()
    {
        int nextId = 1000;
        var ids = new HashSet<int>(_context.Session.Npcs.Select(npc => npc.Id));
        while (ids.Contains(nextId)) nextId++;
        FormField[] fields =
        [
            new FormField("Name", "Name:"),
            new FormField("Id", "ID:", nextId.ToString(CultureInfo.InvariantCulture)),
            new FormField("Type", "Type:", _addType, NpcTypes),
            new FormField("Scripter", "Scripter:", _context.Account),
            new FormField("Level", "Starting level:", _addLevel),
            new FormField("X", "X:", _addX),
            new FormField("Y", "Y:", _addY),
        ];
        IReadOnlyDictionary<string, string>? answer = await FormDialog.AskAsync(this, _context.App, "Add NPC", fields, "Apply").ConfigureAwait(true);
        if (answer is null || IsClosed) return;
        _addType = answer["Type"];
        _addLevel = answer["Level"];
        _addX = answer["X"];
        _addY = answer["Y"];
        if (!int.TryParse(answer["Id"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0)
        {
            await Dialogs.ShowErrorAsync("Add NPC", "The ID must be a number.", this).ConfigureAwait(true);
            return;
        }
        IRcSession session = _context.Session;
        if (!session.IsNcAuthenticated) return;
        if (await session.CreateNpcAsync(answer["Name"].Trim(), id, answer["Type"].Trim(), answer["Scripter"].Trim(), answer["Level"].Trim(), answer["X"].Trim(), answer["Y"].Trim()).ConfigureAwait(true))
            _pendingCreate = id;
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
}
