using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// ADR D42, locked decision 4: <c>Source.SqlNamed</c> resolves its connection through the source
/// registry fresh at the start of every run — proven here against a real SQL Server (Testcontainers)
/// by pointing the same registered source name at two different databases between two runs.
/// </summary>
public class NamedSqlSourceTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public NamedSqlSourceTests(SqlServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task Switching_the_registered_connection_string_redirects_the_very_next_run()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        string altConnectionString = await SeedAltDatabaseAsync();

        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString }), CancellationToken.None);

        var services = new SingleServiceProvider(registry);
        var source = Source.SqlNamed("sales-db", Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        ((INamedSourceResolver)source).AttachServices(services);

        var first = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        first.Records.ShouldContain(r => r.Customer == "C1");
        first.Records.ShouldNotContain(r => r.Customer == "AltCo");

        // The definition's connection string changes; the source instance is unchanged (same as
        // the typed pipeline reusing one source across runs) — only a fresh run (cursor == null)
        // re-resolves it.
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = altConnectionString }), CancellationToken.None);
        ((INamedSourceResolver)source).AttachServices(services);

        var second = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        second.Records.ShouldContain(r => r.Customer == "AltCo");
        second.Records.ShouldNotContain(r => r.Customer == "C1");
    }

    [SkippableFact]
    public async Task Throws_a_ConfigurationException_when_the_source_is_not_registered()
    {
        Skip.IfNot(_fixture.Available, "Docker/SQL Server container not available.");

        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        var services = new SingleServiceProvider(registry);
        var source = Source.SqlNamed("does-not-exist", Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        ((INamedSourceResolver)source).AttachServices(services);

        await Should.ThrowAsync<ConfigurationException>(() =>
            source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None));
    }

    private async Task<string> SeedAltDatabaseAsync()
    {
        await using (var connection = new SqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = "IF DB_ID('SalesAlt') IS NULL CREATE DATABASE SalesAlt;";
            await create.ExecuteNonQueryAsync();
        }

        var altConnectionString = new SqlConnectionStringBuilder(_fixture.ConnectionString) { InitialCatalog = "SalesAlt" }.ConnectionString;

        await using var altConnection = new SqlConnection(altConnectionString);
        await altConnection.OpenAsync();
        await using var seed = altConnection.CreateCommand();
        seed.CommandText = """
            IF OBJECT_ID('Sales') IS NULL
                CREATE TABLE Sales (Id BIGINT PRIMARY KEY, Customer NVARCHAR(100) NOT NULL, Amount DECIMAL(18,2) NOT NULL, Date DATETIME2 NOT NULL);
            IF NOT EXISTS (SELECT 1 FROM Sales)
                INSERT INTO Sales (Id, Customer, Amount, Date) VALUES (9001, N'AltCo', 1.00, '2026-02-02');
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
