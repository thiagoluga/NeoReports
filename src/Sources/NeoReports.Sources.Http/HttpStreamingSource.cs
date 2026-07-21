using System.Runtime.CompilerServices;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Http;

/// <summary>
/// <see cref="IStreamingSource{T}"/> for the <see cref="HttpPaginationStrategy.None"/> case (ADR
/// D61): the whole result set is one JSON array in a single response, with no pagination signal to
/// retry a sub-range against. Stream-parses the body (<see cref="JsonRecords.StreamArrayAsync"/>)
/// so a large single response still reads in constant memory — one element at a time, not the
/// whole array. Honest limitation: only the initial request (before any element has been yielded)
/// is retryable by the engine's resilience pipeline; a drop mid-stream cannot resume, since there
/// is no server-side pagination state to resume from.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
internal sealed class HttpStreamingSource<T> : IStreamingSource<T>
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly HttpSourceOptions _options;
    private readonly Func<JsonElement, T> _materialize;
    private readonly OAuth2ClientCredentialsProvider? _oauth2Provider;

    /// <summary>Creates the source.</summary>
    /// <param name="client">The HTTP client used for the request.</param>
    /// <param name="baseUrl">The endpoint's URL.</param>
    /// <param name="options">Auth/mapping options (pagination is ignored — always a single request).</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Builds one <typeparamref name="T"/> from a single record element.</param>
    public HttpStreamingSource(HttpClient client, string baseUrl, HttpSourceOptions options, ReportSchema schema, Func<JsonElement, T> materialize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? throw new ArgumentException("Base URL must be provided.", nameof(baseUrl)) : baseUrl;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        _oauth2Provider = HttpOAuth2.CreateProvider(_client, _options);
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> ReadAsync(ReportExecutionContext execution, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl);
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
            await foreach (JsonElement element in JsonRecords.StreamArrayAsync(body, _options.RecordsPath, cancellationToken).ConfigureAwait(false))
                yield return _materialize(element);
        }
    }

}
