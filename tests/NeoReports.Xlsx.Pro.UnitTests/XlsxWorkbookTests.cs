using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using Shouldly;
using Xunit;
using static NeoReports.Xlsx.Pro.Format;

namespace NeoReports.Xlsx.Pro.UnitTests;

public sealed record Sale(long Id, string Customer, decimal Amount);

/// <summary>
/// B1.3: the Pro workbook writer produces a single .xlsx with one worksheet per section, each with
/// its own filter and columns, from a single source read.
/// </summary>
public sealed class XlsxWorkbookTests : IDisposable
{
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "nr-xlsxpro", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Produces_one_workbook_with_a_worksheet_per_section()
    {
        var page = new IReadOnlyList<Sale>[]
        {
            new Sale[]
            {
                new(1, "Acme", 100m),
                new(2, "Globex", -5m),
                new(3, "Initech", 42m),
                new(4, "Umbrella", -1m),
            },
        };
        var source = new InMemorySource(page);

        CompiledReport report = new ReportBuilder<Sale>("sales")
            .From(source)
            .Column(v => v.Id, "Sale ID")
            .ToSections(XlsxWorkbook(o => o.AutoFilter()), s => s
                .Section("Approved", v => v.Where(x => x.Amount > 0))
                .Section("Rejected", v => v
                    .Where(x => x.Amount <= 0)
                    .Column(x => x.Id, "Sale ID")
                    .Column(x => x.Customer, "Customer")))
            .UploadTo(Destination.Local(Path.Combine(_outDir, "{name}.{ext}")))
            .Build();

        var exec = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), report.Name, null, NullLogger.Instance, CancellationToken.None);
        ReportRunResult result = await ReportRunner.ExecuteAsync(report, exec, new EmptyServices(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);

        var path = Path.Combine(_outDir, "sales.xlsx");
        File.Exists(path).ShouldBeTrue();

        using var workbook = new XLWorkbook(path);
        workbook.Worksheets.Select(w => w.Name).ShouldBe(new[] { "Approved", "Rejected" });

        IXLWorksheet approved = workbook.Worksheet("Approved");
        approved.Cell(1, 1).GetString().ShouldBe("Sale ID"); // single column
        approved.Cell(2, 1).GetDouble().ShouldBe(1);          // Id 1 (Amount 100 > 0)
        approved.Cell(3, 1).GetDouble().ShouldBe(3);          // Id 3 (Amount 42 > 0)
        approved.LastRowUsed()!.RowNumber().ShouldBe(3);      // header + 2 rows

        IXLWorksheet rejected = workbook.Worksheet("Rejected");
        rejected.Cell(1, 1).GetString().ShouldBe("Sale ID");
        rejected.Cell(1, 2).GetString().ShouldBe("Customer"); // its own second column
        rejected.Cell(2, 2).GetString().ShouldBe("Globex");   // Id 2 (Amount -5)
        rejected.Cell(3, 2).GetString().ShouldBe("Umbrella"); // Id 4 (Amount -1)
        rejected.LastRowUsed()!.RowNumber().ShouldBe(3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outDir))
            Directory.Delete(_outDir, recursive: true);
    }

    private sealed class InMemorySource : IBatchSource<Sale>
    {
        private readonly IReadOnlyList<IReadOnlyList<Sale>> _pages;

        public InMemorySource(IReadOnlyList<IReadOnlyList<Sale>> pages) => _pages = pages;

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            var index = context.PageNumber - 1;
            if (index >= _pages.Count)
                return Task.FromResult(BatchResult<Sale>.Empty);
            var hasMore = index + 1 < _pages.Count;
            var next = hasMore ? (context.PageNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new BatchResult<Sale>(_pages[index], next, hasMore));
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
