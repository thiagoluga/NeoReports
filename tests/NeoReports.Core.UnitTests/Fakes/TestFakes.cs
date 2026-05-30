using System.Text;
using NeoReports.Abstractions;

namespace NeoReports.Core.UnitTests.Fakes;

/// <summary>Reference row type used across the tests.</summary>
public sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);

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
