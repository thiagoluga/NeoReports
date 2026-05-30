using global::Hangfire;
using global::Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Jobs;
using NeoReports.Jobs.Hangfire;
using Shouldly;
using Xunit;

namespace NeoReports.Jobs.UnitTests;

/// <summary>
/// Tests the Hangfire glue: the scheduler creates a tracked job and enqueues it into real Hangfire
/// storage, and cancellation deletes the background job. Execution semantics (worker → pipeline)
/// are covered by the in-memory lifecycle tests, which share the same <see cref="ReportJobWorker"/>;
/// the invoker is exercised directly here too.
/// </summary>
public class HangfireSchedulerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Enqueue_creates_tracked_job_and_hangfire_background_job()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var store = new InMemoryJobStore();
        var scheduler = new HangfireJobScheduler(client, store);

        var jobId = await scheduler.EnqueueAsync(
            new ReportJobRequest("vendas", new Dictionary<string, object?> { ["inicio"] = "2026-01-01" }), Ct);

        var tracked = await scheduler.GetAsync(jobId, Ct);
        tracked!.Status.ShouldBe(ReportJobStatus.Queued);
        tracked.ReportName.ShouldBe("vendas");

        // The Hangfire enqueued set has exactly one job.
        var monitoring = storage.GetMonitoringApi();
        monitoring.EnqueuedCount("default").ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_deletes_the_background_job()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var scheduler = new HangfireJobScheduler(client, new InMemoryJobStore());

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("vendas"), Ct);

        var cancelled = await scheduler.CancelAsync(jobId, Ct);
        cancelled.ShouldBeTrue();

        // Unknown job cannot be cancelled.
        (await scheduler.CancelAsync("does-not-exist", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Invoker_runs_the_worker_to_completion()
    {
        var source = new ControllableSource(totalRows: 20, pageSize: 10, perPageDelay: TimeSpan.Zero);
        var destFactory = new CapturingDestinationFactory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReport<Venda>("vendas", b => b
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new NullWriterFactory()))
            .UploadTo(new DestinationSpec(destFactory)));
        services.AddSingleton<IJobStore, InMemoryJobStore>();
        services.AddSingleton<ReportJobWorker>();
        services.AddSingleton<HangfireReportJobInvoker>();
        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IJobStore>();
        var job = await store.CreateAsync(new ReportJobRequest("vendas"), Ct);

        var invoker = provider.GetRequiredService<HangfireReportJobInvoker>();
        await invoker.ExecuteAsync(job.Id, "vendas", JobParameters.Serialize(null), Ct);

        var finished = await store.GetAsync(job.Id, Ct);
        finished!.Status.ShouldBe(ReportJobStatus.Completed);
        finished.Stats.RecordsWritten.ShouldBe(20);
        destFactory.Last!.UploadedFiles.ShouldHaveSingleItem();
    }
}
