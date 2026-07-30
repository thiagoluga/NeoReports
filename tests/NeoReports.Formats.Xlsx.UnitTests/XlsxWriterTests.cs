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
    public async Task Applies_column_number_and_date_formats()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Price", ColumnType.Decimal, DisplayName: "Price", Format: "C2"),
            new ReportColumn("Qty", ColumnType.Integer, DisplayName: "Qty", Format: "N0"),
            new ReportColumn("When", ColumnType.DateTime, DisplayName: "When", Format: "yyyy-MM-dd"),
        });
        var rows = new object?[][] { new object?[] { 12.5m, 1000, new DateTime(2026, 3, 4) } };

        using var wb = await WriteAndReopen(new XlsxOptions().SheetName("F"), schema, rows);
        var ws = wb.Worksheet("F");

        // The per-column .NET format survives the streaming rewrite as an Excel number/date format code.
        ws.Cell(2, 1).Style.NumberFormat.Format.ShouldBe("#,##0.00"); // C2
        ws.Cell(2, 2).Style.NumberFormat.Format.ShouldBe("#,##0");    // N0
        ws.Cell(2, 3).Style.NumberFormat.Format.ShouldBe("yyyy-mm-dd");

        // Values still round-trip as their native types under the applied formats.
        ws.Cell(2, 1).GetDouble().ShouldBe(12.5);
        ws.Cell(2, 2).GetDouble().ShouldBe(1000);
        ws.Cell(2, 3).Value.IsDateTime.ShouldBeTrue();
        ws.Cell(2, 3).GetDateTime().ShouldBe(new DateTime(2026, 3, 4));
    }

    [Fact]
    public async Task Writing_streams_to_disk_at_constant_memory()
    {
        // The whole point of the OpenXML streaming rewrite (ADR D14): memory must NOT grow with the
        // row count. The ClosedXML/ZipPackage predecessors retained ~20x the output file in RAM
        // (~190 MB for 400k rows); the streaming writer retains only small per-batch buffers. Drives
        // the writer over a write-only FileStream — the real pipeline contract, not a MemoryStream.
        var schema = Schema();
        var path = Path.Join(Path.GetTempPath(), "nr-xlsx-mem-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new XlsxWriter(new XlsxOptions().SheetName("Big"));
            await writer.InitializeAsync(new WriterContext(Exec(), output, schema, null), CancellationToken.None);

            var baseline = GC.GetTotalMemory(forceFullCollection: true);

            const int batches = 400;
            const int perBatch = 1000; // 400k rows total
            for (var b = 0; b < batches; b++)
            {
                var rows = new object?[perBatch][];
                for (var r = 0; r < perBatch; r++)
                {
                    long id = (b * perBatch) + r;
                    rows[r] = new object?[] { id, "Customer " + id, (id % 1000) + 0.5m, new DateTime(2026, 1, 1).AddMinutes(id) };
                }

                await writer.WriteRowsAsync(rows, CancellationToken.None);
            }

            // Retained memory just before finalize — must stay far below the buffered predecessors.
            var retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
            await writer.FinalizeAsync(CancellationToken.None);

            var fileSize = new FileInfo(path).Length;
            fileSize.ShouldBeGreaterThan(1_000_000); // the report really is large on disk
            retained.ShouldBeLessThan(40_000_000, // ~40 MB ceiling: streaming holds ~2 MB; the old writer held ~190 MB
                $"XLSX writer retained {retained / 1_000_000} MB writing 400k rows ({fileSize / 1_000_000} MB file) — memory is not constant.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
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
