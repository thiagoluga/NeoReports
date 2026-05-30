using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Destinations.S3;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace NeoReports.Destinations.S3.UnitTests;

public class S3DestinationTests
{
    private static DestinationContext Context() =>
        new(new ReportExecutionContext("job", "vendas", null, NullLogger.Instance, CancellationToken.None), null);

    private static ReportFile FileOf(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ReportFile(name, "text/csv", bytes.Length, () => new MemoryStream(bytes));
    }

    [Fact]
    public async Task Uploads_to_resolved_bucket_and_key()
    {
        var client = Substitute.For<IAmazonS3>();
        PutObjectRequest? captured = null;
        client.PutObjectAsync(Arg.Do<PutObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var destination = new S3Destination(client, "my-bucket", "reports/{name}/{date:yyyy-MM-dd}.{ext}");
        var result = await destination.UploadAsync(FileOf("vendas.csv", "a,b\n1,2\n"), Context(), CancellationToken.None);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.BucketName.Should().Be("my-bucket");
        captured.Key.Should().StartWith("reports/vendas/").And.EndWith(".csv");
        result.RemotePath.Should().Be(captured.Key);
        result.Url.Should().Be($"s3://my-bucket/{captured.Key}");
    }

    [Fact]
    public async Task Failure_returns_failed_result_and_does_not_create_partial_object()
    {
        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("network blip"));

        var destination = new S3Destination(client, "my-bucket", "{name}.{ext}");
        var result = await destination.UploadAsync(FileOf("r.csv", "x"), Context(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("network blip");
        // All-or-nothing: only the single PutObject was attempted; no fallback partial write.
        await client.Received(1).PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Non_success_status_is_reported_as_failure()
    {
        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.Forbidden });

        var destination = new S3Destination(client, "b", "{name}.{ext}");
        var result = await destination.UploadAsync(FileOf("r.csv", "x"), Context(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("403");
    }
}
