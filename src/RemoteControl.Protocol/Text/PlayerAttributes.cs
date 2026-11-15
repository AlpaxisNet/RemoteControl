using System.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Text;

/// <summary>
/// A player's attributes: what PLO_RC_PLAYERPROPSGET carries and what PLI_RC_PLAYERPROPSSET2 sends back. A property that is
/// <c>null</c> was not received (or not typed in the editor) and is not sent. Properties the editor does not show are kept in
/// <see cref="OtherNumbers"/> and <see cref="OtherStrings"/>.
/// </summary>
public sealed class PlayerAttributes
{
    /// <summary>The account.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>The world (<c>"main"</c> on Alpaxis).</summary>
    public string World { get; set; } = string.Empty;

    /// <summary>The player id the server sent (0 when offline).</summary>
    public int PlayerId { get; set; }

    /// <summary>Property 0.</summary>
    public string? Nickname { get; set; }

    /// <summary>Property 1, "Full Hearts".</summary>
    public int? MaxPower { get; set; }

    /// <summary>Property 2, "Hearts" (halves).</summary>
    public double? Hearts { get; set; }

    /// <summary>Property 3, "Gralats".</summary>
    public int? Rupees { get; set; }

    /// <summary>Property 4.</summary>
    public int? Arrows { get; set; }

    /// <summary>Property 5.</summary>
    public int? Bombs { get; set; }

    /// <summary>Property 6, "Glove".</summary>
    public int? GlovePower { get; set; }

    /// <summary>Property 8: the sword power (1 to 4 with a default image, else the power of a custom image).</summary>
    public int? SwordPower { get; set; }

    /// <summary>Property 8: the sword image.</summary>
    public string? SwordImage { get; set; }

    /// <summary>Property 9: the shield power.</summary>
    public int? ShieldPower { get; set; }

    /// <summary>Property 9: the shield image.</summary>
    public string? ShieldImage { get; set; }

    /// <summary>Property 10, "Animation".</summary>
    public string? Animation { get; set; }

    /// <summary>Property 11.</summary>
    public string? HeadImage { get; set; }

    /// <summary>Property 13: five colour indexes (skin, coat, sleeves, shoes, belt).</summary>
    public int[]? Colors { get; set; }

    /// <summary>Property 15.</summary>
    public double? X { get; set; }

    /// <summary>Property 16.</summary>
    public double? Y { get; set; }

    /// <summary>Property 17.</summary>
    public int? Direction { get; set; }

    /// <summary>Property 18: status bits (1 paused, 2 hidden, 4 male, 8 dead, 16 weapons allowed, 32 hide sword, 64 spin attack).</summary>
    public int? Status { get; set; }

    /// <summary>Property 19.</summary>
    public int? CarrySprite { get; set; }

    /// <summary>Property 20.</summary>
    public string? Level { get; set; }

    /// <summary>Property 21.</summary>
    public string? HorseImage { get; set; }

    /// <summary>Property 25, "AP".</summary>
    public int? ApCounter { get; set; }

    /// <summary>Property 26, "MP".</summary>
    public int? MagicPoints { get; set; }

    /// <summary>Property 27.</summary>
    public int? Kills { get; set; }

    /// <summary>Property 28.</summary>
    public int? Deaths { get; set; }

    /// <summary>Property 29.</summary>
    public int? OnlineSeconds { get; set; }

    /// <summary>Property 30, as <c>a.b.c.d</c> (shown only; not sent back).</summary>
    public string? LastIp { get; set; }

    /// <summary>Property 35.</summary>
    public string? BodyImage { get; set; }

    /// <summary>Property 36: the rating.</summary>
    public int? Rating { get; set; }

    /// <summary>Property 36: the rating deviation.</summary>
    public int? RatingDeviation { get; set; }

    /// <summary>Other numeric properties, by id (shown nowhere, sent nowhere: kept for callers that want them).</summary>
    public Dictionary<int, double> OtherNumbers { get; } = new();

    /// <summary>Other text properties, by RClib name (<c>"account"</c>, <c>"attr1"</c>, <c>"language"</c> ...).</summary>
    public Dictionary<string, string> OtherStrings { get; } = new(StringComparer.Ordinal);

    /// <summary>The script flags (<c>"name=value"</c>).</summary>
    public List<string> Flags { get; } = new();

    /// <summary>The opened chests (<c>"x:y:level"</c>).</summary>
    public List<string> Chests { get; } = new();

    /// <summary>The weapons.</summary>
    public List<string> Weapons { get; } = new();

    /// <summary>Formats the attributes for the editor (RClib <c>rc_format_player_attributes_text</c>, plus the four status bits it left out).</summary>
    /// <returns>The text.</returns>
    public string ToEditorText() => PlayerAttributesText.Format(this);

    internal static string PropertyName(int id)
    {
        int attribute = AttributeIndex(id);
        if (attribute > 0) return "attr" + attribute;
        return id switch
        {
            0 => "nick", 1 => "max_power", 2 => "current_power", 3 => "rupees", 4 => "arrows", 5 => "bombs", 6 => "glove_power",
            7 => "bomb_power", 8 => "sword", 9 => "shield", 10 => "gani", 11 => "head_image", 12 => "chat", 13 => "colors", 14 => "id",
            15 => "x", 16 => "y", 17 => "direction", 18 => "status", 19 => "carry_sprite", 20 => "level", 21 => "horse_image",
            22 => "horse_bushes", 23 => "effect_colors", 24 => "carry_npc", 25 => "ap_counter", 26 => "magic_points", 27 => "kills",
            28 => "deaths", 29 => "online_seconds", 30 => "ip_address", 31 => "udp_port", 32 => "alignment", 33 => "additional_flags",
            34 => "account", 35 => "body_image", 36 => "rating", 42 => "attached_npc", 43 => "gmap_level_x", 44 => "gmap_level_y",
            45 => "z", 50 => "join_leave_level", 51 => "connected", 52 => "language", 53 => "status_message", 75 => "os_type",
            76 => "text_codepage", 77 => "unknown77", 78 => "precise_x", 79 => "precise_y", 80 => "precise_z", 81 => "player_list_category",
            82 => "community",
            _ => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    internal static int AttributeIndex(int id) =>
        id is >= 37 and <= 41 ? id - 37 + 1 : id is >= 46 and <= 49 ? id - 46 + 6 : id is >= 54 and <= 74 ? id - 54 + 10 : 0;

    internal static bool IsStringProperty(int id) =>
        id is 0 or 10 or 12 or 20 or 21 or 34 or 35 or 52 or 75 or 82 || AttributeIndex(id) > 0;

    internal static bool IsByteProperty(int id) =>
        id is 1 or 4 or 5 or 6 or 7 or 17 or 18 or 19 or 22 or 26 or 32 or 33 or 43 or 44 or 50 or 51 or 53 or 77 or 81;

    /// <summary>Reads PLO_RC_PLAYERPROPSGET. Returns <c>null</c> when the packet is too short to hold an account.</summary>
    internal static PlayerAttributes? Decode(byte[] payload)
    {
        var reader = new PacketReader(payload);
        if (payload.Length < 5) return null;
        var attributes = new PlayerAttributes();
        reader.TryReadGShort(out int playerId);
        attributes.PlayerId = playerId;
        attributes.Account = ReadAttrString(reader);
        attributes.World = ReadAttrString(reader);
        if (!reader.TryReadByte(out byte lengthByte)) return attributes;
        int start = reader.Position;
        int declared = (lengthByte - 32) & 0xFF;
        // The length is one GChar, so a block over 223 bytes wraps; take the first length at which the properties end exactly.
        bool parsed = false;
        int available = reader.Remaining;
        for (int candidate = declared; candidate <= available; candidate += 256)
        {
            reader.Rewind(start);
            var trial = new PlayerAttributes();
            if (ParseProperties(reader, start + candidate, trial) && reader.Position == start + candidate)
            {
                CopyProperties(trial, attributes);
                parsed = true;
                break;
            }
        }

        if (!parsed)
        {
            reader.Rewind(start);
            ParseProperties(reader, start + Math.Min(declared, reader.Remaining), attributes);
            reader.Rewind(start + Math.Min(declared, payload.Length - start));
        }

        if (reader.TryReadGShort(out int flagCount))
            for (int index = 0; index < flagCount && !reader.AtEnd; index++) attributes.Flags.Add(ReadAttrString(reader));
        if (reader.TryReadGShort(out int chestCount))
        {
            for (int index = 0; index < chestCount && !reader.AtEnd; index++)
            {
                if (!reader.TryReadGByte(out int length) || length < 2 || length > reader.Remaining)
                {
                    attributes.Chests.Add(string.Empty);
                    continue;
                }

                reader.TryReadGByte(out int x);
                reader.TryReadGByte(out int y);
                reader.TryReadText(length - 2, out string level);
                attributes.Chests.Add($"{x}:{y}:{level}");
            }
        }

        if (reader.TryReadGByte(out int weaponCount))
            for (int index = 0; index < weaponCount && !reader.AtEnd; index++) attributes.Weapons.Add(ReadAttrString(reader));
        return attributes;
    }

    private static void CopyProperties(PlayerAttributes from, PlayerAttributes to)
    {
        foreach (var property in typeof(PlayerAttributes).GetProperties())
        {
            if (!property.CanWrite || property.Name is nameof(Account) or nameof(World) or nameof(PlayerId)) continue;
            property.SetValue(to, property.GetValue(from));
        }

        foreach (var pair in from.OtherNumbers) to.OtherNumbers[pair.Key] = pair.Value;
        foreach (var pair in from.OtherStrings) to.OtherStrings[pair.Key] = pair.Value;
    }

    /// <summary>RClib readAttrString: the length byte is consumed; <c>""</c> when the length is not positive or runs past the end.</summary>
    private static string ReadAttrString(PacketReader reader)
    {
        if (!reader.TryReadGByte(out int length)) return string.Empty;
        if (length <= 0 || length > reader.Remaining) return string.Empty;
        reader.TryReadText(length, out string text);
        return text;
    }

    /// <summary>Parses the property block; returns false when a property runs past <paramref name="end"/>.</summary>
    private static bool ParseProperties(PacketReader reader, int end, PlayerAttributes attributes)
    {
        while (reader.Position < end)
        {
            reader.TryReadGByte(out int id);
            int need = FixedSize(id, reader);
            if (need < 0)
            {
                // A text property: its length byte decides.
                if (!reader.TryPeekByte(out byte raw)) return false;
                int length = raw - 32;
                if (reader.Position + 1 + Math.Max(length, 0) > end) return false;
                string text = ReadAttrString(reader);
                SetString(attributes, id, text);
                continue;
            }

            if (reader.Position + need > end) return false;
            byte[] bytes = reader.TryReadBytes(need, out byte[] read) ? read : Array.Empty<byte>();
            SetFixed(attributes, id, bytes, reader, end);
        }

        return reader.Position == end;
    }

    /// <summary>The byte size of a property after its id, or -1 for a GString-like value whose size is in its first byte.</summary>
    private static int FixedSize(int id, PacketReader reader)
    {
        if (IsStringProperty(id)) return -1;
        if (IsByteProperty(id)) return 1;
        switch (id)
        {
            case 2 or 15 or 16 or 45: return 1;
            case 3 or 27 or 28 or 29 or 24 or 31 or 76 or 36: return 3;
            case 8 or 9:
            {
                // Power, then an image when the power is 10 or more.
                if (!reader.TryPeekByte(out byte power)) return 1;
                if (power - 32 < 10) return 1;
                int position = reader.Position;
                reader.TrySkip(1);
                int imageLength = reader.TryPeekByte(out byte raw) ? Math.Max(raw - 32, 0) : 0;
                reader.Rewind(position);
                return 2 + imageLength;
            }
            case 11:
            {
                if (!reader.TryPeekByte(out byte head)) return 1;
                int value = head - 32;
                return value >= 100 ? 1 + (value - 100) : 1;
            }
            case 13: return 5;
            case 14 or 25 or 78 or 79 or 80: return 2;
            case 23:
                return reader.TryPeekByte(out byte effect) && effect - 32 > 0 ? 4 : 1;
            case 30: return 5;
            case 42: return 4;
            default: return 1;
        }
    }

    private static void SetString(PlayerAttributes attributes, int id, string text)
    {
        switch (id)
        {
            case 0: attributes.Nickname = text; break;
            case 10: attributes.Animation = text; break;
            case 20: attributes.Level = text; break;
            case 21: attributes.HorseImage = text; break;
            case 35: attributes.BodyImage = text; break;
            default: attributes.OtherStrings[PropertyName(id)] = text; break;
        }
    }

    private static void SetFixed(PlayerAttributes attributes, int id, byte[] bytes, PacketReader reader, int end)
    {
        if (bytes.Length == 0) return;
        int b0 = bytes[0] - 32;
        switch (id)
        {
            case 1: attributes.MaxPower = b0; return;
            case 2: attributes.Hearts = b0 / 2.0; return;
            case 3: attributes.Rupees = GEncoding.DecodeGInt3(bytes); return;
            case 4: attributes.Arrows = b0; return;
            case 5: attributes.Bombs = b0; return;
            case 6: attributes.GlovePower = b0; return;
            case 8:
            case 9:
            {
                string image = bytes.Length > 2 ? RcText.GetString(bytes.AsSpan(2)) : string.Empty;
                bool sword = id == 8;
                int power;
                if (b0 >= 10) power = image.Length == 0 ? b0 : b0 - (sword ? 30 : 10);
                else power = b0;
                if (b0 < 10) image = b0 == 0 ? string.Empty : (sword ? $"sword{b0}.gif" : $"shield{b0}.gif");
                if (sword)
                {
                    attributes.SwordPower = power;
                    attributes.SwordImage = image;
                }
                else
                {
                    attributes.ShieldPower = power;
                    attributes.ShieldImage = image;
                }

                return;
            }
            case 11:
                attributes.HeadImage = b0 > 0 && b0 < 100 ? $"head{b0}.gif" : b0 >= 100 ? RcText.GetString(bytes.AsSpan(1)) : "head0.gif";
                return;
            case 13: attributes.Colors = bytes.Select(value => value - 32).ToArray(); return;
            case 15: attributes.X = b0 / 2.0; return;
            case 16: attributes.Y = b0 / 2.0; return;
            case 17: attributes.Direction = b0; return;
            case 18: attributes.Status = b0; return;
            case 19: attributes.CarrySprite = b0; return;
            case 25: attributes.ApCounter = GEncoding.DecodeGShort(bytes); return;
            case 26: attributes.MagicPoints = b0; return;
            case 27: attributes.Kills = GEncoding.DecodeGInt3(bytes); return;
            case 28: attributes.Deaths = GEncoding.DecodeGInt3(bytes); return;
            case 29: attributes.OnlineSeconds = GEncoding.DecodeGInt3(bytes); return;
            case 30: attributes.LastIp = FormatIp(GEncoding.DecodeGInt5(bytes)); return;
            case 36:
            {
                int high = bytes[0] - 32, low = bytes[1] - 32, fraction = bytes[2] - 32;
                attributes.Rating = (high << 5) + (low >> 2);
                attributes.RatingDeviation = ((low & 0x03) << 7) + fraction;
                return;
            }
            case 45: attributes.OtherNumbers[id] = b0 - 50; return;
            case 78 or 79 or 80:
            {
                int raw = GEncoding.DecodeGShort(bytes);
                int value = raw >> 1;
                attributes.OtherNumbers[id] = ((raw & 1) != 0 ? -value : value) / 16.0;
                return;
            }
            default:
                if (IsByteProperty(id)) attributes.OtherNumbers[id] = b0;
                return;
        }
    }

    /// <summary>
    /// The IP address of property 30. The server writes <c>IPAddress.Address</c> (first octet in the low byte) as a GInt5, so the low
    /// byte is printed first (RClib's PLO_RC_PLAYERPROPSGET decoder did the same).
    /// </summary>
    internal static string FormatIp(long value) =>
        $"{value & 0xFF}.{(value >> 8) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 24) & 0xFF}";

    /// <summary>Builds the payload of PLI_RC_PLAYERPROPSSET2 (RClib <c>rc_set_player_attributes</c>).</summary>
    /// <param name="account">The account to change.</param>
    /// <exception cref="PacketTooLongException">A text or the property block is too long for its length byte.</exception>
    internal byte[] EncodeForUpload(string account)
    {
        var props = new PacketWriter();
        void Byte(int id, int? value)
        {
            if (value is null) return;
            props.WriteGByte(id).WriteGByte(value.Value);
        }

        void Half(int id, double? value)
        {
            if (value is null) return;
            props.WriteGByte(id).WriteGByte((int)(value.Value * 2));
        }

        void Int3(int id, int? value)
        {
            if (value is null) return;
            props.WriteGByte(id).WriteGInt3(value.Value);
        }

        void Text(int id, string? value)
        {
            if (value is null) return;
            props.WriteGByte(id).WriteGString(value);
        }

        Byte(1, MaxPower);
        Half(2, Hearts);
        Int3(3, Rupees);
        Byte(4, Arrows);
        Byte(5, Bombs);
        Byte(6, GlovePower);
        if (ApCounter is not null) props.WriteGByte(25).WriteGShort(ApCounter.Value);
        if (SwordPower is not null || SwordImage is not null) WriteWeaponPower(props, 8, SwordPower ?? 0, SwordImage ?? string.Empty, "sword", 4, 30, 50);
        if (ShieldPower is not null || ShieldImage is not null) WriteWeaponPower(props, 9, ShieldPower ?? 0, ShieldImage ?? string.Empty, "shield", 3, 10, 13);
        Text(10, Animation);
        if (HeadImage is not null)
        {
            props.WriteGByte(11);
            string image = HeadImage;
            if (image.StartsWith("head", StringComparison.Ordinal) && image.Length > 8 && TryParseNumber(image[4..^4], out int head) && head is >= 0 and < 100)
                props.WriteGByte(head);
            else if (image.Length > 0)
            {
                if (RcText.ByteCount(image) > 123) throw new PacketTooLongException("The head image name is longer than 123 bytes.");
                props.WriteGByte(100 + image.Length).WriteText(image);
            }
            else props.WriteGByte(0);
        }

        if (Colors is { Length: > 0 })
        {
            props.WriteGByte(13);
            for (int index = 0; index < 5; index++) props.WriteGByte(index < Colors.Length ? Colors[index] : 0);
        }

        Half(15, X);
        Half(16, Y);
        Byte(17, Direction);
        Byte(18, Status);
        Byte(19, CarrySprite);
        Text(20, Level);
        Text(21, HorseImage);
        Byte(26, MagicPoints);
        Int3(27, Kills);
        Int3(28, Deaths);
        Int3(29, OnlineSeconds);
        Text(35, BodyImage);
        if (Rating is not null && RatingDeviation is not null)
        {
            int rating = Rating.Value, deviation = RatingDeviation.Value;
            props.WriteGByte(36).WriteGByte((rating >> 5) & 0xFF).WriteGByte(((rating & 0x1F) << 2) | ((deviation >> 7) & 0x03)).WriteGByte(deviation & 0x7F);
        }

        if (props.Length > GEncoding.MaxGStringLength)
            throw new PacketTooLongException($"The attributes take {props.Length} bytes; the server's length byte carries at most 223.");
        var data = new PacketWriter(256);
        data.WriteGString(account).WriteGString(World);
        data.WriteGByte(props.Length).WriteBytes(props.WrittenSpan);
        if (Flags.Count > GEncoding.MaxGShort) throw new PacketTooLongException("Too many flags.");
        data.WriteGShort(Flags.Count);
        foreach (string flag in Flags) data.WriteGString(flag);
        if (Chests.Count > GEncoding.MaxGShort) throw new PacketTooLongException("Too many chests.");
        data.WriteGShort(Chests.Count);
        foreach (string chest in Chests)
        {
            string[] parts = chest.Split(':', 3);
            if (parts.Length < 3)
            {
                data.WriteGString(chest);
                continue;
            }

            byte[] level = RcText.GetBytes(parts[2]);
            if (level.Length > 0xDD) throw new PacketTooLongException("A chest's level name is longer than 221 bytes.");
            data.WriteGByte(level.Length + 2).WriteGByte(TextHelpers.ParseInt(parts[0])).WriteGByte(TextHelpers.ParseInt(parts[1])).WriteBytes(level);
        }

        if (Weapons.Count > GEncoding.MaxGStringLength) throw new PacketTooLongException("More than 223 weapons.");
        data.WriteGByte(Weapons.Count);
        foreach (string weapon in Weapons) data.WriteGString(weapon);
        return data.ToArray();
    }

    private static void WriteWeaponPower(PacketWriter props, int id, int power, string image, string prefix, int maxDefault, int offset, int maxEncoded)
    {
        props.WriteGByte(id);
        if (power == 0 && image.Length == 0)
        {
            props.WriteGByte(0);
            return;
        }

        if (image.StartsWith(prefix, StringComparison.Ordinal) && image.Length > prefix.Length + 4
            && TryParseNumber(image[prefix.Length..^4], out int index) && index >= 1 && index <= maxDefault)
        {
            props.WriteGByte(index);
            return;
        }

        props.WriteGByte(Math.Clamp(power + offset, 10, maxEncoded)).WriteGString(image);
    }

    private static bool TryParseNumber(string text, out int value)
    {
        // atoi of the middle of "sword3.gif": leading digits.
        value = TextHelpers.ParseInt(text);
        return true;
    }
}

/// <summary>The attributes editor text (RClib <c>rc_format_player_attributes_text</c> and <c>rc_parse_player_attributes_text</c>).</summary>
public static class PlayerAttributesText
{
    private static readonly (string Label, int Bit)[] StatusLabels =
    {
        ("Male", 4), ("Weapons Enabled", 16), ("Spin Attack", 64), ("Paused", 1), ("Hidden", 2), ("Dead", 8), ("Hide sword", 32),
    };

    /// <summary>
    /// Formats the attributes with the sections and labels of the C++ program. Unlike RClib, the status bits Paused, Hidden, Dead and
    /// Hide sword are printed too (after Spin Attack), so that editing and saving no longer clears them.
    /// </summary>
    /// <param name="attributes">The attributes.</param>
    /// <returns>The text.</returns>
    public static string Format(PlayerAttributes attributes)
    {
        int[] colors = new int[5];
        if (attributes.Colors is not null)
            for (int index = 0; index < 5 && index < attributes.Colors.Length; index++) colors[index] = attributes.Colors[index];
        int status = attributes.Status ?? 0;
        var text = new StringBuilder();
        text.Append("[Stats]\n");
        text.Append("Account: ").Append(attributes.Account).Append('\n');
        text.Append("Last IP: ").Append(attributes.LastIp ?? string.Empty).Append('\n');
        text.Append("Kills: ").Append(attributes.Kills ?? 0).Append('\n');
        text.Append("Deaths: ").Append(attributes.Deaths ?? 0).Append('\n');
        text.Append("Online Seconds: ").Append(attributes.OnlineSeconds ?? 0).Append('\n');
        text.Append("Rating: ").Append(attributes.Rating ?? 0).Append('\n');
        text.Append("Rating Deviation: ").Append(attributes.RatingDeviation ?? 0).Append("\n\n");
        text.Append("[Look]\n");
        text.Append("Head Image: ").Append(attributes.HeadImage ?? "head0.gif").Append('\n');
        text.Append("Body Image: ").Append(attributes.BodyImage ?? string.Empty).Append('\n');
        text.Append("Animation: ").Append(attributes.Animation ?? string.Empty).Append('\n');
        text.Append("Skin Color: ").Append(ColorNames.NameOf(colors[0])).Append('\n');
        text.Append("Coat Color: ").Append(ColorNames.NameOf(colors[1])).Append('\n');
        text.Append("Sleeves Color: ").Append(ColorNames.NameOf(colors[2])).Append('\n');
        text.Append("Shoes Color: ").Append(ColorNames.NameOf(colors[3])).Append('\n');
        text.Append("Belt Color: ").Append(ColorNames.NameOf(colors[4])).Append("\n\n");
        text.Append("[Basic Attributes]\n");
        text.Append("Level: ").Append(attributes.Level ?? string.Empty).Append('\n');
        text.Append("X: ").Append(TextHelpers.FormatNumber(attributes.X ?? 0)).Append('\n');
        text.Append("Y: ").Append(TextHelpers.FormatNumber(attributes.Y ?? 0)).Append('\n');
        text.Append("Hearts: ").Append(TextHelpers.FormatNumber(attributes.Hearts ?? 0)).Append('\n');
        text.Append("Full Hearts: ").Append(attributes.MaxPower ?? 0).Append('\n');
        text.Append("AP: ").Append(attributes.ApCounter ?? 0).Append('\n');
        text.Append("MP: ").Append(attributes.MagicPoints ?? 0).Append('\n');
        text.Append("Gralats: ").Append(attributes.Rupees ?? 0).Append('\n');
        text.Append("Glove: ").Append(attributes.GlovePower ?? 0).Append('\n');
        text.Append("Bombs: ").Append(attributes.Bombs ?? 0).Append('\n');
        text.Append("Arrows: ").Append(attributes.Arrows ?? 0).Append('\n');
        text.Append("Sword Power: ").Append(TextHelpers.FormatNumber(attributes.SwordPower ?? 0)).Append('\n');
        text.Append("Sword Image: ").Append(attributes.SwordImage ?? string.Empty).Append('\n');
        text.Append("Shield Power: ").Append(TextHelpers.FormatNumber(attributes.ShieldPower ?? 0)).Append('\n');
        text.Append("Shield Image: ").Append(attributes.ShieldImage ?? string.Empty).Append('\n');
        foreach ((string label, int bit) in StatusLabels)
            text.Append(label).Append(": ").Append((status & bit) != 0 ? "true" : "false").Append('\n');
        text.Append("\n[Chests]\n");
        foreach (string chest in attributes.Chests) text.Append(chest).Append('\n');
        text.Append("\n[Weapons]\n");
        foreach (string weapon in attributes.Weapons) text.Append(weapon).Append('\n');
        text.Append("\n[Script Flags]\n");
        foreach (string flag in attributes.Flags) text.Append(flag).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Parses the editor text. Lines of the [Chests], [Weapons] and [Script Flags] sections are taken whole (RClib dropped every such
    /// line that contained a colon, which is every chest). Labels outside those sections are matched as RClib does, including the
    /// alternative labels "Weapons allowed" and "Has spin attack".
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The attributes; <see cref="PlayerAttributes.Status"/> is always set.</returns>
    public static PlayerAttributes Parse(string? text)
    {
        var attributes = new PlayerAttributes();
        int[] colors = new int[5];
        int status = 0;
        string section = string.Empty;
        foreach (string raw in TextHelpers.Lines(text))
        {
            string line = TextHelpers.Trim(raw);
            if (line.Length == 0) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            switch (section)
            {
                case "Chests":
                    attributes.Chests.Add(line);
                    continue;
                case "Weapons":
                    attributes.Weapons.Add(line);
                    continue;
                case "Script Flags":
                    attributes.Flags.Add(line);
                    continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            string label = TextHelpers.Trim(line[..colon]);
            string value = TextHelpers.Trim(line[(colon + 1)..]);
            switch (label)
            {
                case "Account": attributes.Account = value; break;
                case "Last IP": attributes.LastIp = value; break;
                case "Kills": attributes.Kills = (int)TextHelpers.ParseDouble(value); break;
                case "Deaths": attributes.Deaths = (int)TextHelpers.ParseDouble(value); break;
                case "Online Seconds": attributes.OnlineSeconds = (int)TextHelpers.ParseDouble(value); break;
                case "Rating": attributes.Rating = (int)TextHelpers.ParseDouble(value); break;
                case "Rating Deviation": attributes.RatingDeviation = (int)TextHelpers.ParseDouble(value); break;
                case "Head Image": attributes.HeadImage = value; break;
                case "Body Image": attributes.BodyImage = value; break;
                case "Animation": attributes.Animation = value; break;
                case "Skin Color": colors[0] = ColorNames.IndexOf(value); break;
                case "Coat Color": colors[1] = ColorNames.IndexOf(value); break;
                case "Sleeves Color": colors[2] = ColorNames.IndexOf(value); break;
                case "Shoes Color": colors[3] = ColorNames.IndexOf(value); break;
                case "Belt Color": colors[4] = ColorNames.IndexOf(value); break;
                case "Level": attributes.Level = value; break;
                case "X": attributes.X = TextHelpers.ParseDouble(value); break;
                case "Y": attributes.Y = TextHelpers.ParseDouble(value); break;
                case "Hearts": attributes.Hearts = TextHelpers.ParseDouble(value); break;
                case "Full Hearts": attributes.MaxPower = (int)TextHelpers.ParseDouble(value); break;
                case "AP": attributes.ApCounter = (int)TextHelpers.ParseDouble(value); break;
                case "MP": attributes.MagicPoints = (int)TextHelpers.ParseDouble(value); break;
                case "Gralats": attributes.Rupees = (int)TextHelpers.ParseDouble(value); break;
                case "Glove": attributes.GlovePower = (int)TextHelpers.ParseDouble(value); break;
                case "Bombs": attributes.Bombs = (int)TextHelpers.ParseDouble(value); break;
                case "Arrows": attributes.Arrows = (int)TextHelpers.ParseDouble(value); break;
                case "Sword Power": attributes.SwordPower = (int)TextHelpers.ParseDouble(value); break;
                case "Sword Image": attributes.SwordImage = value; break;
                case "Shield Power": attributes.ShieldPower = (int)TextHelpers.ParseDouble(value); break;
                case "Shield Image": attributes.ShieldImage = value; break;
                case "Paused": if (TextHelpers.IsTruthy(value)) status |= 1; break;
                case "Hidden": if (TextHelpers.IsTruthy(value)) status |= 2; break;
                case "Male": if (TextHelpers.IsTruthy(value)) status |= 4; break;
                case "Dead": if (TextHelpers.IsTruthy(value)) status |= 8; break;
                case "Weapons Enabled" or "Weapons allowed": if (TextHelpers.IsTruthy(value)) status |= 16; break;
                case "Hide sword": if (TextHelpers.IsTruthy(value)) status |= 32; break;
                case "Spin Attack" or "Has spin attack": if (TextHelpers.IsTruthy(value)) status |= 64; break;
            }
        }

        attributes.Colors = colors;
        attributes.Status = status;
        return attributes;
    }
}
