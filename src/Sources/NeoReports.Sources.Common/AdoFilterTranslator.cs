using System.Globalization;
using NeoReports.Abstractions;
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
/// <remarks>
/// A filter's bound value is always its literal text form — <see cref="PreviewFilter.Value"/> is
/// <c>string?</c>, matching exactly what the preview UI's plain text input sends, regardless of the
/// filtered column's real type (G7). Unlike the D43 keyset cursor (part of the report author's own
/// trusted SQL text, so they can hand-write a cast such as <c>@cursor::bigint</c>), the
/// <c>WHERE</c> fragment here is synthesized by this class with no author-controlled place to add
/// one — so a dialect whose implicit text-to-typed conversion is missing (Postgres) or unreliable
/// (Oracle's is session-NLS-dependent for decimal separators) needs an explicit cast added for it,
/// via the constructor's <c>castParameter</c>.
/// </remarks>
public sealed class AdoFilterTranslator : IFilterTranslator
{
    private readonly string _parameterPrefix;
    private readonly Func<ColumnType, string, string?>? _castParameter;
    private readonly string? _innerQuerySuffix;
    private readonly Func<string, string>? _quoteIdentifier;

    /// <summary>Creates a translator for one provider type.</summary>
    /// <param name="type">Source type id this translator handles (e.g. "postgres").</param>
    /// <param name="parameterPrefix">Bind-variable prefix the provider expects (<c>@</c> by default, <c>:</c> for Oracle).</param>
    /// <param name="castParameter">
    /// Given a filtered column's declared <see cref="ColumnType"/> and its bind parameter token
    /// (e.g. <c>"@filter0"</c>), returns the expression to compare against instead of the bare
    /// token — e.g. Postgres's <c>"{token}::numeric"</c>, Oracle's <c>"TO_NUMBER({token}, ...)"</c>
    /// — or <c>null</c> to leave the token bare (no cast needed for that type, or the dialect
    /// implicitly converts it correctly). <c>null</c> (the default) applies no cast to anything —
    /// the right choice for a dialect whose implicit text-to-typed conversion is reliable.
    /// </param>
    /// <param name="innerQuerySuffix">
    /// Text appended directly after <c>sql</c> before it is wrapped as a derived table — e.g. SQL
    /// Server's <c>" OFFSET 0 ROWS"</c>, needed because a derived table containing a bare
    /// <c>ORDER BY</c> (which every keyset query already ends with) is invalid T-SQL unless
    /// followed by <c>TOP</c>, <c>OFFSET</c>, or <c>FOR XML</c>. <c>null</c> (the default) appends
    /// nothing — the right choice for a dialect that allows <c>ORDER BY</c> in a derived table.
    /// </param>
    /// <param name="quoteIdentifier">
    /// Given a filtered column's name, returns how to reference it in the outer <c>t.{...}</c>
    /// comparison — e.g. Oracle's <see cref="OracleQuoteIdentifier"/>, which wraps it in double
    /// quotes when it collides with a reserved word (e.g. <c>Date</c>) that Oracle otherwise
    /// refuses to parse as a bare column reference (ORA-01747). <c>null</c> (the default) leaves
    /// every column bare — the right choice for a dialect with no such collision to work around.
    /// </param>
    public AdoFilterTranslator(
        string type,
        string parameterPrefix = "@",
        Func<ColumnType, string, string?>? castParameter = null,
        string? innerQuerySuffix = null,
        Func<string, string>? quoteIdentifier = null)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        _parameterPrefix = string.IsNullOrEmpty(parameterPrefix) ? "@" : parameterPrefix;
        _castParameter = castParameter;
        _innerQuerySuffix = innerQuerySuffix;
        _quoteIdentifier = quoteIdentifier;
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public bool TryTranslate(
        IReadOnlyDictionary<string, object?> properties,
        IReadOnlyList<PreviewFilter> filters,
        ReportSchema schema,
        out IReadOnlyDictionary<string, object?> propertyOverrides,
        out IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(schema);

        if (properties is null || !properties.TryGetValue("sql", out object? sqlValue) || sqlValue is not string sql)
            throw new ConfigurationException("The ADO source has no 'sql' property to filter.");

        if (filters.Count == 0)
        {
            propertyOverrides = new Dictionary<string, object?> { ["sql"] = sql };
            parameters = new Dictionary<string, object?>();
            return true;
        }

        var conditions = new List<string>(filters.Count);
        var values = new Dictionary<string, object?>(filters.Count, StringComparer.Ordinal);

        for (var i = 0; i < filters.Count; i++)
        {
            PreviewFilter filter = filters[i];

            // A LIKE comparison only makes sense against text — Contains/StartsWith on a numeric/date
            // column would otherwise emit an uncastable `t.Amount LIKE @filter0`, which every dialect
            // rejects at execution time with a raw provider error (e.g. Postgres's "operator does not
            // exist: numeric ~~ unknown") instead of the honest 400 a translator declining to
            // translate produces.
            if (filter.Operator is PreviewFilterOperator.Contains or PreviewFilterOperator.StartsWith
                && schema.Find(filter.Column)?.Type != ColumnType.String)
            {
                propertyOverrides = new Dictionary<string, object?> { ["sql"] = sql };
                parameters = new Dictionary<string, object?>();
                return false;
            }

            string paramName = "filter" + i.ToString(CultureInfo.InvariantCulture);
            string token = _parameterPrefix + paramName;

            (string op, string? value) = filter.Operator switch
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

            // LIKE always compares text to text (Contains/StartsWith coerce the bound value to a
            // wildcarded string above) — casting only applies to the other, type-sensitive comparisons.
            string comparand = op == "LIKE" ? token : ApplyCast(token, schema.Find(filter.Column)?.Type);

            conditions.Add($"t.{QuoteIdentifier(filter.Column)} {op} {comparand}");
            values[paramName] = value;
        }

        string innerSql = _innerQuerySuffix is null ? sql : sql + _innerQuerySuffix;
        string translatedSql = $"SELECT * FROM ({innerSql}) t WHERE {string.Join(" AND ", conditions)}";
        propertyOverrides = new Dictionary<string, object?> { ["sql"] = translatedSql };
        parameters = values;
        return true;
    }

    private string ApplyCast(string token, ColumnType? columnType)
    {
        if (_castParameter is null || columnType is null)
            return token;

        return _castParameter(columnType.Value, token) ?? token;
    }

    private string QuoteIdentifier(string column) => _quoteIdentifier?.Invoke(column) ?? column;

    /// <summary>
    /// Casts a filter's bind parameter (always text-bound, coming from the preview UI's plain text
    /// input) to the filtered column's real type using Postgres's <c>::type</c> syntax — Postgres has
    /// no implicit conversion between <c>text</c> and most other types in a comparison operator.
    /// Returns <c>null</c> for <see cref="ColumnType.String"/>/<see cref="ColumnType.Json"/>/
    /// <see cref="ColumnType.Binary"/> (no cast needed, or no single safe cast to guess).
    /// </summary>
    public static string? PostgresCast(ColumnType type, string token) => type switch
    {
        ColumnType.Integer => $"{token}::bigint",
        ColumnType.Decimal or ColumnType.Money => $"{token}::numeric",
        ColumnType.Boolean => $"{token}::boolean",
        ColumnType.Date => $"{token}::date",
        ColumnType.Time => $"{token}::time",
        ColumnType.DateTime or ColumnType.Timestamp => $"{token}::timestamp",
        ColumnType.Uuid => $"{token}::uuid",
        _ => null,
    };

    /// <summary>
    /// Casts a filter's numeric bind parameter to <c>NUMBER</c> using an explicit format model and
    /// an <c>NLS_NUMERIC_CHARACTERS</c> override that fixes '.' as the decimal separator — Oracle
    /// does implicitly convert a <c>VARCHAR2</c> parameter to <c>NUMBER</c> in a comparison, but that
    /// conversion follows the session's NLS settings, so a value like <c>"2000.00"</c> (the
    /// invariant-culture format every filter value uses) can fail with <c>ORA-01722</c> against a
    /// session whose numeric locale doesn't treat '.' as the decimal separator. The format model has
    /// no explicit sign element (<c>S</c>/<c>MI</c>/<c>PR</c>) on purpose — verified empirically
    /// against a real Oracle container that this plain form already accepts a leading <c>-</c> for
    /// negative values with no special handling, while adding <c>S</c> instead breaks *positive*
    /// values (Oracle then requires an explicit leading <c>+</c>, which invariant-culture formatting
    /// never produces).
    /// </summary>
    /// <remarks>
    /// Only numeric column types are covered here. Boolean/Uuid/Date/DateTime/Timestamp casting has
    /// a separate, still-open gap — there is no single safe cast to guess for those without knowing
    /// the report author's actual underlying Oracle column type (e.g. a <c>Boolean</c> column could
    /// be <c>NUMBER(1)</c> or <c>CHAR(1)</c>) — so all of those are deliberately left uncast (return
    /// <c>null</c>) rather than shipping an unverified fix. Tracked as a follow-up. (The unrelated
    /// reserved-word quoting bug this remark used to mention, ORA-01747 on a <c>Date</c> column, is
    /// fixed separately by <see cref="OracleQuoteIdentifier"/>.)
    /// </remarks>
    public static string? OracleCast(ColumnType type, string token) => type switch
    {
        ColumnType.Integer or ColumnType.Decimal or ColumnType.Money =>
            $"TO_NUMBER({token}, 'FM999999999999999990.099999999999999999', 'NLS_NUMERIC_CHARACTERS=''.,''')",
        _ => null,
    };

    /// <summary>
    /// Quotes <paramref name="column"/> Oracle-style (double quotes) when — and only when — it
    /// collides with an Oracle reserved word/datatype name such as <c>Date</c>, which Oracle
    /// otherwise refuses to parse as a bare column reference (<c>ORA-01747</c>). Every other
    /// column is left bare: a non-colliding column is necessarily unquoted in the report author's
    /// own inner SQL too, so Oracle folds its stored name to upper-case, and an unquoted <c>t.Foo</c>
    /// reference already matches that case-insensitively — quoting it would instead demand an exact
    /// case match this translator has no way to know.
    /// </summary>
    /// <remarks>
    /// A reserved-word column, by contrast, can only exist in the inner SQL because the report
    /// author was forced to quote its alias there (a bare <c>DATE</c> alias isn't valid Oracle
    /// syntax at all) — by convention they quote it with the exact same casing as the column's
    /// declared <see cref="ReportSchema"/> name (see <c>OracleFilterTranslatorIntegrationTests</c>),
    /// so quoting here with that same declared name matches what Oracle actually stored.
    /// </remarks>
    public static string OracleQuoteIdentifier(string column) =>
        OracleReservedWords.Contains(column) ? $"\"{column}\"" : column;

    // Oracle reserved words/datatype names plausible as a report column name. Not the full Oracle
    // reserved-word list (hundreds of entries, most unusable as column names to begin with) — just
    // the ones a report author would naturally reach for.
    private static readonly HashSet<string> OracleReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS", "AUDIT", "CLUSTER", "COLUMN", "COMMENT", "DATE", "DEFAULT", "FILE", "GROUP",
        "INDEX", "LEVEL", "MODE", "NUMBER", "OPTION", "ORDER", "RESOURCE", "ROW", "ROWID",
        "ROWNUM", "SESSION", "SIZE", "START", "TABLE", "TYPE", "UID", "USER", "VIEW",
    };
}
