using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// DI helpers for the GraphQL source (ADR D63). Unlike OData (P5a), no <c>IFilterTranslator</c> is
/// registered — GraphQL has no universal filter/query protocol to translate a structured
/// <c>PreviewFilter</c> into; the author's query document and variables are the only filtering
/// mechanism (D63's honest gap). No <c>ISourceRowCounter</c> either — Relay's optional
/// <c>totalCount</c> has no universal fallback the way OData's <c>$count</c> does, so
/// <see cref="GraphQlBatchSource{T}"/> does not implement it.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven GraphQL source provider (<c>type: "graphql"</c>) so reports
    /// defined in configuration can read from a GraphQL endpoint's Relay connection. Safe to call
    /// multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddGraphQlConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, GraphQlConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, GraphQlSourceHealthCheck>());
        return services;
    }
}
