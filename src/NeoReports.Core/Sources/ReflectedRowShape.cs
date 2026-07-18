using System.Reflection;

namespace NeoReports.Core.Sources;

/// <summary>
/// Reflects a row type's shape once, ahead of any row reads (ADR D58): its longest public
/// constructor with parameters (records, positional types) — falling back to a parameterless
/// constructor with settable properties — the reflection setup every row materializer in this
/// codebase needs regardless of what kind of raw row each source actually reads from (the ADO
/// family's <c>DbDataReader</c>-based materializer, the CSV family's <c>string[]</c>-based one).
/// Only the discovery step is shared here; each materializer still owns its own "read and convert
/// one field" logic, which genuinely differs by source (a <c>DbDataReader</c> hands back typed
/// values with an <c>IsDBNull</c> check, a CSV row hands back raw text needing type conversion).
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
}
