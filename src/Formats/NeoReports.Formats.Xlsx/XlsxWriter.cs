using ClosedXML.Excel;
using NeoReports.Abstractions;

namespace NeoReports.Formats.Xlsx;

/// <summary>
/// XLSX writer backed by ClosedXML. Non-generic by contract: it receives already-projected
/// <c>object?[]</c> rows in schema order. Values keep their native Excel types (numbers, dates,
/// booleans) so the spreadsheet is strongly typed; the column's format string is applied as a
/// number/date format. The auto-filter and the workbook itself are finalized in
/// <see cref="FinalizeAsync"/>.
/// </summary>
/// <remarks>
/// ClosedXML builds the whole workbook in memory before saving, so this writer's memory grows
/// with the row count — unlike the streaming CSV writer (see ADR D14). Acceptable for the v1
/// report sizes; a streaming OpenXML writer is a post-MVP option.
/// </remarks>
public sealed class XlsxWriter : IReportWriter
{
    private readonly XlsxOptions _options;
    private XLWorkbook? _workbook;
    private IXLWorksheet? _sheet;
    private ReportSchema? _schema;
    private Stream? _output;
    private int _nextRow = 1;
    private int _columnCount;

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
        _workbook = new XLWorkbook();
        _sheet = _workbook.AddWorksheet(_options.Sheet);

        if (_options.WriteHeader)
        {
            for (var i = 0; i < _schema.Count; i++)
            {
                var column = _schema.Columns[i];
                _sheet.Cell(_nextRow, i + 1).Value = column.DisplayName ?? column.Name;
            }

            _sheet.Row(_nextRow).Style.Font.Bold = true;
            _nextRow++;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteRowsAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_sheet is null || _schema is null)
            throw new InvalidOperationException("InitializeAsync must be called before WriteRowsAsync.");

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < _schema.Count; i++)
            {
                var value = i < row.Length ? row[i] : null;
                var cell = _sheet.Cell(_nextRow, i + 1);
                XlsxCells.SetCell(cell, value, _schema.Columns[i]);
            }

            _nextRow++;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_workbook is null || _sheet is null || _output is null)
            return;

        if (_options.UseAutoFilter && _columnCount > 0 && _nextRow > 1)
            _sheet.Range(1, 1, _nextRow - 1, _columnCount).SetAutoFilter();

        _sheet.Columns().AdjustToContents();

        // ClosedXML saves synchronously into the (in-memory) output stream.
        await Task.Run(() => _workbook!.SaveAs(_output!), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _workbook?.Dispose();
        _workbook = null;
        _sheet = null;
        return ValueTask.CompletedTask;
    }

}
