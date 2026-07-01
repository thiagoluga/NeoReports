using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Epic B1.1: a single source read can feed several outputs, each with its own filter and/or columns
/// (a "view"). The OSS hook produces one file per view; the Pro workbook writer (B1.2) will pack
/// views as sheets in one file.
/// </summary>
public class MultiViewTests
{
    private static readonly long[] EvenIds = { 2, 4 };
    private static readonly long[] OddIds = { 1, 3 };
    private static readonly string[] OddCustomers = { "C1", "C3" };

    private static Sale[] Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static Task<ReportRunResult> Run(CompiledReport report) =>
        ReportRunner.ExecuteAsync(
            report,
            new ReportExecutionContext(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None),
            new EmptyServiceProvider(),
            CancellationToken.None);

    [Fact]
    public async Task Views_filter_and_project_independently_from_a_single_read()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1, 2, 3, 4) });
        var approved = new FakeWriterFactory("csv", "csv");
        var rejected = new FakeWriterFactory("csv", "csv");

        var report = new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id") // report default columns (used by the "approved" view)
            .To(new OutputSpec(approved), view => view.Where(v => v.Id % 2 == 0))
            .To(new OutputSpec(rejected), view => view
                .Where(v => v.Id % 2 == 1)
                .Column(v => v.Id, "Id")
                .Column(v => v.Customer, "Customer"))
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        source.ReadCalls.ShouldBe(1); // single pass over the source
        result.Stats.RecordsRead.ShouldBe(4);
        result.Stats.RecordsWritten.ShouldBe(4); // each row lands in exactly one view

        // Approved: even ids, the report's single column.
        approved.LastWriter!.Rows.Select(r => (long)r[0]!).ShouldBe(EvenIds);
        approved.LastWriter!.Rows.ShouldAllBe(r => r.Length == 1);

        // Rejected: odd ids, its own two columns (Id, Customer).
        rejected.LastWriter!.Rows.Select(r => (long)r[0]!).ShouldBe(OddIds);
        rejected.LastWriter!.Rows.ShouldAllBe(r => r.Length == 2);
        rejected.LastWriter!.Rows.Select(r => (string)r[1]!).ShouldBe(OddCustomers);
    }

    [Fact]
    public async Task RecordsWritten_counts_a_row_once_even_when_it_lands_in_several_views()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1, 2, 3) });
        var all = new FakeWriterFactory("csv", "csv");
        var big = new FakeWriterFactory("csv", "csv");

        var report = new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(all))                                     // all 3 rows
            .To(new OutputSpec(big), view => view.Where(v => v.Id >= 2)) // rows 2, 3
            .Build();

        var result = await Run(report);

        all.LastWriter!.Rows.Count.ShouldBe(3);
        big.LastWriter!.Rows.Count.ShouldBe(2);
        result.Stats.RecordsWritten.ShouldBe(3); // distinct source rows, not 3 + 2
    }

    [Fact]
    public void A_view_without_columns_and_no_report_columns_is_rejected()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1) });
        var act = () => new ReportBuilder<Sale>("r")
            .From(source)
            .To(new OutputSpec(new FakeWriterFactory("csv", "csv")), view => view.Where(v => v.Id > 0))
            .Build();

        Should.Throw<ConfigurationException>(act).Message.ShouldContain("no columns");
    }
}
