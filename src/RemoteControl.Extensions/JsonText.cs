using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RemoteControl.Extensions;

/// <summary>Small JSON writing helpers shared by the extension protocol and the MCP server.</summary>
internal static class JsonText
{
    /// <summary>Writer options: compact, and only the characters JSON requires are escaped (non-ASCII text stays readable).</summary>
    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false, SkipValidation = false };

    /// <summary>Serializer options matching <see cref="WriterOptions"/>.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    /// <summary>Replaces lone surrogates (which the JSON writer refuses) by U+FFFD.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        StringBuilder? result = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool lone = char.IsHighSurrogate(character) ? index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])
                : char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(value[index - 1]));
            if (lone)
            {
                result ??= new StringBuilder(value, 0, index, value.Length);
                result.Append('�');
                continue;
            }
            result?.Append(character);
        }
        return result?.ToString() ?? value;
    }

    /// <summary>Runs <paramref name="write"/> on a fresh writer and returns the UTF-16 text.</summary>
    public static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
