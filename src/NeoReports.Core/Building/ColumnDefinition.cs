using NeoReports.Abstractions;

namespace NeoReports.Core.Building;

/// <summary>
/// A column declaration bound to a typed accessor. Pairs the public <see cref="ReportColumn"/>
/// metadata with a compiled getter that extracts the value from a <typeparamref name="T"/> row.
/// The getter boxes value types, but it is only invoked at the writer edge.
/// </summary>
/// <typeparam name="T">The row type the column reads from.</typeparam>
public sealed class ColumnDefinition<T>
{
    /// <summary>Creates a column definition.</summary>
    /// <param name="column">The public column metadata.</param>
    /// <param name="getter">Compiled accessor that reads the column value from a row.</param>
    public ColumnDefinition(ReportColumn column, Func<T, object?> getter)
    {
        Column = column;
        Getter = getter;
    }

    /// <summary>The public column metadata (name, type, formatting hints).</summary>
    public ReportColumn Column { get; }

    /// <summary>Compiled accessor that reads the column value from a row.</summary>
    public Func<T, object?> Getter { get; }
}
