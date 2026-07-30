using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.MySql.IntegrationTests;

[Collection(nameof(MySqlServerCollection))]
public class MySqlSourceHealthCheckTests
{
    private readonly MySqlServerFixture _fixture;

    public MySqlSourceHealthCheckTests(MySqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CheckAsync_reports_healthy_for_a_reachable_server()
    {
        Skip.IfNot(_fixture.Available, "Docker/MySQL container not available.");

        var check = new MySqlSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "mysql",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_the_connection_string_property_is_missing()
    {
        var check = new MySqlSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "mysql");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_an_unreachable_server()
    {
        var check = new MySqlSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "mysql", new Dictionary<string, object?>
        {
            ["connectionString"] = "Server=127.0.0.1;Port=1;Database=nonexistent;Uid=root;Pwd=wrong;Connection Timeout=1;",
        });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
