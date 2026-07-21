using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// <see cref="IBatchSource{T}"/> over a paginated REST endpoint (ADR D61) — one HTTP page per
/// <see cref="ReadBatchAsync"/>, encoding the next page's locator into the opaque cursor
/// (<see cref="HttpPagination"/>), the same cursor-per-page shape <c>AdoKeysetSource</c> uses for
/// SQL keyset pagination. Chosen over the file family's <c>IStreamingSource{T}</c> shape
/// specifically so a transient failure retries in isolation by refetching the same page from its
/// token — idempotent, unlike resuming a partially-consumed enumerator. Not used for
/// <see cref="HttpPaginationStrategy.None"/> — see <see cref="HttpStreamingSource{T}"/> instead.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class HttpBatchSource<T> : IBatchSource<T>
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly HttpSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;
    private readonly OAuth2ClientCredentialsProvider? _oauth2Provider;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="baseUrl">The endpoint's base URL.</param>
    /// <param name="options">Pagination/auth/mapping options.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single record element.</param>
    public HttpBatchSource(HttpClient client, string baseUrl, HttpSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? throw new ArgumentException("Base URL must be provided.", nameof(baseUrl)) : baseUrl;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _oauth2Provider = HttpOAuth2.CreateProvider(_client, _options);

        if (options.PaginationStrategy == HttpPaginationStrategy.None)
        {
            throw new ArgumentException(
                "HttpBatchSource does not support the 'None' pagination strategy; use HttpStreamingSource instead.",
                nameof(options));
        }
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpCursorState state = HttpPagination.Decode(context.Cursor);
        Uri requestUri = BuildRequestUri(state, context.PageSize);

        // Every strategy but LinkHeader builds its URI from the configured base URL, so origin can
        // never drift; LinkHeader's next-page URL instead comes verbatim from the *response's* Link
        // header (RFC 5988/8288 — legitimately points elsewhere for e.g. a signed CDN URL, but the
        // configured API key/bearer token/headers are for the configured host, not whatever a
        // response says to fetch next). Refuse to replay those credentials cross-origin — same
        // "don't forward Authorization across a different authority" rule HttpClient itself applies
        // to redirects — rather than silently leaking them to a compromised or malicious endpoint's
        // arbitrary next-page URL.
        if (!HttpOrigin.IsSameOrigin(requestUri, new Uri(_baseUrl, UriKind.Absolute)))
        {
            throw new HttpSourceException(null, null,
                $"The next-page URL from the response's 'Link' header ('{requestUri}') has a different " +
                "scheme/host/port than the configured base URL; refusing to send the configured credentials there.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        HttpAuth auth = await HttpOAuth2.ResolveAuthAsync(_options, _oauth2Provider, cancellationToken).ConfigureAwait(false);
        HttpRequests.ApplyAuth(request, auth);

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await HttpRequests.BuildExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

            JsonElement array = JsonRecords.GetArray(document.RootElement, _options.RecordsPath);
            var records = new List<T>(context.PageSize);
            foreach (JsonElement element in array.EnumerateArray())
                records.Add(_materialize(element));

            return _options.PaginationStrategy switch
            {
                HttpPaginationStrategy.LinkHeader => BuildLinkHeaderResult(records, response),
                HttpPaginationStrategy.Cursor => BuildCursorResult(records, document.RootElement),
                HttpPaginationStrategy.Page => BuildPageResult(records, state, context.PageSize),
                HttpPaginationStrategy.Offset => BuildOffsetResult(records, state, context.PageSize),
                _ => throw new InvalidOperationException($"Unsupported pagination strategy '{_options.PaginationStrategy}'."),
            };
        }
    }

    private Uri BuildRequestUri(HttpCursorState state, int pageSize)
    {
        switch (_options.PaginationStrategy)
        {
            case HttpPaginationStrategy.LinkHeader:
                return state.NextUrl is not null ? new Uri(state.NextUrl) : new Uri(_baseUrl);

            case HttpPaginationStrategy.Cursor:
                return state.Token is null
                    ? new Uri(_baseUrl)
                    : QueryStrings.AddQuery(_baseUrl, (_options.CursorRequestParam, state.Token));

            case HttpPaginationStrategy.Page:
                {
                    int page = state.Page ?? _options.StartPage;
                    return QueryStrings.AddQuery(
                        _baseUrl,
                        (_options.PageParam, page.ToString(CultureInfo.InvariantCulture)),
                        (_options.PageSizeParam, pageSize.ToString(CultureInfo.InvariantCulture)));
                }

            case HttpPaginationStrategy.Offset:
                {
                    int offset = state.Offset ?? 0;
                    return QueryStrings.AddQuery(
                        _baseUrl,
                        (_options.OffsetParam, offset.ToString(CultureInfo.InvariantCulture)),
                        (_options.PageSizeParam, pageSize.ToString(CultureInfo.InvariantCulture)));
                }

            default:
                throw new InvalidOperationException($"Unsupported pagination strategy '{_options.PaginationStrategy}'.");
        }
    }

    private static BatchResult<T> BuildLinkHeaderResult(List<T> records, HttpResponseMessage response)
    {
        string? nextUrl = ParseLinkHeaderNext(response);
        bool hasMore = nextUrl is not null;
        string? cursor = hasMore ? HttpPagination.Encode(new HttpCursorState(NextUrl: nextUrl)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }

    private static string? ParseLinkHeaderNext(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
            return null;

        foreach (string headerValue in values)
        {
            foreach (string link in headerValue.Split(','))
            {
                string[] parts = link.Split(';');
                if (parts.Length < 2)
                    continue;

                string urlPart = parts[0].Trim();
                if (urlPart.Length < 2 || urlPart[0] != '<' || urlPart[^1] != '>')
                    continue;

                bool isNext = parts.Skip(1).Any(p =>
                {
                    string[] kv = p.Trim().Split('=', 2);
                    return kv.Length == 2
                        && kv[0].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase)
                        && kv[1].Trim().Trim('"').Equals("next", StringComparison.OrdinalIgnoreCase);
                });

                if (isNext)
                    return urlPart[1..^1];
            }
        }

        return null;
    }

    private BatchResult<T> BuildCursorResult(List<T> records, JsonElement responseRoot)
    {
        string? token = null;
        if (JsonRecords.TryGetField(responseRoot, _options.CursorResponsePath, out JsonElement value))
        {
            // Continuation tokens are commonly numeric (e.g. a last-row id) as well as opaque
            // strings — only treating JsonValueKind.String as a token silently stopped pagination
            // after the first page against a numeric-cursor API.
            token = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        bool hasMore = token is not null;
        string? cursor = hasMore ? HttpPagination.Encode(new HttpCursorState(Token: token)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }

    private BatchResult<T> BuildPageResult(List<T> records, HttpCursorState state, int pageSize)
    {
        int currentPage = state.Page ?? _options.StartPage;
        bool hasMore = records.Count == pageSize;
        string? cursor = hasMore ? HttpPagination.Encode(new HttpCursorState(Page: currentPage + 1)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }

    private BatchResult<T> BuildOffsetResult(List<T> records, HttpCursorState state, int pageSize)
    {
        int currentOffset = state.Offset ?? 0;
        bool hasMore = records.Count == pageSize;
        string? cursor = hasMore ? HttpPagination.Encode(new HttpCursorState(Offset: currentOffset + records.Count)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }
}
