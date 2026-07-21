using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>Fluent entry point for a Google Sheets source (ADR D66).</summary>
public static class Source
{
    private static readonly ReportSchema PlaceholderSchema = new(Array.Empty<ReportColumn>());

    /// <summary>
    /// Begins configuring a Google Sheets source. <see cref="GoogleSheetsSourceBuilder.Columns"/> must
    /// be called before <see cref="GoogleSheetsSourceBuilder.As{T}"/> — there is no default column
    /// range. Only a spreadsheet shared as "anyone with the link can view" is readable (API-key auth
    /// only; a private sheet needs OAuth2/service-account auth, deferred alongside P4b — see D66).
    /// </summary>
    /// <param name="spreadsheetId">The Google Sheets spreadsheet id.</param>
    /// <param name="sheet">The sheet (tab) name within the spreadsheet.</param>
    /// <param name="apiKey">The Google API key.</param>
    /// <param name="client">An explicit <see cref="HttpClient"/> (caller owns its lifetime), or <c>null</c> to use a lazily-created shared instance.</param>
    public static GoogleSheetsSourceBuilder GoogleSheets(string spreadsheetId, string sheet, string apiKey, HttpClient? client = null) =>
        new GoogleSheetsSourceBuilder(spreadsheetId, sheet, client).ApiKey(apiKey);

    internal static ReportSchema Placeholder => PlaceholderSchema;
}

/// <summary>Intermediate builder for a Google Sheets source, before the row type is chosen.</summary>
public sealed class GoogleSheetsSourceBuilder
{
    private readonly string _spreadsheetId;
    private readonly string _sheet;
    private readonly HttpClient? _client;
    private readonly GoogleSheetsSourceOptions _options = new();

    internal GoogleSheetsSourceBuilder(string spreadsheetId, string sheet, HttpClient? client)
    {
        _spreadsheetId = string.IsNullOrWhiteSpace(spreadsheetId) ? throw new ArgumentException("Spreadsheet id must be provided.", nameof(spreadsheetId)) : spreadsheetId;
        _sheet = string.IsNullOrWhiteSpace(sheet) ? throw new ArgumentException("Sheet name must be provided.", nameof(sheet)) : sheet;
        _client = client;
    }

    /// <summary>Sets the A1 column-letter bounds of the read (required).</summary>
    public GoogleSheetsSourceBuilder Columns(string firstColumn, string lastColumn)
    {
        _options.Columns(firstColumn, lastColumn);
        return this;
    }

    /// <summary>Sets the 1-based row number the header lives at; defaults to <c>1</c>.</summary>
    public GoogleSheetsSourceBuilder HeaderRow(int row)
    {
        _options.HeaderRow(row);
        return this;
    }

    /// <summary>Sets the Google API key.</summary>
    public GoogleSheetsSourceBuilder ApiKey(string key)
    {
        _options.ApiKey(key);
        return this;
    }

    /// <summary>
    /// Completes the source, materializing each row as <typeparamref name="T"/> by matching its
    /// longest constructor's parameter names against the sheet's header row (case-insensitive) —
    /// the same shape <c>Source.CsvFile(...).As&lt;T&gt;()</c> uses.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    public IBatchSource<T> As<T>()
    {
        HttpClient client = _client ?? HttpClients.Default;
        var materializer = new GoogleSheetsRecordMaterializer<T>();
        return new GoogleSheetsBatchSource<T>(client, _spreadsheetId, _sheet, _options, Source.Placeholder, materializer.Materialize);
    }
}
