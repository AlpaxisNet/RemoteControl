using System.Text;

namespace RemoteControl.Protocol.Text;

/// <summary>A player account as the server sends it (PLO_RC_ACCOUNTGET) and as the account editor reads it back.</summary>
public sealed record AccountData
{
    /// <summary>The account name.</summary>
    public string Account { get; init; } = string.Empty;

    /// <summary>The password (the server sends it empty; an empty password is sent back empty).</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>The e-mail address.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>The admin level (0 to 223).</summary>
    public int AdminLevel { get; init; }

    /// <summary>The worlds the admin level applies to (<c>"all"</c> when sent empty).</summary>
    public string AdminWorlds { get; init; } = string.Empty;

    /// <summary>Whether the account is banned.</summary>
    public bool Banned { get; init; }

    /// <summary>Whether the account is a guest (Alpaxis: "load only") account.</summary>
    public bool Guest { get; init; }

    /// <summary>The ban length text the server sent.</summary>
    public string BanLength { get; init; } = string.Empty;

    /// <summary>The ban reason.</summary>
    public string BanReason { get; init; } = string.Empty;

    /// <summary>The "Ban Time" line of the editor text, as typed.</summary>
    public string BanTimeText { get; init; } = string.Empty;

    /// <summary>The "old email:" line of the editor text, if any.</summary>
    public string OldEmail { get; init; } = string.Empty;

    /// <summary>Who changed the account (a <c>"- name, date"</c> line of the editor text).</summary>
    public string ChangedBy { get; init; } = string.Empty;

    /// <summary>When the account was changed (the date of that line).</summary>
    public string ChangeDate { get; init; } = string.Empty;

    /// <summary>The <c>key=value</c> lines RClib passed to <c>on_player_text_data("account", …)</c>.</summary>
    /// <returns>The text.</returns>
    public string ToKeyValueText() =>
        $"account={Account}\npassword={Password}\nemail={Email}\nbanned={(Banned ? 1 : 0)}\nguest={(Guest ? 1 : 0)}\nadmin_level={AdminLevel}\n" +
        $"admin_worlds={AdminWorlds}\nban_length={BanLength}\nban_reason={BanReason}";
}

/// <summary>The account editor text (RClib <c>rc_format_player_account_text</c> and <c>rc_parse_player_account_text</c>).</summary>
public static class AccountText
{
    /// <summary>The ban time RClib always printed.</summary>
    public const string FixedBanTime = "Wed Dec 31 18:00:00 1969";

    /// <summary>Formats an account for the editor; the password is always printed empty.</summary>
    /// <param name="account">The account.</param>
    /// <returns>The text.</returns>
    public static string Format(AccountData account)
    {
        var text = new StringBuilder();
        text.Append("Account name: ").Append(account.Account).Append('\n');
        text.Append("Password:\n");
        text.Append("E-mail address: ").Append(account.Email).Append('\n');
        text.Append("Admin level: ").Append(account.AdminLevel).Append('\n');
        text.Append("Admin worlds: ").Append(account.AdminWorlds).Append('\n');
        text.Append("Banned: ").Append(account.Banned ? "true" : "false").Append('\n');
        text.Append("Guest: ").Append(account.Guest ? "true" : "false").Append('\n');
        text.Append("Ban Time: ").Append(FixedBanTime).Append('\n');
        text.Append("Ban-Reason / Comments: ").Append(account.BanReason).Append('\n');
        return text.ToString();
    }

    /// <summary>Parses the editor text. Lines are trimmed (spaces and quotes); unknown labels are ignored.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The account.</returns>
    public static AccountData Parse(string? text)
    {
        var account = new AccountData();
        foreach (string raw in TextHelpers.Lines(text))
        {
            string line = TextHelpers.Trim(raw);
            if (line.Length == 0) continue;
            if (line.StartsWith("old email:", StringComparison.Ordinal))
            {
                account = account with { OldEmail = TextHelpers.Trim(line["old email:".Length..]) };
                continue;
            }

            if (line.StartsWith('-') && line.Contains(',', StringComparison.Ordinal))
            {
                int comma = line.IndexOf(',', StringComparison.Ordinal);
                account = account with { ChangedBy = TextHelpers.Trim(line[1..comma]), ChangeDate = TextHelpers.Trim(line[(comma + 1)..]) };
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            string label = TextHelpers.Trim(line[..colon]);
            string value = TextHelpers.Trim(line[(colon + 1)..]);
            account = label switch
            {
                "Account name" => account with { Account = value },
                "Password" => account with { Password = value },
                "E-mail address" => account with { Email = value },
                "Admin level" => account with { AdminLevel = TextHelpers.ParseInt(value) },
                "Admin worlds" => account with { AdminWorlds = value },
                "Banned" => account with { Banned = TextHelpers.IsTruthy(value) },
                "Guest" => account with { Guest = TextHelpers.IsTruthy(value) },
                "Ban Time" => account with { BanTimeText = value },
                "Ban-Reason / Comments" => account with { BanReason = value },
                _ => account,
            };
        }

        return account;
    }
}
