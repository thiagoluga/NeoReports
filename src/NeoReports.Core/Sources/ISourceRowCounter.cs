using NeoReports.Abstractions;

namespace NeoReports.Core.Sources;

/// <summary>
/// Optional capability a source (<see cref="IBatchSource{T}"/> / <see cref="IStreamingSource{T}"/>)
/// may additionally implement to report how many rows a full run would read, detected by
/// pattern-matching on the source instance (the same mechanism as
/// <see cref="SourceRegistry.INamedSourceResolver"/>). Used when a report opts into progress
/// tracking (ADR D47) to compute a real completion percentage. Best-effort by contract: a
/// <c>null</c> result — including from a wrapper whose inner source doesn't implement this
/// interface — or a thrown exception both degrade the run's progress to indeterminate. A wrapper
/// that always implements this interface (so a report built over it still gets a real total when
/// its inner source supports counting) must return <c>null</c>, not throw, when the inner source
/// can't count — throwing would make every run log a "row count failed" warning for a source that
/// was never expected to count in the first place, not an actual failure.
/// </summary>
public interface ISourceRowCounter
{
    /// <summary>
    /// Counts the rows a full run would read, honoring the same run-time parameters a read would,
    /// or <c>null</c> when this instance can't currently produce a count (e.g. a wrapper whose
    /// inner source doesn't implement <see cref="ISourceRowCounter"/>).
    /// </summary>
    /// <param name="execution">The execution context (run-time parameters, logger).</param>
    /// <param name="cancellationToken">Token that cancels the count (and the run).</param>
    Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken);
}
