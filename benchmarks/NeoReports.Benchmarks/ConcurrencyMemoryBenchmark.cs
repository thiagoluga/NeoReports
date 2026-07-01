using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using static NeoReports.Core.Building.ReportColumns;

namespace NeoReports.Benchmarks;

/// <summary>
/// Memory under <b>concurrency</b>: runs <see cref="Concurrency"/> reports of
/// <see cref="RowsPerReport"/> rows at the same time (streaming CSV over the lazy
/// <see cref="SyntheticSource"/>). With <c>MemoryDiagnoser</c> the signal is that <b>Allocated</b>
/// scales roughly linearly with <see cref="Concurrency"/> — running many at once causes no
/// super-linear blow-up — and that peak live memory is bounded by ≈ <c>Concurrency × pageSize</c> by
/// design (each report holds one page at a time). Complements the single-report
/// <see cref="ReportMemoryBenchmark"/>.
/// </summary>
[MemoryDiagnoser]
public class ConcurrencyMemoryBenchmark
{
    private static readonly IServiceProvider Services = new EmptyServiceProvider();

    /// <summary>How many reports run at the same time.</summary>
    [Params(1, 8, 32)]
    public int Concurrency { get; set; }

    /// <summary>Rows per report (each report reads them page by page, one page live at a time).</summary>
    [Params(1_000_000L)]
    public long RowsPerReport { get; set; }

    [Benchmark(Description = "Concurrent CSV (streaming)")]
    public async Task<long> ConcurrentCsv()
    {
        var tasks = new Task<ReportRunResult>[Concurrency];
        for (var i = 0; i < Concurrency; i++)
            tasks[i] = RunAsync(BuildReport());

        ReportRunResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        long total = 0;
        foreach (ReportRunResult result in results)
            total += result.Stats.RecordsWritten;
        return total;
    }

    private CompiledReport BuildReport()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        var source = new SyntheticSource(RowsPerReport, schema);

        return new ReportBuilder<Venda>("bench")
            .From(source)
            .WithPageSize(1000)
            .Columns(
                Col<Venda, long>(v => v.Id, "ID Venda"),
                Col<Venda, string>(v => v.Cliente, "Cliente"),
                Col<Venda, decimal>(v => v.Valor, "Valor", format: "C2", culture: "pt-BR"),
                Col<Venda, DateTime>(v => v.Data, "Data Venda", format: "yyyy-MM-dd"))
            .To(Formats.Csv.Format.Csv(o => o.Delimiter(';')))
            .Build();
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
