using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>
/// ADR D42/D43: <c>Source.OracleNamed</c> resolves its connection through the source registry
/// fresh at the start of every run — proven here against a real Oracle server (Testcontainers) by
/// pointing the same registered source name at two different schemas between two runs. Oracle has
/// no lightweight "create another database" operation (unlike Postgres/MySQL), so the two
/// connections here are two different schemas/users on the same instance instead.
/// </summary>
[Collection(nameof(OracleCollection))]
public class NamedOracleSourceTests
{
    private readonly OracleServerFixture _fixture;

    public NamedOracleSourceTests(OracleServerFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, SaleDate AS \"Date\" FROM Sales " +
        "WHERE (:cursor IS NULL OR Id > :cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task Switching_the_registered_connection_string_redirects_the_very_next_run()
    {
        Skip.IfNot(_fixture.Available, "Docker/Oracle container not available.");

        string altConnectionString = await SeedAltSchemaAsync();

        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        await registry.SaveAsync(new SourceDefinition("sales-db", "oracle",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString }), CancellationToken.None);

        var services = new SingleServiceProvider(registry);
        var source = Source.OracleNamed("sales-db", Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        ((INamedSourceResolver)source).AttachServices(services);

        var first = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        first.Records.ShouldContain(r => r.Customer == "C1");
        first.Records.ShouldNotContain(r => r.Customer == "AltCo");

        await registry.SaveAsync(new SourceDefinition("sales-db", "oracle",
            new Dictionary<string, object?> { ["connectionString"] = altConnectionString }), CancellationToken.None);
        ((INamedSourceResolver)source).AttachServices(services);

        var second = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        second.Records.ShouldContain(r => r.Customer == "AltCo");
        second.Records.ShouldNotContain(r => r.Customer == "C1");
    }

    private async Task<string> SeedAltSchemaAsync()
    {
        // The default app schema has no privilege to create another schema/user, so provisioning
        // the alt schema goes through the SYSTEM connection.
        await using (var connection = new OracleConnection(_fixture.SystemConnectionString))
        {
            await connection.OpenAsync();
            await Execute(connection, "CREATE USER salesalt IDENTIFIED BY \"Testcontainers1!\"");
            await Execute(connection, "GRANT CREATE SESSION, RESOURCE, UNLIMITED TABLESPACE TO salesalt");
        }

        var altConnectionString = new OracleConnectionStringBuilder(_fixture.ConnectionString)
        { UserID = "salesalt", Password = "Testcontainers1!" }.ConnectionString;

        await using var altConnection = new OracleConnection(altConnectionString);
        await altConnection.OpenAsync();
        await Execute(altConnection, "CREATE TABLE Sales (Id NUMBER(19) PRIMARY KEY, Customer VARCHAR2(100) NOT NULL, Amount NUMBER(18,2) NOT NULL, SaleDate DATE NOT NULL)");
        await Execute(altConnection, "INSERT INTO Sales (Id, Customer, Amount, SaleDate) VALUES (9001, 'AltCo', 1.00, DATE '2026-02-02')");
        await Execute(altConnection, "COMMIT");

        return altConnectionString;
    }

    private static async Task Execute(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Resolves exactly one registered instance, by assignable type — enough to give a
    /// by-name source access to <see cref="ISourceRegistry"/> without a full DI container.</summary>
    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
