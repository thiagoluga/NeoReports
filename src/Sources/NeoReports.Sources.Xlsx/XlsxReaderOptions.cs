namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Fluent options for the XLSX source (ADR D59). Defaults: the workbook's first sheet, a header row
/// (used both to derive column names and — for the typed path — to match constructor parameters by
/// name).
/// </summary>
public sealed class XlsxReaderOptions
{
    /// <summary>The worksheet to read. <c>null</c> means the workbook's first sheet.</summary>
    internal string? SheetNameValue { get; private set; }

    /// <summary>Whether the first row is a header naming the columns. Default <c>true</c>.</summary>
    internal bool HasHeaderRow { get; private set; } = true;

    /// <summary>Selects a worksheet by name instead of the workbook's first sheet.</summary>
    /// <param name="sheetName">The worksheet name.</param>
    public XlsxReaderOptions SheetName(string sheetName)
    {
        SheetNameValue = sheetName ?? throw new ArgumentNullException(nameof(sheetName));
        return this;
    }

    /// <summary>
    /// Sets whether the first row is a header. Disabling this is only valid for the dynamic
    /// (config-driven) path, which can fall back to positional columns — the typed
    /// <c>.As&lt;T&gt;()</c> path always requires a header to match column names against
    /// <c>T</c>'s constructor parameters.
    /// </summary>
    /// <param name="hasHeader">Whether the first row is a header.</param>
    public XlsxReaderOptions Header(bool hasHeader = true)
    {
        HasHeaderRow = hasHeader;
        return this;
    }
}
