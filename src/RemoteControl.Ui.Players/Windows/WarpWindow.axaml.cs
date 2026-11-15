using System.Globalization;
using Avalonia.Input;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Warp Player" (<c>warpSelectedPlayer</c>, <c>TPlayerList.cpp:1882-1937</c>): level (the player's, unless "Offline"), X and Y
/// (0), the C++ messages when something is missing, and <see cref="Protocol.Session.IRcSession.WarpPlayerAsync"/> with the
/// connection in use when Warp is pressed. Closes when the warp was sent.
/// </summary>
internal sealed partial class WarpWindow : RcWindow
{
    private readonly PlayersSession? _owner;

    /// <summary>For the designer.</summary>
    public WarpWindow() : this(null, 0, string.Empty)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="playerId">The player.</param>
    /// <param name="level">The level to start with.</param>
    public WarpWindow(PlayersSession? owner, int playerId, string level) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        PlayerId = playerId;
        LevelBox.Text = level;
        WarpButton.Click += On.ClickAsync(WarpAsync, "Warp.Warp");
        CancelButton.Click += On.Click(Close, "Warp.Cancel");
        KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Enter or Key.Return) UiSafe.Fire(WarpAsync, "Warp.Enter");
        }, "Warp.Keys");
    }

    /// <summary>The player.</summary>
    public int PlayerId { get; }

    /// <summary>The message shown, or empty.</summary>
    public string Error => ErrorText.IsVisible ? ErrorText.Text ?? string.Empty : string.Empty;

    /// <summary>Validates and sends the warp.</summary>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> WarpAsync()
    {
        if (_owner is null || IsClosed) return false;
        string level = (LevelBox.Text ?? string.Empty).Trim();
        if (level.Length == 0) return ShowError("Enter a destination level.");
        if (!TryParse(XBox.Text, out double x) || !TryParse(YBox.Text, out double y)) return ShowError("X and Y must be finite numbers.");
        var session = _owner.Context.Session;
        if (_owner.Context.IsEnded || !session.IsConnected) return ShowError(PlayersSession.NotConnectedText);
        bool sent = await session.WarpPlayerAsync(PlayerId, level, x, y).ConfigureAwait(true);
        if (!sent) return ShowError(string.IsNullOrEmpty(session.LastError) ? "Warp request failed." : session.LastError!);
        Close();
        return true;
    }

    private bool ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        return false;
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
