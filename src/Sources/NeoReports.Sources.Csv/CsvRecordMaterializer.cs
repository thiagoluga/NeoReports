using System.Globalization;
using NeoReports.Core.Sources;

namespace NeoReports.Sources.Csv;

/// <summary>
/// Maps a CSV row's fields to a <typeparamref name="T"/> instance by header name (ADR D58). The
/// reflection setup (find the longest constructor, or fall back to settable properties) is shared
/// with the ADO family's own <c>NeoReports.Sources.Common.RecordMaterializer&lt;T&gt;</c> via
/// <see cref="ReflectedRowShape{T}"/> — only the "read and convert one field" step differs, since a
/// <c>DbDataReader</c> hands back typed values with an <c>IsDBNull</c> check while a CSV row hands
/// back raw text needing type conversion.
/// </summary>
/// <typeparam name="T">The row type to materialize.</typeparam>
internal sealed class CsvRecordMaterializer<T>
{
    private readonly ReflectedRowShape<T> _shape = new();

    /// <summary>Materializes one row given the header's column-name-to-ordinal map.</summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive).</param>
    /// <param name="row">The row's raw string fields, aligned to the header.</param>
    public T Materialize(IReadOnlyDictionary<string, int> ordinalByName, string[] row)
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

    // A CSV field has no null representation — an empty field is a genuine empty string for a
    // string-typed target, never coerced to null. Only for a non-string target does an empty/
    // unparseable-as-absent field fall back to that type's default (matching the DBNull convention
    // RecordMaterializer<T> uses for the ADO family, but derived from emptiness, not a real null marker).
    private static object? ConvertValue(string raw, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying == typeof(string))
            return raw;

        if (string.IsNullOrEmpty(raw))
            return ReflectedRowShape<T>.DefaultFor(targetType);

        return Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
    }
}
