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

namespace NeoReports.Sources.Csv.UnitTests;

/// <summary>
/// A report defined entirely in JSON config reads from a CSV file (keyset-free — the whole point of
/// authoring the CSV source as an <see cref="IStreamingSource{T}"/>, ADR D58) and writes CSV, end to
/// end. The dynamic <c>"csv"</c> source materializes positional <see cref="ReportRecord"/> rows by
/// schema-column name, reusing the shared pipeline exactly like every ADO provider's own
/// dynamic-config end-to-end test.
/// </summary>
public class DynamicConfigCsvTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-dyn-csv", Guid.NewGuid().ToString("N"));

    public DynamicConfigCsvTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Config_drives_csv_source_to_csv_output_end_to_end()
    {
        var inputPath = Path.Combine(_dir, "sales-in.csv");
        await File.WriteAllTextAsync(inputPath, "Id,Customer,Amount\r\n1,C1,10.50\r\n2,C2,20.00\r\n");
        var outDir = Path.Combine(_dir, "out");

        var json = $$"""
        {
          "name": "sales-dyn",
          "pageSize": 1000,
          "source": {
            "type": "csv",
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
        services.AddCsvConfigSource();
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
        lines[1].ShouldBe("1,C1,10.50");
        lines[2].ShouldBe("2,C2,20.00");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
