namespace NeoReports.Sources.GoogleSheets.UnitTests;

/// <summary>Test double for <see cref="HttpMessageHandler"/>: answers every request from a delegate, and records requests seen.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHttpMessageHandler handler)
    {
        handler = new StubHttpMessageHandler(respond);
        return new HttpClient(handler);
    }
}
