using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.Oracle;

/// <summary>DI helpers for the Oracle source.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the config-driven Oracle source provider (<c>type: "oracle"</c>) so reports
    /// defined in configuration can read from Oracle. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddOracleConfigSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigSourceProvider, OracleConfigSourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISourceHealthCheck, OracleSourceHealthCheck>());
        // Oracle does implicitly convert a text-bound parameter to NUMBER in a comparison, but that
        // conversion follows the session's NLS settings — a value like "2000.00" can fail with
        // ORA-01722 against a session whose numeric locale doesn't treat '.' as the decimal
        // separator, so numeric filters need an explicit, locale-independent cast.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFilterTranslator>(new AdoFilterTranslator(
            "oracle", parameterPrefix: ":", castParameter: AdoFilterTranslator.OracleCast)));
        return services;
    }
}
