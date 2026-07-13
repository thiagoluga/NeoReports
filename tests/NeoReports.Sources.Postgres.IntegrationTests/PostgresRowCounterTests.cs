using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>ADR D47: <see cref="ISourceRowCounter"/> against a real PostgreSQL container.</summary>
public class PostgresRowCounterTests : IClassFixture<PostgresServerFixture>
{
    private readonly PostgresServerFixture _fixture;

    public PostgresRowCounterTests(PostgresServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor::bigint) ORDER BY Id";

    private ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task CountAsync_matches_the_seeded_row_count()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        var source = Source.Postgres(_fixture.ConnectionString, Sql).Keyset<Sale, long>(v => v.Id, pageSize: 1000);
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }

    [SkippableFact]
    public async Task Named_source_counts_by_resolving_through_the_registry()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        await registry.SaveAsync(new SourceDefinition("sales-db", "postgres",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString }), CancellationToken.None);

        var services = new SingleServiceProvider(registry);
        var source = Source.PostgresNamed("sales-db", Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        ((INamedSourceResolver)source).AttachServices(services);

        var count = await ((ISourceRowCounter)source).CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }

    /// <summary>Resolves exactly one registered instance, by assignable type — enough to give a
    /// by-name source access to <see cref="ISourceRegistry"/> without a full DI container.</summary>
    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
