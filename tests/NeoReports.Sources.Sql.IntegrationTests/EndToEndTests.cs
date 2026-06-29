using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.Sources.Sql.IntegrationTests;

public class EndToEndTests : IClassFixture<SqlServerFixture>, IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "nr-e2e", Guid.NewGuid().ToString("N"));

    public EndToEndTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Sql_to_csv_to_local_runs_end_to_end()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var report = new ReportBuilder<Sale>("monthly-sales")
            .From(Source.Sql(
                    _fixture.ConnectionString,
                    "SELECT Id, Customer, Amount, Date FROM Sales " +
                    "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
                .Keyset<Sale, long>(v => v.Id, pageSize: 1000))
            .Filter(v => v.Amount > 0)
            .Columns(
                Col<Sale, long>(v => v.Id, "Sale ID"),
                Col<Sale, string>(v => v.Customer, "Customer"),
                Col<Sale, decimal>(v => v.Amount, "Amount", format: "C2", culture: "pt-BR"),
                Col<Sale, DateTime>(v => v.Date, "Sale Date", format: "yyyy-MM-dd"))
            .To(Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
            .UploadTo(Destination.Local(Path.Combine(_outDir, "{name}-{date:yyyy-MM-dd}.{ext}")))
            .Build();

        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        var result = await ReportRunner.ExecuteAsync(report, exec, new EmptyServices(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        // Filter removes rows where Amount == 0 (every 7th id).
        var expectedWritten = _fixture.SeededRows - _fixture.SeededRows / 7;
        result.Stats.RecordsRead.ShouldBe(_fixture.SeededRows);
        result.Stats.RecordsWritten.ShouldBe(expectedWritten);

        var upload = result.Uploads.ShouldHaveSingleItem();
        upload.Success.ShouldBeTrue();
        File.Exists(upload.RemotePath).ShouldBeTrue();

        var lines = await File.ReadAllLinesAsync(upload.RemotePath!, new UTF8Encoding(false));
        lines[0].ShouldBe("Sale ID;Customer;Amount;Sale Date");
        lines.Length.ShouldBe(expectedWritten + 1); // header + data

        // First data row is Id=1 (Amount 1.5 > 0), formatted pt-BR.
        var amount = 1.5m.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
        lines[1].ShouldBe($"1;C1;{amount};2026-01-01");
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
