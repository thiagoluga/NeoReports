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

        var dir = JobDir(jobId);
        Directory.CreateDirectory(dir);

        // Strip any path components from the caller-supplied file name so it cannot escape the
        // job directory (the same guard FileSystemArtifactStore uses).
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            throw new ArgumentException("File name must be a simple file name.", nameof(fileName));
        var target = Path.Combine(dir, safeFileName);

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(target + ".mime", mimeType, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReportArtifact>> ListAsync(string jobId, CancellationToken cancellationToken)
    {
        var dir = JobDir(jobId);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<ReportArtifact>>(Array.Empty<ReportArtifact>());

        var artifacts = new List<ReportArtifact>();
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if (path.EndsWith(".mime", StringComparison.Ordinal))
                continue;

            var mimePath = path + ".mime";
            var mime = File.Exists(mimePath) ? File.ReadAllText(mimePath) : "application/octet-stream";
            var info = new FileInfo(path);
            artifacts.Add(new ReportArtifact(Path.GetFileName(path), mime, path, info.Length));
        }

        return Task.FromResult<IReadOnlyList<ReportArtifact>>(artifacts);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        var dir = JobDir(jobId);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a locked/permission-denied partial dir must not fail the caller.
        }

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

    private string JobDir(string jobId)
    {
        // Guard against path traversal from a caller-supplied job id.
        var safe = Path.GetFileName(jobId);
        if (string.IsNullOrEmpty(safe) || safe != jobId)
            throw new ArgumentException("Invalid job id.", nameof(jobId));
        return Path.Combine(_options.Directory, safe);
    }
}
