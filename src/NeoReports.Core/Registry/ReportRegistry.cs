using System.Collections.Concurrent;
using NeoReports.Abstractions;

namespace NeoReports.Core.Registry;

/// <summary>Default thread-safe <see cref="IReportRegistry"/>. Reports are added at startup, and
/// — through <see cref="IMutableReportRegistry"/> — at runtime for the dynamic-registration path.</summary>
public sealed class ReportRegistry : IMutableReportRegistry
{
    private readonly ConcurrentDictionary<string, CompiledReport> _reports =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyCollection<CompiledReport> Reports => _reports.Values.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => _reports.Keys.ToArray();

    /// <summary>Registers a compiled report. Throws if the name is already taken.</summary>
    /// <param name="report">The compiled report to register.</param>
    /// <exception cref="ConfigurationException">Thrown when a report with the same name exists.</exception>
    public void Register(CompiledReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_reports.TryAdd(report.Name, report))
            throw new ConfigurationException($"A report named '{report.Name}' is already registered.");
    }

    /// <inheritdoc />
    public CompiledReport? Find(string name) =>
        _reports.TryGetValue(name, out var report) ? report : null;

    /// <inheritdoc />
    public bool Contains(string name) => _reports.ContainsKey(name);

    /// <inheritdoc />
    public bool Unregister(string name) => _reports.TryRemove(name, out _);
}
