using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;

namespace NeoReports.Core.Resilience;

/// <summary>Aborts the report on the first definitively failed batch.</summary>
public sealed class AbortStrategy : IFailureStrategy
{
    /// <inheritdoc />
    public string Name => "abort";

    /// <inheritdoc />
    public Task<FailureDecision> HandleAsync(BatchFailureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The reason is persisted as the run's error (ReportRunResult.Error, the RunFailed event, and
        // GET /jobs). NeoReports' own exceptions carry curated, secret-free messages and are kept; any
        // other exception (a driver exception, whose message can echo the connection string) is
        // reduced to its type name. The full exception is logged through ILogger by the runner's
        // abort path for diagnosis regardless.
        var detail = context.Exception is NeoReportsException
            ? context.Exception.Message
            : context.Exception.GetType().Name;
        return Task.FromResult(FailureDecision.Abort(
            $"Batch {context.PageNumber} failed after {context.AttemptsExhausted} attempt(s): {detail}"));
    }
}

/// <summary>
/// Skips definitively failed batches and logs a structured warning, marking the report partial.
/// An optional threshold predicate escalates to an abort (e.g. too many consecutive failures).
/// </summary>
public sealed class SkipAndLogStrategy : IFailureStrategy
{
    private readonly Func<ThresholdContext, bool>? _abortIf;

    /// <summary>Creates the strategy.</summary>
    /// <param name="abortIf">Optional predicate that, when true, escalates to an abort.</param>
    public SkipAndLogStrategy(Func<ThresholdContext, bool>? abortIf = null) => _abortIf = abortIf;

    /// <inheritdoc />
    public string Name => "skip-and-log";

    /// <inheritdoc />
    public Task<FailureDecision> HandleAsync(BatchFailureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_abortIf is not null && _abortIf(ThresholdContext.From(context)))
        {
            return Task.FromResult(FailureDecision.Abort(
                $"Failure threshold exceeded at batch {context.PageNumber} " +
                $"(consecutive={context.ConsecutiveFailures}, total={context.TotalFailures}, ratio={context.FailureRatio:0.###})."));
        }

        context.Execution.Logger.LogWarning(
            context.Exception,
            "Skipping batch {PageNumber} after {Attempts} attempt(s) (consecutive={Consecutive}, total={Total}). Report will be marked partial.",
            context.PageNumber, context.AttemptsExhausted, context.ConsecutiveFailures, context.TotalFailures);

        return Task.FromResult(FailureDecision.Skip(
            $"Batch {context.PageNumber} skipped after {context.AttemptsExhausted} attempt(s)."));
    }
}
