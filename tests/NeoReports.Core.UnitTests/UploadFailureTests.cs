using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class UploadFailureTests
{
    private static IReadOnlyList<Sale> Page(params long[] ids) =>
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

    [Fact]
    public async Task Failed_upload_fails_the_run_instead_of_reporting_success()
    {
        var report = Build(new DestinationSpec(new FailingDestinationFactory("s3", "Access Denied")));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("s3");
        result.Error!.ShouldContain("Access Denied");
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
