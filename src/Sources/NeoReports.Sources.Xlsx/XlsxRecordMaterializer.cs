using System.Globalization;
using NeoReports.Core.Sources;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Maps an XLSX row's cells to a <typeparamref name="T"/> instance by header name (ADR D59). The
/// reflection setup (find the longest constructor, or fall back to settable properties) is shared
/// with the ADO and CSV families via <see cref="ReflectedRowShape{T}"/> — only the "read and convert
/// one field" step differs, since a cell already carries a native CLR value (<see cref="double"/>,
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
    public T Materialize(IReadOnlyDictionary<string, int> ordinalByName, object?[] row)
    {
        if (_shape.Constructor is not null)
        {
            var args = new object?[_shape.ConstructorParameters.Length];
            for (var i = 0; i < _shape.ConstructorParameters.Length; i++)
            {
                var name = _shape.ConstructorParameters[i].Name!;
                args[i] = ordinalByName.TryGetValue(name, out var ordinal) && ordinal < row.Length
                    ? ConvertValue(row[ordinal], _shape.ConstructorParameters[i].ParameterType)
                    : ReflectedRowShape<T>.DefaultFor(_shape.ConstructorParameters[i].ParameterType);
            }

            return (T)_shape.Constructor.Invoke(args);
        }

        var instance = Activator.CreateInstance<T>();
        foreach (var prop in _shape.SettableProperties)
        {
            if (ordinalByName.TryGetValue(prop.Name, out var ordinal) && ordinal < row.Length)
                prop.SetValue(instance, ConvertValue(row[ordinal], prop.PropertyType));
        }

        return instance;
    }

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
