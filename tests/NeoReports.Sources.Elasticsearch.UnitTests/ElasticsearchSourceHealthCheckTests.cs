using System.Net;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Elasticsearch.UnitTests;

/// <summary>Tests the Elasticsearch/OpenSearch source's on-demand health check (ADR D42/D64, <c>type: "elasticsearch"</c>).</summary>
public sealed class ElasticsearchSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static SourceDefinition Definition(string url, string index = "orders") =>
        new("es-source", "elasticsearch", new Dictionary<string, object?> { ["url"] = url, ["index"] = index });

    [Fact]
    public async Task Healthy_on_a_head_probe_success()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((request, _) =>
        {
            request.Method.ShouldBe(HttpMethod.Head);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new ElasticsearchSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://es.test"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Falls_back_to_get_when_head_is_not_allowed()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient((request, _) =>
        {
            call++;
            return call == 1
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }, out StubHttpMessageHandler handler);

        var check = new ElasticsearchSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://es.test"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Message.Method.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var check = new ElasticsearchSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://es.test"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("500");
    }

    [Fact]
    public async Task Healthy_when_only_url_and_index_are_configured_without_sort()
    {
        // "Test connection" on an incompletely-configured source (url/index set, sort not written
        // yet — a normal incremental setup flow) must not fail just because the read path's
        // required 'sort' property isn't there; the health check only probes reachability/auth.
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK), out _);

        var check = new ElasticsearchSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://es.test"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task Unhealthy_when_the_url_property_is_missing()
    {
        var check = new ElasticsearchSourceHealthCheck();
        var definition = new SourceDefinition("es-source", "elasticsearch", new Dictionary<string, object?> { ["index"] = "orders" });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Probes_the_configured_index_url_by_default()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new ElasticsearchSourceHealthCheck();
        await check.CheckAsync(Definition("http://es.test", "orders"), new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("http://es.test/orders");
    }

    [Fact]
    public async Task A_configured_healthCheckPath_is_resolved_relative_to_the_index_url_not_the_bare_base_url()
    {
        // Regression: an earlier version combined healthCheckPath against the bare base 'url'
        // (dropping the 'index' segment the option's own XML doc promises), and separately, using
        // Uri's relative-combination rules directly (instead of plain path concatenation) would have
        // silently dropped 'index' too whenever '{url}/{index}' had no trailing slash.
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new ElasticsearchSourceHealthCheck();
        var definition = new SourceDefinition("es-source", "elasticsearch", new Dictionary<string, object?>
        {
            ["url"] = "http://es.test",
            ["index"] = "orders",
            ["healthCheckPath"] = "_count",
        });

        await check.CheckAsync(definition, new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("http://es.test/orders/_count");
    }
}
