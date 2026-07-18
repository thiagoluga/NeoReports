using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Formats.Xlsx;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Xlsx.UnitTests;

/// <summary>
/// A report defined entirely in JSON config reads from an XLSX file (ADR D59) and writes CSV, end to
/// end. The dynamic <c>"xlsx"</c> source materializes positional <see cref="ReportRecord"/> rows by
/// schema-column name, reusing the shared pipeline exactly like every other source's own
/// dynamic-config end-to-end test.
/// </summary>
public class DynamicConfigXlsxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-dyn-xlsx", Guid.NewGuid().ToString("N"));

    public DynamicConfigXlsxTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales-dyn", null, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Config_drives_xlsx_source_to_csv_output_end_to_end()
    {
        var inputSchema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("Customer", ColumnType.String),
            new ReportColumn("Amount", ColumnType.Decimal),
        });
        var inputPath = Path.Combine(_dir, "sales-in.xlsx");
        await using (var output = new FileStream(inputPath, FileMode.Create, FileAccess.Write))
        {
            var writer = new XlsxWriter(new XlsxOptions());
            await writer.InitializeAsync(new WriterContext(Exec(), output, inputSchema, null), CancellationToken.None);
            await writer.WriteRowsAsync(new object?[][]
            {
                new object?[] { 1L, "C1", 10.50m },
                new object?[] { 2L, "C2", 20.00m },
            }, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }
        var outDir = Path.Combine(_dir, "out");

        var json = $$"""
        {
          "name": "sales-dyn",
          "pageSize": 1000,
          "source": {
            "type": "xlsx",
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
        services.AddXlsxConfigSource();
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
        // Unlike CSV's decimal.Parse (which preserves a value's original text scale), an XLSX cell is
        // an IEEE double under the hood, so 10.50/20.00 legitimately come back as 10.5/20 — not a bug.
        lines[1].ShouldBe("1,C1,10.5");
        lines[2].ShouldBe("2,C2,20");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
