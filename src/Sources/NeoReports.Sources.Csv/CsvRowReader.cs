using System.Runtime.CompilerServices;
using System.Text;

namespace NeoReports.Sources.Csv;

/// <summary>
/// Streaming RFC 4180 CSV row reader (ADR D58) — the read-direction mirror of
/// <c>NeoReports.Formats.Csv.CsvWriter</c>'s own escaping rules: an unquoted field ends at the
/// delimiter or a line ending; a quoted field (only recognized when <c>"</c> is the first character
/// of a field) runs until an unescaped closing <c>"</c>, where <c>""</c> means a literal embedded
/// quote and the field may itself contain the delimiter or a line ending. A hand-rolled character
/// state machine over <see cref="StreamReader"/> rather than a library — this only needs to be the
/// exact inverse of a writer this codebase already owns and golden-file-tests byte-for-byte.
/// </summary>
internal static class CsvRowReader
{
    /// <summary>
    /// Reads <paramref name="stream"/> as CSV, yielding one <c>string[]</c> per row (including the
    /// header row, if any — the caller decides whether to treat the first yielded row as a header).
    /// Only one row's fields are held in memory at a time; <see cref="StreamReader"/>'s own internal
    /// buffer stays a small, fixed size regardless of file size, so this is constant memory (rule 8).
    /// </summary>
    /// <param name="stream">The CSV content to read. Not disposed by this method — the caller owns it.</param>
    /// <param name="options">Delimiter/encoding options.</param>
    /// <param name="cancellationToken">Token that cancels the read, checked once per row.</param>
    public static async IAsyncEnumerable<string[]> ReadRowsAsync(
        Stream stream, CsvReaderOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        using var reader = new StreamReader(stream, options.InputEncoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        char delimiter = options.DelimiterChar;

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var rowStarted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int next = reader.Read();

            if (next < 0)
            {
                // EOF: flush a partially-built row (a file with no trailing newline), but never yield
                // a spurious empty row when the stream ended cleanly right after the last line ending.
                if (rowStarted || field.Length > 0 || fields.Count > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                yield break;
            }

            var c = (char)next;
            rowStarted = true;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read(); // consume the second quote of an escaped ""
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                // A quote only opens a quoted field as the first character of that field — RFC 4180.
                inQuotes = true;
                continue;
            }

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                    reader.Read(); // CRLF is one line ending, not two
                fields.Add(field.ToString());
                field.Clear();
                yield return fields.ToArray(); // ToArray() copies, so the list can be reused below
                fields.Clear();
                rowStarted = false;
                continue;
            }

            field.Append(c);
        }
    }
}
