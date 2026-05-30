using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using static NeoReports.Core.Building.ReportColumns;

namespace NeoReports.Benchmarks;

/// <summary>
/// Proves CA-3 (constant memory). The benchmark runs the full pipeline over a synthetic source of
/// <see cref="RowCount"/> rows. With <c>MemoryDiagnoser</c>, the key signal is that the
/// <b>Allocated</b> column scales <i>linearly</i> with <see cref="RowCount"/> (constant
/// allocation per row, no super-linear growth) and that the working set does not grow with the
/// total — i.e. nothing buffers the whole report. CSV is fully streaming; XLSX is included for
/// contrast (ClosedXML builds the workbook in memory by design — see ADR D14).
/// </summary>
[MemoryDiagnoser]
public class ReportMemoryBenchmark
{
    private static readonly IServiceProvider Services = new EmptyServiceProvider();

    /// <summary>Row counts spanning an order of magnitude so per-row allocation is observable.</summary>
    [Params(100_000L, 1_000_000L)]
    public long RowCount { get; set; }

    private CompiledReport _csvReport = null!;
    private CompiledReport _xlsxReport = null!;

    [GlobalSetup]
    public void Setup()
    {
        _csvReport = BuildReport(Formats.Csv.Format.Csv(o => o.Delimiter(';')));
        _xlsxReport = BuildReport(Formats.Xlsx.Format.Xlsx(o => o.SheetName("Vendas")));
    }

    private CompiledReport BuildReport(OutputSpec output)
    {
        // The schema the synthetic source declares is not consumed by the pipeline (the builder's
        // columns drive projection), so a placeholder is fine.
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        var source = new SyntheticSource(RowCount, schema);

        return new ReportBuilder<Venda>("bench")
            .From(source)
            .WithPageSize(1000)
            .Columns(
                Col<Venda, long>(v => v.Id, "ID Venda"),
                Col<Venda, string>(v => v.Cliente, "Cliente"),
                Col<Venda, decimal>(v => v.Valor, "Valor", format: "C2", culture: "pt-BR"),
                Col<Venda, DateTime>(v => v.Data, "Data Venda", format: "yyyy-MM-dd"))
            .To(output)
            .Build();
    }

    [Benchmark(Description = "CSV (streaming)")]
    public async Task<long> Csv()
    {
        var result = await RunAsync(_csvReport).ConfigureAwait(false);
        return result.Stats.RecordsWritten;
    }

    [Benchmark(Description = "XLSX (ClosedXML, in-memory)")]
    public async Task<long> Xlsx()
    {
        var result = await RunAsync(_xlsxReport).ConfigureAwait(false);
        return result.Stats.RecordsWritten;
    }

    private static Task<ReportRunResult> RunAsync(CompiledReport report)
    {
        var execution = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        return ReportRunner.ExecuteAsync(report, execution, Services, CancellationToken.None);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
