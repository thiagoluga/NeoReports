using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// DI helpers for the XLSX source (ADR D59). No <c>ISchemaExplorer</c>/<c>IFilterTranslator</c> is
/// registered — a flat file has no catalog or query protocol to introspect or push filters into
/// (D36 honest capability gap, matching D55's own framing for file sources).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven XLSX source provider (<c>type: "xlsx"</c>) so reports defined in
    /// configuration can read from an XLSX file (local or S3). Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddXlsxConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, XlsxConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, XlsxSourceHealthCheck>());
        return services;
    }
}
