using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using NeoReports.Formats.Xlsx;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Xlsx.UnitTests;

public sealed class XlsxSourceHealthCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-xlsx-health-tests", Guid.NewGuid().ToString("N"));

    public XlsxSourceHealthCheckTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private async Task<string> WriteXlsxAsync()
    {
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        var path = Path.Combine(_dir, "sales.xlsx");
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var writer = new XlsxWriter(new XlsxOptions());
            await writer.InitializeAsync(new WriterContext(Exec(), output, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(new object?[][] { new object?[] { 1L } }, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        return path;
    }

    [Fact]
    public async Task CheckAsync_reports_healthy_for_an_existing_local_file()
    {
        var path = await WriteXlsxAsync();

        var check = new XlsxSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "xlsx", new Dictionary<string, object?> { ["path"] = path });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_a_missing_local_file()
    {
        var check = new XlsxSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "xlsx",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_dir, "does-not-exist.xlsx") });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_no_path_or_bucket_is_configured()
    {
        var check = new XlsxSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "xlsx");

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

        var check = new XlsxSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "xlsx",
            new Dictionary<string, object?> { ["bucket"] = "my-bucket", ["key"] = "sales.xlsx" });

        var result = await check.CheckAsync(definition, serviceProvider, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        await client.Received(1).GetObjectMetadataAsync(
            Arg.Is<GetObjectMetadataRequest>(r => r!.BucketName == "my-bucket" && r!.Key == "sales.xlsx"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_bucket_is_set_but_key_is_missing()
    {
        var check = new XlsxSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "xlsx", new Dictionary<string, object?> { ["bucket"] = "my-bucket" });

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
