using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Tests;

public sealed class RightsTextTests
{
    [Fact]
    public void Format_matches_the_cpp_layout()
    {
        string text = PlayerRightsText.Format(new PlayerRights((1 << 0) | (1 << 19), "1.2.3.*", "rw levels/*\nr config/*"));
        Assert.StartsWith("IP Range: 1.2.3.*\n\n# Rights (set to true/false):\nWarp to XY: true\nWarp to Player: false\n", text);
        Assert.Contains("\nComments: false\nStaff Accounts: false\n", text);
        Assert.Contains("NPC Control: true\n\nFolder Access:\nrw levels/*\nr config/*\n", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Parse_round_trips_and_keeps_unnamed_bits()
    {
        var rights = new PlayerRights((1 << 3) | (1 << 13) | (1 << 25) | (1 << 11), "*", "rw levels/*\nr config/*");
        PlayerRights parsed = PlayerRightsText.Parse(PlayerRightsText.Format(rights), unnamedBitsFrom: rights.Rights);
        Assert.Equal(rights, parsed);
        Assert.Equal((1 << 3) | (1 << 11), PlayerRightsText.Parse(PlayerRightsText.Format(rights)).Rights);
    }

    [Fact]
    public void Parse_accepts_truthy_values_and_a_first_folder_on_the_label_line()
    {
        PlayerRights parsed = PlayerRightsText.Parse("IP Range:  10.0.0.1 \r\nDisconnect: YES\nBan Players: 1\nFolder Access: rw a/*\n  r b/*  \n# end\nComments: true");
        Assert.Equal("10.0.0.1", parsed.IpRange);
        Assert.Equal((1 << 4) | (1 << 11) | (1 << 12), parsed.Rights);
        Assert.Equal("rw a/*\nr b/*", parsed.Folders);
    }

    [Fact]
    public void Names_are_rclibs()
    {
        Assert.Equal(20, RightsNames.All.Count);
        Assert.Equal(string.Empty, RightsNames.All[13]);
        Assert.Equal("NPC Control", RightsNames.All[19]);
        Assert.Equal(20, ColorNames.All.Count);
        Assert.Equal("Cynober", ColorNames.NameOf(13));
        Assert.Equal(13, ColorNames.IndexOf("cynober"));
        Assert.Equal(7, ColorNames.IndexOf(" 7 "));
        Assert.Equal(0, ColorNames.IndexOf("nonsense"));
        Assert.StartsWith("White\nYellow\n", ColorNames.AsText());
    }
}

public sealed class AccountTextTests
{
    [Fact]
    public void Format_matches_the_cpp_layout()
    {
        var account = new AccountData { Account = "Bob", Password = "secret", Email = "b@x", AdminLevel = 3, AdminWorlds = "all", Banned = true, BanReason = "spam" };
        Assert.Equal("Account name: Bob\nPassword:\nE-mail address: b@x\nAdmin level: 3\nAdmin worlds: all\nBanned: true\nGuest: false\n" +
                     "Ban Time: Wed Dec 31 18:00:00 1969\nBan-Reason / Comments: spam\n", AccountText.Format(account));
    }

    [Fact]
    public void Parse_reads_the_editor_text_back()
    {
        var account = new AccountData { Account = "Bob", Email = "b@x", AdminLevel = 3, AdminWorlds = "main", Guest = true, BanReason = "a reason" };
        AccountData parsed = AccountText.Parse(AccountText.Format(account) + "old email: \"o@x\"\n- Admin, 2026-01-01\n");
        Assert.Equal("Bob", parsed.Account);
        Assert.Equal(string.Empty, parsed.Password);
        Assert.Equal("b@x", parsed.Email);
        Assert.Equal(3, parsed.AdminLevel);
        Assert.Equal("main", parsed.AdminWorlds);
        Assert.False(parsed.Banned);
        Assert.True(parsed.Guest);
        Assert.Equal(AccountText.FixedBanTime, parsed.BanTimeText);
        Assert.Equal("a reason", parsed.BanReason);
        Assert.Equal("o@x", parsed.OldEmail);
        Assert.Equal("Admin", parsed.ChangedBy);
        Assert.Equal("2026-01-01", parsed.ChangeDate);
    }

    [Fact]
    public void Key_value_text_is_rclibs()
    {
        var account = new AccountData { Account = "A", Banned = true, AdminLevel = 2 };
        Assert.Equal("account=A\npassword=\nemail=\nbanned=1\nguest=0\nadmin_level=2\nadmin_worlds=\nban_length=\nban_reason=", account.ToKeyValueText());
    }
}

public sealed class AttributesTextTests
{
    private static PlayerAttributes Sample()
    {
        var attributes = new PlayerAttributes
        {
            Account = "Bob", World = "main", LastIp = "1.2.3.4", Kills = 5, Deaths = 2, OnlineSeconds = 99, Rating = 1500, RatingDeviation = 350,
            HeadImage = "head3.gif", BodyImage = "body.png", Animation = "idle", Colors = new[] { 0, 1, 2, 3, 13 }, Level = "onlinestartlocal.nw",
            X = 30.5, Y = 31, Hearts = 2.5, MaxPower = 3, ApCounter = 50, MagicPoints = 0, Rupees = 100, GlovePower = 1, Bombs = 5, Arrows = 10,
            SwordPower = 1, SwordImage = "sword1.gif", ShieldPower = 1, ShieldImage = "shield1.gif", Status = 4 | 16 | 1 | 32,
        };
        attributes.Chests.Add("12:34:level1.nw");
        attributes.Weapons.Add("Bomb");
        attributes.Flags.Add("client.time=1:2");
        return attributes;
    }

    [Fact]
    public void Format_has_the_cpp_sections_and_labels()
    {
        string text = PlayerAttributesText.Format(Sample());
        Assert.StartsWith("[Stats]\nAccount: Bob\nLast IP: 1.2.3.4\nKills: 5\nDeaths: 2\nOnline Seconds: 99\nRating: 1500\nRating Deviation: 350\n\n[Look]\n", text);
        Assert.Contains("Head Image: head3.gif\nBody Image: body.png\nAnimation: idle\nSkin Color: White\nCoat Color: Yellow\n", text);
        Assert.Contains("Belt Color: Cynober\n\n[Basic Attributes]\nLevel: onlinestartlocal.nw\nX: 30.5\nY: 31\nHearts: 2.5\nFull Hearts: 3\n", text);
        Assert.Contains("Male: true\nWeapons Enabled: true\nSpin Attack: false\nPaused: true\nHidden: false\nDead: false\nHide sword: true\n", text);
        Assert.EndsWith("\n[Chests]\n12:34:level1.nw\n\n[Weapons]\nBomb\n\n[Script Flags]\nclient.time=1:2\n", text);
    }

    [Fact]
    public void Parse_round_trips_every_shown_value()
    {
        PlayerAttributes original = Sample();
        PlayerAttributes parsed = PlayerAttributesText.Parse(PlayerAttributesText.Format(original));
        Assert.Equal(PlayerAttributesText.Format(original), PlayerAttributesText.Format(parsed));
        Assert.Equal(original.Status, parsed.Status);
        Assert.Equal("onlinestartlocal.nw", parsed.Level);
        Assert.Equal(new[] { "12:34:level1.nw" }, parsed.Chests);
        Assert.Equal(new[] { "client.time=1:2" }, parsed.Flags);
    }

    [Fact]
    public void Parse_accepts_the_alternative_labels()
    {
        PlayerAttributes parsed = PlayerAttributesText.Parse("[Basic Attributes]\nWeapons allowed: true\nHas spin attack: yes\nSkin Color: 5\n");
        Assert.Equal(16 | 64, parsed.Status);
        Assert.Equal(5, parsed.Colors![0]);
    }

    [Fact]
    public void Wire_encoding_round_trips_through_the_server_layout()
    {
        PlayerAttributes original = Sample();
        byte[] upload = original.EncodeForUpload("Bob");
        // PLO_RC_PLAYERPROPSGET is "GShort id" followed by the same block as the upload.
        byte[] reply = new PacketWriter().WriteGShort(7).WriteBytes(upload).ToArray();
        PlayerAttributes decoded = PlayerAttributes.Decode(reply)!;
        Assert.Equal(7, decoded.PlayerId);
        Assert.Equal("Bob", decoded.Account);
        Assert.Equal("main", decoded.World);
        Assert.Equal(30.5, decoded.X);
        Assert.Equal(2.5, decoded.Hearts);
        Assert.Equal(100, decoded.Rupees);
        Assert.Equal("head3.gif", decoded.HeadImage);
        Assert.Equal("sword1.gif", decoded.SwordImage);
        Assert.Equal(1500, decoded.Rating);
        Assert.Equal(350, decoded.RatingDeviation);
        Assert.Equal(original.Colors, decoded.Colors);
        Assert.Equal(original.Chests, decoded.Chests);
        Assert.Equal(original.Weapons, decoded.Weapons);
        Assert.Equal(original.Flags, decoded.Flags);
    }

    [Fact]
    public void A_wrapped_property_length_is_recovered()
    {
        // Alpaxis writes the property block length as one GChar, so a block of 277 bytes is sent as (277 + 32) & 0xFF = 53, which
        // reads as 21 (RClib read it as 21 and lost every field after it).
        string nick = new('n', 223);
        var props = new PacketWriter().WriteGByte(0).WriteGString(nick).WriteGByte(20).WriteGString(new string('l', 50));
        Assert.Equal(277, props.Length);
        byte[] reply = new PacketWriter().WriteGShort(1).WriteGString("Bob").WriteGString("main").WriteByte(unchecked((byte)(props.Length + 32)))
            .WriteBytes(props.WrittenSpan).WriteGShort(1).WriteGString("f=1").WriteGShort(0).WriteGByte(0).ToArray();
        PlayerAttributes decoded = PlayerAttributes.Decode(reply)!;
        Assert.Equal(nick, decoded.Nickname);
        Assert.Equal(new string('l', 50), decoded.Level);
        Assert.Equal(new[] { "f=1" }, decoded.Flags);
    }

    [Fact]
    public void Ip_is_printed_low_byte_first_as_the_server_writes_it()
    {
        // IPAddress.Parse("1.2.3.4").Address is 0x04030201.
        Assert.Equal("1.2.3.4", PlayerAttributes.FormatIp(0x04030201));
    }

    [Fact]
    public void Truncated_attribute_packets_never_throw()
    {
        PlayerAttributes original = Sample();
        byte[] reply = new PacketWriter().WriteGShort(7).WriteBytes(original.EncodeForUpload("Bob")).ToArray();
        for (int length = 0; length < reply.Length; length++) _ = PlayerAttributes.Decode(reply[..length]);
        var random = new Random(11);
        for (int round = 0; round < 500; round++)
        {
            byte[] noise = new byte[random.Next(0, 300)];
            random.NextBytes(noise);
            _ = PlayerAttributes.Decode(noise);
        }
    }
}

public sealed class ProfileAndBanTextTests
{
    [Fact]
    public void Profile_round_trips()
    {
        string[] fields = { "Real", "20", "m", "DE", "msn", "a@b", "http://x", "Plaza", "Hi: there", "12h" };
        Assert.Equal(fields, ProfileText.Parse(ProfileText.Format(fields)));
        Assert.StartsWith("Real Name: Real\nAge: 20\n", ProfileText.Format(fields));
    }

    [Fact]
    public void Profile_parse_accepts_rclibs_alternative_labels()
    {
        IReadOnlyList<string> fields = ProfileText.Parse("Profile for Bob\nEmail: e@x\nFavourite Quote: q\n");
        Assert.Equal("e@x", fields[5]);
        Assert.Equal("q", fields[8]);
    }

    [Fact]
    public void Ban_details_parse()
    {
        Assert.Equal("banned=1\nreason", BanText.FormatLegacyDetails(true, "reason"));
        var values = BanText.ParseDetails("banned=1\nreason = \"x\"\nnoequals");
        Assert.Equal("1", values["banned"]);
        Assert.Equal("x", values["reason"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Set_ban_fields_are_escaped_and_single_line()
    {
        IReadOnlyList<string> fields = BanText.SetBanFields("Bob", "main", true, "account", "", "cheating, a lot\nreally");
        string tokenized = CommaText.Tokenize(fields);
        Assert.Equal("GraalEngine,lister,setban,Bob,world=main,banned=1,bantype=account,\"reason=cheating, a lot really\"", tokenized);
    }

    [Fact]
    public void Packet_names_are_rclibs()
    {
        Assert.Equal("PLO_RC_CHAT", PacketNames.NameOf(false, PacketDirection.Incoming, 74));
        Assert.Equal("PLI_NC_WEAPONADD", PacketNames.NameOf(true, PacketDirection.Outgoing, 117));
        Assert.StartsWith("6=PLI_TOALL\n50=PLI_RAWDATA\n", PacketNames.AsText(false, PacketDirection.Outgoing));
        Assert.StartsWith("157=PLO_NC_NPCATTRIBUTES\n", PacketNames.AsText(true, PacketDirection.Incoming));
    }
}
