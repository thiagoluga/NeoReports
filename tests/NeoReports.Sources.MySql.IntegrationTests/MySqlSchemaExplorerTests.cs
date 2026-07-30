using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.MySql.IntegrationTests;

/// <summary>
/// Verifies the registered MySQL <see cref="ISchemaExplorer"/> (ADR D49) against a real container —
/// catalog shape, FK discovery (MySQL exposes <c>referenced_*</c> directly on
/// <c>key_column_usage</c>), and a bounded preview. Scoped to the connection's current database.
/// </summary>
[Collection(nameof(MySqlServerCollection))]
public class MySqlSchemaExplorerTests
{
    private readonly MySqlServerFixture _fixture;

    public MySqlSchemaExplorerTests(MySqlServerFixture fixture) => _fixture = fixture;

    private static ISchemaExplorer Explorer()
    {
        ServiceProvider services = new ServiceCollection().AddMySqlConfigSource().BuildServiceProvider();
        return services.GetServices<ISchemaExplorer>().Single(e => e.Type == "mysql");
    }

    private SourceDefinition Definition() =>
        new("db", "mysql", new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

    private async Task CreateFkSchemaAsync()
    {
        await using var connection = new MySqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE TABLE IF NOT EXISTS nr_regions (region_id INT PRIMARY KEY, region_name VARCHAR(50) NOT NULL)",
            "CREATE TABLE IF NOT EXISTS nr_customers (customer_id INT PRIMARY KEY, region_id INT, note VARCHAR(100), " +
            "FOREIGN KEY (region_id) REFERENCES nr_regions(region_id))",
            "INSERT IGNORE INTO nr_regions VALUES (1, 'North')",
            "INSERT IGNORE INTO nr_customers VALUES (1, 1, 'hi')",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Catalog_reports_tables_columns_primary_key_and_foreign_key()
    {
        Skip.IfNot(_fixture.Available, "Docker/MySQL container not available.");
        await CreateFkSchemaAsync();

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        CatalogTable customers = catalog.Tables.Single(t => t.Name == "nr_customers");
        customers.Columns.Select(c => c.Name).ShouldBe(new[] { "customer_id", "region_id", "note" });
        customers.Columns.Single(c => c.Name == "customer_id").IsPrimaryKey.ShouldBeTrue();
        customers.Columns.Single(c => c.Name == "region_id").Nullable.ShouldBeTrue();

        ForeignKey fk = customers.ForeignKeys.Single();
        fk.Column.ShouldBe("region_id");
        fk.ReferencedTable.ShouldBe("nr_regions");
        fk.ReferencedColumn.ShouldBe("region_id");
    }

    [SkippableFact]
    public async Task Preview_returns_at_most_the_requested_number_of_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/MySQL container not available.");
        await CreateFkSchemaAsync();

        CatalogTable regions = (await Explorer().GetCatalogAsync(Definition(), CancellationToken.None))
            .Tables.Single(t => t.Name == "nr_regions");
        TablePreview preview = await Explorer().PreviewTableAsync(Definition(), regions.Schema, "nr_regions", top: 1, CancellationToken.None);

        preview.Columns.ShouldContain("region_id");
        preview.Rows.Count.ShouldBeLessThanOrEqualTo(1);
    }
}
