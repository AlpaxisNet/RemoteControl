using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Scripts;

/// <summary>One field of a <see cref="FormDialog"/>.</summary>
/// <param name="Key">What identifies it in the answer (and the text box's name).</param>
/// <param name="Label">The label, for example <c>Name:</c>.</param>
/// <param name="Initial">The starting text.</param>
/// <param name="Choices">Proposals for an editable choice (the NPC types), or null.</param>
internal sealed record FormField(string Key, string Label, string Initial = "", IReadOnlyList<string>? Choices = null);

/// <summary>
/// The small entry dialogs of the lists (C++ "Add Weapon/GUI Script", "Add Class", "Add NPC", "Warp NPC"): labelled text fields and
/// two buttons. Asynchronous: it never runs a nested main loop (review defect D-19).
/// </summary>
internal sealed class FormDialog : RcWindow
{
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _result = new();

    public FormDialog() : this(null, "Dialog", Array.Empty<FormField>(), "OK", "Cancel")
    {
    }

    public FormDialog(AppServices? services, string title, IReadOnlyList<FormField> fields, string okText, string cancelText) : base(services)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(10), RowSpacing = 6, ColumnSpacing = 8 };
        for (int index = 0; index < fields.Count; index++)
        {
            FormField field = fields[index];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, index);
            grid.Children.Add(label);
            Control input;
            if (field.Choices is { } choices)
            {
                input = new AutoCompleteBox { Name = field.Key, Text = field.Initial, ItemsSource = choices, MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.None };
            }
            else
            {
                input = new TextBox { Name = field.Key, Text = field.Initial };
            }
            Grid.SetRow(input, index);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            _fields[field.Key] = input;
        }
        var ok = new Button { Name = "FormOk", Content = okText, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Name = "FormCancel", Content = cancelText, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Accept(), "form ok");
        cancel.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Cancel(), "form cancel");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(10, 0, 10, 10), Children = { cancel, ok } };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(grid);
        Content = root;
        Closed += (_, _) => _result.TrySetResult(null);
        Opened += (_, _) => (_fields.Values.FirstOrDefault() as InputElement)?.Focus();
    }

    /// <summary>The answer: the fields' texts by key, or null for Cancel.</summary>
    public Task<IReadOnlyDictionary<string, string>?> Result => _result.Task;

    /// <summary>Sets a field (tests).</summary>
    public void Set(string key, string text)
    {
        if (!_fields.TryGetValue(key, out Control? control)) return;
        if (control is TextBox box) box.Text = text;
        else if (control is AutoCompleteBox auto) auto.Text = text;
    }

    /// <summary>A field's text.</summary>
    public string Get(string key) => _fields.TryGetValue(key, out Control? control) ? control switch
    {
        TextBox box => box.Text ?? string.Empty,
        AutoCompleteBox auto => auto.Text ?? string.Empty,
        _ => string.Empty,
    } : string.Empty;

    /// <summary>OK.</summary>
    public void Accept()
    {
        _result.TrySetResult(_fields.Keys.ToDictionary(key => key, Get, StringComparer.Ordinal));
        if (!IsClosed) Close();
    }

    /// <summary>Cancel.</summary>
    public void Cancel()
    {
        _result.TrySetResult(null);
        if (!IsClosed) Close();
    }

    /// <summary>Shows a form for an owner and waits.</summary>
    public static async Task<IReadOnlyDictionary<string, string>?> AskAsync(Window owner, AppServices? services, string title, IReadOnlyList<FormField> fields, string okText, string cancelText = "Cancel")
    {
        var dialog = new FormDialog(services, title, fields, okText, cancelText);
        if (owner.IsVisible) _ = dialog.ShowDialog(owner);
        else dialog.Show();
        return await dialog.Result.ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
    }
}
