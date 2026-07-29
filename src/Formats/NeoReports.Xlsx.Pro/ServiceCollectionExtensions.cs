using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Core.Sections;
using NeoReports.Licensing;

namespace NeoReports.Xlsx.Pro;

/// <summary>DI helpers for the Pro XLSX workbook writer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the multi-sheet XLSX workbook writer (format <c>xlsx-workbook</c>) so config-driven
    /// reports can target it via an output with sections. Safe to call multiple times. Requires a
    /// valid NeoReports Pro license (ADR D70): pass an explicit key by calling
    /// <c>services.AddNeoReportsProLicense(key)</c> <em>before</em> this, or set the
    /// <c>NEOREPORTS_LICENSE_KEY</c> environment variable.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional workbook options (header, auto-filter).</param>
    /// <exception cref="NeoReportsLicenseException">No valid NeoReports Pro license is configured.</exception>
    public static IServiceCollection AddXlsxWorkbook(this IServiceCollection services, Action<XlsxWorkbookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNeoReportsProLicense();
        var options = new XlsxWorkbookOptions();
        configure?.Invoke(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISectionedWriterFactory>(new XlsxWorkbookWriterFactory(options)));
        return services;
    }
}
