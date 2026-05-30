using NeoReports.Abstractions;
using NeoReports.Core.Resilience;

namespace NeoReports.Core.Building;

/// <summary>
/// Fluent configuration of what happens after a batch exhausts its retries. v1 supports
/// aborting the report or skipping the batch (with optional threshold-based escalation).
/// </summary>
public sealed class FailureStrategyBuilder
{
    private bool _skip;
    private Func<ThresholdContext, bool>? _abortIf;

    /// <summary>Aborts the whole report on the first definitively failed batch.</summary>
    public FailureStrategyBuilder AbortReport()
    {
        _skip = false;
        return this;
    }

    /// <summary>Skips definitively failed batches and logs a warning (report becomes partial).</summary>
    public FailureStrategyBuilder SkipBatchAndLog()
    {
        _skip = true;
        return this;
    }

    /// <summary>
    /// When skipping, escalates to an abort once the predicate is satisfied
    /// (e.g. <c>t =&gt; t.ConsecutiveFailures(3)</c>).
    /// </summary>
    /// <param name="predicate">Threshold predicate evaluated on each failure.</param>
    public FailureStrategyBuilder AbortIf(Func<ThresholdContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _abortIf = predicate;
        return this;
    }

    /// <summary>Builds the configured failure strategy. Defaults to aborting when unconfigured.</summary>
    public IFailureStrategy Build() =>
        _skip ? new SkipAndLogStrategy(_abortIf) : new AbortStrategy();
}
