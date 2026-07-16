using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;
using Oracle.ManagedDataAccess.Client;

namespace NeoReports.Sources.Oracle;

/// <summary>DI helpers for the Oracle source.</summary>
public static class ServiceCollectionExtensions
{
    // Data-dictionary catalog queries (ADR D49). Scoped to the connecting user's own schema
    // (OWNER = USER) via the ALL_* views — never the SYS/SYSTEM catalog. NULLABLE is 'Y'/'N'. FK
    // discovery joins ALL_CONSTRAINTS (CONSTRAINT_TYPE 'R') to its referenced constraint by
    // R_CONSTRAINT_NAME, pairing columns by POSITION.
    private static readonly SchemaCatalogQueries CatalogQueries = new(
        ColumnsSql: """
            SELECT c.OWNER, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.NULLABLE
            FROM ALL_TAB_COLUMNS c
            JOIN ALL_TABLES t ON t.OWNER = c.OWNER AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.OWNER = USER
            ORDER BY c.OWNER, c.TABLE_NAME, c.COLUMN_ID
            """,
        PrimaryKeysSql: """
            SELECT cons.OWNER, cons.TABLE_NAME, cols.COLUMN_NAME
            FROM ALL_CONSTRAINTS cons
            JOIN ALL_CONS_COLUMNS cols
              ON cols.OWNER = cons.OWNER AND cols.CONSTRAINT_NAME = cons.CONSTRAINT_NAME
            WHERE cons.CONSTRAINT_TYPE = 'P' AND cons.OWNER = USER
            """,
        ForeignKeysSql: """
            SELECT fk.OWNER, fk.TABLE_NAME, fcol.COLUMN_NAME,
                   pk.OWNER, pk.TABLE_NAME, pcol.COLUMN_NAME
            FROM ALL_CONSTRAINTS fk
            JOIN ALL_CONS_COLUMNS fcol
              ON fcol.OWNER = fk.OWNER AND fcol.CONSTRAINT_NAME = fk.CONSTRAINT_NAME
            JOIN ALL_CONSTRAINTS pk
              ON pk.OWNER = fk.R_OWNER AND pk.CONSTRAINT_NAME = fk.R_CONSTRAINT_NAME
            JOIN ALL_CONS_COLUMNS pcol
              ON pcol.OWNER = pk.OWNER AND pcol.CONSTRAINT_NAME = pk.CONSTRAINT_NAME
             AND pcol.POSITION = fcol.POSITION
            WHERE fk.CONSTRAINT_TYPE = 'R' AND fk.OWNER = USER
            """);

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
        // Oracle does implicitly convert a text-bound parameter to NUMBER in a comparison, but that
        // conversion follows the session's NLS settings — a value like "2000.00" can fail with
        // ORA-01722 against a session whose numeric locale doesn't treat '.' as the decimal
        // separator, so numeric filters need an explicit, locale-independent cast. Reserved-word
        // columns (e.g. "Date") also need quoting or Oracle rejects the bare reference (ORA-01747).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(new AdoFilterTranslator(
            "oracle",
            parameterPrefix: ":",
            castParameter: AdoFilterTranslator.OracleCast,
            quoteIdentifier: AdoFilterTranslator.OracleQuoteIdentifier)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchemaExplorer>(
            new AdoSchemaExplorer("oracle", "Oracle", cs => new OracleConnection(cs), CatalogQueries,
                AdoSchemaExplorer.QuoteAnsi, AdoSchemaExplorer.PreviewWithFetchFirst)));
        return services;
    }
}
