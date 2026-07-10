using NeoReports.Core.Events;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Events;

/// <summary>ADR D38: <see cref="FileJobEventStore"/>.</summary>
public class FileJobEventStoreTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "nr-d38-events-" + Guid.NewGuid().ToString("N"));

    private static JobEvent Event(string jobId, int sequence, string type = "page-completed") =>
        new(jobId, sequence, DateTimeOffset.UtcNow, type, null, null);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Unknown_job_returns_empty()
    {
        var store = new FileJobEventStore(new JobEventOptions { Directory = _dir });
        (await store.ListAsync("unknown", null, 100, 0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Append_then_list_returns_events_ascending_by_sequence()
    {
        var store = new FileJobEventStore(new JobEventOptions { Directory = _dir });
        await store.AppendAsync(Event("j1", 1), CancellationToken.None);
        await store.AppendAsync(Event("j1", 2), CancellationToken.None);
        await store.AppendAsync(Event("j1", 3), CancellationToken.None);

        var events = await store.ListAsync("j1", null, 100, 0, CancellationToken.None);
        events.Select(e => e.Sequence).ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task List_filters_by_type_and_honors_limit_offset()
    {
        var store = new FileJobEventStore(new JobEventOptions { Directory = _dir });
        await store.AppendAsync(Event("j1", 1, "retry"), CancellationToken.None);
        await store.AppendAsync(Event("j1", 2, "page-completed"), CancellationToken.None);
        await store.AppendAsync(Event("j1", 3, "retry"), CancellationToken.None);
        await store.AppendAsync(Event("j1", 4, "retry"), CancellationToken.None);

        var retries = await store.ListAsync("j1", "retry", limit: 1, offset: 1, CancellationToken.None);
        retries.Select(e => e.Sequence).ShouldBe(new[] { 3 });
    }

    [Fact]
    public async Task Cap_appends_truncated_marker_exactly_once_and_drops_further_events()
    {
        var store = new FileJobEventStore(new JobEventOptions { Directory = _dir, MaxEventsPerJob = 3 });
        for (var i = 1; i <= 6; i++)
            await store.AppendAsync(Event("j1", i), CancellationToken.None);

        var events = await store.ListAsync("j1", null, 100, 0, CancellationToken.None);
        events.Count.ShouldBe(4);
        events[3].Type.ShouldBe(JobEventTypes.EventsTruncated);
        events.Count(e => e.Type == JobEventTypes.EventsTruncated).ShouldBe(1);
    }

    [Fact]
    public async Task Events_survive_a_new_store_instance_on_the_same_directory()
    {
        var options = new JobEventOptions { Directory = _dir };
        await new FileJobEventStore(options).AppendAsync(Event("j1", 1), CancellationToken.None);

        var reopened = new FileJobEventStore(options);
        var events = await reopened.ListAsync("j1", null, 100, 0, CancellationToken.None);

        events.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_removes_the_jobs_file()
    {
        var options = new JobEventOptions { Directory = _dir };
        var store = new FileJobEventStore(options);
        await store.AppendAsync(Event("j1", 1), CancellationToken.None);

        await store.DeleteAsync("j1", CancellationToken.None);

        (await new FileJobEventStore(options).ListAsync("j1", null, 100, 0, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_of_unknown_job_does_not_throw() =>
        await Should.NotThrowAsync(() =>
            new FileJobEventStore(new JobEventOptions { Directory = _dir }).DeleteAsync("unknown", CancellationToken.None));
}
