using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class DeadlineTests
{
    private static CompiledReport Report(string name, TimeSpan? deadline, TimeSpan perPageDelay)
    {
        var builder = new ReportBuilder<Sale>(name)
            .From(new LazySaleSource(rowCount: 1_000_000, perPageDelay: perPageDelay))
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()));
        if (deadline is { } d)
            builder.Deadline(d);
        return builder.Build();
    }

    private static ReportRunner Runner(CompiledReport report)
    {
        var registry = new ReportRegistry();
        registry.Register(report);
        return new ReportRunner(registry, new EmptyServiceProvider(), NullLoggerFactory.Instance);
    }

    [Fact]
    public void Deadline_rejects_a_non_positive_value()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ReportBuilder<Sale>("r").Deadline(TimeSpan.Zero));
    }

    [Fact]
    public async Task Run_is_cancelled_when_it_exceeds_its_deadline()
    {
        // The source would take ~forever (1M rows, 5s per 10-row page); the 100ms deadline must stop it.
        var runner = Runner(Report("slow", deadline: TimeSpan.FromMilliseconds(100), perPageDelay: TimeSpan.FromSeconds(5)));

        var stopwatch = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync("slow"));
        stopwatch.Stop();

        // Stopped promptly by the deadline, not after a page delay (5s) or the whole source.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Run_without_a_deadline_is_not_cancelled()
    {
        // A quick report with no deadline completes normally (5 rows, no delay).
        var report = new ReportBuilder<Sale>("fast")
            .From(new LazySaleSource(rowCount: 5, perPageDelay: TimeSpan.Zero))
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        ReportRunResult result = await Runner(report).RunAsync("fast");
        result.Status.ShouldBe(ReportRunStatus.Completed);
    }
}
