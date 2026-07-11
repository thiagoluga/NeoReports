using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Postgres;

/// <summary>DI helpers for the PostgreSQL source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven PostgreSQL source provider (<c>type: "postgres"</c>) so reports
    /// defined in configuration can read from PostgreSQL. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddPostgresConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, PostgresConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, PostgresSourceHealthCheck>());
        return services;
    }
}
