namespace NeoReports.Core.Registry;

/// <summary>
/// Read-only view of the reports registered in code. Populated at startup by
/// <c>AddReport&lt;T&gt;(...)</c> and resolved from DI to look reports up by name.
/// </summary>
public interface IReportRegistry
{
    /// <summary>All registered reports.</summary>
    IReadOnlyCollection<CompiledReport> Reports { get; }

    /// <summary>Names of all registered reports.</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>Returns the report registered under <paramref name="name"/>, or <c>null</c>.</summary>
    /// <param name="name">The report name.</param>
    CompiledReport? Find(string name);

    /// <summary>True when a report with the given name is registered.</summary>
    /// <param name="name">The report name.</param>
    bool Contains(string name);
}
