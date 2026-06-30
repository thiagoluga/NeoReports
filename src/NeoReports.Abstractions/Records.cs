namespace NeoReports.Abstractions;

/// <summary>
/// A positional, schema-aligned row used by the dynamic (config-driven) path. It carries an
/// <see cref="ReportSchema"/> and an <c>object?[]</c> of values in the schema's column order, so it
/// can flow through the exact same pipeline as a typed row: the writer edge already consumes
/// <c>object?[]</c> + schema, and the dynamic projection getters simply read by position.
/// </summary>
/// <remarks>
/// This is deliberately <b>not</b> a <c>IDictionary&lt;string, object?&gt;</c> row (architecture
/// rule 1). The values are positional; name access is a convenience resolved through the schema for
/// authoring and dynamic filters. One schema instance is shared across every row of a report, so the
/// per-row cost is a single reference plus the value array — constant memory is preserved.
/// </remarks>
public sealed class ReportRecord
{
    private readonly object?[] _values;

    /// <summary>Creates a record whose values are aligned, by position, to <paramref name="schema"/>.</summary>
    /// <param name="schema">The schema describing the columns, in order.</param>
    /// <param name="values">Values in schema column order; its length must equal the column count.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="schema"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the value count does not match the column count.</exception>
    public ReportRecord(ReportSchema schema, object?[] values)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _values = values ?? throw new ArgumentNullException(nameof(values));
        if (values.Length != schema.Count)
        {
            throw new ArgumentException(
                $"Value count ({values.Length}) does not match the schema column count ({schema.Count}).",
                nameof(values));
        }
    }

    /// <summary>The schema describing this record's columns, in order.</summary>
    public ReportSchema Schema { get; }

    /// <summary>Number of values (equals the schema column count).</summary>
    public int Count => _values.Length;

    /// <summary>The values, in schema column order.</summary>
    public IReadOnlyList<object?> Values => _values;

    /// <summary>Gets the value at the given column position.</summary>
    /// <param name="index">Zero-based column index.</param>
    /// <exception cref="IndexOutOfRangeException">Thrown when <paramref name="index"/> is out of range.</exception>
    public object? this[int index] => _values[index];

    /// <summary>Gets the value of the column with the given name.</summary>
    /// <param name="name">Column name as declared in the schema.</param>
    /// <exception cref="KeyNotFoundException">Thrown when no column with that name exists.</exception>
    public object? this[string name]
    {
        get
        {
            var index = Schema.IndexOf(name);
            if (index < 0)
                throw new KeyNotFoundException($"No column named '{name}' in the record schema.");
            return _values[index];
        }
    }

    /// <summary>Tries to read a value by column name without throwing when the column is absent.</summary>
    /// <param name="name">Column name as declared in the schema.</param>
    /// <param name="value">The value when found; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a column with that name exists; otherwise <c>false</c>.</returns>
    public bool TryGet(string name, out object? value)
    {
        var index = Schema.IndexOf(name);
        if (index < 0)
        {
            value = null;
            return false;
        }

        value = _values[index];
        return true;
    }
}
