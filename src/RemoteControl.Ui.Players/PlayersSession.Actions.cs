using Avalonia.Controls;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>The actions of the player and account context menus. Each reads <see cref="Shell.SessionContext.Session"/> when it sends.</summary>
internal sealed partial class PlayersSession
{
    /// <summary>The title of the message shown when an action needs the server and the session is not connected.</summary>
    public const string NotConnectedTitle = "Not connected";

    /// <summary>The text of that message (the C++ warp dialog's).</summary>
    public const string NotConnectedText = "The server connection is unavailable.";

    /// <summary>
    /// Sends something with the connection in use now. Not connected: a clear message, nothing sent. Refused: the session's error.
    /// Never throws.
    /// </summary>
    /// <param name="owner">The window that asked (owner of the message), or null.</param>
    /// <param name="send">What to send.</param>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> SendAsync(Window? owner, Func<IRcSession, Task<bool>> send)
    {
        if (_ended) return false;
        IRcSession session = Context.Session;
        if (!session.IsConnected)
        {
            await Dialogs.ShowMessageAsync(NotConnectedTitle, NotConnectedText, OwnerOf(owner)).ConfigureAwait(true);
            return false;
        }
        bool sent = await UiSafe.RunAsync(() => send(session), "PlayersSession.Send", false).ConfigureAwait(true);
        if (!sent && !_ended)
        {
            string reason = string.IsNullOrEmpty(session.LastError) ? "The request could not be sent." : session.LastError!;
            await Dialogs.ShowErrorAsync("Remote Control - Error", reason, OwnerOf(owner)).ConfigureAwait(true);
        }
        return sent;
    }

    /// <summary>A window as a dialog owner: only while it is shown.</summary>
    public static Window? OwnerOf(Window? window) => window is { IsVisible: true } && window is not RcWindow { IsClosed: true } ? window : null;

    /// <summary>Asks for a text in a non-modal window that belongs to the session (no nested main loop, defect D6).</summary>
    /// <param name="owner">The window that asked, or null.</param>
    /// <param name="title">The title.</param>
    /// <param name="label">The label above the text.</param>
    /// <param name="accept">The text of the accepting button.</param>
    /// <param name="multiline">Whether the text has several lines.</param>
    /// <returns>The text; null for Cancel, an empty text, or when the session ended meanwhile.</returns>
    public async Task<string?> AskTextAsync(Window? owner, string title, string label, string accept, bool multiline)
    {
        if (_ended) return null;
        var window = new TextInputWindow(App, title, label, accept, multiline);
        Context.Windows.Track(window);
        Window? parent = OwnerOf(owner);
        if (parent is not null) window.Show(parent);
        else window.Show();
        string? text = await window.Result.ConfigureAwait(true);
        return _ended || string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Requests what the Edit Access window shows: the ban types and the bans (new type), or the legacy ban.</summary>
    /// <param name="owner">The window that asked.</param>
    /// <param name="account">The account.</param>
    /// <param name="playerId">The player id, or -1.</param>
    /// <returns>Whether it was sent.</returns>
    public Task<bool> RequestAccessAsync(Window? owner, string account, int playerId)
    {
        if (string.IsNullOrEmpty(account)) return Task.FromResult(false);
        bool useNew = UseNewBanType;
        return SendAsync(owner, async session =>
        {
            if (!useNew) return await session.RequestLegacyPlayerBanAsync(account).ConfigureAwait(true);
            bool types = await session.RequestBanTypesAsync().ConfigureAwait(true);
            return await session.RequestNewPlayerBanAsync(account, playerId).ConfigureAwait(true) && types;
        });
    }

    /// <summary>The player context menu of "This server" (<c>TPlayerList.cpp:609-672</c>), every item bound to the player it opened for.</summary>
    /// <param name="owner">The player list.</param>
    /// <param name="target">The player.</param>
    /// <returns>The items (separators included).</returns>
    public List<Control> PlayerMenu(Window owner, PlayerTarget target)
    {
        var items = new List<Control>
        {
            Item("Private Message", () => { OpenPrivateMessage(target.Id, target.Account, target.Nick); return Task.CompletedTask; }),
            Item("History", () => { ShowHistory(target.Account, target.Nick); return Task.CompletedTask; }),
            Item("Ban History", () => SendIf(owner, target, session => session.RequestBanHistoryAsync(target.Account))),
            Item("Staff Activity", () => SendIf(owner, target, session => session.RequestStaffActivityAsync(target.Account))),
            Item("Profile", () => SendIf(owner, target, session => session.RequestProfileAsync(target.Account))),
            new Separator(),
            Item("Edit Attributes", () => SendIf(owner, target, session => target.Id >= 0 ? session.RequestPlayerAttributesAsync(target.Id) : session.RequestPlayerAttributesAsync(target.Account))),
            Item("Edit Rights", () => SendIf(owner, target, session => session.RequestPlayerRightsAsync(target.Account))),
            Item("Edit Comments", () => SendIf(owner, target, session => session.RequestPlayerCommentsAsync(target.Account))),
            Item("Edit Access", () => RequestAccessAsync(owner, target.Account, target.Id)),
            Item("Disconnect", () => DisconnectAsync(owner, target)),
            Item("Reset", () => ResetAsync(owner, target.Account, "Do you really want to reset the attributes of " + target.Account + " ?")),
            new Separator(),
            Item("Edit Account", () => SendIf(owner, target, session => session.RequestAccountAsync(target.Account))),
            Item("Warp", () => { Warp(owner, target); return Task.CompletedTask; }),
            Item("Update Level", () => UpdateLevelAsync(owner, target)),
            Item("Admin Message", () => AdminMessageAsync(owner, target)),
            new Separator(),
            Item("Copy Account", () => ClipboardHelper.SetTextAsync(owner, target.Account)),
            Item("Copy Nickname", () => ClipboardHelper.SetTextAsync(owner, target.Nick)),
        };
        return items;
    }

    /// <summary>The context menu of a player of another server (Servers tab, <c>TPlayerList.cpp:1290-1363</c>).</summary>
    /// <param name="owner">The player list.</param>
    /// <param name="target">The player.</param>
    /// <returns>The items.</returns>
    public List<Control> RemotePlayerMenu(Window owner, PlayerTarget target) => new()
    {
        Item("Private Message", () => { OpenPrivateMessage(target.Id, target.Account, target.Nick); return Task.CompletedTask; }),
        Item("History", () => { ShowHistory(target.Account, target.Nick); return Task.CompletedTask; }),
        Item("Ban History", () => SendIf(owner, target, session => session.RequestBanHistoryAsync(target.Account))),
        Item("Staff Activity", () => SendIf(owner, target, session => session.RequestStaffActivityAsync(target.Account))),
        new Separator(),
        Item("Copy Account", () => ClipboardHelper.SetTextAsync(owner, target.Account)),
    };

    /// <summary>The context menu of the accounts list (<c>TAccountsWindow::onAccountContext</c>).</summary>
    /// <param name="owner">The accounts list.</param>
    /// <param name="account">The account.</param>
    /// <returns>The items.</returns>
    public List<Control> AccountMenu(Window owner, string account)
    {
        var target = new PlayerTarget(-1, account, account, string.Empty);
        return new List<Control>
        {
            Item("Edit Attributes", () => SendIf(owner, target, session => session.RequestPlayerAttributesAsync(account))),
            new Separator(),
            Item("Edit Account", () => SendIf(owner, target, session => session.RequestAccountAsync(account))),
            Item("Edit Rights", () => SendIf(owner, target, session => session.RequestPlayerRightsAsync(account))),
            Item("Edit Comments", () => SendIf(owner, target, session => session.RequestPlayerCommentsAsync(account))),
            Item("Edit Access", () => RequestAccessAsync(owner, account, -1)),
            Item("Ban History", () => SendIf(owner, target, session => session.RequestBanHistoryAsync(account))),
            Item("Staff Activity", () => SendIf(owner, target, session => session.RequestStaffActivityAsync(account))),
            new Separator(),
            Item("Reset", () => ResetAsync(owner, account, "Do you really want to reset the attributes of " + account + "?")),
            Item("Delete", () => DeleteAccountAsync(owner, account)),
        };
    }

    /// <summary>"Mass PM to all" (or to the selected players): one message to many ids.</summary>
    /// <param name="owner">The player list.</param>
    /// <param name="ids">The recipients.</param>
    /// <param name="selectedOnly">Whether they are a selection (title).</param>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> MassPmAsync(Window owner, IReadOnlyList<int> ids, bool selectedOnly)
    {
        if (ids.Count == 0) return false;
        string title = selectedOnly ? "Mass PM to " + ids.Count + " players" : "Mass PM to all";
        string? message = await AskTextAsync(owner, title, "Message:", "Send", multiline: true).ConfigureAwait(true);
        if (message is null) return false;
        return await SendAsync(owner, session => session.SendMassPrivateMessageAsync(ids, message)).ConfigureAwait(true);
    }

    /// <summary>"Admin Message to all": one line to everyone.</summary>
    /// <param name="owner">The player list.</param>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> AdminMessageToAllAsync(Window owner)
    {
        string? message = await AskTextAsync(owner, "Admin Message to all", "Message", "OK", multiline: false).ConfigureAwait(true);
        if (message is null) return false;
        return await SendAsync(owner, session => session.SendAdminMessageToAllAsync(message)).ConfigureAwait(true);
    }

    private Task SendIf(Window owner, PlayerTarget target, Func<IRcSession, Task<bool>> send) =>
        string.IsNullOrEmpty(target.Account) ? Task.CompletedTask : SendAsync(owner, send);

    private async Task DisconnectAsync(Window owner, PlayerTarget target)
    {
        if (target.Id == 0) return;
        string? reason = await AskTextAsync(owner, "Disconnect Player", "Reason:", "Disconnect", multiline: true).ConfigureAwait(true);
        if (reason is null) return;
        await SendAsync(owner, session => session.DisconnectPlayerAsync(target.Id, reason)).ConfigureAwait(true);
    }

    private async Task ResetAsync(Window owner, string account, string question)
    {
        if (string.IsNullOrEmpty(account)) return;
        if (!await Dialogs.ConfirmAsync("Question", question, OwnerOf(owner)).ConfigureAwait(true)) return;
        await SendAsync(owner, session => session.ResetPlayerAsync(account)).ConfigureAwait(true);
    }

    private async Task DeleteAccountAsync(Window owner, string account)
    {
        if (string.IsNullOrEmpty(account)) return;
        if (!await Dialogs.ConfirmAsync("Question", "Do you really want to delete the account " + account + "?", OwnerOf(owner)).ConfigureAwait(true)) return;
        await SendAsync(owner, session => session.SendRawPacketAsync((int)ClientPacketId.AccountDelete, RcText.GetBytes(account))).ConfigureAwait(true);
    }

    private void Warp(Window owner, PlayerTarget target)
    {
        if (target.Id < 0 || _ended) return;
        string level = string.Equals(target.Level, "Offline", StringComparison.OrdinalIgnoreCase) ? string.Empty : target.Level;
        var window = new WarpWindow(this, target.Id, level);
        Context.Windows.Track(window);
        Window? parent = OwnerOf(owner);
        if (parent is not null) window.Show(parent);
        else window.Show();
    }

    private Task UpdateLevelAsync(Window owner, PlayerTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Level) || string.Equals(target.Level, "Offline", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        return SendAsync(owner, session => session.UpdateLevelAsync(target.Level));
    }

    private async Task AdminMessageAsync(Window owner, PlayerTarget target)
    {
        if (target.Id == 0) return;
        string? message = await AskTextAsync(owner, "Admin Message", "Message:", "Send", multiline: true).ConfigureAwait(true);
        if (message is null) return;
        await SendAsync(owner, session => session.SendAdminMessageAsync(target.Id, message)).ConfigureAwait(true);
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header, Name = "Menu" + header.Replace(" ", string.Empty, StringComparison.Ordinal) };
        item.Click += On.ClickAsync(action, "Menu " + header);
        return item;
    }
}
