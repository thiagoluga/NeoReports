using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Csv.UnitTests;

public sealed class CsvConfigSourceProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-csv-config-tests", Guid.NewGuid().ToString("N"));

    public CsvConfigSourceProviderTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
    });

    [Fact]
    public void Provider_requires_path_or_bucket_and_key()
    {
        var provider = new CsvConfigSourceProvider();

        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("csv"), Schema, services: null!));

        var bucketOnly = new Dictionary<string, object?> { ["bucket"] = "my-bucket" };
        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("csv", bucketOnly), Schema, services: null!));
    }

    [Fact]
    public async Task Reads_a_local_file_via_the_dynamic_path()
    {
        var path = Path.Combine(_dir, "sales.csv");
        await File.WriteAllTextAsync(path, "Id,Customer\r\n1,C1\r\n2,C2\r\n");

        var provider = new CsvConfigSourceProvider();
        var config = new SourceConfig("csv", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(2);
        result.Records[0]["Id"].ShouldBe(1L);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task Honors_a_custom_delimiter_property()
    {
        var path = Path.Combine(_dir, "sales-semicolon.csv");
        await File.WriteAllTextAsync(path, "Id;Customer\r\n1;C1\r\n");

        var provider = new CsvConfigSourceProvider();
        var config = new SourceConfig("csv", new Dictionary<string, object?> { ["path"] = path, ["delimiter"] = ";" });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task An_out_of_range_numeric_field_falls_back_to_the_raw_text_instead_of_throwing()
    {
        // A hand-authored CSV can contain a value a real database driver would never hand back —
        // long.Parse/decimal.Parse throw OverflowException (not FormatException) for a value that
        // parses but doesn't fit, which must fall back the same way a malformed value does, not
        // crash the whole batch read.
        var path = Path.Combine(_dir, "sales-overflow.csv");
        await File.WriteAllTextAsync(path, "Id,Customer\r\n99999999999999999999999999999,C1\r\n");

        var provider = new CsvConfigSourceProvider();
        var config = new SourceConfig("csv", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Id"].ShouldBe("99999999999999999999999999999");
    }

    [Fact]
    public async Task Falls_back_to_positional_alignment_when_the_file_has_no_header()
    {
        var path = Path.Combine(_dir, "sales-no-header.csv");
        await File.WriteAllTextAsync(path, "1,C1\r\n2,C2\r\n");

        var provider = new CsvConfigSourceProvider();
        var config = new SourceConfig("csv", new Dictionary<string, object?> { ["path"] = path, ["hasHeader"] = false });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(2);
        result.Records[0]["Id"].ShouldBe(1L);
        result.Records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task Reads_an_s3_object_via_a_di_registered_client()
    {
        var client = Substitute.For<IAmazonS3>();
        var bytes = Encoding.UTF8.GetBytes("Id,Customer\r\n1,C1\r\n");
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });

        var services = new ServiceCollection();
        services.AddSingleton(client);
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new CsvConfigSourceProvider();
        var config = new SourceConfig("csv", new Dictionary<string, object?> { ["bucket"] = "my-bucket", ["key"] = "sales.csv" });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, serviceProvider);

        BatchResult<ReportRecord> result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);

        result.Records.Count.ShouldBe(1);
        result.Records[0]["Id"].ShouldBe(1L);
        result.Records[0]["Customer"].ShouldBe("C1");
        await client.Received(1).GetObjectAsync(
            Arg.Is<GetObjectRequest>(r => r!.BucketName == "my-bucket" && r!.Key == "sales.csv"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddCsvConfigSource_registers_the_provider_and_health_check()
    {
        var services = new ServiceCollection();
        services.AddCsvConfigSource();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IConfigSourceProvider>().ShouldContain(p => p.Type == "csv");
        provider.GetServices<NeoReports.Core.SourceRegistry.ISourceHealthCheck>().ShouldContain(c => c.Type == "csv");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
