using System.Text.Json;
using NeoReports.Core.Configuration;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// Builds the JSON <c>POST</c> body for one GraphQL request (ADR D63): <c>{"query": ..., "variables": {...}}</c>.
/// The paging variables (under their configured names) are merged alongside the author's static
/// variables. The <c>after</c> variable is omitted entirely on the first page — simpler than sending
/// JSON <c>null</c>, and works for any schema whose <c>after</c> argument is optional (the norm for a
/// Relay connection). Reuses <see cref="PrimitiveObjectConverter"/> to serialize the static
/// variables' CLR-primitive/<see cref="JsonElement"/> values verbatim, the same JsonElement/CLR
/// bidirectional conversion <c>ReportConfig</c> parsing and the source registry already solved (ADR D42).
/// </summary>
internal static class GraphQlRequest
{
    private static readonly JsonSerializerOptions SerializeOptions = CreateOptions();

    /// <summary>Serializes the request body for one page.</summary>
    /// <param name="query">The GraphQL query document.</param>
    /// <param name="staticVariables">Static, author-supplied variables, merged alongside the paging variables.</param>
    /// <param name="firstVariableName">Name of the page-size variable.</param>
    /// <param name="pageSize">The page size to request.</param>
    /// <param name="afterVariableName">Name of the cursor variable.</param>
    /// <param name="after">The prior page's end cursor, or <c>null</c> on the first page (the variable is omitted entirely, not sent as JSON <c>null</c>).</param>
    public static string BuildBody(
        string query,
        IReadOnlyDictionary<string, object?>? staticVariables,
        string firstVariableName,
        int pageSize,
        string afterVariableName,
        string? after)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (staticVariables is not null)
        {
            foreach (KeyValuePair<string, object?> pair in staticVariables)
                variables[pair.Key] = pair.Value;
        }

        variables[firstVariableName] = (long)pageSize;
        if (after is not null)
            variables[afterVariableName] = after;

        return JsonSerializer.Serialize(new { query, variables }, SerializeOptions);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PrimitiveObjectConverter());
        return options;
    }
}
