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

    /// <summary>Date and time.</summary>
    DateTime,

    /// <summary>Point in time, typically UTC.</summary>
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
