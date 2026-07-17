using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sqlite;

/// <summary>DI helpers for the SQLite source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven SQLite source provider (<c>type: "sqlite"</c>) so reports defined
    /// in configuration can read from a SQLite database. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddSqliteConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, SqliteConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, SqliteSourceHealthCheck>());
        // No cast configured (ADR D56): SQLite's operand-affinity rule already applies NUMERIC
        // affinity to a text-bound parameter compared against a NUMERIC/INTEGER/REAL-affinity column,
        // so the preview UI's always-text filter values (D45) compare correctly without one —
        // verified empirically in SqliteFilterTranslatorIntegrationTests.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(new AdoFilterTranslator("sqlite")));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new SqliteSchemaExplorer(cs => new SqliteConnection(cs))));
        return services;
    }
}
