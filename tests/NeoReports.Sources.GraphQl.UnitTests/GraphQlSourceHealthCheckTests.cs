using System.Net;
using System.Text;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.GraphQl.UnitTests;

/// <summary>Tests the GraphQL source's on-demand health check (ADR D42/D63, <c>type: "graphql"</c>).</summary>
public sealed class GraphQlSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private const string Doc =
        "query($first: Int, $after: String) { viewer { repositories(first: $first, after: $after) " +
        "{ edges { node { id } } pageInfo { hasNextPage endCursor } } } }";

    private static SourceDefinition Definition(string url) =>
        new("graphql-source", "graphql", new Dictionary<string, object?>
        {
            ["url"] = url,
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
        });

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Healthy_on_a_typename_probe_with_no_errors()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((request, body) =>
        {
            request.Method.ShouldBe(HttpMethod.Post);
            body!.ShouldContain("__typename");
            return JsonResponse("""{"data":{"__typename":"Query"}}""");
        }, out _);

        var check = new GraphQlSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://api.test/graphql"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var check = new GraphQlSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://api.test/graphql"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("500");
    }

    [Fact]
    public async Task Unhealthy_on_a_200_response_with_errors()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"errors":[{"message":"not authenticated"}]}"""), out _);

        var check = new GraphQlSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://api.test/graphql"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Healthy_when_only_url_and_auth_are_configured_without_query_or_connectionPath()
    {
        // "Test connection" on an incompletely-configured source (url/auth set, query not written
        // yet — a normal incremental setup flow) must not fail just because the read path's
        // required properties aren't there; the health check only probes reachability/auth.
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => JsonResponse("""{"data":{"__typename":"Query"}}"""), out _);

        var check = new GraphQlSourceHealthCheck();
        var definition = new SourceDefinition("graphql-source", "graphql", new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/graphql",
            ["bearerToken"] = "secret",
        });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task Unhealthy_when_the_url_property_is_missing()
    {
        var check = new GraphQlSourceHealthCheck();
        var definition = new SourceDefinition("graphql-source", "graphql", new Dictionary<string, object?>
        {
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
        });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }
}
