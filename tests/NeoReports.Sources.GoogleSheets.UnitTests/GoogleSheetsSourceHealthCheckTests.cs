using System.Net;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.GoogleSheets.UnitTests;

/// <summary>Tests the Google Sheets source's on-demand health check (ADR D42/D66, <c>type: "googlesheets"</c>).</summary>
public sealed class GoogleSheetsSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static SourceDefinition Definition() =>
        new("sheets-source", "googlesheets", new Dictionary<string, object?> { ["spreadsheetId"] = "sheet123", ["apiKey"] = "key123" });

    [Fact]
    public async Task Healthy_on_a_get_probe_success()
    {
        // ADR D66: goes straight to GET, not HttpHealthProbe.ProbeAsync's HEAD-then-GET fallback —
        // Google's REST-transcoded API frontend isn't confirmed to reject HEAD with one of the two
        // status codes that fallback anticipates (405/501).
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new GoogleSheetsSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound), out _);

        var check = new GoogleSheetsSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("404");
    }

    [Fact]
    public async Task Unhealthy_when_the_spreadsheetId_property_is_missing()
    {
        var check = new GoogleSheetsSourceHealthCheck();
        var definition = new SourceDefinition("sheets-source", "googlesheets", new Dictionary<string, object?> { ["apiKey"] = "key123" });

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Healthy_when_only_spreadsheetId_and_apiKey_are_configured_without_columns()
    {
        // "Test connection" on an incompletely-configured source (spreadsheetId/apiKey set,
        // firstColumn/lastColumn not written yet) must not fail just because the read path's
        // required properties aren't there; the health check only probes reachability/auth.
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out _);

        var check = new GoogleSheetsSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task Probes_the_metadata_endpoint_with_the_api_key()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK), out StubHttpMessageHandler handler);

        var check = new GoogleSheetsSourceHealthCheck();
        await check.CheckAsync(Definition(), new SingleServiceProvider(client), CancellationToken.None);

        Uri requestUri = handler.Requests[0].RequestUri!;
        requestUri.ToString().ShouldContain("sheet123");
        requestUri.Query.ShouldContain("key=key123");
    }
}
