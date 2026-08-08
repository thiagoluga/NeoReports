using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

public sealed record ZonedRow(long Id, DateTime Ts);

/// <summary>
/// Empirically validates the cast ADR D81 makes the QueryBuilder emit for a keyset key declared
/// <c>timestamp with time zone</c>. <c>KeysetSqlGeneratorTests</c> pins <i>which</i> cast is emitted;
/// this pins that the cast is the <i>correct</i> one against a real PostgreSQL engine — the same
/// division of labour the Oracle <c>TO_TIMESTAMP</c> format-model fix used, because a plausible-looking
/// cast that the database interprets differently is exactly the failure this pair exists to catch.
/// <para>
/// The session runs in a deliberately non-UTC time zone. That is the whole point: under UTC both casts
/// agree, so a suite that never sets the zone would pass with the bug still in place.
/// </para>
/// </summary>
[Collection(nameof(PostgresServerCollection))]
public class PostgresZonedKeysetTests
{
    private readonly PostgresServerFixture _fixture;

    public PostgresZonedKeysetTests(PostgresServerFixture fixture) => _fixture = fixture;

    private const string ZonedCast =
        "SELECT Id, Ts FROM nr_zoned_keyset WHERE (@cursor IS NULL OR Ts > @cursor::timestamptz) ORDER BY Ts";

    // What the generator emitted before D81, kept as a live control: it must lose rows.
    private const string NaiveCast =
        "SELECT Id, Ts FROM nr_zoned_keyset WHERE (@cursor IS NULL OR Ts > @cursor::timestamp) ORDER BY Ts";

    /// <summary>
    /// The fixture's connection string with the session time zone forced well away from UTC. Npgsql
    /// passes <c>Options</c> through as libpq startup options, so every connection from the pool opens
    /// with it — including the fresh connection each page of a keyset read takes out.
    /// </summary>
    private string NonUtcConnectionString =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Options = "-c timezone=America/Sao_Paulo" }
            .ConnectionString;

    private static ReportExecutionContext Exec() =>
        new("job", "zoned", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task A_zoned_keyset_cursor_reads_every_row_under_a_non_utc_session()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");
        await SeedAsync();

        IReadOnlyList<ZonedRow> rows = await ReadAllAsync(ZonedCast);

        rows.Select(r => r.Id).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
        rows.Select(r => r.Ts).ShouldBeInOrder();
    }

    [SkippableFact]
    public async Task The_session_really_is_non_utc()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");

        // Guards the guard: if Npgsql ever stopped honouring Options, the test above would keep passing
        // for the wrong reason (a UTC session makes both casts agree) and silently stop covering D81.
        await using var connection = new NpgsqlConnection(NonUtcConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('TimeZone')";
        (await command.ExecuteScalarAsync()).ShouldBe("America/Sao_Paulo");
    }

    [SkippableFact]
    public async Task The_zone_less_cast_silently_drops_rows_which_is_the_bug_D81_fixes()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL container not available.");
        await SeedAsync();

        IReadOnlyList<ZonedRow> rows = await ReadAllAsync(NaiveCast);

        // ::timestamp discards the cursor's offset, then Postgres re-reads the naive value in the
        // session zone (-03:00) — pushing the boundary three hours forward and skipping everything
        // inside that window. The run still reports success, which is what made this so quiet.
        rows.Count.ShouldBeLessThan(5);
        rows.Select(r => r.Id).ShouldNotBe(new long[] { 1, 2, 3, 4, 5 });
    }

    private async Task<IReadOnlyList<ZonedRow>> ReadAllAsync(string sql)
    {
        var source = Source.Postgres(NonUtcConnectionString, sql).Keyset<ZonedRow, DateTime>(v => v.Ts, pageSize: 2);

        var all = new List<ZonedRow>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            BatchResult<ZonedRow> result = await source.ReadBatchAsync(
                new BatchContext(Exec(), 2, cursor, pages + 1), CancellationToken.None);
            all.AddRange(result.Records);
            pages++;
            if (!result.HasMore || pages > 20) // the cap keeps a paging regression from hanging the suite
                break;
            cursor = result.NextCursor;
        }

        return all;
    }

    // Five rows one hour apart. The gap has to exceed the session's UTC offset for the zone-less cast to
    // skip anything, so 1h steps under -03:00 put rows 2-4 inside the shifted window.
    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await Execute(connection, "CREATE TABLE IF NOT EXISTS nr_zoned_keyset (Id BIGINT PRIMARY KEY, Ts TIMESTAMPTZ NOT NULL)");
        await Execute(connection,
            "INSERT INTO nr_zoned_keyset (Id, Ts) VALUES " +
            "(1,'2026-01-01 00:00:00+00'),(2,'2026-01-01 01:00:00+00'),(3,'2026-01-01 02:00:00+00')," +
            "(4,'2026-01-01 03:00:00+00'),(5,'2026-01-01 04:00:00+00') ON CONFLICT (Id) DO NOTHING");
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
