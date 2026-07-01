using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Core.Sections;

namespace NeoReports.Xlsx.Pro;

/// <summary>DI helpers for the Pro XLSX workbook writer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the multi-sheet XLSX workbook writer (format <c>xlsx-workbook</c>) so config-driven
    /// reports can target it via an output with sections. Safe to call multiple times.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional workbook options (header, auto-filter).</param>
    public static IServiceCollection AddXlsxWorkbook(this IServiceCollection services, Action<XlsxWorkbookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new XlsxWorkbookOptions();
        configure?.Invoke(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISectionedWriterFactory>(new XlsxWorkbookWriterFactory(options)));
        return services;
    }
}
