using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>Builds the absolute Salesforce REST API URLs used by this source (ADR D67).</summary>
internal static class SalesforceUrls
{
    /// <summary>The first page's query URL: <c>{instanceUrl}/services/data/{apiVersion}/query?q={soql}</c>.</summary>
    public static Uri Query(string instanceUrl, string apiVersion, string soql) =>
        QueryStrings.AddQuery($"{instanceUrl.TrimEnd('/')}/services/data/{apiVersion}/query", ("q", soql));

    /// <summary>
    /// A later page's URL, combining <paramref name="instanceOrigin"/> with the response-supplied
    /// (relative, per Salesforce's documented contract) <c>nextRecordsUrl</c> via real <see cref="Uri"/>
    /// relative-resolution (<c>new Uri(baseUri, relativeRef)</c>) — safe here specifically because
    /// <c>nextRecordsUrl</c> always starts with <c>/</c> (an absolute-path reference replaces the
    /// base's entire path, unlike a same-segment-level relative reference, which is what caused the
    /// D64/D65 "drops the last path segment" bug for a base URL with no trailing slash; that pitfall
    /// only applies to a reference *without* a leading <c>/</c>). Using real resolution rather than
    /// plain string concatenation also makes the caller's same-origin check
    /// (<see cref="HttpOrigin.IsSameOrigin"/>, <see cref="SalesforceBatchSource{T}"/>) meaningful: an
    /// unexpected absolute URL in <c>nextRecordsUrl</c> (a malformed/compromised response) actually
    /// resolves to that other origin here, rather than being silently mangled into a same-origin path
    /// segment the way string concatenation would — the same "don't forward credentials cross-origin"
    /// discipline D61 established for the HTTP/OData families' server-supplied next-page URLs.
    /// </summary>
    /// <param name="instanceOrigin">The org's instance URL, already parsed once by the caller (avoids re-parsing the same never-changing string on every page).</param>
    /// <param name="nextRecordsUrl">The response-supplied next-page locator.</param>
    public static Uri NextPage(Uri instanceOrigin, string nextRecordsUrl) => new(instanceOrigin, nextRecordsUrl);

    /// <summary>
    /// The REST API's "list available resources" endpoint, used by the health check — reachability/
    /// auth only, no query-shape dependency. <paramref name="trailingSegment"/> (the health check's
    /// configured <c>healthCheckPath</c>) is appended via plain string concatenation, not
    /// <c>HttpHealthProbe.CombineUrl</c>'s <c>Uri</c> relative-resolution (code-review finding): a
    /// <paramref name="trailingSegment"/> configured with its own leading <c>/</c> is an
    /// absolute-path reference that replaces this URL's entire path (not just its last segment,
    /// which the trailing slash below would otherwise protect against) — the same D64/D65 bug class,
    /// avoided here the same way <c>HubSpotUrls.ObjectCollection</c>/<c>AirtableUrls.Table</c> avoid
    /// it, structurally rather than by an assumption about how the path is configured.
    /// </summary>
    public static Uri Resources(string instanceUrl, string apiVersion, string? trailingSegment = null)
    {
        string path = $"{instanceUrl.TrimEnd('/')}/services/data/{apiVersion}/";
        if (!string.IsNullOrEmpty(trailingSegment))
            path += trailingSegment.TrimStart('/');

        return new Uri(path, UriKind.Absolute);
    }
}
