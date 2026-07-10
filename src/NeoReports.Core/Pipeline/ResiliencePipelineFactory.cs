using NeoReports.Core.Building;
using Polly;
using Polly.Retry;

namespace NeoReports.Core.Pipeline;

/// <summary>Compiles declarative <see cref="RetryOptions"/> into a Polly v8 resilience pipeline.</summary>
internal static class ResiliencePipelineFactory
{
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

            builder.AddRetry(retry);
        }

        return builder.Build();
    }
}
