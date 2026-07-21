using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Salesforce;

/// <summary>
/// <see cref="IBatchSource{T}"/> over a Salesforce SOQL query (ADR D67) — one page per
/// <see cref="ReadBatchAsync"/>, carrying the response's own <c>nextRecordsUrl</c> verbatim in the
/// opaque cursor (<see cref="SalesforcePagination"/>) so a transient failure retries in isolation
/// from its own cursor (D6/D11), the same reasoning every HTTP-family source in Epic P uses. Records
/// arrive as flat JSON objects (the queried fields at the top level, alongside an unmapped sibling
/// <c>attributes</c> metadata key) — no envelope to descend into, unlike HubSpot/Airtable. Also
/// implements <see cref="ISourceRowCounter"/> by delegating to an internal <see cref="SalesforceRowCounter"/>
/// — the same composition <c>ODataBatchSource{T}</c>/<c>ElasticsearchBatchSource{T}</c> use (code
/// review finding: without this, the fully-built row counter was unreachable dead code in
/// production, since <c>ReportBuilder</c> detects counting support via <c>source as ISourceRowCounter</c>
/// pattern-matching on the instance this class's own factories return).
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class SalesforceBatchSource<T> : IBatchSource<T>, ISourceRowCounter
{
    private readonly HttpClient _client;
    private readonly string _instanceUrl;
    private readonly Uri _instanceOrigin;
    private readonly string _soql;
    private readonly SalesforceSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;
    private readonly SalesforceRowCounter _rowCounter;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for every request.</param>
    /// <param name="instanceUrl">The Salesforce org's instance URL (e.g. <c>https://myorg.my.salesforce.com</c>).</param>
    /// <param name="soql">The SOQL query to run.</param>
    /// <param name="options">API version/auth options.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single record element.</param>
    public SalesforceBatchSource(HttpClient client, string instanceUrl, string soql, SalesforceSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _instanceUrl = string.IsNullOrWhiteSpace(instanceUrl) ? throw new ArgumentException("Instance URL must be provided.", nameof(instanceUrl)) : instanceUrl;
        _instanceOrigin = new Uri(_instanceUrl, UriKind.Absolute);
        _soql = string.IsNullOrWhiteSpace(soql) ? throw new ArgumentException("SOQL query must be provided.", nameof(soql)) : soql;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _rowCounter = new SalesforceRowCounter(_client, _instanceUrl, _soql, _options);
    }

    /// <inheritdoc />
    public Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken) =>
        _rowCounter.CountAsync(execution, cancellationToken);

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        SalesforceCursorState state = SalesforcePagination.Decode(context.Cursor);
        Uri requestUri = BuildRequestUri(state);

        using JsonDocument document = await HttpRequests.GetJsonAsync(_client, requestUri, _options.ToAuth(), cancellationToken).ConfigureAwait(false);

        JsonElement root = document.RootElement;
        JsonElement recordsArray = JsonRecords.GetArray(root, "records");
        var records = new List<T>(context.PageSize);
        foreach (JsonElement record in recordsArray.EnumerateArray())
            records.Add(_materialize(record));

        bool done = JsonRecords.TryGetField(root, "done", out JsonElement doneValue) && doneValue.ValueKind == JsonValueKind.True;
        string? nextRecordsUrl = !done
            && JsonRecords.TryGetField(root, "nextRecordsUrl", out JsonElement nextUrlValue)
            && nextUrlValue.ValueKind == JsonValueKind.String
            && nextUrlValue.GetString() is { Length: > 0 } text
                ? text
                : null;

        bool hasMore = nextRecordsUrl is not null;
        string? cursor = hasMore ? SalesforcePagination.Encode(new SalesforceCursorState(nextRecordsUrl)) : null;
        return new BatchResult<T>(records, cursor, hasMore);
    }

    private Uri BuildRequestUri(SalesforceCursorState state)
    {
        if (state.NextRecordsUrl is null)
            return SalesforceUrls.Query(_instanceUrl, _options.ApiVersionValue, _soql);

        Uri nextPageUri = SalesforceUrls.NextPage(_instanceOrigin, state.NextRecordsUrl);
        if (!HttpOrigin.IsSameOrigin(nextPageUri, _instanceOrigin))
        {
            throw new HttpSourceException(null, null,
                $"The response's 'nextRecordsUrl' ('{nextPageUri}') has a different scheme/host/port than the configured instance URL; refusing to send the configured credentials there.");
        }

        return nextPageUri;
    }
}
