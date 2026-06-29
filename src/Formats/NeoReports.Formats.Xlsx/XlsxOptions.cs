namespace NeoReports.Formats.Xlsx;

/// <summary>
/// Fluent options for the XLSX writer (<c>o.SheetName("Sales").AutoFilter()</c>).
/// Defaults: sheet "Sheet1", a header row from each column's <c>DisplayName</c>, auto-filter off.
/// </summary>
public sealed class XlsxOptions
{
    /// <summary>Worksheet name (read by the writer). Default "Sheet1".</summary>
    internal string Sheet { get; private set; } = "Sheet1";

    /// <summary>Whether to write a header row (read by the writer). Default <c>true</c>.</summary>
    internal bool WriteHeader { get; private set; } = true;

    /// <summary>Whether to enable the Excel auto-filter on the header row. Default <c>false</c>.</summary>
    internal bool UseAutoFilter { get; private set; }

    /// <summary>Sets the worksheet name.</summary>
    /// <param name="name">The sheet name.</param>
    public XlsxOptions SheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sheet name must be provided.", nameof(name));
        Sheet = name;
        return this;
    }

    /// <summary>Enables or disables the header row.</summary>
    /// <param name="hasHeader">Whether to write a header.</param>
    public XlsxOptions Header(bool hasHeader = true)
    {
        WriteHeader = hasHeader;
        return this;
    }

    /// <summary>Enables the Excel auto-filter on the header row.</summary>
    /// <param name="enabled">Whether to enable the auto-filter.</param>
    public XlsxOptions AutoFilter(bool enabled = true)
    {
        UseAutoFilter = enabled;
        return this;
    }
}
