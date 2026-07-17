using System.Text;
using NeoReports.Abstractions;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Csv.UnitTests;

public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

public sealed record CustomerNote(long Id, string Customer);

/// <summary>
/// Tests the CSV source's public surface end to end against real temp files — the streaming RFC
/// 4180 parser and the typed materializer are both <c>internal</c> (this repo has no
/// <c>InternalsVisibleTo</c> convention), so correctness is verified the same way every other
/// internal helper in this repo is: through what it's reachable from, <c>Source.CsvFile(...).As&lt;T&gt;()</c>.
/// </summary>
public sealed class CsvSourceReadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-csv-tests", Guid.NewGuid().ToString("N"));

    public CsvSourceReadingTests() => Directory.CreateDirectory(_dir);

    private string WriteTempCsv(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<T>> CollectAsync<T>(IStreamingSource<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source.ReadAsync(Exec(), CancellationToken.None))
            results.Add(item);
        return results;
    }

    [Fact]
    public async Task Reads_simple_comma_separated_rows()
    {
        var path = WriteTempCsv("Id,Customer,Amount,Date\r\n1,C1,10.50,2026-01-01\r\n2,C2,20.00,2026-01-02\r\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new Sale(1, "C1", 10.50m, new DateTime(2026, 1, 1)));
        rows[1].ShouldBe(new Sale(2, "C2", 20.00m, new DateTime(2026, 1, 2)));
    }

    [Fact]
    public async Task Handles_quoted_fields_with_embedded_delimiter_and_newline()
    {
        var path = WriteTempCsv("Id,Customer,Amount,Date\r\n1,\"Acme, Inc.\r\nSecond line\",10.50,2026-01-01\r\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows.Count.ShouldBe(1);
        rows[0].Customer.ShouldBe("Acme, Inc.\r\nSecond line");
    }

    [Fact]
    public async Task Handles_doubled_quote_escaping()
    {
        var path = WriteTempCsv("Id,Customer,Amount,Date\r\n1,\"Say \"\"hi\"\"\",10.50,2026-01-01\r\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows[0].Customer.ShouldBe("Say \"hi\"");
    }

    [Fact]
    public async Task Accepts_LF_only_line_endings()
    {
        var path = WriteTempCsv("Id,Customer,Amount,Date\n1,C1,10.50,2026-01-01\n2,C2,20.00,2026-01-02\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reads_the_last_row_with_no_trailing_newline()
    {
        var path = WriteTempCsv("Id,Customer,Amount,Date\r\n1,C1,10.50,2026-01-01");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task Empty_file_yields_no_rows()
    {
        var path = WriteTempCsv("");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Header_only_file_yields_no_rows()
    {
        var path = WriteTempCsv("Id,Customer,Amount,Date\r\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Respects_a_custom_delimiter()
    {
        var path = WriteTempCsv("Id;Customer;Amount;Date\r\n1;C1;10.50;2026-01-01\r\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path, new CsvReaderOptions().Delimiter(';')).As<Sale>());

        rows.Count.ShouldBe(1);
        rows[0].Customer.ShouldBe("C1");
    }

    [Fact]
    public async Task Header_names_are_matched_case_insensitively_and_can_be_reordered()
    {
        var path = WriteTempCsv("CUSTOMER,ID,DATE,AMOUNT\r\nC1,1,2026-01-01,10.50\r\n");

        List<Sale> rows = await CollectAsync(Source.CsvFile(path).As<Sale>());

        rows[0].ShouldBe(new Sale(1, "C1", 10.50m, new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void Typed_reading_requires_a_header_row()
    {
        var path = WriteTempCsv("1,C1,10.50,2026-01-01\r\n");

        Should.Throw<ArgumentException>(() => Source.CsvFile(path, new CsvReaderOptions().Header(false)).As<Sale>());
    }

    [Fact]
    public async Task Round_trips_special_characters_written_by_the_real_csv_writer()
    {
        // The reader must be the exact mirror of NeoReports.Formats.Csv.CsvWriter's own escaping —
        // write via the real writer, read back via the source, and confirm the values survive intact.
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("Customer", ColumnType.String),
        });
        var path = Path.Combine(_dir, "roundtrip.csv");
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var writer = new CsvWriter(new CsvOptions());
            await writer.InitializeAsync(new WriterContext(Exec(), output, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(new object?[][]
            {
                new object?[] { 1L, "Comma, quote\", and\r\nnewline" },
                new object?[] { 2L, "Plain" },
            }, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        List<CustomerNote> rows = await CollectAsync(Source.CsvFile(path).As<CustomerNote>());

        rows.Count.ShouldBe(2);
        rows[0].Customer.ShouldBe("Comma, quote\", and\r\nnewline");
        rows[1].Customer.ShouldBe("Plain");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
