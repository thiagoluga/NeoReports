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

    /// <summary>Creates the invoker.</summary>
    /// <param name="worker">The shared job worker.</param>
    public HangfireReportJobInvoker(ReportJobWorker worker) => _worker = worker;

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
}
