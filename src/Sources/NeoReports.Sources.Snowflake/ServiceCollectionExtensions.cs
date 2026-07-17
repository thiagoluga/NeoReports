using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Snowflake.Data.Client;

namespace NeoReports.Sources.Snowflake;

/// <summary>DI helpers for the Snowflake source.</summary>
public static class ServiceCollectionExtensions
{
    // INFORMATION_SCHEMA catalog queries (ADR D49/D57), scoped to the connection's current database
    // (INFORMATION_SCHEMA itself is per-database in Snowflake, the same "one connection, one catalog"
    // scope every other provider's queries already assume) — never the INFORMATION_SCHEMA schema's
    // own system views. Unquoted Snowflake identifiers fold to upper case, hence the literal casing.
    private static readonly SchemaCatalogQueries CatalogQueries = new(
        ColumnsSql: """
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type = 'BASE TABLE' AND c.table_schema <> 'INFORMATION_SCHEMA'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
        PrimaryKeysSql: """
            SELECT tc.table_schema, tc.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
            WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema <> 'INFORMATION_SCHEMA'
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
            WHERE kcu.table_schema <> 'INFORMATION_SCHEMA'
            """);

    /// <summary>
    /// Registers the config-driven Snowflake source provider (<c>type: "snowflake"</c>) so reports
    /// defined in configuration can read from Snowflake. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddSnowflakeConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, SnowflakeConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, SnowflakeSourceHealthCheck>());
        // No cast configured (ADR D57): Snowflake's documented implicit-conversion rules apply NUMBER
        // coercion to a VARCHAR operand in a comparison — not empirically re-verified against a live
        // warehouse. Note the ":" prefix, not "@" — verified against the driver's own docs (D57).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(
            new AdoFilterTranslator("snowflake", parameterPrefix: ":")));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new AdoSchemaExplorer("snowflake", "Snowflake", cs => new SnowflakeDbConnection(cs), CatalogQueries,
                AdoSchemaExplorer.QuoteAnsi, AdoSchemaExplorer.PreviewWithLimit)));
        return services;
    }
}
