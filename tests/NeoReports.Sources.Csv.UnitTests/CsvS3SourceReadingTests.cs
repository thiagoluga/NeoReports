using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using NeoReports.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Csv.UnitTests;

/// <summary>
/// Tests the typed <c>Source.CsvS3(...)</c> path against a substituted <see cref="IAmazonS3"/> —
/// mirrors <c>NeoReports.Destinations.S3.UnitTests.S3DestinationTests</c>' own approach of mocking
/// the AWS SDK client rather than hitting real S3.
/// </summary>
public sealed class CsvS3SourceReadingTests
{
    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<T>> CollectAsync<T>(IStreamingSource<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source.ReadAsync(Exec(), CancellationToken.None))
            results.Add(item);
        return results;
    }

    private static IAmazonS3 ClientReturning(string csvContent)
    {
        var client = Substitute.For<IAmazonS3>();
        var bytes = Encoding.UTF8.GetBytes(csvContent);
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });
        return client;
    }

    [Fact]
    public async Task Reads_rows_from_an_s3_object_via_an_explicit_client()
    {
        var client = ClientReturning("Id,Customer\r\n1,C1\r\n2,C2\r\n");

        List<CustomerNote> rows = await CollectAsync(Source.CsvS3("my-bucket", "sales.csv", client: client).As<CustomerNote>());

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new CustomerNote(1, "C1"));
        rows[1].ShouldBe(new CustomerNote(2, "C2"));
    }

    [Fact]
    public async Task Requests_the_configured_bucket_and_key()
    {
        var client = ClientReturning("Id,Customer\r\n1,C1\r\n");
        GetObjectRequest? captured = null;
        client.GetObjectAsync(Arg.Do<GetObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("Id,Customer\r\n1,C1\r\n")) });

        await CollectAsync(Source.CsvS3("my-bucket", "path/sales.csv", client: client).As<CustomerNote>());

        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("my-bucket");
        captured.Key.ShouldBe("path/sales.csv");
    }

    [Fact]
    public async Task Honors_a_custom_delimiter_when_reading_from_s3()
    {
        var client = ClientReturning("Id;Customer\r\n1;C1\r\n");

        List<CustomerNote> rows = await CollectAsync(
            Source.CsvS3("my-bucket", "sales.csv", new CsvReaderOptions().Delimiter(';'), client).As<CustomerNote>());

        rows.Count.ShouldBe(1);
        rows[0].Customer.ShouldBe("C1");
    }

    [Fact]
    public void An_empty_bucket_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Source.CsvS3(string.Empty, "key"));
    }

    [Fact]
    public void An_empty_key_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Source.CsvS3("bucket", string.Empty));
    }

    [Fact]
    public void Typed_s3_reading_requires_a_header_row()
    {
        Should.Throw<ArgumentException>(() =>
            Source.CsvS3("my-bucket", "sales.csv", new CsvReaderOptions().Header(false)).As<CustomerNote>());
    }
}
