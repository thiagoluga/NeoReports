using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.MongoDb.IntegrationTests;

[Collection(nameof(MongoDbServerCollection))]
public class MongoDbSourceHealthCheckTests
{
    private readonly MongoDbServerFixture _fixture;

    public MongoDbSourceHealthCheckTests(MongoDbServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task CheckAsync_reports_healthy_for_a_reachable_server()
    {
        Skip.IfNot(_fixture.Available, "Docker/MongoDB container not available.");

        var check = new MongoDbSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "mongodb",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_when_the_connection_string_property_is_missing()
    {
        var check = new MongoDbSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "mongodb");

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task CheckAsync_reports_unhealthy_for_an_unreachable_server()
    {
        var check = new MongoDbSourceHealthCheck();
        var definition = new SourceDefinition("sales-db", "mongodb", new Dictionary<string, object?>
        {
            ["connectionString"] = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=1000&connectTimeoutMS=1000",
        });

        var result = await check.CheckAsync(definition, services: null!, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
