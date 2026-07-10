using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Core.Artifacts;

namespace NeoReports.AspNetCore.DependencyInjection;

/// <summary>DI helpers for the ASP.NET Core integration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the artifact store that backs the download and sync-streaming endpoints. The
    /// pipeline saves finished output files here when a report runs, and the endpoints read from it.
    /// Uses <see cref="FileSystemArtifactStore"/> (default temp root) unless overridden.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddNeoReportsArtifacts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IReportArtifactStore, FileSystemArtifactStore>();
        return services;
    }

    /// <summary>
    /// Registers the artifact store rooted at <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="rootPath">Directory under which per-job artifact folders are created.</param>
    public static IServiceCollection AddNeoReportsArtifacts(this IServiceCollection services, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IReportArtifactStore>(_ => new FileSystemArtifactStore(rootPath));
        return services;
    }

    /// <summary>
    /// Registers the partial-artifact store (ADR D40): when a job fails or is cancelled mid-run,
    /// the runner captures its best-effort output here — a location entirely separate from
    /// <see cref="IReportArtifactStore"/> and the report's real configured destinations, protecting
    /// the all-or-nothing publish guarantee (D2/D15). Opt-in: a host that never calls this captures
    /// nothing and behaves exactly as it did before this feature existed.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback (storage directory, retention).</param>
    public static IServiceCollection AddPartialArtifacts(
        this IServiceCollection services, Action<PartialArtifactOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PartialArtifactOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IPartialArtifactStore>(sp => new FileSystemPartialArtifactStore(sp.GetRequiredService<PartialArtifactOptions>()));

        return services;
    }
}
