using System.Runtime.CompilerServices;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// <see cref="IStreamingSource{T}"/> over an XLSX stream (ADR D59) — opens the stream once per run
/// (via the <c>openStream</c> factory, so a local path and an S3 object both plug in the same way),
/// reads the header row (if any) to build a column-name-to-ordinal map, then yields one materialized
/// <typeparamref name="T"/> per data row. <see cref="XlsxRowReader"/> is synchronous (see its own XML
/// doc for why), so this method simply enumerates it inside an async iterator — no thread hop is
/// needed since the ADO/CSV sources this mirrors don't offer one either, and the underlying
/// <c>OpenXmlReader</c> traversal is CPU/zip-inflate work, not I/O that benefits from one.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class XlsxStreamingSource<T> : IStreamingSource<T>
{
    private static readonly IReadOnlyDictionary<string, int> NoHeader = new Dictionary<string, int>(0);

    private readonly Func<CancellationToken, Task<Stream>> _openStream;
    private readonly XlsxReaderOptions _options;
    private readonly Func<IReadOnlyDictionary<string, int>, object?[], T> _materialize;

    /// <summary>Creates the source.</summary>
    /// <param name="openStream">Opens a fresh, readable stream for the XLSX package.</param>
    /// <param name="options">Sheet/header options.</param>
    /// <param name="schema">The declared schema (a placeholder for the typed path — real column projection comes from the report builder's own <c>.Columns(...)</c> step, not this value).</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from the header index and a row's cells.</param>
    public XlsxStreamingSource(
        Func<CancellationToken, Task<Stream>> openStream,
        XlsxReaderOptions options,
        ReportSchema schema,
        Func<IReadOnlyDictionary<string, int>, object?[], T> materialize)
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
            using IEnumerator<object?[]> rows =
                XlsxRowReader.ReadRows(stream, _options.SheetNameValue, cancellationToken).GetEnumerator();

            var headerIndex = NoHeader;
            if (_options.HasHeaderRow)
            {
                if (!rows.MoveNext())
                    yield break; // empty sheet: no header, no rows
                headerIndex = BuildHeaderIndex(rows.Current);
            }

            while (rows.MoveNext())
                yield return _materialize(headerIndex, rows.Current);
        }
    }

    private static Dictionary<string, int> BuildHeaderIndex(object?[] header)
    {
        var index = new Dictionary<string, int>(header.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i]?.ToString();
            if (!string.IsNullOrEmpty(name))
                index[name] = i;
        }

        return index;
    }
}
