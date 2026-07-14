namespace NeoReports.UI.Services;

/// <summary>Formats <see cref="ApiJobEvent"/>s (ADR D38) into UI-ready shapes for the Timeline,
/// Retries, and processing-rate sparkline cards.</summary>
public static class JobEventFormatter
{
    /// <summary>One row for the Timeline component. Kind drives the marker color: "info" (gray),
    /// "ok" (green), "warn" (amber), "err" (red).</summary>
    public sealed record TimelineRow(string Time, string Kind, string Icon, string Text, string? Detail);

    /// <summary>One point of the processing-rate sparkline: cumulative rows written at an elapsed offset.</summary>
    public sealed record RatePoint(double ElapsedMs, long RecordsWritten);

    public static TimelineRow ToTimelineRow(ApiJobEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var time = e.At.ToLocalTime().ToString("HH:mm:ss");
        var data = e.Data;

        return e.Type switch
        {
            "run-started" => new(time, "info", "player-play", "Run started", null),
            "run-restarted" => new(time, "info", "player-play", "Run restarted", "Recovered from a crash mid-run"),
            "page-completed" => new(time, "ok", "check", $"Page {Get(data, "page")} written",
                $"{Get(data, "recordsWritten")} rows written so far · {Get(data, "elapsedMs")}ms elapsed"),
            "retry" => new(time, "warn", "refresh", $"Retry on page {Get(data, "page")} (attempt {Get(data, "attempt")})",
                $"{Get(data, "exceptionType")}{(string.IsNullOrEmpty(e.Message) ? "" : $" · {e.Message}")}"),
            "batch-skipped" => new(time, "warn", "player-skip-forward", $"Batch {Get(data, "page")} skipped", Get(data, "reason")),
            "outputs-finalized" => new(time, "ok", "file-check", $"Output finalized: {Get(data, "fileName")}",
                $"{Get(data, "sizeBytes")} bytes"),
            "upload-completed" => new(time, "ok", "cloud-upload", $"Uploaded to {Get(data, "destinationType")}", Get(data, "fileName")),
            "run-completed" => new(time, "ok", "check", "Run completed", null),
            "run-failed" => new(time, "err", "alert-triangle", "Run failed", e.Message),
            "run-cancelled" => new(time, "warn", "player-stop", "Run cancelled", null),
            "events-truncated" => new(time, "info", "dots", "Further events were not recorded", "The per-job event log cap was reached"),
            _ => new(time, "info", "minus", e.Type, e.Message),
        };
    }

    /// <summary>Extracts the ordered rate-history series from a job's <c>page-completed</c> events.</summary>
    public static IReadOnlyList<RatePoint> ToRateSeries(IEnumerable<ApiJobEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(e => e.Type == "page-completed")
            .Select(TryParseRatePoint)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();
    }

    private static RatePoint? TryParseRatePoint(ApiJobEvent e)
    {
        if (e.Data is null)
            return null;
        if (!double.TryParse(Get(e.Data, "elapsedMs"), out var elapsedMs))
            return null;
        if (!long.TryParse(Get(e.Data, "recordsWritten"), out var written))
            return null;

        return new RatePoint(elapsedMs, written);
    }

    /// <summary>Rows/second between each consecutive pair of <paramref name="series"/> points — the
    /// real, discrete rate history (no second sampling mechanism, ADR D38).</summary>
    public static IReadOnlyList<double> ToIntervalRates(IReadOnlyList<RatePoint> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var rates = new List<double>(Math.Max(0, series.Count - 1));
        for (var i = 1; i < series.Count; i++)
        {
            var deltaSeconds = (series[i].ElapsedMs - series[i - 1].ElapsedMs) / 1000.0;
            var deltaRecords = series[i].RecordsWritten - series[i - 1].RecordsWritten;
            rates.Add(deltaSeconds > 0 ? deltaRecords / deltaSeconds : 0);
        }

        return rates;
    }

    /// <summary>
    /// The most recent <c>recordsRead</c> value carried by a <c>page-completed</c> event (ADR D47)
    /// — the live read counter while a job is running, since <c>JobStats</c> itself only persists
    /// once at job completion. Null when no page has completed yet.
    /// </summary>
    public static long? LatestRecordsRead(IEnumerable<ApiJobEvent> events) => LatestNumericDatum(events, "recordsRead");

    /// <summary>
    /// The most recent <c>totalRecords</c> value carried by an event (ADR D47) — repeated on every
    /// <c>page-completed</c> event, so the latest one found is authoritative. Null when tracking
    /// was disabled, unsupported by the source, or the pre-run count failed.
    /// </summary>
    public static long? LatestTotalRecords(IEnumerable<ApiJobEvent> events) => LatestNumericDatum(events, "totalRecords");

    private static long? LatestNumericDatum(IEnumerable<ApiJobEvent> events, string key)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Select(e => TryParseDatum(e, key))
            .Where(value => value is not null)
            .LastOrDefault();
    }

    private static long? TryParseDatum(ApiJobEvent e, string key) =>
        e.Data is not null && e.Data.TryGetValue(key, out var raw) && long.TryParse(raw, out var value) ? value : null;

    private static string Get(IReadOnlyDictionary<string, string>? data, string key) =>
        data is not null && data.TryGetValue(key, out var value) ? value : "?";
}
