using System.Text;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using NeoReports.Core.Sources;

namespace NeoReports.Core.UnitTests.Fakes;

/// <summary>Reference row type used across the tests.</summary>
public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

/// <summary>Minimal service provider that resolves nothing (factories under test ignore it).</summary>
public sealed class EmptyServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

/// <summary>
/// In-memory batch source driven by page number. Optionally fails a given page a number of times
/// before succeeding, to exercise transient-retry behavior.
/// </summary>
public sealed class FakeBatchSource<T> : IBatchSource<T>
{
    private readonly IReadOnlyList<IReadOnlyList<T>> _pages;
    private readonly IReadOnlyDictionary<int, int> _failTimes;
    private readonly Dictionary<int, int> _attempts = new();

    public FakeBatchSource(IReadOnlyList<IReadOnlyList<T>> pages, IReadOnlyDictionary<int, int>? failTimes = null)
    {
        _pages = pages;
        _failTimes = failTimes ?? new Dictionary<int, int>();
    }

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("_", ColumnType.String) });

    public int ReadCalls { get; private set; }

    public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ReadCalls++;
        var page = context.PageNumber;

        if (_failTimes.TryGetValue(page, out var times))
        {
            var soFar = _attempts.GetValueOrDefault(page);
            if (soFar < times)
            {
                _attempts[page] = soFar + 1;
                throw new InvalidOperationException($"Transient failure reading page {page} (attempt {soFar + 1}).");
            }
        }

        var index = page - 1;
        if (index >= _pages.Count)
            return Task.FromResult(BatchResult<T>.Empty);

        var records = _pages[index];
        var hasMore = index + 1 < _pages.Count;
        var nextCursor = hasMore ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new BatchResult<T>(records, nextCursor, hasMore));
    }
}

/// <summary>
/// Minimal named by-name source (ADR D42's <see cref="INamedSourceResolver"/>) for testing the
/// typed-path wiring (F5) without a real SQL provider: records whether/with-what
/// <see cref="AttachServices"/> was called, then delegates reads to an inner fake.
/// </summary>
public sealed class FakeNamedBatchSource<T> : IBatchSource<T>, INamedSourceResolver
{
    private readonly FakeBatchSource<T> _inner;

    public FakeNamedBatchSource(string sourceName, IReadOnlyList<IReadOnlyList<T>> pages)
    {
        SourceName = sourceName;
        _inner = new FakeBatchSource<T>(pages);
    }

    public string SourceName { get; }

    public int AttachServicesCalls { get; private set; }
    public IServiceProvider? LastAttachedServices { get; private set; }

    public ReportSchema Schema => _inner.Schema;

    public void AttachServices(IServiceProvider services)
    {
        AttachServicesCalls++;
        LastAttachedServices = services;
    }

    public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        _inner.ReadBatchAsync(context, cancellationToken);
}

/// <summary>
/// Batch source that also implements <see cref="ISourceRowCounter"/> (ADR D47), for testing
/// progress-tracking wiring without a real ADO.NET/Mongo provider. Configurable to return a fixed
/// count, throw, or observe cancellation; tracks how many times it was counted.
/// </summary>
public sealed class FakeCountingBatchSource<T> : IBatchSource<T>, ISourceRowCounter
{
    private readonly FakeBatchSource<T> _inner;
    private readonly long? _count;
    private readonly Exception? _throws;

    public FakeCountingBatchSource(IReadOnlyList<IReadOnlyList<T>> pages, long? count = null, Exception? throws = null)
    {
        _inner = new FakeBatchSource<T>(pages);
        _count = count;
        _throws = throws;
    }

    public int CountCalls { get; private set; }

    public ReportSchema Schema => _inner.Schema;

    public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
        _inner.ReadBatchAsync(context, cancellationToken);

    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken)
    {
        CountCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (_throws is not null)
            throw _throws;
        return Task.FromResult<long?>(_count ?? 0L);
    }
}

/// <summary>Writer that records projected rows and can throw on chosen batches (1-based).</summary>
public sealed class FakeWriter : IReportWriter
{
    private readonly HashSet<int> _throwOnBatch;
    private Stream _output = Stream.Null;

    public FakeWriter(IEnumerable<int> throwOnBatch, string format = "fake", string extension = "fake")
    {
        _throwOnBatch = new HashSet<int>(throwOnBatch);
        Format = format;
        FileExtension = extension;
    }

    public List<object?[]> Rows { get; } = new();
    public int WriteCalls { get; private set; }
    public bool Finalized { get; private set; }

    public string Format { get; }
    public string MimeType => "application/x-fake";
    public string FileExtension { get; }

    public Task InitializeAsync(WriterContext context, CancellationToken cancellationToken)
    {
        _output = context.Output;
        return Task.CompletedTask;
    }

    public async Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        WriteCalls++;
        if (_throwOnBatch.Contains(WriteCalls))
            throw new InvalidOperationException($"Writer failure on batch {WriteCalls}.");

        foreach (var row in rows)
        {
            Rows.Add(row);
            var line = string.Join(',', row.Select(v => v?.ToString() ?? string.Empty)) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await _output.WriteAsync(bytes, cancellationToken);
        }
    }

    public Task FinalizeAsync(CancellationToken cancellationToken)
    {
        Finalized = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Factory that produces a single <see cref="FakeWriter"/> and exposes it for assertions.</summary>
public sealed class FakeWriterFactory : IWriterFactory
{
    private readonly int[] _throwOnBatch;
    private readonly string _extension;

    public FakeWriterFactory(params int[] throwOnBatch) : this("fake", "fake", throwOnBatch)
    {
    }

    public FakeWriterFactory(string format, string extension, params int[] throwOnBatch)
    {
        Format = format;
        _extension = extension;
        _throwOnBatch = throwOnBatch;
    }

    public FakeWriter? LastWriter { get; private set; }

    public string Format { get; }

    public IReportWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services)
    {
        var writer = new FakeWriter(_throwOnBatch, Format, _extension);
        LastWriter = writer;
        return writer;
    }
}

/// <summary>Destination that captures the bytes of every uploaded file.</summary>
public sealed class CapturingDestination : IReportDestination
{
    public Dictionary<string, byte[]> Files { get; } = new();

    public string Type => "capture";

    public async Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken)
    {
        using var stream = file.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        Files[file.FileName] = memory.ToArray();
        return UploadResult.Ok($"capture://{file.FileName}", file.FileName);
    }
}

/// <summary>Factory that produces a single <see cref="CapturingDestination"/> and exposes it.</summary>
public sealed class CapturingDestinationFactory : IDestinationFactory
{
    public CapturingDestination? LastDestination { get; private set; }

    public string Type => "capture";

    public IReportDestination Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services)
    {
        var destination = new CapturingDestination();
        LastDestination = destination;
        return destination;
    }
}

/// <summary>Destination that returns a failed <see cref="UploadResult"/> without throwing.</summary>
public sealed class FailingDestination : IReportDestination
{
    private readonly string _type;
    private readonly string _reason;

    public FailingDestination(string type, string reason)
    {
        _type = type;
        _reason = reason;
    }

    public string Type => _type;

    public Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(UploadResult.Fail(_reason));
}

/// <summary>Factory that produces a <see cref="FailingDestination"/> (mirrors how S3/Local report a failed upload).</summary>
public sealed class FailingDestinationFactory : IDestinationFactory
{
    private readonly string _reason;

    public FailingDestinationFactory(string type = "failing", string reason = "simulated upload failure")
    {
        Type = type;
        _reason = reason;
    }

    public string Type { get; }

    public IReportDestination Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new FailingDestination(Type, _reason);
}
