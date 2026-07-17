using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.QueryBuilder;

namespace NeoReports.Core.Preview;

/// <summary>
/// Runs a bounded, read-only sample of an ad-hoc query built by the visual query builder (ADR D49,
/// K6): one page, no output writing, no upload, no job record — the query-side sibling of
/// <see cref="ReportPreviewRunner"/> (which previews a <em>registered</em> report). The SQL is
/// produced by the trusted <c>IQuerySqlGenerator</c> and is injection-safe by construction (every
/// identifier quoted, every value bound); this runner never sees raw caller SQL. It binds that SQL
/// and its parameters through the source's own <c>IConfigSourceProvider</c> — the same keyset engine
/// a real run uses — so the sample matches what a report built from the query would read.
/// </summary>
public static class QueryPreviewRunner
{
    /// <summary>Maximum rows a single query preview may return, regardless of the caller's request.</summary>
    public const int MaxRows = 100;

    /// <summary>Runs one preview page of a generated query against a registered source.</summary>
    /// <param name="sourceType">The source's type id (e.g. "postgres") — selects the <c>IConfigSourceProvider</c>.</param>
    /// <param name="sourceProperties">The resolved source definition's properties (connection string, etc.); the query, key and page size are overlaid here.</param>
    /// <param name="query">The generated query — its keyset SQL (exposes <c>@cursor</c>, ends in <c>ORDER BY</c> the key), bind parameters, and output schema.</param>
    /// <param name="requestedRows">Caller-requested row cap; clamped to <see cref="MaxRows"/>.</param>
    /// <param name="execution">The execution context for this preview.</param>
    /// <param name="services">Service provider used to resolve the source's provider.</param>
    /// <param name="cancellationToken">Token that cancels the preview.</param>
    /// <exception cref="ConfigurationException">No <c>IConfigSourceProvider</c> is registered for <paramref name="sourceType"/>.</exception>
    public static async Task<QueryPreviewResult> PreviewAsync(
        string sourceType,
        IReadOnlyDictionary<string, object?>? sourceProperties,
        GeneratedReportSql query,
        int requestedRows,
        ReportExecutionContext execution,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.KeyColumnName);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        ReportSchema schema = query.Schema;
        int rows = Math.Clamp(requestedRows <= 0 ? MaxRows : requestedRows, 1, MaxRows);

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (sourceProperties is not null)
        {
            foreach (KeyValuePair<string, object?> kvp in sourceProperties)
                properties[kvp.Key] = kvp.Value;
        }

        properties["sql"] = query.Sql;
        properties["key"] = query.KeyColumnName;
        properties["pageSize"] = rows;
        var config = new SourceConfig(sourceType, properties);

        IConfigSourceProvider provider = ReportConfigCompiler.ResolveSource(services, sourceType);
        IBatchSource<ReportRecord> source = provider.Create(config, schema, services);

        var mergedParameters = new Dictionary<string, object?>(execution.Parameters, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> kvp in query.Parameters)
            mergedParameters[kvp.Key] = kvp.Value;
        var previewExecution = new ReportExecutionContext(
            execution.JobId, execution.ReportName, mergedParameters, execution.Logger, cancellationToken);

        BatchResult<ReportRecord> result = await source
            .ReadBatchAsync(new BatchContext(previewExecution, rows, null, 1), cancellationToken)
            .ConfigureAwait(false);

        // Mirror ReportPreviewRunner's page-full heuristic: a page that came back full is the honest
        // signal that more rows exist (the keyset source's exact HasMore isn't relied on here). The
        // row cap is enforced again on the way out — a single capped projection — so a provider that
        // over-serves can never exceed it.
        bool truncated = result.Records.Count >= rows;
        IReadOnlyList<object?[]> data = result.Records
            .Take(rows)
            .Select(r => r.Values.ToArray())
            .ToList();
        return new QueryPreviewResult(data, truncated);
    }
}

/// <summary>The result of one ad-hoc query preview (ADR D49, K6).</summary>
/// <param name="Rows">The sampled rows, each a positional <c>object?[]</c> aligned to the query's output schema.</param>
/// <param name="Truncated">Whether the sample filled the row cap — i.e. more rows likely exist.</param>
public sealed record QueryPreviewResult(IReadOnlyList<object?[]> Rows, bool Truncated);
