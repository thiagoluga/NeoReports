namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Fluent, mutable options for the Google Sheets source (ADR D66). Deliberately does not implement
/// <c>NeoReports.Sources.Http.Common.ICommonHttpOptions{TSelf}</c> — that interface's shared
/// <c>Header</c>/<c>Bearer</c> shape is header-based auth, and this source's only auth mechanism is
/// an API key sent as a query parameter (see D66's authentication section).
/// </summary>
public sealed class GoogleSheetsSourceOptions
{
    /// <summary>1-based row number the header lives at. Default <c>1</c>.</summary>
    internal int HeaderRowValue { get; private set; } = 1;

    /// <summary>A1 column letter the read starts at (inclusive).</summary>
    internal string? FirstColumnValue { get; private set; }

    /// <summary>A1 column letter the read ends at (inclusive).</summary>
    internal string? LastColumnValue { get; private set; }

    /// <summary>The Google API key, sent as <c>?key=</c> on every request.</summary>
    internal string? ApiKeyValue { get; private set; }

    /// <summary>Sets the A1 column-letter bounds of the read (both required before the source can be used).</summary>
    public GoogleSheetsSourceOptions Columns(string firstColumn, string lastColumn)
    {
        FirstColumnValue = string.IsNullOrWhiteSpace(firstColumn) ? throw new ArgumentException("First column must be provided.", nameof(firstColumn)) : firstColumn;
        LastColumnValue = string.IsNullOrWhiteSpace(lastColumn) ? throw new ArgumentException("Last column must be provided.", nameof(lastColumn)) : lastColumn;
        return this;
    }

    /// <summary>Sets the 1-based row number the header lives at; defaults to <c>1</c>.</summary>
    public GoogleSheetsSourceOptions HeaderRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        HeaderRowValue = row;
        return this;
    }

    /// <summary>Sets the Google API key (required before the source can be used).</summary>
    public GoogleSheetsSourceOptions ApiKey(string key)
    {
        ApiKeyValue = string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("API key must be provided.", nameof(key)) : key;
        return this;
    }
}
