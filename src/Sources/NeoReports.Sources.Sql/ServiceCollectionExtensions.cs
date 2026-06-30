using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Sql;

/// <summary>DI helpers for the SQL source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven SQL source provider (<c>type: "sql"</c>) so reports defined in
    /// configuration can read from SQL Server. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddSqlConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, SqlConfigSourceProvider>());
        return services;
    }
}
