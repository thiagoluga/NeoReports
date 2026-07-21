using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// DI helpers for the Google Sheets source (ADR D66). No <c>IFilterTranslator</c>/<c>ISourceRowCounter</c>/
/// <c>ISchemaExplorer</c> registered — <c>values.get</c> has no query language, no relational catalog,
/// and no accurate row-count mechanism (D36 honest gap; see D66).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Google Sheets source provider (<c>type: "googlesheets"</c>) so
    /// reports defined in configuration can read from a Google Sheet. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddGoogleSheetsConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, GoogleSheetsConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, GoogleSheetsSourceHealthCheck>());
        return services;
    }
}
