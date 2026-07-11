using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using Npgsql;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// ADR D42/D43: <c>Source.PostgresNamed</c> resolves its connection through the source registry
/// fresh at the start of every run — proven here against a real PostgreSQL server (Testcontainers)
/// by pointing the same registered source name at two different databases between two runs.
/// </summary>
public class NamedPostgresSourceTests : IClassFixture<PostgresServerFixture>
{
    private readonly PostgresServerFixture _fixture;

    public NamedPostgresSourceTests(PostgresServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor::bigint) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task Switching_the_registered_connection_string_redirects_the_very_next_run()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        string altConnectionString = await SeedAltDatabaseAsync();

        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        await registry.SaveAsync(new SourceDefinition("sales-db", "postgres",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString }), CancellationToken.None);

        var services = new SingleServiceProvider(registry);
        var source = Source.PostgresNamed("sales-db", Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        ((INamedSourceResolver)source).AttachServices(services);

        var first = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        first.Records.ShouldContain(r => r.Customer == "C1");
        first.Records.ShouldNotContain(r => r.Customer == "AltCo");

        await registry.SaveAsync(new SourceDefinition("sales-db", "postgres",
            new Dictionary<string, object?> { ["connectionString"] = altConnectionString }), CancellationToken.None);
        ((INamedSourceResolver)source).AttachServices(services);

        var second = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        second.Records.ShouldContain(r => r.Customer == "AltCo");
        second.Records.ShouldNotContain(r => r.Customer == "C1");
    }

    private async Task<string> SeedAltDatabaseAsync()
    {
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = "SELECT 1 FROM pg_database WHERE datname = 'salesalt'";
            var exists = await create.ExecuteScalarAsync() is not null;
            if (!exists)
            {
                await using var createDb = connection.CreateCommand();
                createDb.CommandText = "CREATE DATABASE salesalt;";
                await createDb.ExecuteNonQueryAsync();
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "salesalt" };
        var altConnectionString = builder.ConnectionString;

        await using var altConnection = new NpgsqlConnection(altConnectionString);
        await altConnection.OpenAsync();
        await using var seed = altConnection.CreateCommand();
        seed.CommandText = """
            CREATE TABLE IF NOT EXISTS Sales (Id BIGINT PRIMARY KEY, Customer VARCHAR(100) NOT NULL, Amount NUMERIC(18,2) NOT NULL, Date TIMESTAMP NOT NULL);
            INSERT INTO Sales (Id, Customer, Amount, Date)
            SELECT 9001, 'AltCo', 1.00, TIMESTAMP '2026-02-02'
            WHERE NOT EXISTS (SELECT 1 FROM Sales);
            """;
        await seed.ExecuteNonQueryAsync();

        return altConnectionString;
    }

    /// <summary>Resolves exactly one registered instance, by assignable type — enough to give a
    /// by-name source access to <see cref="ISourceRegistry"/> without a full DI container.</summary>
    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
