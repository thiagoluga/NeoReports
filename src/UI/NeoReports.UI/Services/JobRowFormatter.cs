using NeoReports.UI.Models;

namespace NeoReports.UI.Services;

/// <summary>
/// Maps an <see cref="ApiJobView"/>'s wire status to the UI's <see cref="JobStatus"/>/route/label —
/// pulled out so <c>Dashboard.razor</c>'s "Recent jobs" card, the full <c>Jobs.razor</c> list, and
/// <c>ReportDetail.razor</c>'s run-history table can't drift into disagreeing about where a job's
/// detail page lives or what its badge says for the same status.
/// </summary>
public static class JobRowFormatter
{
    /// <summary>One row: display fields plus the resolved <see cref="Route"/> to navigate on click.</summary>
    public sealed record Row(
        string Id, string ReportName, JobStatus Status, string? StatusLabel,
        string ProgressLabel, string Started, string Duration, string Route);

    public static Row ToRow(ApiJobView j)
    {
        ArgumentNullException.ThrowIfNull(j);

        return new Row(
            Id: j.Id,
            ReportName: j.ReportName,
            Status: MapStatus(j.Status),
            StatusLabel: StatusLabelFor(j.Status),
            ProgressLabel: ProgressLabelFor(j),
            Started: j.StartedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—",
            Duration: FormatDuration(j.StartedAt, j.CompletedAt),
            Route: MapRoute(j.Id, j.Status));
    }

    /// <summary>
    /// Where a job's detail page lives, by wire status. Cancelled reuses the Failed page — there is
    /// no separate cancelled-job screen — matching <c>JobRunning.razor</c>'s own terminal-state
    /// redirect (<c>job.Status is "Failed" or "Cancelled"</c>).
    /// </summary>
    public static string MapRoute(string id, string wireStatus) => wireStatus switch
    {
        "Completed" => $"jobs/completed/{id}",
        "Failed" or "Cancelled" => $"jobs/failed/{id}",
        _ => $"jobs/{id}",
    };

    /// <summary>
    /// Collapses the engine's wire status into the UI's display enum. Cancelled maps to Failed —
    /// both are terminal, non-success outcomes, and <see cref="JobStatus"/> has no dedicated member
    /// for it — but keeps its own wording via <see cref="StatusLabelFor"/>.
    /// </summary>
    public static JobStatus MapStatus(string wireStatus) => wireStatus switch
    {
        "Completed" => JobStatus.Ok,
        "Failed" or "Cancelled" => JobStatus.Failed,
        "Running" or "Retrying" => JobStatus.Running,
        "Paused" => JobStatus.Paused,
        _ => JobStatus.Queued,
    };

    /// <summary>
    /// Overrides <c>JobStatusBadge</c>'s default label for statuses that collapse into a broader
    /// <see cref="JobStatus"/> bucket but still need their own words: Completed shows "Completed"
    /// (not <c>StatusMaps</c>' generic Ok label), Cancelled shows "Cancelled" (not "Failed", even
    /// though it shares Failed's styling — a deliberate stop isn't the same story as an error).
    /// Null for every other status, meaning "use the badge's own default".
    /// </summary>
    public static string? StatusLabelFor(string wireStatus) => wireStatus switch
    {
        "Completed" => "Completed",
        "Cancelled" => "Cancelled",
        _ => null,
    };

    /// <summary>
    /// A real completion percentage (ADR D47) for terminal jobs only — <c>JobStats</c> persists
    /// once, at job completion, so a still-running job's stats are zero regardless of its actual
    /// progress; showing a number for it here would be misleading. Matches
    /// <c>JobRunning.razor</c>'s own indeterminate fallback: no known positive total is "—", not 0%.
    /// </summary>
    private static string ProgressLabelFor(ApiJobView j)
    {
        if (j.Status is not ("Completed" or "Failed"))
            return "—";
        if (j.Stats.TotalRecords is not { } total || total <= 0)
            return "—";

        var pct = (int)Math.Clamp(j.Stats.RecordsRead * 100 / total, 0, 100);
        return $"{pct}%";
    }

    private static string FormatDuration(DateTimeOffset? started, DateTimeOffset? completed)
    {
        if (started is not { } s || completed is not { } c)
            return "—";

        TimeSpan span = c - s;
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }
}
