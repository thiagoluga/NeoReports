namespace NeoReports.Sources.HubSpot;

/// <summary>
/// Builds the absolute HubSpot CRM object-collection URL from the API host and object type (ADR
/// D65) — <c>{baseUrl}/crm/v3/objects/{objectType}</c>, plus an optional trailing segment (e.g. the
/// health check's <c>healthCheckPath</c>). The object type is percent-encoded since it is
/// author-configured and may contain characters that are not valid bare in a URL path segment.
/// Deliberately does <b>not</b> route a trailing segment through <c>Uri</c>'s relative-combination
/// resolution (<c>new Uri(baseUri, relativePath)</c>) — that replaces the last path segment rather
/// than appending after it whenever the base has no trailing slash (which this collection URL never
/// does), the exact bug D64 found and fixed in <c>ElasticsearchUrls.Combine</c>; plain string
/// concatenation here avoids it structurally.
/// </summary>
internal static class HubSpotUrls
{
    public static string ObjectCollection(string baseUrl, string objectType, string? trailingSegment = null)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        string path = baseUri.AbsolutePath.TrimEnd('/') + "/crm/v3/objects/" + Uri.EscapeDataString(objectType);
        if (!string.IsNullOrEmpty(trailingSegment))
            path += "/" + trailingSegment.TrimStart('/');

        var builder = new UriBuilder(baseUri) { Path = path };
        return builder.Uri.ToString();
    }
}
