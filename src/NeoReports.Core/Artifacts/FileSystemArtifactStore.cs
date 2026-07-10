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
        await FileSystemArtifactLayout.SaveAsync(FileSystemArtifactLayout.JobDir(_root, jobId), sourcePath, fileName, mimeType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReportArtifact>> ListAsync(string jobId, CancellationToken cancellationToken) =>
        FileSystemArtifactLayout.ListAsync(FileSystemArtifactLayout.JobDir(_root, jobId));

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        FileSystemArtifactLayout.Delete(FileSystemArtifactLayout.JobDir(_root, jobId));
        return Task.CompletedTask;
    }
}
