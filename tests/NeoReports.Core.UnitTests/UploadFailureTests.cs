using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Events;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class UploadFailureTests
{
    private static Sale[] Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static Task<ReportRunResult> Run(CompiledReport report) =>
        ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

    private static CompiledReport Build(params DestinationSpec[] destinations)
    {
        var builder = new ReportBuilder<Sale>("r")
            .From(new FakeBatchSource<Sale>(new[] { Page(1, 2) }))
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()));

        foreach (var d in destinations)
            builder.UploadTo(d);

        return builder.Build();
    }

    /// <summary>
    /// What a real destination puts in this string: <c>S3Destination</c> interpolates
    /// <c>s3://{bucket}/{key}</c> plus the AWS SDK's own text, <c>LocalDestination</c> an
    /// <c>IOException</c> carrying the full server path.
    /// </summary>
    private const string LeakyDestinationMessage =
        "S3 upload to s3://acme-private-exports/finance/2026/sales.csv failed: Access Denied";

    [Fact]
    public async Task Failed_upload_fails_the_run_instead_of_reporting_success()
    {
        var report = Build(new DestinationSpec(new FailingDestinationFactory("s3", LeakyDestinationMessage)));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error.ShouldNotBeNullOrEmpty();

        // The operator still learns which destination did not accept the file...
        result.Error!.ShouldContain("s3");

        // ...but this string is persisted as the job's error and returned verbatim by GET /jobs/{id},
        // so the destination's own wording — bucket, key, provider text — must not travel with it.
        // The read-failure path reduces a non-NeoReports exception to its type name for the same
        // reason; the full detail is logged instead.
        result.Error!.ShouldNotContain("acme-private-exports");
        result.Error!.ShouldNotContain("Access Denied");
    }

    [Fact]
    public async Task The_upload_failed_event_carries_the_same_scrubbed_message()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var report = Build(new DestinationSpec(new FailingDestinationFactory("s3", LeakyDestinationMessage)));

        await ReportRunner.ExecuteAsync(
            report,
            new ReportExecutionContext(jobId, "r", null, NullLogger.Instance, CancellationToken.None),
            new SingleServiceProvider(store),
            CancellationToken.None);

        // GET /jobs/{id}/events is a second route out of the process for this same text, so scrubbing
        // only the run error would leave the leak reachable through the events feed.
        var events = await store.ListAsync(jobId, JobEventTypes.UploadFailed, 100, 0, CancellationToken.None);
        JobEvent uploadFailed = events.ShouldHaveSingleItem();
        uploadFailed.Message.ShouldNotBeNull();
        uploadFailed.Message!.ShouldNotContain("acme-private-exports");
        uploadFailed.Message!.ShouldNotContain("Access Denied");
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    [Fact]
    public async Task Successful_upload_still_completes()
    {
        var report = Build(new DestinationSpec(new CapturingDestinationFactory()));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task One_failing_destination_among_several_fails_the_run()
    {
        var report = Build(
            new DestinationSpec(new CapturingDestinationFactory()),
            new DestinationSpec(new FailingDestinationFactory("s3", "network unreachable")));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        // Both destinations were attempted; the successful one still recorded its result.
        result.Uploads.Count.ShouldBe(2);
        result.Uploads.ShouldContain(u => u.Success);
        result.Uploads.ShouldContain(u => !u.Success);
    }
}
