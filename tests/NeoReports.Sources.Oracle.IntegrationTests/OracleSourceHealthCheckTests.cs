using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

[Collection(nameof(OracleCollection))]
public class OracleSourceHealthCheckTests
{
    private readonly OracleServerFixture _fixture;

    public OracleSourceHealthCheckTests(OracleServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CheckAsync_reports_healthy_for_a_reachable_server()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        var check = new OracleSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "oracle",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_the_connection_string_property_is_missing()
    {
        var check = new OracleSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "oracle");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_an_unreachable_server()
    {
        var check = new OracleSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "oracle", new Dictionary<string, object?>
        {
            ["connectionString"] = "User Id=system;Password=wrong;Data Source=127.0.0.1:1/nonexistent;Connection Timeout=1;",
        });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
