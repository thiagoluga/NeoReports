using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;

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

        var report = new ReportBuilder<Venda>("vendas-mensal")
            .From(Source.Sql(
                    _fixture.ConnectionString,
                    "SELECT Id, Cliente, Valor, Data FROM Vendas " +
                    "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
                .Keyset<Venda, long>(v => v.Id, pageSize: 1000))
            .Filter(v => v.Valor > 0)
            .Columns(
                Col<Venda, long>(v => v.Id, "ID Venda"),
                Col<Venda, string>(v => v.Cliente, "Cliente"),
                Col<Venda, decimal>(v => v.Valor, "Valor", format: "C2", culture: "pt-BR"),
                Col<Venda, DateTime>(v => v.Data, "Data Venda", format: "yyyy-MM-dd"))
            .To(Format.Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
            .UploadTo(Destination.Local(Path.Combine(_outDir, "{name}-{date:yyyy-MM-dd}.{ext}")))
            .Build();

        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        var result = await ReportRunner.ExecuteAsync(report, exec, new EmptyServices(), CancellationToken.None);

        result.Status.Should().Be(ReportRunStatus.Completed);
        // Filter removes rows where Valor == 0 (every 7th id).
        var expectedWritten = _fixture.SeededRows - _fixture.SeededRows / 7;
        result.Stats.RecordsRead.Should().Be(_fixture.SeededRows);
        result.Stats.RecordsWritten.Should().Be(expectedWritten);

        var upload = result.Uploads.Should().ContainSingle().Subject;
        upload.Success.Should().BeTrue();
        File.Exists(upload.RemotePath).Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(upload.RemotePath!, new UTF8Encoding(false));
        lines[0].Should().Be("ID Venda;Cliente;Valor;Data Venda");
        lines.Should().HaveCount(expectedWritten + 1); // header + data

        // First data row is Id=1 (Valor 1.5 > 0), formatted pt-BR.
        var valor = 1.5m.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
        lines[1].Should().Be($"1;C1;{valor};2026-01-01");
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
