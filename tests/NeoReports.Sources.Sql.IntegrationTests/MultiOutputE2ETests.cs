using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
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

        var report = new ReportBuilder<Venda>("vendas-multi")
            .From(Source.Sql(
                    _fixture.ConnectionString,
                    "SELECT Id, Cliente, Valor, Data FROM Vendas " +
                    "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
                .Keyset<Venda, long>(v => v.Id, pageSize: 1000))
            .Column(v => v.Id, "ID Venda")
            .Column(v => v.Cliente, "Cliente")
            .Column(v => v.Valor, "Valor", format: "C2", culture: "pt-BR")
            .Column(v => v.Data, "Data Venda", format: "yyyy-MM-dd")
            .To(Csv(o => o.Delimiter(';')))
            .To(Xlsx(o => o.SheetName("Vendas").AutoFilter()))
            .UploadTo(Destination.Local(Path.Combine(_outDir, "{name}.{ext}")))
            .Build();

        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        var result = await ReportRunner.ExecuteAsync(report, exec, new EmptyServices(), CancellationToken.None);

        result.Status.Should().Be(ReportRunStatus.Completed);

        // Single pass: the source is read once and every row is fed to BOTH outputs. RecordsWritten
        // counts distinct rows (not per-output), so it equals the row count; the proof that both
        // formats received all rows is the file contents asserted below.
        result.Stats.RecordsRead.Should().Be(_fixture.SeededRows);
        result.Stats.RecordsWritten.Should().Be(_fixture.SeededRows);
        result.Uploads.Should().HaveCount(2);
        result.Uploads.Should().OnlyContain(u => u.Success);

        var csvPath = Path.Combine(_outDir, "vendas-multi.csv");
        var xlsxPath = Path.Combine(_outDir, "vendas-multi.xlsx");
        File.Exists(csvPath).Should().BeTrue();
        File.Exists(xlsxPath).Should().BeTrue();

        // CSV: header + all data rows.
        var csvLines = await File.ReadAllLinesAsync(csvPath);
        csvLines[0].Should().Be("ID Venda;Cliente;Valor;Data Venda");
        csvLines.Should().HaveCount(_fixture.SeededRows + 1);

        // XLSX: named sheet, auto-filter, native types, header + data rows.
        using var wb = new XLWorkbook(xlsxPath);
        var ws = wb.Worksheet("Vendas");
        ws.AutoFilter.IsEnabled.Should().BeTrue();
        ws.Cell(1, 1).GetString().Should().Be("ID Venda");
        ws.Cell(2, 1).GetDouble().Should().Be(1);
        ws.LastRowUsed()!.RowNumber().Should().Be(_fixture.SeededRows + 1);
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
