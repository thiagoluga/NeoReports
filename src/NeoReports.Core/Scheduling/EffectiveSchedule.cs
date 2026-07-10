using NeoReports.Abstractions;

namespace NeoReports.Core.Scheduling;

/// <summary>
/// Resolves a report's effective schedule (ADR D41): the runtime override wins when present (its
/// tombstone — a null <see cref="ScheduleOverrideEntry.Cron"/> — means "explicitly unscheduled",
/// even when the declaration itself has a schedule); otherwise the declared schedule applies.
/// </summary>
public static class EffectiveSchedule
{
    /// <summary>Resolves the effective cron expression, or <c>null</c> when the report has no active schedule.</summary>
    /// <param name="declared">The report's own declared schedule (config document or code-first builder), or <c>null</c>.</param>
    /// <param name="override_">The runtime override entry, or <c>null</c> when none exists.</param>
    public static string? Resolve(ScheduleConfig? declared, ScheduleOverrideEntry? override_) =>
        override_ is not null ? override_.Cron : declared?.Cron;

    /// <summary><c>true</c> when an override entry is in effect (including a tombstone), so a caller can flag "overridden at runtime".</summary>
    /// <param name="override_">The runtime override entry, or <c>null</c>.</param>
    public static bool IsOverridden(ScheduleOverrideEntry? override_) => override_ is not null;
}
