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
    /// <summary>Total number of attempts, including the first. <c>1</c> means no retries.</summary>
    public int Attempts { get; private set; } = 1;

    /// <summary>Backoff shape between attempts.</summary>
    public RetryBackoff Backoff { get; private set; } = RetryBackoff.Constant;

    /// <summary>Base delay used for the first retry (and scaled for exponential backoff).</summary>
    public TimeSpan BaseDelay { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether to add randomized jitter to the delays.</summary>
    public bool UseJitter { get; private set; }

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
}
