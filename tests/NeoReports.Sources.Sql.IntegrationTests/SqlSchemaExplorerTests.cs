using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// Verifies the registered SQL Server <see cref="ISchemaExplorer"/> (ADR D49) against a real
/// container — catalog shape, FK discovery (via the <c>sys.foreign_key_columns</c> catalog view,
/// since SQL Server's <c>INFORMATION_SCHEMA.KEY_COLUMN_USAGE</c> has no
/// <c>POSITION_IN_UNIQUE_CONSTRAINT</c>), and a bounded preview (<c>SELECT TOP n</c>). New tables
/// land in the <c>dbo</c> schema.
/// </summary>
public class SqlSchemaExplorerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlSchemaExplorerTests(SqlServerFixture fixture) => _fixture = fixture;

    private static ISchemaExplorer Explorer()
    {
        ServiceProvider services = new ServiceCollection().AddSqlConfigSource().BuildServiceProvider();
        return services.GetServices<ISchemaExplorer>().Single(e => e.Type == "sql");
    }

    private SourceDefinition Definition() =>
        new("db", "sql", new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

    private async Task CreateFkSchemaAsync()
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "IF OBJECT_ID('nr_regions','U') IS NULL CREATE TABLE nr_regions (region_id INT PRIMARY KEY, region_name VARCHAR(50) NOT NULL)",
            "IF OBJECT_ID('nr_customers','U') IS NULL CREATE TABLE nr_customers (customer_id INT PRIMARY KEY, region_id INT NULL " +
            "REFERENCES nr_regions(region_id), note VARCHAR(100) NULL)",
            "IF NOT EXISTS (SELECT 1 FROM nr_regions) INSERT INTO nr_regions VALUES (1, 'North')",
            "IF NOT EXISTS (SELECT 1 FROM nr_customers) INSERT INTO nr_customers VALUES (1, 1, 'hi')",
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
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");
        await CreateFkSchemaAsync();

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        CatalogTable customers = catalog.Tables.Single(t => t.Name == "nr_customers");
        customers.Schema.ShouldBe("dbo");
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
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");
        await CreateFkSchemaAsync();

        TablePreview preview = await Explorer().PreviewTableAsync(Definition(), "dbo", "nr_regions", top: 1, CancellationToken.None);

        preview.Columns.ShouldContain("region_id");
        preview.Rows.Count.ShouldBeLessThanOrEqualTo(1);
    }
}
