using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Destinations.S3;
using NSubstitute;
using Shouldly;
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

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("my-bucket");
        captured.Key.ShouldStartWith("reports/vendas/");
        captured.Key.ShouldEndWith(".csv");
        result.RemotePath.ShouldBe(captured.Key);
        result.Url.ShouldBe($"s3://my-bucket/{captured.Key}");
    }

    [Fact]
    public async Task Failure_returns_failed_result_and_does_not_create_partial_object()
    {
        var client = Substitute.For<IAmazonS3>();
        // A read failure surfaces through the same try/catch that wraps the upload: it must become
        // a Fail result, and PutObject must never run — so no partial object can be created.
        var file = new ReportFile("r.csv", "text/csv", 1, () => throw new IOException("disk gone"));

        var destination = new S3Destination(client, "my-bucket", "{name}.{ext}");
        var result = await destination.UploadAsync(file, Context(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("disk gone");
        client.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IAmazonS3.PutObjectAsync))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Non_success_status_is_reported_as_failure()
    {
        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.Forbidden });

        var destination = new S3Destination(client, "b", "{name}.{ext}");
        var result = await destination.UploadAsync(FileOf("r.csv", "x"), Context(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("403");
    }
}
