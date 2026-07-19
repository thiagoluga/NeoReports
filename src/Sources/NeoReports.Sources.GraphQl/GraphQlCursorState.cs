using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// The Relay cursor-paging state carried in the opaque cursor (ADR D63) — only the prior page's
/// <c>pageInfo.endCursor</c>, since <c>relay</c> is the only pagination strategy this source supports.
/// </summary>
/// <param name="After">The <c>after</c> variable value for the next page, from <c>pageInfo.endCursor</c>; <c>null</c> on the first page.</param>
internal sealed record GraphQlCursorState(string? After = null);

/// <summary>
/// Encodes/decodes <see cref="GraphQlCursorState"/> into the single opaque <c>string?</c> cursor the
/// pipeline carries (D3 rule: the cursor is never a raw structured value) — a thin wrapper over the
/// shared <see cref="OpaqueCursor"/> Base64(UTF-8(JSON)) mechanism.
/// </summary>
internal static class GraphQlPagination
{
    private static readonly GraphQlCursorState FirstPage = new();

    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode(GraphQlCursorState state) => OpaqueCursor.Encode(state);

    /// <summary>Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to empty state.</summary>
    public static GraphQlCursorState Decode(string? cursor) => OpaqueCursor.Decode<GraphQlCursorState>(cursor) ?? FirstPage;
}
