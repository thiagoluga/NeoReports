using System.Data.Common;
using System.Reflection;

namespace NeoReports.Sources.Common;

/// <summary>
/// Maps a data reader's current row to a <typeparamref name="T"/> instance. Prefers the POCO's
/// longest constructor (records, positional types), matching parameters to result columns by name
/// (case-insensitive); falls back to a parameterless constructor with settable properties.
/// Compiled column ordinals are cached per reader shape on first use.
/// </summary>
/// <typeparam name="T">The row type to materialize.</typeparam>
public sealed class RecordMaterializer<T>
{
    private readonly ConstructorInfo? _ctor;
    private readonly ParameterInfo[] _ctorParams;
    private readonly PropertyInfo[] _settableProps;

    /// <summary>Reflects over <typeparamref name="T"/> once, ahead of any row reads.</summary>
    public RecordMaterializer()
    {
        var type = typeof(T);
        _ctor = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        _ctorParams = _ctor?.GetParameters() ?? Array.Empty<ParameterInfo>();
        _settableProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();
    }

    /// <summary>Materializes the reader's current row into a <typeparamref name="T"/> instance.</summary>
    /// <param name="reader">The reader, positioned on the row to materialize.</param>
    /// <param name="ordinalByName">Column ordinal by name (case-insensitive), for this reader's shape.</param>
    public T Materialize(DbDataReader reader, IReadOnlyDictionary<string, int> ordinalByName)
    {
        if (_ctor is not null)
        {
            var args = new object?[_ctorParams.Length];
            for (var i = 0; i < _ctorParams.Length; i++)
            {
                var name = _ctorParams[i].Name!;
                args[i] = ordinalByName.TryGetValue(name, out var ordinal)
                    ? ReadValue(reader, ordinal, _ctorParams[i].ParameterType)
                    : GetDefault(_ctorParams[i].ParameterType);
            }

            return (T)_ctor.Invoke(args);
        }

        var instance = Activator.CreateInstance<T>();
        foreach (var prop in _settableProps)
        {
            if (ordinalByName.TryGetValue(prop.Name, out var ordinal))
                prop.SetValue(instance, ReadValue(reader, ordinal, prop.PropertyType));
        }

        return instance;
    }

    private static object? ReadValue(DbDataReader reader, int ordinal, Type targetType)
    {
        if (reader.IsDBNull(ordinal))
            return GetDefault(targetType);

        var value = reader.GetValue(ordinal);
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying.IsInstanceOfType(value))
            return value;

        return Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? GetDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;
}
