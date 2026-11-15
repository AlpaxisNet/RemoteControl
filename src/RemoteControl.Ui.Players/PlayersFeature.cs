using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Players;

/// <summary>
/// The players group: the player list, private messages, the accounts windows and the dialogs that edit rights, attributes,
/// comments, profiles, accounts and bans.
/// </summary>
public static class PlayersFeature
{
    /// <summary>
    /// Registers the group's tools: <see cref="ToolIds.PlayerList"/>, <see cref="ToolIds.Accounts"/> and
    /// <see cref="ToolIds.PrivateMessage"/> (argument: the player id as <see cref="int"/>).
    /// </summary>
    /// <param name="tools">The program's tool registry.</param>
    public static void Register(ToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Register(ToolIds.PlayerList, (context, _) => PlayersSession.For(context)?.ShowPlayerList());
        tools.Register(ToolIds.Accounts, (context, _) => PlayersSession.For(context)?.ShowAccounts());
        tools.Register(ToolIds.PrivateMessage, (context, argument) => argument switch
        {
            int id => PlayersSession.For(context)?.OpenPrivateMessage(id),
            long id when id is >= int.MinValue and <= int.MaxValue => PlayersSession.For(context)?.OpenPrivateMessage((int)id),
            string text when int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id) =>
                PlayersSession.For(context)?.OpenPrivateMessage(id),
            _ => null,
        });
    }

    /// <summary>
    /// Starts the group's part of a session, to be called by the program when a session starts: from then on private messages are
    /// noticed (unread state, attention, history) and the server's replies open their dialogs even when no window of the group was
    /// opened, and <see cref="IPrivateMessages"/> is offered for the session. Calling it again, or opening a tool of the group, does not
    /// start it twice; a session that has ended is ignored.
    /// </summary>
    /// <param name="context">The session.</param>
    public static void Start(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = PlayersSession.For(context);
    }
}
