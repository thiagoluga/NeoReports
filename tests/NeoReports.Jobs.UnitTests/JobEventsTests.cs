using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Events;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;

namespace NeoReports.Jobs.UnitTests;

/// <summary>ADR D38: <see cref="ReportJobWorker"/>'s run-cancelled emission, and that the job event
/// log is fully opt-in (a host that never calls AddJobEvents/AddInMemoryJobEvents is unaffected).</summary>
public class JobEventsTests
{
    private static ServiceProvider BuildProvider(IBatchSource<Sale> source, bool withEvents)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReport<Sale>("sales", b => b
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new NullWriterFactory())));

        if (withEvents)
            services.AddInMemoryJobEvents();

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
    public async Task Cancelled_job_records_a_run_cancelled_event()
    {
        var source = new ControllableSource(totalRows: 100_000, pageSize: 10, perPageDelay: TimeSpan.FromMilliseconds(20));
        await using var provider = BuildProvider(source, withEvents: true);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();
        var events = provider.GetRequiredService<IJobEventStore>();

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);
        await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Running);
        await scheduler.CancelAsync(jobId, CancellationToken.None);
        await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Cancelled);

        var recorded = await events.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        recorded.ShouldContain(e => e.Type == JobEventTypes.RunCancelled);
    }

    [Fact]
    public async Task No_AddJobEvents_call_leaves_cancellation_unaffected()
    {
        var source = new ControllableSource(totalRows: 100_000, pageSize: 10, perPageDelay: TimeSpan.FromMilliseconds(20));
        await using var provider = BuildProvider(source, withEvents: false);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);
        await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Running);
        await scheduler.CancelAsync(jobId, CancellationToken.None);

        var job = await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Cancelled);
        job.Status.ShouldBe(ReportJobStatus.Cancelled);
    }

    [Fact]
    public async Task Completed_job_records_the_full_lifecycle()
    {
        var source = new ControllableSource(totalRows: 20, pageSize: 10, perPageDelay: TimeSpan.Zero);
        await using var provider = BuildProvider(source, withEvents: true);
        var scheduler = provider.GetRequiredService<IReportJobScheduler>();
        var events = provider.GetRequiredService<IJobEventStore>();

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), CancellationToken.None);
        await WaitForAsync(scheduler, jobId, s => s is ReportJobStatus.Completed);

        var recorded = await events.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        recorded.ShouldContain(e => e.Type == JobEventTypes.RunStarted);
        recorded.ShouldContain(e => e.Type == JobEventTypes.RunCompleted);
    }
}
