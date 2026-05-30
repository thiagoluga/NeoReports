using NeoReports.Abstractions;

namespace NeoReports.Jobs.UnitTests;

/// <summary>Reference row type.</summary>
public sealed record Venda(long Id, string Cliente);

/// <summary>
/// Stateless batch source computed from the cursor (so it is safely re-runnable across enqueues).
/// Each page optionally waits <see cref="_perPageDelay"/> honoring the cancellation token, which
/// makes a run long enough to cancel deterministically.
/// </summary>
public sealed class ControllableSource : IBatchSource<Venda>
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

    public async Task<BatchResult<Venda>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        if (_perPageDelay > TimeSpan.Zero)
            await Task.Delay(_perPageDelay, cancellationToken).ConfigureAwait(false);

        var lastId = context.Cursor is null ? 0L : long.Parse(context.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        var start = lastId + 1;
        if (start > _totalRows)
            return BatchResult<Venda>.Empty;

        var end = Math.Min(start + _pageSize - 1, _totalRows);
        var count = (int)(end - start + 1);
        var rows = new Venda[count];
        for (var i = 0; i < count; i++)
        {
            var id = start + i;
            rows[i] = new Venda(id, $"C{id}");
        }

        var hasMore = end < _totalRows;
        var nextCursor = hasMore ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new BatchResult<Venda>(rows, nextCursor, hasMore);
    }
}

/// <summary>Source whose first read always throws — drives the failed-job path (default Abort).</summary>
public sealed class ThrowingSource : IBatchSource<Venda>
{
    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

    public Task<BatchResult<Venda>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("source exploded");
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
