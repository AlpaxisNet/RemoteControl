using System.Globalization;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Session;

/// <summary>
/// Reads the player property stream of PLO_ADDPLAYER and PLO_OTHERPLPROPS (RClib <c>readPlayerPropValue</c>): each entry is a GByte
/// id and a value whose layout depends on the id; reading stops at the first id it does not know. Two deliberate differences from
/// RClib, both to match what the Alpaxis server sends: property 53 (status message) is one GByte (RClib read a string, which put
/// the rest of the stream out of step whenever the status was not 0), and the player-list category is property 81 (RClib looked
/// for 0x71 = 113, which no server sends). The IP address (30) is printed low byte first, as the server writes it.
/// </summary>
internal static class PlayerPropertyStream
{
    /// <summary>Reads one value; false when the id is unknown or the value is cut off (the reader is then left as it was).</summary>
    public static bool TryRead(PacketReader reader, int id, out string value)
    {
        int start = reader.Position;
        if (!TryReadCore(reader, id, out value))
        {
            reader.Rewind(start);
            value = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>Reads every property up to the end or the first unknown id.</summary>
    public static List<(int Id, string Value)> ReadAll(PacketReader reader)
    {
        var properties = new List<(int, string)>();
        while (!reader.AtEnd)
        {
            int start = reader.Position;
            reader.TryReadGByte(out int id);
            if (!TryRead(reader, id, out string value))
            {
                reader.Rewind(start);
                break;
            }

            properties.Add((id, value));
        }

        return properties;
    }

    private static string Float(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static bool TryReadCore(PacketReader reader, int id, out string value)
    {
        value = string.Empty;
        if (PlayerAttributes.IsStringProperty(id))
        {
            if (!reader.TryReadGByte(out int length)) return true;
            if (length > 0 && length <= reader.Remaining) reader.TryReadText(length, out value);
            return true;
        }

        int b;
        switch (id)
        {
            case 1 or 4 or 5 or 6 or 7 or 17 or 18 or 19 or 22 or 26 or 32 or 33 or 43 or 44 or 50 or 51 or 53 or 77 or 81:
                if (!reader.TryReadGByte(out b)) return false;
                value = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case 2 or 15 or 16:
                if (!reader.TryReadGByte(out b)) return false;
                value = Float(b / 2.0);
                return true;
            case 3 or 24 or 27 or 28 or 29 or 31 or 76:
                if (!reader.TryReadGInt3(out b)) return false;
                value = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case 8 or 9:
            {
                if (!reader.TryReadGByte(out int raw)) return false;
                string image = string.Empty;
                if (raw >= 10 && !reader.AtEnd)
                {
                    reader.TryReadGByte(out int length);
                    if (length > 0 && length <= reader.Remaining) reader.TryReadText(length, out image);
                }

                value = image.Length == 0 ? raw.ToString(CultureInfo.InvariantCulture) : $"{raw}:{image}";
                return true;
            }
            case 11:
            {
                if (!reader.TryReadGByte(out int head)) return false;
                if (head is > 0 and < 100) value = $"head{head}.gif";
                else if (head >= 100 && reader.TryReadText(head - 100, out string image)) value = image;
                else value = "head0.gif";
                return true;
            }
            case 13:
            {
                if (!reader.TryReadBytes(5, out byte[] colors)) return false;
                value = string.Join(',', colors.Select(color => (color - 32).ToString(CultureInfo.InvariantCulture)));
                return true;
            }
            case 14 or 25:
                if (!reader.TryReadGShort(out b)) return false;
                value = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case 23:
            {
                if (!reader.TryPeekByte(out byte first)) return false;
                int count = first - 32 > 0 ? 4 : 1;
                if (!reader.TryReadBytes(count, out byte[] effect)) return false;
                value = string.Join(',', effect.Select(item => (item - 32).ToString(CultureInfo.InvariantCulture)));
                return true;
            }
            case 30:
                if (!reader.TryReadGInt5(out long ip)) return false;
                value = PlayerAttributes.FormatIp(ip);
                return true;
            case 36:
            {
                if (!reader.TryReadBytes(3, out byte[] rating)) return false;
                int high = rating[0] - 32, low = rating[1] - 32, fraction = rating[2] - 32;
                value = $"{(high << 5) + (low >> 2)}:{((low & 0x03) << 7) + fraction}";
                return true;
            }
            case 42:
            {
                if (!reader.TryReadGByte(out int kind) || !reader.TryReadGInt3(out int npc)) return false;
                value = $"{kind}:{npc}";
                return true;
            }
            case 45:
                if (!reader.TryReadGByte(out b)) return false;
                value = (b - 50).ToString(CultureInfo.InvariantCulture);
                return true;
            case 78 or 79 or 80:
            {
                if (!reader.TryReadGShort(out int raw)) return false;
                int magnitude = raw >> 1;
                value = Float(((raw & 1) != 0 ? -magnitude : magnitude) / 16.0);
                return true;
            }
            default:
                return false;
        }
    }
}
