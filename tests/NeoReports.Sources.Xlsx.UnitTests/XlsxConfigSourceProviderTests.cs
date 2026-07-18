using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Formats.Xlsx;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Xlsx.UnitTests;

public sealed class XlsxConfigSourceProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-xlsx-config-tests", Guid.NewGuid().ToString("N"));

    public XlsxConfigSourceProviderTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
    });

    private async Task<string> WriteXlsxAsync(ReportSchema schema, object?[][] rows, XlsxOptions? options = null)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.xlsx");
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var writer = new XlsxWriter(options ?? new XlsxOptions());
            await writer.InitializeAsync(new WriterContext(Exec(), output, schema, null), CancellationToken.None);
            await writer.WriteRowsAsync(rows, CancellationToken.None);
            await writer.FinalizeAsync(CancellationToken.None);
        }

        return path;
    }

    [Fact]
    public void Provider_requires_path_or_bucket_and_key()
    {
        var provider = new XlsxConfigSourceProvider();

        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("xlsx"), Schema, services: null!));

        var bucketOnly = new Dictionary<string, object?> { ["bucket"] = "my-bucket" };
        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("xlsx", bucketOnly), Schema, services: null!));
    }

    [Fact]
    public async Task Reads_a_local_file_via_the_dynamic_path()
    {
        var path = await WriteXlsxAsync(Schema, new object?[][] { new object?[] { 1L, "C1" }, new object?[] { 2L, "C2" } });

        var provider = new XlsxConfigSourceProvider();
        var config = new SourceConfig("xlsx", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(2);
        result.Records[0]["Id"].ShouldBe(1L);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task Honors_a_sheetName_property()
    {
        var path = await WriteXlsxAsync(Schema, new object?[][] { new object?[] { 1L, "C1" } }, new XlsxOptions().SheetName("Report"));

        var provider = new XlsxConfigSourceProvider();
        var config = new SourceConfig("xlsx", new Dictionary<string, object?> { ["path"] = path, ["sheetName"] = "Report" });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task Falls_back_to_positional_alignment_when_the_sheet_has_no_header()
    {
        var path = await WriteXlsxAsync(Schema, new object?[][] { new object?[] { 1L, "C1" }, new object?[] { 2L, "C2" } },
            new XlsxOptions().Header(false));

        var provider = new XlsxConfigSourceProvider();
        var config = new SourceConfig("xlsx", new Dictionary<string, object?> { ["path"] = path, ["hasHeader"] = false });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(2);
        result.Records[0]["Id"].ShouldBe(1L);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task Reads_an_s3_object_via_a_di_registered_client()
    {
        var path = await WriteXlsxAsync(Schema, new object?[][] { new object?[] { 1L, "C1" } });
        var bytes = await File.ReadAllBytesAsync(path);

        var client = Substitute.For<IAmazonS3>();
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });

        var services = new ServiceCollection();
        services.AddSingleton(client);
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new XlsxConfigSourceProvider();
        var config = new SourceConfig("xlsx", new Dictionary<string, object?> { ["bucket"] = "my-bucket", ["key"] = "sales.xlsx" });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, serviceProvider);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Id"].ShouldBe(1L);
        result.Records[0]["Customer"].ShouldBe("C1");
        await client.Received(1).GetObjectAsync(
            Arg.Is<GetObjectRequest>(r => r.BucketName == "my-bucket" && r.Key == "sales.xlsx"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddXlsxConfigSource_registers_the_provider_and_health_check()
    {
        var services = new ServiceCollection();
        services.AddXlsxConfigSource();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IConfigSourceProvider>().ShouldContain(p => p.Type == "xlsx");
        provider.GetServices<NeoReports.Core.SourceRegistry.ISourceHealthCheck>().ShouldContain(c => c.Type == "xlsx");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
