using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// DI helpers for the Salesforce source (ADR D67). No <c>IFilterTranslator</c>/<c>ISchemaExplorer</c>
/// registered — translating <c>PreviewFilter</c> into SOQL <c>WHERE</c> syntax and Salesforce's
/// Describe metadata are both new, non-trivial work declined for this pass (D36 honest gap; see D67).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Salesforce source provider (<c>type: "salesforce"</c>) so reports
    /// defined in configuration can read from a Salesforce SOQL query. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddSalesforceConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, SalesforceConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, SalesforceSourceHealthCheck>());
        return services;
    }
}
