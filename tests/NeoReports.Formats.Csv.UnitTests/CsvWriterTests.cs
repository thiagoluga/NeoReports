using System.Globalization;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;

namespace NeoReports.Formats.Csv.UnitTests;

public class CsvWriterTests
{
    private static ReportSchema SalesSchema() => new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer, DisplayName: "Sale ID"),
        new ReportColumn("Customer", ColumnType.String, DisplayName: "Customer"),
        new ReportColumn("Amount", ColumnType.Decimal, DisplayName: "Amount", Format: "C2", Culture: "pt-BR"),
        new ReportColumn("Date", ColumnType.DateTime, DisplayName: "Sale Date", Format: "yyyy-MM-dd"),
    });

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<byte[]> WriteAsync(CsvOptions options, ReportSchema schema, IReadOnlyList<object?[]> rows)
    {
        await using var stream = new MemoryStream();
        await using (var writer = new CsvWriter(options))
        {
            await writer.InitializeAsync(new WriterContext(Exec(), stream, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(rows, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task Writes_header_and_rows_with_culture_and_format()
    {
        var rows = new object?[][]
        {
            new object?[] { 1L, "Ana", 1234.5m, new DateTime(2026, 1, 15) },
            new object?[] { 2L, "João", 0.99m, new DateTime(2026, 2, 1) },
        };

        var bytes = await WriteAsync(new CsvOptions().Delimiter(';'), SalesSchema(), rows);
        var text = Encoding.UTF8.GetString(bytes);

        var amount = 1234.5m.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
        text.ShouldBe(
            "Sale ID;Customer;Amount;Sale Date\r\n" +
            $"1;Ana;{amount};2026-01-15\r\n" +
            $"2;João;{0.99m.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))};2026-02-01\r\n");
    }

    [Fact]
    public async Task Escapes_delimiter_quote_and_newline_per_rfc4180()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("A", ColumnType.String, DisplayName: "A"),
            new ReportColumn("B", ColumnType.String, DisplayName: "B"),
        });
        var rows = new object?[][]
        {
            new object?[] { "has,comma", "has\"quote" },
            new object?[] { "has\nnewline", "plain" },
        };

        var bytes = await WriteAsync(new CsvOptions(), schema, rows);
        var text = Encoding.UTF8.GetString(bytes);

        text.ShouldBe(
            "A,B\r\n" +
            "\"has,comma\",\"has\"\"quote\"\r\n" +
            "\"has\nnewline\",plain\r\n");
    }

    [Fact]
    public async Task Binary_values_render_as_base64_not_the_type_name()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Blob", ColumnType.String, DisplayName: "Blob") });
        var rows = new object?[][] { new object?[] { new byte[] { 1, 2, 3 } } };

        var bytes = await WriteAsync(new CsvOptions().Header(false), schema, rows);
        var text = Encoding.UTF8.GetString(bytes);

        text.ShouldBe(Convert.ToBase64String(new byte[] { 1, 2, 3 }) + "\r\n"); // "AQID", not "System.Byte[]"
    }

    [Fact]
    public async Task Null_values_render_empty_and_header_can_be_disabled()
    {
        var schema = new ReportSchema(new[]
        {
            new ReportColumn("A", ColumnType.String, DisplayName: "A"),
            new ReportColumn("B", ColumnType.Integer, DisplayName: "B"),
        });
        var rows = new object?[][] { new object?[] { null, 5 } };

        var bytes = await WriteAsync(new CsvOptions().Header(false), schema, rows);
        var text = Encoding.UTF8.GetString(bytes);

        text.ShouldBe(",5\r\n");
    }

    [Fact]
    public async Task Utf8_encoding_has_no_bom()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("A", ColumnType.String, DisplayName: "A") });
        var bytes = await WriteAsync(new CsvOptions(), schema, new object?[][] { new object?[] { "x" } });

        // UTF-8 BOM is EF BB BF — must be absent.
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
    }
}
