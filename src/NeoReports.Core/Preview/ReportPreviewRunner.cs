using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Core.Preview;

/// <summary>
/// Runs a bounded, read-only sample of a registered report: one page, no output writing, no upload,
/// no job record (ADR D45). Unfiltered previews reuse the exact reader machinery a real run uses
/// (<see cref="CompiledReport.ReaderFactory"/>), so the sample matches what the report would actually
/// write. Filtered previews are SQL-family-only (dynamic, config-registered reports whose source type
/// has a registered <see cref="IFilterTranslator"/>) and read directly from a source built for the
/// filtered query — typed (code-first) reports have no structured source representation to filter.
/// </summary>
public static class ReportPreviewRunner
{
    /// <summary>Maximum rows a single preview page may return, regardless of the caller's request.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Runs one preview page.</summary>
    /// <param name="report">The compiled report to preview.</param>
    /// <param name="filters">Structured filters to apply; empty for an unfiltered sample.</param>
    /// <param name="requestedPageSize">Caller-requested page size; capped at <see cref="MaxPageSize"/>.</param>
    /// <param name="execution">The execution context for this preview.</param>
    /// <param name="services">Service provider used to resolve the report's source and, for filtered previews, its translator.</param>
    /// <param name="cancellationToken">Token that cancels the preview.</param>
    /// <exception cref="ConfigurationException">
    /// Thrown when <paramref name="filters"/> is non-empty but the report is typed (code-first) or
    /// no stored configuration can be found for it — filters require a structured, dynamic source.
    /// </exception>
    public static async Task<PreviewResult> PreviewAsync(
        CompiledReport report,
        IReadOnlyList<PreviewFilter> filters,
        int requestedPageSize,
        ReportExecutionContext execution,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        var pageSize = Math.Clamp(requestedPageSize <= 0 ? MaxPageSize : requestedPageSize, 1, MaxPageSize);

        if (filters.Count == 0)
            return await PreviewUnfilteredAsync(report, pageSize, execution, services, cancellationToken).ConfigureAwait(false);

        IReportConfigStore? configStore = services.GetService<IReportConfigStore>();
        bool isDynamic = configStore is not null
            && await configStore.ExistsAsync(report.Name, cancellationToken).ConfigureAwait(false);

        if (!isDynamic)
        {
            throw new ConfigurationException(
                $"Report '{report.Name}' is typed (code-first) — it has no structured source representation to filter. " +
                "Only dynamic (config-registered) reports support preview filters.");
        }

        ValidateFilterColumns(report, filters);

        return await PreviewFilteredAsync(report, filters, pageSize, execution, configStore!, services, cancellationToken)
            .ConfigureAwait(false);
    }

    // Every filter column is interpolated directly into SQL text by IFilterTranslator implementations
    // (there is no safe way to parametrize an identifier) — restricting it to the report's own
    // already-declared, trusted schema columns is what keeps that safe. Without this check, an
    // attacker-supplied Column value would flow straight into the query.
    private static void ValidateFilterColumns(CompiledReport report, IReadOnlyList<PreviewFilter> filters)
    {
        PreviewFilter? invalid = filters.FirstOrDefault(f => report.Schema.IndexOf(f.Column) < 0);
        if (invalid is not null)
            throw new ConfigurationException($"'{invalid.Column}' is not a declared column of report '{report.Name}'.");
    }

    private static async Task<PreviewResult> PreviewUnfilteredAsync(
        CompiledReport report, int pageSize, ReportExecutionContext execution, IServiceProvider services, CancellationToken cancellationToken)
    {
        await using IProjectedBatchReader reader = report.ReaderFactory(execution, services);
        ProjectedBatch batch = await reader.ReadAsync(1, null, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<object?[]> rows = batch.Outputs.Count > 0 ? batch.Outputs[0] : Array.Empty<object?[]>();
        ReportSchema schema = report.OutputSchemas.Count > 0 ? report.OutputSchemas[0] : report.Schema;

        // The reader always reads the report's own configured PageSize, which may exceed the
        // preview's (capped) requested size — truncating here means HasMore must also account for
        // rows dropped by truncation, not just whether the underlying batch itself had more.
        bool hasMore = batch.HasMore || rows.Count > pageSize;
        return new PreviewResult(Cap(rows, pageSize), schema, FiltersApplied: false, hasMore);
    }

    private static async Task<PreviewResult> PreviewFilteredAsync(
        CompiledReport report,
        IReadOnlyList<PreviewFilter> filters,
        int pageSize,
        ReportExecutionContext execution,
        IReportConfigStore configStore,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, string Document)> documents = await configStore.ListAsync(cancellationToken).ConfigureAwait(false);
        (string Name, string Document) stored = documents.FirstOrDefault(d => string.Equals(d.Name, report.Name, StringComparison.OrdinalIgnoreCase));
        if (stored.Document is null)
            throw new ConfigurationException($"No stored configuration found for report '{report.Name}'.");

        ReportConfig config = ReportConfigEnvironment.Substitute(new JsonReportConfigParser().Parse(stored.Document));
        SourceConfig sourceConfig = config.Source;

        (string effectiveType, IReadOnlyDictionary<string, object?>? effectiveProperties) = sourceConfig.Ref is null
            ? (sourceConfig.Type!, sourceConfig.Properties)
            : await ResolveRefPropertiesAsync(sourceConfig, services, cancellationToken).ConfigureAwait(false);

        IFilterTranslator? translator = services.GetServices<IFilterTranslator>()
            .FirstOrDefault(t => string.Equals(t.Type, effectiveType, StringComparison.OrdinalIgnoreCase));

        if (translator is null)
        {
            // Ignored honestly, not silently dropped and not a 400 — the source type just has no
            // translator (e.g. MongoDB, D44); the sample still runs, unfiltered.
            PreviewResult unfiltered = await PreviewUnfilteredAsync(report, pageSize, execution, services, cancellationToken).ConfigureAwait(false);
            return unfiltered with { FiltersApplied = false };
        }

        if (effectiveProperties is null || !effectiveProperties.TryGetValue("sql", out var sqlValue) || sqlValue is not string sql)
            throw new ConfigurationException($"Source for report '{report.Name}' has no 'sql' property to filter.");

        if (!translator.TryTranslate(sql, filters, report.Schema, out string translatedSql, out IReadOnlyDictionary<string, object?> filterParameters))
            throw new ConfigurationException($"Filters could not be translated for source type '{effectiveType}'.");

        // AdoKeysetSource reads its own baked-in pageSize property, not BatchContext.PageSize, so the
        // capped preview size must be overridden here too, not just passed to ReadBatchAsync below.
        var filteredProperties = new Dictionary<string, object?>(effectiveProperties, StringComparer.Ordinal)
        {
            ["sql"] = translatedSql,
            ["pageSize"] = pageSize,
        };
        var filteredConfig = new SourceConfig(effectiveType, filteredProperties);

        IConfigSourceProvider provider = ReportConfigCompiler.ResolveSource(services, effectiveType);
        IBatchSource<ReportRecord> filteredSource = provider.Create(filteredConfig, report.Schema, services);

        var mergedParameters = new Dictionary<string, object?>(execution.Parameters, StringComparer.Ordinal);
        foreach ((string key, object? value) in filterParameters)
            mergedParameters[key] = value;
        var filteredExecution = new ReportExecutionContext(execution.JobId, execution.ReportName, mergedParameters, execution.Logger, cancellationToken);

        BatchResult<ReportRecord> result = await filteredSource
            .ReadBatchAsync(new BatchContext(filteredExecution, pageSize, null, 1), cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<object?[]> rows = result.Records.Select(r => r.Values.ToArray()).ToList();
        return new PreviewResult(rows, report.Schema, FiltersApplied: true, result.HasMore);
    }

    // Definition base, report-local overlay wins — mirrors RefBatchSource's merge (ADR D42): the
    // connection string comes from the registered source, the query/key/page-size are per-report.
    private static async Task<(string Type, IReadOnlyDictionary<string, object?>? Properties)> ResolveRefPropertiesAsync(
        SourceConfig sourceConfig, IServiceProvider services, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = services.GetService<ISourceRegistry>();
        SourceDefinition? definition = registry is null
            ? null
            : await registry.ResolveAsync(sourceConfig.Ref!, cancellationToken).ConfigureAwait(false);

        if (definition is null)
            throw new ConfigurationException($"Source '{sourceConfig.Ref}' is not registered.");

        string effectiveType = sourceConfig.Type ?? definition.Type;
        IReadOnlyDictionary<string, object?>? merged = MergeProperties(definition.Properties, sourceConfig.Properties);
        return (effectiveType, merged);
    }

    private static IReadOnlyDictionary<string, object?>? MergeProperties(
        IReadOnlyDictionary<string, object?>? baseProperties, IReadOnlyDictionary<string, object?>? overlay)
    {
        if (overlay is null || overlay.Count == 0)
            return baseProperties;
        if (baseProperties is null || baseProperties.Count == 0)
            return overlay;

        var merged = new Dictionary<string, object?>(baseProperties, StringComparer.Ordinal);
        foreach ((string key, object? value) in overlay)
            merged[key] = value;

        return merged;
    }

    private static IReadOnlyList<object?[]> Cap(IReadOnlyList<object?[]> rows, int pageSize) =>
        rows.Count <= pageSize ? rows : rows.Take(pageSize).ToList();
}
