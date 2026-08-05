using NeoReports.Abstractions;
using NeoReports.Core.Pipeline;

namespace NeoReports.Jobs.UnitTests;

/// <summary>Reference row type.</summary>
public sealed record Sale(long Id, string Customer);

/// <summary>
/// Stateless batch source computed from the cursor (so it is safely re-runnable across enqueues).
/// Each page optionally waits <see cref="_perPageDelay"/> honoring the cancellation token, which
/// makes a run long enough to cancel deterministically.
/// </summary>
public sealed class ControllableSource : IBatchSource<Sale>
{
    private readonly long _totalRows;
    private readonly int _pageSize;
    private readonly TimeSpan _perPageDelay;

    public ControllableSource(long totalRows, int pageSize, TimeSpan perPageDelay)
    {
        _totalRows = totalRows;
        _pageSize = pageSize;
        _perPageDelay = perPageDelay;
    }

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public async Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        if (_perPageDelay > TimeSpan.Zero)
            await Task.Delay(_perPageDelay, cancellationToken).ConfigureAwait(false);

        var lastId = context.Cursor is null ? 0L : long.Parse(context.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        var start = lastId + 1;
        if (start > _totalRows)
            return BatchResult<Sale>.Empty;

        var end = Math.Min(start + _pageSize - 1, _totalRows);
        var count = (int)(end - start + 1);
        var rows = new Sale[count];
        for (var i = 0; i < count; i++)
        {
            var id = start + i;
            rows[i] = new Sale(id, $"C{id}");
        }

        var hasMore = end < _totalRows;
        var nextCursor = hasMore ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new BatchResult<Sale>(rows, nextCursor, hasMore);
    }
}

/// <summary>Source whose first read always throws — drives the failed-job path (default Abort).</summary>
public sealed class ThrowingSource : IBatchSource<Sale>
{
    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("source exploded");
}

/// <summary>
/// Source that fails the way <see cref="HttpClient"/> does when its own <c>Timeout</c> elapses: a
/// <see cref="TaskCanceledException"/> (an <see cref="OperationCanceledException"/>) carrying a token
/// that is <b>not</b> the run's. It must be reported as a failure, not as a cancellation.
/// </summary>
public sealed class TimingOutSource : IBatchSource<Sale>
{
    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
}

/// <summary>Source whose every read outlives any short deadline, so the run is cut off by it.</summary>
public sealed class SlowSource : IBatchSource<Sale>
{
    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public async Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return new BatchResult<Sale>(Array.Empty<Sale>(), null, false);
    }
}

/// <summary>Writer that discards content (we assert on job status / uploads, not bytes).</summary>
public sealed class NullWriter : IReportWriter
{
    public string Format => "null";
    public string MimeType => "application/x-null";
    public string FileExtension => "dat";

    public Task InitializeAsync(WriterContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        // Touch the token so a cancel during writing is observed promptly.
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task FinalizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Factory for <see cref="NullWriter"/>.</summary>
public sealed class NullWriterFactory : IWriterFactory
{
    public string Format => "null";

    public IReportWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new NullWriter();
}

/// <summary>
/// Writer that throws on the Nth batch, so a run driven with <c>SkipBatchAndLog</c> reaches
/// <c>CompletedPartial</c> — the state the job layer has to translate into a status.
/// </summary>
public sealed class FailOnBatchWriter : IReportWriter
{
    private readonly int _failOnBatch;
    private int _batch;

    public FailOnBatchWriter(int failOnBatch) => _failOnBatch = failOnBatch;

    public string Format => "null";
    public string MimeType => "application/x-null";
    public string FileExtension => "dat";

    public Task InitializeAsync(WriterContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        _batch++;
        if (_batch == _failOnBatch)
            throw new InvalidOperationException($"Simulated write failure on batch {_batch}.");

        return Task.CompletedTask;
    }

    public Task FinalizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Factory for <see cref="FailOnBatchWriter"/>.</summary>
public sealed class FailOnBatchWriterFactory : IWriterFactory
{
    private readonly int _failOnBatch;

    public FailOnBatchWriterFactory(int failOnBatch) => _failOnBatch = failOnBatch;

    public string Format => "null";

    public IReportWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new FailOnBatchWriter(_failOnBatch);
}

/// <summary>
/// Wraps <see cref="InMemoryJobStore"/> to count firings and, optionally, make one throw. The
/// recurring loop reaches the store through <c>EnqueueAsync</c> -> <c>CreateAsync</c>, so this is
/// where a failed firing is injected.
/// </summary>
public sealed class RecordingJobStore : IJobStore
{
    private readonly InMemoryJobStore _inner = new();
    private readonly Func<Task>? _onRun;
    private int _attempts;
    private int _created;

    public RecordingJobStore(Func<Task>? onRun = null) => _onRun = onRun;

    /// <summary>Firings that reached the store, including ones that threw.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Firings that produced a job record.</summary>
    public int Created => Volatile.Read(ref _created);

    public async Task<ReportJob> CreateAsync(ReportJobRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        if (_onRun is not null)
            await _onRun().ConfigureAwait(false);

        ReportJob job = await _inner.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _created);
        return job;
    }

    public Task<ReportJob?> GetAsync(string jobId, CancellationToken cancellationToken) =>
        _inner.GetAsync(jobId, cancellationToken);

    public Task UpdateStatusAsync(string jobId, ReportJobStatus status, string? error, CancellationToken cancellationToken) =>
        _inner.UpdateStatusAsync(jobId, status, error, cancellationToken);

    public Task UpdateStatsAsync(string jobId, JobStats stats, CancellationToken cancellationToken) =>
        _inner.UpdateStatsAsync(jobId, stats, cancellationToken);

    public Task<IReadOnlyList<ReportJob>> ListAsync(JobQuery query, CancellationToken cancellationToken) =>
        _inner.ListAsync(query, cancellationToken);

    /// <summary>
    /// Waits for a count to be reached. The fake clock makes the loop's *waiting* deterministic, but
    /// the firing it releases still runs on the thread pool, so the assertion has to wait for it —
    /// polling with a ceiling, never an unbounded wait.
    /// </summary>
    public Task WaitForCreationsAsync(int count) => WaitForAsync(() => Created >= count, $"{count} creation(s)");

    /// <inheritdoc cref="WaitForCreationsAsync"/>
    public Task WaitForAttemptsAsync(int count) => WaitForAsync(() => Attempts >= count, $"{count} attempt(s)");

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}.");
    }
}

/// <summary>Runner that reports a clean run without touching a pipeline.</summary>
public sealed class NoOpRunner : IReportRunner
{
    public Task<ReportRunResult> RunAsync(
        string reportName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? jobId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReportRunResult(
            ReportRunStatus.Completed, new JobStats(), 0, null, Array.Empty<UploadResult>()));
}

/// <summary>Destination that records the names of every file it received.</summary>
public sealed class CapturingDestination : IReportDestination
{
    public List<string> UploadedFiles { get; } = new();

    public string Type => "capture";

    public Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken)
    {
        UploadedFiles.Add(file.FileName);
        return Task.FromResult(UploadResult.Ok($"capture://{file.FileName}", file.FileName));
    }
}

/// <summary>Factory that exposes the single destination it created for assertions.</summary>
public sealed class CapturingDestinationFactory : IDestinationFactory
{
    public CapturingDestination? Last { get; private set; }

    public string Type => "capture";

    public IReportDestination Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services)
    {
        Last = new CapturingDestination();
        return Last;
    }
}
