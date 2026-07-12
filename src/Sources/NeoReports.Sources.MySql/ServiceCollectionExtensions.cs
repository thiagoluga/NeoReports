using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.MySql;

/// <summary>DI helpers for the MySQL/MariaDB source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven MySQL/MariaDB source provider (<c>type: "mysql"</c>) so reports
    /// defined in configuration can read from MySQL/MariaDB. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMySqlConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, MySqlConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, MySqlSourceHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(new AdoFilterTranslator("mysql")));
        return services;
    }
}
