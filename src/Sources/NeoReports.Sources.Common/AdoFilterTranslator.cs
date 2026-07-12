using System.Globalization;
using NeoReports.Core.Preview;

namespace NeoReports.Sources.Common;

/// <summary>
/// Shared <see cref="IFilterTranslator"/> for every relational provider (ADR D45): wraps the
/// original keyset query as a derived table and applies the filters in an outer <c>WHERE</c>, e.g.
/// <c>SELECT * FROM (&lt;sql&gt;) t WHERE t.Amount &gt; @filter0</c>. No table alias keyword ("AS")
/// is used before <c>t</c> — Oracle rejects it for derived tables, and every other supported dialect
/// accepts an alias with or without it, so omitting it is the one syntax that works everywhere.
/// Filter values are always bound as parameters, never string-concatenated into the query text.
/// One instance is registered per provider, parametrized by its bind-variable prefix (Oracle's
/// <c>:</c> vs. everyone else's <c>@</c>) — the translation logic itself is identical ADO.NET
/// regardless of dialect.
/// </summary>
public sealed class AdoFilterTranslator : IFilterTranslator
{
    private readonly string _parameterPrefix;

    /// <summary>Creates a translator for one provider type.</summary>
    /// <param name="type">Source type id this translator handles (e.g. "postgres").</param>
    /// <param name="parameterPrefix">Bind-variable prefix the provider expects (<c>@</c> by default, <c>:</c> for Oracle).</param>
    public AdoFilterTranslator(string type, string parameterPrefix = "@")
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        _parameterPrefix = string.IsNullOrEmpty(parameterPrefix) ? "@" : parameterPrefix;
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public bool TryTranslate(
        string sql,
        IReadOnlyList<PreviewFilter> filters,
        out string translatedSql,
        out IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(filters);

        if (filters.Count == 0)
        {
            translatedSql = sql;
            parameters = new Dictionary<string, object?>();
            return true;
        }

        var conditions = new List<string>(filters.Count);
        var values = new Dictionary<string, object?>(filters.Count, StringComparer.Ordinal);

        for (var i = 0; i < filters.Count; i++)
        {
            PreviewFilter filter = filters[i];
            string paramName = "filter" + i.ToString(CultureInfo.InvariantCulture);
            string token = _parameterPrefix + paramName;

            (string op, object? value) = filter.Operator switch
            {
                PreviewFilterOperator.Equals => ("=", filter.Value),
                PreviewFilterOperator.NotEquals => ("<>", filter.Value),
                PreviewFilterOperator.GreaterThan => (">", filter.Value),
                PreviewFilterOperator.GreaterThanOrEqual => (">=", filter.Value),
                PreviewFilterOperator.LessThan => ("<", filter.Value),
                PreviewFilterOperator.LessThanOrEqual => ("<=", filter.Value),
                PreviewFilterOperator.Contains => ("LIKE", $"%{filter.Value}%"),
                PreviewFilterOperator.StartsWith => ("LIKE", $"{filter.Value}%"),
                _ => throw new ArgumentOutOfRangeException(nameof(filters), filter.Operator, "Unknown preview filter operator."),
            };

            conditions.Add($"t.{filter.Column} {op} {token}");
            values[paramName] = value;
        }

        translatedSql = $"SELECT * FROM ({sql}) t WHERE {string.Join(" AND ", conditions)}";
        parameters = values;
        return true;
    }
}
