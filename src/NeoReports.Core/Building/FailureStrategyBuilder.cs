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
    private AbortThresholdConfig? _abortThresholds;

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
    /// (e.g. <c>t =&gt; t.ConsecutiveFailures(3)</c>). Not introspectable — <see cref="AbortThresholds"/>
    /// stays <c>null</c> when this overload is used (ADR D37); prefer <see cref="AbortIf(AbortThresholdConfig)"/>
    /// when the thresholds need to round-trip through the dynamic path or the API.
    /// </summary>
    /// <param name="predicate">Threshold predicate evaluated on each failure.</param>
    public FailureStrategyBuilder AbortIf(Func<ThresholdContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _abortIf = predicate;
        _abortThresholds = null;
        return this;
    }

    /// <summary>
    /// When skipping, escalates to an abort once any configured threshold is reached (OR
    /// semantics). Unlike the predicate overload, this is introspectable via
    /// <see cref="AbortThresholds"/> — the shape the dynamic path (<c>ResilienceConfig.AbortWhen</c>)
    /// and <c>GET /reports/{name}</c> expose (ADR D37).
    /// </summary>
    /// <param name="thresholds">The thresholds to escalate on; at least one field must be set.</param>
    /// <exception cref="ConfigurationException">Thrown when no field is set or a value is out of range.</exception>
    public FailureStrategyBuilder AbortIf(AbortThresholdConfig thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        if (thresholds is { ConsecutiveFailures: null, TotalFailures: null, FailureRate: null })
            throw new ConfigurationException("AbortThresholdConfig must set at least one of ConsecutiveFailures, TotalFailures, or FailureRate.");
        if (thresholds.ConsecutiveFailures is < 1)
            throw new ConfigurationException("AbortThresholdConfig.ConsecutiveFailures must be >= 1.");
        if (thresholds.TotalFailures is < 1)
            throw new ConfigurationException("AbortThresholdConfig.TotalFailures must be >= 1.");
        if (thresholds.FailureRate is <= 0 or > 1)
            throw new ConfigurationException("AbortThresholdConfig.FailureRate must be in the range (0, 1].");

        _abortThresholds = thresholds;
        _abortIf = t => ExceedsAnyThreshold(thresholds, t);
        return this;
    }

    /// <summary>The thresholds configured via the data-based <see cref="AbortIf(AbortThresholdConfig)"/>
    /// overload, or <c>null</c> when none was set or a raw predicate was used instead.</summary>
    public AbortThresholdConfig? AbortThresholds => _abortThresholds;

    /// <summary>Builds the configured failure strategy. Defaults to aborting when unconfigured.</summary>
    public IFailureStrategy Build() =>
        _skip ? new SkipAndLogStrategy(_abortIf) : new AbortStrategy();

    private static bool ExceedsAnyThreshold(AbortThresholdConfig thresholds, ThresholdContext context)
    {
        if (thresholds.ConsecutiveFailures is int consecutive && context.ConsecutiveFailures(consecutive))
            return true;
        if (thresholds.TotalFailures is int total && context.TotalFailures(total))
            return true;
        if (thresholds.FailureRate is double rate && context.FailureRatio(rate))
            return true;

        return false;
    }
}
