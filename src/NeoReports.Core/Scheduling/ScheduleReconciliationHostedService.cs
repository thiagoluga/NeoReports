using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Registry;

namespace NeoReports.Core.Scheduling;

/// <summary>
/// Reconciles recurring registrations with the registry's effective schedules at startup (ADR
/// D41). Dynamic reports hydrate lazily (D33), so this service forces hydration by resolving
/// <see cref="IReportRegistry"/>, then for every report registers/removes its effective schedule
/// (declared × override, tombstone-aware) and removes orphaned registrations — recurring entries
/// for report names no longer registered (e.g. a report deleted while the host was down). A no-op
/// when no <see cref="IRecurringReportScheduler"/> is registered (scheduling is entirely opt-in).
/// </summary>
public sealed class ScheduleReconciliationHostedService : IHostedService
{
    private readonly IReportRegistry _registry;
    private readonly IScheduleOverrideStore _overrides;
    private readonly IRecurringReportScheduler? _scheduler;
    private readonly ILogger<ScheduleReconciliationHostedService> _logger;

    /// <summary>Creates the hosted service.</summary>
    /// <param name="registry">The report registry (resolved here to force lazy hydration).</param>
    /// <param name="overrides">The schedule override store.</param>
    /// <param name="scheduler">The recurring scheduler, or <c>null</c> when no Jobs package registered one.</param>
    /// <param name="logger">Logger.</param>
    public ScheduleReconciliationHostedService(
        IReportRegistry registry,
        IScheduleOverrideStore overrides,
        ILogger<ScheduleReconciliationHostedService> logger,
        IRecurringReportScheduler? scheduler = null)
    {
        _registry = registry;
        _overrides = overrides;
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_scheduler is null)
        {
            _logger.LogDebug("No IRecurringReportScheduler is registered; skipping schedule reconciliation.");
            return;
        }

        var reportNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (CompiledReport report in _registry.Reports)
        {
            reportNames.Add(report.Name);

            ScheduleOverrideEntry? overrideEntry = null;
            try
            {
                overrideEntry = await _overrides.GetAsync(report.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // A code-first report name that doesn't satisfy the file-safe naming rule can never
                // have a stored override — nothing to reconcile for it beyond the declared schedule.
            }

            string? effectiveCron = EffectiveSchedule.Resolve(report.Schedule, overrideEntry);
            try
            {
                if (effectiveCron is not null)
                    await _scheduler.RegisterRecurringAsync(report.Name, effectiveCron, cancellationToken).ConfigureAwait(false);
                else
                    await _scheduler.RemoveRecurringAsync(report.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfigurationException ex)
            {
                _logger.LogWarning(ex, "Could not reconcile the schedule for report {ReportName}.", report.Name);
            }
        }

        IReadOnlyList<string> registered = await _scheduler.ListRegisteredNamesAsync(cancellationToken).ConfigureAwait(false);
        foreach (string orphan in registered.Where(name => !reportNames.Contains(name)))
        {
            _logger.LogInformation("Removing orphaned recurring schedule for '{ReportName}' (report no longer registered).", orphan);
            await _scheduler.RemoveRecurringAsync(orphan, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
