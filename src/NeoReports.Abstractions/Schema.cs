namespace NeoReports.Abstractions;

/// <summary>Semantic type of a report column, used for formatting and output projection.</summary>
public enum ColumnType
{
    /// <summary>Text value.</summary>
    String,

    /// <summary>Whole number.</summary>
    Integer,

    /// <summary>Fixed-point decimal number.</summary>
    Decimal,

    /// <summary>Monetary amount (decimal with currency formatting).</summary>
    Money,

    /// <summary>Boolean value.</summary>
    Boolean,

    /// <summary>Calendar date without time.</summary>
    Date,

    /// <summary>Time of day without date.</summary>
    Time,

    /// <summary>
    /// Date and time carrying no zone or offset — a "wall clock" reading, and what
    /// <see cref="System.DateTime"/> members are inferred as (SQL Server <c>datetime2</c>,
    /// PostgreSQL <c>timestamp</c>, Oracle <c>TIMESTAMP</c>).
    /// </summary>
    DateTime,

    /// <summary>
    /// An offset-aware point in time, and what <see cref="System.DateTimeOffset"/> members are
    /// inferred as (PostgreSQL <c>timestamptz</c>, SQL Server <c>datetimeoffset</c>, Oracle
    /// <c>TIMESTAMP WITH TIME ZONE</c>).
    /// <para>
    /// The distinction from <see cref="DateTime"/> is load-bearing, not cosmetic: a provider that
    /// binds a temporal value as text has to cast it to the matching zoned type. Casting an
    /// offset-bearing value to a zone-less one discards the offset and re-reads the value in the
    /// session's time zone, which moves the instant — silently skipping or repeating rows when the
    /// value is a keyset cursor (ADR D81).
    /// </para>
    /// </summary>
    Timestamp,

    /// <summary>Universally unique identifier.</summary>
    Uuid,

    /// <summary>JSON document.</summary>
    Json,

    /// <summary>Raw binary value.</summary>
    Binary
}

/// <summary>
/// Describes a single output column of a report: its name, semantic type, and formatting hints.
/// Columns are declared in the builder and drive the projection of <c>T</c> rows
/// into the values written by formats.
/// </summary>
/// <param name="Name">Stable column key, unique within a schema.</param>
/// <param name="Type">Semantic type used for formatting and projection.</param>
/// <param name="Nullable">Whether the column may contain null values.</param>
/// <param name="DisplayName">Optional header label; defaults to <paramref name="Name"/> when null.</param>
/// <param name="Format">Optional .NET format string applied when rendering the value.</param>
/// <param name="Culture">Optional culture name used for formatting (e.g. "pt-BR").</param>
/// <param name="Metadata">Optional free-form metadata for plugins.</param>
public sealed record ReportColumn(
    string Name,
    ColumnType Type,
    bool Nullable = true,
    string? DisplayName = null,
    string? Format = null,
    string? Culture = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>Ordered set of <see cref="ReportColumn"/> describing a report's output shape.</summary>
public sealed class ReportSchema
{
    private readonly Dictionary<string, int> _indexByName;

    /// <summary>Creates a schema from columns in output order.</summary>
    /// <param name="columns">The columns, in the order they are written.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="columns"/> is null.</exception>
    public ReportSchema(IReadOnlyList<ReportColumn> columns)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        _indexByName = new(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
            _indexByName[columns[i].Name] = i;
    }

    /// <summary>Columns in output order.</summary>
    public IReadOnlyList<ReportColumn> Columns { get; }

    /// <summary>Number of columns.</summary>
    public int Count => Columns.Count;

    /// <summary>Returns the column with the given name, or <c>null</c> when absent.</summary>
    /// <param name="name">Column key to look up.</param>
    public ReportColumn? Find(string name) =>
        _indexByName.TryGetValue(name, out var i) ? Columns[i] : null;

    /// <summary>Returns the output index of a column name, or -1 when absent.</summary>
    /// <param name="name">Column key to look up.</param>
    public int IndexOf(string name) =>
        _indexByName.TryGetValue(name, out var i) ? i : -1;

    /// <summary>True when a column with the given name exists.</summary>
    /// <param name="name">Column key to look up.</param>
    public bool Contains(string name) => _indexByName.ContainsKey(name);
}
