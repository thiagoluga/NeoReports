using NeoReports.Core.Building;
using Polly;
using Polly.Retry;

namespace NeoReports.Core.Pipeline;

/// <summary>Compiles declarative <see cref="RetryOptions"/> into a Polly v8 resilience pipeline.</summary>
internal static class ResiliencePipelineFactory
{
    // Caps how long any IRetryDelayHint (e.g. an HTTP Retry-After header, ADR D61) can push a
    // single retry delay to. Without a ceiling here, a misconfigured or hostile upstream returning
    // an extreme Retry-After (hours/days) would block this run's single worker for that entire
    // span (rule 6: a job is an atomic unit on one worker) — a fixed, generous-but-bounded ceiling
    // protects every current and future IRetryDelayHint implementer, not just this one HTTP source.
    private static readonly TimeSpan MaxHintedDelay = TimeSpan.FromMinutes(5);

    /// <summary>Builds a resilience pipeline for batch reads. Cancellation is never retried.</summary>
    /// <param name="options">The declarative retry options.</param>
    /// <param name="onRetry">Optional hook invoked on every retry attempt (ADR D38 job-event emission);
    /// awaited before the next attempt starts. Never affects retry behavior itself.</param>
    public static ResiliencePipeline Build(RetryOptions options, Func<int, TimeSpan, Exception?, ValueTask>? onRetry = null)
    {
        var builder = new ResiliencePipelineBuilder();

        if (options.Attempts > 1)
        {
            var retry = new RetryStrategyOptions
            {
                MaxRetryAttempts = options.Attempts - 1,
                BackoffType = options.Backoff == RetryBackoff.Exponential
                    ? DelayBackoffType.Exponential
                    : DelayBackoffType.Constant,
                Delay = options.BaseDelay,
                UseJitter = options.UseJitter,
                ShouldHandle = args => new ValueTask<bool>(
                    args.Outcome.Exception is not null and not OperationCanceledException)
            };

            if (onRetry is not null)
                retry.OnRetry = args => onRetry(args.AttemptNumber, args.RetryDelay, args.Outcome.Exception);

            // Lets a thrown exception suggest its own retry delay (e.g. an HTTP Retry-After header,
            // ADR D61) without a second resilience mechanism. Returning null falls back to the
            // configured backoff above, so this is a no-op for every exception that isn't an
            // IRetryDelayHint — byte-identical behavior for every pre-existing source. The hinted
            // delay is clamped to MaxHintedDelay regardless of what the hint requests.
            retry.DelayGenerator = args => new ValueTask<TimeSpan?>(
                args.Outcome.Exception is IRetryDelayHint { RetryAfter: { } delay }
                    ? (delay > MaxHintedDelay ? MaxHintedDelay : delay)
                    : null);

            builder.AddRetry(retry);
        }

        // Added after the retry so it is the INNER strategy: the timeout bounds each individual read
        // attempt, and a timed-out attempt surfaces as TimeoutRejectedException — which the retry's
        // ShouldHandle treats as a transient failure (it is not an OperationCanceledException), so a
        // slow read is retried and a persistently slow one fails the run in bounded time rather than
        // wedging the worker. With Attempts == 1 the timeout still applies to the single attempt.
        // Polly v8's timeout is cooperative: it cancels the attempt's token, so it bounds any read
        // that honors cancellation (the async ADO/HTTP/Mongo reads do), but does not forcibly abandon
        // a read stuck in genuinely non-cancellable I/O.
        if (options.AttemptTimeout is { } timeout)
            builder.AddTimeout(timeout);

        return builder.Build();
    }
}
