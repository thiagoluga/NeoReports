using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>
/// Tests that <see cref="HttpBatchSource{T}"/>, <see cref="HttpStreamingSource{T}"/>,
/// <see cref="HttpSourceHealthCheck"/>, and the dynamic config path all correctly resolve and apply
/// an OAuth2-fetched bearer token instead of a static one, when configured (ADR D68).
/// </summary>
public sealed class HttpOAuth2WiringTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<T>> CollectAsync<T>(IBatchSource<T> source, int pageSize)
    {
        var results = new List<T>();
        string? cursor = null;
        var pageNumber = 1;
        while (true)
        {
            var context = new BatchContext(Exec(), pageSize, cursor, pageNumber);
            BatchResult<T> result = await source.ReadBatchAsync(context, CancellationToken.None);
            results.AddRange(result.Records);
            if (!result.HasMore)
                break;
            cursor = result.NextCursor;
            pageNumber++;
        }

        return results;
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TokenResponse(string accessToken) =>
        JsonResponse($$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":3600}""");

    [Fact]
    public async Task Batch_source_fetches_a_token_once_and_reuses_it_across_pages()
    {
        var apiCall = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            if (request.RequestUri!.ToString() == "https://auth.test/token")
                return TokenResponse("access-token-1");

            apiCall++;
            return apiCall switch
            {
                1 => JsonResponse("""{"items":[{"id":1,"name":"A"}],"nextCursor":"c1"}"""),
                _ => JsonResponse("""{"items":[{"id":2,"name":"B"}]}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Cursor)
            .RecordsAt("items")
            .OAuth2ClientCredentials("https://auth.test/token", "client-id", "client-secret")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2 });
        handler.Requests.Count(r => r.RequestUri!.ToString() == "https://auth.test/token").ShouldBe(1);
        handler.Requests.Where(r => r.RequestUri!.Host == "api.test")
            .ShouldAllBe(r => r.Headers.Authorization!.ToString() == "Bearer access-token-1");
    }

    [Fact]
    public async Task Streaming_source_none_strategy_applies_the_fetched_token()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
            request.RequestUri!.ToString() == "https://auth.test/token"
                ? TokenResponse("access-token-1")
                : JsonResponse("""[{"id":1,"name":"A"}]"""), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .OAuth2ClientCredentials("https://auth.test/token", "client-id", "client-secret")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Single().Id.ShouldBe(1L);
        HttpRequestMessage apiRequest = handler.Requests.Single(r => r.RequestUri!.Host == "api.test");
        apiRequest.Headers.Authorization!.ToString().ShouldBe("Bearer access-token-1");
    }

    [Fact]
    public async Task Health_check_applies_the_fetched_token()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
            request.RequestUri!.ToString() == "https://auth.test/token"
                ? TokenResponse("access-token-1")
                : new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new HttpSourceHealthCheck();
        var definition = new SourceDefinition("http-source", "http", new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["oauth2TokenEndpoint"] = "https://auth.test/token",
            ["oauth2ClientId"] = "client-id",
            ["oauth2ClientSecret"] = "client-secret",
        });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        HttpRequestMessage apiRequest = handler.Requests.Single(r => r.RequestUri!.Host == "api.test");
        apiRequest.Headers.Authorization!.ToString().ShouldBe("Bearer access-token-1");
    }

    [Fact]
    public async Task Dynamic_config_path_reads_oauth2_properties_and_applies_the_fetched_token()
    {
        var tokenRequestBodies = new List<string>();
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            if (request.RequestUri!.ToString() != "https://auth.test/token")
                return JsonResponse("""[{"id":1,"name":"A"}]""");

            tokenRequestBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return TokenResponse("access-token-1");
        }, out StubHttpMessageHandler handler);

        var provider = new HttpConfigSourceProvider();
        var schema = new ReportSchema(new[] { new ReportColumn("id", ColumnType.Integer), new ReportColumn("name", ColumnType.String) });
        var source = new SourceConfig("http", new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["oauth2TokenEndpoint"] = "https://auth.test/token",
            ["oauth2ClientId"] = "client-id",
            ["oauth2ClientSecret"] = "client-secret",
            ["oauth2Scope"] = "reports:read",
        });

        IBatchSource<ReportRecord> batchSource = provider.Create(source, schema, new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 10);

        Uri.UnescapeDataString(tokenRequestBodies[0]).ShouldContain("scope=reports:read");

        HttpRequestMessage apiRequest = handler.Requests.Single(r => r.RequestUri!.Host == "api.test");
        apiRequest.Headers.Authorization!.ToString().ShouldBe("Bearer access-token-1");
    }

    [Fact]
    public void Bearer_and_oauth2_client_credentials_are_mutually_exclusive()
    {
        Should.Throw<ArgumentException>(() =>
            new HttpSourceOptions().Bearer("static-token").OAuth2ClientCredentials("https://auth.test/token", "client-id", "client-secret"));

        Should.Throw<ArgumentException>(() =>
            new HttpSourceOptions().OAuth2ClientCredentials("https://auth.test/token", "client-id", "client-secret").Bearer("static-token"));
    }

    [Fact]
    public async Task Token_endpoint_failure_surfaces_as_a_clear_error_not_a_generic_auth_failure()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _);

        var source = Source.Http("http://api.test/items", client)
            .OAuth2ClientCredentials("https://auth.test/token", "client-id", "wrong-secret")
            .As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldContain("token endpoint");
    }
}
