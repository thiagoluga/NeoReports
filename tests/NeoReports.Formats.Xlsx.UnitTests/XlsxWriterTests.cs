using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Formats.Xlsx;
using Shouldly;
using Xunit;

namespace NeoReports.Formats.Xlsx.UnitTests;

public class XlsxWriterTests
{
    private static ReportSchema Schema() => new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer, DisplayName: "Sale ID"),
        new ReportColumn("Customer", ColumnType.String, DisplayName: "Customer"),
        new ReportColumn("Amount", ColumnType.Decimal, DisplayName: "Amount", Format: "C2", Culture: "pt-BR"),
        new ReportColumn("Date", ColumnType.DateTime, DisplayName: "Sale Date", Format: "yyyy-MM-dd"),
    });

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    private static async Task<XLWorkbook> WriteAndReopen(XlsxOptions options, ReportSchema schema, IReadOnlyList<object?[]> rows)
    {
        var stream = new MemoryStream();
        await using (var writer = new XlsxWriter(options))
        {
            await writer.InitializeAsync(new WriterContext(Exec(), stream, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(rows, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    [Fact]
    public async Task Writes_named_sheet_with_header_and_native_types()
    {
        var rows = new object?[][]
        {
            new object?[] { 1L, "Ana", 1234.5m, new DateTime(2026, 1, 15) },
            new object?[] { 2L, "João", 7m, new DateTime(2026, 2, 1) },
        };

        using var wb = await WriteAndReopen(
            new XlsxOptions().SheetName("Sales").AutoFilter(), Schema(), rows);

        var ws = wb.Worksheet("Sales");
        ws.Name.ShouldBe("Sales");

        // Header row.
        ws.Cell(1, 1).GetString().ShouldBe("Sale ID");
        ws.Cell(1, 4).GetString().ShouldBe("Sale Date");

        // Native numeric + date types preserved (not strings).
        ws.Cell(2, 1).Value.IsNumber.ShouldBeTrue();
        ws.Cell(2, 1).GetDouble().ShouldBe(1d);
        ws.Cell(2, 3).GetDouble().ShouldBe(1234.5);
        ws.Cell(2, 4).Value.IsDateTime.ShouldBeTrue();
        ws.Cell(2, 4).GetDateTime().ShouldBe(new DateTime(2026, 1, 15));
        ws.Cell(2, 2).GetString().ShouldBe("Ana");
    }

    [Fact]
    public async Task AutoFilter_is_enabled_when_requested()
    {
        var rows = new object?[][] { new object?[] { 1L, "Ana", 1m, new DateTime(2026, 1, 1) } };

        using var wb = await WriteAndReopen(new XlsxOptions().SheetName("S").AutoFilter(), Schema(), rows);
        wb.Worksheet("S").AutoFilter.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task AutoFilter_is_disabled_by_default()
    {
        var rows = new object?[][] { new object?[] { 1L, "Ana", 1m, new DateTime(2026, 1, 1) } };

        using var wb = await WriteAndReopen(new XlsxOptions().SheetName("S"), Schema(), rows);
        wb.Worksheet("S").AutoFilter.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Null_values_produce_empty_cells()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("A", ColumnType.String, DisplayName: "A"),
            new ReportColumn("B", ColumnType.Integer, DisplayName: "B"),
        });
        var rows = new object?[][] { new object?[] { null, 5 } };

        using var wb = await WriteAndReopen(new XlsxOptions().SheetName("S"), schema, rows);
        var ws = wb.Worksheet("S");
        ws.Cell(2, 1).IsEmpty().ShouldBeTrue();
        ws.Cell(2, 2).GetDouble().ShouldBe(5);
    }
}
