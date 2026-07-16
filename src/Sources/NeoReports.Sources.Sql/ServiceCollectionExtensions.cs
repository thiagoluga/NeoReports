using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sql;

/// <summary>DI helpers for the SQL source.</summary>
public static class ServiceCollectionExtensions
{
    // INFORMATION_SCHEMA catalog queries (ADR D49). User tables only (the sys/INFORMATION_SCHEMA
    // schemas are excluded). FK discovery uses the sys.foreign_key_columns catalog view rather than
    // the ANSI REFERENTIAL_CONSTRAINTS join — SQL Server's INFORMATION_SCHEMA.KEY_COLUMN_USAGE has
    // no POSITION_IN_UNIQUE_CONSTRAINT column (verified empirically: "Invalid column name"), so the
    // ANSI column-pairing join doesn't exist here; sys.foreign_key_columns pairs parent↔referenced
    // columns directly, one row per column pair (correct for composite keys too).
    private static readonly SchemaCatalogQueries CatalogQueries = new(
        ColumnsSql: """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
              AND c.TABLE_SCHEMA NOT IN ('sys', 'INFORMATION_SCHEMA')
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """,
        PrimaryKeysSql: """
            SELECT tc.TABLE_SCHEMA, tc.TABLE_NAME, kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND tc.TABLE_SCHEMA NOT IN ('sys', 'INFORMATION_SCHEMA')
            """,
        ForeignKeysSql: """
            SELECT sch.name, tp.name, cp.name, rsch.name, rtp.name, rcp.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.tables tp ON tp.object_id = fkc.parent_object_id
            JOIN sys.schemas sch ON sch.schema_id = tp.schema_id
            JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.tables rtp ON rtp.object_id = fkc.referenced_object_id
            JOIN sys.schemas rsch ON rsch.schema_id = rtp.schema_id
            JOIN sys.columns rcp ON rcp.object_id = fkc.referenced_object_id AND rcp.column_id = fkc.referenced_column_id
            """);

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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new AdoSchemaExplorer("sql", "SQL Server", cs => new SqlConnection(cs), CatalogQueries,
                AdoSchemaExplorer.QuoteSqlServer, AdoSchemaExplorer.PreviewWithTop)));
        return services;
    }
}
