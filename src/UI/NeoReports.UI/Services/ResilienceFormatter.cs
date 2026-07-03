namespace NeoReports.UI.Services;

/// <summary>Formats an <see cref="ApiReportDetail"/>'s retry/failure policy into a single summary line.</summary>
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
}
