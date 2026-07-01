using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Behavior under load: many reports generated at once must not interfere, each run is isolated
/// (its own temp dir, output and cancellation), and the pipeline reads the source page by page so
/// memory stays bounded (≈ concurrency × pageSize, not total rows). A synthetic lazy source keeps
/// each run at O(pageSize) so these assertions are about the pipeline, not the source.
/// </summary>
public class ConcurrencyTests
{
    private static ReportExecutionContext Exec(string? jobId = null) =>
        new(jobId ?? Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static CompiledReport BuildReport(string name, IBatchSource<Sale> source, DestinationSpec destination) =>
        new ReportBuilder<Sale>(name)
            .From(source)
            .WithPageSize(1000)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory("csv", "csv")))
            .UploadTo(destination)
            .Build();

    [Fact]
    public async Task Many_reports_run_concurrently_without_interfering()
    {
        const int concurrency = 32;
        const long rows = 2500; // three pages at pageSize 1000

        var destinations = new CapturingDestinationFactory[concurrency];
        var tasks = new Task<ReportRunResult>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            destinations[i] = new CapturingDestinationFactory();
            var report = BuildReport($"report-{i}", new LazySaleSource(rows), new DestinationSpec(destinations[i]));
            tasks[i] = ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.Status == ReportRunStatus.Completed);
        results.ShouldAllBe(r => r.Stats.RecordsRead == rows);
        results.ShouldAllBe(r => r.Stats.RecordsWritten == rows);

        // Each run produced exactly one file into its own destination — no cross-contamination.
        for (var i = 0; i < concurrency; i++)
            destinations[i].LastDestination!.Files.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_report_reads_the_source_page_by_page_bounding_memory()
    {
        var source = new LazySaleSource(5000);
        var report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(1000)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory("csv", "csv")))
            .Build();

        var result = await ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        result.Stats.RecordsWritten.ShouldBe(5000);
        // Read incrementally, one page at a time — the pipeline never asks for everything at once.
        source.PagesProduced.ShouldBe(5);
    }

    [Fact]
    public async Task Cancelling_some_runs_does_not_affect_others()
    {
        const int pairs = 8;
        var completing = new List<Task<ReportRunResult>>(pairs);
        var cancelling = new List<Task<ReportRunResult>>(pairs);
        var sources = new List<CancellationTokenSource>(pairs);

        for (var i = 0; i < pairs; i++)
        {
            var ok = BuildReport($"ok-{i}", new LazySaleSource(2000), new DestinationSpec(new CapturingDestinationFactory()));
            completing.Add(ReportRunner.ExecuteAsync(ok, Exec(), new EmptyServiceProvider(), CancellationToken.None));

            var cts = new CancellationTokenSource();
            sources.Add(cts);
            var slow = BuildReport($"cancel-{i}", new LazySaleSource(1_000_000, TimeSpan.FromMilliseconds(20)), new DestinationSpec(new CapturingDestinationFactory()));
            cancelling.Add(ReportRunner.ExecuteAsync(slow, Exec(), new EmptyServiceProvider(), cts.Token));
        }

        foreach (var cts in sources)
            await cts.CancelAsync();

        // The unaffected runs all complete successfully...
        var okResults = await Task.WhenAll(completing);
        okResults.ShouldAllBe(r => r.Status == ReportRunStatus.Completed);

        // ...while the cancelled ones each fault with a cancellation, independently.
        foreach (var task in cancelling)
            await Should.ThrowAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task Per_job_temp_directories_are_isolated_and_cleaned_up()
    {
        const int concurrency = 16;
        var jobIds = new string[concurrency];
        var tasks = new Task<ReportRunResult>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            jobIds[i] = Guid.NewGuid().ToString("N");
            var report = BuildReport($"r-{i}", new LazySaleSource(2000), new DestinationSpec(new CapturingDestinationFactory()));
            tasks[i] = ReportRunner.ExecuteAsync(report, Exec(jobIds[i]), new EmptyServiceProvider(), CancellationToken.None);
        }

        await Task.WhenAll(tasks);

        var baseTemp = Path.Join(Path.GetTempPath(), "neoreports");
        foreach (var jobId in jobIds)
            Directory.Exists(Path.Join(baseTemp, jobId)).ShouldBeFalse();
    }
}
