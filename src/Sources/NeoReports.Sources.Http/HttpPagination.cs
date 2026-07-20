using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// The pagination state a strategy needs to fetch its next page, per <see cref="HttpPaginationStrategy"/>
/// (ADR D61). Only the field(s) relevant to the configured strategy are ever populated.
/// </summary>
/// <param name="Token">Continuation token (<see cref="HttpPaginationStrategy.Cursor"/>).</param>
/// <param name="NextUrl">Absolute next-page URL from the <c>Link</c> header (<see cref="HttpPaginationStrategy.LinkHeader"/>).</param>
/// <param name="Page">Next page number (<see cref="HttpPaginationStrategy.Page"/>).</param>
/// <param name="Offset">Next offset (<see cref="HttpPaginationStrategy.Offset"/>).</param>
internal sealed record HttpCursorState(
    [property: JsonPropertyName("t")] string? Token = null,
    [property: JsonPropertyName("u")] string? NextUrl = null,
    [property: JsonPropertyName("p")] int? Page = null,
    [property: JsonPropertyName("o")] int? Offset = null);

/// <summary>
/// Encodes/decodes <see cref="HttpCursorState"/> into the single opaque <c>string?</c> cursor the
/// pipeline carries (D3 rule: the cursor is never a raw structured value) — a thin wrapper over the
/// shared <see cref="OpaqueCursor"/> Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class HttpPagination
{
    private static readonly HttpCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(HttpCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static HttpCursorState Decode(string? cursor) => OpaqueCursor.Decode<HttpCursorState>(cursor) ?? FirstPage;
}
