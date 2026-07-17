using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

/// <summary>
/// Verifies the registered SQLite <see cref="ISchemaExplorer"/> (ADR D49/D56) against a real file —
/// catalog shape via <c>sqlite_master</c>/<c>PRAGMA table_info</c>/<c>PRAGMA foreign_key_list</c>
/// (including a foreign key that omits its referenced column, resolved to the referenced table's own
/// primary key), and a bounded preview.
/// </summary>
public class SqliteSchemaExplorerTests : IClassFixture<SqliteFileFixture>
{
    private readonly SqliteFileFixture _fixture;

    public SqliteSchemaExplorerTests(SqliteFileFixture fixture) => _fixture = fixture;

    private static ISchemaExplorer Explorer()
    {
        ServiceProvider services = new ServiceCollection().AddSqliteConfigSource().BuildServiceProvider();
        return services.GetServices<ISchemaExplorer>().Single(e => e.Type == "sqlite");
    }

    private SourceDefinition Definition() =>
        new("db", "sqlite", new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

    private async Task CreateFkSchemaAsync()
    {
        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE TABLE IF NOT EXISTS nr_regions (region_id INTEGER PRIMARY KEY, region_name TEXT NOT NULL)",
            // Omits the referenced column on purpose — SQLite's shorthand for "the referenced table's
            // own primary key", which SqliteSchemaExplorer resolves itself (PRAGMA reports "to" empty).
            "CREATE TABLE IF NOT EXISTS nr_customers (customer_id INTEGER PRIMARY KEY, region_id INTEGER, note TEXT, " +
            "FOREIGN KEY (region_id) REFERENCES nr_regions)",
            "INSERT OR IGNORE INTO nr_regions VALUES (1, 'North')",
            "INSERT OR IGNORE INTO nr_customers VALUES (1, 1, 'hi')",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Catalog_reports_tables_columns_primary_key_and_foreign_key()
    {
        await CreateFkSchemaAsync();

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        CatalogTable customers = catalog.Tables.Single(t => t.Name == "nr_customers");
        customers.Columns.Select(c => c.Name).ShouldBe(new[] { "customer_id", "region_id", "note" });
        customers.Columns.Single(c => c.Name == "customer_id").IsPrimaryKey.ShouldBeTrue();
        customers.Columns.Single(c => c.Name == "region_id").Nullable.ShouldBeTrue();

        ForeignKey fk = customers.ForeignKeys.Single();
        fk.Column.ShouldBe("region_id");
        fk.ReferencedTable.ShouldBe("nr_regions");
        // The FK omitted its referenced column — resolved to nr_regions' own primary key.
        fk.ReferencedColumn.ShouldBe("region_id");
    }

    [Fact]
    public async Task Foreign_key_referencing_a_table_with_different_casing_still_resolves()
    {
        // SQLite identifiers are matched case-insensitively — a REFERENCES clause's table name (here
        // upper-cased) doesn't have to match sqlite_master's own stored casing (lower-case).
        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE TABLE IF NOT EXISTS nr_case_target (target_id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE IF NOT EXISTS nr_case_source (id INTEGER PRIMARY KEY, target_id INTEGER, " +
            "FOREIGN KEY (target_id) REFERENCES NR_CASE_TARGET)",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        ForeignKey fk = catalog.Tables.Single(t => t.Name == "nr_case_source").ForeignKeys.Single();
        fk.ReferencedTable.ShouldBe("NR_CASE_TARGET");
        fk.ReferencedColumn.ShouldBe("target_id");
    }

    [Fact]
    public async Task Foreign_key_referencing_a_table_with_no_declared_primary_key_falls_back_to_rowid()
    {
        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE TABLE IF NOT EXISTS nr_no_pk_target (label TEXT)", // no PRIMARY KEY at all
            "CREATE TABLE IF NOT EXISTS nr_no_pk_source (id INTEGER PRIMARY KEY, target_id INTEGER, " +
            "FOREIGN KEY (target_id) REFERENCES nr_no_pk_target)",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        SchemaCatalog catalog = await Explorer().GetCatalogAsync(Definition(), CancellationToken.None);

        ForeignKey fk = catalog.Tables.Single(t => t.Name == "nr_no_pk_source").ForeignKeys.Single();
        fk.ReferencedColumn.ShouldBe("rowid");
    }

    [Fact]
    public async Task Preview_returns_at_most_the_requested_number_of_rows()
    {
        await CreateFkSchemaAsync();

        CatalogTable regions = (await Explorer().GetCatalogAsync(Definition(), CancellationToken.None))
            .Tables.Single(t => t.Name == "nr_regions");
        TablePreview preview = await Explorer().PreviewTableAsync(Definition(), regions.Schema, "nr_regions", top: 1, CancellationToken.None);

        preview.Columns.ShouldContain("region_id");
        preview.Rows.Count.ShouldBeLessThanOrEqualTo(1);
    }
}
