using ClosedXML.Excel;
using NeoReports.Abstractions;
using NeoReports.Formats.Xlsx;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Xlsx.UnitTests;

public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date, bool Active);

public sealed record CustomerNote(long Id, string Customer);

public sealed record AmountRow(long Id, decimal Amount);

/// <summary>
/// Tests the XLSX source's public surface end to end against real workbooks produced by this repo's
/// own <see cref="XlsxWriter"/> — the internal read engine (<c>XlsxRowReader</c>,
/// <c>SharedStringCache</c>, <c>NumberFormatCache</c>) has no <c>InternalsVisibleTo</c> (this repo has
/// no such convention), so correctness is verified the same way every other internal helper in this
/// repo is: through what it's reachable from, <c>Source.XlsxFile(...).As&lt;T&gt;()</c>.
/// </summary>
public sealed class XlsxSourceReadingTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "nr-xlsx-tests", Guid.NewGuid().ToString("N"));

    public XlsxSourceReadingTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<T>> CollectAsync<T>(IStreamingSource<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source.ReadAsync(Exec(), CancellationToken.None))
            results.Add(item);
        return results;
    }

    private async Task<string> WriteXlsxAsync(ReportSchema schema, object?[][] rows, XlsxOptions? options = null)
    {
        var path = Path.Join(_dir, $"{Guid.NewGuid():N}.xlsx");
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var writer = new XlsxWriter(options ?? new XlsxOptions());
            await writer.InitializeAsync(new WriterContext(Exec(), output, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(rows, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        return path;
    }

    [Fact]
    public async Task Reads_simple_rows_with_mixed_native_types()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("Customer", ColumnType.String),
            new ReportColumn("Amount", ColumnType.Decimal),
            new ReportColumn("Date", ColumnType.Date),
            new ReportColumn("Active", ColumnType.Boolean),
        });
        var path = await WriteXlsxAsync(schema, new object?[][]
        {
            new object?[] { 1L, "C1", 10.50m, new DateTime(2026, 1, 1), true },
            new object?[] { 2L, "C2", 20.00m, new DateTime(2026, 1, 2), false },
        });

        List<Sale> rows = await CollectAsync(Source.XlsxFile(path).As<Sale>());

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new Sale(1, "C1", 10.50m, new DateTime(2026, 1, 1), true));
        rows[1].ShouldBe(new Sale(2, "C2", 20.00m, new DateTime(2026, 1, 2), false));
    }

    [Fact]
    public async Task A_locale_currency_format_is_not_misread_as_a_date()
    {
        // Regression test: a bracketed locale-currency format code like "[$USD-409]#,##0.00" must not
        // be misclassified as a date. Earlier code stripped only quoted literals, so the trailing "D" in
        // "USD" (and, more generally, any color/currency/condition bracket) survived into the date-token
        // scan and made ordinary currency cells come back as garbage DateTime-derived text instead of
        // their real decimal value. ClosedXML is used directly here (not this project's own XlsxWriter,
        // which never emits this format) since the bug is specific to Excel's own bracketed-format
        // syntax, producible by any real spreadsheet tool.
        var path = Path.Join(_dir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(1, 2).Value = "Amount";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = 1234.56;
            ws.Cell(2, 2).Style.NumberFormat.Format = "[$USD-409]#,##0.00";
            workbook.SaveAs(path);
        }

        List<AmountRow> rows = await CollectAsync(Source.XlsxFile(path).As<AmountRow>());

        rows.Count.ShouldBe(1);
        rows[0].Amount.ShouldBe(1234.56m);
    }

    [Fact]
    public async Task A_builtin_date_format_id_is_recognized_without_a_custom_format_code()
    {
        // Unlike this project's own writer (which always uses a custom "yyyy-mm-dd" FormatCode), a
        // cell can be styled with a spec-reserved built-in NumberFormatId and no <numFmts> entry at
        // all — the built-in-id branch of NumberFormatCache, not the FormatCode-string heuristic.
        var path = Path.Join(_dir, $"{Guid.NewGuid():N}.xlsx");
        var when = new DateTime(2026, 3, 15);
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "When";
            ws.Cell(2, 1).Value = when;
            ws.Cell(2, 1).Style.NumberFormat.NumberFormatId = 14; // built-in short date (m/d/yyyy)
            workbook.SaveAs(path);
        }

        var schema = new ReportSchema(new[] { new ReportColumn("When", ColumnType.Date) });
        var records = await CollectDynamicAsync(path, schema);

        records[0]["When"].ShouldBe(when);
    }

    [Fact]
    public async Task An_elapsed_time_only_bracket_format_is_still_recognized_as_a_date()
    {
        // "[h]" alone (no trailing mm:ss) exercises the branch where StripLiteralsAndNonElapsedBrackets
        // must keep the bracket's inner content, not just discard every bracket outright — an elapsed
        // format with nothing else surviving the strip would otherwise be missed.
        var path = Path.Join(_dir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Elapsed";
            ws.Cell(2, 1).Value = 1.5; // 36 elapsed hours as an OA serial
            ws.Cell(2, 1).Style.NumberFormat.Format = "[h]";
            workbook.SaveAs(path);
        }

        var schema = new ReportSchema(new[] { new ReportColumn("Elapsed", ColumnType.Date) });
        var records = await CollectDynamicAsync(path, schema);

        records[0]["Elapsed"].ShouldBeOfType<DateTime>();
    }

    [Fact]
    public async Task A_quoted_literal_containing_date_letters_is_not_misread_as_a_date()
    {
        // The literal text "Days" contains 'D' and 's', both date/time tokens — but only when NOT
        // inside quotes. This is the mirror case of the bracket regression test, proving the
        // quote-stripping half of the heuristic independently of the bracket-stripping half.
        var path = Path.Join(_dir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Count";
            ws.Cell(2, 1).Value = 42;
            ws.Cell(2, 1).Style.NumberFormat.Format = "0 \"Days\"";
            workbook.SaveAs(path);
        }

        var schema = new ReportSchema(new[] { new ReportColumn("Count", ColumnType.Integer) });
        var records = await CollectDynamicAsync(path, schema);

        records[0]["Count"].ShouldBe(42L);
    }

    [Fact]
    public async Task Reading_an_unknown_sheet_name_throws()
    {
        var path = Path.Join(_dir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.Worksheets.Add("Sheet1");
            workbook.SaveAs(path);
        }

        await Should.ThrowAsync<InvalidOperationException>(() =>
            CollectAsync(Source.XlsxFile(path, new XlsxReaderOptions().SheetName("DoesNotExist")).As<CustomerNote>()));
    }

    [Fact]
    public async Task Resolves_a_shared_string_referenced_by_multiple_rows()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("Customer", ColumnType.String),
        });
        var path = await WriteXlsxAsync(schema, new object?[][]
        {
            new object?[] { 1L, "Repeated Co." },
            new object?[] { 2L, "Repeated Co." },
        });

        List<CustomerNote> rows = await CollectAsync(Source.XlsxFile(path).As<CustomerNote>());

        rows[0].Customer.ShouldBe("Repeated Co.");
        rows[1].Customer.ShouldBe("Repeated Co.");
    }

    [Fact]
    public async Task Header_only_sheet_yields_no_rows()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        var path = await WriteXlsxAsync(schema, Array.Empty<object?[]>());

        List<CustomerNote> rows = await CollectAsync(Source.XlsxFile(path).As<CustomerNote>());

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Header_names_are_matched_case_insensitively_and_can_be_reordered()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("CUSTOMER", ColumnType.String),
            new ReportColumn("ID", ColumnType.Integer),
        });
        var path = await WriteXlsxAsync(schema, new object?[][] { new object?[] { "C1", 1L } });

        List<CustomerNote> rows = await CollectAsync(Source.XlsxFile(path).As<CustomerNote>());

        rows[0].ShouldBe(new CustomerNote(1, "C1"));
    }

    [Fact]
    public async Task Reads_a_named_worksheet()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer), new ReportColumn("Customer", ColumnType.String) });
        var path = await WriteXlsxAsync(schema, new object?[][] { new object?[] { 42L, "C1" } },
            new XlsxOptions().SheetName("Report"));

        List<CustomerNote> rows = await CollectAsync(
            Source.XlsxFile(path, new XlsxReaderOptions().SheetName("Report")).As<CustomerNote>());

        rows.Count.ShouldBe(1);
        rows[0].ShouldBe(new CustomerNote(42, "C1"));
    }

    [Fact]
    public async Task A_named_worksheet_is_matched_case_insensitively()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer), new ReportColumn("Customer", ColumnType.String) });
        var path = await WriteXlsxAsync(schema, new object?[][] { new object?[] { 42L, "C1" } },
            new XlsxOptions().SheetName("Report"));

        List<CustomerNote> rows = await CollectAsync(
            Source.XlsxFile(path, new XlsxReaderOptions().SheetName("REPORT")).As<CustomerNote>());

        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_wide_row_past_column_Z_is_read_correctly()
    {
        // Exercises the base-26 multi-letter CellReference parsing (column AB, index 27) through the
        // real writer/reader round trip rather than unit-testing the internal ColumnIndex helper directly.
        var columns = Enumerable.Range(0, 30).Select(i => new ReportColumn($"C{i}", ColumnType.Integer)).ToArray();
        var schema = new ReportSchema(columns);
        var values = Enumerable.Range(0, 30).Select(i => (object?)(long)i).ToArray();
        var path = await WriteXlsxAsync(schema, new[] { values });

        var records = await CollectDynamicAsync(path, schema);

        records.Count.ShouldBe(1);
        records[0]["C27"].ShouldBe(27L);
        records[0]["C29"].ShouldBe(29L);
    }

    private static async Task<List<ReportRecord>> CollectDynamicAsync(string path, ReportSchema schema)
    {
        var provider = new XlsxConfigSourceProvider();
        var config = new SourceConfig("xlsx", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, schema, services: null!);
        BatchResult<ReportRecord> result = await source.ReadBatchAsync(
            new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);
        return result.Records.ToList();
    }

    [Fact]
    public async Task A_sparse_row_with_a_gap_does_not_shift_later_columns()
    {
        // A null in the middle column makes the writer clear that cell's contents, which Excel emits
        // as an omitted cell. The reader must place the trailing value by its CellReference (column C),
        // not pack it into the gap left by the missing column B.
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("A", ColumnType.String),
            new ReportColumn("B", ColumnType.String),
            new ReportColumn("C", ColumnType.String),
        });
        var path = await WriteXlsxAsync(schema, new object?[][] { new object?[] { "left", null, "right" } });

        var records = await CollectDynamicAsync(path, schema);

        records[0]["A"].ShouldBe("left");
        records[0]["B"].ShouldBeNull();
        records[0]["C"].ShouldBe("right");
    }

    [Fact]
    public void Typed_reading_requires_a_header_row()
    {
        Should.Throw<ArgumentException>(() =>
            Source.XlsxFile("does-not-matter.xlsx", new XlsxReaderOptions().Header(false)).As<Sale>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
