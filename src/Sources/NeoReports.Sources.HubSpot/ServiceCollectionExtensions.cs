using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// DI helpers for the HubSpot source (ADR D65). No <c>IFilterTranslator</c>/<c>ISourceRowCounter</c>
/// registered — HubSpot's plain object-collection read endpoint has no universal filter language or
/// total-count field (D36 honest gap; see D65).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven HubSpot source provider (<c>type: "hubspot"</c>) so reports
    /// defined in configuration can read from a HubSpot CRM object collection. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddHubSpotConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, HubSpotConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, HubSpotSourceHealthCheck>());
        return services;
    }
}
