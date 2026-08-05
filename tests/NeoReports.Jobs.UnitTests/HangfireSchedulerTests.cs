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
        var scheduler = new HangfireJobScheduler(client, store, new RecurringJobManager(storage));

        var jobId = await scheduler.EnqueueAsync(
            new ReportJobRequest("sales", new Dictionary<string, object?> { ["start"] = "2026-01-01" }), Ct);

        var tracked = await scheduler.GetAsync(jobId, Ct);
        tracked!.Status.ShouldBe(ReportJobStatus.Queued);
        tracked.ReportName.ShouldBe("sales");

        // The Hangfire enqueued set has exactly one job.
        var monitoring = storage.GetMonitoringApi();
        monitoring.EnqueuedCount("default").ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_deletes_the_background_job()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var scheduler = new HangfireJobScheduler(client, new InMemoryJobStore(), new RecurringJobManager(storage));

        var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("sales"), Ct);

        var cancelled = await scheduler.CancelAsync(jobId, Ct);
        cancelled.ShouldBeTrue();

        // Unknown job cannot be cancelled.
        (await scheduler.CancelAsync("does-not-exist", Ct)).ShouldBeFalse();
    }

    [Fact]
    public void The_invoker_is_pinned_to_a_single_attempt()
    {
        // Hangfire's default is to re-run a failed job 10 times. Report jobs fail deterministically
        // far more often than transiently, and each retry re-reads the whole dataset and flaps the
        // stored status Failed -> Running -> Failed, so one problem reads as ten failures. Retrying a
        // transient fault is the pipeline's own job: Polly retries a batch from its cursor (D6),
        // which is a far cheaper unit than the whole run. ADR D74.
        //
        // Asserted through the attribute because that is the whole mechanism — Hangfire reads it off
        // the type when the job is created, and there is no code path of ours to exercise instead.
        var attribute = typeof(HangfireReportJobInvoker)
            .GetCustomAttributes(typeof(global::Hangfire.AutomaticRetryAttribute), inherit: false)
            .Cast<global::Hangfire.AutomaticRetryAttribute>()
            .SingleOrDefault();

        attribute.ShouldNotBeNull("the invoker must pin its retry count; without it Hangfire applies its default of 10");
        attribute.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Invoker_runs_the_worker_to_completion()
    {
        var source = new ControllableSource(totalRows: 20, pageSize: 10, perPageDelay: TimeSpan.Zero);
        var destFactory = new CapturingDestinationFactory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReport<Sale>("sales", b => b
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
        var job = await store.CreateAsync(new ReportJobRequest("sales"), Ct);

        var invoker = provider.GetRequiredService<HangfireReportJobInvoker>();
        await invoker.ExecuteAsync(job.Id, "sales", JobParameters.Serialize(null), Ct);

        var finished = await store.GetAsync(job.Id, Ct);
        finished!.Status.ShouldBe(ReportJobStatus.Completed);
        finished.Stats.RecordsWritten.ShouldBe(20);
        destFactory.Last!.UploadedFiles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RegisterRecurring_creates_a_hangfire_recurring_job()
    {
        using var storage = new InMemoryStorage();
        JobStorage.Current = storage;
        var scheduler = new HangfireJobScheduler(new BackgroundJobClient(storage), new InMemoryJobStore(), new RecurringJobManager(storage));

        await scheduler.RegisterRecurringAsync("sales", "0 6 * * 1", Ct);

        var names = await scheduler.ListRegisteredNamesAsync(Ct);
        names.ShouldContain("sales");
    }

    [Fact]
    public async Task RemoveRecurring_removes_the_hangfire_recurring_job()
    {
        using var storage = new InMemoryStorage();
        JobStorage.Current = storage;
        var scheduler = new HangfireJobScheduler(new BackgroundJobClient(storage), new InMemoryJobStore(), new RecurringJobManager(storage));

        await scheduler.RegisterRecurringAsync("sales", "0 6 * * 1", Ct);
        await scheduler.RemoveRecurringAsync("sales", Ct);

        var names = await scheduler.ListRegisteredNamesAsync(Ct);
        names.ShouldNotContain("sales");
    }

    [Fact]
    public async Task GetNextOccurrence_computes_from_the_registered_cron()
    {
        using var storage = new InMemoryStorage();
        var scheduler = new HangfireJobScheduler(new BackgroundJobClient(storage), new InMemoryJobStore(), new RecurringJobManager(storage));

        await scheduler.RegisterRecurringAsync("sales", "* * * * *", Ct);
        var next = await scheduler.GetNextOccurrenceAsync("sales", Ct);

        next.ShouldNotBeNull();
        next!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1.1));
    }

    [Fact]
    public async Task Unregistered_report_has_no_next_occurrence()
    {
        using var storage = new InMemoryStorage();
        var scheduler = new HangfireJobScheduler(new BackgroundJobClient(storage), new InMemoryJobStore(), new RecurringJobManager(storage));

        (await scheduler.GetNextOccurrenceAsync("sales", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task RegisterRecurring_rejects_an_invalid_cron_expression()
    {
        using var storage = new InMemoryStorage();
        var scheduler = new HangfireJobScheduler(new BackgroundJobClient(storage), new InMemoryJobStore(), new RecurringJobManager(storage));

        await Should.ThrowAsync<ConfigurationException>(() => scheduler.RegisterRecurringAsync("sales", "garbage", Ct));
    }
}
