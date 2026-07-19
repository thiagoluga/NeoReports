using System.Globalization;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.OData;

/// <summary>
/// <see cref="ISourceRowCounter"/> for an OData v4 resource (ADR D62) — the first non-SQL source in
/// Epic P to implement row counting honestly, since OData has a real <c>$count</c> mechanism. Issues
/// <c>GET &lt;resource&gt;/$count</c> (honoring any configured static <c>$filter</c>, with the same
/// auth every read request uses) and parses the bare integer response body. Best-effort by contract
/// (<see cref="ISourceRowCounter"/>'s documented "must return null, not throw"): any non-2xx,
/// unsupported <c>$count</c>, or parse failure returns <c>null</c> rather than fabricating a count or
/// failing the run (D36).
/// </summary>
public sealed class ODataRowCounter : ISourceRowCounter
{
    private readonly HttpClient _client;
    private readonly string _resourceUrl;
    private readonly ODataSourceOptions _options;

    /// <summary>Creates the counter.</summary>
    /// <param name="client">The HTTP client used for the count request.</param>
    /// <param name="resourceUrl">The OData resource's (collection's) URL.</param>
    /// <param name="options">Auth/filter options — typically the same instance the batch source reads from.</param>
    public ODataRowCounter(HttpClient client, string resourceUrl, ODataSourceOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resourceUrl = string.IsNullOrWhiteSpace(resourceUrl)
            ? throw new ArgumentException("Resource URL must be provided.", nameof(resourceUrl))
            : resourceUrl;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        try
        {
            Uri countUri = BuildCountUri();

            using var request = new HttpRequestMessage(HttpMethod.Get, countUri);
            HttpRequests.ApplyAuth(request, _options.ToAuth());

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return long.TryParse(body.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long count)
                ? count
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Appends /$count to the resource path — UriBuilder carries the original query string forward
    // unchanged since only Path is set here (e.g. a required tenant/api-version parameter, or an
    // $expand baked into the resource URL because OData has no other configured way to express it,
    // ADR D62's honest gap) — then merges $filter onto it via the shared QueryStrings.AddQuery, the
    // same "preserve, don't overwrite" behavior ODataBatchSource.BuildRequestUri already has.
    private Uri BuildCountUri()
    {
        var baseUri = new Uri(_resourceUrl, UriKind.Absolute);
        var pathBuilder = new UriBuilder(baseUri) { Path = baseUri.AbsolutePath.TrimEnd('/') + "/$count" };
        string countUrl = pathBuilder.Uri.ToString();

        return _options.StaticFilter is { Length: > 0 } filter
            ? QueryStrings.AddQuery(countUrl, ("$filter", filter))
            : new Uri(countUrl);
    }
}
