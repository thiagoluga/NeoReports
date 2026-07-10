namespace NeoReports.Core.Scheduling;

/// <summary>
/// Manages recurring (cron-based) report executions (ADR D41). A **Core capability interface** —
/// deliberately not a member of the frozen <c>Abstractions.IReportJobScheduler</c> — implemented by
/// each Jobs package (<c>InMemoryJobScheduler</c>, <c>HangfireJobScheduler</c>). Optional: a host
/// that never registers an implementation has no recurring support; the endpoints reject schedule
/// input with 409 rather than silently dropping it.
/// </summary>
public interface IRecurringReportScheduler
{
    /// <summary>
    /// Registers (or replaces) the recurring schedule for a report. Each firing creates a fresh job
    /// record, so recurring runs appear in the job list like any other job. Overlapping firings run
    /// concurrently — there is no skip-if-running logic (ADR D41).
    /// </summary>
    /// <param name="reportName">The registered report to run on the schedule.</param>
    /// <param name="cron">A validated 5-field cron expression, evaluated in UTC.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterRecurringAsync(string reportName, string cron, CancellationToken cancellationToken);

    /// <summary>Removes a report's recurring schedule, if any. A no-op when none is registered.</summary>
    /// <param name="reportName">The report to unschedule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken);

    /// <summary>The next occurrence in UTC, or <c>null</c> when the report has no active schedule.</summary>
    /// <param name="reportName">The report to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DateTimeOffset?> GetNextOccurrenceAsync(string reportName, CancellationToken cancellationToken);

    /// <summary>
    /// Report names with an active recurring registration. Used by startup reconciliation to find
    /// and remove orphaned registrations (a report deleted while the host was down).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListRegisteredNamesAsync(CancellationToken cancellationToken);
}
