namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// Persists named source definitions (ADR D42). Property bags are stored exactly as given —
/// including any <c>${VAR}</c> placeholders — the store never resolves environment variables;
/// that is <see cref="ISourceRegistry"/>'s job, at run time.
/// </summary>
/// <remarks>
/// <b>Names.</b> A write rejects a name the store cannot key — it becomes a file name in a
/// file-backed implementation, so this is what keeps a caller-supplied name from becoming a path.
/// A <i>read</i> or <i>delete</i> under such a name is a miss rather than an error: an implementation
/// returns <c>null</c>/<c>false</c> instead of throwing, because a lookup for something that could
/// never have been written is simply not found. Throwing there made an endpoint answer 500 for a name
/// that a plain 404 already describes.
/// </remarks>
public interface ISourceRegistryStore
{
    /// <summary>Creates or fully replaces the definition under <c>definition.Name</c>.</summary>
    /// <param name="definition">The source definition to persist.</param>
    /// <exception cref="ArgumentException">Thrown when the name is one the store cannot key.</exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a source definition by name, or <c>null</c> when it doesn't exist — including when the
    /// name is one this store could never have written.
    /// </summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SourceDefinition?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a source definition. A no-op (returns <c>false</c>) when it doesn't exist, including
    /// when the name is one this store could never have written.
    /// </summary>
    /// <param name="name">The source name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a definition existed and was removed.</returns>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists every stored source definition.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken);
}
