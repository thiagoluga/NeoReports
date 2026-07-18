using System.Reflection;

namespace NeoReports.Core.Sources;

/// <summary>
/// Reflects a row type's shape once, ahead of any row reads (ADR D58): its longest public
/// constructor with parameters (records, positional types) — falling back to a parameterless
/// constructor with settable properties — the reflection setup every row materializer in this
/// codebase needs regardless of what kind of raw row each source actually reads from (the ADO
/// family's <c>DbDataReader</c>-based materializer, the CSV family's <c>string[]</c>-based one,
/// the XLSX family's <c>object?[]</c>-based one). <see cref="Materialize"/> also owns the shared
/// "build constructor args, or fall back to settable properties" loop (ADR D59) — every materializer
/// in this codebase had reimplemented that identical loop around its own per-field conversion, which
/// is now the only thing that genuinely differs by source (a <c>DbDataReader</c> hands back typed
/// values with an <c>IsDBNull</c> check, a CSV row hands back raw text needing type conversion, an
/// XLSX row hands back an already-native-typed cell value).
/// </summary>
/// <typeparam name="T">The row type to materialize.</typeparam>
public sealed class ReflectedRowShape<T>
{
    /// <summary>Reflects over <typeparamref name="T"/> once.</summary>
    public ReflectedRowShape()
    {
        var type = typeof(T);
        Constructor = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        ConstructorParameters = Constructor?.GetParameters() ?? Array.Empty<ParameterInfo>();
        SettableProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();
    }

    /// <summary>The longest public constructor with parameters, or <c>null</c> when <typeparamref name="T"/> has none (e.g. a plain POCO with a parameterless constructor).</summary>
    public ConstructorInfo? Constructor { get; }

    /// <summary><see cref="Constructor"/>'s parameters, in order; empty when <see cref="Constructor"/> is <c>null</c>.</summary>
    public ParameterInfo[] ConstructorParameters { get; }

    /// <summary>Public, writable instance properties — used when <see cref="Constructor"/> is <c>null</c>.</summary>
    public PropertyInfo[] SettableProperties { get; }

    /// <summary>The CLR default for <paramref name="type"/>: <c>0</c>/<c>false</c>/etc. for a non-nullable value type, otherwise <c>null</c>.</summary>
    public static object? DefaultFor(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;

    /// <summary>
    /// Builds one <typeparamref name="T"/> instance from a row: via <see cref="Constructor"/> (matching
    /// parameter names against <paramref name="ordinalByName"/>, falling back to <see cref="DefaultFor"/>
    /// for an unmatched parameter), or via <see cref="SettableProperties"/> when <typeparamref name="T"/>
    /// has no such constructor. <paramref name="convertOrdinal"/> reads and converts the row's raw value
    /// at a given ordinal to a given target type — the one step a source's own row shape still owns.
    /// </summary>
    /// <param name="ordinalByName">Header column ordinal by name (case-insensitive).</param>
    /// <param name="rowLength">The row's own field/cell count, for the same out-of-bounds guard every caller needs.</param>
    /// <param name="convertOrdinal">Reads the row's raw value at the given ordinal and converts it to the given target type.</param>
    public T Materialize(IReadOnlyDictionary<string, int> ordinalByName, int rowLength, Func<int, Type, object?> convertOrdinal)
    {
        if (Constructor is not null)
        {
            var args = new object?[ConstructorParameters.Length];
            for (var i = 0; i < ConstructorParameters.Length; i++)
            {
                var name = ConstructorParameters[i].Name!;
                args[i] = ordinalByName.TryGetValue(name, out var ordinal) && ordinal < rowLength
                    ? convertOrdinal(ordinal, ConstructorParameters[i].ParameterType)
                    : DefaultFor(ConstructorParameters[i].ParameterType);
            }

            return (T)Constructor.Invoke(args);
        }

        var instance = Activator.CreateInstance<T>();
        foreach (var prop in SettableProperties)
        {
            if (ordinalByName.TryGetValue(prop.Name, out var ordinal) && ordinal < rowLength)
                prop.SetValue(instance, convertOrdinal(ordinal, prop.PropertyType));
        }

        return instance;
    }
}
