using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Parquet;

/// <summary>
/// DI helpers for the Parquet source (ADR D60). No <c>ISchemaExplorer</c>/<c>IFilterTranslator</c> is
/// registered — a flat file has no query protocol to push filters into (D36 honest capability gap,
/// matching D55's own framing for file sources and D58/D59's precedent). Parquet's embedded schema
/// could in principle back a real <c>ISchemaExplorer</c> more honestly than CSV/XLSX's inferred-from-
/// header approach, but that stays out of this pass — same "ship the source, capabilities can come
/// later" precedent D58 set.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Parquet source provider (<c>type: "parquet"</c>) so reports defined
    /// in configuration can read from a Parquet file (local or S3). Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddParquetConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, ParquetConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, ParquetSourceHealthCheck>());
        return services;
    }
}
