using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

public class SqliteSourceHealthCheckTests : IClassFixture<SqliteFileFixture>
{
    private readonly SqliteFileFixture _fixture;

    public SqliteSourceHealthCheckTests(SqliteFileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CheckAsync_reports_healthy_for_a_reachable_database()
    {
        var check = new SqliteSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "sqlite",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_the_connection_string_property_is_missing()
    {
        var check = new SqliteSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "sqlite");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_an_unopenable_database()
    {
        var check = new SqliteSourceHealthCheck();
        // A directory path can never be opened as a SQLite database file.
        var definition = new SourceDefinition("sales-db", "sqlite", new Dictionary<string, object?>
        {
            ["connectionString"] = $"Data Source={Path.GetTempPath()}",
        });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
