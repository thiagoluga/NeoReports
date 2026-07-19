using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.OData;

/// <summary>
/// DI helpers for the OData source (ADR D62). Unlike the HTTP family (P4a), a real
/// <see cref="IFilterTranslator"/> is registered — OData's standardized <c>$filter</c> query
/// language lets it genuinely implement server-side filter pushdown. No <c>ISourceRowCounter</c> is
/// registered here: that interface is never DI-resolved anywhere in this codebase (grep-verified
/// against every existing source package) — every implementer (<c>AdoKeysetSource{T}</c>,
/// <c>RefBatchSource</c>, <c>MappingBatchSource</c>/<c>MappingStreamingSource</c>) instead
/// implements it directly on the source class the pipeline already holds, and callers detect it by
/// pattern-matching that instance (<c>is ISourceRowCounter</c>). <see cref="ODataBatchSource{T}"/>
/// follows the same shape.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven OData source provider (<c>type: "odata"</c>) so reports defined
    /// in configuration can read from an OData v4 endpoint. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddODataConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, ODataConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, ODataSourceHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator, ODataFilterTranslator>());
        return services;
    }
}
