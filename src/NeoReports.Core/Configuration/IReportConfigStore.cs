namespace NeoReports.Core.Configuration;

/// <summary>
/// Persists dynamic report config documents so runtime-registered reports (ADR D33) survive a
/// restart. v1 ships <see cref="FileReportConfigStore"/>, consistent with the single-server
/// philosophy (CLAUDE.md rule 6) — a multi-server store is out of scope.
/// </summary>
public interface IReportConfigStore
{
    /// <summary>Saves (creates or overwrites) the raw config document under the report name.</summary>
    /// <param name="name">The report name; must satisfy <see cref="DynamicReportName.IsValid"/>.</param>
    /// <param name="configDocument">The raw serialized configuration (the original document, with any <c>${VAR}</c> placeholders unresolved).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(string name, string configDocument, CancellationToken cancellationToken);

    /// <summary>Deletes the stored document. Returns <c>false</c> when none was stored.</summary>
    /// <param name="name">The report name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>True when a document is stored under the name.</summary>
    /// <param name="name">The report name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>All stored documents, as (name, document) pairs, ordered by name.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<(string Name, string Document)>> ListAsync(CancellationToken cancellationToken);
}
