using System.Runtime.CompilerServices;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Csv;

/// <summary>
/// <see cref="IStreamingSource{T}"/> over a CSV stream (ADR D58) — opens the stream once per run
/// (via the <c>openStream</c> factory, so a local path and an S3 object both plug in the same way),
/// reads the header row (if any) to build a column-name-to-ordinal map, then yields one materialized
/// <typeparamref name="T"/> per data row. Constant memory: only one row's fields are ever held at a
/// time (see <see cref="CsvRowReader"/>).
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class CsvStreamingSource<T> : IStreamingSource<T>
{
    private static readonly IReadOnlyDictionary<string, int> NoHeader = new Dictionary<string, int>(0);

    private readonly Func<CancellationToken, Task<Stream>> _openStream;
    private readonly CsvReaderOptions _options;
    private readonly Func<IReadOnlyDictionary<string, int>, string[], T> _materialize;

    /// <summary>Creates the source.</summary>
    /// <param name="openStream">Opens a fresh, readable stream for the CSV content.</param>
    /// <param name="options">Delimiter/encoding/header options.</param>
    /// <param name="schema">The declared schema (a placeholder for the typed path — real column projection comes from the report builder's own <c>.Columns(...)</c> step, not this value).</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from the header index and a row's raw fields.</param>
    public CsvStreamingSource(
        Func<CancellationToken, Task<Stream>> openStream,
        CsvReaderOptions options,
        ReportSchema schema,
        Func<IReadOnlyDictionary<string, int>, string[], T> materialize)
    {
        _openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> ReadAsync(
        ReportExecutionContext execution, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream stream = await _openStream(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            IAsyncEnumerator<string[]> rows = CsvRowReader.ReadRowsAsync(stream, _options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            await using (rows.ConfigureAwait(false))
            {
                var headerIndex = NoHeader;
                if (_options.HasHeaderRow)
                {
                    if (!await rows.MoveNextAsync().ConfigureAwait(false))
                        yield break; // empty file: no header, no rows
                    headerIndex = BuildHeaderIndex(rows.Current);
                }

                while (await rows.MoveNextAsync().ConfigureAwait(false))
                    yield return _materialize(headerIndex, rows.Current);
            }
        }
    }

    private static Dictionary<string, int> BuildHeaderIndex(string[] header)
    {
        var index = new Dictionary<string, int>(header.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            index[header[i]] = i;
        return index;
    }
}
