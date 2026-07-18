using Amazon.S3;
using Amazon.S3.Model;
using NeoReports.Abstractions;
using NeoReports.Sources.Files.Common;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Parquet.UnitTests;

/// <summary>
/// Tests the typed <c>Source.ParquetS3(...)</c> path against a substituted <see cref="IAmazonS3"/> —
/// mirrors the CSV/XLSX S3 tests. An S3 response body is forward-only, so these tests double as the
/// seekability regression: the mock returns a genuinely non-seekable stream and the read must still
/// succeed via <see cref="SeekableStream.EnsureSeekableAsync"/> (ADR D60).
/// </summary>
public sealed class ParquetS3SourceReadingTests
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

    private static IAmazonS3 ClientReturning(Func<Stream> body)
    {
        var client = Substitute.For<IAmazonS3>();
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse { ResponseStream = body() });
        return client;
    }

    [Fact]
    public async Task Reads_rows_from_an_s3_object_via_an_explicit_client()
    {
        byte[] bytes = await ParquetTestFile.WriteBytesAsync(new[]
        {
            new CustomerNote { Id = 1, Customer = "C1" },
            new CustomerNote { Id = 2, Customer = "C2" },
        });
        var client = ClientReturning(() => new MemoryStream(bytes));

        List<CustomerNote> rows = await CollectAsync(Source.ParquetS3("my-bucket", "sales.parquet", client).As<CustomerNote>());

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new CustomerNote { Id = 1, Customer = "C1" });
        rows[1].ShouldBe(new CustomerNote { Id = 2, Customer = "C2" });
    }

    [Fact]
    public async Task Reads_a_forward_only_s3_body_by_making_it_seekable()
    {
        // The regression that matters most for Parquet: its reader throws on a non-seekable stream, so
        // a raw S3 response body cannot be read directly. SeekableStream must copy it to a temp file
        // first. ForwardOnlyStream throws on Position/Length/Seek and reports CanSeek == false.
        byte[] bytes = await ParquetTestFile.WriteBytesAsync(new[]
        {
            new CustomerNote { Id = 42, Customer = "C42" },
        });
        var client = ClientReturning(() => new ForwardOnlyStream(bytes));

        List<CustomerNote> rows = await CollectAsync(Source.ParquetS3("my-bucket", "sales.parquet", client).As<CustomerNote>());

        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(42L);
    }

    [Fact]
    public async Task Requests_the_configured_bucket_and_key()
    {
        byte[] bytes = await ParquetTestFile.WriteBytesAsync(new[] { new CustomerNote { Id = 1, Customer = "C1" } });
        var client = Substitute.For<IAmazonS3>();
        GetObjectRequest? captured = null;
        client.GetObjectAsync(Arg.Do<GetObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });

        await CollectAsync(Source.ParquetS3("my-bucket", "path/sales.parquet", client).As<CustomerNote>());

        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("my-bucket");
        captured.Key.ShouldBe("path/sales.parquet");
    }

    [Fact]
    public void An_empty_bucket_is_rejected() =>
        Should.Throw<ArgumentException>(() => Source.ParquetS3(string.Empty, "key"));

    [Fact]
    public void An_empty_key_is_rejected() =>
        Should.Throw<ArgumentException>(() => Source.ParquetS3("bucket", string.Empty));
}
