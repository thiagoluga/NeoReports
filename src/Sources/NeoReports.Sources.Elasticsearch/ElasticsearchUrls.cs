namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// Builds an absolute Elasticsearch/OpenSearch endpoint URL from a base URL, index name, and
/// optional trailing endpoint segment (ADR D64) — e.g. <c>Combine(url, "orders", "_search")</c> for
/// <c>{url}/orders/_search</c>, or <c>Combine(url, "orders")</c> for <c>{url}/orders</c> (the health
/// check's reachability probe). The index name is percent-encoded since it is author-configured and
/// may contain characters that are not valid bare in a URL path segment.
/// </summary>
internal static class ElasticsearchUrls
{
    public static string Combine(string baseUrl, string index, string? endpoint = null)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        string path = baseUri.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(index);
        if (!string.IsNullOrEmpty(endpoint))
            path += "/" + endpoint;

        var builder = new UriBuilder(baseUri) { Path = path };
        return builder.Uri.ToString();
    }
}
