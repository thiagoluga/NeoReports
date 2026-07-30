using System.Globalization;
using System.Text;
using NeoReports.Abstractions;

namespace NeoReports.Formats.Csv;

/// <summary>
/// Streaming CSV writer. Non-generic by contract: it receives already-projected
/// <c>object?[]</c> rows in schema order, formats each value per the column's format/culture,
/// escapes per RFC 4180, and writes CRLF-terminated lines to the output stream.
/// </summary>
public sealed class CsvWriter : IReportWriter
{
    private const string LineEnding = "\r\n";

    private readonly CsvOptions _options;
    private StreamWriter? _writer;
    private ReportSchema? _schema;
    private CultureInfo[] _cultures = Array.Empty<CultureInfo>();

    /// <summary>Creates a CSV writer with the given options.</summary>
    /// <param name="options">CSV formatting options.</param>
    public CsvWriter(CsvOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string Format => "csv";

    /// <inheritdoc />
    public string MimeType => "text/csv";

    /// <inheritdoc />
    public string FileExtension => "csv";

    /// <inheritdoc />
    public async Task InitializeAsync(WriterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _schema = context.Schema;
        _writer = new StreamWriter(context.Output, _options.OutputEncoding, leaveOpen: true)
        {
            NewLine = LineEnding,
        };

        // Resolve a culture per column once.
        _cultures = new CultureInfo[_schema.Count];
        for (var i = 0; i < _schema.Count; i++)
        {
            var culture = _schema.Columns[i].Culture;
            _cultures[i] = string.IsNullOrEmpty(culture)
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(culture);
        }

        if (_options.WriteHeader)
        {
            var header = new StringBuilder();
            for (var i = 0; i < _schema.Count; i++)
            {
                if (i > 0)
                    header.Append(_options.DelimiterChar);
                var column = _schema.Columns[i];
                header.Append(Escape(column.DisplayName ?? column.Name));
            }

            await _writer.WriteAsync(header.ToString()).ConfigureAwait(false);
            await _writer.WriteAsync(LineEnding).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_writer is null || _schema is null)
            throw new InvalidOperationException("InitializeAsync must be called before WriteRowsAsync.");

        var line = new StringBuilder();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            line.Clear();
            for (var i = 0; i < _schema.Count; i++)
            {
                if (i > 0)
                    line.Append(_options.DelimiterChar);
                var value = i < row.Length ? row[i] : null;
                line.Append(Escape(FormatValue(value, _schema.Columns[i], _cultures[i])));
            }

            await _writer.WriteAsync(line.ToString()).ConfigureAwait(false);
            await _writer.WriteAsync(LineEnding).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
    }

    private static string FormatValue(object? value, ReportColumn column, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        if (!string.IsNullOrEmpty(column.Format) && value is IFormattable formattable)
            return formattable.ToString(column.Format, culture);

        // Binary would otherwise stringify to "System.Byte[]" (total data loss); Base64 is a faithful,
        // culture-independent text form (matches the XLSX writer).
        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes);

        return Convert.ToString(value, culture) ?? string.Empty;
    }

    private string Escape(string field)
    {
        var needsQuoting = field.IndexOf(_options.DelimiterChar) >= 0
            || field.IndexOf('"') >= 0
            || field.IndexOf('\n') >= 0
            || field.IndexOf('\r') >= 0;

        if (!needsQuoting)
            return field;

        return string.Concat("\"", field.Replace("\"", "\"\""), "\"");
    }
}
