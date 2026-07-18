using System.Runtime.CompilerServices;
using NeoReports.Abstractions;
using NeoReports.Sources.Files.Common;
using Parquet;

namespace NeoReports.Sources.Parquet;

/// <summary>
/// <see cref="IStreamingSource{T}"/> over a Parquet file (ADR D60) — opens the stream once per run
/// (via the <c>openStream</c> factory, so a local path and an S3 object plug in the same way), makes
/// it seekable if necessary (<see cref="SeekableStream.EnsureSeekableAsync"/> — Parquet's reader
/// requires random access, unlike XLSX's), then yields the rows of one <b>row group</b> at a time.
/// Row-group granularity is the finest the columnar format exposes and the honest interpretation of
/// rule 8's constant memory: peak memory is bounded by a single row group's rows, never the whole
/// file. The per-row-group read is delegated (the <c>readRowGroup</c> constructor argument) so
/// the same class serves both the typed path (<c>ParquetSerializer.DeserializeAsync&lt;T&gt;</c>) and
/// the dynamic path (<c>DeserializeUntypedAsync</c> materialized into <see cref="ReportRecord"/>),
/// mirroring how the CSV/XLSX sources reuse one streaming class behind a materializer delegate. The
/// per-row-group read is supplied as a constructor delegate (<c>readRowGroup</c>).
/// </summary>
/// <remarks>
/// A <see cref="ParquetReader"/> is opened once here just to learn <c>RowGroupCount</c>, then disposed
/// — the loop below hands the raw <see cref="Stream"/> to <c>readRowGroup</c> instead of that reader,
/// because <c>Parquet.Net</c> 6.0.3's <c>ParquetSerializer.DeserializeAsync</c>/<c>DeserializeUntypedAsync</c>
/// only accept a <see cref="Stream"/> or file path, not an already-open reader — so each row-group
/// call re-parses the file's footer metadata (verified empirically). This is metadata-only work
/// (never a second read of row data, so peak memory stays bounded), but is real, avoidable I/O for a
/// file with many row groups. Avoiding it would mean abandoning <c>ParquetSerializer</c> for
/// <c>Parquet.Net</c>'s lower-level, buffer-oriented per-column read API and hand-rolling the exact
/// type-mapping/nullable/decimal-precision logic <c>ParquetSerializer</c> already gets right — judged
/// not worth the risk for this pass; see ADR D60's "known, accepted tradeoff" note.
/// </remarks>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class ParquetStreamingSource<T> : IStreamingSource<T>
{
    private readonly Func<CancellationToken, Task<Stream>> _openStream;
    private readonly Func<Stream, int, CancellationToken, Task<IReadOnlyList<T>>> _readRowGroup;

    /// <summary>Creates the source.</summary>
    /// <param name="openStream">Opens a fresh, readable stream for the Parquet file.</param>
    /// <param name="schema">The declared schema (a placeholder for the typed path — real column projection comes from the report builder's own <c>.Columns(...)</c> step, not this value).</param>
    /// <param name="readRowGroup">Reads and materializes all rows of the row group at the given index from an open, seekable stream.</param>
    public ParquetStreamingSource(
        Func<CancellationToken, Task<Stream>> openStream,
        ReportSchema schema,
        Func<Stream, int, CancellationToken, Task<IReadOnlyList<T>>> readRowGroup)
    {
        _openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _readRowGroup = readRowGroup ?? throw new ArgumentNullException(nameof(readRowGroup));
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> ReadAsync(
        ReportExecutionContext execution, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream opened = await _openStream(cancellationToken).ConfigureAwait(false);
        Stream stream = await SeekableStream.EnsureSeekableAsync(opened, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            int rowGroupCount;
            ParquetReader reader = await ParquetReader
                .CreateAsync(stream, new ParquetOptions(), leaveStreamOpen: true, cancellationToken)
                .ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
                rowGroupCount = reader.RowGroupCount;

            for (var i = 0; i < rowGroupCount; i++)
            {
                IReadOnlyList<T> rows = await _readRowGroup(stream, i, cancellationToken).ConfigureAwait(false);
                foreach (T row in rows)
                    yield return row;
            }
        }
    }
}
