using Amazon.S3;
using NeoReports.Abstractions;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Xlsx;

/// <summary>Fluent entry points for XLSX file sources (ADR D59) — local disk or Amazon S3.</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring an XLSX source read from the local filesystem. Reading is fully streaming
    /// (constant memory per row) — no cursor/keyset concept applies, since resuming just means
    /// continuing to read the same open worksheet.
    /// </summary>
    /// <param name="path">Path to the XLSX file.</param>
    /// <param name="options">Sheet/header options; defaults to the first sheet, header present.</param>
    public static XlsxFileSourceBuilder XlsxFile(string path, XlsxReaderOptions? options = null) =>
        new(path, options ?? new XlsxReaderOptions());

    /// <summary>
    /// Begins configuring an XLSX source read from an Amazon S3 object — symmetric to the
    /// <c>NeoReports.Destinations.S3</c> destination. Streams the object body directly; nothing is
    /// downloaded to a temp file first (<c>DocumentFormat.OpenXml</c> buffers a non-seekable stream
    /// internally, since the OOXML zip container needs random access — see ADR D59).
    /// </summary>
    /// <param name="bucket">The S3 bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="options">Sheet/header options; defaults to the first sheet, header present.</param>
    /// <param name="client">An explicit S3 client (caller owns its lifetime), or <c>null</c> to build one from ambient AWS credentials/region.</param>
    public static XlsxS3SourceBuilder XlsxS3(string bucket, string key, XlsxReaderOptions? options = null, IAmazonS3? client = null) =>
        new(bucket, key, options ?? new XlsxReaderOptions(), client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a local-file XLSX source, before the row type is chosen.</summary>
public sealed class XlsxFileSourceBuilder
{
    private readonly string _path;
    private readonly XlsxReaderOptions _options;

    internal XlsxFileSourceBuilder(string path, XlsxReaderOptions options)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _options = options;
    }

    /// <summary>
    /// Completes the source, materializing each row as <typeparamref name="T"/> by matching its
    /// longest constructor's parameter names against the header (case-insensitive) — requires
    /// <see cref="XlsxReaderOptions.Header"/> to stay enabled (the default).
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IStreamingSource<T> As<T>()
    {
        if (!_options.HasHeaderRow)
        {
            throw new ArgumentException(
                "The typed XLSX source requires a header row to match columns to constructor parameters by name; " +
                "disabling XlsxReaderOptions.Header(false) is only supported for the dynamic (config-driven) path.");
        }

        var materializer = new XlsxRecordMaterializer<T>();
        var path = _path;
        return new XlsxStreamingSource<T>(
            _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true)),
            _options, Source.Placeholder, materializer.Materialize);
    }
}

/// <summary>Intermediate builder for an S3 XLSX source, before the row type is chosen.</summary>
public sealed class XlsxS3SourceBuilder
{
    private readonly string _bucket;
    private readonly string _key;
    private readonly XlsxReaderOptions _options;
    private readonly IAmazonS3? _client;

    internal XlsxS3SourceBuilder(string bucket, string key, XlsxReaderOptions options, IAmazonS3? client)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Bucket must be provided.", nameof(bucket));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be provided.", nameof(key));
        _bucket = bucket;
        _key = key;
        _options = options;
        _client = client;
    }

    /// <summary>
    /// Completes the source, materializing each row as <typeparamref name="T"/> by matching its
    /// longest constructor's parameter names against the header (case-insensitive) — requires
    /// <see cref="XlsxReaderOptions.Header"/> to stay enabled (the default).
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IStreamingSource<T> As<T>()
    {
        if (!_options.HasHeaderRow)
        {
            throw new ArgumentException(
                "The typed XLSX source requires a header row to match columns to constructor parameters by name; " +
                "disabling XlsxReaderOptions.Header(false) is only supported for the dynamic (config-driven) path.");
        }

        var materializer = new XlsxRecordMaterializer<T>();
        var bucket = _bucket;
        var key = _key;
        var client = _client;
        return new XlsxStreamingSource<T>(
            async ct => await S3Stream.OpenAsync(client, bucket, key, ct).ConfigureAwait(false),
            _options, Source.Placeholder, materializer.Materialize);
    }
}
