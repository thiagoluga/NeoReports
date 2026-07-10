using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Artifacts;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>ADR D40: the runner's best-effort partial-artifact capture on Failed/Cancelled runs.</summary>
public class PartialArtifactCaptureTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "nr-d40-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static IReadOnlyList<Sale> Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec(string jobId) =>
        new(jobId, "r", null, NullLogger.Instance, CancellationToken.None);

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
    public async Task Aborted_run_captures_the_fully_written_batches_renamed_partial()
    {
        var store = new FileSystemPartialArtifactStore(new PartialArtifactOptions { Directory = _dir });
        var jobId = Guid.NewGuid().ToString("N");

        // Page 3 (of 5) throws on write — abort. Pages 1-2 were fully written before that.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4), Page(5) });
        var writer = new FakeWriterFactory("csv", "csv", throwOnBatch: 3);
        var report = Build(source, writer, b => b.OnFailure(f => f.AbortReport()));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.Failed);

        var partials = await store.ListAsync(jobId, CancellationToken.None);
        partials.ShouldHaveSingleItem();
        partials[0].FileName.ShouldBe("r.partial.csv");

        var content = await File.ReadAllTextAsync(partials[0].Path);
        content.ShouldContain("1"); // page 1
        content.ShouldContain("2"); // page 2
        content.ShouldNotContain("4"); // page 4/5 never reached
    }

    [Fact]
    public async Task CompletedPartial_run_does_not_capture_and_still_publishes_to_the_real_destination()
    {
        var store = new FileSystemPartialArtifactStore(new PartialArtifactOptions { Directory = _dir });
        var jobId = Guid.NewGuid().ToString("N");

        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory("csv", "csv", throwOnBatch: 1);
        var destination = new CapturingDestinationFactory();
        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog())
            .UploadTo(new DestinationSpec(destination)));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.CompletedPartial);

        (await store.ListAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
        destination.LastDestination!.Files.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Cancelled_run_captures_partial_output()
    {
        var store = new FileSystemPartialArtifactStore(new PartialArtifactOptions { Directory = _dir });
        var jobId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();

        var source = new CancellingSource(cts, cancelAfterPage: 2);
        var report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ReportRunner.ExecuteAsync(report, Exec(jobId), new SingleServiceProvider(store), cts.Token));

        var partials = await store.ListAsync(jobId, CancellationToken.None);
        partials.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task No_store_registered_leaves_the_run_unaffected()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory("csv", "csv", throwOnBatch: 1);
        var report = Build(source, writer, b => b.OnFailure(f => f.AbortReport()));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(Guid.NewGuid().ToString("N")), new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Failed);
    }

    [Fact]
    public async Task A_store_that_throws_never_fails_the_run()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory("csv", "csv", throwOnBatch: 1);
        var report = Build(source, writer, b => b.OnFailure(f => f.AbortReport()));

        var result = await ReportRunner.ExecuteAsync(
            report, Exec(Guid.NewGuid().ToString("N")), new SingleServiceProvider(new ThrowingPartialArtifactStore()), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Failed);
    }

    private sealed class ThrowingPartialArtifactStore : IPartialArtifactStore
    {
        public Task SaveAsync(string jobId, string sourcePath, string fileName, string mimeType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store is down");

        public Task<IReadOnlyList<ReportArtifact>> ListAsync(string jobId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store is down");

        public Task DeleteAsync(string jobId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("store is down");
    }

    /// <summary>Source that cancels the run's token partway through, simulating an external cancel request.</summary>
    private sealed class CancellingSource : IBatchSource<Sale>
    {
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfterPage;

        public CancellingSource(CancellationTokenSource cts, int cancelAfterPage)
        {
            _cts = cts;
            _cancelAfterPage = cancelAfterPage;
        }

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("_", ColumnType.String) });

        public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            if (context.PageNumber > _cancelAfterPage)
            {
                _cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var page = Page(context.PageNumber);
            return Task.FromResult(new BatchResult<Sale>(page, (context.PageNumber + 1).ToString(), true));
        }

        private static IReadOnlyList<Sale> Page(int page) => new[] { new Sale(page, $"C{page}", page * 10m, DateTime.UnixEpoch) };
    }
}
