using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Sections;

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

        // Resolve every registration up front (fail fast on a missing provider/factory) before
        // instantiating the source, which may open connections.
        IConfigSourceProvider sourceProvider = ResolveSource(services, config.Source.Type);

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

        IBatchSource<ReportRecord> source = sourceProvider.Create(config.Source, schema, services);

        ReportBuilder<ReportRecord> builder = new ReportBuilder<ReportRecord>(config.Name)
            .From(source)
            .Columns(columns);

        if (config.PageSize is int pageSize)
            builder.WithPageSize(pageSize);

        if (config.Filter is not null)
            builder.Filter(JsonLogicFilter.Compile(config.Filter));

        if (config.Resilience is { } resilience)
            ApplyResilience(builder, resilience);

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

    private static IConfigSourceProvider ResolveSource(IServiceProvider services, string type)
    {
        IConfigSourceProvider? provider = services.GetServices<IConfigSourceProvider>()
            .FirstOrDefault(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));
        return provider ?? throw new ConfigurationException(
            $"No source provider is registered for type '{type}'. Register an IConfigSourceProvider with that Type.");
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
