using System.Globalization;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;

namespace NeoReports.Sources.OData;

/// <summary>
/// <see cref="IFilterTranslator"/> for OData v4 sources (ADR D62) — the reason OData is more than
/// "REST with <c>value</c> at the root". The closed <see cref="PreviewFilterOperator"/> set maps
/// directly onto OData v4 <c>$filter</c>: <c>Equals</c>/<c>NotEquals</c> → <c>eq</c>/<c>ne</c>,
/// <c>GreaterThan(OrEqual)</c>/<c>LessThan(OrEqual)</c> → <c>gt</c>/<c>ge</c>/<c>lt</c>/<c>le</c>,
/// <c>Contains</c>/<c>StartsWith</c> → <c>contains(...)</c>/<c>startswith(...)</c>; multiple filters
/// join with <c>and</c>. Unlike <c>AdoFilterTranslator</c>, there is no bind-parameter mechanism —
/// <c>$filter</c> inlines every value directly into the request URL, so <c>parameters</c> is always
/// empty. A pre-existing, author-configured static <c>$filter</c> property (read from
/// <c>properties["filter"]</c>) is ANDed with the generated expression rather than replaced.
/// </summary>
public sealed class ODataFilterTranslator : IFilterTranslator
{
    /// <inheritdoc />
    public string Type => "odata";

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

        // No bind-parameter mechanism — $filter inlines every value into the URL.
        parameters = new Dictionary<string, object?>();

        string? existingFilter = properties.TryGetValue("filter", out object? existing) && existing is string { Length: > 0 } text
            ? text
            : null;

        if (filters.Count == 0)
        {
            propertyOverrides = existingFilter is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["filter"] = existingFilter };
            return true;
        }

        var conditions = new List<string>(filters.Count);
        foreach (PreviewFilter filter in filters)
        {
            if (!TryBuildCondition(filter, schema, out string condition))
            {
                propertyOverrides = new Dictionary<string, object?>();
                return false;
            }

            conditions.Add(condition);
        }

        string generated = string.Join(" and ", conditions);
        string combined = existingFilter is null ? generated : $"({existingFilter}) and ({generated})";

        propertyOverrides = new Dictionary<string, object?> { ["filter"] = combined };
        return true;
    }

    private static bool TryBuildCondition(PreviewFilter filter, ReportSchema schema, out string condition)
    {
        ColumnType? columnType = schema.Find(filter.Column)?.Type;

        if (filter.Operator is PreviewFilterOperator.Contains or PreviewFilterOperator.StartsWith)
        {
            // contains()/startswith() require a string operand — declining on a non-String column
            // mirrors AdoFilterTranslator's "decline, don't emit garbage" stance for the same
            // operator/type combination (ADR D45/D62), rather than emitting a function call OData
            // would reject at execution time with a type-mismatch error.
            if (columnType is not null && columnType != ColumnType.String)
            {
                condition = string.Empty;
                return false;
            }

            string quoted = QuoteString(filter.Value ?? string.Empty);
            condition = filter.Operator == PreviewFilterOperator.Contains
                ? $"contains({filter.Column},{quoted})"
                : $"startswith({filter.Column},{quoted})";
            return true;
        }

        if (!TryFormatLiteral(filter.Value, columnType, out string literal))
        {
            condition = string.Empty;
            return false;
        }

        string op = filter.Operator switch
        {
            PreviewFilterOperator.Equals => "eq",
            PreviewFilterOperator.NotEquals => "ne",
            PreviewFilterOperator.GreaterThan => "gt",
            PreviewFilterOperator.GreaterThanOrEqual => "ge",
            PreviewFilterOperator.LessThan => "lt",
            PreviewFilterOperator.LessThanOrEqual => "le",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unknown preview filter operator."),
        };

        condition = $"{filter.Column} {op} {literal}";
        return true;
    }

    // Literal formatting — OData's analog of AdoFilterTranslator's bind-parameter cast, since
    // $filter inlines everything into the URL with no bind-parameter mechanism (ADR D62).
    // String: single-quoted, doubling an embedded quote. Uuid: OData v4's Edm.Guid literal is
    // UNQUOTED (a v4 departure from v2/v3's quoted "guid'...'" form — the v4 ABNF's guidValue
    // production has no surrounding quotes), so a value that doesn't parse as a GUID declines
    // rather than being emitted as a bare, unvalidated token. Numeric/Boolean: parse-validated,
    // then emitted from the PARSED value (not the original text) so a value that passed validation
    // via an allowed-but-non-canonical form (e.g. a thousands separator under NumberStyles.Number)
    // can't leak invalid syntax into the URL — declining (returns false) on a non-parseable value
    // rather than emitting a malformed expression. Date/Time/DateTime/Timestamp: parsed via
    // DateTimeOffset then emitted as a round-tripped ISO-8601 ("O") literal, OData v4's unquoted
    // date/datetime-offset literal form.
    private static bool TryFormatLiteral(string? value, ColumnType? columnType, out string literal)
    {
        string text = value ?? string.Empty;

        switch (columnType)
        {
            case ColumnType.String:
                literal = QuoteString(text);
                return true;

            case ColumnType.Uuid:
                if (!Guid.TryParse(text, out Guid guid))
                {
                    literal = string.Empty;
                    return false;
                }

                literal = guid.ToString();
                return true;

            case ColumnType.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
                {
                    literal = string.Empty;
                    return false;
                }

                literal = integerValue.ToString(CultureInfo.InvariantCulture);
                return true;

            case ColumnType.Decimal:
            case ColumnType.Money:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal decimalValue))
                {
                    literal = string.Empty;
                    return false;
                }

                literal = decimalValue.ToString(CultureInfo.InvariantCulture);
                return true;

            case ColumnType.Boolean:
                if (!bool.TryParse(text, out bool boolValue))
                {
                    literal = string.Empty;
                    return false;
                }

                literal = boolValue ? "true" : "false";
                return true;

            case ColumnType.Date:
            case ColumnType.Time:
            case ColumnType.DateTime:
            case ColumnType.Timestamp:
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
                {
                    literal = string.Empty;
                    return false;
                }

                literal = parsed.ToString("O", CultureInfo.InvariantCulture);
                return true;

            default:
                // Unknown/unset column type (Json, Binary, or no declared schema column) — quote as
                // a string literal, the safest default that never emits a bare, uncastable token.
                literal = QuoteString(text);
                return true;
        }
    }

    private static string QuoteString(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
