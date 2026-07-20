using Amazon.S3;
using NeoReports.Abstractions;
using NeoReports.Sources.Files.Common;

namespace NeoReports.Sources.Parquet;

/// <summary>Fluent entry points for Parquet file sources (ADR D60) — local disk or Amazon S3.</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring a Parquet source read from the local filesystem. Reading is streaming one
    /// row group at a time (constant memory, bounded by a row group's size — ADR D60) — no
    /// cursor/keyset concept applies, since resuming just means continuing to read the same open file.
    /// Unlike CSV/XLSX there are no reader options: Parquet is self-describing (no header/sheet toggle)
    /// and no meaningful read-time knob exists to expose.
    /// </summary>
    /// <param name="path">Path to the Parquet file.</param>
    public static ParquetFileSourceBuilder ParquetFile(string path) => new(path);

    /// <summary>
    /// Begins configuring a Parquet source read from an Amazon S3 object — symmetric to the
    /// <c>NeoReports.Destinations.S3</c> destination. Because <c>Parquet.Net</c>'s reader requires a
    /// seekable stream (the footer is read from the end of the file) and an S3 response body is
    /// forward-only, the object is copied once into a self-deleting temp file before reading — see
    /// <see cref="SeekableStream"/> and ADR D60 (the mirror image of XLSX's transparent buffering).
    /// </summary>
    /// <param name="bucket">The S3 bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="client">An explicit S3 client (caller owns its lifetime), or <c>null</c> to build one from ambient AWS credentials/region.</param>
    public static ParquetS3SourceBuilder ParquetS3(string bucket, string key, IAmazonS3? client = null) =>
        new(bucket, key, client);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a local-file Parquet source, before the row type is chosen.</summary>
public sealed class ParquetFileSourceBuilder
{
    private readonly string _path;

    internal ParquetFileSourceBuilder(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>
    /// Completes the source, deserializing each row group directly into <typeparamref name="T"/> via
    /// <c>Parquet.Net</c>'s own object mapper. <typeparamref name="T"/> must have a public parameterless
    /// constructor and settable/init properties (a class or an <c>init</c>-only record — not a
    /// positional record, unlike the CSV/XLSX typed paths); see the capability note in ADR D60. Columns
    /// are matched to properties by name, case-insensitively.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IStreamingSource<T> As<T>() where T : class, new()
    {
        var path = _path;
        return new ParquetStreamingSource<T>(
            _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true)),
            Source.Placeholder,
            ParquetRowGroups.TypedReader<T>());
    }
}

/// <summary>Intermediate builder for an S3 Parquet source, before the row type is chosen.</summary>
public sealed class ParquetS3SourceBuilder
{
    private readonly string _bucket;
    private readonly string _key;
    private readonly IAmazonS3? _client;

    internal ParquetS3SourceBuilder(string bucket, string key, IAmazonS3? client)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Bucket must be provided.", nameof(bucket));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be provided.", nameof(key));
        _bucket = bucket;
        _key = key;
        _client = client;
    }

    /// <summary>
    /// Completes the source, deserializing each row group directly into <typeparamref name="T"/> via
    /// <c>Parquet.Net</c>'s own object mapper. <typeparamref name="T"/> must have a public parameterless
    /// constructor and settable/init properties (a class or an <c>init</c>-only record — not a
    /// positional record, unlike the CSV/XLSX typed paths); see the capability note in ADR D60. Columns
    /// are matched to properties by name, case-insensitively.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IStreamingSource<T> As<T>() where T : class, new()
    {
        var bucket = _bucket;
        var key = _key;
        var client = _client;
        return new ParquetStreamingSource<T>(
            async ct => await S3Stream.OpenAsync(client, bucket, key, ct).ConfigureAwait(false),
            Source.Placeholder,
            ParquetRowGroups.TypedReader<T>());
    }
}
