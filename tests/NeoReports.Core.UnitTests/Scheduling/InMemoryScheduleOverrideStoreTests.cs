using NeoReports.Core.Scheduling;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

/// <summary>ADR D41: <see cref="InMemoryScheduleOverrideStore"/> — same contract as the file-backed twin.</summary>
public class InMemoryScheduleOverrideStoreTests
{
    [Fact]
    public async Task Save_then_get_roundtrips()
    {
        var store = new InMemoryScheduleOverrideStore();
        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);

        (await store.GetAsync("alpha", CancellationToken.None)).ShouldBe(new ScheduleOverrideEntry("0 6 * * 1"));
    }

    [Fact]
    public async Task Remove_deletes_the_entry()
    {
        var store = new InMemoryScheduleOverrideStore();
        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);

        (await store.RemoveAsync("alpha", CancellationToken.None)).ShouldBeTrue();
        (await store.GetAsync("alpha", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task List_returns_every_entry()
    {
        var store = new InMemoryScheduleOverrideStore();
        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);
        await store.SaveAsync("beta", new ScheduleOverrideEntry(null), CancellationToken.None);

        (await store.ListAsync(CancellationToken.None)).Count.ShouldBe(2);
    }
}
