using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Pipeline;

namespace NeoReports.Jobs;

/// <summary>
/// Executes a single report job: marks it running, runs the pipeline, and records the terminal
/// status (completed / failed / cancelled) plus stats in the <see cref="IJobStore"/>. Shared by
/// every scheduler (in-memory and Hangfire) so job lifecycle semantics are identical regardless
/// of the backend.
/// </summary>
/// <remarks>
/// Idempotent restart (CA-16) is provided by the pipeline: output is staged to a per-job temp
/// directory and only uploaded after a fully successful run, so a process that dies mid-job leaves
/// no partial file at the destination and a re-run starts from zero.
/// </remarks>
public sealed class ReportJobWorker
{
    private readonly IReportRunner _runner;
    private readonly IJobStore _store;
    private readonly ILogger<ReportJobWorker> _logger;

    /// <summary>Creates the worker.</summary>
    /// <param name="runner">Pipeline runner that executes the report.</param>
    /// <param name="store">Store that holds job status and stats.</param>
    /// <param name="logger">Logger.</param>
    public ReportJobWorker(IReportRunner runner, IJobStore store, ILogger<ReportJobWorker> logger)
    {
        _runner = runner;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Runs the job to completion, updating the store as it progresses. Cancellation via
    /// <paramref name="cancellationToken"/> moves the job to <see cref="ReportJobStatus.Cancelled"/>.
    /// </summary>
    /// <param name="jobId">Id of an already-created (queued) job.</param>
    /// <param name="reportName">Registered report name to run.</param>
    /// <param name="parameters">Run-time parameters for the report.</param>
    /// <param name="cancellationToken">Token that cooperatively cancels the run.</param>
    public async Task RunAsync(
        string jobId,
        string reportName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await _store.UpdateStatusAsync(jobId, ReportJobStatus.Running, null, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await _runner.RunAsync(reportName, parameters, jobId, cancellationToken).ConfigureAwait(false);

            await _store.UpdateStatsAsync(jobId, result.Stats, CancellationToken.None).ConfigureAwait(false);

            var status = result.Status == ReportRunStatus.Failed
                ? ReportJobStatus.Failed
                : ReportJobStatus.Completed;
            await _store.UpdateStatusAsync(jobId, status, result.Error, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Job {JobId} for report {Report} finished with status {Status} (read={Read}, written={Written}).",
                jobId, reportName, status, result.Stats.RecordsRead, result.Stats.RecordsWritten);
        }
        catch (OperationCanceledException)
        {
            // Use CancellationToken.None for the store update — the cancelling token is already
            // tripped and we still need to record the terminal state.
            await _store.UpdateStatusAsync(jobId, ReportJobStatus.Cancelled, "Cancelled.", CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogInformation("Job {JobId} for report {Report} was cancelled.", jobId, reportName);
        }
        catch (Exception ex)
        {
            await _store.UpdateStatusAsync(jobId, ReportJobStatus.Failed, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogError(ex, "Job {JobId} for report {Report} failed.", jobId, reportName);
            throw;
        }
    }
}
