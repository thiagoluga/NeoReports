using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// Epic A / A3: a report defined entirely in JSON config reads from SQL Server (keyset) and writes
/// CSV, end to end. The dynamic <c>"sql"</c> source materializes positional <see cref="ReportRecord"/>
/// rows by schema-column name, reusing the v1 keyset engine and the existing pipeline.
/// </summary>
public class DynamicConfigSqlTests : IClassFixture<SqlServerFixture>, IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "nr-dyn-sql", Guid.NewGuid().ToString("N"));

    public DynamicConfigSqlTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Config_drives_sql_source_to_csv_end_to_end()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var json = $$"""
        {
          "name": "sales-dyn",
          "pageSize": 1000,
          "source": {
            "type": "sql",
            "properties": {
              "connectionString": {{JsonString(_fixture.ConnectionString)}},
              "sql": "SELECT Id, Customer, Amount, Date FROM Sales WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id",
              "key": "Id",
              "pageSize": 1000
            }
          },
          "columns": [
            { "name": "Id", "type": "Integer", "displayName": "Sale ID", "nullable": false },
            { "name": "Customer", "type": "String" },
            { "name": "Amount", "type": "Decimal", "format": "C2", "culture": "pt-BR" },
            { "name": "Date", "type": "DateTime", "displayName": "Sale Date", "format": "yyyy-MM-dd" }
          ],
          "outputs": [ { "format": "csv" } ],
          "destinations": [ { "type": "local" } ]
        }
        """;

        var config = new JsonReportConfigParser().Parse(json);

        var services = new ServiceCollection();
        services.AddSqlConfigSource();
        services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        services.AddSingleton<IDestinationFactory>(new LocalDestinationFactory(Path.Combine(_outDir, "{name}.{ext}")));
        await using var provider = services.BuildServiceProvider();

        var report = ReportConfigCompiler.Compile(config, provider);
        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        var result = await ReportRunner.ExecuteAsync(report, exec, provider, CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsRead.ShouldBe(_fixture.SeededRows);
        result.Stats.RecordsWritten.ShouldBe(_fixture.SeededRows);

        var csvPath = Path.Combine(_outDir, "sales-dyn.csv");
        File.Exists(csvPath).ShouldBeTrue();

        var lines = await File.ReadAllLinesAsync(csvPath);
        lines[0].ShouldBe("Sale ID,Customer,Amount,Sale Date");
        lines.Length.ShouldBe(_fixture.SeededRows + 1);
        // First seeded row: Id=1, Customer=C1, Date=2026-01-01. The currency format is culture/ICU
        // dependent, so we assert the stable positional edges only.
        lines[1].ShouldStartWith("1,C1,");
        lines[1].ShouldEndWith(",2026-01-01");
    }

    [Fact]
    public void Provider_requires_connection_string_sql_and_key()
    {
        var provider = new SqlConfigSourceProvider();
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        using var services = new ServiceCollection().BuildServiceProvider();

        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("sql"), schema, services));

        var partial = new Dictionary<string, object?> { ["connectionString"] = "Server=.", ["sql"] = "SELECT 1" };
        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("sql", partial), schema, services));
    }

    private static string JsonString(string raw) => JsonSerializer.Serialize(raw);

    public void Dispose()
    {
        if (Directory.Exists(_outDir))
            Directory.Delete(_outDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
