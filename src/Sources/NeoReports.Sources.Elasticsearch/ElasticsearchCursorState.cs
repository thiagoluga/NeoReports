using System.Text.Json;
using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// The pagination state <c>search_after</c> keyset paging needs to fetch its next page (ADR D64):
/// the previous page's last hit's <c>sort</c> values, carried verbatim so the next request's
/// <c>search_after</c> lines up exactly with the configured <c>sort</c>.
/// </summary>
/// <param name="SearchAfter">The last hit's <c>sort</c> array from the previous page; <c>null</c> on the first page.</param>
internal sealed record ElasticsearchCursorState([property: JsonPropertyName("a")] JsonElement[]? SearchAfter = null);

/// <summary>
/// Encodes/decodes <see cref="ElasticsearchCursorState"/> into the single opaque <c>string?</c>
/// cursor the pipeline carries (D3 rule: the cursor is never a raw structured value) — a thin
/// wrapper over the shared <see cref="OpaqueCursor"/> Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class ElasticsearchPagination
{
    private static readonly ElasticsearchCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(ElasticsearchCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static ElasticsearchCursorState Decode(string? cursor) => OpaqueCursor.Decode<ElasticsearchCursorState>(cursor) ?? FirstPage;
}
