using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Configuration;
using NeoReports.Core.Events;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;

namespace NeoReports.Core.DependencyInjection;

/// <summary>Options for <see cref="ServiceCollectionExtensions.AddDynamicReports"/>.</summary>
public sealed class DynamicReportsOptions
{
    /// <summary>Directory where dynamic report configs are persisted. Default: <c>./neoreports-configs</c>.</summary>
    public string Directory { get; set; } = "./neoreports-configs";
}

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

    /// <summary>
    /// Registers a config-driven (dynamic) report from a JSON document. The config is parsed now and
    /// compiled lazily when the registry is first resolved, so the source/format/destination
    /// providers it references (e.g. <c>AddSqlConfigSource()</c>) only need to be registered by then.
    /// The report is then runnable by name like any code-first report.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configJson">The report configuration as a JSON string.</param>
    public static IServiceCollection AddReportFromConfig(this IServiceCollection services, string configJson)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configJson);

        services.AddNeoReports();
        ReportConfig config = new JsonReportConfigParser().Parse(configJson);
        services.AddSingleton(config);
        return services;
    }

    /// <summary>Registers a config-driven report read from a JSON file.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="path">Path to the JSON configuration file.</param>
    public static IServiceCollection AddReportFromConfigFile(this IServiceCollection services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(path);
        return services.AddReportFromConfig(File.ReadAllText(path));
    }

    /// <summary>Registers every JSON report configuration found in a directory (non-recursive).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="directory">Directory to scan.</param>
    /// <param name="searchPattern">File search pattern; defaults to <c>*.json</c>.</param>
    /// <exception cref="DirectoryNotFoundException">Thrown when the directory does not exist.</exception>
    public static IServiceCollection AddReportsFromConfigDirectory(
        this IServiceCollection services, string directory, string searchPattern = "*.json")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Report config directory not found: {directory}");

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern).OrderBy(f => f, StringComparer.Ordinal))
            services.AddReportFromConfig(File.ReadAllText(file));

        return services;
    }

    /// <summary>
    /// Registers dynamic (runtime-created) report support: a file-backed <see cref="IReportConfigStore"/>
    /// and startup rehydration of every document it holds, through the same lazy mechanism
    /// code-first config reports (<see cref="AddReportFromConfig"/>) already use. Combine with the
    /// AspNetCore package's dynamic report endpoints to let a UI create reports at runtime.
    /// Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback (e.g. to change the storage directory).</param>
    public static IServiceCollection AddDynamicReports(
        this IServiceCollection services, Action<DynamicReportsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddNeoReports();

        var options = new DynamicReportsOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<IReportConfigStore>(
            serviceProvider => new FileReportConfigStore(serviceProvider.GetRequiredService<DynamicReportsOptions>().Directory));

        if (!services.Any(d => d.ServiceType == typeof(IRegistryHydrator) && d.ImplementationType == typeof(FileStoreRegistryHydrator)))
            services.AddSingleton<IRegistryHydrator, FileStoreRegistryHydrator>();

        return services;
    }

    /// <summary>
    /// Registers the file-backed job event log (ADR D38): the runner records discrete, structured
    /// per-job lifecycle events (retries, page progress, uploads, terminal status) that back the
    /// job Timeline/Retries/rate-history UI cards. Opt-in and best-effort — omitting this call means
    /// the runner emits nothing and behaves exactly as it did before this feature existed.
    /// Events survive a process restart; see <see cref="AddInMemoryJobEvents"/> for tests/dev.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback (cap, retention, storage directory).</param>
    public static IServiceCollection AddJobEvents(this IServiceCollection services, Action<JobEventOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new JobEventOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IJobEventStore, FileJobEventStore>();

        return services;
    }

    /// <summary>
    /// Registers an in-process job event log (ADR D38) — same behavior as <see cref="AddJobEvents"/>,
    /// but events are lost on restart. Intended for tests and single-process dev hosts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback (cap, retention).</param>
    public static IServiceCollection AddInMemoryJobEvents(this IServiceCollection services, Action<JobEventOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new JobEventOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IJobEventStore, InMemoryJobEventStore>();

        return services;
    }

    private static ReportRegistry GetOrAddRegistry(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ReportRegistry) &&
                descriptor.ImplementationInstance is ReportRegistry existing)
            {
                return existing;
            }
        }

        var registry = new ReportRegistry();

        // Concrete instance: the dedup anchor and the eager target for typed AddReport calls.
        services.AddSingleton(registry);

        // Same singleton instance, exposed through the mutable interface for the dynamic path
        // (ADR D33) — runtime register/unregister without a second registry to keep in sync.
        services.AddSingleton<IMutableReportRegistry>(registry);

        // The public registry compiles any config-registered reports on first resolution, when the
        // service provider — and thus the source/format/destination providers — is available.
        // Hydrators (e.g. FileStoreRegistryHydrator, added by AddDynamicReports) run afterwards
        // regardless of whether AddDynamicReports was called before or after this factory was
        // registered — GetServices<IRegistryHydrator> only runs when IReportRegistry is resolved,
        // by which point the whole service collection has already been built.
        services.AddSingleton<IReportRegistry>(serviceProvider =>
        {
            foreach (ReportConfig config in serviceProvider.GetServices<ReportConfig>())
                registry.Register(ReportConfigCompiler.Compile(config, serviceProvider));

            foreach (IRegistryHydrator hydrator in serviceProvider.GetServices<IRegistryHydrator>())
                hydrator.Hydrate(registry, serviceProvider);

            return registry;
        });

        return registry;
    }
}
