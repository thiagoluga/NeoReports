namespace NeoReports.Core.Pipeline;

/// <summary>
/// Optional capability an exception thrown from a batch read can implement to suggest the delay
/// before the next retry attempt (ADR D61) — e.g. honoring an HTTP <c>Retry-After</c> header. A
/// <c>null</c> <see cref="RetryAfter"/> falls back to the report's configured backoff, exactly as if
/// the exception didn't implement this interface at all.
/// </summary>
public interface IRetryDelayHint
{
    /// <summary>The suggested delay before the next attempt, or <c>null</c> to use the configured backoff.</summary>
    TimeSpan? RetryAfter { get; }
}
