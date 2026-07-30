using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.HubSpot.UnitTests;

public sealed record Contact(string Firstname, string Lastname);

/// <summary>
/// Tests the typed HubSpot source's cursor pagination (ADR D65) end to end against a stubbed
/// <see cref="HttpMessageHandler"/> — <c>HubSpotBatchSource{T}</c> is <c>internal</c> (no
/// <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.HubSpot(...).As&lt;T&gt;()</c>, the same approach OData's/Elasticsearch's tests use.
/// </summary>
public sealed class HubSpotBatchSourceTests
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
            if (pageNumber > 1000)
                throw new Xunit.Sdk.XunitException("drain did not terminate within 1000 pages - likely a non-advancing cursor.");
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
    public async Task Paginates_via_paging_next_after_until_absent()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse("""{"results":[{"id":"1","properties":{"firstname":"Ada","lastname":"Lovelace"}}],"paging":{"next":{"after":"c1"}}}"""),
                _ => JsonResponse("""{"results":[{"id":"2","properties":{"firstname":"Alan","lastname":"Turing"}}]}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.HubSpot("contacts", "token123", client).As<Contact>();

        List<Contact> all = await CollectAsync(source, pageSize: 10);

        all.Select(c => c.Firstname).ShouldBe(new[] { "Ada", "Alan" });
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://api.hubapi.com/crm/v3/objects/contacts?limit=10");
        handler.Requests[1].RequestUri!.Query.ShouldContain("after=c1");
    }

    [Fact]
    public async Task Applies_configured_bearer_token()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"results":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.HubSpot("contacts", "token123", client).As<Contact>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer token123");
    }

    [Fact]
    public async Task Configured_properties_are_sent_as_a_comma_separated_query_param()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"results":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.HubSpot("contacts", "token123", client)
            .Properties("firstname", "lastname", "email")
            .As<Contact>();

        await CollectAsync(source, pageSize: 10);

        Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query).ShouldContain("properties=firstname,lastname,email");
    }

    [Fact]
    public async Task A_result_missing_properties_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"results":[{"id":"1"}]}"""), out _);

        var source = Source.HubSpot("contacts", "token123", client).As<Contact>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task Missing_results_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"items":[]}"""), out _);

        var source = Source.HubSpot("contacts", "token123", client).As<Contact>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
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

        var source = Source.HubSpot("contacts", "token123", client).As<Contact>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task BaseUrl_override_is_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"results":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.HubSpot("contacts", "token123", client)
            .BaseUrl("https://proxy.internal")
            .As<Contact>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests[0].RequestUri!.Host.ShouldBe("proxy.internal");
    }
}
