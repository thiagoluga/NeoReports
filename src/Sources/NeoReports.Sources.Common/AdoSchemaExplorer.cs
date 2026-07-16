using System.Data.Common;
using System.Globalization;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Common;

/// <summary>
/// The three catalog queries a dialect supplies to <see cref="AdoSchemaExplorer"/>. Each returns a
/// fixed positional shape (read by ordinal, never by name — Oracle upper-cases catalog column
/// names, <c>information_schema</c> lower-cases them):
/// <list type="bullet">
/// <item><see cref="ColumnsSql"/>: (schema, table, column, dataType, isNullable) ordered by schema, table, ordinal position.</item>
/// <item><see cref="PrimaryKeysSql"/>: (schema, table, column) — one row per PK column.</item>
/// <item><see cref="ForeignKeysSql"/>: (schema, table, column, refSchema, refTable, refColumn) — one row per FK column.</item>
/// </list>
/// <c>isNullable</c> is read tolerantly: any value whose first character is <c>Y</c> (case-insensitive)
/// means nullable — covers <c>information_schema</c>'s <c>'YES'/'NO'</c> and Oracle's <c>'Y'/'N'</c>.
/// </summary>
public sealed record SchemaCatalogQueries(string ColumnsSql, string PrimaryKeysSql, string ForeignKeysSql);

/// <summary>
/// Shared <see cref="ISchemaExplorer"/> for every relational provider (ADR D49, Epic K): opens the
/// registered source's own connection and reads its system catalog. One instance is registered per
/// provider, parametrized by that dialect's <see cref="SchemaCatalogQueries"/>, identifier quoting,
/// and top-N preview syntax — the assembly logic itself is identical ADO.NET regardless of dialect.
/// Every identifier emitted into the preview SELECT comes from the catalog and is quoted, so the
/// query is never built from unescaped caller text.
/// </summary>
public sealed class AdoSchemaExplorer : ISchemaExplorer
{
    private readonly string _label;
    private readonly Func<string, DbConnection> _connectionFactory;
    private readonly SchemaCatalogQueries _queries;
    private readonly Func<string, string> _quoteIdentifier;
    private readonly Func<string, int, string> _buildPreviewSql;

    /// <summary>Hard engine-side ceiling on a preview's row count, regardless of what the caller requests.</summary>
    public const int MaxPreviewRows = 1000;

    /// <summary>Creates an explorer for one provider type.</summary>
    /// <param name="type">Source type id this explorer handles (e.g. "postgres").</param>
    /// <param name="label">Human-readable provider name for error messages (e.g. "PostgreSQL").</param>
    /// <param name="connectionFactory">Given the resolved connection string, creates a new, unopened connection.</param>
    /// <param name="queries">The dialect's three catalog queries.</param>
    /// <param name="quoteIdentifier">Quotes one identifier part for the preview SELECT — e.g. <see cref="QuoteAnsi"/> (Postgres/Oracle), <see cref="QuoteMySql"/>, <see cref="QuoteSqlServer"/>.</param>
    /// <param name="buildPreviewSql">Given the quoted, schema-qualified table name and the row cap, returns the full preview SELECT — dialects differ (<c>LIMIT</c> vs. <c>TOP</c> vs. <c>FETCH FIRST</c>).</param>
    public AdoSchemaExplorer(
        string type,
        string label,
        Func<string, DbConnection> connectionFactory,
        SchemaCatalogQueries queries,
        Func<string, string> quoteIdentifier,
        Func<string, int, string> buildPreviewSql)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        _label = label ?? throw new ArgumentNullException(nameof(label));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _quoteIdentifier = quoteIdentifier ?? throw new ArgumentNullException(nameof(quoteIdentifier));
        _buildPreviewSql = buildPreviewSql ?? throw new ArgumentNullException(nameof(buildPreviewSql));
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public async Task<SchemaCatalog> GetCatalogAsync(SourceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string connectionString = AdoConfigProperties.RequireString(definition.Properties, "connectionString", _label);

        await using DbConnection connection = _connectionFactory(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var columns = new Dictionary<TableKey, List<CatalogColumn>>();
        var order = new List<TableKey>();
        await ReadAsync(connection, _queries.ColumnsSql, r =>
        {
            var keyName = new TableKey(GetString(r, 0), GetString(r, 1));
            if (!columns.TryGetValue(keyName, out List<CatalogColumn>? list))
            {
                list = [];
                columns[keyName] = list;
                order.Add(keyName);
            }

            list.Add(new CatalogColumn(GetString(r, 2), GetString(r, 3), IsNullable(GetString(r, 4)), IsPrimaryKey: false));
        }, cancellationToken).ConfigureAwait(false);

        var primaryKeys = new HashSet<(TableKey Table, string Column)>();
        await ReadAsync(connection, _queries.PrimaryKeysSql, r =>
            primaryKeys.Add((new TableKey(GetString(r, 0), GetString(r, 1)), GetString(r, 2))), cancellationToken).ConfigureAwait(false);

        var foreignKeys = new Dictionary<TableKey, List<ForeignKey>>();
        await ReadAsync(connection, _queries.ForeignKeysSql, r =>
        {
            var keyName = new TableKey(GetString(r, 0), GetString(r, 1));
            if (!foreignKeys.TryGetValue(keyName, out List<ForeignKey>? list))
            {
                list = [];
                foreignKeys[keyName] = list;
            }

            list.Add(new ForeignKey(GetString(r, 2), GetString(r, 3), GetString(r, 4), GetString(r, 5)));
        }, cancellationToken).ConfigureAwait(false);

        var tables = new List<CatalogTable>(order.Count);
        foreach (TableKey key in order)
        {
            IReadOnlyList<CatalogColumn> cols = columns[key]
                .Select(c => primaryKeys.Contains((key, c.Name)) ? c with { IsPrimaryKey = true } : c)
                .ToArray();
            IReadOnlyList<ForeignKey> fks = foreignKeys.TryGetValue(key, out List<ForeignKey>? f) ? f : Array.Empty<ForeignKey>();
            tables.Add(new CatalogTable(key.Schema, key.Name, cols, fks));
        }

        tables.Sort((a, b) =>
        {
            int bySchema = string.CompareOrdinal(a.Schema, b.Schema);
            return bySchema != 0 ? bySchema : string.CompareOrdinal(a.Name, b.Name);
        });
        return new SchemaCatalog(tables);
    }

    /// <inheritdoc />
    public async Task<TablePreview> PreviewTableAsync(
        SourceDefinition definition, string schema, string table, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        string connectionString = AdoConfigProperties.RequireString(definition.Properties, "connectionString", _label);

        string qualified = string.IsNullOrEmpty(schema)
            ? _quoteIdentifier(table)
            : _quoteIdentifier(schema) + "." + _quoteIdentifier(table);
        // Defense in depth: the caller (the future preview endpoint) caps this at ~50, but the
        // engine keeps its own hard ceiling so a buggy or endpoint-bypassing caller can never turn a
        // bounded preview into an unbounded SELECT *.
        string sql = _buildPreviewSql(qualified, Math.Clamp(top, 1, MaxPreviewRows));

        await using DbConnection connection = _connectionFactory(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var names = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            names[i] = reader.GetName(i);

        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }

        return new TablePreview(names, rows);
    }

    private static async Task ReadAsync(DbConnection connection, string sql, Action<DbDataReader> onRow, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            onRow(reader);
    }

    private static string GetString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetValue(ordinal).ToString() ?? string.Empty;

    private static bool IsNullable(string value) =>
        value.Length > 0 && (value[0] is 'Y' or 'y');

    private readonly record struct TableKey(string Schema, string Name);

    /// <summary>ANSI double-quote identifier quoting (Postgres, Oracle): <c>foo</c> → <c>"foo"</c>, embedded <c>"</c> doubled.</summary>
    public static string QuoteAnsi(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>MySQL back-tick identifier quoting: <c>foo</c> → <c>`foo`</c>, embedded <c>`</c> doubled.</summary>
    public static string QuoteMySql(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>SQL Server bracket identifier quoting: <c>foo</c> → <c>[foo]</c>, embedded <c>]</c> doubled.</summary>
    public static string QuoteSqlServer(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Preview SELECT using a trailing <c>LIMIT</c> (Postgres, MySQL).</summary>
    public static string PreviewWithLimit(string qualifiedTable, int top) =>
        $"SELECT * FROM {qualifiedTable} LIMIT {top.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Preview SELECT using <c>SELECT TOP n</c> (SQL Server).</summary>
    public static string PreviewWithTop(string qualifiedTable, int top) =>
        $"SELECT TOP {top.ToString(CultureInfo.InvariantCulture)} * FROM {qualifiedTable}";

    /// <summary>Preview SELECT using <c>FETCH FIRST n ROWS ONLY</c> (Oracle 12c+).</summary>
    public static string PreviewWithFetchFirst(string qualifiedTable, int top) =>
        $"SELECT * FROM {qualifiedTable} FETCH FIRST {top.ToString(CultureInfo.InvariantCulture)} ROWS ONLY";
}
