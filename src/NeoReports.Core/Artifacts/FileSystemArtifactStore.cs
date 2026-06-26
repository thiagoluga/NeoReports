namespace NeoReports.Core.Artifacts;

/// <summary>
/// Stores report artifacts on the local filesystem under <c>{root}/{jobId}/{fileName}</c>. The
/// default root is a stable subdirectory of the system temp folder. Suitable for the single-worker
/// v1 (the download endpoint reads from the same machine that produced the file).
/// </summary>
public sealed class FileSystemArtifactStore : IReportArtifactStore
{
    private readonly string _root;

    /// <summary>Creates a store rooted at the default location (temp/neoreports-artifacts).</summary>
    public FileSystemArtifactStore()
        : this(Path.Combine(Path.GetTempPath(), "neoreports-artifacts"))
    {
    }

    /// <summary>Creates a store rooted at <paramref name="root"/>.</summary>
    /// <param name="root">Directory under which per-job artifact folders are created.</param>
    public FileSystemArtifactStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root path must be provided.", nameof(root));
        _root = root;
    }

    /// <inheritdoc />
    public async Task SaveAsync(string jobId, string sourcePath, string fileName, string mimeType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var dir = JobDir(jobId);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName);

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        }

        // Sidecar with the MIME type so List can report it without guessing from the extension.
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
        catch (Exception)
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    private string JobDir(string jobId)
    {
        // Guard against path traversal from a caller-supplied job id.
        var safe = Path.GetFileName(jobId);
        if (string.IsNullOrEmpty(safe) || safe != jobId)
            throw new ArgumentException("Invalid job id.", nameof(jobId));
        return Path.Combine(_root, safe);
    }
}
