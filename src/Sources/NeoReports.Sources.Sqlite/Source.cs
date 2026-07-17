using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sqlite;

/// <summary>Fluent entry points for SQLite sources.</summary>
public static class Source
{
    /// <summary>
    /// Begins configuring a SQLite source. The query must expose a <c>@cursor</c> parameter on the
    /// key column and order by it (e.g.
    /// <c>... WHERE (@cursor IS NULL OR id &gt; @cursor) ORDER BY id</c>). Run-time report
    /// parameters are bound automatically when the query references them.
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g. <c>Data Source=./reports.db</c>).</param>
    /// <param name="sql">The keyset query.</param>
    public static SqliteSourceBuilder Sqlite(string connectionString, string sql) => new(connectionString, sql);

    /// <summary>
    /// Begins configuring a SQLite source that resolves its connection by name through the source
    /// registry (ADR D42/D43), instead of an inline connection string.
    /// </summary>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="sql">The keyset query.</param>
    public static SqliteNamedSourceBuilder SqliteNamed(string sourceName, string sql) => new(sourceName, sql);
}

/// <summary>Intermediate builder that captures the SQL before the keyset key/page size are chosen.</summary>
public sealed class SqliteSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _sql;

    internal SqliteSourceBuilder(string connectionString, string sql)
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
        var keyColumn = MemberSelector.GetMemberName(keySelector);
        var schema = new ReportSchema(new[] { new ReportColumn(keyColumn, ColumnType.String) });

        return new AdoKeysetSource<T>(() => new SqliteConnection(_connectionString), _sql, keyColumn, pageSize, schema);
    }
}

/// <summary>Intermediate builder for a by-name SQLite source, before the keyset key/page size are chosen.</summary>
public sealed class SqliteNamedSourceBuilder
{
    private readonly string _sourceName;
    private readonly string _sql;

    internal SqliteNamedSourceBuilder(string sourceName, string sql)
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
        var keyColumn = MemberSelector.GetMemberName(keySelector);
        var schema = new ReportSchema(new[] { new ReportColumn(keyColumn, ColumnType.String) });

        return new AdoNamedKeysetSource<T>(_sourceName, _sql, keyColumn, pageSize, schema, cs => new SqliteConnection(cs));
    }
}
