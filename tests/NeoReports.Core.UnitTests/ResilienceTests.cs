using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class ResilienceTests
{
    private static IReadOnlyList<Venda> Page(params long[] ids) =>
        ids.Select(id => new Venda(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static Task<ReportRunResult> Run(CompiledReport report) =>
        ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

    private static CompiledReport Build(
        FakeBatchSource<Venda> source,
        FakeWriterFactory writer,
        Action<ReportBuilder<Venda>>? extra = null)
    {
        var builder = new ReportBuilder<Venda>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .Column(v => v.Cliente, "Cliente")
            .To(new OutputSpec(writer));

        extra?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task Transient_read_failure_is_retried_and_report_completes()
    {
        var source = new FakeBatchSource<Venda>(
            new[] { Page(1, 2), Page(3, 4) },
            new Dictionary<int, int> { [1] = 2 });
        var writer = new FakeWriterFactory();

        var report = Build(source, writer, b => b.Retry(r => r.MaxAttempts(3).Constant(TimeSpan.Zero)));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.Retries.ShouldBe(2);
        writer.LastWriter!.Rows.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Abort_strategy_fails_report_on_definitive_failure()
    {
        var source = new FakeBatchSource<Venda>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory(throwOnBatch: 1);

        var report = Build(source, writer, b => b.OnFailure(f => f.AbortReport()));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Skip_strategy_skips_failed_batch_and_marks_partial()
    {
        var source = new FakeBatchSource<Venda>(new[] { Page(1), Page(2), Page(3) });
        var writer = new FakeWriterFactory(2);

        var report = Build(source, writer, b => b.OnFailure(f => f.SkipBatchAndLog()));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.CompletedPartial);
        result.SkippedBatches.ShouldBe(1);
        writer.LastWriter!.Rows.Select(r => (long)r[0]!).ShouldBe(new long[] { 1, 3 });
    }

    [Fact]
    public async Task Threshold_aborts_even_in_skip_mode()
    {
        var source = new FakeBatchSource<Venda>(new[] { Page(1), Page(2), Page(3), Page(4) });
        var writer = new FakeWriterFactory(1, 2, 3);

        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3))));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.SkippedBatches.ShouldBe(2);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("threshold");
    }
}
