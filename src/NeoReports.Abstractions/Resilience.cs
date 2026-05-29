namespace NeoReports.Abstractions;

// NOTE: Retry/backoff/classification are handled by Polly v8 directly in Core.
// The only resilience abstraction NeoReports owns is what to do AFTER retries are exhausted.

/// <summary>What the pipeline should do after a batch's retries are exhausted.</summary>
public enum FailureAction
{
    AbortReport,
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

    public FailureAction Action { get; }
    public string? Reason { get; }

    public static FailureDecision Abort(string reason) => new(FailureAction.AbortReport, reason);
    public static FailureDecision Skip(string? reason = null) => new(FailureAction.SkipBatch, reason);
}

/// <summary>Context passed to the failure strategy when a batch fails definitively.</summary>
public sealed class BatchFailureContext
{
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

    public ReportExecutionContext Execution { get; }
    public int PageNumber { get; }
    public string? Cursor { get; }
    public Exception Exception { get; }
    public int AttemptsExhausted { get; }
    public int ConsecutiveFailures { get; }
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
    string Name { get; }
    Task<FailureDecision> HandleAsync(BatchFailureContext context, CancellationToken cancellationToken);
}
