namespace NeoReports.Xlsx.Pro;

/// <summary>Options for the multi-sheet XLSX workbook writer.</summary>
public sealed class XlsxWorkbookOptions
{
    /// <summary>Whether each worksheet starts with a bold header row. Default <c>true</c>.</summary>
    public bool WriteHeader { get; private set; } = true;

    /// <summary>Whether each worksheet gets an auto-filter over its data. Default <c>false</c>.</summary>
    public bool UseAutoFilter { get; private set; }

    /// <summary>Enables or disables the header row on every worksheet.</summary>
    /// <param name="write">Whether to write the header.</param>
    public XlsxWorkbookOptions Header(bool write = true)
    {
        WriteHeader = write;
        return this;
    }

    /// <summary>Enables the auto-filter on every worksheet.</summary>
    /// <param name="enable">Whether to enable the auto-filter.</param>
    public XlsxWorkbookOptions AutoFilter(bool enable = true)
    {
        UseAutoFilter = enable;
        return this;
    }
}
