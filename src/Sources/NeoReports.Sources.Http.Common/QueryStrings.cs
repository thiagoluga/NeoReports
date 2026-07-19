namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Appends key/value pairs to a URL's query string (ADR D61/D62), preserving any query string
/// already present on the base URL rather than overwriting it. Shared across the HTTP-family
/// sources' paginated strategies.
/// </summary>
public static class QueryStrings
{
    /// <summary>
    /// Appends <paramref name="queryParams"/> to <paramref name="baseUrl"/>. Both <c>Key</c> and
    /// <c>Value</c> are percent-encoded — except a literal <c>$</c> in the key is left unescaped, so
    /// a fixed OData system query-option name (<c>$filter</c>, <c>$select</c>, ...) reaches the wire
    /// exactly as OData v4 requires, without <c>Uri.EscapeDataString</c> turning it into <c>%24filter</c>
    /// (a real bug an earlier version of this method had). Escaping the rest of the key matters
    /// because some HTTP-family callers' key comes from an author-configured property (e.g. the
    /// generic REST source's <c>cursorRequestParam</c>/<c>pageParam</c>) — leaving it entirely
    /// unescaped would let a value like <c>"page&amp;evil=1"</c> inject an extra query parameter
    /// (security-review finding); <c>$</c> alone can't do that (it isn't a query-string structural
    /// character — <c>&amp;</c>/<c>=</c>/<c>#</c> still are, and those stay escaped).
    /// </summary>
    public static Uri AddQuery(string baseUrl, params (string Key, string Value)[] queryParams)
    {
        if (queryParams.Length == 0)
            return new Uri(baseUrl, UriKind.Absolute);

        var builder = new UriBuilder(baseUrl);
        string existing = builder.Query.TrimStart('?');
        string added = string.Join("&", queryParams.Select(p => $"{EscapeKey(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        builder.Query = existing.Length > 0 ? $"{existing}&{added}" : added;
        return builder.Uri;
    }

    private static string EscapeKey(string key) => Uri.EscapeDataString(key).Replace("%24", "$", StringComparison.Ordinal);
}
