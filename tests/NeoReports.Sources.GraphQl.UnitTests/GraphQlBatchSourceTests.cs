using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.GraphQl.UnitTests;

public sealed record Item(long Id, string Name);

/// <summary>
/// Tests the typed GraphQL source's Relay-connection pagination (ADR D63) end to end against a
/// stubbed <see cref="HttpMessageHandler"/> — <c>GraphQlBatchSource{T}</c> is <c>internal</c> (no
/// <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.GraphQl(...).As&lt;T&gt;()</c>, the same approach OData's tests use.
/// </summary>
public sealed class GraphQlBatchSourceTests
{
    private const string Doc =
        "query($first: Int, $after: String) { viewer { repositories(first: $first, after: $after) " +
        "{ edges { node { id name } } pageInfo { hasNextPage endCursor } } } }";

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

    private static JsonElement ParseJson(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static GraphQlSourceBuilder Builder(HttpClient client, string connection = "viewer.repositories") =>
        Source.GraphQl("http://api.test/graphql", client).Query(Doc).Connection(connection);

    [Fact]
    public async Task Paginates_via_relay_connection_until_hasNextPage_is_false()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"node":{"id":1,"name":"A"}}],"pageInfo":{"hasNextPage":true,"endCursor":"c1"}}}}}"""),
                _ => JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"node":{"id":2,"name":"B"}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Builder(client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2 });
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Message.Method.ShouldBe(HttpMethod.Post);

        JsonElement firstVariables = ParseJson(handler.Requests[0].Body!).GetProperty("variables");
        firstVariables.TryGetProperty("after", out _).ShouldBeFalse();
        firstVariables.GetProperty("first").GetInt32().ShouldBe(10);

        JsonElement secondVariables = ParseJson(handler.Requests[1].Body!).GetProperty("variables");
        secondVariables.GetProperty("after").GetString().ShouldBe("c1");
    }

    [Fact]
    public async Task Errors_array_on_a_200_response_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"errors":[{"message":"field not found"}]}"""), out _);

        var source = Builder(client).As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
        ex.Message.ShouldContain("field not found");
        ex.StatusCode.ShouldBeNull();
    }

    [Fact]
    public async Task Errors_array_on_a_200_response_honors_retry_after()
    {
        // Some GraphQL APIs (e.g. GitHub's) signal rate-limiting as 200 + errors + Retry-After —
        // the exact scenario ADR D63 cites to justify reusing IRetryDelayHint for this source.
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
        {
            HttpResponseMessage response = JsonResponse("""{"errors":[{"message":"rate limited"}]}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return response;
        }, out _);

        var source = Builder(client).As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HasNextPage_true_with_no_endCursor_throws_instead_of_looping_forever()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"node":{"id":1,"name":"A"}}],"pageInfo":{"hasNextPage":true}}}}}"""), out StubHttpMessageHandler handler);

        var source = Builder(client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_null_node_is_skipped_rather_than_materialized()
    {
        // A present-but-null "node" is a valid Relay tombstone for a deleted/inaccessible entity.
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"node":null},{"node":{"id":1,"name":"A"}}],"pageInfo":{"hasNextPage":false}}}}}"""), out _);

        var source = Builder(client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.ShouldHaveSingleItem();
        all[0].Id.ShouldBe(1L);
    }

    [Fact]
    public void A_static_variable_colliding_with_the_paging_variable_name_throws_at_construction()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) => JsonResponse("{}"), out _);

        Should.Throw<ArgumentException>(() =>
            Builder(client).Variables(new Dictionary<string, object?> { ["after"] = "some-business-cursor" }).As<Item>());
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
    public async Task Missing_pageInfo_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[]}}}}"""), out _);

        var source = Builder(client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task Static_variables_are_merged_alongside_paging_variables()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[],"pageInfo":{"hasNextPage":false}}}}}"""), out StubHttpMessageHandler handler);

        var source = Builder(client)
            .Variables(new Dictionary<string, object?> { ["owner"] = "octocat" })
            .As<Item>();

        await CollectAsync(source, pageSize: 10);

        JsonElement variables = ParseJson(handler.Requests[0].Body!).GetProperty("variables");
        variables.GetProperty("owner").GetString().ShouldBe("octocat");
        variables.GetProperty("first").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Custom_first_and_after_variable_names_are_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"node":{"id":1,"name":"A"}}],"pageInfo":{"hasNextPage":false}}}}}"""), out StubHttpMessageHandler handler);

        var source = Builder(client)
            .PageVariables(first: "limit", after: "cursor")
            .As<Item>();

        await CollectAsync(source, pageSize: 5);

        JsonElement variables = ParseJson(handler.Requests[0].Body!).GetProperty("variables");
        variables.GetProperty("limit").GetInt32().ShouldBe(5);
        variables.TryGetProperty("first", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Custom_node_path_is_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"item":{"id":1,"name":"A"}}],"pageInfo":{"hasNextPage":false}}}}}"""), out _);

        var source = Builder(client).Node("item").As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Single().Id.ShouldBe(1L);
    }

    [Fact]
    public async Task Applies_configured_api_key_bearer_and_static_headers()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[],"pageInfo":{"hasNextPage":false}}}}}"""), out StubHttpMessageHandler handler);

        var source = Builder(client)
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
