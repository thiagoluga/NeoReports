using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// The pagination state Salesforce's REST Query resource needs to fetch its next page (ADR D67):
/// the previous page's <c>nextRecordsUrl</c>, carried verbatim (it's an opaque locator Salesforce
/// hands back, not something this source could reconstruct from an offset).
/// </summary>
/// <param name="NextRecordsUrl">The next page's relative URL; <c>null</c> on the first page.</param>
internal sealed record SalesforceCursorState([property: JsonPropertyName("n")] string? NextRecordsUrl = null);

/// <summary>
/// Encodes/decodes <see cref="SalesforceCursorState"/> into the single opaque <c>string?</c> cursor
/// the pipeline carries (D3 rule) — a thin wrapper over the shared <see cref="OpaqueCursor"/>
/// Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class SalesforcePagination
{
    private static readonly SalesforceCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(SalesforceCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static SalesforceCursorState Decode(string? cursor) => OpaqueCursor.Decode<SalesforceCursorState>(cursor) ?? FirstPage;
}
