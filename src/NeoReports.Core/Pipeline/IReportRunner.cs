namespace NeoReports.Core.Pipeline;

/// <summary>Executes a registered report synchronously (in-process), returning its outcome.</summary>
public interface IReportRunner
{
    /// <summary>Runs a registered report by name.</summary>
    /// <param name="reportName">The report to run.</param>
    /// <param name="parameters">Run-time parameters; <c>null</c> is treated as empty.</param>
    /// <param name="jobId">Optional job id; a new one is generated when omitted.</param>
    /// <param name="cancellationToken">Token that cooperatively cancels the run.</param>
    /// <returns>The run result.</returns>
    Task<ReportRunResult> RunAsync(
        string reportName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? jobId = null,
        CancellationToken cancellationToken = default);
}
