using System.Net;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.OData.UnitTests;

/// <summary>
/// ADR D62: <see cref="ODataRowCounter"/> — the first non-SQL <c>ISourceRowCounter</c> in Epic P.
/// Best-effort by contract: never throws, always <c>null</c> on failure.
/// </summary>
public sealed class ODataRowCounterTests
{
    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Successful_count_parses_the_bare_integer_body()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            request.RequestUri!.AbsolutePath.ShouldEndWith("/$count");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42") };
        }, out _);

        var counter = new ODataRowCounter(client, "http://api.test/items", new ODataSourceOptions());

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(42);
    }

    [Fact]
    public async Task Non_success_response_returns_null()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var counter = new ODataRowCounter(client, "http://api.test/items", new ODataSourceOptions());

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
    }

    [Fact]
    public async Task Malformed_body_returns_null()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-a-number") }, out _);

        var counter = new ODataRowCounter(client, "http://api.test/items", new ODataSourceOptions());

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
    }

    [Fact]
    public async Task Appends_the_configured_static_filter_to_the_count_request()
    {
        Uri? seenUri = null;
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            seenUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("7") };
        }, out _);

        var options = new ODataSourceOptions().Filter("Amount gt 100");
        var counter = new ODataRowCounter(client, "http://api.test/items", options);

        await counter.CountAsync(Exec(), CancellationToken.None);

        Uri.UnescapeDataString(seenUri!.Query).ShouldContain("$filter=Amount gt 100");
    }

    [Fact]
    public async Task Preserves_a_pre_existing_query_string_on_the_configured_resource_url()
    {
        // OData has no supported way to express $expand (a documented honest gap, ADR D62), so a
        // report author needing it has no option but to bake it into the resource URL — $count must
        // not silently drop that (or any other pre-existing query content) the way the read path
        // (ODataBatchSource) doesn't.
        Uri? seenUri = null;
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            seenUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("5") };
        }, out _);

        var counter = new ODataRowCounter(client, "http://api.test/items?$expand=Lines", new ODataSourceOptions());

        await counter.CountAsync(Exec(), CancellationToken.None);

        seenUri!.AbsolutePath.ShouldEndWith("/$count");
        Uri.UnescapeDataString(seenUri.Query).ShouldContain("$expand=Lines");
    }
}
