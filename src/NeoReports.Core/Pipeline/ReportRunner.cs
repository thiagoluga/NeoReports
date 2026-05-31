using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Registry;

namespace NeoReports.Core.Pipeline;

/// <summary>
/// Default in-process report runner. Reads the source in batches, applies retry (Polly) and the
/// configured failure strategy, writes each batch to every output, then uploads the finished
/// files. Output is staged to per-run temp files so memory stays roughly constant and publishing
/// is all-at-the-end.
/// </summary>
public sealed class ReportRunner : IReportRunner
{
    private readonly IReportRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates a report runner.</summary>
    /// <param name="registry">Registry used to resolve reports by name.</param>
    /// <param name="services">Service provider passed to writer/destination factories.</param>
    /// <param name="loggerFactory">Factory used to create per-execution loggers.</param>
    public ReportRunner(IReportRegistry registry, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _services = services;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public Task<ReportRunResult> RunAsync(
        string reportName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? jobId = null,
        CancellationToken cancellationToken = default)
    {
        var report = _registry.Find(reportName)
            ?? throw new ConfigurationException($"No report named '{reportName}' is registered.");

        jobId ??= Guid.NewGuid().ToString("N");
        var logger = _loggerFactory.CreateLogger($"NeoReports.Report.{report.Name}");
        var execution = new ReportExecutionContext(jobId, report.Name, parameters, logger, cancellationToken);

        return ExecuteAsync(report, execution, _services, cancellationToken);
    }

    /// <summary>
    /// Executes a compiled report against a prepared execution context. Exposed for direct use
    /// (e.g. tests and the job worker) without resolving through the registry.
    /// </summary>
    /// <param name="report">The compiled report.</param>
    /// <param name="execution">The execution context (carries job id, parameters, logger, token).</param>
    /// <param name="services">Service provider passed to writer/destination factories.</param>
    /// <param name="cancellationToken">Token that cooperatively cancels the run.</param>
    public static async Task<ReportRunResult> ExecuteAsync(
        CompiledReport report,
        ReportExecutionContext execution,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(execution);

        var resilience = ResiliencePipelineFactory.Build(report.Retry);
        var tempDir = Path.Combine(Path.GetTempPath(), "neoreports", execution.JobId);
        Directory.CreateDirectory(tempDir);

        var outputs = new List<RunningOutput>(report.Outputs.Count);
        var reader = report.ReaderFactory(execution);

        long recordsRead = 0, recordsWritten = 0;
        int retries = 0, batches = 0, skipped = 0;
        int consecutiveFailures = 0, totalFailures = 0;
        var status = ReportRunStatus.Completed;
        string? error = null;

        try
        {
            foreach (var spec in report.Outputs)
            {
                var writer = spec.Factory.Create(spec.Options, services);
                var fileName = $"{report.Name}.{writer.FileExtension}";
                var path = Path.Combine(tempDir, fileName);
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                await writer.InitializeAsync(
                    new WriterContext(execution, stream, report.Schema, spec.Options), cancellationToken)
                    .ConfigureAwait(false);
                outputs.Add(new RunningOutput(writer, stream, path, fileName, writer.MimeType));
            }

            string? cursor = null;
            var pageNumber = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageNumber++;

                var attempts = 0;
                ProjectedBatch batch;
                try
                {
                    batch = await resilience.ExecuteAsync(
                        async token =>
                        {
                            attempts++;
                            return await reader.ReadAsync(pageNumber, cursor, token).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    retries += Math.Max(0, attempts - 1);
                    batches++;
                    totalFailures++;
                    consecutiveFailures++;
                    var decision = await report.FailureStrategy.HandleAsync(
                        new BatchFailureContext(execution, pageNumber, cursor, ex, attempts,
                            consecutiveFailures, totalFailures, totalFailures / (double)batches),
                        cancellationToken).ConfigureAwait(false);

                    // A read failure cannot advance keyset pagination, so skipping is impossible:
                    // either the strategy aborts, or we abort to avoid silently truncating data.
                    status = ReportRunStatus.Failed;
                    error = decision.Action == FailureAction.AbortReport
                        ? decision.Reason
                        : $"Batch {pageNumber} could not be read and cannot be skipped (no cursor to advance): {ex.Message}";
                    break;
                }

                retries += Math.Max(0, attempts - 1);
                batches++;
                recordsRead += batch.RawCount;
                cursor = batch.NextCursor;

                try
                {
                    foreach (var output in outputs)
                        await output.Writer.WriteRowsAsync(batch.Rows, cancellationToken).ConfigureAwait(false);

                    recordsWritten += batch.Rows.Count;
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    totalFailures++;
                    consecutiveFailures++;
                    var decision = await report.FailureStrategy.HandleAsync(
                        new BatchFailureContext(execution, pageNumber, cursor, ex, 1,
                            consecutiveFailures, totalFailures, totalFailures / (double)batches),
                        cancellationToken).ConfigureAwait(false);

                    if (decision.Action == FailureAction.AbortReport)
                    {
                        status = ReportRunStatus.Failed;
                        error = decision.Reason;
                        break;
                    }

                    skipped++;
                    status = ReportRunStatus.CompletedPartial;
                }

                if (!batch.HasMore)
                    break;
            }

            long bytesWritten = 0;
            var uploads = new List<UploadResult>();

            if (status != ReportRunStatus.Failed)
            {
                foreach (var output in outputs)
                {
                    await output.Writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);
                    await output.Writer.DisposeAsync().ConfigureAwait(false);
                    await output.WriteStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await output.WriteStream.DisposeAsync().ConfigureAwait(false);
                    output.Closed = true;
                    output.SizeBytes = new FileInfo(output.Path).Length;
                    bytesWritten += output.SizeBytes;
                }

                foreach (var destSpec in report.Destinations)
                {
                    var destination = destSpec.Factory.Create(destSpec.Options, services);
                    foreach (var output in outputs)
                    {
                        var file = new ReportFile(
                            output.FileName, output.MimeType, output.SizeBytes,
                            () => new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                        uploads.Add(await destination.UploadAsync(
                            file, new DestinationContext(execution, destSpec.Options), cancellationToken)
                            .ConfigureAwait(false));
                    }
                }

                // Retain finished files for later retrieval (API download / sync streaming) when an
                // artifact store is registered. Copying happens before the temp dir is cleaned up.
                if (services.GetService(typeof(NeoReports.Core.Artifacts.IReportArtifactStore)) is NeoReports.Core.Artifacts.IReportArtifactStore artifactStore)
                {
                    foreach (var output in outputs)
                        await artifactStore.SaveAsync(
                            execution.JobId, output.Path, output.FileName, output.MimeType, cancellationToken)
                            .ConfigureAwait(false);
                }
            }

            var stats = new JobStats(recordsRead, recordsWritten, bytesWritten, retries, batches);
            return new ReportRunResult(status, stats, skipped, error, uploads);
        }
        finally
        {
            foreach (var output in outputs)
            {
                if (!output.Closed)
                {
                    await SafeDisposeAsync(output.Writer).ConfigureAwait(false);
                    await SafeDisposeAsync(output.WriteStream).ConfigureAwait(false);
                }
            }

            await reader.DisposeAsync().ConfigureAwait(false);

            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup: a leftover temp file must never change the job's outcome.
                // Catch broadly (IOException, UnauthorizedAccessException when a file is briefly
                // locked on Windows, etc.) so cleanup cannot replace an in-flight cancellation
                // exception and turn a cancelled run into a failed one.
            }
        }
    }

    private static async ValueTask SafeDisposeAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal failures during cleanup are non-fatal.
        }
    }

    private sealed class RunningOutput
    {
        public RunningOutput(IReportWriter writer, FileStream writeStream, string path, string fileName, string mimeType)
        {
            Writer = writer;
            WriteStream = writeStream;
            Path = path;
            FileName = fileName;
            MimeType = mimeType;
        }

        public IReportWriter Writer { get; }
        public FileStream WriteStream { get; }
        public string Path { get; }
        public string FileName { get; }
        public string MimeType { get; }
        public bool Closed { get; set; }
        public long SizeBytes { get; set; }
    }
}
