using ClosedXML.Excel;
using NeoReports.Abstractions;
using NeoReports.Core.Sections;
using NeoReports.Formats.Xlsx;

namespace NeoReports.Xlsx.Pro;

/// <summary>
/// Multi-sheet XLSX workbook writer (Pro): one worksheet per section, all in a single <c>.xlsx</c>
/// file. Reuses the exact cell semantics of the MIT XLSX writer (<see cref="XlsxCells"/>) so native
/// types and formats match. Like the single-sheet writer, ClosedXML builds the whole workbook in
/// memory before saving (ADR D14).
/// </summary>
public sealed class XlsxWorkbookWriter : IReportSectionedWriter
{
    private readonly XlsxWorkbookOptions _options;
    private XLWorkbook? _workbook;
    private Stream? _output;
    private IReadOnlyList<ReportSection> _sections = [];
    private IXLWorksheet[] _sheets = [];
    private int[] _nextRow = [];

    /// <summary>Creates the workbook writer with the given options.</summary>
    /// <param name="options">Workbook formatting options.</param>
    public XlsxWorkbookWriter(XlsxWorkbookOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

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
        _workbook = new XLWorkbook();
        _sheets = new IXLWorksheet[_sections.Count];
        _nextRow = new int[_sections.Count];

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < _sections.Count; s++)
        {
            ReportSection section = _sections[s];
            IXLWorksheet sheet = _workbook.AddWorksheet(UniqueSheetName(section.Name, s, usedNames));
            _sheets[s] = sheet;
            _nextRow[s] = 1;

            if (_options.WriteHeader)
            {
                ReportSchema schema = section.Schema;
                for (var i = 0; i < schema.Count; i++)
                    sheet.Cell(1, i + 1).Value = schema.Columns[i].DisplayName ?? schema.Columns[i].Name;
                sheet.Row(1).Style.Font.Bold = true;
                _nextRow[s] = 2;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteSectionRowsAsync(int sectionIndex, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_workbook is null)
            throw new InvalidOperationException("InitializeAsync must be called before WriteSectionRowsAsync.");

        IXLWorksheet sheet = _sheets[sectionIndex];
        ReportSchema schema = _sections[sectionIndex].Schema;
        var nextRow = _nextRow[sectionIndex];

        foreach (var values in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < schema.Count; i++)
            {
                var value = i < values.Length ? values[i] : null;
                XlsxCells.SetCell(sheet.Cell(nextRow, i + 1), value, schema.Columns[i]);
            }

            nextRow++;
        }

        _nextRow[sectionIndex] = nextRow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_workbook is null || _output is null)
            return;

        for (var s = 0; s < _sheets.Length; s++)
        {
            IXLWorksheet sheet = _sheets[s];
            var columnCount = _sections[s].Schema.Count;
            if (_options.UseAutoFilter && columnCount > 0 && _nextRow[s] > 1)
                sheet.Range(1, 1, _nextRow[s] - 1, columnCount).SetAutoFilter();
            sheet.Columns().AdjustToContents();
        }

        await Task.Run(() => _workbook!.SaveAs(_output!), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _workbook?.Dispose();
        _workbook = null;
        return ValueTask.CompletedTask;
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
