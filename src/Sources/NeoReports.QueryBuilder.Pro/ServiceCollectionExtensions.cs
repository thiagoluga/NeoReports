using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Core.QueryBuilder;

namespace NeoReports.QueryBuilder.Pro;

/// <summary>DI helpers for the Pro visual query builder.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Pro <see cref="IQuerySqlGenerator"/> so the query-builder endpoint
    /// (<c>POST /sources/{name}/query-sql</c>, ADR D49) can turn a visually-composed query into
    /// keyset-safe report SQL. Without this call a host has no generator and the endpoint reports the
    /// capability as unavailable. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddQueryBuilder(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IQuerySqlGenerator, QuerySqlGenerator>();
        return services;
    }
}
