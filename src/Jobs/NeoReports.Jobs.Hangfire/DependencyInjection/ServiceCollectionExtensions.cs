using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Scheduling;
using NeoReports.Jobs;

namespace NeoReports.Jobs.Hangfire.DependencyInjection;

/// <summary>DI entry points for the Hangfire job backend.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Hangfire-backed scheduler plus the shared worker, invoker, and a no-op
    /// checkpoint store — exposed as both <see cref="IReportJobScheduler"/> and
    /// <see cref="IRecurringReportScheduler"/> (ADR D41), the same singleton instance under both
    /// interfaces. The caller is responsible for configuring Hangfire itself (<c>AddHangfire(...)</c>
    /// with a storage provider and <c>AddHangfireServer()</c> for a single server, which also
    /// registers the <see cref="global::Hangfire.IRecurringJobManager"/> the recurring capability
    /// needs) and for registering the reports and core services (<c>AddReport</c> / <c>AddNeoReports</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddNeoReportsHangfireJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        services.TryAddSingleton<ICheckpointStore, NoOpCheckpointStore>();
        services.TryAddSingleton<ReportJobWorker>();
        services.TryAddSingleton<HangfireReportJobInvoker>();
        services.TryAddSingleton<HangfireJobScheduler>();
        services.TryAddSingleton<IReportJobScheduler>(sp => sp.GetRequiredService<HangfireJobScheduler>());
        services.TryAddSingleton<IRecurringReportScheduler>(sp => sp.GetRequiredService<HangfireJobScheduler>());

        return services;
    }
}
