using System.Text.Json.Serialization;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// The pagination state HubSpot's cursor paging needs to fetch its next page (ADR D65): the
/// previous page's <c>paging.next.after</c> value, carried verbatim as the next request's <c>after</c>.
/// </summary>
/// <param name="After">The next page's continuation token; <c>null</c> on the first page.</param>
internal sealed record HubSpotCursorState([property: JsonPropertyName("a")] string? After = null);

/// <summary>
/// Encodes/decodes <see cref="HubSpotCursorState"/> into the single opaque <c>string?</c> cursor the
/// pipeline carries (D3 rule) — a thin wrapper over the shared <see cref="OpaqueCursor"/>
/// Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class HubSpotPagination
{
    private static readonly HubSpotCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(HubSpotCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static HubSpotCursorState Decode(string? cursor) => OpaqueCursor.Decode<HubSpotCursorState>(cursor) ?? FirstPage;
}
