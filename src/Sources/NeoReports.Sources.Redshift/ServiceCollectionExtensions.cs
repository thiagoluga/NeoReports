using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Npgsql;

namespace NeoReports.Sources.Redshift;

/// <summary>DI helpers for the Amazon Redshift source.</summary>
public static class ServiceCollectionExtensions
{
    // information_schema catalog queries (ADR D49/D57) — deliberately identical to the Postgres
    // provider's own (not accidental drift-risk duplication): Redshift documents
    // information_schema.columns/table_constraints/key_column_usage support matching its Postgres
    // 8.0.2 lineage, but this package cannot reference NeoReports.Sources.Postgres's private query
    // text directly — every source-type package is independently installable (a host adding just
    // Redshift must not be forced to also pull in the Postgres package), so each package owns its own
    // copy of the SQL, exactly like Postgres/MySQL/Oracle already do among themselves. Redshift stores
    // PK/FK metadata for the query planner even though it doesn't enforce those constraints at write
    // time — irrelevant here, this explorer only reads DDL metadata, never enforces anything.
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
    /// Registers the config-driven Amazon Redshift source provider (<c>type: "redshift"</c>) so
    /// reports defined in configuration can read from Redshift. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddRedshiftConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, RedshiftConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, RedshiftSourceHealthCheck>());
        // Assumed from Redshift's documented Postgres lineage (no implicit text-to-typed conversion
        // in comparisons) — not empirically re-verified against a live cluster (ADR D57's testing gap).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(
            new AdoFilterTranslator("redshift", castParameter: AdoFilterTranslator.PostgresCast)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new AdoSchemaExplorer("redshift", "Redshift", cs => new NpgsqlConnection(cs), CatalogQueries,
                AdoSchemaExplorer.QuoteAnsi, AdoSchemaExplorer.PreviewWithLimit)));
        return services;
    }
}
