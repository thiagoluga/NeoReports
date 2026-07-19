using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// <see cref="IBatchSource{T}"/> over a GraphQL endpoint's Relay connection (ADR D63) — one
/// <c>POST</c> per <see cref="ReadBatchAsync"/>, injecting the page size and the prior page's
/// <c>endCursor</c> as GraphQL variables and encoding the next <c>after</c> into the opaque cursor
/// (<see cref="GraphQlPagination"/>), the same cursor-per-page shape the rest of the HTTP family
/// (P4a/P5a) uses so a transient failure retries in isolation by refetching the same page from its
/// token. Unlike REST, a <c>200 OK</c> response can still be a failure: the response's <c>errors</c>
/// array is inspected before <c>data</c> is read, and an <see cref="HttpSourceException"/> is thrown
/// even though the transport succeeded. A response bounded by the requested page size is always
/// safely materialized with <see cref="JsonDocument.ParseAsync"/> — constant memory across pages.
/// No <c>ISourceRowCounter</c> — Relay's optional <c>totalCount</c> has no universal mechanism this
/// source can rely on (ADR D63's honest gap).
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class GraphQlBatchSource<T> : IBatchSource<T>
{
    private readonly HttpClient _client;
    private readonly string _endpointUrl;
    private readonly GraphQlSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="endpointUrl">The GraphQL endpoint's URL (single endpoint, every query is <c>POST</c>ed there).</param>
    /// <param name="options">Query/variables/connection/auth options.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single edge's node element.</param>
    public GraphQlBatchSource(HttpClient client, string endpointUrl, GraphQlSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpointUrl = string.IsNullOrWhiteSpace(endpointUrl)
            ? throw new ArgumentException("Endpoint URL must be provided.", nameof(endpointUrl))
            : endpointUrl;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.QueryDocument))
            throw new ArgumentException("A GraphQL query document must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ConnectionPath))
            throw new ArgumentException("A connection path must be configured.", nameof(options));

        // A static variable sharing the configured paging-variable name would otherwise be silently
        // overwritten by the injected page-size/cursor value every request — fail loudly at
        // construction instead of producing quietly-wrong query results.
        if (_options.StaticVariables is { } staticVariables
            && (staticVariables.ContainsKey(_options.FirstVariableName) || staticVariables.ContainsKey(_options.AfterVariableName)))
        {
            throw new ArgumentException(
                $"A configured variable collides with the paging variable name ('{_options.FirstVariableName}' or " +
                $"'{_options.AfterVariableName}'); rename the variable or the paging variable.", nameof(options));
        }

        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        GraphQlCursorState state = GraphQlPagination.Decode(context.Cursor);
        string requestBody = GraphQlRequest.BuildBody(
            _options.QueryDocument!,
            _options.StaticVariables,
            _options.FirstVariableName,
            context.PageSize,
            _options.AfterVariableName,
            state.After);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
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

            ThrowIfGraphQlErrors(response, root);

            string connectionPath = "data." + _options.ConnectionPath;
            JsonElement edges = JsonRecords.GetArray(root, connectionPath + ".edges");

            var records = new List<T>(context.PageSize);
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (!JsonRecords.TryGetField(edge, _options.NodePath, out JsonElement node))
                {
                    throw new HttpSourceException(null, null,
                        $"An edge in the connection at '{_options.ConnectionPath}' is missing its '{_options.NodePath}' field.");
                }

                // A present-but-null node is a valid Relay tombstone (the referenced entity was
                // deleted/is no longer accessible) — not a malformed response like a missing field.
                // Skipping it, rather than materializing a garbage null/all-null row or treating it as
                // an error, is the honest behavior for a spec-compliant occurrence.
                if (node.ValueKind == JsonValueKind.Null)
                    continue;

                records.Add(_materialize(node));
            }

            return BuildResult(records, root, connectionPath);
        }
    }

    private static BatchResult<T> BuildResult(List<T> records, JsonElement root, string connectionPath)
    {
        // A missing pageInfo is a defensive, not silent, failure (ADR D63): the author's query is
        // expected to select "pageInfo { hasNextPage endCursor }" on the configured connection, and
        // an author who forgets it gets a clear error here instead of a silent single-page run.
        if (!JsonRecords.TryGetField(root, connectionPath + ".pageInfo", out JsonElement pageInfo) || pageInfo.ValueKind != JsonValueKind.Object)
        {
            throw new HttpSourceException(null, null,
                $"The response is missing 'pageInfo' at '{connectionPath}'; the query must select 'pageInfo {{ hasNextPage endCursor }}'.");
        }

        bool hasNextPage = pageInfo.TryGetProperty("hasNextPage", out JsonElement hasNextPageElement)
            && hasNextPageElement.ValueKind == JsonValueKind.True;

        string? endCursor = pageInfo.TryGetProperty("endCursor", out JsonElement endCursorElement) && endCursorElement.ValueKind == JsonValueKind.String
            ? endCursorElement.GetString()
            : null;

        // hasNextPage:true with no endCursor is a malformed response, not "no more pages" — encoding
        // a cursor with a null After would re-request the exact same page forever (After omitted is
        // indistinguishable from the first request), an infinite loop rather than a clean failure.
        if (hasNextPage && endCursor is null)
        {
            throw new HttpSourceException(null, null,
                $"The response at '{connectionPath}' has pageInfo.hasNextPage=true but no 'endCursor' to resume from.");
        }

        string? cursor = hasNextPage ? GraphQlPagination.Encode(new GraphQlCursorState(endCursor)) : null;
        return new BatchResult<T>(records, cursor, hasNextPage);
    }

    /// <summary>
    /// Inspects the response's <c>errors</c> array before <c>data</c> is read (ADR D63) — the
    /// load-bearing difference from REST: a GraphQL error can arrive as HTTP <c>200</c> with a
    /// populated <c>errors</c> array, which is a failure even though the transport succeeded.
    /// <c>Retry-After</c> is still honored on this path (some GraphQL APIs, e.g. GitHub's, signal
    /// rate-limiting this way) via the same header reader the non-2xx path uses.
    /// </summary>
    private static void ThrowIfGraphQlErrors(HttpResponseMessage response, JsonElement root)
    {
        if (!GraphQlErrors.TryGetMessage(root, out string? message))
            return;

        // statusCode: null — the transport succeeded (HTTP 200); this is a GraphQL-level failure.
        throw new HttpSourceException(null, HttpRequests.ReadRetryAfter(response), $"The GraphQL response contained errors: {message}");
    }
}
