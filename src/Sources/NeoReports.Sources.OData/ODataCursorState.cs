using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>
/// The pagination state a strategy needs to fetch its next page, per <see cref="ODataPaginationStrategy"/>
/// (ADR D62). Only the field(s) relevant to the configured strategy are ever populated.
/// </summary>
/// <param name="NextUrl">Absolute next-page URL from <c>@odata.nextLink</c> (<see cref="ODataPaginationStrategy.NextLink"/>).</param>
/// <param name="Skip">Next <c>$skip</c> value (<see cref="ODataPaginationStrategy.Skip"/>).</param>
internal sealed record ODataCursorState(
    [property: JsonPropertyName("u")] string? NextUrl = null,
    [property: JsonPropertyName("s")] int? Skip = null);

/// <summary>
/// Encodes/decodes <see cref="ODataCursorState"/> into the single opaque <c>string?</c> cursor the
/// pipeline carries (D3 rule: the cursor is never a raw structured value) — a thin wrapper over the
/// shared <see cref="OpaqueCursor"/> Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class ODataPagination
{
    private static readonly ODataCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(ODataCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static ODataCursorState Decode(string? cursor) => OpaqueCursor.Decode<ODataCursorState>(cursor) ?? FirstPage;
}
