using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Core.QueryBuilder;
using NeoReports.Licensing;

namespace NeoReports.QueryBuilder.Pro;

/// <summary>DI helpers for the Pro visual query builder.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Pro <see cref="IQuerySqlGenerator"/> so the query-builder endpoint
    /// (<c>POST /sources/{name}/query-sql</c>, ADR D49) can turn a visually-composed query into
    /// keyset-safe report SQL. Without this call a host has no generator and the endpoint reports the
    /// capability as unavailable. Safe to call multiple times. Requires a valid NeoReports Pro
    /// license (ADR D70): pass an explicit key by calling <c>services.AddNeoReportsProLicense(key)</c>
    /// <em>before</em> this, or set the <c>NEOREPORTS_LICENSE_KEY</c> environment variable.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <exception cref="NeoReportsLicenseException">No valid NeoReports Pro license is configured.</exception>
    public static IServiceCollection AddQueryBuilder(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNeoReportsProLicense();
        services.TryAddSingleton<IQuerySqlGenerator, QuerySqlGenerator>();
        return services;
    }
}
