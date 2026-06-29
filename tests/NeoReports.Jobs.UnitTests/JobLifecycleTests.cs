using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Jobs;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;

namespace NeoReports.Jobs.UnitTests;

/// <summary>
/// End-to-end job lifecycle through the in-process scheduler: the real worker + pipeline driven by
/// controllable fakes. Covers CA-15 (cancellation) and CA-16 (idempotent restart / no partial
/// publish), plus the queued→running→completed and failed paths.
/// </summary>
public class JobLifecycleTests
{
    private static ServiceProvider BuildProvider(
        IBatchSource<Sale> source,
        out CapturingDestinationFactory destinationFactory,
        Action<ReportBuilder<Sale>>? extra = null)
    {
        var destFactory = new CapturingDestinationFactory();
        destinationFactory = destFactory;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReport<Sale>("sales", b =>
        {
            b.From(source)
                .WithPageSize(10)
                .Column(v => v.Id, "Id")
                .To(new OutputSpec(new NullWriterFactory()))
                .UploadTo(new DestinationSpec(destFactory));
            extra?.Invoke(b);
        });
        services.AddNeoReportsInMemoryJobs();

        return services.BuildServiceProvider();
    }

    private static async Task<ReportJob> WaitForAsync(
        IReportJobScheduler scheduler, string jobId, Func<ReportJobStatus, bool> predicate, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var job = await scheduler.GetAsync(jobId, CancellationToken.None);
            if (job is not null && predicate(job.Status))
                return job;
            await Task.Delay(20);
        }

        var last = await scheduler.GetAsync(jobId, CancellationToken.None);
        throw new TimeoutException($"Job {jobId} did not reach the expected status in time (last: {last?.Status}).");
    }

    [Fact]
    public async Task Queued_to_running_to_completed()
    {
        var source = new ControllableSource(totalRows: 50, pageSize: 10, perPageDelay: TimeSpan.Zero);
        await using var provider = BuildProvider(source, out var dest);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);

        var job = await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Completed);
        job.Status.ShouldBe(ReportJobStatus.Completed);
        job.StartedAt.ShouldNotBeNull();
        job.CompletedAt.ShouldNotBeNull();
        job.Stats.RecordsWritten.ShouldBe(50);
        dest.Last!.UploadedFiles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Cancellation_moves_job_to_cancelled()
    {
        // Long-running: 20ms per 10-row page over 100k rows ⇒ ample time to cancel mid-run.
        var source = new ControllableSource(totalRows: 100_000, pageSize: 10, perPageDelay: TimeSpan.FromMilliseconds(20));
        await using var provider = BuildProvider(source, out var dest);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);
        await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Running);

        var accepted = await scheduler.CancelAsync(jobId, CancellationToken.None);
        accepted.ShouldBeTrue();

        var job = await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Cancelled);
        job.Status.ShouldBe(ReportJobStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelled_run_publishes_nothing_and_a_fresh_run_completes()
    {
        // CA-16: a cancelled (≈crashed) job must not leave a partial file at the destination, and a
        // restart from zero must complete cleanly.
        var slow = new ControllableSource(totalRows: 100_000, pageSize: 10, perPageDelay: TimeSpan.FromMilliseconds(20));
        await using var provider = BuildProvider(slow, out var dest);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();

        var cancelledId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);
        await WaitForAsync(scheduler, cancelledId, s => s is ReportJobStatus.Running);
        await scheduler.CancelAsync(cancelledId, CancellationToken.None);
        await WaitForAsync(scheduler, cancelledId, s => s is ReportJobStatus.Cancelled);

        // Upload happens only after a fully successful run, so the cancelled job published nothing.
        (dest.Last?.UploadedFiles.Count ?? 0).ShouldBe(0);
        // Temp staging directory for the job is cleaned up.
        Directory.Exists(Path.Combine(Path.GetTempPath(), "neoreports", cancelledId)).ShouldBeFalse();

        // A fresh, fast run completes and publishes exactly one file.
        var fast = new ControllableSource(totalRows: 30, pageSize: 10, perPageDelay: TimeSpan.Zero);
        await using var provider2 = BuildProvider(fast, out var dest2);
        var scheduler2 = provider2.GetRequiredService<IReportJobScheduler>();

        var okId = await scheduler2.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);
        var done = await WaitForAsync(scheduler2, okId, s => s is ReportJobStatus.Completed);
        done.Stats.RecordsWritten.ShouldBe(30);
        dest2.Last!.UploadedFiles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Source_failure_moves_job_to_failed()
    {
        await using var provider = BuildProvider(new ThrowingSource(), out _);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);

        var job = await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Failed);
        job.Status.ShouldBe(ReportJobStatus.Failed);
        job.Error.ShouldNotBeNullOrEmpty();
    }
}
