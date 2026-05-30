using NeoReports.Abstractions;

namespace NeoReports.Core.Pipeline;

/// <summary>Final outcome of a report execution.</summary>
public enum ReportRunStatus
{
    /// <summary>All batches were read and written successfully.</summary>
    Completed,

    /// <summary>Finished, but one or more batches were skipped (see <see cref="ReportRunResult.SkippedBatches"/>).</summary>
    CompletedPartial,

    /// <summary>Aborted before completion.</summary>
    Failed
}

/// <summary>Result of running a report through the pipeline.</summary>
public sealed class ReportRunResult
{
    /// <summary>Creates a run result.</summary>
    /// <param name="status">Final status.</param>
    /// <param name="stats">Aggregate counters.</param>
    /// <param name="skippedBatches">Number of batches skipped by the failure strategy.</param>
    /// <param name="error">Failure reason when <see cref="Status"/> is <see cref="ReportRunStatus.Failed"/>.</param>
    /// <param name="uploads">Upload results, one per (output file × destination).</param>
    public ReportRunResult(
        ReportRunStatus status,
        JobStats stats,
        int skippedBatches,
        string? error,
        IReadOnlyList<UploadResult> uploads)
    {
        Status = status;
        Stats = stats;
        SkippedBatches = skippedBatches;
        Error = error;
        Uploads = uploads;
    }

    /// <summary>Final status.</summary>
    public ReportRunStatus Status { get; }

    /// <summary>Aggregate counters for the run.</summary>
    public JobStats Stats { get; }

    /// <summary>Number of batches skipped by the failure strategy.</summary>
    public int SkippedBatches { get; }

    /// <summary>Failure reason, when the run failed.</summary>
    public string? Error { get; }

    /// <summary>Upload results, one per (output file × destination).</summary>
    public IReadOnlyList<UploadResult> Uploads { get; }
}
