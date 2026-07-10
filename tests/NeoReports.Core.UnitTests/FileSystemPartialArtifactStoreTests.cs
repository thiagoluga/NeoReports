using NeoReports.Core.Artifacts;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>ADR D40: <see cref="FileSystemPartialArtifactStore"/>.</summary>
public sealed class FileSystemPartialArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "nr-d40-store-tests", Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken Ct = CancellationToken.None;

    private string MakeSourceFile(string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, "src-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, content);
        return path;
    }

    private FileSystemPartialArtifactStore MakeStore(TimeSpan? retention = null) =>
        new(new PartialArtifactOptions { Directory = _root, Retention = retention ?? TimeSpan.FromDays(7) });

    [Fact]
    public async Task Save_then_list_round_trips_file_and_mime()
    {
        var store = MakeStore();
        var src = MakeSourceFile("id\n1\n");

        await store.SaveAsync("job-1", src, "report.partial.csv", "text/csv", Ct);

        var partials = await store.ListAsync("job-1", Ct);
        var partial = partials.ShouldHaveSingleItem();
        partial.FileName.ShouldBe("report.partial.csv");
        partial.MimeType.ShouldBe("text/csv");
        partial.SizeBytes.ShouldBeGreaterThan(0);
        File.Exists(partial.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task List_excludes_mime_sidecar_files()
    {
        var store = MakeStore();
        await store.SaveAsync("job-2", MakeSourceFile("a"), "a.partial.csv", "text/csv", Ct);
        await store.SaveAsync("job-2", MakeSourceFile("b"), "b.partial.xlsx", "application/vnd.ms-excel", Ct);

        var partials = await store.ListAsync("job-2", Ct);

        partials.Count.ShouldBe(2);
        partials.Select(a => a.FileName).ShouldBe(new[] { "a.partial.csv", "b.partial.xlsx" }, ignoreOrder: true);
    }

    [Fact]
    public async Task List_unknown_job_returns_empty()
    {
        var store = MakeStore();
        (await store.ListAsync("does-not-exist", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_removes_all_partials_for_job()
    {
        var store = MakeStore();
        await store.SaveAsync("job-3", MakeSourceFile("x"), "x.partial.csv", "text/csv", Ct);

        await store.DeleteAsync("job-3", Ct);

        (await store.ListAsync("job-3", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_unknown_job_is_noop() =>
        await Should.NotThrowAsync(async () => await MakeStore().DeleteAsync("nope", Ct));

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public async Task Save_rejects_job_ids_that_escape_the_root(string jobId)
    {
        var store = MakeStore();
        var src = MakeSourceFile("x");

        await Should.ThrowAsync<ArgumentException>(
            async () => await store.SaveAsync(jobId, src, "x.partial.csv", "text/csv", Ct));
    }

    [Fact]
    public async Task Save_rejects_blank_job_id()
    {
        var store = MakeStore();
        var src = MakeSourceFile("x");

        await Should.ThrowAsync<ArgumentException>(
            async () => await store.SaveAsync("  ", src, "x.partial.csv", "text/csv", Ct));
    }

    [Fact]
    public void Ctor_rejects_null_options() =>
        Should.Throw<ArgumentNullException>(() => new FileSystemPartialArtifactStore(null!));

    [Fact]
    public async Task Save_prunes_job_directories_older_than_retention()
    {
        var store = MakeStore(TimeSpan.FromMilliseconds(1));
        await store.SaveAsync("expiring-job", MakeSourceFile("x"), "x.partial.csv", "text/csv", Ct);
        (await store.ListAsync("expiring-job", Ct)).ShouldHaveSingleItem();

        await Task.Delay(20);

        // Pruning is opportunistic (runs on the next save) — touch the store for a different job.
        await store.SaveAsync("other-job", MakeSourceFile("y"), "y.partial.csv", "text/csv", Ct);

        (await store.ListAsync("expiring-job", Ct)).ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
