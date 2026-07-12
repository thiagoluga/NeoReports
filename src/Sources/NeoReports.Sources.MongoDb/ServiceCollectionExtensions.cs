using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.MongoDb;

/// <summary>DI helpers for the MongoDB source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven MongoDB source provider (<c>type: "mongodb"</c>) so reports
    /// defined in configuration can read from MongoDB. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMongoDbConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, MongoDbConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, MongoDbSourceHealthCheck>());
        return services;
    }
}
