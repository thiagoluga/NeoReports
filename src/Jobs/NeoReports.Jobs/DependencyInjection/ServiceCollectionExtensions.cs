using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;

namespace NeoReports.Jobs.DependencyInjection;

/// <summary>DI entry points for NeoReports job execution.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory job backend: <see cref="InMemoryJobStore"/>,
    /// <see cref="NoOpCheckpointStore"/>, the shared <see cref="ReportJobWorker"/>, and the
    /// in-process <see cref="InMemoryJobScheduler"/>. Assumes <c>AddNeoReports</c> and the reports
    /// have already been registered so an <see cref="Core.Pipeline.IReportRunner"/> is available.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddNeoReportsInMemoryJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        services.TryAddSingleton<ICheckpointStore, NoOpCheckpointStore>();
        services.TryAddSingleton<ReportJobWorker>();
        services.TryAddSingleton<IReportJobScheduler, InMemoryJobScheduler>();

        return services;
    }
}
