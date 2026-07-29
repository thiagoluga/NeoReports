using System.Data;
using System.Data.Common;
using System.Globalization;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;

namespace NeoReports.Sources.Common;

/// <summary>
/// Provider-agnostic batch source using keyset pagination over any ADO.NET provider (D43): the
/// query must expose a cursor parameter for the key column (named per <see cref="_parameterPrefix"/>,
/// <c>@cursor</c> by default) and order by that key; each page reads up to <c>pageSize</c> rows
/// where the key is greater than the previous page's last key. A fresh connection is opened and
/// closed per page; the cursor is the opaque, serializable last-key value (<c>string?</c>).
/// Reused by every relational provider package (Postgres, MySQL, Oracle) — <c>NeoReports.Sources.Sql</c>
/// (SQL Server) predates this extraction and is left on its own, functionally identical,
/// implementation to avoid an unnecessary break of its already-published public API (D43).
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
public sealed class AdoKeysetSource<T> : IBatchSource<T>, ISourceRowCounter
{
    private readonly Func<DbConnection> _connectionFactory;
    private readonly string _sql;
    private readonly string _keyColumn;
    private readonly int _pageSize;
    private readonly IReadOnlyDictionary<string, object?> _parameters;
    private readonly Func<DbDataReader, IReadOnlyDictionary<string, int>, T> _materialize;
    private readonly string _parameterPrefix;
    private readonly Action<DbCommand>? _configureCommand;
    private readonly string _countInnerSuffix;

    /// <summary>Creates the source.</summary>
    /// <param name="connectionFactory">Creates a new, unopened connection for each page.</param>
    /// <param name="sql">Query with a cursor parameter and an ORDER BY on the key column.</param>
    /// <param name="keyColumn">Name of the keyset column in the result set.</param>
    /// <param name="pageSize">Maximum rows per page.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="options">Provider-specific extension knobs; defaults suit SQL Server/Postgres/MySQL.</param>
    public AdoKeysetSource(
        Func<DbConnection> connectionFactory,
        string sql,
        string keyColumn,
        int pageSize,
        ReportSchema schema,
        AdoProviderOptions? options = null)
        : this(connectionFactory, sql, keyColumn, pageSize, schema, materialize: null, options)
    {
    }

    /// <summary>
    /// Creates the source with a custom row materializer. Used by config-driven providers to
    /// materialize a positional <c>ReportRecord</c> by schema name; when <paramref name="materialize"/>
    /// is null the reflection-based <see cref="RecordMaterializer{T}"/> (typed POCO) is used.
    /// </summary>
    public AdoKeysetSource(
        Func<DbConnection> connectionFactory,
        string sql,
        string keyColumn,
        int pageSize,
        ReportSchema schema,
        Func<DbDataReader, IReadOnlyDictionary<string, int>, T>? materialize,
        AdoProviderOptions? options = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _keyColumn = keyColumn ?? throw new ArgumentNullException(nameof(keyColumn));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        options ??= new AdoProviderOptions();
        _parameters = options.Parameters ?? new Dictionary<string, object?>();
        _materialize = materialize ?? new RecordMaterializer<T>().Materialize;
        _parameterPrefix = string.IsNullOrEmpty(options.ParameterPrefix) ? "@" : options.ParameterPrefix;
        _configureCommand = options.ConfigureCommand;
        _countInnerSuffix = options.CountInnerSuffix;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using DbConnection connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = _sql;
        _configureCommand?.Invoke(command);

        // Merge run-time parameters from the execution with the source's static parameters.
        foreach (KeyValuePair<string, object?> kvp in _parameters)
            AddParameter(command, kvp.Key, kvp.Value, _parameterPrefix);
        foreach (KeyValuePair<string, object?> kvp in context.Execution.Parameters)
            AddParameter(command, kvp.Key, kvp.Value, _parameterPrefix);

        AddParameter(command, "cursor", DecodeCursor(context.Cursor), _parameterPrefix);

        // Cap rows per page without requiring TOP/OFFSET/LIMIT/ROWNUM in the user's SQL.
        AddParameter(command, "pageSize", _pageSize, _parameterPrefix);

        var records = new List<T>(_pageSize);
        string? lastKey = null;
        var read = 0;

        // SingleResult only: the record materializer reads columns by name (random access),
        // which is incompatible with SequentialAccess's forward-only column rule.
        await using DbDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleResult, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, int> ordinals = BuildOrdinalMap(reader);
        var keyOrdinal = ordinals.TryGetValue(_keyColumn, out var ko) ? ko : -1;

        while (read < _pageSize && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(_materialize(reader, ordinals));
            if (keyOrdinal >= 0 && !reader.IsDBNull(keyOrdinal))
                lastKey = KeysetCursorCodec.FormatKey(reader.GetValue(keyOrdinal), _keyColumn);
            read++;
        }

        var hasMore = records.Count == _pageSize && lastKey is not null;
        var nextCursor = hasMore ? EncodeCursor(lastKey!) : null;
        return new BatchResult<T>(records, nextCursor, hasMore);
    }

    /// <summary>
    /// Counts the rows a full run would read (ADR D47) by wrapping the report's own query as a
    /// derived table — <c>SELECT COUNT(*) FROM (&lt;sql&gt;) q</c> — and binding the exact same
    /// static/run-time parameters a real read would, with the cursor bound null (first-page/
    /// count-everything semantics).
    /// </summary>
    public async Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        await using DbConnection connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A trailing statement terminator is harmless standalone (ReadBatchAsync runs _sql as its
        // own statement) but is a syntax error inside a derived table — trim the common case of a
        // report author's copy-pasted trailing semicolon before wrapping.
        ReadOnlySpan<char> innerSql = _sql.AsSpan().TrimEnd().TrimEnd(';');

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM (\n{innerSql}{_countInnerSuffix}\n) q";
        _configureCommand?.Invoke(command);

        foreach (KeyValuePair<string, object?> kvp in _parameters)
            AddParameter(command, kvp.Key, kvp.Value, _parameterPrefix);
        foreach (KeyValuePair<string, object?> kvp in execution.Parameters)
            AddParameter(command, kvp.Key, kvp.Value, _parameterPrefix);

        AddParameter(command, "cursor", null, _parameterPrefix);
        AddParameter(command, "pageSize", _pageSize, _parameterPrefix);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, int> BuildOrdinalMap(DbDataReader reader)
    {
        var map = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            map[reader.GetName(i)] = i;
        return map;
    }

    private static void AddParameter(DbCommand command, string name, object? value, string parameterPrefix)
    {
        // Skip if the query doesn't reference this parameter, to avoid "too many parameters".
        var token = parameterPrefix + name;
        if (command.CommandText.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        // Avoid duplicates (run-time params may repeat static ones).
        foreach (DbParameter existing in command.Parameters)
        {
            if (string.Equals(existing.ParameterName, token, StringComparison.OrdinalIgnoreCase))
                return;
        }

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = token;
        if (value is null)
        {
            // Some providers (Postgres) can't infer a parameter's type from a null CLR value alone
            // and reject the query outright ("could not determine data type of parameter") unless
            // the parameter carries an explicit type — DbType.String is a safe default here since
            // callers needing a non-string null (e.g. Postgres's ::bigint cursor cast) apply their
            // own cast in the SQL text.
            parameter.DbType = DbType.String;
            parameter.Value = DBNull.Value;
        }
        else
        {
            parameter.Value = value;
        }

        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The cursor is the opaque string form of the last key value. On the first page it is
    /// <c>null</c>; the query is expected to treat a null cursor as "from the beginning"
    /// (e.g. <c>(@cursor IS NULL OR Id &gt; @cursor)</c>).
    /// </summary>
    private static string? DecodeCursor(string? cursor) => cursor;

    private static string EncodeCursor(string lastKey) => lastKey;
}
