using System.Linq.Expressions;
using System.Reflection;
using NeoReports.Abstractions;

namespace NeoReports.Core.Building;

/// <summary>
/// Factory helpers for declaring report columns from typed member selectors.
/// Intended to be used with <c>using static NeoReports.Core.Building.ReportColumns;</c>.
/// </summary>
public static class ReportColumns
{
    /// <summary>
    /// Declares a column from a member selector. The column name is taken from the selected
    /// member, its <see cref="ColumnType"/> is inferred from the member type, and the selector
    /// is compiled into a value getter.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <typeparam name="TProp">The selected member type.</typeparam>
    /// <param name="selector">A member access expression, e.g. <c>v =&gt; v.Total</c>.</param>
    /// <param name="displayName">Optional header label; defaults to the member name.</param>
    /// <param name="format">Optional .NET format string for rendering.</param>
    /// <param name="culture">Optional culture name (e.g. "pt-BR") for rendering.</param>
    /// <returns>A typed column definition.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="selector"/> is not a member access.</exception>
    public static ColumnDefinition<T> Col<T, TProp>(
        Expression<Func<T, TProp>> selector,
        string? displayName = null,
        string? format = null,
        string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var member = GetMember(selector);
        var name = member.Name;
        var nullable = IsNullable(typeof(TProp), member);
        var type = InferColumnType(typeof(TProp));
        var getter = selector.Compile();

        var column = new ReportColumn(
            Name: name,
            Type: type,
            Nullable: nullable,
            DisplayName: displayName ?? name,
            Format: format,
            Culture: culture);

        return new ColumnDefinition<T>(column, row => getter(row));
    }

    /// <summary>
    /// Declares a column for the dynamic path that reads a <see cref="ReportRecord"/> by position.
    /// The schema (name and <see cref="ColumnType"/>) is supplied explicitly because a positional
    /// record has no compile-time member to infer from. The getter reads <c>record[index]</c>.
    /// </summary>
    /// <param name="index">Zero-based position of the value within the record.</param>
    /// <param name="name">Stable column key, unique within the schema.</param>
    /// <param name="type">Semantic column type used for formatting and projection.</param>
    /// <param name="nullable">Whether the column may contain null values.</param>
    /// <param name="displayName">Optional header label; defaults to <paramref name="name"/>.</param>
    /// <param name="format">Optional .NET format string for rendering.</param>
    /// <param name="culture">Optional culture name (e.g. "pt-BR") for rendering.</param>
    /// <returns>A column definition over <see cref="ReportRecord"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is negative.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or whitespace.</exception>
    public static ColumnDefinition<ReportRecord> Positional(
        int index,
        string name,
        ColumnType type,
        bool nullable = true,
        string? displayName = null,
        string? format = null,
        string? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Column name must be provided.", nameof(name));

        var column = new ReportColumn(
            Name: name,
            Type: type,
            Nullable: nullable,
            DisplayName: displayName ?? name,
            Format: format,
            Culture: culture);

        return new ColumnDefinition<ReportRecord>(column, record => record[index]);
    }

    private static MemberInfo GetMember<T, TProp>(Expression<Func<T, TProp>> selector)
    {
        var body = selector.Body;

        // Unwrap conversions inserted for value types boxed to object, etc.
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is MemberExpression { Member: { } m })
            return m;

        throw new ArgumentException(
            "Column selector must be a simple member access (e.g. v => v.Total).", nameof(selector));
    }

    private static bool IsNullable(Type type, MemberInfo member)
    {
        if (Nullable.GetUnderlyingType(type) is not null)
            return true;

        if (type.IsValueType)
            return false;

        // Reference type: assume nullable. (Nullable reference annotations are not reliably
        // available via reflection across target frameworks, so we default to nullable.)
        return true;
    }

    private static ColumnType InferColumnType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string)) return ColumnType.String;
        if (t == typeof(bool)) return ColumnType.Boolean;
        if (t == typeof(byte) || t == typeof(sbyte) ||
            t == typeof(short) || t == typeof(ushort) ||
            t == typeof(int) || t == typeof(uint) ||
            t == typeof(long) || t == typeof(ulong))
            return ColumnType.Integer;
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float))
            return ColumnType.Decimal;
        if (t == typeof(DateOnly)) return ColumnType.Date;
        if (t == typeof(TimeOnly) || t == typeof(TimeSpan)) return ColumnType.Time;
        if (t == typeof(DateTime)) return ColumnType.DateTime;
        if (t == typeof(DateTimeOffset)) return ColumnType.Timestamp;
        if (t == typeof(Guid)) return ColumnType.Uuid;
        if (t == typeof(byte[])) return ColumnType.Binary;

        return ColumnType.String;
    }
}
