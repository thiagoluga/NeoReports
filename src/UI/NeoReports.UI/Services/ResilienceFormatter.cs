namespace NeoReports.UI.Services;

/// <summary>Formats an <see cref="ApiReportDetail"/>'s retry/failure policy into a summary line.</summary>
public static class ResilienceFormatter
{
    /// <summary>Renders "N× attempts · backoff Ns [· jitter] · on failure: Strategy".</summary>
    public static string Format(ApiReportDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        string jitter = detail.RetryUseJitter ? " · jitter" : "";
        return $"{detail.RetryMaxAttempts}× attempts · {detail.RetryBackoff.ToLowerInvariant()} {detail.RetryBaseDelaySeconds:0.#}s" +
               jitter + $" · on failure: {detail.FailureStrategy}";
    }

    /// <summary>
    /// Renders the abort-escalation thresholds (ADR D37), e.g. "abort after 3 consecutive failures
    /// or 10 total or 50% failure rate". <c>null</c> when no threshold is configured — a report with
    /// a code-first custom escalation predicate is indistinguishable from "none" over the wire (the
    /// predicate isn't data), so both render as "not configured" rather than guessing.
    /// </summary>
    public static string? FormatAbortThresholds(ApiReportDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var parts = new List<string>();
        if (detail.AbortAfterConsecutiveFailures is int consecutive)
            parts.Add($"{consecutive} consecutive failure(s)");
        if (detail.AbortAfterTotalFailures is int total)
            parts.Add($"{total} total failure(s)");
        if (detail.AbortAtFailureRate is double rate)
            parts.Add($"{rate:0.#%} failure rate");

        return parts.Count == 0 ? null : "abort after " + string.Join(" or ", parts);
    }
}
