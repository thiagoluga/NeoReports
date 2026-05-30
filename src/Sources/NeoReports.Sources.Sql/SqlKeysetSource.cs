using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Sql;

/// <summary>
/// SQL Server batch source using keyset pagination. The query must expose a <c>@cursor</c>
/// parameter for the key column and order by that key; each page reads up to <c>pageSize</c> rows
/// where the key is greater than the previous page's last key. A fresh connection is opened and
/// closed per page (D2/D3): the cursor is the opaque, serializable last-key value (<c>string?</c>).
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
public sealed class SqlKeysetSource<T> : IBatchSource<T>
{
    private readonly string _connectionString;
    private readonly string _sql;
    private readonly string _keyColumn;
    private readonly int _pageSize;
    private readonly IReadOnlyDictionary<string, object?> _parameters;
    private readonly RecordMaterializer<T> _materializer = new();

    /// <summary>Creates the source.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="sql">Query with a <c>@cursor</c> parameter and an ORDER BY on the key column.</param>
    /// <param name="keyColumn">Name of the keyset column in the result set.</param>
    /// <param name="pageSize">Maximum rows per page.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="parameters">Static parameters bound on every page (besides <c>@cursor</c>).</param>
    public SqlKeysetSource(
        string connectionString,
        string sql,
        string keyColumn,
        int pageSize,
        ReportSchema schema,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _keyColumn = keyColumn ?? throw new ArgumentNullException(nameof(keyColumn));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _parameters = parameters ?? new Dictionary<string, object?>();
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = _sql;

        // Merge run-time parameters from the execution with the source's static parameters.
        foreach (var kvp in _parameters)
            AddParameter(command, kvp.Key, kvp.Value);
        foreach (var kvp in context.Execution.Parameters)
            AddParameter(command, kvp.Key, kvp.Value);

        AddParameter(command, "cursor", DecodeCursor(context.Cursor));

        // Cap rows per page without requiring TOP/OFFSET in the user's SQL.
        AddParameter(command, "pageSize", _pageSize);

        var records = new List<T>(_pageSize);
        string? lastKey = null;
        var read = 0;

        // SingleResult only: the record materializer reads columns by name (random access),
        // which is incompatible with SequentialAccess's forward-only column rule.
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleResult, cancellationToken)
            .ConfigureAwait(false);

        var ordinals = BuildOrdinalMap(reader);
        var keyOrdinal = ordinals.TryGetValue(_keyColumn, out var ko) ? ko : -1;

        while (read < _pageSize && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(_materializer.Materialize(reader, ordinals));
            if (keyOrdinal >= 0 && !reader.IsDBNull(keyOrdinal))
                lastKey = Convert.ToString(reader.GetValue(keyOrdinal), CultureInfo.InvariantCulture);
            read++;
        }

        var hasMore = records.Count == _pageSize && lastKey is not null;
        var nextCursor = hasMore && lastKey is not null ? EncodeCursor(lastKey) : null;
        return new BatchResult<T>(records, nextCursor, hasMore);
    }

    private static IReadOnlyDictionary<string, int> BuildOrdinalMap(DbDataReader reader)
    {
        var map = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            map[reader.GetName(i)] = i;
        return map;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        // Skip if the query doesn't reference this parameter, to avoid "too many parameters".
        var token = "@" + name;
        if (command.CommandText.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        // Avoid duplicates (run-time params may repeat static ones).
        foreach (DbParameter existing in command.Parameters)
        {
            if (string.Equals(existing.ParameterName, token, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = token;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The cursor is the opaque string form of the last key value. On the first page it is
    /// <c>null</c>; the query is expected to treat a null <c>@cursor</c> as "from the beginning"
    /// (e.g. <c>(@cursor IS NULL OR Id &gt; @cursor)</c>).
    /// </summary>
    private static object? DecodeCursor(string? cursor) => cursor;

    private static string EncodeCursor(string lastKey) => lastKey;
}
