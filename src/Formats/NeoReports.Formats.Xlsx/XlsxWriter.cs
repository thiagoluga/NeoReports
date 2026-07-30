using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using NeoReports.Abstractions;

namespace NeoReports.Formats.Xlsx;

/// <summary>
/// Streaming XLSX writer built on <see cref="OpenXmlWriter"/> (the SAX-style, forward-only writer).
/// Non-generic by contract: it receives already-projected <c>object?[]</c> rows in schema order.
/// Values keep their native Excel types (numbers, dates, booleans) so the spreadsheet is strongly
/// typed; the column's format string is applied as a number/date format via a precomputed stylesheet.
/// </summary>
/// <remarks>
/// Memory stays constant regardless of row count: the worksheet XML is streamed straight to a temp
/// file (nothing buffered per row), and the final <c>.xlsx</c> is assembled with
/// <see cref="XlsxOpcPackage"/> — a hand-written <c>ZipArchive</c> that deflates each entry to the
/// output as it copies, bypassing <c>System.IO.Packaging</c>'s in-memory entry buffer. Strings are
/// written as inline strings (not shared strings) so no per-value table accumulates. This replaces the
/// previous ClosedXML implementation, which materialized the whole workbook in memory (ADR D14).
/// </remarks>
public sealed class XlsxWriter : IReportWriter
{
    private readonly XlsxOptions _options;
    private ReportSchema? _schema;
    private Stream? _output;
    private Stylesheet? _stylesheet;
    private string? _tempPath;
    private FileStream? _sheetFile;
    private OpenXmlWriter? _writer;
    private int[] _numberStyles = [];
    private int[] _dateStyles = [];
    private int _nextRow = 1;
    private int _columnCount;
    private bool _hasDataRows;

    /// <summary>Creates an XLSX writer with the given options.</summary>
    /// <param name="options">XLSX formatting options.</param>
    public XlsxWriter(XlsxOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public string Format => "xlsx";

    /// <inheritdoc />
    public string MimeType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <inheritdoc />
    public string FileExtension => "xlsx";

    /// <inheritdoc />
    public Task InitializeAsync(WriterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _schema = context.Schema;
        _output = context.Output;
        _columnCount = _schema.Count;

        var styles = new XlsxStyleTable();
        _numberStyles = new int[_columnCount];
        _dateStyles = new int[_columnCount];
        for (var i = 0; i < _columnCount; i++)
        {
            ReportColumn column = _schema.Columns[i];
            _numberStyles[i] = styles.RegisterNumberStyle(column);
            _dateStyles[i] = styles.RegisterDateStyle(column);
        }

        _stylesheet = styles.Build();

        _tempPath = XlsxOpcPackage.CreateTempPath();
        _sheetFile = XlsxOpcPackage.CreateTempFile(_tempPath);
        _writer = new OpenXmlPartWriter(_sheetFile);
        _writer.WriteStartElement(new Worksheet());
        _writer.WriteStartElement(new SheetData());

        if (_options.WriteHeader)
        {
            _writer.WriteStartElement(new Row { RowIndex = 1U });
            for (var i = 0; i < _columnCount; i++)
            {
                ReportColumn column = _schema.Columns[i];
                var header = column.DisplayName ?? column.Name;
                _writer.WriteElement(XlsxCells.HeaderCell(header, XlsxCells.ColumnLetter(i) + "1"));
            }

            _writer.WriteEndElement();
            _nextRow = 2;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_writer is null || _schema is null)
            throw new InvalidOperationException("InitializeAsync must be called before WriteRowsAsync.");

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowRef = _nextRow.ToString(CultureInfo.InvariantCulture);
            _writer.WriteStartElement(new Row { RowIndex = (uint)_nextRow });
            for (var i = 0; i < _columnCount; i++)
            {
                var value = i < row.Length ? row[i] : null;
                Cell? cell = XlsxCells.BuildCell(value, XlsxCells.ColumnLetter(i) + rowRef, _numberStyles[i], _dateStyles[i]);
                if (cell is not null)
                    _writer.WriteElement(cell);
            }

            _writer.WriteEndElement();
            _nextRow++;
            _hasDataRows = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_writer is null || _output is null || _stylesheet is null || _tempPath is null)
            return;

        _writer.WriteEndElement(); // </sheetData>

        if (_options.UseAutoFilter && _columnCount > 0 && _hasDataRows)
        {
            var reference = $"A1:{XlsxCells.ColumnLetter(_columnCount - 1)}{_nextRow - 1}";
            _writer.WriteElement(new AutoFilter { Reference = reference });
        }

        _writer.WriteEndElement(); // </worksheet>
        _writer.Dispose();
        _writer = null;
        _sheetFile?.Dispose(); // flush the streamed worksheet XML to disk before it is copied out
        _sheetFile = null;

        var tempPath = _tempPath;
        _tempPath = null;
        try
        {
            var sheets = new[] { new XlsxSheetPart(_options.Sheet, tempPath) };
            await XlsxOpcPackage.AssembleAsync(_output, _stylesheet, sheets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            XlsxOpcPackage.TryDelete(tempPath);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _writer = null;
        _sheetFile?.Dispose();
        _sheetFile = null;
        XlsxOpcPackage.TryDelete(_tempPath);
        _tempPath = null;
        return ValueTask.CompletedTask;
    }
}
