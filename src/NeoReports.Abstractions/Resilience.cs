namespace NeoReports.Abstractions;

// NOTE: Retry/backoff/classification are handled by Polly v8 directly in Core.
// The only resilience abstraction NeoReports owns is what to do AFTER retries are exhausted.

/// <summary>What the pipeline should do after a batch's retries are exhausted.</summary>
public enum FailureAction
{
    /// <summary>Abort the whole report and mark the job failed.</summary>
    AbortReport,

    /// <summary>Skip the failed batch, log a warning, and continue (partial report).</summary>
    SkipBatch

    // PauseForReview, FallbackToCache, Custom -> post-MVP
}

/// <summary>The decision returned by an <see cref="IFailureStrategy"/>.</summary>
public sealed class FailureDecision
{
    private FailureDecision(FailureAction action, string? reason)
    {
        Action = action;
        Reason = reason;
    }

    /// <summary>The action to take.</summary>
    public FailureAction Action { get; }

    /// <summary>Optional human-readable reason for the decision.</summary>
    public string? Reason { get; }

    /// <summary>Creates a decision to abort the report.</summary>
    /// <param name="reason">Reason for aborting.</param>
    public static FailureDecision Abort(string reason) => new(FailureAction.AbortReport, reason);

    /// <summary>Creates a decision to skip the failed batch.</summary>
    /// <param name="reason">Optional reason for skipping.</param>
    public static FailureDecision Skip(string? reason = null) => new(FailureAction.SkipBatch, reason);
}

/// <summary>Context passed to the failure strategy when a batch fails definitively.</summary>
public sealed class BatchFailureContext
{
    /// <summary>Creates the context for a definitively failed batch.</summary>
    /// <param name="execution">The ambient execution context.</param>
    /// <param name="pageNumber">Index of the page that failed.</param>
    /// <param name="cursor">Cursor at the point of failure, if any.</param>
    /// <param name="exception">The exception that ended the retries.</param>
    /// <param name="attemptsExhausted">Number of attempts made before giving up.</param>
    /// <param name="consecutiveFailures">Number of consecutive batch failures so far.</param>
    /// <param name="totalFailures">Total number of batch failures so far.</param>
    /// <param name="failureRatio">Fraction of pages that have failed so far (0..1).</param>
    public BatchFailureContext(
        ReportExecutionContext execution,
        int pageNumber,
        string? cursor,
        Exception exception,
        int attemptsExhausted,
        int consecutiveFailures,
        int totalFailures,
        double failureRatio)
    {
        Execution = execution;
        PageNumber = pageNumber;
        Cursor = cursor;
        Exception = exception;
        AttemptsExhausted = attemptsExhausted;
        ConsecutiveFailures = consecutiveFailures;
        TotalFailures = totalFailures;
        FailureRatio = failureRatio;
    }

    /// <summary>The ambient execution context.</summary>
    public ReportExecutionContext Execution { get; }

    /// <summary>Index of the page that failed.</summary>
    public int PageNumber { get; }

    /// <summary>Cursor at the point of failure, if any.</summary>
    public string? Cursor { get; }

    /// <summary>The exception that ended the retries.</summary>
    public Exception Exception { get; }

    /// <summary>Number of attempts made before giving up.</summary>
    public int AttemptsExhausted { get; }

    /// <summary>Number of consecutive batch failures so far.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>Total number of batch failures so far.</summary>
    public int TotalFailures { get; }

    /// <summary>Fraction of pages that have failed so far (0..1).</summary>
    public double FailureRatio { get; }
}

/// <summary>
/// Decides what happens after a batch exhausts its retries. v1 ships two implementations:
/// AbortReport and SkipBatchAndLog (with threshold-based escalation handled in Core).
/// </summary>
public interface IFailureStrategy
{
    /// <summary>Stable strategy name (e.g. "abort", "skip-and-log").</summary>
    string Name { get; }

    /// <summary>Decides what to do for a definitively failed batch.</summary>
    /// <param name="context">Details of the failure.</param>
    /// <param name="cancellationToken">Token that cancels the decision.</param>
    /// <returns>The decision to apply.</returns>
    Task<FailureDecision> HandleAsync(BatchFailureContext context, CancellationToken cancellationToken);
}
