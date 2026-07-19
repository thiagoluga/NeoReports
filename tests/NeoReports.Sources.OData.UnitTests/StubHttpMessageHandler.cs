namespace NeoReports.Sources.OData.UnitTests;

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

/// <summary>Shared test helpers, so query-string assertions don't need their own copy per test class.</summary>
internal static class HttpTestHelpers
{
    /// <summary>Reads a query parameter's value as an int, throwing if the parameter is absent.</summary>
    public static int ParseQueryInt(Uri uri, string key)
    {
        string[]? match = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .FirstOrDefault(kv => Uri.UnescapeDataString(kv[0]) == key);

        return match is not null
            ? int.Parse(Uri.UnescapeDataString(match[1]))
            : throw new KeyNotFoundException(key);
    }
}
