using NeoReports.Core.Artifacts;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public sealed class FileSystemArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr-artifact-tests", Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken Ct = CancellationToken.None;

    private string MakeSourceFile(string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Save_then_list_round_trips_file_and_mime()
    {
        var store = new FileSystemArtifactStore(_root);
        var src = MakeSourceFile("id;name\n1;Ana\n");

        await store.SaveAsync("job-1", src, "vendas.csv", "text/csv", Ct);

        var artifacts = await store.ListAsync("job-1", Ct);
        var artifact = artifacts.ShouldHaveSingleItem();
        artifact.FileName.ShouldBe("vendas.csv");
        artifact.MimeType.ShouldBe("text/csv");
        artifact.SizeBytes.ShouldBeGreaterThan(0);
        File.Exists(artifact.Path).ShouldBeTrue();
        (await File.ReadAllTextAsync(artifact.Path)).ShouldBe("id;name\n1;Ana\n");
    }

    [Fact]
    public async Task List_excludes_mime_sidecar_files()
    {
        var store = new FileSystemArtifactStore(_root);
        await store.SaveAsync("job-2", MakeSourceFile("a"), "a.csv", "text/csv", Ct);
        await store.SaveAsync("job-2", MakeSourceFile("b"), "b.xlsx", "application/vnd.ms-excel", Ct);

        var artifacts = await store.ListAsync("job-2", Ct);

        artifacts.Count.ShouldBe(2);
        artifacts.Select(a => a.FileName).ShouldBe(new[] { "a.csv", "b.xlsx" }, ignoreOrder: true);
    }

    [Fact]
    public async Task List_unknown_job_returns_empty()
    {
        var store = new FileSystemArtifactStore(_root);
        (await store.ListAsync("does-not-exist", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_removes_all_artifacts_for_job()
    {
        var store = new FileSystemArtifactStore(_root);
        await store.SaveAsync("job-3", MakeSourceFile("x"), "x.csv", "text/csv", Ct);

        await store.DeleteAsync("job-3", Ct);

        (await store.ListAsync("job-3", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_unknown_job_is_noop()
    {
        var store = new FileSystemArtifactStore(_root);
        await Should.NotThrowAsync(async () => await store.DeleteAsync("nope", Ct));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task Save_rejects_job_ids_that_escape_the_root(string jobId)
    {
        var store = new FileSystemArtifactStore(_root);
        var src = MakeSourceFile("x");

        await Should.ThrowAsync<ArgumentException>(
            async () => await store.SaveAsync(jobId, src, "x.csv", "text/csv", Ct));
    }

    [Fact]
    public async Task Save_rejects_blank_job_id()
    {
        var store = new FileSystemArtifactStore(_root);
        var src = MakeSourceFile("x");

        await Should.ThrowAsync<ArgumentException>(
            async () => await store.SaveAsync("  ", src, "x.csv", "text/csv", Ct));
    }

    [Fact]
    public void Ctor_rejects_blank_root()
    {
        Should.Throw<ArgumentException>(() => new FileSystemArtifactStore("  "));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
