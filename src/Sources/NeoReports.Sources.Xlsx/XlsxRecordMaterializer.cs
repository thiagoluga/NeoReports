using System.Globalization;
using NeoReports.Core.Sources;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Maps an XLSX row's cells to a <typeparamref name="T"/> instance by header name (ADR D59). The
/// reflection setup AND the constructor/property materialization loop are shared with the ADO and
/// CSV families via <see cref="ReflectedRowShape{T}.Materialize"/> — only the "read and convert one
/// field" step differs, since a cell already carries a native CLR value (<see cref="double"/>,
/// <see cref="string"/>, <see cref="bool"/>, <see cref="DateTime"/>, or <c>null</c>) rather than raw
/// text needing type conversion.
/// </summary>
/// <typeparam name="T">The row type to materialize.</typeparam>
internal sealed class XlsxRecordMaterializer<T>
{
    private readonly ReflectedRowShape<T> _shape = new();

    /// <summary>Materializes one row given the header's column-name-to-ordinal map.</summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive).</param>
    /// <param name="row">The row's raw cell values, aligned to the header.</param>
    public T Materialize(IReadOnlyDictionary<string, int> ordinalByName, object?[] row) =>
        _shape.Materialize(ordinalByName, row.Length, (ordinal, targetType) => ConvertValue(row[ordinal], targetType));

    // A cell already carries a native value, or null for an empty cell. When the cell's runtime type
    // already matches the target, it is used as-is (e.g. a date-styled cell -> a DateTime property);
    // otherwise Convert.ChangeType bridges the common cases (a numeric cell -> long/decimal, etc.).
    private static object? ConvertValue(object? raw, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (raw is null)
            return ReflectedRowShape<T>.DefaultFor(targetType);

        if (underlying.IsInstanceOfType(raw))
            return raw;

        if (underlying == typeof(string))
            return raw.ToString();

        return Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
    }
}
