using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;

namespace NeoReports.Core.DependencyInjection;

/// <summary>DI entry points for registering NeoReports and individual reports.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NeoReports core services (report registry and runner). Safe to call multiple
    /// times; <see cref="AddReport{TRow}"/> calls it implicitly.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddNeoReports(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        GetOrAddRegistry(services);
        services.TryAddSingleton<IReportRunner, ReportRunner>();
        return services;
    }

    /// <summary>
    /// Registers a strongly typed report in code. The builder runs immediately and the compiled
    /// report is added to the registry.
    /// </summary>
    /// <typeparam name="TRow">The report's row type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Unique report name.</param>
    /// <param name="configure">Configures the report via the fluent builder.</param>
    public static IServiceCollection AddReport<TRow>(
        this IServiceCollection services,
        string name,
        Action<ReportBuilder<TRow>> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddNeoReports();

        var builder = new ReportBuilder<TRow>(name);
        configure(builder);
        GetOrAddRegistry(services).Register(builder.Build());

        return services;
    }

    private static ReportRegistry GetOrAddRegistry(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IReportRegistry) &&
                descriptor.ImplementationInstance is ReportRegistry existing)
            {
                return existing;
            }
        }

        var registry = new ReportRegistry();
        services.AddSingleton<IReportRegistry>(registry);
        return registry;
    }
}
