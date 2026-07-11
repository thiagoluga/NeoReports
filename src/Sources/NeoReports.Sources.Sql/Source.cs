using System.Linq.Expressions;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Sql;

/// <summary>Fluent entry points for sources. SQL Server (keyset) lives here.</summary>
public static class Source
{
    /// <summary>
    /// Begins configuring a SQL Server source. The query must expose a <c>@cursor</c> parameter on
    /// the key column and order by it (e.g.
    /// <c>... WHERE (@cursor IS NULL OR Id &gt; @cursor) ORDER BY Id</c>). Run-time report parameters
    /// are bound automatically when the query references them.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="sql">The keyset query.</param>
    public static SqlSourceBuilder Sql(string connectionString, string sql) => new(connectionString, sql);

    /// <summary>
    /// Begins configuring a SQL Server source that resolves its connection by name through the
    /// source registry (ADR D42), instead of an inline connection string. The registry is queried
    /// fresh at the start of every run — rotating the registered source's connection string, or
    /// swapping which database it points at, takes effect on the very next run without
    /// recompiling anything. Requires a source registry configured on the host
    /// (<c>AddSourceRegistry()</c>/<c>AddInMemorySourceRegistry()</c>) — registering a report built
    /// from this source without one throws <see cref="ConfigurationException"/> immediately.
    /// </summary>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="sql">The keyset query.</param>
    public static SqlNamedSourceBuilder SqlNamed(string sourceName, string sql) => new(sourceName, sql);
}

/// <summary>Intermediate builder for a by-name SQL source (<see cref="Source.SqlNamed"/>), before the keyset key/page size are chosen.</summary>
public sealed class SqlNamedSourceBuilder
{
    private readonly string _sourceName;
    private readonly string _sql;

    internal SqlNamedSourceBuilder(string sourceName, string sql)
    {
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>
    /// Completes the source with a keyset key selector and page size. The key selector names the
    /// column used for keyset pagination and to compute the next cursor.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    /// <typeparam name="TKey">The key column type.</typeparam>
    /// <param name="keySelector">Member selector for the key column, e.g. <c>v =&gt; v.Id</c>.</param>
    /// <param name="pageSize">Maximum rows per page. Default 1000.</param>
    public IBatchSource<T> Keyset<T, TKey>(Expression<Func<T, TKey>> keySelector, int pageSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var keyColumn = SqlSourceBuilder.GetMemberName(keySelector);

        var schema = new ReportSchema(new[] { new ReportColumn(keyColumn, ColumnType.String) });

        return new NamedSqlKeysetSource<T>(_sourceName, _sql, keyColumn, pageSize, schema);
    }
}

/// <summary>Intermediate builder that captures the SQL before the keyset key/page size are chosen.</summary>
public sealed class SqlSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _sql;

    internal SqlSourceBuilder(string connectionString, string sql)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>
    /// Completes the source with a keyset key selector and page size. The key selector names the
    /// column used for keyset pagination and to compute the next cursor.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    /// <typeparam name="TKey">The key column type.</typeparam>
    /// <param name="keySelector">Member selector for the key column, e.g. <c>v =&gt; v.Id</c>.</param>
    /// <param name="pageSize">Maximum rows per page. Default 1000.</param>
    public IBatchSource<T> Keyset<T, TKey>(Expression<Func<T, TKey>> keySelector, int pageSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var keyColumn = GetMemberName(keySelector);

        // The source's declared schema is not consumed by the pipeline (projection uses the
        // builder's columns), so a minimal placeholder is sufficient here.
        var schema = new ReportSchema(new[] { new ReportColumn(keyColumn, ColumnType.String) });

        return new SqlKeysetSource<T>(_connectionString, _sql, keyColumn, pageSize, schema);
    }

    internal static string GetMemberName<T, TKey>(Expression<Func<T, TKey>> selector)
    {
        var body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is MemberExpression { Member.Name: { } name })
            return name;

        throw new ArgumentException(
            "Keyset selector must be a simple member access (e.g. v => v.Id).", nameof(selector));
    }
}
