using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.SourceRegistry;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Parquet.UnitTests;

public sealed class ParquetSourceHealthCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-parquet-health-tests", Guid.NewGuid().ToString("N"));

    public ParquetSourceHealthCheckTests() => Directory.CreateDirectory(_dir);

    private async Task<string> WriteParquetAsync()
    {
        var path = Path.Combine(_dir, "sales.parquet");
        await ParquetTestFile.WriteFileAsync(path, new[] { new CustomerNote { Id = 1, Customer = "C1" } });
        return path;
    }

    [Fact]
    public async Task CheckAsync_reports_healthy_for_an_existing_local_file()
    {
        var path = await WriteParquetAsync();

        var check = new ParquetSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "parquet", new Dictionary<string, object?> { ["path"] = path });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_a_missing_local_file()
    {
        var check = new ParquetSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "parquet",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_dir, "does-not-exist.parquet") });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_no_path_or_bucket_is_configured()
    {
        var check = new ParquetSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "parquet");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_resolves_a_di_registered_client_for_the_s3_case()
    {
        var client = Substitute.For<IAmazonS3>();
        client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse());

        var services = new ServiceCollection();
        services.AddSingleton(client);
        await using var serviceProvider = services.BuildServiceProvider();

        var check = new ParquetSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "parquet",
            new Dictionary<string, object?> { ["bucket"] = "my-bucket", ["key"] = "sales.parquet" });

        var result = await check.CheckAsync(definition, serviceProvider, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        await client.Received(1).GetObjectMetadataAsync(
            Arg.Is<GetObjectMetadataRequest>(r => r!.BucketName == "my-bucket" && r!.Key == "sales.parquet"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_bucket_is_set_but_key_is_missing()
    {
        var check = new ParquetSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "parquet", new Dictionary<string, object?> { ["bucket"] = "my-bucket" });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
