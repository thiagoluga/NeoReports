using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class ErrorScrubTests
{
    /// <summary>A source whose read throws a fixed exception, to drive the run's error composition.</summary>
    private sealed class ThrowingSource(Exception toThrow) : IBatchSource<Sale>
    {
        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
            throw toThrow;
    }

    private static async Task<ReportRunResult> RunWithReadFailure(Exception toThrow)
    {
        var report = new ReportBuilder<Sale>("r")
            .From(new ThrowingSource(toThrow))
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .OnFailure(f => f.AbortReport())
            .Build();

        var execution = new ReportExecutionContext("job", "r", null, NullLogger.Instance, CancellationToken.None);
        return await ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), CancellationToken.None);
    }

    [Fact]
    public async Task A_driver_exception_message_is_reduced_to_its_type_in_the_run_error()
    {
        // Simulates a driver exception whose message echoes a connection string.
        var driver = new InvalidOperationException("Login failed for user 'sa'. Server=db.internal:1433;Database=payroll");
        ReportRunResult result = await RunWithReadFailure(driver);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error!.ShouldContain("InvalidOperationException");
        result.Error!.ShouldNotContain("db.internal");
        result.Error!.ShouldNotContain("sa");
        result.Error!.ShouldNotContain("payroll");
    }

    [Fact]
    public async Task Skip_strategy_read_failure_also_scrubs_the_driver_message()
    {
        // A read failure can't be skipped (no cursor to advance), so even SkipBatchAndLog aborts here
        // via the runner's own readDetail composition — which must scrub the driver message too.
        var driver = new InvalidOperationException("Cannot open server 'sql-prod.corp' requested by the login.");
        var report = new ReportBuilder<Sale>("r")
            .From(new ThrowingSource(driver))
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .OnFailure(f => f.SkipBatchAndLog())
            .Build();

        var execution = new ReportExecutionContext("job", "r", null, NullLogger.Instance, CancellationToken.None);
        ReportRunResult result = await ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error!.ShouldContain("InvalidOperationException");
        result.Error!.ShouldNotContain("sql-prod.corp");
    }

    [Fact]
    public async Task A_NeoReports_exception_message_is_kept_because_it_is_curated()
    {
        // Our own exceptions carry secret-free, useful messages (e.g. a missing named source).
        var ours = new ConfigurationException("No source named 'sales-db' is registered.");
        ReportRunResult result = await RunWithReadFailure(ours);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error!.ShouldContain("sales-db");
    }
}
