using System.Net;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.HubSpot.UnitTests;

/// <summary>Tests the HubSpot source's on-demand health check (ADR D42/D65, <c>type: "hubspot"</c>).</summary>
public sealed class HubSpotSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static SourceDefinition Definition(string objectType = "contacts") =>
        new("hubspot-source", "hubspot", new Dictionary<string, object?> { ["objectType"] = objectType, ["bearerToken"] = "token123" });

    [Fact]
    public async Task Healthy_on_a_head_probe_success()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            request.Method.ShouldBe(HttpMethod.Head);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new HubSpotSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _);

        var check = new HubSpotSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("401");
    }

    [Fact]
    public async Task Unhealthy_when_the_objectType_property_is_missing()
    {
        var check = new HubSpotSourceHealthCheck();
        var definition = new SourceDefinition("hubspot-source", "hubspot", new Dictionary<string, object?> { ["bearerToken"] = "token123" });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Probes_the_resolved_object_collection_url_by_default()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new HubSpotSourceHealthCheck();
        await check.CheckAsync(Definition("companies"), new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://api.hubapi.com/crm/v3/objects/companies");
    }

    [Fact]
    public async Task A_configured_healthCheckPath_is_appended_after_the_collection_url_not_replacing_its_last_segment()
    {
        // Regression: a Uri-relative-resolution-based combine (new Uri(collectionUrl, path)) would
        // silently REPLACE "contacts" instead of appending after it, since collectionUrl never ends
        // in a trailing slash — the same bug class D64 found and fixed for Elasticsearch.
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new HubSpotSourceHealthCheck();
        var definition = new SourceDefinition("hubspot-source", "hubspot", new Dictionary<string, object?>
        {
            ["objectType"] = "contacts",
            ["bearerToken"] = "token123",
            ["healthCheckPath"] = "ping",
        });

        await check.CheckAsync(definition, new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://api.hubapi.com/crm/v3/objects/contacts/ping");
    }
}
