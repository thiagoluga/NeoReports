using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Csv;

/// <summary>
/// DI helpers for the CSV source (ADR D58). No <c>ISchemaExplorer</c>/<c>IFilterTranslator</c> is
/// registered — a flat file has no catalog or query protocol to introspect or push filters into
/// (D36 honest capability gap, matching D55's own framing for file sources).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven CSV source provider (<c>type: "csv"</c>) so reports defined in
    /// configuration can read from a CSV file (local or S3). Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddCsvConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, CsvConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, CsvSourceHealthCheck>());
        return services;
    }
}
