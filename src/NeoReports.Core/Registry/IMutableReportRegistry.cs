namespace NeoReports.Core.Registry;

/// <summary>
/// A report registry that accepts changes at runtime (the dynamic-registration path, ADR D33).
/// Every <see cref="IReportRegistry"/> resolved from DI is also reachable through this interface
/// when the underlying implementation supports it — <see cref="ReportRegistry"/> always does.
/// </summary>
public interface IMutableReportRegistry : IReportRegistry
{
    /// <summary>Registers a compiled report. Throws <see cref="Abstractions.ConfigurationException"/> if the name is taken.</summary>
    /// <param name="report">The compiled report to register.</param>
    void Register(CompiledReport report);

    /// <summary>
    /// Removes the report registered under <paramref name="name"/>. A job already running for
    /// that report keeps running to completion — the worker holds its own reference to the
    /// <see cref="CompiledReport"/>; unregistering only prevents new lookups by name.
    /// </summary>
    /// <param name="name">The report name.</param>
    /// <returns><c>true</c> when a report was removed; <c>false</c> when none was registered under that name.</returns>
    bool Unregister(string name);

    /// <summary>
    /// Puts <paramref name="report"/> in place of whatever is registered under its name, without the
    /// window in which the name resolves to nothing (ADR D86 — an edit must not make a report
    /// briefly 404 or briefly free for another request to claim). Registers it outright when the
    /// name is unused.
    /// </summary>
    /// <param name="report">The compiled report to put in place.</param>
    /// <remarks>
    /// Default-implemented as unregister-then-register so an existing external implementation keeps
    /// compiling; that fallback is *not* atomic. <see cref="ReportRegistry"/> overrides it with a
    /// single dictionary assignment.
    /// </remarks>
    void Replace(CompiledReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Unregister(report.Name);
        Register(report);
    }
}
