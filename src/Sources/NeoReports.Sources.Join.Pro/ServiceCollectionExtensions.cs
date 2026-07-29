using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Licensing;

namespace NeoReports.Sources.Join.Pro;

/// <summary>DI helpers for the Pro multi-source providers.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven merge-join source (<c>type: "merge-join"</c>) so dynamic-path
    /// reports can compose two nested sources by key. Safe to call multiple times. Requires a valid
    /// NeoReports Pro license (ADR D70): pass an explicit key by calling
    /// <c>services.AddNeoReportsProLicense(key)</c> <em>before</em> this, or set the
    /// <c>NEOREPORTS_LICENSE_KEY</c> environment variable.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <exception cref="NeoReportsLicenseException">No valid NeoReports Pro license is configured.</exception>
    public static IServiceCollection AddMergeJoinConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNeoReportsProLicense();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigSourceProvider, JoinConfigSourceProvider>());
        return services;
    }
}
