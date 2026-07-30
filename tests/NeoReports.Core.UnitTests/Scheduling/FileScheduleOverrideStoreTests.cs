using NeoReports.Core.Scheduling;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

/// <summary>ADR D41: <see cref="FileScheduleOverrideStore"/> save/get/remove/list, including the tombstone.</summary>
public class FileScheduleOverrideStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "nr-schedule-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_then_get_roundtrips_a_cron_override()
    {
        var store = new FileScheduleOverrideStore(_directory);

        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);

        var entry = await store.GetAsync("alpha", CancellationToken.None);
        entry.ShouldBe(new ScheduleOverrideEntry("0 6 * * 1"));
    }

    [Fact]
    public async Task Save_a_tombstone_roundtrips_a_null_cron()
    {
        var store = new FileScheduleOverrideStore(_directory);

        await store.SaveAsync("alpha", new ScheduleOverrideEntry(null), CancellationToken.None);

        var entry = await store.GetAsync("alpha", CancellationToken.None);
        entry.ShouldNotBeNull();
        entry!.Cron.ShouldBeNull();
    }

    [Fact]
    public async Task Get_unknown_report_returns_null()
    {
        var store = new FileScheduleOverrideStore(_directory);
        (await store.GetAsync("missing", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Save_overwrites_an_existing_entry()
    {
        var store = new FileScheduleOverrideStore(_directory);

        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);
        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 0 * * *"), CancellationToken.None);

        var entry = await store.GetAsync("alpha", CancellationToken.None);
        entry.ShouldBe(new ScheduleOverrideEntry("0 0 * * *"));
    }

    [Fact]
    public async Task Remove_deletes_the_entry_entirely()
    {
        var store = new FileScheduleOverrideStore(_directory);
        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);

        (await store.RemoveAsync("alpha", CancellationToken.None)).ShouldBeTrue();
        (await store.GetAsync("alpha", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Remove_unknown_report_returns_false()
    {
        var store = new FileScheduleOverrideStore(_directory);
        (await store.RemoveAsync("missing", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_returns_every_stored_entry()
    {
        var store = new FileScheduleOverrideStore(_directory);
        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);
        await store.SaveAsync("beta", new ScheduleOverrideEntry(null), CancellationToken.None);

        var listed = await store.ListAsync(CancellationToken.None);
        listed.Count.ShouldBe(2);
        listed.ShouldContain(e => e.ReportName == "alpha" && e.Entry.Cron == "0 6 * * 1");
        listed.ShouldContain(e => e.ReportName == "beta" && e.Entry.Cron == null);
    }

    [Fact]
    public async Task List_on_a_missing_directory_returns_empty()
    {
        var store = new FileScheduleOverrideStore(_directory);
        (await store.ListAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Invalid_report_name_throws(string invalidName)
    {
        var store = new FileScheduleOverrideStore(_directory);
        await Should.ThrowAsync<ArgumentException>(
            () => store.SaveAsync(invalidName, new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None));
    }

    [Fact]
    public async Task A_successful_save_leaves_no_temp_file_behind()
    {
        var store = new FileScheduleOverrideStore(_directory);

        await store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None);

        // The write is staged through a unique temp file and moved into place; nothing may linger
        // (a stray temp would also have to stay out of ListAsync's "*.json" enumeration).
        Directory.EnumerateFiles(_directory, "*.tmp").ShouldBeEmpty();
        (await store.ListAsync(CancellationToken.None)).Select(x => x.ReportName).ShouldBe(new[] { "alpha" });
    }

    [Fact]
    public async Task A_failed_save_cleans_up_its_temp_file_and_surfaces_the_error()
    {
        var store = new FileScheduleOverrideStore(_directory);
        Directory.CreateDirectory(_directory);
        // A directory sitting where the store wants its file makes the final move fail *after* the
        // temp file has been written — the one path that must not leave an orphan behind.
        Directory.CreateDirectory(Path.Join(_directory, "alpha.json"));

        // The exact type is the OS's business (Windows raises UnauthorizedAccessException here, other
        // platforms an IOException); what matters is that the failure surfaces rather than being
        // swallowed by the cleanup.
        await Should.ThrowAsync<Exception>(
            () => store.SaveAsync("alpha", new ScheduleOverrideEntry("0 6 * * 1"), CancellationToken.None));

        Directory.EnumerateFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
