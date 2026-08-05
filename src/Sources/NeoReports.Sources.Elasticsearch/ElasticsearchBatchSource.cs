using System.Net.Http.Headers;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// <see cref="IBatchSource{T}"/> over an Elasticsearch/OpenSearch index (ADR D64) — one page per
/// <see cref="ReadBatchAsync"/>, encoding the next page's <c>search_after</c> values into the opaque
/// cursor (<see cref="ElasticsearchPagination"/>), the same cursor-per-page shape the rest of the
/// HTTP family and <c>AdoKeysetSource</c> use so a transient failure retries in isolation by
/// refetching the same page from its token. Every response is a bounded page (<c>size</c> hits), so
/// it is safely materialized whole with <see cref="JsonDocument.ParseAsync"/> — constant memory
/// across pages. Also implements <see cref="ISourceRowCounter"/> by delegating to an internal
/// <see cref="ElasticsearchRowCounter"/> — that interface is never DI-registered in this codebase;
/// callers instead pattern-match the resolved source instance, the same shape
/// <c>ODataBatchSource{T}</c> uses.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class ElasticsearchBatchSource<T> : IBatchSource<T>, ISourceRowCounter
{
    private readonly HttpClient _client;
    private readonly string _searchUrl;
    private readonly ElasticsearchSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;
    private readonly ElasticsearchRowCounter _rowCounter;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="url">The Elasticsearch/OpenSearch base URL.</param>
    /// <param name="index">The index (or alias/pattern) to search.</param>
    /// <param name="options">Query/sort/auth options — <see cref="ElasticsearchSourceOptions.SortSpec"/> is required.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single hit's <c>_source</c>.</param>
    public ElasticsearchBatchSource(HttpClient client, string url, string index, ElasticsearchSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must be provided.", nameof(url));
        if (string.IsNullOrWhiteSpace(index))
            throw new ArgumentException("Index must be provided.", nameof(index));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.SortSpec is null)
        {
            throw new ArgumentException(
                "An Elasticsearch source requires a configured 'sort' (search_after keyset paging has no default sort to fall back on).",
                nameof(options));
        }

        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _searchUrl = ElasticsearchUrls.Combine(url, index, "_search");
        _rowCounter = new ElasticsearchRowCounter(client, url, index, _options);
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ElasticsearchCursorState state = ElasticsearchPagination.Decode(context.Cursor);
        byte[] body = BuildSearchBody(state, context.PageSize);

        using var request = new HttpRequestMessage(HttpMethod.Post, _searchUrl) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpRequests.ApplyAuth(request, _options.ToAuth());

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await HttpRequests.BuildExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        Stream responseBody = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (responseBody.ConfigureAwait(false))
        {
            using JsonDocument document = await JsonDocument.ParseAsync(responseBody, cancellationToken: cancellationToken).ConfigureAwait(false);

            EnsureSearchWasComplete(document.RootElement);

            JsonElement hits = JsonRecords.GetArray(document.RootElement, "hits.hits");
            var records = new List<T>(context.PageSize);
            JsonElement? lastHit = null;

            foreach (JsonElement hit in hits.EnumerateArray())
            {
                if (!JsonRecords.TryGetField(hit, "_source", out JsonElement source))
                    throw new HttpSourceException(null, null, "A hit in the Elasticsearch response is missing '_source'.");

                records.Add(_materialize(source));
                lastHit = hit;
            }

            // Computed from the truly-last hit only (not "the last hit seen that happened to carry a
            // 'sort' field") — an earlier version tracked lastSort inside the loop, which could latch
            // onto a stale hit's sort values if a later hit in the same page were missing 'sort',
            // silently building the next search_after from the wrong position instead of tripping the
            // guard below (code-review finding).
            JsonElement[]? lastSort = lastHit is { } finalHit && JsonRecords.TryGetField(finalHit, "sort", out JsonElement sortValue) && sortValue.ValueKind == JsonValueKind.Array
                ? sortValue.EnumerateArray().Select(e => e.Clone()).ToArray()
                : null;

            if (records.Count == context.PageSize && lastSort is null)
            {
                // A full page with no per-hit 'sort' values means the next page's search_after can't
                // be computed — always present when the request includes a sort (which this source
                // always sends), so its absence signals a response shape mismatch. Throwing here
                // instead of silently treating the page as the last one avoids masking truncated
                // results as a successful, if short, run (the same instinct as D63's infinite-loop
                // fix, applied to the opposite failure direction: fail loud, don't fail silent).
                throw new HttpSourceException(null, null,
                    "The response's hits did not include 'sort' values needed to fetch the next page, even though the page appears full.");
            }

            bool hasMore = records.Count == context.PageSize;
            string? cursor = hasMore ? ElasticsearchPagination.Encode(new ElasticsearchCursorState(lastSort)) : null;
            return new BatchResult<T>(records, cursor, hasMore);
        }
    }

    /// <summary>
    /// Counts the rows a full run would read (ADR D47/D64) by delegating to <see cref="ElasticsearchRowCounter"/>.
    /// </summary>
    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _rowCounter.CountAsync(execution, cancellationToken);

    private byte[] BuildSearchBody(ElasticsearchCursorState state, int pageSize)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("size", pageSize);
            ElasticsearchQueries.WriteQuery(writer, _options.StaticQuery);
            writer.WritePropertyName("sort");
            _options.SortSpec!.Value.WriteTo(writer);

            if (state.SearchAfter is { Length: > 0 } searchAfter)
            {
                writer.WritePropertyName("search_after");
                writer.WriteStartArray();
                foreach (JsonElement value in searchAfter)
                    value.WriteTo(writer);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Rejects a search that Elasticsearch answered only partially.
    /// <para>
    /// A partial search is <b>HTTP 200</b> with fewer hits than the shards actually hold: the cluster
    /// sets <c>timed_out</c>, or reports failed shards under <c>_shards</c>, and returns whatever the
    /// responsive shards had. Read as an ordinary short page, that ends pagination and the report is
    /// delivered as <c>Completed</c> while silently missing rows — the worst outcome available, since
    /// nothing downstream can tell it apart from a genuinely complete run.
    /// </para>
    /// <para>
    /// This is the same posture GraphQL (D63) already takes toward a 200 carrying <c>errors</c>, and
    /// the same direction as this source's own "full page with no sort values" guard: fail loudly
    /// rather than truncate quietly (ADR D72).
    /// </para>
    /// </summary>
    private static void EnsureSearchWasComplete(JsonElement root)
    {
        if (JsonRecords.TryGetField(root, "timed_out", out JsonElement timedOut)
            && timedOut.ValueKind == JsonValueKind.True)
        {
            throw new HttpSourceException(null, null,
                "Elasticsearch reported 'timed_out': the search returned only the hits it had gathered " +
                "before the timeout, so the report would silently be missing rows. Raise the search " +
                "timeout, or narrow the query.");
        }

        if (JsonRecords.TryGetField(root, "_shards.failed", out JsonElement failed)
            && failed.ValueKind == JsonValueKind.Number
            && failed.TryGetInt32(out int failedShards)
            && failedShards > 0)
        {
            JsonRecords.TryGetField(root, "_shards.total", out JsonElement total);
            string totalText = total.ValueKind == JsonValueKind.Number ? total.GetRawText() : "?";
            throw new HttpSourceException(null, null,
                $"Elasticsearch reported {failedShards} of {totalText} shards failed, so the response " +
                "covers only part of the index and the report would silently be missing rows.");
        }
    }
}
