using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// <see cref="IBatchSource{T}"/> over a Google Sheets range (ADR D66) — one page per
/// <see cref="ReadBatchAsync"/>, a fixed-size row window advanced by the opaque cursor
/// (<see cref="GoogleSheetsPagination"/>). Chosen over the file family's <c>IStreamingSource{T}</c>
/// specifically because each page is one bounded HTTP request, retried in isolation from its own
/// cursor on a transient failure (D6/D11) — the same reasoning every HTTP-family source in Epic P
/// uses. The Sheets API has no cursor/next-page mechanism at all; this source synthesizes paging by
/// advancing a row offset by a fixed page size and stopping only when a window's response has no
/// data rows whatsoever (see D66's documented gap for a large blank-run edge case).
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class GoogleSheetsBatchSource<T> : IBatchSource<T>
{
    private readonly HttpClient _client;
    private readonly string _spreadsheetId;
    private readonly string _sheet;
    private readonly GoogleSheetsSourceOptions _options;
    private readonly Func<IReadOnlyDictionary<string, int>, JsonElement[], T> _materialize;

    // Cached after the first successful page — the header row never changes for a given
    // spreadsheet/sheet/column-range within one run, so re-fetching it on every page (as the first
    // draft did) was a wasted Sheets API range-fetch per page (code review finding); caching it does
    // not reintroduce any cross-page state a retry depends on, since a retry of a later page's data
    // window only ever needs a header this instance has *already* successfully fetched once.
    private IReadOnlyDictionary<string, int>? _cachedHeaderIndex;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="spreadsheetId">The Google Sheets spreadsheet id.</param>
    /// <param name="sheet">The sheet (tab) name within the spreadsheet.</param>
    /// <param name="options">Column bounds/header row/API key options — <see cref="GoogleSheetsSourceOptions.LastColumnValue"/> and <see cref="GoogleSheetsSourceOptions.ApiKeyValue"/> are required.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from the header ordinal map and a single row's raw cells.</param>
    public GoogleSheetsBatchSource(HttpClient client, string spreadsheetId, string sheet, GoogleSheetsSourceOptions options, ReportSchema schema, Func<IReadOnlyDictionary<string, int>, JsonElement[], T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(spreadsheetId))
            throw new ArgumentException("Spreadsheet id must be provided.", nameof(spreadsheetId));
        if (string.IsNullOrWhiteSpace(sheet))
            throw new ArgumentException("Sheet name must be provided.", nameof(sheet));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.FirstColumnValue) || string.IsNullOrWhiteSpace(_options.LastColumnValue))
            throw new ArgumentException("Columns(firstColumn, lastColumn) must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ApiKeyValue))
            throw new ArgumentException("An API key must be configured.", nameof(options));

        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _spreadsheetId = spreadsheetId;
        _sheet = sheet;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        GoogleSheetsCursorState state = GoogleSheetsPagination.Decode(context.Cursor);
        bool needsHeader = _cachedHeaderIndex is null;

        string dataRange = GoogleSheetsRanges.DataWindow(_sheet, _options.HeaderRowValue + 1, state.Offset, context.PageSize, _options.FirstColumnValue!, _options.LastColumnValue!);
        Uri requestUri = needsHeader
            ? GoogleSheetsUrls.BatchGet(_spreadsheetId, _options.ApiKeyValue!, GoogleSheetsRanges.Header(_sheet, _options.HeaderRowValue, _options.FirstColumnValue!, _options.LastColumnValue!), dataRange)
            : GoogleSheetsUrls.BatchGet(_spreadsheetId, _options.ApiKeyValue!, dataRange);

        // The API key travels in the URL's query string (GoogleSheetsUrls.BatchGet), not a header —
        // this source's auth model, unlike the rest of the HTTP family — so no HttpAuth is applied.
        using JsonDocument document = await HttpRequests.GetJsonAsync(_client, requestUri, new HttpAuth(), cancellationToken).ConfigureAwait(false);

        JsonElement valueRanges = JsonRecords.GetArray(document.RootElement, "valueRanges");
        int expectedRanges = needsHeader ? 2 : 1;
        if (valueRanges.GetArrayLength() < expectedRanges)
        {
            throw new HttpSourceException(null, null,
                "The Google Sheets response did not include the expected number of ranges requested via batchGet.");
        }

        if (needsHeader)
            _cachedHeaderIndex = BuildHeaderIndex(valueRanges[0]);

        JsonElement dataValueRange = needsHeader ? valueRanges[1] : valueRanges[0];
        IReadOnlyDictionary<string, int> ordinalByName = _cachedHeaderIndex!;

        var records = new List<T>(context.PageSize);
        if (JsonRecords.TryGetField(dataValueRange, "values", out JsonElement dataRows) && dataRows.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in dataRows.EnumerateArray())
                records.Add(_materialize(ordinalByName, row.EnumerateArray().ToArray()));
        }

        // Advances by the fixed page size regardless of how many rows this window actually returned
        // (blank trailing rows within a window are omitted by the API, so a short-but-non-empty
        // window does not mean "no more data") — only a window with zero rows means exhausted. See
        // D66's documented gap: a page-size-or-larger fully blank run in the middle of a sparse sheet
        // is indistinguishable from genuine exhaustion and ends pagination early.
        bool hasMore = records.Count > 0;
        string? cursor = hasMore ? GoogleSheetsPagination.Encode(new GoogleSheetsCursorState(state.Offset + context.PageSize)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }

    private static Dictionary<string, int> BuildHeaderIndex(JsonElement headerValueRange)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!JsonRecords.TryGetField(headerValueRange, "values", out JsonElement headerRows) || headerRows.ValueKind != JsonValueKind.Array || headerRows.GetArrayLength() == 0)
            return index;

        var i = 0;
        foreach (JsonElement cell in headerRows[0].EnumerateArray())
        {
            // Decode the header cell the same way the data path does. Requests ask for
            // UNFORMATTED_VALUE, so a numeric-looking header (a year column such as 2024, or a
            // TRUE/FALSE one) arrives as a JSON number/bool rather than a string — accepting only
            // strings here left those columns unindexed, so every row bound the type default and the
            // whole column silently read as empty.
            if (GoogleSheetsCellText.TextOrNull(cell) is { Length: > 0 } name)
                index[name] = i;
            i++;
        }

        return index;
    }
}
