using System.Collections.Concurrent;
using NeoReports.Core.Configuration;

namespace NeoReports.Core.SourceRegistry;

/// <summary>Default <see cref="ISourceRegistry"/>: caches raw definitions, substitutes fresh per resolve.</summary>
public sealed class SourceRegistryService : ISourceRegistry
{
    private readonly ISourceRegistryStore _store;
    private readonly ConcurrentDictionary<string, SourceDefinition> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the registry over a persistence store.</summary>
    /// <param name="store">The underlying source definition store.</param>
    public SourceRegistryService(ISourceRegistryStore store) => _store = store;

    /// <inheritdoc />
    public async Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _store.SaveAsync(definition, cancellationToken).ConfigureAwait(false);
        _cache.TryRemove(definition.Name, out _);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        bool deleted = await _store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        _cache.TryRemove(name, out _);
        return deleted;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SourceDefinition?> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        if (!_cache.TryGetValue(name, out SourceDefinition? raw))
        {
            raw = await _store.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (raw is null)
                return null;

            _cache[name] = raw;
        }

        // Substitution always runs fresh from the cached raw definition — never cached itself —
        // so a changed environment variable is reflected on the very next call (ADR D42, locked
        // decision 2), with no need to re-read the definition from disk.
        var substitutedProperties = ReportConfigEnvironment.SubstituteProperties(raw.Properties);
        return raw with { Properties = substitutedProperties };
    }
}
