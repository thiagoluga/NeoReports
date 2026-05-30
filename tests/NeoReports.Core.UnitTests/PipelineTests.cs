using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class PipelineTests
{
    private static IReadOnlyList<Venda> Page(params long[] ids) =>
        ids.Select(id => new Venda(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static Task<ReportRunResult> Run(CompiledReport report) =>
        ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

    [Fact]
    public async Task Reads_all_pages_writes_rows_and_uploads_file()
    {
        var source = new FakeBatchSource<Venda>(new[] { Page(1, 2), Page(3) });
        var writer = new FakeWriterFactory();
        var destination = new CapturingDestinationFactory();

        var report = new ReportBuilder<Venda>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(writer))
            .UploadTo(new DestinationSpec(destination))
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsRead.ShouldBe(3);
        result.Stats.RecordsWritten.ShouldBe(3);
        result.Stats.BatchesProcessed.ShouldBe(2);
        result.Uploads.ShouldHaveSingleItem().Success.ShouldBeTrue();

        destination.LastDestination!.Files.ShouldContainKey("r.fake");
        var content = Encoding.UTF8.GetString(destination.LastDestination.Files["r.fake"]);
        content.ShouldContain("1");
        content.ShouldContain("3");
    }

    [Fact]
    public async Task Multi_output_reads_source_only_once_per_page()
    {
        var source = new FakeBatchSource<Venda>(new[] { Page(1, 2), Page(3, 4) });
        var csvLike = new FakeWriterFactory("csv", "csv");
        var xlsxLike = new FakeWriterFactory("xlsx", "xlsx");

        var report = new ReportBuilder<Venda>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(csvLike))
            .To(new OutputSpec(xlsxLike))
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        source.ReadCalls.ShouldBe(2);
        csvLike.LastWriter!.Rows.Count.ShouldBe(4);
        xlsxLike.LastWriter!.Rows.Count.ShouldBe(4);
        csvLike.LastWriter!.Finalized.ShouldBeTrue();
    }

    [Fact]
    public async Task Filter_excludes_non_matching_rows()
    {
        var source = new FakeBatchSource<Venda>(new[] { Page(1, 2, 3, 4) });
        var writer = new FakeWriterFactory();

        var report = new ReportBuilder<Venda>("r")
            .From(source)
            .Filter(v => v.Id % 2 == 0)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(writer))
            .Build();

        var result = await Run(report);

        result.Stats.RecordsRead.ShouldBe(4);
        result.Stats.RecordsWritten.ShouldBe(2);
        writer.LastWriter!.Rows.Select(r => (long)r[0]!).ShouldBe(new long[] { 2, 4 });
    }

    [Fact]
    public async Task Streaming_source_is_sliced_into_batches()
    {
        var items = Enumerable.Range(1, 5).Select(i => new Venda(i, $"C{i}", i, DateTime.UnixEpoch)).ToArray();
        var source = new FakeStreamingSource<Venda>(items);
        var writer = new FakeWriterFactory();

        var report = new ReportBuilder<Venda>("r")
            .From(source)
            .WithPageSize(2)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(writer))
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsRead.ShouldBe(5);
        result.Stats.BatchesProcessed.ShouldBe(3);
        writer.LastWriter!.Rows.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Mapping_from_source_type_projects_to_row_type()
    {
        var source = new FakeBatchSource<(long Id, string Name)>(new[]
        {
            new[] { (1L, "a"), (2L, "b") },
        });
        var writer = new FakeWriterFactory();

        var report = new ReportBuilder<Venda>("r")
            .From(source, t => new Venda(t.Id, t.Name, t.Id, DateTime.UnixEpoch))
            .Column(v => v.Id, "Id")
            .Column(v => v.Cliente, "Cliente")
            .To(new OutputSpec(writer))
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        writer.LastWriter!.Rows.Count.ShouldBe(2);
        writer.LastWriter!.Rows.Select(r => (string)r[1]!).ShouldBe(new[] { "a", "b" });
    }
}
