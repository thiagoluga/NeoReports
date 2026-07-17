using System.Data.Common;
using System.Globalization;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Sqlite;

/// <summary>
/// <see cref="ISchemaExplorer"/> for SQLite (ADR D49/D56, <c>type: "sqlite"</c>). Unlike the other
/// relational providers, this is a bespoke implementation rather than an <see cref="AdoSchemaExplorer"/>
/// instantiation: SQLite has no <c>information_schema</c>, so catalog introspection reads
/// <c>sqlite_master</c> for the table list, then <c>PRAGMA table_info</c>/<c>PRAGMA foreign_key_list</c>
/// **once per table** — a shape <see cref="AdoSchemaExplorer"/>'s three whole-catalog queries can't
/// express. Table identifiers interpolated into a per-table <c>PRAGMA</c> call (which, unlike a normal
/// query, cannot bind the table name as a parameter) are quoted with
/// <see cref="AdoSchemaExplorer.QuoteAnsi"/> — for <see cref="GetCatalogAsync"/> they only ever come
/// from <c>sqlite_master</c> itself (never caller input), and for <see cref="PreviewTableAsync"/> the
/// quoting is what keeps a caller-supplied table name injection-safe, exactly like every other explorer.
/// </summary>
public sealed class SqliteSchemaExplorer : ISchemaExplorer
{
    // SQLite's default (and, absent ATTACH DATABASE, only) database name — the same "one connection
    // string, one catalog" scope every other provider's Schema value represents (Postgres/SQL Server's
    // real schema, MySQL's current database).
    private const string SchemaName = "main";

    private readonly Func<string, DbConnection> _connectionFactory;

    /// <summary>Creates the explorer.</summary>
    /// <param name="connectionFactory">Given the resolved connection string, creates a new, unopened connection.</param>
    public SqliteSchemaExplorer(Func<string, DbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <inheritdoc />
    public string Type => "sqlite";

    /// <inheritdoc />
    public async Task<SchemaCatalog> GetCatalogAsync(SourceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string connectionString = AdoConfigProperties.RequireString(definition.Properties, "connectionString", "SQLite");

        await using DbConnection connection = _connectionFactory(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> tableNames = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);

        // SQLite identifiers (including a REFERENCES clause's table name) are matched case-insensitively
        // — a FK can name its target with different casing than sqlite_master's own stored name.
        var columnsByTable = new Dictionary<string, IReadOnlyList<CatalogColumn>>(StringComparer.OrdinalIgnoreCase);
        foreach (string tableName in tableNames)
            columnsByTable[tableName] = await ReadColumnsAsync(connection, tableName, cancellationToken).ConfigureAwait(false);

        var tables = new List<CatalogTable>(tableNames.Count);
        foreach (string tableName in tableNames)
        {
            IReadOnlyList<ForeignKey> foreignKeys = await ReadForeignKeysAsync(
                connection, tableName, columnsByTable, cancellationToken).ConfigureAwait(false);
            tables.Add(new CatalogTable(SchemaName, tableName, columnsByTable[tableName], foreignKeys));
        }

        return new SchemaCatalog(tables);
    }

    /// <inheritdoc />
    public async Task<TablePreview> PreviewTableAsync(
        SourceDefinition definition, string schema, string table, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        string connectionString = AdoConfigProperties.RequireString(definition.Properties, "connectionString", "SQLite");

        // SQLite has no meaningful schema qualifier beyond "main" (see SchemaName above), so `schema`
        // is accepted (matching the ISchemaExplorer contract) but not used to qualify the table.
        string sql = AdoSchemaExplorer.PreviewWithLimit(
            AdoSchemaExplorer.QuoteAnsi(table), Math.Clamp(top, 1, AdoSchemaExplorer.MaxPreviewRows));

        await using DbConnection connection = _connectionFactory(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var names = new List<string>();
        var rows = new List<object?[]>();
        await ReadAsync(connection, sql, reader =>
        {
            if (names.Count == 0)
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    names.Add(reader.GetName(i));
            }

            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }, cancellationToken).ConfigureAwait(false);

        return new TablePreview(names, rows);
    }

    private static async Task<List<string>> ReadTableNamesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await ReadAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            reader => names.Add(reader.GetString(0)),
            cancellationToken).ConfigureAwait(false);
        return names;
    }

    // PRAGMA table_info(<table>) columns (by ordinal): cid, name, type, notnull, dflt_value, pk.
    // Rows are already returned in column (cid) order. `type` is never NULL — an untyped column
    // (`CREATE TABLE t(x)`) reports an empty string, which GetString reads fine as "".
    private static async Task<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(
        DbConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var columns = new List<CatalogColumn>();
        await ReadAsync(connection, $"PRAGMA table_info({AdoSchemaExplorer.QuoteAnsi(tableName)})", reader =>
        {
            columns.Add(new CatalogColumn(
                Name: reader.GetString(1),
                DataType: reader.GetString(2),
                Nullable: !IsFlagSet(reader, 3),
                IsPrimaryKey: IsFlagSet(reader, 5)));
        }, cancellationToken).ConfigureAwait(false);
        return columns;
    }

    // PRAGMA foreign_key_list(<table>) columns (by ordinal): id, seq, table (referenced), from (local
    // column), to (referenced column — NULL/empty means "the referenced table's own primary key", a
    // valid shorthand SQLite's FOREIGN KEY syntax allows when the referenced column is omitted).
    private static async Task<IReadOnlyList<ForeignKey>> ReadForeignKeysAsync(
        DbConnection connection, string tableName, IReadOnlyDictionary<string, IReadOnlyList<CatalogColumn>> columnsByTable,
        CancellationToken cancellationToken)
    {
        var foreignKeys = new List<ForeignKey>();
        await ReadAsync(connection, $"PRAGMA foreign_key_list({AdoSchemaExplorer.QuoteAnsi(tableName)})", reader =>
        {
            string referencedTable = reader.GetString(2);
            string referencedColumn = reader.IsDBNull(4) || reader.GetString(4).Length == 0
                ? ResolvePrimaryKeyColumn(columnsByTable, referencedTable)
                : reader.GetString(4);

            foreignKeys.Add(new ForeignKey(reader.GetString(3), SchemaName, referencedTable, referencedColumn));
        }, cancellationToken).ConfigureAwait(false);
        return foreignKeys;
    }

    // The referenced table's declared PRIMARY KEY column, or SQLite's implicit "rowid" when the
    // referenced table declares none — an ordinary table (the common case; a WITHOUT ROWID table has
    // no such fallback, but that's a deliberately rare opt-in this best-effort resolution doesn't
    // special-case) always has a rowid, and SQLite's own FK-omitted-column shorthand resolves to it.
    private static string ResolvePrimaryKeyColumn(
        IReadOnlyDictionary<string, IReadOnlyList<CatalogColumn>> columnsByTable, string referencedTable) =>
        columnsByTable.TryGetValue(referencedTable, out IReadOnlyList<CatalogColumn>? columns)
            ? columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name ?? "rowid"
            : "rowid";

    private static bool IsFlagSet(DbDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;

    // Shared read loop for every PRAGMA/sqlite_master/preview query this explorer runs — mirrors
    // AdoSchemaExplorer's own private ReadAsync helper, kept local since this explorer's catalog shape
    // (one PRAGMA per table) doesn't fit that shared class's three-whole-catalog-query API.
    private static async Task ReadAsync(DbConnection connection, string sql, Action<DbDataReader> onRow, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            onRow(reader);
    }
}
