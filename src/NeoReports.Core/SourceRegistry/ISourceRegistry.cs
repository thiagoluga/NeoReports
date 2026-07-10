namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// Resolves named source definitions for report execution (ADR D42) — a thin layer over
/// <see cref="ISourceRegistryStore"/> that substitutes <c>${VAR}</c> placeholders **at resolve
/// time** (not at save time), so editing a source's environment (rotating a connection string)
/// takes effect on the next run of every referencing report without recompiling anything. An
/// in-memory read-through cache of the raw (unsubstituted) definitions avoids repeated disk I/O
/// across runs, invalidated whenever a definition is saved or deleted through this interface;
/// substitution itself is never cached, so a changed environment variable is always reflected
/// on the very next call.
/// </summary>
public interface ISourceRegistry
{
    /// <summary>Creates or fully replaces a source definition and invalidates its cached entry.</summary>
    /// <param name="definition">The source definition to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken);

    /// <summary>Deletes a source definition and invalidates its cached entry.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a definition existed and was removed.</returns>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists every registered source definition, as stored (placeholders unsubstituted).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads a source definition by name, as stored (placeholders unsubstituted), or <c>null</c>
    /// when it doesn't exist. Prefer this over <see cref="ResolveAsync"/> when only metadata is
    /// needed (e.g. rendering source details) — unlike <see cref="ResolveAsync"/>, this never
    /// throws for a missing environment variable.
    /// </summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SourceDefinition?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a source definition by name with every <c>${VAR}</c> property placeholder
    /// substituted from the current environment, or <c>null</c> when no definition exists under
    /// that name.
    /// </summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Abstractions.ConfigurationException">Thrown when a referenced environment variable is not set.</exception>
    Task<SourceDefinition?> ResolveAsync(string name, CancellationToken cancellationToken);
}
