using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using NeoReports.Abstractions;
using NeoReports.Core.Sections;
using NeoReports.Formats.Xlsx;
using NeoReports.Licensing;

namespace NeoReports.Xlsx.Pro;

/// <summary>
/// Streaming multi-sheet XLSX workbook writer (Pro): one worksheet per section, all in a single
/// <c>.xlsx</c> file. Built on <see cref="OpenXmlWriter"/> and reusing the exact cell/style helpers of
/// the MIT XLSX writer (<see cref="XlsxCells"/>, <see cref="XlsxStyleTable"/>) so native types and
/// formats match. Replaces the previous ClosedXML implementation, which materialized the whole
/// workbook in memory (ADR D14).
/// </summary>
/// <remarks>
/// Section rows arrive interleaved across many <see cref="WriteSectionRowsAsync"/> calls, so a sheet
/// cannot be finished before the next begins. Each section therefore streams its <c>&lt;worksheet&gt;</c>
/// XML to its own temp file through a dedicated <see cref="OpenXmlWriter"/> that stays open, positioned
/// inside its <c>&lt;sheetData&gt;</c>; every call routes to the right one. In
/// <see cref="FinalizeAsync"/> all writers close and the temp files are copied into a hand-assembled
/// package (<see cref="XlsxOpcPackage"/>) streamed to the output — bypassing
/// <c>System.IO.Packaging</c>'s in-memory entry buffer. Memory stays O(sections × pageSize).
/// </remarks>
public sealed class XlsxWorkbookWriter : IReportSectionedWriter
{
    private readonly XlsxWorkbookOptions _options;
    private Stream? _output;
    private Stylesheet? _stylesheet;
    private IReadOnlyList<ReportSection> _sections = [];
    private string[] _tempPaths = [];
    private FileStream?[] _sheetFiles = [];
    private OpenXmlWriter?[] _writers = [];
    private string[] _sheetNames = [];
    private int[][] _numberStyles = [];
    private int[][] _dateStyles = [];
    private int[] _nextRow = [];
    private bool[] _hasDataRows = [];

    /// <summary>
    /// Creates the workbook writer with the given options. Gated independently of
    /// <see cref="XlsxWorkbookWriterFactory"/> (ADR D70, Q2): this type is public, sealed, and needs
    /// nothing from the factory beyond a publicly-constructible <see cref="XlsxWorkbookOptions"/>, so
    /// a caller could otherwise reach the whole multi-sheet implementation through a hand-rolled
    /// <see cref="ISectionedWriterFactory"/> without the factory's check ever running.
    /// </summary>
    /// <param name="options">Workbook formatting options.</param>
    /// <exception cref="NeoReportsLicenseException">No valid NeoReports Pro license is configured.</exception>
    public XlsxWorkbookWriter(XlsxWorkbookOptions options)
    {
        ProLicenseGate.EnsureValidated();
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Format => "xlsx-workbook";

    /// <inheritdoc />
    public string MimeType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <inheritdoc />
    public string FileExtension => "xlsx";

    /// <inheritdoc />
    public Task InitializeAsync(SectionedWriterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _output = context.Output;
        _sections = context.Sections;
        var count = _sections.Count;

        // One workbook-wide stylesheet must be written before any sheet references a style, so register
        // every section's column styles first, then build the styles part once.
        var styles = new XlsxStyleTable();
        _numberStyles = new int[count][];
        _dateStyles = new int[count][];
        for (var s = 0; s < count; s++)
        {
            ReportSchema schema = _sections[s].Schema;
            var numberStyles = new int[schema.Count];
            var dateStyles = new int[schema.Count];
            for (var i = 0; i < schema.Count; i++)
            {
                ReportColumn column = schema.Columns[i];
                numberStyles[i] = styles.RegisterNumberStyle(column);
                dateStyles[i] = styles.RegisterDateStyle(column);
            }

            _numberStyles[s] = numberStyles;
            _dateStyles[s] = dateStyles;
        }

        _stylesheet = styles.Build();

        _tempPaths = new string[count];
        _sheetFiles = new FileStream?[count];
        _writers = new OpenXmlWriter?[count];
        _sheetNames = new string[count];
        _nextRow = new int[count];
        _hasDataRows = new bool[count];

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < count; s++)
        {
            ReportSection section = _sections[s];
            _sheetNames[s] = UniqueSheetName(section.Name, s, usedNames);

            var tempPath = XlsxOpcPackage.CreateTempPath();
            _tempPaths[s] = tempPath;
            FileStream sheetFile = XlsxOpcPackage.CreateTempFile(tempPath);
            _sheetFiles[s] = sheetFile;

            // Assign the new writer straight to the field (its owner) — a separate local would look like
            // an undisposed IDisposable to CodeQL, which cannot see it stored in the array field.
            _writers[s] = new OpenXmlPartWriter(sheetFile);
            OpenXmlWriter writer = _writers[s]!;
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());
            _nextRow[s] = 1;

            if (_options.WriteHeader)
            {
                ReportSchema schema = section.Schema;
                writer.WriteStartElement(new Row { RowIndex = 1U });
                for (var i = 0; i < schema.Count; i++)
                {
                    ReportColumn column = schema.Columns[i];
                    var header = column.DisplayName ?? column.Name;
                    writer.WriteElement(XlsxCells.HeaderCell(header, XlsxCells.ColumnLetter(i) + "1"));
                }

                writer.WriteEndElement();
                _nextRow[s] = 2;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteSectionRowsAsync(int sectionIndex, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        OpenXmlWriter writer = _writers[sectionIndex]
            ?? throw new InvalidOperationException("InitializeAsync must be called before WriteSectionRowsAsync.");

        ReportSchema schema = _sections[sectionIndex].Schema;
        int[] numberStyles = _numberStyles[sectionIndex];
        int[] dateStyles = _dateStyles[sectionIndex];
        var nextRow = _nextRow[sectionIndex];

        foreach (var values in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowRef = nextRow.ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement(new Row { RowIndex = (uint)nextRow });
            for (var i = 0; i < schema.Count; i++)
            {
                var value = i < values.Length ? values[i] : null;
                Cell? cell = XlsxCells.BuildCell(value, XlsxCells.ColumnLetter(i) + rowRef, numberStyles[i], dateStyles[i]);
                if (cell is not null)
                    writer.WriteElement(cell);
            }

            writer.WriteEndElement();
            nextRow++;
        }

        _nextRow[sectionIndex] = nextRow;
        if (rows.Count > 0)
            _hasDataRows[sectionIndex] = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_output is null || _stylesheet is null || _writers.Length == 0)
            return;

        for (var s = 0; s < _writers.Length; s++)
        {
            OpenXmlWriter? writer = _writers[s];
            if (writer is null)
                continue;

            writer.WriteEndElement(); // </sheetData>

            var columnCount = _sections[s].Schema.Count;
            if (_options.UseAutoFilter && columnCount > 0 && _hasDataRows[s])
            {
                var reference = $"A1:{XlsxCells.ColumnLetter(columnCount - 1)}{_nextRow[s] - 1}";
                writer.WriteElement(new AutoFilter { Reference = reference });
            }

            writer.WriteEndElement(); // </worksheet>
            writer.Dispose();
            _writers[s] = null;

            FileStream? sheetFile = _sheetFiles[s];
            if (sheetFile is not null)
            {
                // Flush the streamed worksheet XML to disk before it is copied out.
                await sheetFile.DisposeAsync().ConfigureAwait(false);
                _sheetFiles[s] = null;
            }
        }

        try
        {
            var sheets = new XlsxSheetPart[_tempPaths.Length];
            for (var s = 0; s < _tempPaths.Length; s++)
                sheets[s] = new XlsxSheetPart(_sheetNames[s], _tempPaths[s]);

            await XlsxOpcPackage.AssembleAsync(_output, _stylesheet, sheets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var tempPath in _tempPaths)
                XlsxOpcPackage.TryDelete(tempPath);
            _tempPaths = [];
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        for (var s = 0; s < _writers.Length; s++)
        {
            _writers[s]?.Dispose();
            _writers[s] = null;

            FileStream? sheetFile = _sheetFiles[s];
            if (sheetFile is not null)
            {
                await sheetFile.DisposeAsync().ConfigureAwait(false);
                _sheetFiles[s] = null;
            }
        }

        foreach (var tempPath in _tempPaths)
            XlsxOpcPackage.TryDelete(tempPath);
        _tempPaths = [];
    }

    // Excel worksheet names: 1-31 chars, none of : \ / ? * [ ], and unique within the workbook.
    private static string UniqueSheetName(string name, int index, HashSet<string> used)
    {
        var cleaned = new string((name ?? string.Empty)
            .Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
            cleaned = $"Sheet{index + 1}";
        if (cleaned.Length > 31)
            cleaned = cleaned[..31];

        var candidate = cleaned;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            var tag = $"-{suffix++}";
            candidate = cleaned.Length + tag.Length > 31 ? cleaned[..(31 - tag.Length)] + tag : cleaned + tag;
        }

        return candidate;
    }
}
