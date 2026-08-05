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

    private static ReportSchema OneColumn(string name, ColumnType type) =>
        new(new[] { new ReportColumn(name, type, DisplayName: name) });

    [Fact]
    public async Task A_DateTimeOffset_is_written_as_its_UTC_instant()
    {
        // dto.DateTime is the wall-clock part with the offset discarded, so this value used to be
        // written as 08:00 — three hours off as an instant, while the CSV writer kept the offset.
        // The cell model has no time zone; storing the UTC instant is the reading that is correct.
        var value = new DateTimeOffset(2026, 3, 14, 8, 30, 0, TimeSpan.FromHours(-3));

        using XLWorkbook workbook = await WriteAndReopen(
            new XlsxOptions(), OneColumn("At", ColumnType.DateTime), new object?[][] { new object?[] { value } });

        DateTime written = workbook.Worksheet(1).Cell(2, 1).GetDateTime();
        written.ShouldBe(value.UtcDateTime);
        written.Hour.ShouldBe(11, "08:30-03:00 is 11:30 UTC");
    }

    [Fact]
    public async Task A_bigint_beyond_double_precision_keeps_its_exact_digits()
    {
        // 2^53 + 1 is the first integer a double cannot represent: it rounds to 2^53, so an id like
        // this came out of Excel as a DIFFERENT id. Written as text, the digits survive.
        const long value = 9_007_199_254_740_993L;

        using XLWorkbook workbook = await WriteAndReopen(
            new XlsxOptions(), OneColumn("Id", ColumnType.Integer), new object?[][] { new object?[] { value } });

        workbook.Worksheet(1).Cell(2, 1).GetString().ShouldBe("9007199254740993");
    }

    [Fact]
    public async Task A_decimal_beyond_double_precision_keeps_its_exact_digits()
    {
        // 29 significant digits: double holds ~15-17, so this silently rounded.
        const decimal value = 1.2345678901234567890123456789m;

        using XLWorkbook workbook = await WriteAndReopen(
            new XlsxOptions(), OneColumn("Amount", ColumnType.Decimal), new object?[][] { new object?[] { value } });

        workbook.Worksheet(1).Cell(2, 1).GetString().ShouldBe("1.2345678901234567890123456789");
    }

    [Fact]
    public async Task An_unsigned_bigint_beyond_double_precision_keeps_its_exact_digits()
    {
        // ulong has the same 2^53 ceiling as long but cannot reuse its bound check, so it is its own
        // branch — and therefore its own test.
        const ulong value = 9_007_199_254_740_993UL;

        using XLWorkbook workbook = await WriteAndReopen(
            new XlsxOptions(), OneColumn("Id", ColumnType.Integer), new object?[][] { new object?[] { value } });

        workbook.Worksheet(1).Cell(2, 1).GetString().ShouldBe("9007199254740993");
    }

    [Fact]
    public async Task A_decimal_whose_round_trip_check_itself_overflows_is_still_written()
    {
        // decimal.MaxValue converts to a double that rounds ABOVE the decimal range, so converting
        // back throws OverflowException — the losslessness check has to survive being asked about the
        // largest decimal there is, rather than taking the writer down with it.
        const decimal value = decimal.MaxValue;

        using XLWorkbook workbook = await WriteAndReopen(
            new XlsxOptions(), OneColumn("Amount", ColumnType.Decimal), new object?[][] { new object?[] { value } });

        workbook.Worksheet(1).Cell(2, 1).GetString().ShouldBe("79228162514264337593543950335");
    }

    [Theory]
    [InlineData(long.MaxValue, false)]
    [InlineData(long.MinValue, false)]
    [InlineData(9_007_199_254_740_992L, true)]
    [InlineData(-9_007_199_254_740_992L, true)]
    [InlineData(42L, true)]
    public async Task Only_values_that_would_round_fall_back_to_text(long value, bool expectNumber)
    {
        // The fallback is per value, so ordinary numbers must keep a real number cell — otherwise
        // every integer column would lose Excel's sorting and formatting for the sake of the rare one.
        // long.MinValue is included because Math.Abs on it overflows, which is exactly the bound this
        // check must not be written with.
        using XLWorkbook workbook = await WriteAndReopen(
            new XlsxOptions(), OneColumn("Id", ColumnType.Integer), new object?[][] { new object?[] { value } });

        IXLCell cell = workbook.Worksheet(1).Cell(2, 1);
        (cell.DataType == XLDataType.Number).ShouldBe(expectNumber);
        cell.GetString().Replace(",", "").ShouldContain(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
    public async Task Serializes_edge_case_values_without_corrupting_the_file()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Text", ColumnType.String),
            new ReportColumn("Number", ColumnType.Decimal),
            new ReportColumn("Binary", ColumnType.String),
            new ReportColumn("Time", ColumnType.String),
        });
        var rows = new object?[][]
        {
            new object?[] { "AB\u0001CD", double.NaN, new byte[] { 1, 2, 3 }, new TimeOnly(14, 30, 15) },
        };

        // The write must not throw and the workbook must reopen — before the fix an illegal control
        // char aborted the whole file and a NaN produced an invalid (un-openable) number cell.
        using var wb = await WriteAndReopen(new XlsxOptions().SheetName("Edge"), schema, rows);
        var ws = wb.Worksheet("Edge");

        ws.Cell(2, 1).GetString().ShouldBe("ABCD"); // 0x01 stripped so the sheet stays valid XML
        ws.Cell(2, 2).GetString().ShouldBe("NaN");   // NaN emitted as text, not an invalid number cell
        ws.Cell(2, 3).GetString().ShouldBe(Convert.ToBase64String(new byte[] { 1, 2, 3 })); // not "System.Byte[]"
        ws.Cell(2, 4).GetString().ShouldBe(
            new TimeOnly(14, 30, 15).ToString("O", System.Globalization.CultureInfo.InvariantCulture)); // invariant
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
