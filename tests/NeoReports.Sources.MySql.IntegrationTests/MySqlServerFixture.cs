using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace NeoReports.Sources.MySql.IntegrationTests;

/// <summary>
/// Spins up an ephemeral MySQL container once per test class and seeds a Sales table.
/// Skipped automatically when Docker is unavailable.
/// </summary>
public sealed class MySqlServerFixture : IAsyncLifetime
{
    // The default Testcontainers app user only has grants on its own MYSQL_DATABASE — an
    // explicit, known root password lets tests that need a second database (cross-database
    // privileges) connect as root for just that administrative step.
    private const string RootPassword = "Testcontainers1!";

    private readonly MySqlContainer _container =
        new MySqlBuilder("mysql:8.0").WithEnvironment("MYSQL_ROOT_PASSWORD", RootPassword).Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public bool Available { get; private set; }
    public int SeededRows { get; private set; }

    /// <summary>The same server, authenticated as root — full privileges, for administrative steps
    /// the default app user can't do (e.g. creating another database).</summary>
    public string RootConnectionString =>
        new MySqlConnectionStringBuilder(ConnectionString) { UserID = "root", Password = RootPassword }.ConnectionString;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception) when (NeoReports.IntegrationTests.Support.DockerGate.SkipWhenUnavailable)
        {
            // Docker isn't available and CI hasn't demanded it (see DockerGate) — tests skip;
            // with NEOREPORTS_REQUIRE_DOCKER=1 the filter is false and this start failure propagates.
            Available = false;
            return;
        }

        ConnectionString = _container.GetConnectionString();
        await SeedAsync();
        Available = true;
    }

    private async Task SeedAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();

        await Execute(connection,
            "CREATE TABLE Sales (Id BIGINT PRIMARY KEY, Customer VARCHAR(100) NOT NULL, " +
            "Amount DECIMAL(18,2) NOT NULL, Date DATETIME NOT NULL);");

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

    private static async Task Execute(MySqlConnection connection, string sql)
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
