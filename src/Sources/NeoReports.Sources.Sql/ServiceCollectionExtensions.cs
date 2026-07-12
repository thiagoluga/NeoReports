using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, SqlSourceHealthCheck>());
        // A derived table containing a bare ORDER BY (every keyset query already ends with one) is
        // invalid T-SQL unless followed by TOP, OFFSET, or FOR XML — every other supported dialect
        // allows it as-is.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(
            new AdoFilterTranslator("sql", innerQuerySuffix: " OFFSET 0 ROWS")));
        return services;
    }
}
