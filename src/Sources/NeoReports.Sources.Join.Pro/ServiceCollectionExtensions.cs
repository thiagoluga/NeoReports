using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Join.Pro;

/// <summary>DI helpers for the Pro multi-source providers.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven merge-join source (<c>type: "merge-join"</c>) so dynamic-path
    /// reports can compose two nested sources by key. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMergeJoinConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigSourceProvider, JoinConfigSourceProvider>());
        return services;
    }
}
