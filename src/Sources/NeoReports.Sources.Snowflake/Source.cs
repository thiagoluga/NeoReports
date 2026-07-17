using System.Linq.Expressions;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Snowflake.Data.Client;

namespace NeoReports.Sources.Snowflake;

/// <summary>Fluent entry points for Snowflake sources.</summary>
public static class Source
{
    internal static readonly AdoProviderOptions ColonPrefix = new() { ParameterPrefix = ":" };

    /// <summary>
    /// Begins configuring a Snowflake source. The query must expose a <c>:cursor</c> bind variable
    /// on the key column and order by it — Snowflake uses a <c>:</c> prefix, not <c>@</c> (e.g.
    /// <c>... WHERE (:cursor IS NULL OR id &gt; :cursor) ORDER BY id</c>). Run-time report
    /// parameters are bound automatically when the query references them.
    /// </summary>
    /// <param name="connectionString">Snowflake connection string.</param>
    /// <param name="sql">The keyset query.</param>
    public static SnowflakeSourceBuilder Snowflake(string connectionString, string sql) => new(connectionString, sql);

    /// <summary>
    /// Begins configuring a Snowflake source that resolves its connection by name through the source
    /// registry (ADR D42/D43), instead of an inline connection string.
    /// </summary>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="sql">The keyset query.</param>
    public static SnowflakeNamedSourceBuilder SnowflakeNamed(string sourceName, string sql) => new(sourceName, sql);
}

/// <summary>Intermediate builder that captures the SQL before the keyset key/page size are chosen.</summary>
public sealed class SnowflakeSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _sql;

    internal SnowflakeSourceBuilder(string connectionString, string sql)
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
        AdoSourceBuilder.Keyset(() => new SnowflakeDbConnection(_connectionString), _sql, keySelector, pageSize, Source.ColonPrefix);
}

/// <summary>Intermediate builder for a by-name Snowflake source, before the keyset key/page size are chosen.</summary>
public sealed class SnowflakeNamedSourceBuilder
{
    private readonly string _sourceName;
    private readonly string _sql;

    internal SnowflakeNamedSourceBuilder(string sourceName, string sql)
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
        AdoSourceBuilder.NamedKeyset(_sourceName, cs => new SnowflakeDbConnection(cs), _sql, keySelector, pageSize, Source.ColonPrefix);
}
