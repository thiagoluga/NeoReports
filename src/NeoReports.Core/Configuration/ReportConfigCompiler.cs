using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;

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

        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ConfigurationException("Report configuration has no name.");
        if (config.Source is null)
            throw new ConfigurationException($"Report '{config.Name}' has no source.");
        if (config.Columns is null || config.Columns.Count == 0)
            throw new ConfigurationException($"Report '{config.Name}' has no columns.");
        if (config.Outputs is null || config.Outputs.Count == 0)
            throw new ConfigurationException($"Report '{config.Name}' has no outputs.");
        if (config.Filter is not null)
        {
            throw new ConfigurationException(
                $"Report '{config.Name}' declares a filter, but dynamic filters require the JsonLogic " +
                "compiler (Epic A4), which is not available yet.");
        }

        var columns = new ColumnDefinition<ReportRecord>[config.Columns.Count];
        for (var i = 0; i < config.Columns.Count; i++)
        {
            var c = config.Columns[i];
            columns[i] = ReportColumns.Positional(i, c.Name, c.Type, c.Nullable, c.DisplayName, c.Format, c.Culture);
        }

        var schema = new ReportSchema(columns.Select(c => c.Column).ToList());

        // Resolve every registration up front (fail fast on a missing provider/factory) before
        // instantiating the source, which may open connections.
        var sourceProvider = ResolveSource(services, config.Source.Type);
        var outputs = config.Outputs
            .Select(o => new OutputSpec(ResolveWriter(services, o.Format), o.Properties))
            .ToArray();
        var destinations = config.Destinations?
            .Select(d => new DestinationSpec(ResolveDestination(services, d.Type), d.Properties))
            .ToArray() ?? Array.Empty<DestinationSpec>();

        var source = sourceProvider.Create(config.Source, schema, services);

        var builder = new ReportBuilder<ReportRecord>(config.Name)
            .From(source)
            .Columns(columns);

        if (config.PageSize is int pageSize)
            builder.WithPageSize(pageSize);

        foreach (var output in outputs)
            builder.To(output);
        foreach (var destination in destinations)
            builder.UploadTo(destination);

        return builder.Build();
    }

    private static IConfigSourceProvider ResolveSource(IServiceProvider services, string type)
    {
        var provider = services.GetServices<IConfigSourceProvider>()
            .FirstOrDefault(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));
        return provider ?? throw new ConfigurationException(
            $"No source provider is registered for type '{type}'. Register an IConfigSourceProvider with that Type.");
    }

    private static IWriterFactory ResolveWriter(IServiceProvider services, string format)
    {
        var factory = services.GetServices<IWriterFactory>()
            .FirstOrDefault(f => string.Equals(f.Format, format, StringComparison.OrdinalIgnoreCase));
        return factory ?? throw new ConfigurationException(
            $"No writer factory is registered for format '{format}'. Register an IWriterFactory with that Format.");
    }

    private static IDestinationFactory ResolveDestination(IServiceProvider services, string type)
    {
        var factory = services.GetServices<IDestinationFactory>()
            .FirstOrDefault(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase));
        return factory ?? throw new ConfigurationException(
            $"No destination factory is registered for type '{type}'. Register an IDestinationFactory with that Type.");
    }
}
