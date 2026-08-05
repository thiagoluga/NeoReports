using NeoReports.Abstractions;

namespace NeoReports.Core.Resilience;

/// <summary>
/// Snapshot of failure counters used to evaluate escalation thresholds. Exposes small predicate
/// helpers so callers can write <c>t =&gt; t.ConsecutiveFailures(3)</c> in the fluent API.
/// </summary>
public readonly struct ThresholdContext
{
    private readonly int _consecutiveFailures;
    private readonly int _totalFailures;
    private readonly double _failureRatio;
    private readonly int _batchesSeen;

    /// <summary>Creates a threshold snapshot.</summary>
    /// <param name="consecutiveFailures">Consecutive batch failures so far.</param>
    /// <param name="totalFailures">Total batch failures so far.</param>
    /// <param name="failureRatio">Fraction of batches that have failed so far (0..1).</param>
    /// <param name="batchesSeen">How many batches have been processed, including the failing one.</param>
    public ThresholdContext(int consecutiveFailures, int totalFailures, double failureRatio, int batchesSeen = 0)
    {
        _consecutiveFailures = consecutiveFailures;
        _totalFailures = totalFailures;
        _failureRatio = failureRatio;
        _batchesSeen = batchesSeen;
    }

    /// <summary>Creates a snapshot from a batch failure context.</summary>
    /// <param name="context">The batch failure context.</param>
    public static ThresholdContext From(BatchFailureContext context) =>
        // PageNumber is the batch count: the runner increments it once per loop iteration, and the
        // failing batch is counted before the ratio is computed, so the two move together.
        new(context.ConsecutiveFailures, context.TotalFailures, context.FailureRatio, context.PageNumber);

    /// <summary>True when consecutive failures reached <paramref name="count"/>.</summary>
    /// <param name="count">Threshold count.</param>
    public bool ConsecutiveFailures(int count) => _consecutiveFailures >= count;

    /// <summary>True when total failures reached <paramref name="count"/>.</summary>
    /// <param name="count">Threshold count.</param>
    public bool TotalFailures(int count) => _totalFailures >= count;

    /// <summary>True when the failure ratio reached <paramref name="ratio"/>.</summary>
    /// <param name="ratio">Threshold ratio (0..1).</param>
    public bool FailureRatio(double ratio) => _failureRatio >= ratio;

    /// <summary>True when at least <paramref name="count"/> batches have been processed.</summary>
    /// <param name="count">Minimum batch count.</param>
    public bool BatchesSeen(int count) => _batchesSeen >= count;
}
