using NeoReports.Core.Events;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Events;

/// <summary>ADR D38: <see cref="InMemoryJobEventStore"/>.</summary>
public class InMemoryJobEventStoreTests
{
    private static JobEvent Event(string jobId, int sequence, string type = "page-completed") =>
        new(jobId, sequence, DateTimeOffset.UtcNow, type, null, null);

    [Fact]
    public async Task Unknown_job_returns_empty()
    {
        var store = new InMemoryJobEventStore();
        var events = await store.ListAsync("unknown", null, 100, 0, CancellationToken.None);
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Append_then_list_returns_events_ascending_by_sequence()
    {
        var store = new InMemoryJobEventStore();
        await store.AppendAsync(Event("j1", 2), CancellationToken.None);
        await store.AppendAsync(Event("j1", 1), CancellationToken.None);
        await store.AppendAsync(Event("j1", 3), CancellationToken.None);

        var events = await store.ListAsync("j1", null, 100, 0, CancellationToken.None);
        events.Select(e => e.Sequence).ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task List_filters_by_type()
    {
        var store = new InMemoryJobEventStore();
        await store.AppendAsync(Event("j1", 1, "retry"), CancellationToken.None);
        await store.AppendAsync(Event("j1", 2, "page-completed"), CancellationToken.None);
        await store.AppendAsync(Event("j1", 3, "retry"), CancellationToken.None);

        var events = await store.ListAsync("j1", "retry", 100, 0, CancellationToken.None);
        events.Select(e => e.Sequence).ShouldBe(new[] { 1, 3 });
    }

    [Fact]
    public async Task List_honors_limit_and_offset()
    {
        var store = new InMemoryJobEventStore();
        for (var i = 1; i <= 5; i++)
            await store.AppendAsync(Event("j1", i), CancellationToken.None);

        var page = await store.ListAsync("j1", null, limit: 2, offset: 1, CancellationToken.None);
        page.Select(e => e.Sequence).ShouldBe(new[] { 2, 3 });
    }

    [Fact]
    public async Task Cap_appends_truncated_marker_exactly_once_and_drops_further_events()
    {
        var store = new InMemoryJobEventStore(new JobEventOptions { MaxEventsPerJob = 3 });
        for (var i = 1; i <= 6; i++)
            await store.AppendAsync(Event("j1", i), CancellationToken.None);

        var events = await store.ListAsync("j1", null, 100, 0, CancellationToken.None);
        events.Count.ShouldBe(4); // 3 real + 1 truncation marker
        events[3].Type.ShouldBe(JobEventTypes.EventsTruncated);
        events.Count(e => e.Type == JobEventTypes.EventsTruncated).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_removes_all_events_for_the_job()
    {
        var store = new InMemoryJobEventStore();
        await store.AppendAsync(Event("j1", 1), CancellationToken.None);

        await store.DeleteAsync("j1", CancellationToken.None);

        var events = await store.ListAsync("j1", null, 100, 0, CancellationToken.None);
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_of_unknown_job_does_not_throw() =>
        await Should.NotThrowAsync(() => new InMemoryJobEventStore().DeleteAsync("unknown", CancellationToken.None));

    [Fact]
    public async Task Retention_prunes_a_jobs_events_after_it_expires()
    {
        var store = new InMemoryJobEventStore(new JobEventOptions { Retention = TimeSpan.FromMilliseconds(1) });
        await store.AppendAsync(Event("expiring", 1), CancellationToken.None);
        await Task.Delay(20);

        // Pruning is opportunistic (runs on the next append) — touch the store for a different job.
        await store.AppendAsync(Event("other", 1), CancellationToken.None);

        var events = await store.ListAsync("expiring", null, 100, 0, CancellationToken.None);
        events.ShouldBeEmpty();
    }
}
