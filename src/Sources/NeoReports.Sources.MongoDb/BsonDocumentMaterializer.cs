using System.Globalization;
using System.Reflection;
using MongoDB.Bson;

namespace NeoReports.Sources.MongoDb;

/// <summary>
/// Maps a BSON document to a <typeparamref name="T"/> instance by reading, for each of the POCO's
/// longest-constructor parameters (or settable properties, for a parameterless POCO), the document
/// field of the same name (case-insensitive). Deliberately does not use MongoDB.Driver's own
/// <c>BsonClassMap</c>-based deserialization, which silently rebinds a property literally named
/// <c>Id</c> to the document's <c>_id</c> field via its default naming convention — surprising when
/// a report's key field is legitimately named <c>Id</c> but stored under its own literal name, not
/// Mongo's internal <c>_id</c>.
/// </summary>
/// <typeparam name="T">The row type to materialize.</typeparam>
internal sealed class BsonDocumentMaterializer<T>
{
    private readonly ConstructorInfo? _ctor;
    private readonly ParameterInfo[] _ctorParams;
    private readonly PropertyInfo[] _settableProps;

    /// <summary>Reflects over <typeparamref name="T"/> once, ahead of any document reads.</summary>
    public BsonDocumentMaterializer()
    {
        Type type = typeof(T);
        _ctor = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        _ctorParams = _ctor?.GetParameters() ?? Array.Empty<ParameterInfo>();
        _settableProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();
    }

    /// <summary>Materializes a document into a <typeparamref name="T"/> instance.</summary>
    public T Materialize(BsonDocument document)
    {
        if (_ctor is not null)
        {
            var args = new object?[_ctorParams.Length];
            for (var i = 0; i < _ctorParams.Length; i++)
            {
                var name = _ctorParams[i].Name!;
                args[i] = document.TryGetValue(name, out BsonValue value) && value is not BsonNull
                    ? ReadValue(value, _ctorParams[i].ParameterType)
                    : GetDefault(_ctorParams[i].ParameterType);
            }

            return (T)_ctor.Invoke(args);
        }

        T instance = Activator.CreateInstance<T>();
        foreach (PropertyInfo prop in _settableProps)
        {
            if (document.TryGetValue(prop.Name, out BsonValue value) && value is not BsonNull)
                prop.SetValue(instance, ReadValue(value, prop.PropertyType));
        }

        return instance;
    }

    private static object? ReadValue(BsonValue value, Type targetType)
    {
        var clrValue = BsonTypeMapper.MapToDotNetValue(value);
        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (clrValue is null)
            return GetDefault(targetType);

        return underlying.IsInstanceOfType(clrValue)
            ? clrValue
            : Convert.ChangeType(clrValue, underlying, CultureInfo.InvariantCulture);
    }

    private static object? GetDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;
}
