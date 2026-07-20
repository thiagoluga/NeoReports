using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// DI helpers for the Elasticsearch/OpenSearch source (ADR D64). A real <see cref="IFilterTranslator"/>
/// is registered — Elasticsearch's Query DSL lets it genuinely implement server-side filter pushdown,
/// like D62's OData translator. No <c>ISourceRowCounter</c> is registered here: that interface is
/// never DI-resolved anywhere in this codebase — every implementer instead implements it directly on
/// the source class the pipeline already holds, and callers detect it by pattern-matching that
/// instance (<c>is ISourceRowCounter</c>). <see cref="ElasticsearchBatchSource{T}"/> follows the same shape.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Elasticsearch/OpenSearch source provider (<c>type: "elasticsearch"</c>)
    /// so reports defined in configuration can read from an index. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElasticsearchConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, ElasticsearchConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, ElasticsearchSourceHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator, ElasticsearchFilterTranslator>());
        return services;
    }
}
