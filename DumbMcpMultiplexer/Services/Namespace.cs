namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Handles slug-based namespace prefixing for tool/prompt names and resource URIs.
/// </summary>
public static class Namespace
{
    public const string Separator = "__";

    /// <summary>
    /// Prefix a name with a server slug: "github" + "create_issue" → "github__create_issue"
    /// </summary>
    public static string Prefix(string slug, string name) => $"{slug}{Separator}{name}";

    /// <summary>
    /// Split a prefixed name into (slug, originalName).
    /// Returns null if the name doesn't contain the separator.
    /// </summary>
    public static (string Slug, string Name)? Split(string prefixedName)
    {
        var index = prefixedName.IndexOf(Separator, StringComparison.Ordinal);
        if (index < 0) return null;
        return (prefixedName[..index], prefixedName[(index + Separator.Length)..]);
    }

    /// <summary>
    /// Prefix a URI with the server slug for resource namespacing.
    /// Uses a custom scheme: proxy://{slug}/{original_uri}
    /// </summary>
    public static string PrefixUri(string slug, string uri) => $"proxy://{slug}/{uri}";

    /// <summary>
    /// Split a prefixed URI back into (slug, originalUri).
    /// Expects format: proxy://{slug}/{rest}
    /// </summary>
    public static (string Slug, string Uri)? SplitUri(string prefixedUri)
    {
        const string scheme = "proxy://";
        if (!prefixedUri.StartsWith(scheme, StringComparison.Ordinal)) return null;
        var rest = prefixedUri[scheme.Length..];
        var slashIndex = rest.IndexOf('/');
        if (slashIndex < 0) return null;
        return (rest[..slashIndex], rest[(slashIndex + 1)..]);
    }
}
