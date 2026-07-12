using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Artifacts;
using NeoReports.Core.Building;
using NeoReports.Core.Events;
using NeoReports.Core.Registry;
using NeoReports.Core.Sections;

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

        var tempDir = Path.Combine(Path.GetTempPath(), "neoreports", execution.JobId);
        Directory.CreateDirectory(tempDir);

        var outputs = new List<RunningOutput>(report.Outputs.Count);
        var sectioned = new List<RunningSectioned>(report.SectionedOutputs.Count);
        var reader = report.ReaderFactory(execution, services);

        long recordsRead = 0, recordsWritten = 0;
        int retries = 0, batches = 0, skipped = 0;
        int consecutiveFailures = 0, totalFailures = 0;
        var status = ReportRunStatus.Completed;
        string? error = null;
        string? cursor = null;
        var pageNumber = 0;
        var stopwatch = Stopwatch.StartNew();

        var jobEventStore = services.GetService(typeof(IJobEventStore)) as IJobEventStore;
        var events = await JobEventEmitter.CreateAsync(jobEventStore, execution.JobId, execution.Logger, cancellationToken)
            .ConfigureAwait(false);
        await events.EmitAsync(events.IsRestart ? JobEventTypes.RunRestarted : JobEventTypes.RunStarted, null, null, cancellationToken)
            .ConfigureAwait(false);

        // The retry hook only ever emits an event (ADR D38, ground rule 3: telemetry must never
        // change a run's outcome) — ShouldHandle/backoff/jitter above are untouched.
        var resilience = ResiliencePipelineFactory.Build(report.Retry, async (attempt, delay, ex) =>
        {
            await events.EmitAsync(JobEventTypes.Retry, ex?.Message, new Dictionary<string, string>
            {
                ["page"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                ["delayMs"] = delay.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                ["exceptionType"] = ex?.GetType().Name ?? "Unknown",
            }, cancellationToken).ConfigureAwait(false);
        });

        // Best-effort capture of whatever was already written when a run fails or is cancelled
        // (ADR D40) — never at the report's real configured destinations (protects D2/D15's
        // all-or-nothing publish guarantee), only in a dedicated partial-artifact store, and only
        // when one is registered. A capture failure (writer refuses to finalize mid-failure, store
        // I/O error, ...) must never change the run's own outcome.
        async Task CaptureOnePartialAsync(
            Func<CancellationToken, Task> finalize, Func<ValueTask> disposeWriter, FileStream stream,
            Action markClosed, string path, string fileName, string mimeType, IPartialArtifactStore partialStore)
        {
            try
            {
                await finalize(CancellationToken.None).ConfigureAwait(false);
                await disposeWriter().ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                markClosed();

                var partialName = Path.GetFileNameWithoutExtension(fileName) + ".partial" + Path.GetExtension(fileName);
                await partialStore.SaveAsync(execution.JobId, path, partialName, mimeType, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // This one file's partial couldn't be captured (writer refused to finalize
                // mid-failure, store I/O error, ...) — move on to the next, never affect the run.
            }
        }

        async Task CapturePartialArtifactsAsync()
        {
            if (services.GetService(typeof(IPartialArtifactStore)) is not IPartialArtifactStore partialStore)
                return;

            foreach (RunningOutput output in outputs)
            {
                await CaptureOnePartialAsync(
                    output.Writer.FinalizeAsync, output.Writer.DisposeAsync, output.WriteStream,
                    () => output.Closed = true, output.Path, output.FileName, output.MimeType, partialStore)
                    .ConfigureAwait(false);
            }

            foreach (RunningSectioned output in sectioned)
            {
                await CaptureOnePartialAsync(
                    output.Writer.FinalizeAsync, output.Writer.DisposeAsync, output.WriteStream,
                    () => output.Closed = true, output.Path, output.FileName, output.MimeType, partialStore)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var outputIndex = 0; outputIndex < report.Outputs.Count; outputIndex++)
            {
                OutputSpec spec = report.Outputs[outputIndex];
                var writer = spec.Factory.Create(spec.Options, services);

                // Two outputs can share an extension (e.g. two CSVs). Disambiguate the file name so
                // they neither collide on disk nor overwrite each other's stored artifact.
                var fileName = $"{report.Name}.{writer.FileExtension}";
                var suffix = 2;
                while (!usedFileNames.Add(fileName))
                {
                    fileName = $"{report.Name}-{suffix}.{writer.FileExtension}";
                    suffix++;
                }

                var path = Path.Combine(tempDir, fileName);
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                await writer.InitializeAsync(
                    new WriterContext(execution, stream, report.OutputSchemas[outputIndex], spec.Options), cancellationToken)
                    .ConfigureAwait(false);
                outputs.Add(new RunningOutput(writer, stream, path, fileName, writer.MimeType));
            }

            for (var sectionedIndex = 0; sectionedIndex < report.SectionedOutputs.Count; sectionedIndex++)
            {
                CompiledSectionedOutput sectionedSpec = report.SectionedOutputs[sectionedIndex];
                IReportSectionedWriter writer = sectionedSpec.Spec.Factory.Create(sectionedSpec.Spec.Options, services);

                var fileName = $"{report.Name}.{writer.FileExtension}";
                var suffix = 2;
                while (!usedFileNames.Add(fileName))
                {
                    fileName = $"{report.Name}-{suffix}.{writer.FileExtension}";
                    suffix++;
                }

                var path = Path.Join(tempDir, fileName);
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                await writer.InitializeAsync(
                    new SectionedWriterContext(execution, stream, sectionedSpec.Sections, sectionedSpec.Spec.Options), cancellationToken)
                    .ConfigureAwait(false);
                sectioned.Add(new RunningSectioned(writer, stream, path, fileName, writer.MimeType, sectionedSpec.Sections.Count));
            }

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
                    for (var outputIndex = 0; outputIndex < outputs.Count; outputIndex++)
                        await outputs[outputIndex].Writer.WriteRowsAsync(batch.Outputs[outputIndex], cancellationToken).ConfigureAwait(false);

                    for (var s = 0; s < sectioned.Count; s++)
                    {
                        IReadOnlyList<IReadOnlyList<object?[]>> sectionRows = batch.SectionedOutputs[s];
                        for (var sec = 0; sec < sectioned[s].SectionCount; sec++)
                            await sectioned[s].Writer.WriteSectionRowsAsync(sec, sectionRows[sec], cancellationToken).ConfigureAwait(false);
                    }

                    recordsWritten += batch.WrittenCount;
                    consecutiveFailures = 0;

                    await events.EmitAsync(JobEventTypes.PageCompleted, null, new Dictionary<string, string>
                    {
                        ["page"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                        ["recordsRead"] = recordsRead.ToString(CultureInfo.InvariantCulture),
                        ["recordsWritten"] = recordsWritten.ToString(CultureInfo.InvariantCulture),
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    }, cancellationToken).ConfigureAwait(false);
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

                    await events.EmitAsync(JobEventTypes.BatchSkipped, null, new Dictionary<string, string>
                    {
                        ["page"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                        ["reason"] = decision.Reason ?? "",
                    }, cancellationToken).ConfigureAwait(false);
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

                    await events.EmitAsync(JobEventTypes.OutputsFinalized, null, new Dictionary<string, string>
                    {
                        ["fileName"] = output.FileName,
                        ["sizeBytes"] = output.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    }, cancellationToken).ConfigureAwait(false);
                }

                foreach (RunningSectioned output in sectioned)
                {
                    await output.Writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);
                    await output.Writer.DisposeAsync().ConfigureAwait(false);
                    await output.WriteStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await output.WriteStream.DisposeAsync().ConfigureAwait(false);
                    output.Closed = true;
                    output.SizeBytes = new FileInfo(output.Path).Length;
                    bytesWritten += output.SizeBytes;

                    await events.EmitAsync(JobEventTypes.OutputsFinalized, null, new Dictionary<string, string>
                    {
                        ["fileName"] = output.FileName,
                        ["sizeBytes"] = output.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    }, cancellationToken).ConfigureAwait(false);
                }

                var finishedFiles = new List<IFinishedFile>(outputs.Count + sectioned.Count);
                finishedFiles.AddRange(outputs);
                finishedFiles.AddRange(sectioned);

                foreach (var destSpec in report.Destinations)
                {
                    var destination = destSpec.Factory.Create(destSpec.Options, services);
                    foreach (IFinishedFile finished in finishedFiles)
                    {
                        var file = new ReportFile(
                            finished.FileName, finished.MimeType, finished.SizeBytes,
                            () => new FileStream(finished.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                        uploads.Add(await destination.UploadAsync(
                            file, new DestinationContext(execution, destSpec.Options), cancellationToken)
                            .ConfigureAwait(false));

                        await events.EmitAsync(JobEventTypes.UploadCompleted, null, new Dictionary<string, string>
                        {
                            ["destinationType"] = destSpec.Factory.Type,
                            ["fileName"] = finished.FileName,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Retain finished files for later retrieval (API download / sync streaming) when an
                // artifact store is registered. Copying happens before the temp dir is cleaned up.
                if (services.GetService(typeof(NeoReports.Core.Artifacts.IReportArtifactStore)) is NeoReports.Core.Artifacts.IReportArtifactStore artifactStore)
                {
                    foreach (IFinishedFile finished in finishedFiles)
                        await artifactStore.SaveAsync(
                            execution.JobId, finished.Path, finished.FileName, finished.MimeType, cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            else
            {
                // status == Failed (from either a read failure or a write failure escalated to
                // abort) — CompletedPartial runs legitimately publish above and never reach here;
                // only a genuine failure captures partials (D11's batch-atomicity: the partial file
                // contains exactly the fully-written batches).
                await CapturePartialArtifactsAsync().ConfigureAwait(false);
            }

            await events.EmitAsync(
                status == ReportRunStatus.Failed ? JobEventTypes.RunFailed : JobEventTypes.RunCompleted,
                status == ReportRunStatus.Failed ? error : null,
                null,
                cancellationToken).ConfigureAwait(false);

            var stats = new JobStats(recordsRead, recordsWritten, bytesWritten, retries, batches);
            return new ReportRunResult(status, stats, skipped, error, uploads);
        }
        catch (OperationCanceledException)
        {
            // The runner unwinds by exception on cancellation — capture here, before the finally
            // block deletes the temp directory, then rethrow to preserve the worker's Cancelled
            // status flow exactly as before this feature existed.
            await CapturePartialArtifactsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (RunningOutput output in outputs.Where(o => !o.Closed))
            {
                await SafeDisposeAsync(output.Writer).ConfigureAwait(false);
                await SafeDisposeAsync(output.WriteStream).ConfigureAwait(false);
            }

            foreach (RunningSectioned output in sectioned.Where(o => !o.Closed))
            {
                await SafeDisposeAsync(output.Writer).ConfigureAwait(false);
                await SafeDisposeAsync(output.WriteStream).ConfigureAwait(false);
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

    /// <summary>A finished output file (regular or sectioned) for upload and artifact retention.</summary>
    private interface IFinishedFile
    {
        string Path { get; }
        string FileName { get; }
        string MimeType { get; }
        long SizeBytes { get; }
    }

    private sealed class RunningOutput : IFinishedFile
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

    private sealed class RunningSectioned : IFinishedFile
    {
        public RunningSectioned(
            IReportSectionedWriter writer, FileStream writeStream, string path, string fileName, string mimeType, int sectionCount)
        {
            Writer = writer;
            WriteStream = writeStream;
            Path = path;
            FileName = fileName;
            MimeType = mimeType;
            SectionCount = sectionCount;
        }

        public IReportSectionedWriter Writer { get; }
        public FileStream WriteStream { get; }
        public string Path { get; }
        public string FileName { get; }
        public string MimeType { get; }
        public int SectionCount { get; }
        public bool Closed { get; set; }
        public long SizeBytes { get; set; }
    }
}
