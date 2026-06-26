namespace NeoReports.Core.Artifacts;

/// <summary>A finished report file retained for later retrieval (e.g. API download).</summary>
/// <param name="FileName">File name including extension.</param>
/// <param name="MimeType">MIME type of the content.</param>
/// <param name="Path">Absolute path of the stored copy.</param>
/// <param name="SizeBytes">Size of the content in bytes.</param>
public sealed record ReportArtifact(string FileName, string MimeType, string Path, long SizeBytes);

/// <summary>
/// Retains the finished output files of a report run so they can be retrieved after the run
/// completes (the pipeline otherwise stages output in a temp directory that is deleted at the end).
/// Optional: when no store is registered, the runner simply uploads to destinations and cleans up.
/// </summary>
/// <remarks>
/// This lives in Core (not the frozen <c>Abstractions</c>) because it is an engine/host concern,
/// not part of the public plugin contract (ADR D20).
/// </remarks>
public interface IReportArtifactStore
{
    /// <summary>Copies a finished output file into the store under the given job id.</summary>
    /// <param name="jobId">The job the artifact belongs to.</param>
    /// <param name="sourcePath">Path of the file to copy in.</param>
    /// <param name="fileName">File name to expose (including extension).</param>
    /// <param name="mimeType">MIME type of the content.</param>
    /// <param name="cancellationToken">Token that cancels the copy.</param>
    Task SaveAsync(string jobId, string sourcePath, string fileName, string mimeType, CancellationToken cancellationToken);

    /// <summary>Lists the artifacts stored for a job (empty when none).</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Token that cancels the lookup.</param>
    Task<IReadOnlyList<ReportArtifact>> ListAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Deletes all artifacts stored for a job (best-effort; no error if absent).</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Token that cancels the delete.</param>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}
