using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Jobs;

namespace NeoReports.Jobs.Hangfire.DependencyInjection;

/// <summary>DI entry points for the Hangfire job backend.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Hangfire-backed scheduler plus the shared worker, invoker, and a no-op
    /// checkpoint store. The caller is responsible for configuring Hangfire itself
    /// (<c>AddHangfire(...)</c> with a storage provider and <c>AddHangfireServer()</c> for a single
    /// server) and for registering the reports and core services (<c>AddReport</c> / <c>AddNeoReports</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddNeoReportsHangfireJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        services.TryAddSingleton<ICheckpointStore, NoOpCheckpointStore>();
        services.TryAddSingleton<ReportJobWorker>();
        services.TryAddSingleton<HangfireReportJobInvoker>();
        services.TryAddSingleton<IReportJobScheduler, HangfireJobScheduler>();

        return services;
    }
}
