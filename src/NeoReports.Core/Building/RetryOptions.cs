namespace NeoReports.Core.Building;

/// <summary>Backoff shape applied between retry attempts.</summary>
public enum RetryBackoff
{
    /// <summary>The same delay between every attempt.</summary>
    Constant,

    /// <summary>Exponentially growing delay between attempts.</summary>
    Exponential
}

/// <summary>
/// Declarative retry configuration. Compiled into a Polly v8 resilience pipeline by the Core
/// engine; the builder owns no Polly types so the public surface stays small.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>
    /// Total number of attempts, including the first. <c>1</c> (the default) means <b>no retries</b> —
    /// a transient source blip fails the batch immediately and, under the default abort strategy,
    /// aborts the report. Enabling retries is strongly recommended for a production deployment against
    /// a network source; the one-call <c>ReportBuilder&lt;T&gt;.Retry()</c> applies a sensible default.
    /// </summary>
    public int Attempts { get; private set; } = 1;

    /// <summary>Backoff shape between attempts.</summary>
    public RetryBackoff Backoff { get; private set; } = RetryBackoff.Constant;

    /// <summary>Base delay used for the first retry (and scaled for exponential backoff).</summary>
    public TimeSpan BaseDelay { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether to add randomized jitter to the delays.</summary>
    public bool UseJitter { get; private set; }

    /// <summary>
    /// Optional per-attempt timeout for a batch read. <c>null</c> (the default) means no timeout —
    /// a read can block indefinitely. When set, each read attempt is bounded by cancelling its
    /// <see cref="System.Threading.CancellationToken"/>; a timed-out attempt is retried like any
    /// other transient failure (up to <see cref="Attempts"/>). The bound is <b>cooperative</b>: it
    /// only interrupts a read that honors cancellation (the async ADO/HTTP/Mongo reads do). A read
    /// stuck in genuinely non-cancellable I/O is not forcibly abandoned.
    /// </summary>
    public TimeSpan? AttemptTimeout { get; private set; }

    /// <summary>Sets the total number of attempts (including the first).</summary>
    /// <param name="attempts">A value of at least 1.</param>
    public RetryOptions MaxAttempts(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        Attempts = attempts;
        return this;
    }

    /// <summary>Uses exponential backoff with the given base delay.</summary>
    /// <param name="baseDelay">Base delay for the first retry.</param>
    public RetryOptions Exponential(TimeSpan baseDelay)
    {
        Backoff = RetryBackoff.Exponential;
        BaseDelay = baseDelay;
        return this;
    }

    /// <summary>Uses a constant delay between attempts.</summary>
    /// <param name="delay">Delay between attempts.</param>
    public RetryOptions Constant(TimeSpan delay)
    {
        Backoff = RetryBackoff.Constant;
        BaseDelay = delay;
        return this;
    }

    /// <summary>Adds randomized jitter to the delays.</summary>
    public RetryOptions WithJitter()
    {
        UseJitter = true;
        return this;
    }

    /// <summary>
    /// Bounds each batch-read attempt: if a read does not complete within <paramref name="timeout"/>
    /// its cancellation token is cancelled and the attempt is treated as a transient failure (retried
    /// up to <see cref="Attempts"/>). Protects the single worker (rule 6) from a slow or half-open
    /// source read blocking a job — for any read that honors cancellation. Off by default.
    /// </summary>
    /// <param name="timeout">A positive per-attempt timeout.</param>
    public RetryOptions Timeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        AttemptTimeout = timeout;
        return this;
    }
}
