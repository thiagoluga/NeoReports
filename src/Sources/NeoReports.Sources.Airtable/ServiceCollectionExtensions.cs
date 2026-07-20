using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// DI helpers for the Airtable source (ADR D65). No <c>IFilterTranslator</c>/<c>ISourceRowCounter</c>
/// registered — Airtable's plain table-read endpoint has no universal filter language or total-count
/// field through the endpoint this source uses (D36 honest gap; see D65).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Airtable source provider (<c>type: "airtable"</c>) so reports
    /// defined in configuration can read from an Airtable table. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddAirtableConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, AirtableConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, AirtableSourceHealthCheck>());
        return services;
    }
}
