using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;
using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>
/// Spins up an ephemeral Oracle container once per test class and seeds a Sales table.
/// Skipped automatically when Docker is unavailable. Oracle containers are considerably slower to
/// start than SQL Server/PostgreSQL/MySQL, so this fixture is shared across the whole assembly.
/// </summary>
public sealed class OracleServerFixture : IAsyncLifetime
{
    // gvenzl/oracle-free honors ORACLE_PASSWORD as the SYS/SYSTEM password — a known value lets
    // tests that need a second schema (cross-schema isolation) connect as SYSTEM for that step.
    private const string SystemPassword = "Testcontainers1!";

    private readonly OracleContainer _container =
        new OracleBuilder("gvenzl/oracle-xe:21.3.0-slim-faststart").WithEnvironment("ORACLE_PASSWORD", SystemPassword).Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public bool Available { get; private set; }
    public int SeededRows { get; private set; }

    /// <summary>The same server, authenticated as SYSTEM — full privileges, for administrative
    /// steps the default app user can't do (e.g. creating another schema/user).</summary>
    public string SystemConnectionString =>
        new OracleConnectionStringBuilder(ConnectionString) { UserID = "system", Password = SystemPassword }.ConnectionString;

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
        var script = BuildSeedScript("Sales");
        var result = await _container.ExecScriptAsync(script);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to seed Oracle Sales table: {result.Stdout}\n{result.Stderr}");

        SeededRows = 2500;
    }

    /// <summary>
    /// Builds a script with individual INSERT statements — Oracle has no multi-row VALUES syntax
    /// (unlike SQL Server/PostgreSQL/MySQL), and ADO.NET can't batch multiple statements in one
    /// round trip outside a PL/SQL block, so seeding goes through sqlplus instead. The column is
    /// named <c>SaleDate</c>, not <c>Date</c> — Oracle rejects <c>DATE</c> as a bare column
    /// identifier in DDL/DML (<c>ORA-00904</c>); callers alias it back to <c>"Date"</c> in SELECT.
    /// <c>WHENEVER SQLERROR EXIT SQL.SQLCODE</c> is required so a bad statement fails the whole
    /// script with a non-zero exit code — sqlplus otherwise logs the error and keeps going.
    /// </summary>
    internal static string BuildSeedScript(string table)
    {
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("WHENEVER SQLERROR EXIT SQL.SQLCODE");
        sql.AppendLine($"CREATE TABLE {table} (Id NUMBER(19) PRIMARY KEY, Customer VARCHAR2(100) NOT NULL, Amount NUMBER(18,2) NOT NULL, SaleDate DATE NOT NULL);");

        const int total = 2500;
        for (var id = 1; id <= total; id++)
        {
            var amount = id % 7 == 0 ? "0.00" : (id * 1.5m).ToString(System.Globalization.CultureInfo.InvariantCulture);
            sql.AppendLine($"INSERT INTO {table} (Id, Customer, Amount, SaleDate) VALUES ({id}, 'C{id}', {amount}, DATE '2026-01-01');");
        }

        sql.AppendLine("COMMIT;");
        return sql.ToString();
    }

    public async Task DisposeAsync()
    {
        if (Available)
            await _container.DisposeAsync();
    }
}
