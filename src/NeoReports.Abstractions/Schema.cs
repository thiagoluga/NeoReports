namespace NeoReports.Abstractions;

/// <summary>Semantic type of a report column, used for formatting and output projection.</summary>
public enum ColumnType
{
    String, Integer, Decimal, Money, Boolean,
    Date, Time, DateTime, Timestamp, Uuid, Json, Binary
}

/// <summary>
/// Describes a single output column of a report: its name, semantic type, and formatting hints.
/// Columns are declared in the builder and drive the projection of <typeparamref name="T"/> rows
/// into the values written by formats.
/// </summary>
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
    public ReportColumn? Find(string name) =>
        _indexByName.TryGetValue(name, out var i) ? Columns[i] : null;

    /// <summary>Returns the output index of a column name, or -1 when absent.</summary>
    public int IndexOf(string name) =>
        _indexByName.TryGetValue(name, out var i) ? i : -1;

    /// <summary>True when a column with the given name exists.</summary>
    public bool Contains(string name) => _indexByName.ContainsKey(name);
}
