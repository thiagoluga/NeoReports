using System.Text;

namespace NeoReports.Sources.Csv;

/// <summary>
/// Fluent options for the CSV source (ADR D58), mirroring <c>NeoReports.Formats.Csv.CsvOptions</c>'
/// shape for the read direction. Defaults: comma delimiter, UTF-8, a header row (used both to derive
/// column names and — for the typed path — to match constructor parameters by name).
/// </summary>
public sealed class CsvReaderOptions
{
    /// <summary>Field delimiter. Default <c>,</c>.</summary>
    internal char DelimiterChar { get; private set; } = ',';

    /// <summary>Input encoding. Default UTF-8.</summary>
    internal Encoding InputEncoding { get; private set; } = System.Text.Encoding.UTF8;

    /// <summary>Whether the first row is a header naming the columns. Default <c>true</c>.</summary>
    internal bool HasHeaderRow { get; private set; } = true;

    /// <summary>Sets the field delimiter.</summary>
    /// <param name="delimiter">The delimiter character.</param>
    public CsvReaderOptions Delimiter(char delimiter)
    {
        DelimiterChar = delimiter;
        return this;
    }

    /// <summary>Sets the input encoding.</summary>
    /// <param name="encoding">The encoding to use.</param>
    public CsvReaderOptions Encoding(Encoding encoding)
    {
        InputEncoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        return this;
    }

    /// <summary>
    /// Sets whether the first row is a header. Disabling this is only valid for the dynamic
    /// (config-driven) path, which can fall back to positional columns — the typed
    /// <c>.As&lt;T&gt;()</c> path always requires a header to match column names against
    /// <c>T</c>'s constructor parameters.
    /// </summary>
    /// <param name="hasHeader">Whether the first row is a header.</param>
    public CsvReaderOptions Header(bool hasHeader = true)
    {
        HasHeaderRow = hasHeader;
        return this;
    }
}
