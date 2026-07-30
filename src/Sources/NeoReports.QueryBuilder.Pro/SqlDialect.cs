using NeoReports.Abstractions;

namespace NeoReports.QueryBuilder.Pro;

/// <summary>
/// The per-provider knobs the <see cref="KeysetSqlGenerator"/> needs (ADR D49): the bind-variable
/// prefix, identifier quoting, and an optional type-driven cast for a text-bound parameter — the
/// same three concerns <c>AdoFilterTranslator</c> is parametrized by, kept independent here so the
/// Pro package has no dependency on the source packages. Four ready presets are provided.
/// </summary>
/// <param name="ParameterPrefix">Bind-variable prefix (<c>@</c> for SQL Server/Postgres/MySQL, <c>:</c> for Oracle).</param>
/// <param name="QuoteIdentifier">Quotes one identifier part, doubling the embedded delimiter (injection-neutralizing).</param>
/// <param name="CastParameter">
/// Given a column's <see cref="ColumnType"/> and a bind token (e.g. <c>@qbfilter0</c>), returns the
/// expression to compare against instead of the bare token (e.g. Postgres's <c>{token}::uuid</c>) —
/// or <c>null</c> to leave it bare. <c>null</c> (the field) applies no cast to anything.
/// </param>
public sealed record SqlDialect(
    string ParameterPrefix,
    Func<string, string> QuoteIdentifier,
    Func<ColumnType, string, string?>? CastParameter = null)
{
    /// <summary>ANSI double-quote quoting: <c>foo</c> → <c>"foo"</c>, embedded <c>"</c> doubled.</summary>
    public static string QuoteAnsi(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>MySQL back-tick quoting: <c>foo</c> → <c>`foo`</c>, embedded <c>`</c> doubled.</summary>
    public static string QuoteMySql(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>SQL Server bracket quoting: <c>foo</c> → <c>[foo]</c>, embedded <c>]</c> doubled.</summary>
    public static string QuoteSqlServer(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>PostgreSQL: <c>@</c> prefix, ANSI quoting, <c>::type</c> casts (no implicit text→typed comparison).</summary>
    public static readonly SqlDialect Postgres = new("@", QuoteAnsi, PostgresCast);

    /// <summary>MySQL/MariaDB: <c>@</c> prefix, back-tick quoting, no casts (implicit conversion works).</summary>
    public static readonly SqlDialect MySql = new("@", QuoteMySql);

    /// <summary>SQL Server: <c>@</c> prefix, bracket quoting, no casts (implicit conversion works).</summary>
    public static readonly SqlDialect SqlServer = new("@", QuoteSqlServer);

    /// <summary>Oracle: <c>:</c> prefix, ANSI quoting, locale-independent numeric casts.</summary>
    public static readonly SqlDialect Oracle = new(":", QuoteAnsi, OracleCast);

    /// <summary>SQLite: <c>@</c> prefix, ANSI quoting, no casts (operand-affinity coercion works — ADR D56).</summary>
    public static readonly SqlDialect Sqlite = new("@", QuoteAnsi);

    /// <summary>Amazon Redshift: <c>@</c> prefix, ANSI quoting, the same casts as Postgres (its documented lineage — not empirically re-verified, ADR D57).</summary>
    public static readonly SqlDialect Redshift = new("@", QuoteAnsi, PostgresCast);

    /// <summary>Snowflake: <c>:</c> prefix (verified against driver docs, not <c>@</c>), ANSI quoting, no casts (documented implicit VARCHAR→NUMBER conversion — not empirically re-verified, ADR D57).</summary>
    public static readonly SqlDialect Snowflake = new(":", QuoteAnsi);

    /// <summary>Resolves a preset by source type id (as used by <c>ISchemaExplorer.Type</c>), or <c>null</c>.</summary>
    public static SqlDialect? ForType(string type) => type?.ToLowerInvariant() switch
    {
        "postgres" => Postgres,
        "mysql" => MySql,
        "sql" => SqlServer,
        "oracle" => Oracle,
        "sqlite" => Sqlite,
        "redshift" => Redshift,
        "snowflake" => Snowflake,
        _ => null,
    };

    private static string? PostgresCast(ColumnType type, string token) => type switch
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

    private static string? OracleCast(ColumnType type, string token) => type switch
    {
        ColumnType.Integer or ColumnType.Decimal or ColumnType.Money =>
            $"TO_NUMBER({token}, 'FM999999999999999990.099999999999999999', 'NLS_NUMERIC_CHARACTERS=''.,''')",
        // A temporal key's cursor is the KeysetCursorCodec's culture-invariant ISO-8601 round-trip
        // form (e.g. 2026-01-01T00:00:00.0000000). Oracle binds it as text and would otherwise
        // implicit-convert it with the session's NLS_DATE_FORMAT — which is not ISO-8601, so the
        // second page throws ORA-01858. Parse it explicitly with the exact format model the codec
        // documents (7 fractional digits, matching .NET's "O" specifier). Comparing a DATE column
        // against a TIMESTAMP promotes the DATE, so one model covers DATE/DATETIME/TIMESTAMP keys.
        ColumnType.Date or ColumnType.DateTime or ColumnType.Timestamp =>
            $"TO_TIMESTAMP({token}, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF7')",
        _ => null,
    };
}

/// <summary>Maps a catalog's declared DB type name to the nearest NeoReports <see cref="ColumnType"/>.</summary>
public static class SqlTypeMap
{
    /// <summary>
    /// Best-effort DB-type-name → <see cref="ColumnType"/> mapping, by case-insensitive substring —
    /// covers the four supported dialects' common type names (e.g. Postgres <c>integer</c>/<c>uuid</c>,
    /// SQL Server <c>int</c>/<c>uniqueidentifier</c>, MySQL <c>varchar</c>, Oracle <c>NUMBER</c>/
    /// <c>VARCHAR2</c>). Anything unrecognized maps to <see cref="ColumnType.String"/> — the safe
    /// default, since every value has a text form.
    /// </summary>
    public static ColumnType ToColumnType(string? dbType)
    {
        string t = (dbType ?? string.Empty).ToLowerInvariant();
        if (t.Contains("uuid", StringComparison.Ordinal) || t.Contains("uniqueidentifier", StringComparison.Ordinal))
            return ColumnType.Uuid;
        if (t.Contains("bool", StringComparison.Ordinal) || t == "bit")
            return ColumnType.Boolean;
        if (t.Contains("money", StringComparison.Ordinal))
            return ColumnType.Money;
        if (t.Contains("int", StringComparison.Ordinal))
            return ColumnType.Integer;
        if (t.Contains("num", StringComparison.Ordinal) || t.Contains("dec", StringComparison.Ordinal)
            || t.Contains("float", StringComparison.Ordinal) || t.Contains("double", StringComparison.Ordinal)
            || t.Contains("real", StringComparison.Ordinal))
            return ColumnType.Decimal;
        if (t.Contains("timestamp", StringComparison.Ordinal) || t.Contains("datetime", StringComparison.Ordinal))
            return ColumnType.DateTime;
        if (t.Contains("date", StringComparison.Ordinal))
            return ColumnType.Date;
        if (t.Contains("time", StringComparison.Ordinal))
            return ColumnType.Time;
        return ColumnType.String;
    }
}
