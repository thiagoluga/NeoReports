namespace NeoReports.Abstractions;

// ---------------------------------------------------------------------------
// Formats (writers)
// ---------------------------------------------------------------------------

/// <summary>Inputs given to a writer when it is initialized for one output.</summary>
public sealed class WriterContext
{
    public WriterContext(
        ReportExecutionContext execution,
        Stream output,
        ReportSchema schema,
        IReadOnlyDictionary<string, object?>? options)
    {
        Execution = execution;
        Output = output;
        Schema = schema;
        Options = options ?? new Dictionary<string, object?>();
    }

    public ReportExecutionContext Execution { get; }
    public Stream Output { get; }
    public ReportSchema Schema { get; }

    /// <summary>Format-specific options (delimiter, encoding, sheet name, ...).</summary>
    public IReadOnlyDictionary<string, object?> Options { get; }
}

/// <summary>
/// A format serializer. Deliberately NON-generic: the pipeline projects each typed row into
/// <c>object?[]</c> in schema order before calling <see cref="WriteRowsAsync"/>, so format
/// plugins never need to know about <c>T</c>.
/// </summary>
public interface IReportWriter : IAsyncDisposable
{
    /// <summary>Stable format id (e.g. "csv", "xlsx").</summary>
    string Format { get; }
    string MimeType { get; }
    string FileExtension { get; }

    Task InitializeAsync(WriterContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a page of already-projected rows. Each row is an <c>object?[]</c> whose elements
    /// align with <see cref="WriterContext.Schema"/> column order.
    /// </summary>
    Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken);

    Task FinalizeAsync(CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------
// Destinations (upload of the finished file)
// ---------------------------------------------------------------------------

/// <summary>A finished report file ready to be uploaded to a destination.</summary>
public sealed class ReportFile
{
    private readonly Func<Stream> _openRead;

    public ReportFile(string fileName, string mimeType, long sizeBytes, Func<Stream> openRead)
    {
        FileName = fileName;
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        _openRead = openRead;
    }

    public string FileName { get; }
    public string MimeType { get; }
    public long SizeBytes { get; }

    /// <summary>Opens a fresh read stream over the file content.</summary>
    public Stream OpenRead() => _openRead();
}

/// <summary>Inputs given to a destination when uploading.</summary>
public sealed class DestinationContext
{
    public DestinationContext(ReportExecutionContext execution, IReadOnlyDictionary<string, object?>? options)
    {
        Execution = execution;
        Options = options ?? new Dictionary<string, object?>();
    }

    public ReportExecutionContext Execution { get; }
    public IReadOnlyDictionary<string, object?> Options { get; }
}

/// <summary>Outcome of an upload attempt.</summary>
public sealed class UploadResult
{
    private UploadResult(bool success, string? url, string? remotePath, string? errorMessage)
    {
        Success = success;
        Url = url;
        RemotePath = remotePath;
        ErrorMessage = errorMessage;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public bool Success { get; }
    public string? Url { get; }
    public string? RemotePath { get; }
    public string? ErrorMessage { get; }
    public DateTimeOffset CompletedAt { get; }

    public static UploadResult Ok(string? url, string? remotePath) => new(true, url, remotePath, null);
    public static UploadResult Fail(string errorMessage) => new(false, null, null, errorMessage);
}

/// <summary>A destination the finished file is uploaded to (Local, S3, ...).</summary>
public interface IReportDestination
{
    /// <summary>Stable type id (e.g. "local", "s3").</summary>
    string Type { get; }

    Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken);
}
