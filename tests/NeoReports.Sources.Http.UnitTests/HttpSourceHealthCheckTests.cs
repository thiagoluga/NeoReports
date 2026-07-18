using System.Net;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>Tests the HTTP source's on-demand health check (ADR D42/D61, <c>type: "http"</c>).</summary>
public sealed class HttpSourceHealthCheckTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static SourceDefinition Definition(string url, string? healthCheckPath = null)
    {
        var properties = new Dictionary<string, object?> { ["url"] = url };
        if (healthCheckPath is not null)
            properties["healthCheckPath"] = healthCheckPath;
        return new SourceDefinition("http-source", "http", properties);
    }

    [Fact]
    public async Task Healthy_when_the_HEAD_request_succeeds()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            request.Method.ShouldBe(HttpMethod.Head);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new HttpSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://api.test/"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Falls_back_to_GET_when_HEAD_is_not_allowed()
    {
        var methodsSeen = new List<HttpMethod>();
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            methodsSeen.Add(request.Method);
            return request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new HttpSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://api.test/"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        methodsSeen.ShouldBe(new[] { HttpMethod.Head, HttpMethod.Get });
    }

    [Fact]
    public async Task Unhealthy_with_the_status_code_on_a_non_success_response()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var check = new HttpSourceHealthCheck();
        SourceHealthResult result = await check.CheckAsync(Definition("http://api.test/"), new SingleServiceProvider(client), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("500");
    }

    [Fact]
    public async Task Probes_the_configured_health_check_path_relative_to_the_base_url()
    {
        Uri? seenUri = null;
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            seenUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, out _);

        var check = new HttpSourceHealthCheck();
        await check.CheckAsync(Definition("http://api.test/v1/", "health"), new SingleServiceProvider(client), CancellationToken.None);

        seenUri.ShouldBe(new Uri("http://api.test/v1/health"));
    }

    [Fact]
    public async Task Unhealthy_when_the_url_property_is_missing()
    {
        var check = new HttpSourceHealthCheck();
        var definition = new SourceDefinition("http-source", "http", new Dictionary<string, object?>());

        SourceHealthResult result = await check.CheckAsync(definition, new SingleServiceProvider(new HttpClient()), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }
}
