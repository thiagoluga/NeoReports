using System.Net;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Salesforce.UnitTests;

/// <summary>Tests the Salesforce source's on-demand health check (ADR D42/D67, <c>type: "salesforce"</c>).</summary>
public sealed class SalesforceSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static SourceDefinition Definition() =>
        new("sf-source", "salesforce", new Dictionary<string, object?>
        {
            ["instanceUrl"] = "https://myorg.my.salesforce.com",
            ["soql"] = "SELECT Id FROM Account",
            ["bearerToken"] = "token123",
        });

    [Fact]
    public async Task Healthy_on_a_head_probe_success()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            request.Method.ShouldBe(HttpMethod.Head);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new SalesforceSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _);

        var check = new SalesforceSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("401");
    }

    [Fact]
    public async Task Unhealthy_when_the_instanceUrl_property_is_missing()
    {
        var check = new SalesforceSourceHealthCheck();
        var definition = new SourceDefinition("sf-source", "salesforce", new Dictionary<string, object?>
        {
            ["soql"] = "SELECT Id FROM Account",
            ["bearerToken"] = "token123",
        });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Probes_the_resolved_resources_url_by_default()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new SalesforceSourceHealthCheck();
        await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://myorg.my.salesforce.com/services/data/v59.0/");
    }

    [Fact]
    public async Task A_healthCheckPath_starting_with_a_slash_is_appended_not_treated_as_an_absolute_path()
    {
        // Regression: HttpHealthProbe.CombineUrl's Uri relative-resolution treats a path starting
        // with '/' as an absolute-path reference that replaces the ENTIRE base path, regardless of
        // the base's own trailing slash — the same D64/D65 bug class HubSpot/Airtable/Elasticsearch
        // already had to avoid.
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new SalesforceSourceHealthCheck();
        var definition = new SourceDefinition("sf-source", "salesforce", new Dictionary<string, object?>
        {
            ["instanceUrl"] = "https://myorg.my.salesforce.com",
            ["soql"] = "SELECT Id FROM Account",
            ["bearerToken"] = "token123",
            ["healthCheckPath"] = "/limits",
        });

        await check.CheckAsync(definition, new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://myorg.my.salesforce.com/services/data/v59.0/limits");
    }
}
