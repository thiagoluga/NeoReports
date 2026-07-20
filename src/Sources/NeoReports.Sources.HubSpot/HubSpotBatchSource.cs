using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.HubSpot;

/// <summary>
/// <see cref="IBatchSource{T}"/> over a HubSpot CRM object collection (ADR D65) — one page per
/// <see cref="ReadBatchAsync"/>, encoding <c>paging.next.after</c> into the opaque cursor
/// (<see cref="HubSpotPagination"/>), the same cursor-per-page shape the rest of the HTTP family
/// uses so a transient failure retries in isolation by refetching the same page from its token.
/// Every response is a bounded page (<c>limit</c> results), so it is safely materialized whole with
/// <see cref="JsonDocument.ParseAsync"/> — constant memory across pages.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class HubSpotBatchSource<T> : IBatchSource<T>
{
    private readonly HttpClient _client;
    private readonly string _collectionUrl;
    private readonly HubSpotSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;
    private readonly string? _propertiesQueryValue;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="objectType">The CRM object type (e.g. <c>"contacts"</c>, <c>"companies"</c>, <c>"deals"</c>).</param>
    /// <param name="options">Properties/auth options.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single result's <c>properties</c> envelope.</param>
    public HubSpotBatchSource(HttpClient client, string objectType, HubSpotSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(objectType))
            throw new ArgumentException("Object type must be provided.", nameof(objectType));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _collectionUrl = HubSpotUrls.ObjectCollection(_options.BaseUrlValue, objectType);
        // Precomputed once — RequestedProperties never changes after construction, so joining it on
        // every page in BuildRequestUri would be wasted work on the hot path (code-review finding).
        _propertiesQueryValue = _options.RequestedProperties is { Count: > 0 } properties ? string.Join(",", properties) : null;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        HubSpotCursorState state = HubSpotPagination.Decode(context.Cursor);
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
            JsonElement results = JsonRecords.GetArray(root, "results");
            var records = new List<T>(context.PageSize);
            foreach (JsonElement item in results.EnumerateArray())
            {
                if (!JsonRecords.TryGetField(item, "properties", out JsonElement envelope))
                    throw new HttpSourceException(null, null, "A result in the HubSpot response is missing 'properties'.");

                records.Add(_materialize(envelope));
            }

            string? after = JsonRecords.TryGetField(root, "paging.next.after", out JsonElement afterValue)
                && afterValue.ValueKind == JsonValueKind.String
                && afterValue.GetString() is { Length: > 0 } text
                    ? text
                    : null;

            bool hasMore = after is not null;
            string? cursor = hasMore ? HubSpotPagination.Encode(new HubSpotCursorState(after)) : null;
            return new BatchResult<T>(records, cursor, hasMore);
        }
    }

    private Uri BuildRequestUri(HubSpotCursorState state, int pageSize)
    {
        var queryParams = new List<(string Key, string Value)> { ("limit", pageSize.ToString(CultureInfo.InvariantCulture)) };

        if (state.After is { Length: > 0 } after)
            queryParams.Add(("after", after));

        if (_propertiesQueryValue is not null)
            queryParams.Add(("properties", _propertiesQueryValue));

        return QueryStrings.AddQuery(_collectionUrl, queryParams.ToArray());
    }
}
