using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

/// <summary>
/// ADR D42/D43: <c>Source.SqliteNamed</c> resolves its connection through the source registry fresh
/// at the start of every run — proven here by pointing the same registered source name at two
/// different SQLite files between two runs.
/// </summary>
public class NamedSqliteSourceTests : IClassFixture<SqliteFileFixture>, IDisposable
{
    private readonly SqliteFileFixture _fixture;
    private readonly string _altPath = Path.Combine(Path.GetTempPath(), "nr-sqlite-tests", $"{Guid.NewGuid():N}-alt.db");

    public NamedSqliteSourceTests(SqliteFileFixture fixture) => _fixture = fixture;

    private const string Sql =
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id";

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Switching_the_registered_connection_string_redirects_the_very_next_run()
    {
        string altConnectionString = await SeedAltDatabaseAsync();

        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        await registry.SaveAsync(new SourceDefinition("sales-db", "sqlite",
            new Dictionary<string, object?> { ["connectionString"] = _fixture.ConnectionString }), CancellationToken.None);

        var services = new SingleServiceProvider(registry);
        var source = Source.SqliteNamed("sales-db", Sql).Keyset<Sale, long>(v => v.Id, pageSize: 10);
        ((INamedSourceResolver)source).AttachServices(services);

        var first = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        first.Records.ShouldContain(r => r.Customer == "C1");
        first.Records.ShouldNotContain(r => r.Customer == "AltCo");

        await registry.SaveAsync(new SourceDefinition("sales-db", "sqlite",
            new Dictionary<string, object?> { ["connectionString"] = altConnectionString }), CancellationToken.None);
        ((INamedSourceResolver)source).AttachServices(services);

        var second = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        second.Records.ShouldContain(r => r.Customer == "AltCo");
        second.Records.ShouldNotContain(r => r.Customer == "C1");
    }

    private async Task<string> SeedAltDatabaseAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_altPath)!);
        var altConnectionString = $"Data Source={_altPath}";

        await using var altConnection = new SqliteConnection(altConnectionString);
        await altConnection.OpenAsync();
        await using var seed = altConnection.CreateCommand();
        seed.CommandText = """
            CREATE TABLE Sales (Id INTEGER PRIMARY KEY, Customer TEXT NOT NULL, Amount REAL NOT NULL, Date TEXT NOT NULL);
            INSERT INTO Sales (Id, Customer, Amount, Date) VALUES (9001, 'AltCo', 1.00, '2026-02-02');
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_altPath))
            File.Delete(_altPath);
        GC.SuppressFinalize(this);
    }
}
