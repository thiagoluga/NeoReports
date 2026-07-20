namespace NeoReports.Sources.Elasticsearch.UnitTests;

/// <summary>
/// One captured request: the original <see cref="HttpRequestMessage"/> (its headers/URI are safe to
/// read after the source disposes it — only <see cref="HttpContent"/> is disposed) plus its POST body
/// text, captured at receipt time before that disposal happens.
/// </summary>
internal sealed record CapturedRequest(HttpRequestMessage Message, string? Body)
{
    public Uri? RequestUri => Message.RequestUri;
}

/// <summary>
/// Test double for <see cref="HttpMessageHandler"/>: answers every request from a delegate, and
/// records each request plus its body. Every Elasticsearch/OpenSearch request carries its payload in
/// the POST body (search/count), so the body is read and captured synchronously inside
/// <see cref="SendAsync"/> — while the request is still alive — since the source's
/// <c>using var request = ...</c> disposes the request (and its <see cref="HttpContent"/>) once
/// <c>ReadBatchAsync</c> returns, before a test would otherwise get a chance to read
/// <see cref="HttpContent"/> itself.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) => _respond = respond;

    public List<CapturedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new CapturedRequest(request, body));
        return _respond(request, body);
    }

    public static HttpClient CreateClient(Func<HttpRequestMessage, string?, HttpResponseMessage> respond, out StubHttpMessageHandler handler)
    {
        handler = new StubHttpMessageHandler(respond);
        return new HttpClient(handler);
    }
}
