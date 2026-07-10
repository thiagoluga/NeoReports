using NeoReports.Abstractions;
using NeoReports.Jobs;

namespace NeoReports.Jobs.Hangfire;

/// <summary>
/// The unit of work Hangfire invokes for a report job. Hangfire resolves it from DI, persists its
/// arguments in storage (so the job survives restarts), and injects a <see cref="CancellationToken"/>
/// that is tripped on server shutdown or when the background job is aborted/deleted — which is how
/// cooperative cancellation reaches the pipeline.
/// </summary>
public sealed class HangfireReportJobInvoker
{
    private readonly ReportJobWorker _worker;
    private readonly IJobStore _store;

    /// <summary>Creates the invoker.</summary>
    /// <param name="worker">The shared job worker.</param>
    /// <param name="store">The job store — used by <see cref="ExecuteRecurringAsync"/> to create the job record per firing.</param>
    public HangfireReportJobInvoker(ReportJobWorker worker, IJobStore store)
    {
        _worker = worker;
        _store = store;
    }

    /// <summary>
    /// Executes the job. Called by Hangfire; parameters arrive as a JSON string because Hangfire
    /// serializes method arguments into its storage.
    /// </summary>
    /// <param name="jobId">The NeoReports job id (created in the store before enqueueing).</param>
    /// <param name="reportName">The registered report to run.</param>
    /// <param name="parametersJson">Parameters serialized by <see cref="JobParameters.Serialize"/>.</param>
    /// <param name="cancellationToken">Injected by Hangfire; cancels on shutdown/abort.</param>
    public Task ExecuteAsync(string jobId, string reportName, string parametersJson, CancellationToken cancellationToken)
    {
        var parameters = JobParameters.Deserialize(parametersJson);
        return _worker.RunAsync(jobId, reportName, parameters, cancellationToken);
    }

    /// <summary>
    /// Executes one recurring firing (ADR D41). Unlike <see cref="ExecuteAsync"/>, a recurring job
    /// re-invokes this same method on every occurrence, so there is no pre-created job id to pass in
    /// — this method creates the job record itself, so each firing appears in <c>GET /jobs</c> like
    /// any other job.
    /// </summary>
    /// <param name="reportName">The scheduled report to run.</param>
    /// <param name="cancellationToken">Injected by Hangfire; cancels on shutdown/abort.</param>
    public async Task ExecuteRecurringAsync(string reportName, CancellationToken cancellationToken)
    {
        var request = new ReportJobRequest(reportName);
        ReportJob job = await _store.CreateAsync(request, CancellationToken.None).ConfigureAwait(false);
        await _worker.RunAsync(job.Id, reportName, request.Parameters, cancellationToken).ConfigureAwait(false);
    }
}
