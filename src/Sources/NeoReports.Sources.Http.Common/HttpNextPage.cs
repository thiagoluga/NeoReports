namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Resolves the next-page URL a response hands back (a <c>Link</c> header's target, OData's
/// <c>@odata.nextLink</c>) into the absolute URL the next request is sent to.
/// </summary>
public static class HttpNextPage
{
    /// <summary>
    /// Resolves <paramref name="nextUrl"/> against the URL the response came from.
    /// <para>
    /// RFC 8288 and the OData protocol both define these as URI <i>references</i>, so a server may
    /// legitimately answer with a relative one (<c>?page=2</c>, <c>/v2/items?page=2</c>). Feeding
    /// that straight to <c>new Uri(string)</c> — which accepts absolute URIs only — turned a normal
    /// response into a <see cref="UriFormatException"/> with an opaque message.
    /// </para>
    /// <para>
    /// Resolution here is RFC 3986 (<c>new Uri(Uri, string)</c>), which is what both specifications
    /// call for and which leaves an absolute URL untouched. Note this is deliberately <b>not</b> the
    /// concatenation <c>HttpHealthProbe</c> uses: a health path is a sub-path to append below the
    /// base, whereas a next-page link is a URI reference to resolve — same-looking call, different
    /// contract.
    /// </para>
    /// </summary>
    /// <param name="nextUrl">The server-supplied next-page URL, absolute or relative.</param>
    /// <param name="requestUri">The absolute URL the response that carried it came from.</param>
    /// <returns>The absolute URL for the next request.</returns>
    /// <exception cref="HttpSourceException">The value cannot be resolved into an absolute URL.</exception>
    public static Uri Resolve(string nextUrl, Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(nextUrl);
        ArgumentNullException.ThrowIfNull(requestUri);

        if (!Uri.TryCreate(requestUri, nextUrl, out Uri? resolved))
        {
            throw new HttpSourceException(null, null,
                $"The next-page URL '{nextUrl}' could not be resolved against '{requestUri}'.");
        }

        return resolved;
    }
}
