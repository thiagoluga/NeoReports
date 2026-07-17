using System.Data.Common;
using System.Linq.Expressions;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Common;

/// <summary>
/// Shared body for a provider's typed <c>Keyset&lt;T,TKey&gt;</c> fluent builder step (ADR D57):
/// derives the key column name and single-column schema from the key selector, then constructs the
/// keyset source. Extracted once two providers (Redshift, Snowflake) produced byte-identical builder
/// bodies against the shared <see cref="AdoKeysetSource{T}"/>/<see cref="AdoNamedKeysetSource{T}"/>
/// engine — SonarCloud's new-code duplication gate caught it. Earlier providers (Postgres, MySQL,
/// Oracle, SQL Server, SQLite) predate this extraction and are left on their own, functionally
/// identical, hand-written builders — the same "extracted once duplication was introduced, older
/// siblings left alone" precedent D43 already set for <c>SqlKeysetSource</c>.
/// </summary>
public static class AdoSourceBuilder
{
    /// <summary>Builds an inline (fixed-connection-string) keyset source from a key selector.</summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    /// <typeparam name="TKey">The key column type.</typeparam>
    /// <param name="connectionFactory">Creates a new, unopened connection for each page.</param>
    /// <param name="sql">Query with a cursor parameter and an ORDER BY on the key column.</param>
    /// <param name="keySelector">Member selector for the key column, e.g. <c>v =&gt; v.Id</c>.</param>
    /// <param name="pageSize">Maximum rows per page.</param>
    /// <param name="options">Provider-specific extension knobs (parameter prefix, command hooks, …).</param>
    public static IBatchSource<T> Keyset<T, TKey>(
        Func<DbConnection> connectionFactory, string sql, Expression<Func<T, TKey>> keySelector,
        int pageSize, AdoProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var keyColumn = MemberSelector.GetMemberName(keySelector);
        var schema = new ReportSchema(new[] { new ReportColumn(keyColumn, ColumnType.String) });
        return new AdoKeysetSource<T>(connectionFactory, sql, keyColumn, pageSize, schema, options);
    }

    /// <summary>Builds a by-name (source-registry-resolved) keyset source from a key selector.</summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    /// <typeparam name="TKey">The key column type.</typeparam>
    /// <param name="sourceName">Name of a source registered via <c>ISourceRegistry</c>.</param>
    /// <param name="connectionFactory">Given the resolved connection string, creates a new, unopened connection.</param>
    /// <param name="sql">Query with a cursor parameter and an ORDER BY on the key column.</param>
    /// <param name="keySelector">Member selector for the key column, e.g. <c>v =&gt; v.Id</c>.</param>
    /// <param name="pageSize">Maximum rows per page.</param>
    /// <param name="options">Provider-specific extension knobs (parameter prefix, command hooks, …).</param>
    public static IBatchSource<T> NamedKeyset<T, TKey>(
        string sourceName, Func<string, DbConnection> connectionFactory, string sql,
        Expression<Func<T, TKey>> keySelector, int pageSize, AdoProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var keyColumn = MemberSelector.GetMemberName(keySelector);
        var schema = new ReportSchema(new[] { new ReportColumn(keyColumn, ColumnType.String) });
        return new AdoNamedKeysetSource<T>(sourceName, sql, keyColumn, pageSize, schema, connectionFactory, options);
    }
}
