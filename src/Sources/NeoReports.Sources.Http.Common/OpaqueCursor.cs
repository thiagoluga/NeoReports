using System.Text.Json;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Encodes/decodes an arbitrary pagination state into the single opaque <c>string?</c> cursor the
/// pipeline carries (D3 rule: the cursor is never a raw structured value) — Base64(UTF-8(JSON)).
/// Generic over the caller's own cursor-state shape (ADR D61); each HTTP-family source keeps its
/// own strategy-specific state record and only reuses this Base64-JSON mechanism.
/// </summary>
public static class OpaqueCursor
{
    /// <summary>Encodes pagination state into the opaque cursor string.</summary>
    public static string Encode<TState>(TState state) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state));

    /// <summary>
    /// Decodes the opaque cursor string; a <c>null</c> cursor (first page) decodes to <c>default</c>.
    /// A non-null but malformed/empty cursor throws rather than silently restarting pagination — a
    /// corrupted cursor is a real failure, not an empty first page.
    /// </summary>
    public static TState? Decode<TState>(string? cursor)
    {
        if (cursor is null)
            return default;

        return JsonSerializer.Deserialize<TState>(Convert.FromBase64String(cursor));
    }
}
