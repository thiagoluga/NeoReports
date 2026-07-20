using System.Net;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Airtable.UnitTests;

/// <summary>Tests the Airtable source's on-demand health check (ADR D42/D65, <c>type: "airtable"</c>).</summary>
public sealed class AirtableSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static SourceDefinition Definition(string table = "Projects") =>
        new("airtable-source", "airtable", new Dictionary<string, object?> { ["baseId"] = "appXXX", ["table"] = table, ["bearerToken"] = "token123" });

    [Fact]
    public async Task Healthy_on_a_head_probe_success()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            request.Method.ShouldBe(HttpMethod.Head);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new AirtableSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _);

        var check = new AirtableSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("401");
    }

    [Fact]
    public async Task Unhealthy_when_the_baseId_property_is_missing()
    {
        var check = new AirtableSourceHealthCheck();
        var definition = new SourceDefinition("airtable-source", "airtable", new Dictionary<string, object?> { ["table"] = "Projects", ["bearerToken"] = "token123" });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Probes_the_resolved_table_url_by_default()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new AirtableSourceHealthCheck();
        await check.CheckAsync(Definition("Projects"), new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://api.airtable.com/v0/appXXX/Projects");
    }

    [Fact]
    public async Task A_configured_healthCheckPath_is_appended_after_the_table_url_not_replacing_its_last_segment()
    {
        // Regression: a Uri-relative-resolution-based combine (new Uri(tableUrl, path)) would
        // silently REPLACE "Projects" instead of appending after it, since tableUrl never ends in a
        // trailing slash — the same bug class D64 found and fixed for Elasticsearch.
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new AirtableSourceHealthCheck();
        var definition = new SourceDefinition("airtable-source", "airtable", new Dictionary<string, object?>
        {
            ["baseId"] = "appXXX",
            ["table"] = "Projects",
            ["bearerToken"] = "token123",
            ["healthCheckPath"] = "ping",
        });

        await check.CheckAsync(definition, new SingleServiceProvider(client), CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://api.airtable.com/v0/appXXX/Projects/ping");
    }
}
