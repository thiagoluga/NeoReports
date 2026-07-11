using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

public class PostgresSourceHealthCheckTests : IClassFixture<PostgresServerFixture>
{
    private readonly PostgresServerFixture _fixture;

    public PostgresSourceHealthCheckTests(PostgresServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CheckAsync_reports_healthy_for_a_reachable_server()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        var check = new PostgresSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "postgres",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_the_connection_string_property_is_missing()
    {
        var check = new PostgresSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "postgres");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_an_unreachable_server()
    {
        var check = new PostgresSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "postgres", new Dictionary<string, object?>
        {
            ["connectionString"] = "Host=127.0.0.1;Port=1;Database=nonexistent;Username=postgres;Password=wrong;Timeout=1;",
        });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
