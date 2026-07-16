using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Postgres;

/// <summary>DI helpers for the PostgreSQL source.</summary>
public static class ServiceCollectionExtensions
{
    // information_schema catalog queries (ADR D49). User tables only; the pg_catalog/information_schema
    // system schemas are excluded. FK discovery uses the ANSI referential_constraints join (position_in_
    // unique_constraint pairs each FK column to its referenced column, correct even for composite keys).
    private static readonly SchemaCatalogQueries CatalogQueries = new(
        ColumnsSql: """
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type = 'BASE TABLE'
              AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
        PrimaryKeysSql: """
            SELECT tc.table_schema, tc.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
            """,
        ForeignKeysSql: """
            SELECT kcu.table_schema, kcu.table_name, kcu.column_name,
                   ccu.table_schema, ccu.table_name, ccu.column_name
            FROM information_schema.referential_constraints rc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = rc.constraint_name AND kcu.constraint_schema = rc.constraint_schema
            JOIN information_schema.key_column_usage ccu
              ON ccu.constraint_name = rc.unique_constraint_name AND ccu.constraint_schema = rc.unique_constraint_schema
             AND ccu.ordinal_position = kcu.position_in_unique_constraint
            WHERE kcu.table_schema NOT IN ('pg_catalog', 'information_schema')
            """);

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
        // Postgres has no implicit conversion between text and most other types in a comparison —
        // filter values always arrive text-bound (the preview UI's plain text input), so the
        // translator must cast the bind parameter to the filtered column's real type.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(
            new AdoFilterTranslator("postgres", castParameter: AdoFilterTranslator.PostgresCast)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new AdoSchemaExplorer("postgres", "PostgreSQL", cs => new NpgsqlConnection(cs), CatalogQueries,
                AdoSchemaExplorer.QuoteAnsi, AdoSchemaExplorer.PreviewWithLimit)));
        return services;
    }
}
