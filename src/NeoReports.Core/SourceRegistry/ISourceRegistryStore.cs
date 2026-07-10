namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// Persists named source definitions (ADR D42). Property bags are stored exactly as given —
/// including any <c>${VAR}</c> placeholders — the store never resolves environment variables;
/// that is <see cref="ISourceRegistry"/>'s job, at run time.
/// </summary>
public interface ISourceRegistryStore
{
    /// <summary>Creates or fully replaces the definition under <c>definition.Name</c>.</summary>
    /// <param name="definition">The source definition to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken);

    /// <summary>Reads a source definition by name, or <c>null</c> when it doesn't exist.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SourceDefinition?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Deletes a source definition. A no-op (returns <c>false</c>) when it doesn't exist.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a definition existed and was removed.</returns>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists every stored source definition.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken);
}
