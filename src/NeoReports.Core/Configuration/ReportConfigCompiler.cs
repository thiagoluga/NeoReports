using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Scheduling;
using NeoReports.Core.Sections;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Compiles a <see cref="ReportConfig"/> into a runnable <see cref="CompiledReport"/> over the
/// positional <see cref="ReportRecord"/> row. The source/format/destination of a config section are
/// resolved from DI by their stable ids: an <see cref="IConfigSourceProvider"/> for the source, an
/// <see cref="IWriterFactory"/> per output format, and an <see cref="IDestinationFactory"/> per
/// destination type. The rest is the same fluent build the typed path uses — no parallel pipeline.
/// </summary>
public static class ReportConfigCompiler
{
    /// <summary>Compiles a parsed configuration into a runnable report.</summary>
    /// <param name="config">The parsed report configuration.</param>
    /// <param name="services">Service provider that holds the registered providers/factories.</param>
    /// <returns>An immutable compiled report ready to run or register.</returns>
    /// <exception cref="ConfigurationException">Thrown when the config is invalid or a referenced provider/factory is not registered.</exception>
    public static CompiledReport Compile(ReportConfig config, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(services);
        Validate(config);

        var columns = new ColumnDefinition<ReportRecord>[config.Columns.Count];
        for (var i = 0; i < config.Columns.Count; i++)
        {
            ColumnConfig c = config.Columns[i];
            columns[i] = ReportColumns.Positional(i, c.Name, c.Type, c.Nullable, c.DisplayName, c.Format, c.Culture);
        }

        var schema = new ReportSchema(columns.Select(c => c.Column).ToList());

        var columnsByName = new Dictionary<string, ColumnDefinition<ReportRecord>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.Columns.Count; i++)
            columnsByName[config.Columns[i].Name] = columns[i];

        var regularOutputs = new List<OutputSpec>();
        var sectionedOutputs = new List<(SectionedOutputSpec Spec, IReadOnlyList<SectionConfig> Sections)>();
        foreach (OutputConfig output in config.Outputs)
        {
            if (output.Sections is { Count: > 0 })
                sectionedOutputs.Add((new SectionedOutputSpec(ResolveSectionedWriter(services, output.Format), output.Properties), output.Sections));
            else
                regularOutputs.Add(new OutputSpec(ResolveWriter(services, output.Format), output.Properties));
        }

        DestinationSpec[] destinations = config.Destinations?
            .Select(d => new DestinationSpec(ResolveDestination(services, d.Type), d.Properties))
            .ToArray() ?? Array.Empty<DestinationSpec>();

        // Resolve every registration up front (fail fast on a missing provider/factory) before
        // instantiating the source, which may open connections. A ref-based source (ADR D42) is
        // deliberately NOT instantiated here — RefBatchSource resolves and creates the real
        // underlying source itself, fresh on every run (never baked into the compiled report).
        IBatchSource<ReportRecord> source = config.Source.Ref is not null
            ? ResolveRefSource(services, config.Source, schema)
            : ResolveSource(services, config.Source.Type!).Create(config.Source, schema, services);

        ReportBuilder<ReportRecord> builder = new ReportBuilder<ReportRecord>(config.Name)
            .From(source)
            .Columns(columns)
            .WithSourceRef(config.Source.Ref);

        if (config.PageSize is int pageSize)
            builder.WithPageSize(pageSize);

        if (config.Filter is not null)
            builder.Filter(JsonLogicFilter.Compile(config.Filter));

        if (config.Resilience is { } resilience)
            ApplyResilience(builder, resilience);

        if (config.Schedule is { } schedule)
            builder.Schedule(schedule.Cron);

        if (config.TrackProgress is { } trackProgress)
            builder.TrackProgress(trackProgress);

        foreach (OutputSpec output in regularOutputs)
            builder.To(output);
        foreach ((SectionedOutputSpec spec, IReadOnlyList<SectionConfig> sections) in sectionedOutputs)
            builder.ToSections(spec, s => BuildSections(s, sections, columnsByName));
        foreach (DestinationSpec destination in destinations)
            builder.UploadTo(destination);

        return builder.Build();
    }

    private static void ApplyResilience(ReportBuilder<ReportRecord> builder, ResilienceConfig resilience)
    {
        bool hasBackoff = resilience.Backoff is not null || resilience.BaseDelaySeconds is not null;
        if (resilience.MaxAttempts is not null || hasBackoff || resilience.Jitter is not null)
        {
            builder.Retry(r =>
            {
                if (resilience.MaxAttempts is int attempts)
                    r.MaxAttempts(attempts);

                if (hasBackoff)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(resilience.BaseDelaySeconds ?? 1);
                    if (resilience.Backoff is null || string.Equals(resilience.Backoff, "Constant", StringComparison.OrdinalIgnoreCase))
                        r.Constant(delay);
                    else if (string.Equals(resilience.Backoff, "Exponential", StringComparison.OrdinalIgnoreCase))
                        r.Exponential(delay);
                    else
                        throw new ConfigurationException($"Unknown resilience.backoff value '{resilience.Backoff}'. Use 'Constant' or 'Exponential'.");
                }

                if (resilience.Jitter is true)
                    r.WithJitter();
            });
        }

        bool isSkipAndLog = string.Equals(resilience.OnFailure, "skip-and-log", StringComparison.OrdinalIgnoreCase);
        if (resilience.AbortWhen is not null && !isSkipAndLog)
        {
            throw new ConfigurationException(
                "resilience.abortWhen requires resilience.onFailure to be 'skip-and-log' — there is nothing to escalate from when the strategy already aborts.");
        }

        if (resilience.OnFailure is not null || resilience.AbortWhen is not null)
        {
            builder.OnFailure(f =>
            {
                if (resilience.OnFailure is null || string.Equals(resilience.OnFailure, "abort", StringComparison.OrdinalIgnoreCase))
                    f.AbortReport();
                else if (isSkipAndLog)
                    f.SkipBatchAndLog();
                else
                    throw new ConfigurationException($"Unknown resilience.onFailure value '{resilience.OnFailure}'. Use 'abort' or 'skip-and-log'.");

                if (resilience.AbortWhen is { } thresholds)
                    f.AbortIf(thresholds);
            });
        }
    }

    private static void Validate(ReportConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ConfigurationException("Report configuration has no name.");
        if (config.Source is null)
            throw new ConfigurationException($"Report '{config.Name}' has no source.");
        if (config.Source.Ref is null && string.IsNullOrWhiteSpace(config.Source.Type))
            throw new ConfigurationException($"Report '{config.Name}' source has neither a 'type' nor a 'ref'.");
        if (config.Columns is null || config.Columns.Count == 0)
            throw new ConfigurationException($"Report '{config.Name}' has no columns.");
        if (config.Outputs is null || config.Outputs.Count == 0)
            throw new ConfigurationException($"Report '{config.Name}' has no outputs.");
    }

    private static void BuildSections(
        SectionBuilder<ReportRecord> sectionBuilder,
        IReadOnlyList<SectionConfig> sections,
        IReadOnlyDictionary<string, ColumnDefinition<ReportRecord>> columnsByName)
    {
        foreach (SectionConfig section in sections)
        {
            sectionBuilder.Section(section.Name, view =>
            {
                if (section.Filter is not null)
                    view.Where(JsonLogicFilter.Compile(section.Filter));

                if (section.Columns is { Count: > 0 })
                    view.Columns(ResolveSectionColumns(section, columnsByName));
            });
        }
    }

    private static ColumnDefinition<ReportRecord>[] ResolveSectionColumns(
        SectionConfig section, IReadOnlyDictionary<string, ColumnDefinition<ReportRecord>> columnsByName)
    {
        var defs = new ColumnDefinition<ReportRecord>[section.Columns!.Count];
        for (var i = 0; i < section.Columns.Count; i++)
        {
            var name = section.Columns[i];
            if (!columnsByName.TryGetValue(name, out ColumnDefinition<ReportRecord>? def))
                throw new ConfigurationException($"Section '{section.Name}' references unknown column '{name}'.");
            defs[i] = def;
        }

        return defs;
    }

    private static ISectionedWriterFactory ResolveSectionedWriter(IServiceProvider services, string format)
    {
        ISectionedWriterFactory? factory = services.GetServices<ISectionedWriterFactory>()
            .FirstOrDefault(f => string.Equals(f.Format, format, StringComparison.OrdinalIgnoreCase));
        return factory ?? throw new ConfigurationException(
            $"No sectioned writer factory is registered for format '{format}'. Register an ISectionedWriterFactory with that Format.");
    }

    // Internal (not private): RefBatchSource re-resolves the provider on every run, from the same
    // registrations, rather than duplicating this lookup (ADR D42).
    internal static IConfigSourceProvider ResolveSource(IServiceProvider services, string type)
    {
        IConfigSourceProvider? provider = services.GetServices<IConfigSourceProvider>()
            .FirstOrDefault(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));
        return provider ?? throw new ConfigurationException(
            $"No source provider is registered for type '{type}'. Register an IConfigSourceProvider with that Type.");
    }

    /// <summary>
    /// Resolves a ref-based source (ADR D42) into an <see cref="IBatchSource{ReportRecord}"/> that
    /// re-resolves the definition through <see cref="ISourceRegistry"/> on every run — this method
    /// only validates existence and type compatibility now (fail fast at compile time); the actual
    /// definition properties are never baked into the returned source.
    /// </summary>
    private static IBatchSource<ReportRecord> ResolveRefSource(IServiceProvider services, SourceConfig source, ReportSchema schema)
    {
        string refName = source.Ref!;
        ISourceRegistry? registry = services.GetService<ISourceRegistry>();
        if (registry is null)
        {
            throw new ConfigurationException(
                $"Report source references '{refName}' but no source registry is configured on this host. " +
                "Register one with AddSourceRegistry()/AddInMemorySourceRegistry().");
        }

        SourceDefinition? definition = registry.ResolveAsync(refName, CancellationToken.None).GetAwaiter().GetResult();
        if (definition is null)
            throw new ConfigurationException($"No source named '{refName}' is registered.");

        if (source.Type is not null && !string.Equals(source.Type, definition.Type, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationException(
                $"Source '{refName}' is registered as type '{definition.Type}', but the report declares type '{source.Type}'.");
        }

        string effectiveType = source.Type ?? definition.Type;
        ResolveSource(services, effectiveType); // fail fast when no provider is registered for the type

        return new RefBatchSource(refName, effectiveType, source.Properties, registry, services, schema);
    }

    private static IWriterFactory ResolveWriter(IServiceProvider services, string format)
    {
        IWriterFactory? factory = services.GetServices<IWriterFactory>()
            .FirstOrDefault(f => string.Equals(f.Format, format, StringComparison.OrdinalIgnoreCase));
        return factory ?? throw new ConfigurationException(
            $"No writer factory is registered for format '{format}'. Register an IWriterFactory with that Format.");
    }

    private static IDestinationFactory ResolveDestination(IServiceProvider services, string type)
    {
        IDestinationFactory? factory = services.GetServices<IDestinationFactory>()
            .FirstOrDefault(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase));
        return factory ?? throw new ConfigurationException(
            $"No destination factory is registered for type '{type}'. Register an IDestinationFactory with that Type.");
    }
}
