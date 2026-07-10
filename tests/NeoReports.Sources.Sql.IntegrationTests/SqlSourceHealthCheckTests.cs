using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

public class SqlSourceHealthCheckTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlSourceHealthCheckTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CheckAsync_reports_healthy_for_a_reachable_server()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var check = new SqlSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_the_connection_string_property_is_missing()
    {
        var check = new SqlSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "sql");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_an_unreachable_server()
    {
        var check = new SqlSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "sql", new Dictionary<string, object?>
        {
            ["connectionString"] = "Server=127.0.0.1,1;Database=nonexistent;User Id=sa;Password=wrong;Connect Timeout=1;TrustServerCertificate=true;",
        });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
