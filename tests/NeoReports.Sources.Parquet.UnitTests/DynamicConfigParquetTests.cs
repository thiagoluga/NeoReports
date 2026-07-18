using System.Globalization;
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

namespace NeoReports.Sources.Parquet.UnitTests;

/// <summary>
/// A report defined entirely in JSON config reads from a Parquet file (ADR D60) and writes CSV, end to
/// end. The dynamic <c>"parquet"</c> source materializes positional <see cref="ReportRecord"/> rows by
/// schema-column name, reusing the shared pipeline exactly like every other source's own
/// dynamic-config end-to-end test.
/// </summary>
public sealed class DynamicConfigParquetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-dyn-parquet", Guid.NewGuid().ToString("N"));

    public DynamicConfigParquetTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Config_drives_parquet_source_to_csv_output_end_to_end()
    {
        var inputPath = Path.Combine(_dir, "sales-in.parquet");
        await ParquetTestFile.WriteFileAsync(inputPath, new[]
        {
            new WideRow { Id = 1, Customer = "C1", Amount = 10.5m, Note = null },
            new WideRow { Id = 2, Customer = "C2", Amount = 20.25m, Note = null },
        });
        var outDir = Path.Combine(_dir, "out");

        var json = $$"""
        {
          "name": "sales-dyn",
          "pageSize": 1000,
          "source": {
            "type": "parquet",
            "properties": {
              "path": {{JsonSerializer.Serialize(inputPath)}}
            }
          },
          "columns": [
            { "name": "Id", "type": "Integer", "displayName": "Sale ID", "nullable": false },
            { "name": "Customer", "type": "String" },
            { "name": "Amount", "type": "Decimal" }
          ],
          "outputs": [ { "format": "csv" } ],
          "destinations": [ { "type": "local" } ]
        }
        """;

        var config = new JsonReportConfigParser().Parse(json);

        var services = new ServiceCollection();
        services.AddParquetConfigSource();
        services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        services.AddSingleton<IDestinationFactory>(new LocalDestinationFactory(Path.Combine(outDir, "{name}.{ext}")));
        await using var provider = services.BuildServiceProvider();

        var report = ReportConfigCompiler.Compile(config, provider);
        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        var result = await ReportRunner.ExecuteAsync(report, exec, provider, CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsRead.ShouldBe(2);
        result.Stats.RecordsWritten.ShouldBe(2);

        var outputPath = Path.Combine(outDir, "sales-dyn.csv");
        File.Exists(outputPath).ShouldBeTrue();
        var lines = await File.ReadAllLinesAsync(outputPath);
        lines[0].ShouldBe("Sale ID,Customer,Amount");

        // Parse the value columns rather than asserting exact decimal text: Parquet stores decimals at a
        // fixed scale, so the rendered trailing zeros are an implementation detail, not part of the value.
        var row1 = lines[1].Split(',');
        row1[0].ShouldBe("1");
        row1[1].ShouldBe("C1");
        decimal.Parse(row1[2], CultureInfo.InvariantCulture).ShouldBe(10.5m);

        var row2 = lines[2].Split(',');
        row2[0].ShouldBe("2");
        decimal.Parse(row2[2], CultureInfo.InvariantCulture).ShouldBe(20.25m);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
