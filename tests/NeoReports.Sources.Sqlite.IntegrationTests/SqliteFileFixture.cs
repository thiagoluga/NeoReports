using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace NeoReports.Sources.Sqlite.IntegrationTests;

/// <summary>
/// Creates a temp-file-backed SQLite database and seeds a Sales table, once per test class. Unlike
/// the other providers' Testcontainers fixtures, this needs no Docker and never skips — SQLite is
/// embedded, so the file always exists (ADR D56). A real temp file is used rather than
/// <c>:memory:</c> because <c>AdoKeysetSource</c> opens a new connection per page, and a plain
/// in-memory SQLite database is private per-connection.
/// </summary>
public sealed class SqliteFileFixture : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nr-sqlite-tests", $"{Guid.NewGuid():N}.db");

    public string ConnectionString { get; private set; } = string.Empty;
    public int SeededRows { get; private set; }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        ConnectionString = $"Data Source={_path}";
        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await Execute(connection,
            "CREATE TABLE Sales (Id INTEGER PRIMARY KEY, Customer TEXT NOT NULL, Amount REAL NOT NULL, Date TEXT NOT NULL);");

        // 2500 rows so a pageSize of 1000 yields 3 pages (2 full + 1 partial). Date cycles through
        // 2026-01-01..2026-01-28 (id 1 lands on 2026-01-01, preserving the fixed-date assertions
        // elsewhere) — zero-padded ISO-8601 text, so a date filter's lexicographic TEXT comparison
        // matches chronological order, and there's real variety to filter on (unlike a constant date).
        const int total = 2500;
        for (var start = 1; start <= total; start += 500)
        {
            var values = new List<string>();
            for (var id = start; id < start + 500 && id <= total; id++)
            {
                var day = 1 + (id - 1) % 28;
                values.Add($"({id}, 'C{id}', {(id % 7 == 0 ? "0.00" : (id * 1.5m).ToString(CultureInfo.InvariantCulture))}, '2026-01-{day:D2}')");
            }
            await Execute(connection, "INSERT INTO Sales (Id, Customer, Amount, Date) VALUES " + string.Join(",", values) + ";");
        }

        SeededRows = total;
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        // Releases the file handle Microsoft.Data.Sqlite's connection pool keeps open, so the delete
        // below doesn't race a still-locked file on Windows.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }
}
