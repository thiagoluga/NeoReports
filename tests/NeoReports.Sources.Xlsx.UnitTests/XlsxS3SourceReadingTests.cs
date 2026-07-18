using Amazon.S3;
using Amazon.S3.Model;
using NeoReports.Abstractions;
using NeoReports.Formats.Xlsx;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Xlsx.UnitTests;

/// <summary>
/// Tests the typed <c>Source.XlsxS3(...)</c> path against a substituted <see cref="IAmazonS3"/> —
/// mirrors <c>NeoReports.Sources.Csv.UnitTests.CsvS3SourceReadingTests</c>' own approach of mocking
/// the AWS SDK client rather than hitting real S3.
/// </summary>
public sealed class XlsxS3SourceReadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-xlsx-s3-tests", Guid.NewGuid().ToString("N"));

    public XlsxS3SourceReadingTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<T>> CollectAsync<T>(IStreamingSource<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source.ReadAsync(Exec(), CancellationToken.None))
            results.Add(item);
        return results;
    }

    private async Task<byte[]> WriteXlsxBytesAsync(ReportSchema schema, object?[][] rows)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.xlsx");
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var writer = new XlsxWriter(new XlsxOptions());
            await writer.InitializeAsync(new WriterContext(Exec(), output, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(rows, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        return await File.ReadAllBytesAsync(path);
    }

    private static IAmazonS3 ClientReturning(byte[] xlsxBytes)
    {
        var client = Substitute.For<IAmazonS3>();
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream(xlsxBytes) });
        return client;
    }

    [Fact]
    public async Task Reads_rows_from_an_s3_object_via_an_explicit_client()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer), new ReportColumn("Customer", ColumnType.String) });
        var bytes = await WriteXlsxBytesAsync(schema, new object?[][]
        {
            new object?[] { 1L, "C1" },
            new object?[] { 2L, "C2" },
        });
        var client = ClientReturning(bytes);

        List<CustomerNote> rows = await CollectAsync(Source.XlsxS3("my-bucket", "sales.xlsx", client: client).As<CustomerNote>());

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new CustomerNote(1, "C1"));
        rows[1].ShouldBe(new CustomerNote(2, "C2"));
    }

    [Fact]
    public async Task Requests_the_configured_bucket_and_key()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        var bytes = await WriteXlsxBytesAsync(schema, new object?[][] { new object?[] { 1L } });
        var client = Substitute.For<IAmazonS3>();
        GetObjectRequest? captured = null;
        client.GetObjectAsync(Arg.Do<GetObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });

        await CollectAsync(Source.XlsxS3("my-bucket", "path/sales.xlsx", client: client).As<CustomerNote>());

        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("my-bucket");
        captured.Key.ShouldBe("path/sales.xlsx");
    }

    [Fact]
    public void An_empty_bucket_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Source.XlsxS3(string.Empty, "key"));
    }

    [Fact]
    public void An_empty_key_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Source.XlsxS3("bucket", string.Empty));
    }

    [Fact]
    public void Typed_s3_reading_requires_a_header_row()
    {
        Should.Throw<ArgumentException>(() =>
            Source.XlsxS3("my-bucket", "sales.xlsx", new XlsxReaderOptions().Header(false)).As<CustomerNote>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
