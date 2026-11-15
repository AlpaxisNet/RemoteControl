using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RemoteControl.Core;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The positions of the graphical header (<c>createGraphicalButton</c>, <c>repositionGraphicalButtons</c>, <c>TRemoteFrame.cpp:1579-1612, 1993-2011</c>):
/// designed for a width of 500, the right-hand buttons move with the right edge and the visible bottom-row buttons take the right-most slots.
/// </summary>
internal static class HeaderLayout
{
    /// <summary>The height of the header.</summary>
    public const double Height = 180;

    /// <summary>Where the chat tabs start, over the header's lower edge.</summary>
    public const double TabsTop = 156;

    /// <summary>The design width.</summary>
    public const double DesignWidth = 500;

    /// <summary>The tool tips of the 15 buttons, in the order of <see cref="RcOptions.ButtonNames"/>.</summary>
    public static readonly string[] ToolTips = ["Player List", "File Browser", "Accounts", "Toalls", "Options", "Server Flags", "Folder Options", "Server Options", "Local NPCs", "Classes", "Weapons", "NPCs", "Extensions", "Level List", "Sync & Git"];

    /// <summary>The tool each button opens.</summary>
    public static readonly string[] Tools =
    [
        ToolIds.PlayerList, ToolIds.FileBrowser, ToolIds.Accounts, ToolIds.Toalls, ToolIds.RcOptions, ToolIds.ServerFlags, ToolIds.FolderConfig, ToolIds.ServerOptions,
        ToolIds.LocalNpcs, ToolIds.Classes, ToolIds.Weapons, ToolIds.Npcs, ToolIds.Extensions, ToolIds.Levels, ToolIds.Sync,
    ];

    /// <summary>The positions for a width of 500.</summary>
    public static readonly (double X, double Y)[] Positions = [(5, 15), (5, 48), (38, 15), (71, 15), (394, 15), (427, 15), (460, 15), (460, 48), (460, 81), (427, 114), (394, 114), (360, 114), (104, 15), (460, 114), (361, 15)];

    private static readonly int[] s_bottomRow = [11, 10, 9, 13];
    private static readonly double[] s_bottomSlots = [360, 394, 427, 460];

    /// <summary>The position of each button at a width, given which are visible.</summary>
    /// <param name="width">The header width.</param>
    /// <param name="visible">Whether each of the 15 buttons is visible.</param>
    /// <returns>The positions.</returns>
    public static (double X, double Y)[] Arrange(double width, IReadOnlyList<bool> visible)
    {
        var result = new (double X, double Y)[Positions.Length];
        double Shift(double x) => Math.Clamp(width - (DesignWidth - x), 0, Math.Max(0, width - 32));
        for (int index = 0; index < Positions.Length; index++)
        {
            (double x, double y) = Positions[index];
            result[index] = index < 4 || index == 12 ? (x, y) : (Shift(x), y);
        }
        int count = s_bottomRow.Count(index => visible[index]);
        int slot = s_bottomSlots.Length - count;
        foreach (int index in s_bottomRow)
            if (visible[index]) result[index] = (Shift(s_bottomSlots[slot++]), 114);
        return result;
    }
}

internal sealed partial class MainWindow
{
    private readonly ImageButton[] _buttons = new ImageButton[15];

    /// <summary>The server label of the header.</summary>
    internal ShadowLabel ServerLabel { get; } = new() { Name = "ServerLabel", Width = 300 };

    /// <summary>The players label of the header.</summary>
    internal ShadowLabel PlayersLabel { get; } = new() { Name = "PlayersLabel", Width = 300 };

    /// <summary>The NPC-server label of the header (shown while the NC is on).</summary>
    internal ShadowLabel NpcServerLabel { get; } = new() { Name = "NpcServerLabel", Width = 500, TextAlignment = TextAlignment.Center };

    /// <summary>The header buttons, in the order of <see cref="RcOptions.ButtonNames"/> (tests).</summary>
    internal IReadOnlyList<ImageButton> HeaderButtons => _buttons;

    private void InitializeMenu()
    {
        void On(MenuItem item, Action action) => item.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => action(), "MainWindow." + item.Name);
        On(MenuPlayerList, () => OpenTool(ToolIds.PlayerList));
        On(MenuAccounts, () => OpenTool(ToolIds.Accounts));
        On(MenuToalls, () => OpenTool(ToolIds.Toalls));
        On(MenuFileBrowser, () => OpenTool(ToolIds.FileBrowser));
        On(MenuRcOptions, () => OpenTool(ToolIds.RcOptions));
        On(MenuServerOptions, () => OpenTool(ToolIds.ServerOptions));
        On(MenuFolderConfig, () => OpenTool(ToolIds.FolderConfig));
        On(MenuNpcs, () => OpenTool(ToolIds.Npcs));
        On(MenuClasses, () => OpenTool(ToolIds.Classes));
        On(MenuWeapons, () => OpenTool(ToolIds.Weapons));
        On(MenuServerFlags, () => OpenTool(ToolIds.ServerFlags));
        On(MenuLocalNpcs, () => OpenTool(ToolIds.LocalNpcs));
        On(MenuExtensions, () => OpenTool(ToolIds.Extensions));
        On(MenuServerList, () => _context.Shell.ShowServerList(_context));
        On(MenuSignOut, SignOut);
        On(HeaderServerList, () => _context.Shell.ShowServerList(_context));
        On(HeaderSignOut, SignOut);
    }

    private void InitializeHeader()
    {
        RcOptions options = _context.App.Options.Current;
        HeaderBackground.Log = _context.App.Log;
        HeaderBackground.ApplyBackgroundOptions(_context.App.Paths, options);
        for (int index = 0; index < _buttons.Length; index++)
        {
            ImageButton button = ImageButton.FromAssets(_context.App.Assets, options.ButtonImages[index], options.ButtonImagesPressed[index]);
            button.Name = "Button_" + RcOptions.ButtonNames[index];
            ToolTip.SetTip(button, HeaderLayout.ToolTips[index]);
            string tool = HeaderLayout.Tools[index];
            button.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => OpenTool(tool), "MainWindow.Header." + tool);
            _buttons[index] = button;
            HeaderCanvas.Children.Add(button);
        }
        AddLabel(ServerLabel, 10, 90);
        AddLabel(PlayersLabel, 10, 110);
        AddLabel(NpcServerLabel, 0, 130);
        NpcServerLabel.Text = options.LabelNpcServer;
        UpdateServerLabel();
        Header.SizeChanged += (_, _) => UiSafe.Run(LayoutHeader, "MainWindow.HeaderSize");
    }

    private void AddLabel(ShadowLabel label, double x, double y)
    {
        label.Height = 22;
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        HeaderCanvas.Children.Add(label);
    }

    private void UpdateButtonImages()
    {
        RcOptions options = _context.App.Options.Current;
        for (int index = 0; index < _buttons.Length; index++)
        {
            string normal = options.ButtonImages[index];
            _buttons[index].Source = _context.App.Assets.Get(normal);
            _buttons[index].PressedSource = _context.App.Assets.Get(_context.App.Assets.PressedImageName(normal, options.ButtonImagesPressed[index]));
        }
    }

    /// <summary>Classic menu bar or graphical header, as <c>graphicalmenu</c> says (switched at once; the C++ needed a new window).</summary>
    private void ApplyLayout()
    {
        bool graphical = _context.App.Options.Current.GraphicalMenu;
        MainMenu.IsVisible = !graphical;
        Header.IsVisible = graphical;
        ChatTabs.Margin = graphical ? new Thickness(0, HeaderLayout.TabsTop, 0, 0) : default;
        ChatEntry.Classes.Set("graphical", graphical);
        LayoutHeader();
    }

    private void LayoutHeader()
    {
        if (_context is null || !Header.IsVisible) return;
        double width = Header.Bounds.Width > 0 ? Header.Bounds.Width : (Bounds.Width > 0 ? Bounds.Width : HeaderLayout.DesignWidth);
        bool[] visible = _buttons.Select(button => button.IsVisible).ToArray();
        (double X, double Y)[] positions = HeaderLayout.Arrange(width, visible);
        for (int index = 0; index < _buttons.Length; index++)
        {
            Canvas.SetLeft(_buttons[index], positions[index].X);
            Canvas.SetTop(_buttons[index], positions[index].Y);
        }
        double labelWidth = Math.Min(HeaderLayout.DesignWidth, width);
        NpcServerLabel.Width = labelWidth;
        Canvas.SetLeft(NpcServerLabel, Math.Max(0, (width - labelWidth) / 2));
    }
}
