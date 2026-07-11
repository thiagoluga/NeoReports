using System.Data.Common;
using System.Linq.Expressions;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;
using Oracle.ManagedDataAccess.Client;

namespace NeoReports.Sources.Oracle;

/// <summary>Fluent entry points for Oracle sources.</summary>
public static class Source
{
    /// <summary>
    /// Begins configuring an Oracle source. The query must expose a <c>:cursor</c> bind variable on
    /// the key column and order by it — Oracle uses a <c>:</c> prefix, not <c>@</c> (e.g.
    /// <c>... WHERE (:cursor IS NULL OR id &gt; :cursor) ORDER BY id</c>). Run-time report
    /// parameters are bound automatically when the query references them. Oracle rejects a handful
    /// of type-name keywords (notably <c>DATE</c>) as a bare column identifier — alias such columns
    /// in the SELECT list, e.g. <c>SELECT ..., SaleDate AS "Date" FROM ...</c>.
    /// </summary>
    /// <param name="connectionString">Oracle connection string.</param>
    /// <param name="sql">The keyset query.</param>
    public static OracleSourceBuilder Oracle(string connectionString, string sql) => new(connectionString, sql);

    /// <summary>
    /// Begins configuring an Oracle source that resolves its connection by name through the source
    /// registry (ADR D42/D43), instead of an inline connection string.
    /// </summary>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="sql">The keyset query.</param>
    public static OracleNamedSourceBuilder OracleNamed(string sourceName, string sql) => new(sourceName, sql);

    /// <summary>Oracle's ODP.NET binds parameters positionally by default; every provider call here opts into bind-by-name.</summary>
    internal static void ConfigureCommand(DbCommand command)
    {
        if (command is OracleCommand oracle)
            oracle.BindByName = true;
    }
}

/// <summary>Intermediate builder that captures the SQL before the keyset key/page size are chosen.</summary>
public sealed class OracleSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _sql;

    internal OracleSourceBuilder(string connectionString, string sql)
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

        return new AdoKeysetSource<T>(
            () => new OracleConnection(_connectionString), _sql, keyColumn, pageSize, schema,
            parameters: null, parameterPrefix: ":", configureCommand: Source.ConfigureCommand);
    }
}

/// <summary>Intermediate builder for a by-name Oracle source, before the keyset key/page size are chosen.</summary>
public sealed class OracleNamedSourceBuilder
{
    private readonly string _sourceName;
    private readonly string _sql;

    internal OracleNamedSourceBuilder(string sourceName, string sql)
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

        return new AdoNamedKeysetSource<T>(
            _sourceName, _sql, keyColumn, pageSize, schema, cs => new OracleConnection(cs),
            parameterPrefix: ":", configureCommand: Source.ConfigureCommand);
    }
}
