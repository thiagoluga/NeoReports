using System.Text;

namespace NeoReports.Formats.Csv;

/// <summary>
/// Fluent options for the CSV writer, matching the spec API
/// (<c>o.Delimiter(';').Encoding(Encoding.UTF8)</c>). Defaults: comma delimiter, UTF-8 (no BOM),
/// a header row from each column's <c>DisplayName</c>, and CRLF line endings (RFC 4180).
/// </summary>
public sealed class CsvOptions
{
    /// <summary>Field delimiter (read by the writer). Default <c>,</c>.</summary>
    internal char DelimiterChar { get; private set; } = ',';

    /// <summary>Output encoding (read by the writer). Default UTF-8 without BOM.</summary>
    internal Encoding OutputEncoding { get; private set; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Whether to write a header row (read by the writer). Default <c>true</c>.</summary>
    internal bool WriteHeader { get; private set; } = true;

    /// <summary>Sets the field delimiter.</summary>
    /// <param name="delimiter">The delimiter character.</param>
    public CsvOptions Delimiter(char delimiter)
    {
        DelimiterChar = delimiter;
        return this;
    }

    /// <summary>Sets the output encoding.</summary>
    /// <param name="encoding">The encoding to use.</param>
    public CsvOptions Encoding(Encoding encoding)
    {
        OutputEncoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        return this;
    }

    /// <summary>Enables or disables the header row.</summary>
    /// <param name="hasHeader">Whether to write a header.</param>
    public CsvOptions Header(bool hasHeader = true)
    {
        WriteHeader = hasHeader;
        return this;
    }
}
