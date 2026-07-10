namespace NeoReports.Core.Scheduling;

/// <summary>
/// A runtime override of a report's declared schedule (ADR D41). <see cref="Cron"/> <c>null</c> is
/// the explicit "unscheduled" tombstone — distinct from no override existing at all: it means the
/// report has a declared schedule (config document or code-first builder) that has been explicitly
/// turned off at runtime.
/// </summary>
/// <param name="Cron">The override cron expression, or <c>null</c> for the unscheduled tombstone.</param>
public sealed record ScheduleOverrideEntry(string? Cron);

/// <summary>
/// Persists runtime schedule overrides, uniformly for reports of either origin (code-first and
/// config-first — ADR D41). The declared schedule in a report's own definition is never patched;
/// an override here always wins when present. Effective schedule = override if present (tombstone
/// ⇒ none) else the declared schedule.
/// </summary>
public interface IScheduleOverrideStore
{
    /// <summary>Writes (creates or replaces) the override entry for a report.</summary>
    /// <param name="reportName">The report name.</param>
    /// <param name="entry">The override entry (a null <see cref="ScheduleOverrideEntry.Cron"/> is the tombstone).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(string reportName, ScheduleOverrideEntry entry, CancellationToken cancellationToken);

    /// <summary>Reads the override entry for a report, or <c>null</c> when none exists.</summary>
    /// <param name="reportName">The report name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScheduleOverrideEntry?> GetAsync(string reportName, CancellationToken cancellationToken);

    /// <summary>Removes the override entry entirely (not a tombstone — the declared schedule, if any, applies again).</summary>
    /// <param name="reportName">The report name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when an entry existed and was removed.</returns>
    Task<bool> RemoveAsync(string reportName, CancellationToken cancellationToken);

    /// <summary>Lists every override entry currently stored.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<(string ReportName, ScheduleOverrideEntry Entry)>> ListAsync(CancellationToken cancellationToken);
}
