using Amazon.S3;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Csv;

/// <summary>Fluent entry points for CSV file sources (ADR D58) — local disk or Amazon S3.</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring a CSV source read from the local filesystem. Reading is fully streaming
    /// (constant memory) — no cursor/keyset concept applies, since resuming just means continuing to
    /// read the same open file.
    /// </summary>
    /// <param name="path">Path to the CSV file.</param>
    /// <param name="options">Delimiter/encoding/header options; defaults to comma, UTF-8, header present.</param>
    public static CsvFileSourceBuilder CsvFile(string path, CsvReaderOptions? options = null) =>
        new(path, options ?? new CsvReaderOptions());

    /// <summary>
    /// Begins configuring a CSV source read from an Amazon S3 object — symmetric to the
    /// <c>NeoReports.Destinations.S3</c> destination. Streams the object body directly; nothing is
    /// downloaded to a temp file first.
    /// </summary>
    /// <param name="bucket">The S3 bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="options">Delimiter/encoding/header options; defaults to comma, UTF-8, header present.</param>
    /// <param name="client">An explicit S3 client (caller owns its lifetime), or <c>null</c> to build one from ambient AWS credentials/region.</param>
    public static CsvS3SourceBuilder CsvS3(string bucket, string key, CsvReaderOptions? options = null, IAmazonS3? client = null) =>
        new(bucket, key, options ?? new CsvReaderOptions(), client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a local-file CSV source, before the row type is chosen.</summary>
public sealed class CsvFileSourceBuilder
{
    private readonly string _path;
    private readonly CsvReaderOptions _options;

    internal CsvFileSourceBuilder(string path, CsvReaderOptions options)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _options = options;
    }

    /// <summary>
    /// Completes the source, materializing each row as <typeparamref name="T"/> by matching its
    /// longest constructor's parameter names against the CSV header (case-insensitive) — requires
    /// <see cref="CsvReaderOptions.Header"/> to stay enabled (the default).
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IStreamingSource<T> As<T>()
    {
        if (!_options.HasHeaderRow)
        {
            throw new ArgumentException(
                "The typed CSV source requires a header row to match columns to constructor parameters by name; " +
                "disabling CsvReaderOptions.Header(false) is only supported for the dynamic (config-driven) path.");
        }

        var materializer = new CsvRecordMaterializer<T>();
        var path = _path;
        return new CsvStreamingSource<T>(
            _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true)),
            _options, Source.Placeholder, materializer.Materialize);
    }
}

/// <summary>Intermediate builder for an S3 CSV source, before the row type is chosen.</summary>
public sealed class CsvS3SourceBuilder
{
    private readonly string _bucket;
    private readonly string _key;
    private readonly CsvReaderOptions _options;
    private readonly IAmazonS3? _client;

    internal CsvS3SourceBuilder(string bucket, string key, CsvReaderOptions options, IAmazonS3? client)
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
    /// longest constructor's parameter names against the CSV header (case-insensitive) — requires
    /// <see cref="CsvReaderOptions.Header"/> to stay enabled (the default).
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IStreamingSource<T> As<T>()
    {
        if (!_options.HasHeaderRow)
        {
            throw new ArgumentException(
                "The typed CSV source requires a header row to match columns to constructor parameters by name; " +
                "disabling CsvReaderOptions.Header(false) is only supported for the dynamic (config-driven) path.");
        }

        var materializer = new CsvRecordMaterializer<T>();
        var bucket = _bucket;
        var key = _key;
        var client = _client;
        return new CsvStreamingSource<T>(
            async ct => await S3Stream.OpenAsync(client, bucket, key, ct).ConfigureAwait(false),
            _options, Source.Placeholder, materializer.Materialize);
    }
}
