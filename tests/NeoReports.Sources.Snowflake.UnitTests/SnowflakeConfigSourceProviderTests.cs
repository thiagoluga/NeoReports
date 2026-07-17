using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Snowflake.UnitTests;

/// <summary>
/// ADR D57: no live Snowflake warehouse is available in this repo's test infrastructure (a paid
/// cloud service with no Testcontainers/local equivalent), so coverage here is unit-level only —
/// config validation and DI wiring, never an actual query against a server. See ADR D57 for the
/// documented gap and what a future integration suite would need to add.
/// </summary>
public class SnowflakeConfigSourceProviderTests
{
    [Fact]
    public void Provider_requires_connection_string_sql_and_key()
    {
        var provider = new SnowflakeConfigSourceProvider();
        var schema = new ReportSchema(new[] { new ReportColumn("Id", ColumnType.Integer) });
        using var services = new ServiceCollection().BuildServiceProvider();

        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("snowflake"), schema, services));

        var partial = new Dictionary<string, object?> { ["connectionString"] = "account=x", ["sql"] = "SELECT 1" };
        Should.Throw<ConfigurationException>(() => provider.Create(new SourceConfig("snowflake", partial), schema, services));
    }

    [Fact]
    public void Provider_type_is_snowflake() =>
        new SnowflakeConfigSourceProvider().Type.ShouldBe("snowflake");

    [Fact]
    public void AddSnowflakeConfigSource_registers_the_provider_and_health_check()
    {
        var services = new ServiceCollection();
        services.AddSnowflakeConfigSource();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IConfigSourceProvider>().ShouldContain(p => p.Type == "snowflake");
        provider.GetServices<ISourceHealthCheck>().ShouldContain(c => c.Type == "snowflake");
    }
}
