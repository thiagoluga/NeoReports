using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Events;
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
    private readonly IJobEventStore? _jobEvents;

    /// <summary>Creates the worker.</summary>
    /// <param name="runner">Pipeline runner that executes the report.</param>
    /// <param name="store">Store that holds job status and stats.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="jobEvents">Optional job event log (ADR D38) — used only to record the terminal
    /// "run-cancelled" event; the runner itself records everything else. <c>null</c> when the host
    /// never called <c>AddJobEvents</c>/<c>AddInMemoryJobEvents</c>.</param>
    public ReportJobWorker(IReportRunner runner, IJobStore store, ILogger<ReportJobWorker> logger, IJobEventStore? jobEvents = null)
    {
        _runner = runner;
        _store = store;
        _logger = logger;
        _jobEvents = jobEvents;
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

            // A run that skipped batches produced a file missing rows the source held, and used to
            // land on Completed. The count is no help: SkippedBatches is on ReportRunResult and is
            // not one of JobStats's counters, so it never reaches the job record — a partial run and
            // a whole one were identical to every caller of the job API (ADR D75).
            ReportJobStatus status = result.Status switch
            {
                ReportRunStatus.Failed => ReportJobStatus.Failed,
                ReportRunStatus.CompletedPartial => ReportJobStatus.Partial,
                _ => ReportJobStatus.Completed,
            };
            await _store.UpdateStatusAsync(jobId, status, result.Error, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Job {JobId} for report {Report} finished with status {Status} (read={Read}, written={Written}).",
                jobId, reportName, status, result.Stats.RecordsRead, result.Stats.RecordsWritten);
        }
        // A cancellation is either one caused by OUR token (a caller-requested cancel) or a run that
        // exceeded its configured deadline — the runner reports the latter as
        // ReportDeadlineExceededException precisely because the caller's token is not cancelled for
        // it. An unfiltered catch also swallowed an OperationCanceledException raised by anything
        // else — most commonly HttpClient.Timeout, which throws TaskCanceledException carrying its
        // own internal token, and which the runner deliberately lets through (its batch handlers
        // filter on `ex is not OperationCanceledException`). Those are genuine failures: labelling
        // them "Cancelled." discarded the real error and, because this path does not rethrow, made
        // Hangfire record the run as succeeded.
        catch (OperationCanceledException ex)
            when (cancellationToken.IsCancellationRequested || ex is ReportDeadlineExceededException)
        {
            // Record the event before flipping the store status, so any observer that polls the
            // store and sees Cancelled is guaranteed to already find the event on lookup — not a
            // race where the status update wins and the event append hasn't landed yet.
            await TryEmitCancelledEventAsync(jobId).ConfigureAwait(false);
            // A deadline expiry is still a cancellation, but say so: "Cancelled." alone gives an
            // operator no way to tell a caller-requested cancel from a run that ran out of time. The
            // message is ours (curated, secret-free), so it is safe to persist verbatim.
            var reason = ex is ReportDeadlineExceededException deadline ? deadline.Message : "Cancelled.";
            // Use CancellationToken.None for the store update — the cancelling token is already
            // tripped and we still need to record the terminal state.
            await _store.UpdateStatusAsync(jobId, ReportJobStatus.Cancelled, reason, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogInformation(ex, "Job {JobId} for report {Report} was cancelled ({Reason}).", jobId, reportName, reason);
        }
        catch (Exception ex)
        {
            // Persisted as job.Error (surfaced by GET /jobs). This path handles exceptions that
            // escape the runner rather than becoming a Failed result (e.g. a source/writer that
            // throws during setup). Keep a NeoReports message (curated, secret-free); reduce any
            // other — a driver exception can echo the connection string — to its type name. The full
            // exception is logged just below for diagnosis.
            var reason = ex is NeoReportsException ? ex.Message : ex.GetType().Name;
            await _store.UpdateStatusAsync(jobId, ReportJobStatus.Failed, reason, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogError(ex, "Job {JobId} for report {Report} failed.", jobId, reportName);
            throw;
        }
    }

    // The runner itself unwinds by exception on cancellation (no normal return to emit
    // "run-cancelled" from), so the worker records this one terminal event instead. Best-effort,
    // like every other job-event append (ADR D38, ground rule 3).
    private async Task TryEmitCancelledEventAsync(string jobId)
    {
        if (_jobEvents is null)
            return;

        try
        {
            var existing = (await _jobEvents.ListAsync(jobId, null, int.MaxValue, 0, CancellationToken.None).ConfigureAwait(false)).Count;
            await _jobEvents.AppendAsync(
                new JobEvent(jobId, existing + 1, DateTimeOffset.UtcNow, JobEventTypes.RunCancelled, null, null),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not append the run-cancelled event for job {JobId}.", jobId);
        }
    }
}
