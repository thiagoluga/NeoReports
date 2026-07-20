using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>
/// <see cref="IBatchSource{T}"/> over an OData v4 collection (ADR D62) — one page per
/// <see cref="ReadBatchAsync"/>, encoding the next page's locator into the opaque cursor
/// (<see cref="ODataPagination"/>), the same cursor-per-page shape the HTTP family (P4a) and
/// <c>AdoKeysetSource</c> use so a transient failure retries in isolation by refetching the same
/// page from its token. Unlike the HTTP family, there is no <c>None</c>-equivalent single-response
/// streaming case: an OData collection response is always a bounded page (the service enforces its
/// own max page size and emits <c>@odata.nextLink</c> when it truncates), so every response is
/// safely materialized with <see cref="JsonDocument.ParseAsync"/> — constant memory across pages.
/// Also implements <see cref="ISourceRowCounter"/> by delegating to an internal
/// <see cref="ODataRowCounter"/> — that interface is never DI-registered in this codebase; callers
/// instead detect it by pattern-matching the resolved source instance, the same shape
/// <c>AdoKeysetSource{T}</c> uses.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class ODataBatchSource<T> : IBatchSource<T>, ISourceRowCounter
{
    private readonly HttpClient _client;
    private readonly string _resourceUrl;
    private readonly ODataSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;
    private readonly ODataRowCounter _rowCounter;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="resourceUrl">The OData resource's (collection's) URL.</param>
    /// <param name="options">Pagination/auth/query options.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single record element.</param>
    public ODataBatchSource(HttpClient client, string resourceUrl, ODataSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resourceUrl = string.IsNullOrWhiteSpace(resourceUrl)
            ? throw new ArgumentException("Resource URL must be provided.", nameof(resourceUrl))
            : resourceUrl;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _rowCounter = new ODataRowCounter(client, _resourceUrl, _options);
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ODataCursorState state = ODataPagination.Decode(context.Cursor);
        bool usingServerNextLink = _options.PaginationStrategy == ODataPaginationStrategy.NextLink && state.NextUrl is not null;
        Uri requestUri = BuildRequestUri(state, context.PageSize);

        // @odata.nextLink comes verbatim from the *response body* — OData v4's own continuation
        // mechanism, legitimately pointing anywhere the service likes (e.g. an opaque $skiptoken
        // URL), but the configured API key/bearer token/headers are for the configured resource
        // host, not whatever the response says to fetch next. Same "don't forward credentials
        // across a different authority" posture HttpBatchSource's LinkHeader strategy takes
        // (ADR D61/D62) — refuse rather than silently leaking them to a compromised or malicious
        // endpoint's arbitrary next-page URL.
        if (usingServerNextLink && !HttpOrigin.IsSameOrigin(requestUri, new Uri(_resourceUrl, UriKind.Absolute)))
        {
            throw new HttpSourceException(null, null,
                $"The next-page URL from the response's '@odata.nextLink' ('{requestUri}') has a different " +
                "scheme/host/port than the configured resource URL; refusing to send the configured credentials there.");
        }

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
            JsonElement array = JsonRecords.GetArray(root, _options.RecordsPath);
            var records = new List<T>(context.PageSize);
            foreach (JsonElement element in array.EnumerateArray())
                records.Add(_materialize(element));

            return _options.PaginationStrategy switch
            {
                ODataPaginationStrategy.NextLink => BuildNextLinkResult(records, root),
                ODataPaginationStrategy.Skip => BuildSkipResult(records, state, context.PageSize),
                _ => throw new InvalidOperationException($"Unsupported pagination strategy '{_options.PaginationStrategy}'."),
            };
        }
    }

    /// <summary>
    /// Counts the rows a full run would read (ADR D47/D62) by delegating to <see cref="ODataRowCounter"/>.
    /// </summary>
    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _rowCounter.CountAsync(execution, cancellationToken);

    private Uri BuildRequestUri(ODataCursorState state, int pageSize)
    {
        if (_options.PaginationStrategy == ODataPaginationStrategy.NextLink && state.NextUrl is not null)
            return new Uri(state.NextUrl);

        var queryParams = new List<(string Key, string Value)>();
        if (_options.StaticFilter is { Length: > 0 } filter)
            queryParams.Add(("$filter", filter));
        if (_options.StaticSelect is { Length: > 0 } select)
            queryParams.Add(("$select", select));
        if (_options.StaticOrderBy is { Length: > 0 } orderBy)
            queryParams.Add(("$orderby", orderBy));

        if (_options.PaginationStrategy == ODataPaginationStrategy.Skip)
        {
            int skip = state.Skip ?? 0;
            int top = _options.TopValue ?? pageSize;
            queryParams.Add(("$skip", skip.ToString(CultureInfo.InvariantCulture)));
            queryParams.Add(("$top", top.ToString(CultureInfo.InvariantCulture)));
        }

        return queryParams.Count == 0 ? new Uri(_resourceUrl) : QueryStrings.AddQuery(_resourceUrl, queryParams.ToArray());
    }

    private static BatchResult<T> BuildNextLinkResult(List<T> records, JsonElement responseRoot)
    {
        // Read the property directly, not via JsonRecords.TryGetField's dotted-path traversal —
        // "@odata.nextLink" is one flat property name containing a literal '.', not a nested
        // "@odata" object with a "nextLink" field.
        string? nextUrl = responseRoot.ValueKind == JsonValueKind.Object
            && responseRoot.TryGetProperty("@odata.nextLink", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
                ? text
                : null;

        bool hasMore = nextUrl is not null;
        string? cursor = hasMore ? ODataPagination.Encode(new ODataCursorState(NextUrl: nextUrl)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }

    private BatchResult<T> BuildSkipResult(List<T> records, ODataCursorState state, int pageSize)
    {
        int currentSkip = state.Skip ?? 0;
        int top = _options.TopValue ?? pageSize;
        bool hasMore = records.Count == top;
        string? cursor = hasMore ? ODataPagination.Encode(new ODataCursorState(Skip: currentSkip + records.Count)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }
}
