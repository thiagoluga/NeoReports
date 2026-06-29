using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;

namespace NeoReports.Sources.Sql.IntegrationTests;

public class MultiOutputE2ETests : IClassFixture<SqlServerFixture>, IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "nr-multi", Guid.NewGuid().ToString("N"));

    public MultiOutputE2ETests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Csv_and_xlsx_are_generated_reading_the_source_once()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var report = new ReportBuilder<Sale>("sales-multi")
            .From(Source.Sql(
                    _fixture.ConnectionString,
                    "SELECT Id, Customer, Amount, Date FROM Sales " +
                    "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
                .Keyset<Sale, long>(v => v.Id, pageSize: 1000))
            .Column(v => v.Id, "Sale ID")
            .Column(v => v.Customer, "Customer")
            .Column(v => v.Amount, "Amount", format: "C2", culture: "pt-BR")
            .Column(v => v.Date, "Sale Date", format: "yyyy-MM-dd")
            .To(Csv(o => o.Delimiter(';')))
            .To(Xlsx(o => o.SheetName("Sales").AutoFilter()))
            .UploadTo(Destination.Local(Path.Combine(_outDir, "{name}.{ext}")))
            .Build();

        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        var result = await ReportRunner.ExecuteAsync(report, exec, new EmptyServices(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);

        // Single pass: the source is read once and every row is fed to BOTH outputs. RecordsWritten
        // counts distinct rows (not per-output), so it equals the row count; the proof that both
        // formats received all rows is the file contents asserted below.
        result.Stats.RecordsRead.ShouldBe(_fixture.SeededRows);
        result.Stats.RecordsWritten.ShouldBe(_fixture.SeededRows);
        result.Uploads.Count.ShouldBe(2);
        result.Uploads.ShouldAllBe(u => u.Success);

        var csvPath = Path.Combine(_outDir, "sales-multi.csv");
        var xlsxPath = Path.Combine(_outDir, "sales-multi.xlsx");
        File.Exists(csvPath).ShouldBeTrue();
        File.Exists(xlsxPath).ShouldBeTrue();

        // CSV: header + all data rows.
        var csvLines = await File.ReadAllLinesAsync(csvPath);
        csvLines[0].ShouldBe("Sale ID;Customer;Amount;Sale Date");
        csvLines.Length.ShouldBe(_fixture.SeededRows + 1);

        // XLSX: named sheet, auto-filter, native types, header + data rows.
        using var wb = new XLWorkbook(xlsxPath);
        var ws = wb.Worksheet("Sales");
        ws.AutoFilter.IsEnabled.ShouldBeTrue();
        ws.Cell(1, 1).GetString().ShouldBe("Sale ID");
        ws.Cell(2, 1).GetDouble().ShouldBe(1);
        ws.LastRowUsed()!.RowNumber().ShouldBe(_fixture.SeededRows + 1);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_outDir))
            Directory.Delete(_outDir, recursive: true);
    }
}
