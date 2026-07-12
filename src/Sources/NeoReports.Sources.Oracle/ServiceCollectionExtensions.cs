using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Oracle;

/// <summary>DI helpers for the Oracle source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Oracle source provider (<c>type: "oracle"</c>) so reports
    /// defined in configuration can read from Oracle. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddOracleConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, OracleConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, OracleSourceHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(new AdoFilterTranslator("oracle", parameterPrefix: ":")));
        return services;
    }
}
