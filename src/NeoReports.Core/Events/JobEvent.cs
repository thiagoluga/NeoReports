namespace NeoReports.Core.Events;

/// <summary>
/// One structured lifecycle event of a job run (ADR D38). Lives in Core, not the frozen
/// <c>Abstractions</c> — D9 removed the earlier <c>JobEvent</c>/<c>JobEventType</c> pair from the
/// ABI; the concept returns as an engine/host concern, the same precedent as
/// <c>IReportArtifactStore</c> (D20).
/// </summary>
/// <param name="JobId">The job this event belongs to.</param>
/// <param name="Sequence">Monotonic, per-job event ordinal (1-based).</param>
/// <param name="At">When the event was recorded (UTC).</param>
/// <param name="Type">One of the <see cref="JobEventTypes"/> constants.</param>
/// <param name="Message">Optional free-text detail (e.g. a truncated, sanitized exception message).</param>
/// <param name="Data">Optional structured fields specific to <paramref name="Type"/> (see <see cref="JobEventTypes"/>).</param>
public sealed record JobEvent(
    string JobId,
    int Sequence,
    DateTimeOffset At,
    string Type,
    string? Message,
    IReadOnlyDictionary<string, string>? Data);

/// <summary>
/// The closed vocabulary of <see cref="JobEvent.Type"/> values (ADR D38). New types are additive;
/// existing ones are never renamed, since they are part of a durable, persisted log.
/// </summary>
public static class JobEventTypes
{
    /// <summary>Runner entry, first execution of this job id. No <see cref="JobEvent.Data"/>.</summary>
    public const string RunStarted = "run-started";

    /// <summary>Runner entry when events for this job id already exist (crash re-execution). No <see cref="JobEvent.Data"/>.</summary>
    public const string RunRestarted = "run-restarted";

    /// <summary>A page was written to every output. Data: "page", "recordsRead", "recordsWritten" (cumulative), "elapsedMs".</summary>
    public const string PageCompleted = "page-completed";

    /// <summary>A batch-read retry attempt (Polly). Data: "page", "attempt", "delayMs", "exceptionType"; <see cref="JobEvent.Message"/> carries the exception message.</summary>
    public const string Retry = "retry";

    /// <summary>A batch was skipped after retries were exhausted. Data: "page", "reason".</summary>
    public const string BatchSkipped = "batch-skipped";

    /// <summary>An output file was finalized. Data: "fileName", "sizeBytes".</summary>
    public const string OutputsFinalized = "outputs-finalized";

    /// <summary>A finished file was uploaded to a destination. Data: "destinationType", "fileName".</summary>
    public const string UploadCompleted = "upload-completed";

    /// <summary>A finished file failed to upload to a destination. Data: "destinationType", "fileName"; <see cref="JobEvent.Message"/> carries the failure reason.</summary>
    public const string UploadFailed = "upload-failed";

    /// <summary>Terminal: the run finished (with or without skipped batches). No <see cref="JobEvent.Data"/>.</summary>
    public const string RunCompleted = "run-completed";

    /// <summary>Terminal: the run failed. <see cref="JobEvent.Message"/> carries the failure reason.</summary>
    public const string RunFailed = "run-failed";

    /// <summary>Terminal: the run was cancelled. No <see cref="JobEvent.Data"/>.</summary>
    public const string RunCancelled = "run-cancelled";

    /// <summary>
    /// A completed run read a different number of rows than the pre-run count reported (ADR D80).
    /// Advisory: the count is taken before the loop starts, so a concurrent write explains a small
    /// difference — but a keyset key that is not unique loses the tail of a duplicate group at a page
    /// boundary, and this is the only place that shows up at all.
    /// </summary>
    public const string RowCountMismatch = "row-count-mismatch";

    /// <summary>The per-job event cap was reached; appended exactly once, then every further event for this job is dropped. No <see cref="JobEvent.Data"/>.</summary>
    public const string EventsTruncated = "events-truncated";
}
