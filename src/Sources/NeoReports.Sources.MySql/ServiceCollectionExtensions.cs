using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.MySql;

/// <summary>DI helpers for the MySQL/MariaDB source.</summary>
public static class ServiceCollectionExtensions
{
    // information_schema catalog queries (ADR D49). In MySQL the "schema" is the database, so the
    // connection's own current database (DATABASE()) is the scope — never information_schema/mysql/
    // performance_schema/sys. MySQL exposes referenced_* directly on key_column_usage, so FK
    // discovery needs no referential_constraints join.
    private static readonly SchemaCatalogQueries CatalogQueries = new(
        ColumnsSql: """
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type = 'BASE TABLE' AND c.table_schema = DATABASE()
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
        PrimaryKeysSql: """
            SELECT tc.table_schema, tc.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name
             AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
            WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = DATABASE()
            """,
        ForeignKeysSql: """
            SELECT table_schema, table_name, column_name,
                   referenced_table_schema, referenced_table_name, referenced_column_name
            FROM information_schema.key_column_usage
            WHERE table_schema = DATABASE() AND referenced_table_name IS NOT NULL
            """);

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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new AdoSchemaExplorer("mysql", "MySQL", cs => new MySqlConnection(cs), CatalogQueries,
                AdoSchemaExplorer.QuoteMySql, AdoSchemaExplorer.PreviewWithLimit)));
        return services;
    }
}
