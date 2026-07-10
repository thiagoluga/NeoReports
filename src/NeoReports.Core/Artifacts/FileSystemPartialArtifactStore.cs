namespace NeoReports.Core.Artifacts;

/// <summary>
/// Stores partial-run artifacts on the local filesystem under
/// <c>{root}/{jobId}/{fileName}</c> — the same shape as <see cref="FileSystemArtifactStore"/>, but
/// a separate root and a separate type (ADR D40), so nothing can accidentally treat a partial as a
/// completed artifact. Prunes job directories older than <see cref="PartialArtifactOptions.Retention"/>
/// opportunistically on each save.
/// </summary>
public sealed class FileSystemPartialArtifactStore : IPartialArtifactStore
{
    private readonly PartialArtifactOptions _options;

    /// <summary>Creates a store with default options.</summary>
    public FileSystemPartialArtifactStore() : this(new PartialArtifactOptions())
    {
    }

    /// <summary>Creates a store with the given options.</summary>
    /// <param name="options">Directory/retention options.</param>
    public FileSystemPartialArtifactStore(PartialArtifactOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task SaveAsync(string jobId, string sourcePath, string fileName, string mimeType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        PruneExpired();

        await FileSystemArtifactLayout.SaveAsync(FileSystemArtifactLayout.JobDir(_options.Directory, jobId), sourcePath, fileName, mimeType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReportArtifact>> ListAsync(string jobId, CancellationToken cancellationToken) =>
        FileSystemArtifactLayout.ListAsync(FileSystemArtifactLayout.JobDir(_options.Directory, jobId));

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        FileSystemArtifactLayout.Delete(FileSystemArtifactLayout.JobDir(_options.Directory, jobId));
        return Task.CompletedTask;
    }

    private void PruneExpired()
    {
        if (!Directory.Exists(_options.Directory))
            return;

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.Retention;
        try
        {
            IEnumerable<string> expired = Directory.EnumerateDirectories(_options.Directory)
                .Where(dir => Directory.GetLastWriteTimeUtc(dir) < cutoff);
            foreach (var dir in expired)
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort pruning: a locked directory must not break a save.
        }
    }
}
