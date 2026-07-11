using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Sql;

/// <summary>
/// SQL Server batch source that resolves its connection by name through the source registry
/// (ADR D42, <see cref="Source.SqlNamed"/>) instead of a fixed connection string. Per the ground
/// rule shared with the dynamic path's <c>RefBatchSource</c>, resolution happens fresh at the
/// start of every run — never baked into the source at construction — using
/// <see cref="BatchContext.Cursor"/> being <c>null</c> as the "first page of a run" signal.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class NamedSqlKeysetSource<T> : IBatchSource<T>, INamedSourceResolver
{
    private readonly string _sourceName;
    private readonly string _sql;
    private readonly string _keyColumn;
    private readonly int _pageSize;
    private IServiceProvider? _services;
    private SqlKeysetSource<T>? _resolved;

    public NamedSqlKeysetSource(string sourceName, string sql, string keyColumn, int pageSize, ReportSchema schema)
    {
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _keyColumn = keyColumn ?? throw new ArgumentNullException(nameof(keyColumn));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
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

    private async Task<SqlKeysetSource<T>> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_services is null)
            throw new ConfigurationException($"Source '{_sourceName}' was read before the run's services were attached (internal pipeline error).");

        var registry = _services.GetService(typeof(ISourceRegistry)) as ISourceRegistry
            ?? throw new ConfigurationException(
                $"Source.SqlNamed(\"{_sourceName}\", ...) requires a source registry, but none is configured on this host.");

        SourceDefinition? definition = await registry.ResolveAsync(_sourceName, cancellationToken).ConfigureAwait(false)
            ?? throw new ConfigurationException($"Source '{_sourceName}' is not registered.");

        if (definition.Properties is not { } properties
            || !properties.TryGetValue("connectionString", out var value)
            || value is not string connectionString
            || string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ConfigurationException($"Source '{_sourceName}' has no 'connectionString' property.");
        }

        return new SqlKeysetSource<T>(connectionString, _sql, _keyColumn, _pageSize, Schema);
    }
}
