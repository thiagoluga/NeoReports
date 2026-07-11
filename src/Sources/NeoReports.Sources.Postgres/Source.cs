using System.Linq.Expressions;
using Npgsql;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Postgres;

/// <summary>Fluent entry points for PostgreSQL sources.</summary>
public static class Source
{
    /// <summary>
    /// Begins configuring a PostgreSQL source. The query must expose a <c>@cursor</c> parameter on
    /// the key column and order by it (e.g.
    /// <c>... WHERE (@cursor IS NULL OR id &gt; @cursor::bigint) ORDER BY id</c>). Unlike SQL
    /// Server, Postgres does not implicitly convert the cursor parameter to the key column's type —
    /// an explicit <c>::type</c> cast on the comparison is required. Run-time report parameters are
    /// bound automatically when the query references them.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="sql">The keyset query.</param>
    public static PostgresSourceBuilder Postgres(string connectionString, string sql) => new(connectionString, sql);

    /// <summary>
    /// Begins configuring a PostgreSQL source that resolves its connection by name through the
    /// source registry (ADR D42/D43), instead of an inline connection string.
    /// </summary>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="sql">The keyset query.</param>
    public static PostgresNamedSourceBuilder PostgresNamed(string sourceName, string sql) => new(sourceName, sql);
}

/// <summary>Intermediate builder that captures the SQL before the keyset key/page size are chosen.</summary>
public sealed class PostgresSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _sql;

    internal PostgresSourceBuilder(string connectionString, string sql)
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

        return new AdoKeysetSource<T>(() => new NpgsqlConnection(_connectionString), _sql, keyColumn, pageSize, schema);
    }
}

/// <summary>Intermediate builder for a by-name PostgreSQL source, before the keyset key/page size are chosen.</summary>
public sealed class PostgresNamedSourceBuilder
{
    private readonly string _sourceName;
    private readonly string _sql;

    internal PostgresNamedSourceBuilder(string sourceName, string sql)
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

        return new AdoNamedKeysetSource<T>(_sourceName, _sql, keyColumn, pageSize, schema, cs => new NpgsqlConnection(cs));
    }
}
