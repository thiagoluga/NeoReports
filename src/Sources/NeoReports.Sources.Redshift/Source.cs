using System.Linq.Expressions;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Redshift;

/// <summary>Fluent entry points for Amazon Redshift sources.</summary>
public static class Source
{
    /// <summary>
    /// Begins configuring an Amazon Redshift source. The query must expose a <c>@cursor</c>
    /// parameter on the key column and order by it (e.g.
    /// <c>... WHERE (@cursor IS NULL OR id &gt; @cursor::bigint) ORDER BY id</c>). Redshift, like
    /// the PostgreSQL it's derived from, does not implicitly convert the cursor parameter to the key
    /// column's type — an explicit <c>::type</c> cast on the comparison is required. Run-time report
    /// parameters are bound automatically when the query references them.
    /// </summary>
    /// <param name="connectionString">Redshift connection string (Npgsql wire protocol).</param>
    /// <param name="sql">The keyset query.</param>
    public static RedshiftSourceBuilder Redshift(string connectionString, string sql) => new(connectionString, sql);

    /// <summary>
    /// Begins configuring an Amazon Redshift source that resolves its connection by name through the
    /// source registry (ADR D42/D43), instead of an inline connection string.
    /// </summary>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="sql">The keyset query.</param>
    public static RedshiftNamedSourceBuilder RedshiftNamed(string sourceName, string sql) => new(sourceName, sql);
}

/// <summary>Intermediate builder that captures the SQL before the keyset key/page size are chosen.</summary>
public sealed class RedshiftSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _sql;

    internal RedshiftSourceBuilder(string connectionString, string sql)
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
    public IBatchSource<T> Keyset<T, TKey>(Expression<Func<T, TKey>> keySelector, int pageSize = 1000) =>
        AdoSourceBuilder.Keyset(() => new NpgsqlConnection(_connectionString), _sql, keySelector, pageSize);
}

/// <summary>Intermediate builder for a by-name Amazon Redshift source, before the keyset key/page size are chosen.</summary>
public sealed class RedshiftNamedSourceBuilder
{
    private readonly string _sourceName;
    private readonly string _sql;

    internal RedshiftNamedSourceBuilder(string sourceName, string sql)
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
    public IBatchSource<T> Keyset<T, TKey>(Expression<Func<T, TKey>> keySelector, int pageSize = 1000) =>
        AdoSourceBuilder.NamedKeyset(_sourceName, cs => new NpgsqlConnection(cs), _sql, keySelector, pageSize);
}
