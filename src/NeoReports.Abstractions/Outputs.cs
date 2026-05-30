namespace NeoReports.Abstractions;

// ---------------------------------------------------------------------------
// Formats (writers)
// ---------------------------------------------------------------------------

/// <summary>Inputs given to a writer when it is initialized for one output.</summary>
public sealed class WriterContext
{
    /// <summary>Creates a context for one writer/output.</summary>
    /// <param name="execution">The ambient execution context.</param>
    /// <param name="output">Destination stream the writer appends to.</param>
    /// <param name="schema">Schema describing the projected row layout.</param>
    /// <param name="options">Format-specific options; <c>null</c> is treated as empty.</param>
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

    /// <summary>The ambient execution context.</summary>
    public ReportExecutionContext Execution { get; }

    /// <summary>Destination stream the writer appends to.</summary>
    public Stream Output { get; }

    /// <summary>Schema describing the projected row layout.</summary>
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

    /// <summary>MIME type of the produced file.</summary>
    string MimeType { get; }

    /// <summary>File extension (without the dot) of the produced file.</summary>
    string FileExtension { get; }

    /// <summary>Initializes the writer for a single output (e.g. writes the header).</summary>
    /// <param name="context">Output stream, schema, and options.</param>
    /// <param name="cancellationToken">Token that cancels initialization.</param>
    Task InitializeAsync(WriterContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a page of already-projected rows. Each row is an <c>object?[]</c> whose elements
    /// align with <see cref="WriterContext.Schema"/> column order.
    /// </summary>
    /// <param name="rows">The projected rows to write.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken);

    /// <summary>Flushes and finalizes the output (e.g. closes the workbook).</summary>
    /// <param name="cancellationToken">Token that cancels finalization.</param>
    Task FinalizeAsync(CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------
// Destinations (upload of the finished file)
// ---------------------------------------------------------------------------

/// <summary>A finished report file ready to be uploaded to a destination.</summary>
public sealed class ReportFile
{
    private readonly Func<Stream> _openRead;

    /// <summary>Creates a handle over a finished file.</summary>
    /// <param name="fileName">Suggested file name (including extension).</param>
    /// <param name="mimeType">MIME type of the content.</param>
    /// <param name="sizeBytes">Size of the content in bytes.</param>
    /// <param name="openRead">Factory that opens a fresh read stream over the content.</param>
    public ReportFile(string fileName, string mimeType, long sizeBytes, Func<Stream> openRead)
    {
        FileName = fileName;
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        _openRead = openRead;
    }

    /// <summary>Suggested file name (including extension).</summary>
    public string FileName { get; }

    /// <summary>MIME type of the content.</summary>
    public string MimeType { get; }

    /// <summary>Size of the content in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>Opens a fresh read stream over the file content.</summary>
    public Stream OpenRead() => _openRead();
}

/// <summary>Inputs given to a destination when uploading.</summary>
public sealed class DestinationContext
{
    /// <summary>Creates a context for one upload.</summary>
    /// <param name="execution">The ambient execution context.</param>
    /// <param name="options">Destination-specific options; <c>null</c> is treated as empty.</param>
    public DestinationContext(ReportExecutionContext execution, IReadOnlyDictionary<string, object?>? options)
    {
        Execution = execution;
        Options = options ?? new Dictionary<string, object?>();
    }

    /// <summary>The ambient execution context.</summary>
    public ReportExecutionContext Execution { get; }

    /// <summary>Destination-specific options (path/key templates, bucket, ...).</summary>
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

    /// <summary>Whether the upload succeeded.</summary>
    public bool Success { get; }

    /// <summary>Public URL of the uploaded object, when available.</summary>
    public string? Url { get; }

    /// <summary>Remote path/key of the uploaded object, when available.</summary>
    public string? RemotePath { get; }

    /// <summary>Error message when the upload failed.</summary>
    public string? ErrorMessage { get; }

    /// <summary>UTC timestamp when the attempt completed.</summary>
    public DateTimeOffset CompletedAt { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="url">Public URL of the uploaded object, when available.</param>
    /// <param name="remotePath">Remote path/key of the uploaded object, when available.</param>
    public static UploadResult Ok(string? url, string? remotePath) => new(true, url, remotePath, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="errorMessage">Reason the upload failed.</param>
    public static UploadResult Fail(string errorMessage) => new(false, null, null, errorMessage);
}

/// <summary>A destination the finished file is uploaded to (Local, S3, ...).</summary>
public interface IReportDestination
{
    /// <summary>Stable type id (e.g. "local", "s3").</summary>
    string Type { get; }

    /// <summary>Uploads a finished file to this destination.</summary>
    /// <param name="file">The finished file to upload.</param>
    /// <param name="context">Destination options and execution context.</param>
    /// <param name="cancellationToken">Token that cancels the upload.</param>
    /// <returns>The outcome of the upload.</returns>
    Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken);
}
