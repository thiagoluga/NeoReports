using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Elasticsearch.UnitTests;

public sealed record Item(long Id, string Name);

/// <summary>
/// Tests the typed Elasticsearch/OpenSearch source's <c>search_after</c> keyset pagination (ADR D64)
/// end to end against a stubbed <see cref="HttpMessageHandler"/> — <c>ElasticsearchBatchSource{T}</c>
/// is <c>internal</c> (no <c>InternalsVisibleTo</c> convention in this repo), so correctness is
/// verified through <c>Source.Elasticsearch(...).As&lt;T&gt;()</c>, the same approach OData's/
/// GraphQL's tests use.
/// </summary>
public sealed class ElasticsearchBatchSourceTests
{
    private const string SortDsl = """[{"createdAt":"asc"},{"_id":"asc"}]""";

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

    private static ElasticsearchSourceBuilder Builder(HttpClient client) =>
        Source.Elasticsearch("http://es.test", "orders", client).Sort(SortDsl);

    [Fact]
    public async Task Paginates_via_search_after_until_a_short_page()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse("""{"hits":{"hits":[{"_source":{"id":1,"name":"A"},"sort":[1,"a"]},{"_source":{"id":2,"name":"B"},"sort":[2,"b"]}]}}"""),
                _ => JsonResponse("""{"hits":{"hits":[{"_source":{"id":3,"name":"C"},"sort":[3,"c"]}]}}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Builder(client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 2);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests.Count.ShouldBe(2);

        JsonElement firstBody = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
        firstBody.TryGetProperty("search_after", out _).ShouldBeFalse();
        firstBody.GetProperty("size").GetInt32().ShouldBe(2);

        JsonElement secondBody = JsonDocument.Parse(handler.Requests[1].Body!).RootElement;
        JsonElement searchAfter = secondBody.GetProperty("search_after");
        searchAfter[0].GetInt32().ShouldBe(2);
        searchAfter[1].GetString().ShouldBe("b");
    }

    [Fact]
    public async Task A_page_shorter_than_size_ends_pagination_without_a_next_search_after()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[{"_source":{"id":1,"name":"A"},"sort":[1]}]}}"""), out _);

        var source = Builder(client).As<Item>();

        var context = new BatchContext(Exec(), pageSize: 10, cursor: null, pageNumber: 1);
        BatchResult<Item> result = await source.ReadBatchAsync(context, CancellationToken.None);

        result.HasMore.ShouldBeFalse();
        result.NextCursor.ShouldBeNull();
    }

    [Fact]
    public void Construction_without_a_configured_sort_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => JsonResponse("{}"), out _);

        Should.Throw<ArgumentException>(() => Source.Elasticsearch("http://es.test", "orders", client).As<Item>());
    }

    [Fact]
    public async Task Default_query_is_match_all_when_none_is_configured()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[]}}"""), out StubHttpMessageHandler handler);

        var source = Builder(client).As<Item>();

        await CollectAsync(source, pageSize: 10);

        JsonElement body = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
        body.GetProperty("query").GetProperty("match_all").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task Configured_static_query_is_sent_verbatim()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[]}}"""), out StubHttpMessageHandler handler);

        var source = Source.Elasticsearch("http://es.test", "orders", client)
            .Sort(SortDsl)
            .Query("""{"term":{"status":"open"}}""")
            .As<Item>();

        await CollectAsync(source, pageSize: 10);

        JsonElement body = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
        body.GetProperty("query").GetProperty("term").GetProperty("status").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task Request_targets_search_endpoint_of_the_configured_index()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[]}}"""), out StubHttpMessageHandler handler);

        var source = Builder(client).As<Item>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests[0].RequestUri!.ToString().ShouldBe("http://es.test/orders/_search");
        handler.Requests[0].Message.Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task A_hit_missing_source_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[{"sort":[1]}]}}"""), out _);

        var source = Builder(client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task A_full_page_missing_sort_values_throws_instead_of_silently_ending_pagination()
    {
        // A full page (records.Count == pageSize) with no per-hit 'sort' means the next
        // search_after can't be computed — this must be a loud failure, not a truncated "success".
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[{"_source":{"id":1,"name":"A"}}]}}"""), out _);

        var source = Builder(client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 1));
    }

    [Fact]
    public async Task A_full_page_whose_final_hit_lacks_sort_throws_even_when_an_earlier_hit_has_it()
    {
        // Regression: an earlier version tracked 'lastSort' from any hit that happened to carry a
        // 'sort' field, so a full page whose LAST hit specifically lacked 'sort' (while an earlier
        // hit had it) silently built the next search_after from the earlier hit's stale values
        // instead of tripping this guard — which would have caused duplicate rows on the next page.
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[{"_source":{"id":1,"name":"A"},"sort":[1]},{"_source":{"id":2,"name":"B"}}]}}"""), out _);

        var source = Builder(client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 2));
    }

    [Fact]
    public async Task Missing_hits_hits_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => JsonResponse("""{"hits":{}}"""), out _);

        var source = Builder(client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task Non_success_response_throws_with_status_and_retry_after()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        }, out _);

        var source = Builder(client).As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Applies_configured_api_key_bearer_and_static_headers()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[]}}"""), out StubHttpMessageHandler handler);

        var source = Source.Elasticsearch("http://es.test", "orders", client)
            .Sort(SortDsl)
            .ApiKey("X-Api-Key", "secret123")
            .Bearer("token456")
            .Header("X-Custom", "value")
            .As<Item>();

        await CollectAsync(source, pageSize: 10);

        HttpRequestMessage request = handler.Requests[0].Message;
        request.Headers.GetValues("X-Api-Key").ShouldContain("secret123");
        request.Headers.Authorization!.ToString().ShouldBe("Bearer token456");
        request.Headers.GetValues("X-Custom").ShouldContain("value");
    }
}
