using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Csv.UnitTests;

public sealed class CsvSourceHealthCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-csv-health-tests", Guid.NewGuid().ToString("N"));

    public CsvSourceHealthCheckTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task CheckAsync_reports_healthy_for_an_existing_local_file()
    {
        var path = Path.Combine(_dir, "sales.csv");
        await File.WriteAllTextAsync(path, "Id\r\n1\r\n");

        var check = new CsvSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "csv", new Dictionary<string, object?> { ["path"] = path });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_a_missing_local_file()
    {
        var check = new CsvSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "csv",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_dir, "does-not-exist.csv") });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_no_path_or_bucket_is_configured()
    {
        var check = new CsvSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "csv");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_bucket_is_set_but_key_is_missing()
    {
        var check = new CsvSourceHealthCheck();
        var definition = new SourceDefinition("sales-file", "csv", new Dictionary<string, object?> { ["bucket"] = "my-bucket" });

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
