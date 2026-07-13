using System.Data.Common;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using NeoReports.Core.Sources;

namespace NeoReports.Sources.Common;

/// <summary>
/// Provider-agnostic batch source that resolves its connection by name through the source
/// registry (ADR D42/D43) instead of a fixed connection string, shared by every relational
/// provider's <c>Source.&lt;Provider&gt;Named(...)</c> entry point. Resolution happens fresh at
/// the start of every run — never baked in at construction — using
/// <see cref="BatchContext.Cursor"/> being <c>null</c> as the "first page of a run" signal, the
/// same mechanism the dynamic path's <c>RefBatchSource</c> and F5's <c>NamedSqlKeysetSource</c>
/// use.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
public sealed class AdoNamedKeysetSource<T> : IBatchSource<T>, INamedSourceResolver, ISourceRowCounter
{
    private readonly string _sourceName;
    private readonly string _sql;
    private readonly string _keyColumn;
    private readonly int _pageSize;
    private readonly Func<string, DbConnection> _connectionFactory;
    private readonly string _parameterPrefix;
    private readonly Action<DbCommand>? _configureCommand;
    private readonly string _countInnerSuffix;
    private IServiceProvider? _services;
    private AdoKeysetSource<T>? _resolved;

    /// <summary>Creates the source.</summary>
    /// <param name="sourceName">Name of a source registered via <see cref="ISourceRegistry"/>.</param>
    /// <param name="sql">Query with a cursor parameter and an ORDER BY on the key column.</param>
    /// <param name="keyColumn">Name of the keyset column in the result set.</param>
    /// <param name="pageSize">Maximum rows per page.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="connectionFactory">Given the resolved connection string, creates a new, unopened connection.</param>
    /// <param name="options">Provider-specific extension knobs; defaults suit SQL Server/Postgres/MySQL.</param>
    public AdoNamedKeysetSource(
        string sourceName, string sql, string keyColumn, int pageSize, ReportSchema schema,
        Func<string, DbConnection> connectionFactory, AdoProviderOptions? options = null)
    {
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _keyColumn = keyColumn ?? throw new ArgumentNullException(nameof(keyColumn));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        options ??= new AdoProviderOptions();
        _parameterPrefix = options.ParameterPrefix;
        _configureCommand = options.ConfigureCommand;
        _countInnerSuffix = options.CountInnerSuffix;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public string SourceName => _sourceName;

    /// <inheritdoc />
    public void AttachServices(IServiceProvider services) => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Cursor is null)
            _resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (_resolved is null)
            throw new ConfigurationException($"Source '{_sourceName}' was not resolved before its first page was read (internal pipeline error).");

        return await _resolved.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the rows a full run would read (ADR D47). Resolves fresh through the registry, the
    /// same freshness rule <see cref="ReadBatchAsync"/> applies on the first page of a run — the
    /// resolved instance is not cached into <see cref="_resolved"/>, which is owned by that signal.
    /// </summary>
    public async Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken)
    {
        AdoKeysetSource<T> resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await resolved.CountAsync(execution, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdoKeysetSource<T>> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_services is null)
            throw new ConfigurationException($"Source '{_sourceName}' was read before the run's services were attached (internal pipeline error).");

        ISourceRegistry registry = _services.GetService(typeof(ISourceRegistry)) as ISourceRegistry
            ?? throw new ConfigurationException($"This named source requires a source registry, but none is configured on this host.");

        SourceDefinition? definition = await registry.ResolveAsync(_sourceName, cancellationToken).ConfigureAwait(false)
            ?? throw new ConfigurationException($"Source '{_sourceName}' is not registered.");

        if (definition.Properties is not { } properties
            || !properties.TryGetValue("connectionString", out var value)
            || value is not string connectionString
            || string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ConfigurationException($"Source '{_sourceName}' has no 'connectionString' property.");
        }

        return new AdoKeysetSource<T>(
            () => _connectionFactory(connectionString), _sql, _keyColumn, _pageSize, Schema,
            new AdoProviderOptions
            {
                ParameterPrefix = _parameterPrefix,
                ConfigureCommand = _configureCommand,
                CountInnerSuffix = _countInnerSuffix,
            });
    }
}
