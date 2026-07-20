using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// <see cref="IFilterTranslator"/> for Elasticsearch/OpenSearch sources (ADR D64). Unlike
/// <c>ODataFilterTranslator</c>'s string-based <c>$filter</c>, Elasticsearch's Query DSL is itself
/// JSON — so the merged query is built as a <see cref="JsonObject"/>/<see cref="JsonArray"/> tree and
/// serialized by <see cref="JsonSerializer"/>, never string-concatenated. This structurally rules out
/// the URL-encoding bug class ADR D62's <c>$filter</c> translator needed a code-review/
/// security-review pass to fix after the fact: a value embedded as a real JSON node can never "break
/// out" of its string/number slot into the surrounding query structure.
/// </summary>
public sealed class ElasticsearchFilterTranslator : IFilterTranslator
{
    /// <inheritdoc />
    public string Type => "elasticsearch";

    /// <inheritdoc />
    public bool TryTranslate(
        IReadOnlyDictionary<string, object?> properties,
        IReadOnlyList<PreviewFilter> filters,
        ReportSchema schema,
        out IReadOnlyDictionary<string, object?> propertyOverrides,
        out IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(schema);

        // Every value is embedded as a native JSON node directly in the request body — nothing to bind.
        parameters = new Dictionary<string, object?>();

        JsonNode? baseQuery = properties.TryGetValue("query", out object? existing) && existing is JsonElement { ValueKind: JsonValueKind.Object } element
            ? JsonNode.Parse(element.GetRawText())
            : null;

        if (filters.Count == 0)
        {
            propertyOverrides = baseQuery is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["query"] = ToJsonElement(baseQuery) };
            return true;
        }

        var clauses = new JsonArray();
        foreach (PreviewFilter filter in filters)
        {
            if (!TryBuildClause(filter, schema, out JsonObject clause))
            {
                propertyOverrides = new Dictionary<string, object?>();
                return false;
            }

            clauses.Add(clause);
        }

        var generated = new JsonObject { ["bool"] = new JsonObject { ["filter"] = clauses } };
        JsonNode merged = baseQuery is null
            ? generated
            : new JsonObject { ["bool"] = new JsonObject { ["must"] = new JsonArray { baseQuery, generated } } };

        propertyOverrides = new Dictionary<string, object?> { ["query"] = ToJsonElement(merged) };
        return true;
    }

    private static bool TryBuildClause(PreviewFilter filter, ReportSchema schema, out JsonObject clause)
    {
        ColumnType? columnType = schema.Find(filter.Column)?.Type;

        if (filter.Operator is PreviewFilterOperator.Contains or PreviewFilterOperator.StartsWith)
        {
            // wildcard/prefix require a string operand — declining on a non-String column mirrors
            // AdoFilterTranslator's/ODataFilterTranslator's "decline, don't emit garbage" stance for
            // the same operator/type combination (ADR D45/D62/D64).
            if (columnType is not null && columnType != ColumnType.String)
            {
                clause = new JsonObject();
                return false;
            }

            string text = filter.Value ?? string.Empty;
            clause = filter.Operator == PreviewFilterOperator.StartsWith
                ? new JsonObject { ["prefix"] = new JsonObject { [filter.Column] = text } }
                : new JsonObject { ["wildcard"] = new JsonObject { [filter.Column] = $"*{EscapeWildcard(text)}*" } };
            return true;
        }

        if (!TryBuildLiteral(filter.Value, columnType, out JsonNode? literal))
        {
            clause = new JsonObject();
            return false;
        }

        if (filter.Operator is PreviewFilterOperator.Equals or PreviewFilterOperator.NotEquals)
        {
            var term = new JsonObject { ["term"] = new JsonObject { [filter.Column] = literal } };
            clause = filter.Operator == PreviewFilterOperator.Equals
                ? term
                : new JsonObject { ["bool"] = new JsonObject { ["must_not"] = new JsonArray { term } } };
            return true;
        }

        string rangeOp = filter.Operator switch
        {
            PreviewFilterOperator.GreaterThan => "gt",
            PreviewFilterOperator.GreaterThanOrEqual => "gte",
            PreviewFilterOperator.LessThan => "lt",
            PreviewFilterOperator.LessThanOrEqual => "lte",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unknown preview filter operator."),
        };

        clause = new JsonObject { ["range"] = new JsonObject { [filter.Column] = new JsonObject { [rangeOp] = literal } } };
        return true;
    }

    // Literal formatting mirrors ODataFilterTranslator.TryFormatLiteral's switch (ADR D62/D64), but
    // values are embedded as native JsonNode types directly — no string-quoting/unquoting rules to
    // get right (OData's URL literal grammar has none of this: everything here is just JSON).
    private static bool TryBuildLiteral(string? value, ColumnType? columnType, out JsonNode? literal)
    {
        string text = value ?? string.Empty;

        switch (columnType)
        {
            case ColumnType.Uuid:
            case ColumnType.String:
                literal = JsonValue.Create(text);
                return true;

            case ColumnType.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
                {
                    literal = null;
                    return false;
                }

                literal = JsonValue.Create(integerValue);
                return true;

            case ColumnType.Decimal:
            case ColumnType.Money:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal decimalValue))
                {
                    literal = null;
                    return false;
                }

                literal = JsonValue.Create(decimalValue);
                return true;

            case ColumnType.Boolean:
                if (!bool.TryParse(text, out bool boolValue))
                {
                    literal = null;
                    return false;
                }

                literal = JsonValue.Create(boolValue);
                return true;

            case ColumnType.Date:
            case ColumnType.Time:
            case ColumnType.DateTime:
            case ColumnType.Timestamp:
                // AssumeUniversal|AdjustToUniversal (unlike ODataFilterTranslator's DateTimeStyles.None):
                // an offset-less value (e.g. "2026-01-01") must not be interpreted in whatever timezone
                // the report engine's host happens to run in — Elasticsearch date fields are
                // conventionally stored in UTC, and a host-timezone-dependent parse would silently shift
                // the filter boundary by the host's UTC offset, differently across deployments (code-review finding).
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                {
                    literal = null;
                    return false;
                }

                literal = JsonValue.Create(parsed.ToString("O", CultureInfo.InvariantCulture));
                return true;

            default:
                // Unknown/unset column type (Json, Binary, or no declared schema column) — treat as a
                // string literal, the safest default that never emits an unvalidated raw token.
                literal = JsonValue.Create(text);
                return true;
        }
    }

    // Escapes Elasticsearch wildcard-query metacharacters ('*', '?') and the escape character itself
    // ('\') in a Contains filter's value, so a value containing a literal '*'/'?' (e.g. "50%*off")
    // can't silently change the wildcard query's meaning once wrapped in "*value*" (ADR D64).
    private static string EscapeWildcard(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("*", "\\*", StringComparison.Ordinal)
             .Replace("?", "\\?", StringComparison.Ordinal);

    private static JsonElement ToJsonElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}
