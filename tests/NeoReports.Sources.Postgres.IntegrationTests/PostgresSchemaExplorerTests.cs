using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using Npgsql;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// Verifies the registered PostgreSQL <see cref="ISchemaExplorer"/> (ADR D49) against a real
/// container — catalog shape (tables/columns/nullable/PK), FK discovery, and a bounded preview.
/// Resolves the explorer through DI so the real registered <c>information_schema</c> queries are
/// exercised, not a hand-built instance. Postgres folds unquoted identifiers to lower case, so the
/// catalog reports lower-case names.
/// </summary>
[Collection(nameof(PostgresServerCollection))]
public class PostgresSchemaExplorerTests
{
    private readonly PostgresServerFixture _fixture;

    public PostgresSchemaExplorerTests(PostgresServerFixture fixture) => _fixture = fixture;

    private static ISchemaExplorer Explorer()
    {
        ServiceProvider services = new ServiceCollection().AddPostgresConfigSource().BuildServiceProvider();
        return services.GetServices<ISchemaExplorer>().Single(e => e.Type == "postgres");
    }

    private SourceDefinition Definition() =>
        new("db", "postgres", new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

    private async Task CreateFkSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE TABLE IF NOT EXISTS nr_regions (region_id INT PRIMARY KEY, region_name VARCHAR(50) NOT NULL)",
            "CREATE TABLE IF NOT EXISTS nr_customers (customer_id INT PRIMARY KEY, region_id INT REFERENCES nr_regions(region_id), note VARCHAR(100))",
            "INSERT INTO nr_regions VALUES (1, 'North') ON CONFLICT DO NOTHING",
            "INSERT INTO nr_customers VALUES (1, 1, 'hi') ON CONFLICT DO NOTHING",
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
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");
        await CreateFkSchemaAsync();

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        catalog.Tables.ShouldContain(t => t.Name == "sales"); // the fixture's own seeded table
        CatalogTable customers = catalog.Tables.Single(t => t.Name == "nr_customers");
        customers.Columns.Select(c => c.Name).ShouldBe(new[] { "customer_id", "region_id", "note" });
        customers.Columns.Single(c => c.Name == "customer_id").IsPrimaryKey.ShouldBeTrue();
        customers.Columns.Single(c => c.Name == "region_id").IsPrimaryKey.ShouldBeFalse();
        customers.Columns.Single(c => c.Name == "region_id").Nullable.ShouldBeTrue();
        customers.Columns.Single(c => c.Name == "note").Nullable.ShouldBeTrue();

        ForeignKey fk = customers.ForeignKeys.Single();
        fk.Column.ShouldBe("region_id");
        fk.ReferencedTable.ShouldBe("nr_regions");
        fk.ReferencedColumn.ShouldBe("region_id");
    }

    [SkippableFact]
    public async Task Preview_returns_at_most_the_requested_number_of_rows()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");
        await CreateFkSchemaAsync();

        TablePreview preview = await Explorer().PreviewTableAsync(Definition(), "public", "nr_regions", top: 1, CancellationToken.None);

        preview.Columns.ShouldContain("region_id");
        preview.Columns.ShouldContain("region_name");
        preview.Rows.Count.ShouldBeLessThanOrEqualTo(1);
    }
}
