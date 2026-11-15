using System.Globalization;

namespace RemoteControl.Core;

/// <summary>A named list server (<c>SavedListServer</c> in the C++).</summary>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Host">The host name or address.</param>
/// <param name="Port">The TCP port, 1 to 65535.</param>
public sealed record ListServerProfile(string Name, string Host, int Port)
{
    /// <summary>The name of the built-in profile.</summary>
    public const string DefaultName = "Alpaxis";

    /// <summary>The host of the built-in profile.</summary>
    public const string DefaultHost = "listserver.alpaxis.net";

    /// <summary>The port of the built-in profile, and the usual list-server port.</summary>
    public const int DefaultPort = 14922;

    /// <summary>The built-in profile: always present, always first, cannot be edited or removed.</summary>
    public static ListServerProfile Default { get; } = new(DefaultName, DefaultHost, DefaultPort);

    /// <summary>Whether this is the built-in profile.</summary>
    public bool IsBuiltIn => Equals(Default);

    /// <summary><c>host:port</c>.</summary>
    public string Endpoint => Host + ":" + Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The association string saved with an account: <c>"Name — host:port"</c> with an em dash (<c>listServerAssociation</c>).</summary>
    public string Association => Name + ListServerAssociation.LabelSeparator + Endpoint;

    /// <summary>Whether a port is valid.</summary>
    /// <param name="port">The port.</param>
    /// <returns><c>true</c> for 1 to 65535.</returns>
    public static bool IsValidPort(int port) => port is > 0 and <= 65535;
}
