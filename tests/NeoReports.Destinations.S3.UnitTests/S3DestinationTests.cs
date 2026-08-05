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
        new(new ReportExecutionContext("job", "sales", null, NullLogger.Instance, CancellationToken.None), null);

    private static ReportFile FileOf(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ReportFile(name, "text/csv", bytes.Length, () => new MemoryStream(bytes));
    }

    private static DestinationContext ContextWith(IReadOnlyDictionary<string, object?> parameters) =>
        new(new ReportExecutionContext("job", "sales", parameters, NullLogger.Instance, CancellationToken.None), null);

    [Fact]
    public async Task A_run_parameter_cannot_steer_the_object_into_another_prefix()
    {
        // The template describes one level of tenant hierarchy; the caller supplies the tenant in the
        // run request. Without a guard, a value carrying '/' writes under a prefix the author never
        // described — a cross-tenant write wherever a shared bucket relies on prefix isolation
        // (ADR D73). The upload must fail rather than land somewhere unintended.
        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var destination = new S3Destination(client, "shared-bucket", "reports/{tenant}/{name}.{ext}");
        var context = ContextWith(new Dictionary<string, object?> { ["tenant"] = "acme/../victim" });

        UploadResult result = await destination.UploadAsync(
            FileOf("sales.csv", "a,b\n1,2\n"), context, CancellationToken.None);

        result.Success.ShouldBeFalse();

        // Nothing was sent: the key is built before the request, so a rejected value must not reach S3.
        await client.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_parameter_without_a_separator_still_fills_its_token()
    {
        // The guard rejects hierarchy, not ordinary values — a tenant name is exactly what this
        // template is for, and '..' inside a segment is literal in S3, so it stays allowed.
        var client = Substitute.For<IAmazonS3>();
        PutObjectRequest? captured = null;
        client.PutObjectAsync(Arg.Do<PutObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var destination = new S3Destination(client, "shared-bucket", "reports/{tenant}/{name}.{ext}");
        var context = ContextWith(new Dictionary<string, object?> { ["tenant"] = "acme..corp" });

        UploadResult result = await destination.UploadAsync(
            FileOf("sales.csv", "a,b\n1,2\n"), context, CancellationToken.None);

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.Key.ShouldBe("reports/acme..corp/sales.csv");
    }

    [Fact]
    public async Task The_template_itself_may_still_contain_slashes()
    {
        // The guard applies to substituted values only. An author's own hierarchy is untouched —
        // this is the whole reason the Local destination's stricter segment guard was not reused.
        var client = Substitute.For<IAmazonS3>();
        PutObjectRequest? captured = null;
        client.PutObjectAsync(Arg.Do<PutObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var destination = new S3Destination(client, "my-bucket", "a/b/c/{name}.{ext}");

        UploadResult result = await destination.UploadAsync(
            FileOf("sales.csv", "a,b\n1,2\n"), Context(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        captured!.Key.ShouldBe("a/b/c/sales.csv");
    }

    [Fact]
    public async Task Uploads_to_resolved_bucket_and_key()
    {
        var client = Substitute.For<IAmazonS3>();
        PutObjectRequest? captured = null;
        client.PutObjectAsync(Arg.Do<PutObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var destination = new S3Destination(client, "my-bucket", "reports/{name}/{date:yyyy-MM-dd}.{ext}");
        var result = await destination.UploadAsync(FileOf("sales.csv", "a,b\n1,2\n"), Context(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("my-bucket");
        captured.Key.ShouldStartWith("reports/sales/");
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
        result.ErrorMessage.ShouldNotBeNull();
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
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("403");
    }
}
