using System.Net.Http.Headers;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// <see cref="ISourceRowCounter"/> for an Elasticsearch/OpenSearch index (ADR D64) — issues
/// <c>POST &lt;url&gt;/&lt;index&gt;/_count</c> with the same effective query every read request
/// uses (honoring any configured static query, with the same auth), and reads the response's
/// <c>count</c> field. Best-effort by contract (<see cref="ISourceRowCounter"/>'s documented "must
/// return null, not throw"): any non-2xx, missing-field, or parse failure returns <c>null</c> rather
/// than fabricating a count or failing the run (D36).
/// </summary>
public sealed class ElasticsearchRowCounter : ISourceRowCounter
{
    private readonly HttpClient _client;
    private readonly string _countUrl;
    private readonly ElasticsearchSourceOptions _options;

    /// <summary>Creates the counter.</summary>
    /// <param name="client">The HTTP client used for the count request.</param>
    /// <param name="url">The Elasticsearch/OpenSearch base URL.</param>
    /// <param name="index">The index (or alias/pattern) to count.</param>
    /// <param name="options">Query/auth options — typically the same instance the batch source reads from.</param>
    public ElasticsearchRowCounter(HttpClient client, string url, string index, ElasticsearchSourceOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must be provided.", nameof(url));
        if (string.IsNullOrWhiteSpace(index))
            throw new ArgumentException("Index must be provided.", nameof(index));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _countUrl = ElasticsearchUrls.Combine(url, index, "_count");
    }

    /// <inheritdoc />
    public async Task<long?> CountAsync(ReportExecutionContext execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        try
        {
            using var bodyStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bodyStream))
            {
                writer.WriteStartObject();
                ElasticsearchQueries.WriteQuery(writer, _options.StaticQuery);
                writer.WriteEndObject();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _countUrl) { Content = new ByteArrayContent(bodyStream.ToArray()) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            HttpRequests.ApplyAuth(request, _options.ToAuth());

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            Stream responseBody = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (responseBody.ConfigureAwait(false))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(responseBody, cancellationToken: cancellationToken).ConfigureAwait(false);
                return document.RootElement.TryGetProperty("count", out JsonElement countElement) && countElement.TryGetInt64(out long count)
                    ? count
                    : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
