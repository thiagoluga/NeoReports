using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.OData.UnitTests;

public sealed record Item(long Id, string Name);

/// <summary>
/// Tests the typed OData source's paginated strategies (ADR D62) end to end against a stubbed
/// <see cref="HttpMessageHandler"/> — <c>ODataBatchSource{T}</c> is <c>internal</c> (no
/// <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.OData(...).As&lt;T&gt;()</c>, the same approach the HTTP source's tests use.
/// </summary>
public sealed class ODataBatchSourceTests
{
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

    [Fact]
    public async Task NextLink_strategy_follows_the_response_link_until_absent()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse("""{"value":[{"id":1,"name":"A"}],"@odata.nextLink":"http://api.test/items?$skiptoken=abc"}"""),
                _ => JsonResponse("""{"value":[{"id":2,"name":"B"}]}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.OData("http://api.test/items", client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2 });
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].RequestUri!.ToString().ShouldBe("http://api.test/items?$skiptoken=abc");
    }

    [Fact]
    public async Task NextLink_strategy_refuses_to_send_credentials_to_a_different_host()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
                JsonResponse("""{"value":[{"id":1,"name":"A"}],"@odata.nextLink":"http://attacker.test/steal"}"""),
            out StubHttpMessageHandler handler);

        var source = Source.OData("http://api.test/items", client)
            .ApiKey("X-Api-Key", "secret123")
            .As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
        ex.Message.ShouldContain("attacker.test");

        // The first (legitimate) request went through; the credential-bearing second request to
        // attacker.test must never have been sent at all.
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.Host.ShouldBe("api.test");
    }

    [Fact]
    public async Task Skip_strategy_ends_pagination_on_a_shorter_than_top_page()
    {
        var responses = new Dictionary<int, string>
        {
            [0] = """{"value":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""",
            [2] = """{"value":[{"id":3,"name":"C"}]}""",
        };

        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            int skip = HttpTestHelpers.ParseQueryInt(request.RequestUri!, "$skip");
            return JsonResponse(responses[skip]);
        }, out StubHttpMessageHandler handler);

        var source = Source.OData("http://api.test/items", client)
            .Paginate(ODataPaginationStrategy.Skip)
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 2);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.Query.ShouldContain("$top=2");
        handler.Requests[0].RequestUri!.Query.ShouldNotContain("%24");
    }

    [Fact]
    public async Task Static_filter_select_orderby_are_appended_to_the_request_url()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"value":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.OData("http://api.test/items", client)
            .Filter("Amount gt 100")
            .Select("Id,Name")
            .OrderBy("Id desc")
            .As<Item>();

        await CollectAsync(source, pageSize: 10);

        // Asserted on the RAW (not Uri.Unescape'd) query string: the '$' in '$filter'/'$select'/
        // '$orderby' must reach the wire literally, not percent-encoded to '%24' — a server matching
        // OData's system query options against the literal '$'-prefixed name would otherwise not
        // recognize them.
        string rawQuery = handler.Requests[0].RequestUri!.Query;
        rawQuery.ShouldContain("$filter=");
        rawQuery.ShouldContain("$select=");
        rawQuery.ShouldContain("$orderby=");
        rawQuery.ShouldNotContain("%24");

        string query = Uri.UnescapeDataString(rawQuery);
        query.ShouldContain("$filter=Amount gt 100");
        query.ShouldContain("$select=Id,Name");
        query.ShouldContain("$orderby=Id desc");
    }

    [Fact]
    public async Task Non_success_response_throws_with_status_and_retry_after()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        }, out _);

        var source = Source.OData("http://api.test/items", client).As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Applies_configured_api_key_bearer_and_static_headers()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"value":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.OData("http://api.test/items", client)
            .ApiKey("X-Api-Key", "secret123")
            .Bearer("token456")
            .Header("X-Custom", "value")
            .As<Item>();

        await CollectAsync(source, pageSize: 10);

        HttpRequestMessage request = handler.Requests[0];
        request.Headers.GetValues("X-Api-Key").ShouldContain("secret123");
        request.Headers.Authorization!.ToString().ShouldBe("Bearer token456");
        request.Headers.GetValues("X-Custom").ShouldContain("value");
    }

    [Fact]
    public async Task Missing_records_path_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"items":[]}"""), out _);

        var source = Source.OData("http://api.test/items", client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }
}
