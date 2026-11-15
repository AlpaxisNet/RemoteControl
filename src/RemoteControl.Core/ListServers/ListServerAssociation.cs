using System.Globalization;

namespace RemoteControl.Core;

/// <summary>Where an account's list-server association leads.</summary>
/// <param name="Name">The profile name, or for an endpoint that matches no profile the name in the association or the host.</param>
/// <param name="Host">The host.</param>
/// <param name="Port">The port.</param>
/// <param name="Profile">The profile that matched, or <c>null</c> for an endpoint that matches no profile.</param>
/// <param name="IsFallback">Whether nothing matched and the built-in profile was chosen.</param>
public sealed record ListServerEndpoint(string Name, string Host, int Port, ListServerProfile? Profile, bool IsFallback)
{
    /// <summary><c>host:port</c>.</summary>
    public string Endpoint => Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The list-server association strings saved with accounts (<c>TServerList.cpp:25-51</c>, <c>TAccountPresentation.cpp:29-38</c>,
/// <c>TStartFrame.cpp:13-26</c>): <c>"Name — host:port"</c> (em dash), a bare profile name, <c>host:port</c>, or with direct mode
/// <c>direct://...</c>.
/// </summary>
/// <remarks>
/// Differences from the C++: names and hosts are compared ignoring case everywhere (the C++ did so only for direct associations);
/// "contains <c>host:port</c>" is narrowed to "ends with <c>host:port</c> after a space or slash", so <c>a.b:149220</c> no longer
/// matches <c>a.b:14922</c>; a port must be all digits; when nothing matches, <see cref="Resolve"/> returns the built-in profile
/// (the C++ kept its default list server, which was the Graal one).
/// </remarks>
public static class ListServerAssociation
{
    /// <summary>The separator between name and endpoint: space, em dash, space.</summary>
    public const string LabelSeparator = " — ";

    /// <summary>The prefix of a direct-mode association.</summary>
    public const string DirectPrefix = "direct://";

    /// <summary>The association of a profile, <c>"Name — host:port"</c>.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The association string.</returns>
    public static string For(ListServerProfile profile) => (profile ?? throw new ArgumentNullException(nameof(profile))).Association;

    /// <summary>
    /// Rewrites an association that names a profile (by name, endpoint, association, or <c>"… — host:port"</c>) to that profile's
    /// <c>"Name — host:port"</c>; any other text is returned unchanged (<c>normalizeListServerAssociation</c>).
    /// </summary>
    /// <param name="association">The stored association.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns>The normalized association.</returns>
    public static string Normalize(string association, IEnumerable<ListServerProfile> profiles)
    {
        if (string.IsNullOrEmpty(association)) return association ?? string.Empty;
        foreach (ListServerProfile profile in profiles)
        {
            if (Same(association, profile.Name) || Same(association, profile.Endpoint) || Same(association, profile.Association)
                || (association.Length > profile.Endpoint.Length && association.EndsWith(LabelSeparator + profile.Endpoint, StringComparison.OrdinalIgnoreCase)))
                return profile.Association;
        }
        return association;
    }

    /// <summary>The label shown on an account badge: the profile name of the association, or <c>Unassigned</c> when empty.</summary>
    /// <param name="association">The stored association.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns>The label.</returns>
    public static string BadgeLabel(string association, IEnumerable<ListServerProfile> profiles)
    {
        if (string.IsNullOrEmpty(association)) return "Unassigned";
        string normalized = Normalize(association, profiles);
        int separator = normalized.IndexOf(LabelSeparator, StringComparison.Ordinal);
        return separator < 0 ? normalized : normalized[..separator];
    }

    /// <summary>
    /// Finds where an association leads (<c>associationEndpoint</c>): a profile whose name, <c>host:port</c> or association it is;
    /// a profile named before the separator; or the <c>host:port</c> it contains.
    /// </summary>
    /// <param name="association">The stored association (without <c>direct://</c>).</param>
    /// <param name="profiles">The profiles.</param>
    /// <param name="endpoint">The result when found.</param>
    /// <returns><c>true</c> when something matched.</returns>
    public static bool TryResolve(string? association, IEnumerable<ListServerProfile> profiles, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ListServerEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(association)) return false;
        string text = association.Trim();
        List<ListServerProfile> list = profiles.ToList();
        foreach (ListServerProfile profile in list)
        {
            if (Same(text, profile.Name) || Same(text, profile.Endpoint) || Same(text, profile.Association) || EndsWithEndpoint(text, profile.Endpoint))
            {
                endpoint = new ListServerEndpoint(profile.Name, profile.Host, profile.Port, profile, false);
                return true;
            }
        }
        string associationName = string.Empty;
        string endpointText = text;
        int label = text.IndexOf(LabelSeparator, StringComparison.Ordinal);
        if (label >= 0)
        {
            associationName = text[..label];
            endpointText = text[(label + LabelSeparator.Length)..];
            foreach (ListServerProfile profile in list)
            {
                if (Same(profile.Name, associationName))
                {
                    endpoint = new ListServerEndpoint(profile.Name, profile.Host, profile.Port, profile, false);
                    return true;
                }
            }
        }
        if (!TrySplitEndpoint(endpointText, out string host, out int port)) return false;
        endpoint = new ListServerEndpoint(associationName.Length == 0 ? host : associationName, host, port, null, false);
        return true;
    }

    /// <summary>Like <see cref="TryResolve"/>, but when nothing matches the built-in profile is returned with <see cref="ListServerEndpoint.IsFallback"/> set.</summary>
    /// <param name="association">The stored association.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns>Where to connect.</returns>
    public static ListServerEndpoint Resolve(string? association, IEnumerable<ListServerProfile> profiles)
    {
        if (TryResolve(association, profiles, out ListServerEndpoint? endpoint)) return endpoint;
        ListServerProfile fallback = ListServerProfile.Default;
        return new ListServerEndpoint(fallback.Name, fallback.Host, fallback.Port, fallback, true);
    }

    /// <summary>Whether an association is a direct-mode one (<c>direct://…</c>).</summary>
    /// <param name="association">The association.</param>
    /// <returns><c>true</c> for a direct association.</returns>
    public static bool IsDirect(string? association) => association is not null && association.StartsWith(DirectPrefix, StringComparison.Ordinal);

    /// <summary>The direct-mode form of an association.</summary>
    /// <param name="listServer">The association without prefix.</param>
    /// <returns><c>direct://</c> followed by it.</returns>
    public static string ToDirect(string listServer) => DirectPrefix + listServer;

    /// <summary>
    /// The game server of a direct association (<c>parseDirectAssociation</c>): a profile named by name, <c>host:port</c> or association,
    /// otherwise the <c>host:port</c> after the prefix.
    /// </summary>
    /// <param name="association">The association, with <c>direct://</c>.</param>
    /// <param name="profiles">The profiles.</param>
    /// <param name="host">The host when found.</param>
    /// <param name="port">The port when found.</param>
    /// <returns><c>true</c> when it is a direct association that names a server.</returns>
    public static bool TryParseDirect(string? association, IEnumerable<ListServerProfile> profiles, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!IsDirect(association)) return false;
        string target = association![DirectPrefix.Length..];
        foreach (ListServerProfile profile in profiles)
        {
            if (Same(target, profile.Name) || Same(target, profile.Endpoint) || Same(target, profile.Association))
            {
                host = profile.Host;
                port = profile.Port;
                return true;
            }
        }
        return TrySplitEndpoint(target, out host, out port);
    }

    /// <summary>Splits <c>host:port</c> at the last colon; the host must not be empty and the port must be 1 to 65535.</summary>
    /// <param name="text">The text.</param>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <returns><c>true</c> when it is a valid endpoint.</returns>
    public static bool TrySplitEndpoint(string? text, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(text)) return false;
        int separator = text.LastIndexOf(':');
        if (separator <= 0 || separator + 1 >= text.Length) return false;
        if (!int.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || !ListServerProfile.IsValidPort(parsed)) return false;
        string candidate = text[..separator].Trim();
        if (candidate.Length == 0) return false;
        host = candidate;
        port = parsed;
        return true;
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithEndpoint(string text, string endpoint)
    {
        if (!text.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Length == endpoint.Length) return true;
        char before = text[text.Length - endpoint.Length - 1];
        return before is ' ' or '/';
    }
}
