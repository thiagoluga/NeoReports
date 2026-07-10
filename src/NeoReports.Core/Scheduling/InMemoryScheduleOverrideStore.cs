using System.Collections.Concurrent;
using NeoReports.Core.Configuration;

namespace NeoReports.Core.Scheduling;

/// <summary>
/// In-process <see cref="IScheduleOverrideStore"/> — same behavior as
/// <see cref="FileScheduleOverrideStore"/>, but overrides are lost on restart. Intended for tests
/// and single-process dev hosts.
/// </summary>
public sealed class InMemoryScheduleOverrideStore : IScheduleOverrideStore
{
    private readonly ConcurrentDictionary<string, ScheduleOverrideEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(string reportName, ScheduleOverrideEntry entry, CancellationToken cancellationToken)
    {
        ValidateName(reportName);
        ArgumentNullException.ThrowIfNull(entry);
        _entries[reportName] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ScheduleOverrideEntry?> GetAsync(string reportName, CancellationToken cancellationToken)
    {
        ValidateName(reportName);
        return Task.FromResult(_entries.TryGetValue(reportName, out ScheduleOverrideEntry? entry) ? entry : null);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string reportName, CancellationToken cancellationToken)
    {
        ValidateName(reportName);
        return Task.FromResult(_entries.TryRemove(reportName, out _));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(string ReportName, ScheduleOverrideEntry Entry)>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string, ScheduleOverrideEntry)>>(
            _entries.Select(kvp => (kvp.Key, kvp.Value)).ToArray());

    private static void ValidateName(string reportName)
    {
        if (!DynamicReportName.IsValid(reportName))
            throw new ArgumentException($"'{reportName}' is not a valid report name (must match {DynamicReportName.Pattern}).", nameof(reportName));
    }
}
