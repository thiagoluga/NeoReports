using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Http;

/// <summary>
/// DI helpers for the HTTP source (ADR D61). No <c>ISchemaExplorer</c>/<c>IFilterTranslator</c> is
/// registered — a generic REST API has no catalog/query protocol to introspect or push filters
/// into (D36 honest capability gap); nor is an <c>ISourceRowCounter</c> registered — most REST APIs
/// don't return a total count, so progress goes indeterminate for this source (D47).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven HTTP source provider (<c>type: "http"</c>) so reports defined in
    /// configuration can read from a REST endpoint. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddHttpConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, HttpConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, HttpSourceHealthCheck>());
        return services;
    }
}
