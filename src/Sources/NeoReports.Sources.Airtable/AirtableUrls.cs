namespace NeoReports.Sources.Airtable;

/// <summary>
/// Builds the absolute Airtable table URL from the API host, base id, and table id/name (ADR D65)
/// — <c>{baseUrl}/{baseId}/{tableIdOrName}</c>, plus an optional trailing segment (e.g. the health
/// check's <c>healthCheckPath</c>). Both path segments are percent-encoded since they are
/// author-configured and a table name commonly contains spaces/other characters not valid bare in a
/// URL path segment. Deliberately does <b>not</b> route a trailing segment through <c>Uri</c>'s
/// relative-combination resolution (<c>new Uri(baseUri, relativePath)</c>) — that replaces the last
/// path segment rather than appending after it whenever the base has no trailing slash (which this
/// table URL never does), the exact bug D64 found and fixed in <c>ElasticsearchUrls.Combine</c>;
/// plain string concatenation here avoids it structurally.
/// </summary>
internal static class AirtableUrls
{
    public static string Table(string baseUrl, string baseId, string tableIdOrName, string? trailingSegment = null)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        string path = baseUri.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(baseId) + "/" + Uri.EscapeDataString(tableIdOrName);
        if (!string.IsNullOrEmpty(trailingSegment))
            path += "/" + trailingSegment.TrimStart('/');

        var builder = new UriBuilder(baseUri) { Path = path };
        return builder.Uri.ToString();
    }
}
