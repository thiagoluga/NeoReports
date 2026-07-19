using System.Text.Json;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// Detects and formats a GraphQL response's <c>errors</c> array (ADR D63) — shared by
/// <see cref="GraphQlBatchSource{T}"/> (the read path) and <see cref="GraphQlSourceHealthCheck"/>
/// (the probe), so the "does this response count as failed" rule is defined exactly once.
/// </summary>
internal static class GraphQlErrors
{
    /// <summary>
    /// Returns <c>true</c> and a concatenated error message when <paramref name="root"/> has a
    /// non-empty <c>errors</c> array; <c>false</c> (message <c>null</c>) otherwise.
    /// </summary>
    public static bool TryGetMessage(JsonElement root, out string? message)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("errors", out JsonElement errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
        {
            message = null;
            return false;
        }

        message = string.Join("; ", errors.EnumerateArray().Select(error =>
            error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out JsonElement messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()!
                : error.GetRawText()));
        return true;
    }
}
