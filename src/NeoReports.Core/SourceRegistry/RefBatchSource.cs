using NeoReports.Abstractions;
using NeoReports.Core.Configuration;

namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// Wraps a ref-based dynamic-path source (ADR D42): re-resolves the registered definition through
/// <see cref="ISourceRegistry"/> and (re-)creates the real underlying <see cref="IBatchSource{ReportRecord}"/>
/// at the start of every run — never once at compile time — so a rotated connection string takes
/// effect on the very next run without recompiling anything. "Start of a run" is detected the same
/// way the rest of the pipeline does: <see cref="BatchContext.Cursor"/> is <c>null</c> only on a
/// report's first page (a documented <see cref="BatchContext"/> invariant), so a retry of that first
/// page simply (harmlessly) re-resolves rather than needing a separate "new run" signal.
/// </summary>
internal sealed class RefBatchSource : IBatchSource<ReportRecord>
{
    private readonly string _refName;
    private readonly string _declaredType;
    private readonly IReadOnlyDictionary<string, object?>? _overlayProperties;
    private readonly ISourceRegistry _registry;
    private readonly IServiceProvider _services;

    private IBatchSource<ReportRecord>? _resolved;

    public RefBatchSource(
        string refName,
        string declaredType,
        IReadOnlyDictionary<string, object?>? overlayProperties,
        ISourceRegistry registry,
        IServiceProvider services,
        ReportSchema schema)
    {
        _refName = refName;
        _declaredType = declaredType;
        _overlayProperties = overlayProperties;
        _registry = registry;
        _services = services;
        Schema = schema;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<ReportRecord>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        if (context.Cursor is null)
            _resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (_resolved is null)
        {
            throw new ConfigurationException(
                $"Source '{_refName}' was not resolved before its first page was read (internal pipeline error).");
        }

        return await _resolved.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IBatchSource<ReportRecord>> ResolveAsync(CancellationToken cancellationToken)
    {
        SourceDefinition? definition = await _registry.ResolveAsync(_refName, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            throw new ConfigurationException(
                $"Source '{_refName}' is no longer registered. It may have been deleted since this report was compiled.");
        }

        IReadOnlyDictionary<string, object?>? mergedProperties = MergeProperties(definition.Properties, _overlayProperties);
        IConfigSourceProvider provider = ReportConfigCompiler.ResolveSource(_services, _declaredType);
        var mergedConfig = new SourceConfig(_declaredType, mergedProperties);
        return provider.Create(mergedConfig, Schema, _services);
    }

    // Definition base, report-local overlay wins — the SQL query/key/page-size are per-report,
    // the connection string comes from the source (ADR D42).
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
}
