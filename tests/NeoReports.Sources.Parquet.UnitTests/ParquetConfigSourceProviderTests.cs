using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Parquet.UnitTests;

public sealed class ParquetConfigSourceProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-parquet-config-tests", Guid.NewGuid().ToString("N"));

    public ParquetConfigSourceProviderTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Customer", ColumnType.String),
    });

    private string Path2(string name) => Path.Combine(_dir, name);

    private static async Task<List<ReportRecord>> ReadAllAsync(IBatchSource<ReportRecord> source)
    {
        var result = await source.ReadBatchAsync(new BatchContext(Exec(), 1000, null, 1), CancellationToken.None);
        return result.Records.ToList();
    }

    [Fact]
    public void Provider_requires_path_or_bucket_and_key()
    {
        var provider = new ParquetConfigSourceProvider();

        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("parquet"), Schema, services: null!));

        var bucketOnly = new Dictionary<string, object?> { ["bucket"] = "my-bucket" };
        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("parquet", bucketOnly), Schema, services: null!));
    }

    [Fact]
    public async Task Reads_a_local_file_via_the_dynamic_path()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("sales.parquet"), new[]
        {
            new CustomerNote { Id = 1, Customer = "C1" },
            new CustomerNote { Id = 2, Customer = "C2" },
        });

        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        List<ReportRecord> records = await ReadAllAsync(source);

        records.Count.ShouldBe(2);
        records[0]["Id"].ShouldBe(1L);
        records[0]["Customer"].ShouldBe("C1");
        records[1]["Id"].ShouldBe(2L);
    }

    [Fact]
    public async Task Matches_declared_columns_to_file_columns_case_insensitively_and_reordered()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("ci.parquet"), new[]
        {
            new CustomerNote { Id = 5, Customer = "C5" },
        });

        var reordered = new ReportSchema(new[]
        {
            new ReportColumn("CUSTOMER", ColumnType.String),
            new ReportColumn("id", ColumnType.Integer),
        });
        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, reordered, services: null!);

        List<ReportRecord> records = await ReadAllAsync(source);

        records[0]["CUSTOMER"].ShouldBe("C5");
        records[0]["id"].ShouldBe(5L);
    }

    [Fact]
    public async Task A_null_value_in_a_nullable_column_reads_as_null()
    {
        // Parquet.Net omits the key entirely for a null cell (verified empirically, ADR D60), so the
        // materializer must treat an absent key as null rather than assuming every declared column is
        // present in every row's dictionary.
        var path = await ParquetTestFile.WriteFileAsync(Path2("nulls.parquet"), new[]
        {
            new WideRow { Id = 1, Customer = "C1", Amount = 10m, Note = "present" },
            new WideRow { Id = 2, Customer = "C2", Amount = 20m, Note = null },
        });

        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("Note", ColumnType.String),
        });
        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, schema, services: null!);

        List<ReportRecord> records = await ReadAllAsync(source);

        records.Count.ShouldBe(2);
        records[0]["Note"].ShouldBe("present");
        records[1]["Note"].ShouldBeNull();
    }

    [Fact]
    public async Task A_file_column_not_in_the_declared_schema_is_ignored()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("extra.parquet"), new[]
        {
            new WideRow { Id = 1, Customer = "C1", Amount = 99m, Note = "x" },
        });

        // Declares only Id/Customer; the file's Amount and Note columns are simply not projected.
        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, services: null!);

        List<ReportRecord> records = await ReadAllAsync(source);

        records.Count.ShouldBe(1);
        records[0].Count.ShouldBe(2);
        records[0]["Id"].ShouldBe(1L);
        records[0]["Customer"].ShouldBe("C1");
    }

    [Fact]
    public async Task A_declared_column_absent_from_the_file_reads_as_null()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("missing.parquet"), new[]
        {
            new CustomerNote { Id = 1, Customer = "C1" },
        });

        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("Customer", ColumnType.String),
            new ReportColumn("Missing", ColumnType.String),
        });
        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, schema, services: null!);

        List<ReportRecord> records = await ReadAllAsync(source);

        records[0]["Id"].ShouldBe(1L);
        records[0]["Missing"].ShouldBeNull();
    }

    [Fact]
    public async Task A_utc_adjusted_timestamp_column_reads_as_a_plain_DateTime()
    {
        // A column explicitly written as isAdjustedToUTC=true (the shape a non-.NET producer like
        // Spark/Arrow/pandas would use) still comes back from Parquet.Net's untyped deserializer as a
        // plain DateTime, never a DateTimeOffset — verified empirically (ADR D60), not assumed.
        var when = new DateTime(2026, 3, 15, 13, 30, 0, DateTimeKind.Utc);
        var path = await ParquetTestFile.WriteFileAsync(Path2("timestamps.parquet"), new[]
        {
            new TimestampRow { Id = 1, When = when },
        });

        var schema = new ReportSchema(new[]
        {
            new ReportColumn("Id", ColumnType.Integer),
            new ReportColumn("When", ColumnType.Timestamp),
        });
        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["path"] = path });
        IBatchSource<ReportRecord> source = provider.Create(config, schema, services: null!);

        List<ReportRecord> records = await ReadAllAsync(source);

        records[0]["When"].ShouldBeOfType<DateTime>().ShouldBe(when);
    }

    [Fact]
    public async Task Reads_an_s3_object_via_a_di_registered_client()
    {
        byte[] bytes = await ParquetTestFile.WriteBytesAsync(new[] { new CustomerNote { Id = 1, Customer = "C1" } });

        var client = Substitute.For<IAmazonS3>();
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse { ResponseStream = new MemoryStream(bytes) });

        var services = new ServiceCollection();
        services.AddSingleton(client);
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new ParquetConfigSourceProvider();
        var config = new SourceConfig("parquet", new Dictionary<string, object?> { ["bucket"] = "my-bucket", ["key"] = "sales.parquet" });
        IBatchSource<ReportRecord> source = provider.Create(config, Schema, serviceProvider);

        List<ReportRecord> records = await ReadAllAsync(source);

        records.Count.ShouldBe(1);
        records[0]["Id"].ShouldBe(1L);
        records[0]["Customer"].ShouldBe("C1");
        await client.Received(1).GetObjectAsync(
            Arg.Is<GetObjectRequest>(r => r!.BucketName == "my-bucket" && r!.Key == "sales.parquet"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddParquetConfigSource_registers_the_provider_and_health_check()
    {
        var services = new ServiceCollection();
        services.AddParquetConfigSource();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IConfigSourceProvider>().ShouldContain(p => p.Type == "parquet");
        provider.GetServices<NeoReports.Core.SourceRegistry.ISourceHealthCheck>().ShouldContain(c => c.Type == "parquet");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
