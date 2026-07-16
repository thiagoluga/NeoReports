using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>
/// Verifies the registered Oracle <see cref="ISchemaExplorer"/> (ADR D49) against a real container —
/// catalog shape, FK discovery (ALL_CONSTRAINTS join), and a bounded preview (<c>FETCH FIRST</c>).
/// Oracle folds unquoted identifiers to upper case, so the catalog reports upper-case names; the
/// explorer's <c>OWNER = USER</c> scoping means these tables (created as the default app user) show.
/// Joins the shared Oracle collection fixture, so its DDL is guarded to be idempotent across runs.
/// </summary>
[Collection(nameof(OracleCollection))]
public class OracleSchemaExplorerTests
{
    private readonly OracleServerFixture _fixture;

    public OracleSchemaExplorerTests(OracleServerFixture fixture) => _fixture = fixture;

    private static ISchemaExplorer Explorer()
    {
        ServiceProvider services = new ServiceCollection().AddOracleConfigSource().BuildServiceProvider();
        return services.GetServices<ISchemaExplorer>().Single(e => e.Type == "oracle");
    }

    private SourceDefinition Definition() =>
        new("db", "oracle", new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

    // Oracle has no CREATE TABLE IF NOT EXISTS and the fixture is shared assembly-wide, so each DDL
    // runs in a PL/SQL block that swallows "name already used" (ORA-00955); inserts swallow the
    // unique-constraint violation (ORA-00001) — making the whole setup safely re-runnable.
    private async Task CreateFkSchemaAsync()
    {
        await using var connection = new OracleConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var ddl in new[]
        {
            "CREATE TABLE nr_regions (region_id NUMBER PRIMARY KEY, region_name VARCHAR2(50) NOT NULL)",
            "CREATE TABLE nr_customers (customer_id NUMBER PRIMARY KEY, region_id NUMBER REFERENCES nr_regions(region_id), note VARCHAR2(100))",
        })
        {
            await ExecuteIgnoring(connection, ddl, ignoreCode: -955);
        }

        await ExecuteIgnoring(connection, "INSERT INTO nr_regions (region_id, region_name) VALUES (1, 'North')", ignoreCode: -1);
        await ExecuteIgnoring(connection, "INSERT INTO nr_customers (customer_id, region_id, note) VALUES (1, 1, 'hi')", ignoreCode: -1);
        await ExecuteIgnoring(connection, "COMMIT", ignoreCode: 0);
    }

    private static async Task ExecuteIgnoring(OracleConnection connection, string statement, int ignoreCode)
    {
        string body = ignoreCode == 0
            ? statement
            : $"BEGIN EXECUTE IMMEDIATE '{statement.Replace("'", "''", StringComparison.Ordinal)}'; " +
              $"EXCEPTION WHEN OTHERS THEN IF SQLCODE != {ignoreCode} THEN RAISE; END IF; END;";
        await using var command = connection.CreateCommand();
        command.CommandText = body;
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Catalog_reports_tables_columns_primary_key_and_foreign_key()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");
        await CreateFkSchemaAsync();

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        CatalogTable customers = catalog.Tables.Single(t => t.Name == "NR_CUSTOMERS");
        customers.Columns.Select(c => c.Name).ShouldBe(new[] { "CUSTOMER_ID", "REGION_ID", "NOTE" });
        customers.Columns.Single(c => c.Name == "CUSTOMER_ID").IsPrimaryKey.ShouldBeTrue();
        customers.Columns.Single(c => c.Name == "REGION_ID").Nullable.ShouldBeTrue();

        ForeignKey fk = customers.ForeignKeys.Single();
        fk.Column.ShouldBe("REGION_ID");
        fk.ReferencedTable.ShouldBe("NR_REGIONS");
        fk.ReferencedColumn.ShouldBe("REGION_ID");
    }

    [SkippableFact]
    public async Task Preview_returns_at_most_the_requested_number_of_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");
        await CreateFkSchemaAsync();

        CatalogTable regions = (await Explorer().GetCatalogAsync(Definition(), CancellationToken.None))
            .Tables.Single(t => t.Name == "NR_REGIONS");
        TablePreview preview = await Explorer().PreviewTableAsync(Definition(), regions.Schema, "NR_REGIONS", top: 1, CancellationToken.None);

        preview.Columns.ShouldContain("REGION_ID");
        preview.Rows.Count.ShouldBeLessThanOrEqualTo(1);
    }
}
