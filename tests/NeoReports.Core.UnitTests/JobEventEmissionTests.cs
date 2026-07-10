using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Events;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D38: the runner's engine-side event emission. Complements <c>Events/*StoreTests</c> (store
/// mechanics) and <c>ResilienceTests</c> (retry/skip/abort behavior, unaffected by this feature).
/// </summary>
public class JobEventEmissionTests
{
    private static IReadOnlyList<Sale> Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec(string? jobId = null) =>
        new(jobId ?? Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static CompiledReport Build(
        FakeBatchSource<Sale> source, FakeWriterFactory writer, Action<ReportBuilder<Sale>>? extra = null)
    {
        var builder = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .Column(v => v.Customer, "Customer")
            .To(new OutputSpec(writer));

        extra?.Invoke(builder);
        return builder.Build();
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    [Fact]
    public async Task No_store_registered_leaves_the_run_unaffected()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1, 2) });
        var report = Build(source, new FakeWriterFactory());

        var result = await ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
    }

    [Fact]
    public async Task Happy_run_emits_the_full_lifecycle_in_order_with_cumulative_counters()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeBatchSource<Sale>(new[] { Page(1, 2), Page(3, 4, 5) });
        var report = Build(source, new FakeWriterFactory());

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);

        var events = await store.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        events.Select(e => e.Type).ShouldBe(new[]
        {
            JobEventTypes.RunStarted,
            JobEventTypes.PageCompleted,
            JobEventTypes.PageCompleted,
            JobEventTypes.OutputsFinalized,
            JobEventTypes.RunCompleted,
        });

        var pageEvents = events.Where(e => e.Type == JobEventTypes.PageCompleted).ToArray();
        pageEvents[0].Data!["recordsWritten"].ShouldBe("2");
        pageEvents[1].Data!["recordsWritten"].ShouldBe("5"); // cumulative across both pages
        events.Select(e => e.Sequence).ShouldBe(Enumerable.Range(1, events.Count));
    }

    [Fact]
    public async Task Restart_with_existing_events_emits_run_restarted()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        await store.AppendAsync(new JobEvent(jobId, 1, DateTimeOffset.UtcNow, JobEventTypes.RunStarted, null, null), CancellationToken.None);

        var report = Build(new FakeBatchSource<Sale>(new[] { Page(1) }), new FakeWriterFactory());
        await ReportRunner.ExecuteAsync(report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        var events = await store.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        events[1].Type.ShouldBe(JobEventTypes.RunRestarted);
    }

    [Fact]
    public async Task Retried_batch_emits_a_retry_event_with_page_and_attempt()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeBatchSource<Sale>(
            new[] { Page(1, 2) }, new Dictionary<int, int> { [1] = 2 });
        var report = Build(source, new FakeWriterFactory(), b => b.Retry(r => r.MaxAttempts(3).Constant(TimeSpan.Zero)));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.Completed);

        var events = await store.ListAsync(jobId, JobEventTypes.Retry, 100, 0, CancellationToken.None);
        events.Count.ShouldBe(2);
        events[0].Data!["page"].ShouldBe("1");
        events[0].Data!["attempt"].ShouldNotBeNullOrEmpty();
        events[0].Data!["exceptionType"].ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Skip_and_log_emits_batch_skipped()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory(2);
        var report = Build(source, writer, b => b.OnFailure(f => f.SkipBatchAndLog()));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.CompletedPartial);

        var events = await store.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        events.ShouldContain(e => e.Type == JobEventTypes.BatchSkipped && e.Data!["page"] == "2");
        events.Last().Type.ShouldBe(JobEventTypes.RunCompleted);
    }

    [Fact]
    public async Task Aborted_run_emits_run_failed_with_the_error_as_message()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory(throwOnBatch: 1);
        var report = Build(source, writer, b => b.OnFailure(f => f.AbortReport()));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.Failed);

        var events = await store.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        var failed = events.Last();
        failed.Type.ShouldBe(JobEventTypes.RunFailed);
        failed.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_store_that_throws_never_fails_the_run()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1) });
        var report = Build(source, new FakeWriterFactory());

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(), new SingleServiceProvider(new ThrowingJobEventStore()), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
    }

    private sealed class ThrowingJobEventStore : IJobEventStore
    {
        public Task AppendAsync(JobEvent jobEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store is down");

        public Task<IReadOnlyList<JobEvent>> ListAsync(string jobId, string? type, int limit, int offset, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store is down");

        public Task DeleteAsync(string jobId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store is down");
    }
}
