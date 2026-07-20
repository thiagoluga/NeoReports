using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// The pagination state Airtable's cursor paging needs to fetch its next page (ADR D65): the
/// previous page's <c>offset</c> value, carried verbatim as the next request's <c>?offset=</c>.
/// </summary>
/// <param name="Offset">The next page's continuation token; <c>null</c> on the first page.</param>
internal sealed record AirtableCursorState([property: JsonPropertyName("o")] string? Offset = null);

/// <summary>
/// Encodes/decodes <see cref="AirtableCursorState"/> into the single opaque <c>string?</c> cursor
/// the pipeline carries (D3 rule) — a thin wrapper over the shared <see cref="OpaqueCursor"/>
/// Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class AirtablePagination
{
    private static readonly AirtableCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(AirtableCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static AirtableCursorState Decode(string? cursor) => OpaqueCursor.Decode<AirtableCursorState>(cursor) ?? FirstPage;
}
