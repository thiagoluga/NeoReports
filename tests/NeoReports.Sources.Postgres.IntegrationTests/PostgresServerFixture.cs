using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// Spins up an ephemeral PostgreSQL container once per test class and seeds a Sales table.
/// Skipped automatically when Docker is unavailable.
/// </summary>
public sealed class PostgresServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public bool Available { get; private set; }
    public int SeededRows { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker not available in this environment — tests will skip.
            Available = false;
            return;
        }

        ConnectionString = _container.GetConnectionString();
        await SeedAsync();
        Available = true;
    }

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await Execute(connection,
            "CREATE TABLE Sales (Id BIGINT PRIMARY KEY, Customer VARCHAR(100) NOT NULL, " +
            "Amount NUMERIC(18,2) NOT NULL, Date TIMESTAMP NOT NULL);");

        // 2500 rows so a pageSize of 1000 yields 3 pages (2 full + 1 partial).
        const int total = 2500;
        for (var start = 1; start <= total; start += 500)
        {
            var values = new List<string>();
            for (var id = start; id < start + 500 && id <= total; id++)
                values.Add($"({id}, '{$"C{id}"}', {(id % 7 == 0 ? "0.00" : (id * 1.5m).ToString(System.Globalization.CultureInfo.InvariantCulture))}, '2026-01-01')");
            await Execute(connection, "INSERT INTO Sales (Id, Customer, Amount, Date) VALUES " + string.Join(",", values) + ";");
        }

        SeededRows = total;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (Available)
            await _container.DisposeAsync();
    }
}
