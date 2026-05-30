using NeoReports.Abstractions;
using NeoReports.Jobs;
using Shouldly;
using Xunit;

namespace NeoReports.Jobs.UnitTests;

public class InMemoryJobStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Create_starts_queued_with_id_and_timestamp()
    {
        var store = new InMemoryJobStore();

        var job = await store.CreateAsync(new ReportJobRequest("vendas", requestedBy: "alice"), Ct);

        job.Id.ShouldNotBeNullOrWhiteSpace();
        job.ReportName.ShouldBe("vendas");
        job.Status.ShouldBe(ReportJobStatus.Queued);
        job.RequestedBy.ShouldBe("alice");
        job.StartedAt.ShouldBeNull();
        job.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Status_transitions_set_started_and_completed_timestamps()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(new ReportJobRequest("vendas"), Ct);

        await store.UpdateStatusAsync(job.Id, ReportJobStatus.Running, null, Ct);
        var running = await store.GetAsync(job.Id, Ct);
        running!.Status.ShouldBe(ReportJobStatus.Running);
        running.StartedAt.ShouldNotBeNull();
        running.CompletedAt.ShouldBeNull();

        await store.UpdateStatusAsync(job.Id, ReportJobStatus.Completed, null, Ct);
        var done = await store.GetAsync(job.Id, Ct);
        done!.Status.ShouldBe(ReportJobStatus.Completed);
        done.StartedAt.ShouldNotBeNull();
        done.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Update_stats_is_persisted()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(new ReportJobRequest("vendas"), Ct);

        await store.UpdateStatsAsync(job.Id, new JobStats(RecordsRead: 10, RecordsWritten: 9), Ct);

        var updated = await store.GetAsync(job.Id, Ct);
        updated!.Stats.RecordsRead.ShouldBe(10);
        updated.Stats.RecordsWritten.ShouldBe(9);
    }

    [Fact]
    public async Task List_filters_by_status_and_orders_newest_first()
    {
        var store = new InMemoryJobStore();
        var a = await store.CreateAsync(new ReportJobRequest("a"), Ct);
        var b = await store.CreateAsync(new ReportJobRequest("b"), Ct);
        await store.UpdateStatusAsync(b.Id, ReportJobStatus.Running, null, Ct);

        var running = await store.ListAsync(new JobQuery { Status = ReportJobStatus.Running }, Ct);
        running.ShouldHaveSingleItem().Id.ShouldBe(b.Id);

        var all = await store.ListAsync(new JobQuery(), Ct);
        all.Count.ShouldBe(2);
        all.Select(j => j.Id).ShouldContain(a.Id);
    }

    [Fact]
    public async Task Get_unknown_job_returns_null()
    {
        var store = new InMemoryJobStore();
        (await store.GetAsync("nope", Ct)).ShouldBeNull();
    }
}
