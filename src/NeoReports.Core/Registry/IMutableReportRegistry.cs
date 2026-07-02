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
}
