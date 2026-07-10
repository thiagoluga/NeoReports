namespace NeoReports.Core.Artifacts;

/// <summary>Options for <see cref="FileSystemPartialArtifactStore"/>.</summary>
public sealed class PartialArtifactOptions
{
    /// <summary>
    /// Directory under which per-job partial-artifact folders are created. Defaults to an absolute
    /// path under the system temp folder (the same pattern <see cref="FileSystemArtifactStore"/>
    /// uses) — deliberately not a relative path: <c>Results.File(string, ...)</c> resolves a
    /// relative path against the ASP.NET web root, not the process working directory, which would
    /// silently break the download endpoint for a relative default.
    /// </summary>
    public string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "neoreports-partials");

    /// <summary>Age after which a job's captured partials may be pruned. Default 7 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Retains the best-effort partial output of a job that failed or was cancelled mid-run (ADR D40).
/// Deliberately a <b>separate</b> interface from <see cref="IReportArtifactStore"/> — not a flag on
/// it — so no consumer can ever list a partial as a real, completed artifact by omission. Partials
/// never reach the report's real configured destinations (protects D2/D15's all-or-nothing publish
/// guarantee); this store is the only place they exist. Optional: when no store is registered, the
/// runner captures nothing and behaves exactly as it did before this feature existed.
/// </summary>
public interface IPartialArtifactStore
{
    /// <summary>Copies a partial output file into the store under the given job id.</summary>
    /// <param name="jobId">The job the partial artifact belongs to.</param>
    /// <param name="sourcePath">Path of the file to copy in.</param>
    /// <param name="fileName">File name to expose (including extension) — the caller is expected
    /// to have already marked it as partial (e.g. <c>report.partial.csv</c>).</param>
    /// <param name="mimeType">MIME type of the content.</param>
    /// <param name="cancellationToken">Token that cancels the copy.</param>
    Task SaveAsync(string jobId, string sourcePath, string fileName, string mimeType, CancellationToken cancellationToken);

    /// <summary>Lists the partial artifacts captured for a job (empty when none).</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Token that cancels the lookup.</param>
    Task<IReadOnlyList<ReportArtifact>> ListAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Deletes all partial artifacts captured for a job (best-effort; no error if absent).</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Token that cancels the delete.</param>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}
