using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Airtable;

/// <summary>
/// <see cref="IBatchSource{T}"/> over an Airtable table (ADR D65) — one page per
/// <see cref="ReadBatchAsync"/>, encoding the response's <c>offset</c> into the opaque cursor
/// (<see cref="AirtablePagination"/>), the same cursor-per-page shape the rest of the HTTP family
/// uses so a transient failure retries in isolation by refetching the same page from its token.
/// Every response is a bounded page (<c>pageSize</c> records), so it is safely materialized whole
/// with <see cref="JsonDocument.ParseAsync"/> — constant memory across pages.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class AirtableBatchSource<T> : IBatchSource<T>
{
    private readonly HttpClient _client;
    private readonly string _tableUrl;
    private readonly AirtableSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="baseId">The Airtable base id (e.g. <c>"appXXXXXXXXXXXXXX"</c>).</param>
    /// <param name="tableIdOrName">The table id or name within the base.</param>
    /// <param name="options">Auth options.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single record's <c>fields</c> envelope.</param>
    public AirtableBatchSource(HttpClient client, string baseId, string tableIdOrName, AirtableSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(baseId))
            throw new ArgumentException("Base id must be provided.", nameof(baseId));
        if (string.IsNullOrWhiteSpace(tableIdOrName))
            throw new ArgumentException("Table id/name must be provided.", nameof(tableIdOrName));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _tableUrl = AirtableUrls.Table(_options.BaseUrlValue, baseId, tableIdOrName);
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        AirtableCursorState state = AirtablePagination.Decode(context.Cursor);
        Uri requestUri = BuildRequestUri(state, context.PageSize);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        HttpRequests.ApplyAuth(request, _options.ToAuth());

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await HttpRequests.BuildExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

            JsonElement root = document.RootElement;
            JsonElement recordsArray = JsonRecords.GetArray(root, "records");
            var records = new List<T>(context.PageSize);
            foreach (JsonElement item in recordsArray.EnumerateArray())
            {
                if (!JsonRecords.TryGetField(item, "fields", out JsonElement envelope))
                    throw new HttpSourceException(null, null, "A record in the Airtable response is missing 'fields'.");

                records.Add(_materialize(envelope));
            }

            string? offset = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("offset", out JsonElement offsetValue)
                && offsetValue.ValueKind == JsonValueKind.String
                && offsetValue.GetString() is { Length: > 0 } text
                    ? text
                    : null;

            bool hasMore = offset is not null;
            string? cursor = hasMore ? AirtablePagination.Encode(new AirtableCursorState(offset)) : null;
            return new BatchResult<T>(records, cursor, hasMore);
        }
    }

    private Uri BuildRequestUri(AirtableCursorState state, int pageSize)
    {
        var queryParams = new List<(string Key, string Value)> { ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)) };

        if (state.Offset is { Length: > 0 } offset)
            queryParams.Add(("offset", offset));

        return QueryStrings.AddQuery(_tableUrl, queryParams.ToArray());
    }
}
