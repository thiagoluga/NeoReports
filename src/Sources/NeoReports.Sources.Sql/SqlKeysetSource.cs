using System.Data.Common;
using Microsoft.Data.SqlClient;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sql;

/// <summary>
/// SQL Server's derived-table suffix for counting rows (ADR D47). Every keyset query ends in
/// <c>ORDER BY</c>, which SQL Server rejects inside a bare derived table unless followed by
/// <c>TOP</c>/<c>OFFSET</c>/<c>FETCH</c> — the same fix <c>AdoFilterTranslator</c> already
/// established for filtered previews (D45/G7). Shared by <see cref="SqlKeysetSource{T}"/> and
/// <see cref="SqlNamedSourceBuilder"/> so both SQL Server paths count correctly.
/// </summary>
internal static class SqlServerCount
{
    public const string InnerSuffix = " OFFSET 0 ROWS";
}

/// <summary>
/// SQL Server batch source using keyset pagination. The query must expose a <c>@cursor</c>
/// parameter for the key column and order by that key; each page reads up to <c>pageSize</c> rows
/// where the key is greater than the previous page's last key. A fresh connection is opened and
/// closed per page (D2/D3): the cursor is the opaque, serializable last-key value (<c>string?</c>).
/// A thin, API-compatible wrapper (D43) over the shared <see cref="AdoKeysetSource{T}"/> engine —
/// its own constructor signatures and public members are unchanged from before the extraction,
/// since this type has been published (v1.2.0) and consumers construct it directly.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
public sealed class SqlKeysetSource<T> : IBatchSource<T>, ISourceRowCounter
{
    private readonly AdoKeysetSource<T> _inner;

    /// <summary>Creates the source.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="sql">Query with a <c>@cursor</c> parameter and an ORDER BY on the key column.</param>
    /// <param name="keyColumn">Name of the keyset column in the result set.</param>
    /// <param name="pageSize">Maximum rows per page.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="parameters">Static parameters bound on every page (besides <c>@cursor</c>).</param>
    public SqlKeysetSource(
        string connectionString,
        string sql,
        string keyColumn,
        int pageSize,
        ReportSchema schema,
        IReadOnlyDictionary<string, object?>? parameters = null)
        : this(connectionString, sql, keyColumn, pageSize, schema, parameters, materialize: null)
    {
    }

    /// <summary>
    /// Creates the source with a custom row materializer. Used by the dynamic path to materialize a
    /// positional <c>ReportRecord</c> by schema name; when <paramref name="materialize"/> is null the
    /// reflection-based <see cref="RecordMaterializer{T}"/> (typed POCO) is used.
    /// </summary>
    internal SqlKeysetSource(
        string connectionString,
        string sql,
        string keyColumn,
        int pageSize,
        ReportSchema schema,
        IReadOnlyDictionary<string, object?>? parameters,
        Func<DbDataReader, IReadOnlyDictionary<string, int>, T>? materialize)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        _inner = new AdoKeysetSource<T>(
            () => new SqlConnection(connectionString), sql, keyColumn, pageSize, schema, materialize,
            new AdoProviderOptions { Parameters = parameters, CountInnerSuffix = SqlServerCount.InnerSuffix });
    }

    /// <inheritdoc />
    public ReportSchema Schema => _inner.Schema;

    /// <inheritdoc />
    public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        _inner.ReadBatchAsync(context, cancellationToken);

    /// <inheritdoc />
    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _inner.CountAsync(execution, cancellationToken);
}
