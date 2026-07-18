using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

public sealed record Item(long Id, string Name);

/// <summary>
/// Tests the typed HTTP source's paginated strategies (ADR D61) end to end against a stubbed
/// <see cref="HttpMessageHandler"/> — <c>HttpBatchSource{T}</c> is <c>internal</c> (no
/// <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.Http(...).As&lt;T&gt;()</c>, the same approach the CSV/Parquet source tests use.
/// </summary>
public sealed class HttpBatchSourceTests
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

    private static int ParseQueryInt(Uri uri, string key)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (Uri.UnescapeDataString(kv[0]) == key)
                return int.Parse(Uri.UnescapeDataString(kv[1]));
        }

        throw new KeyNotFoundException(key);
    }

    [Fact]
    public async Task Page_strategy_pages_until_a_shorter_than_full_response()
    {
        var responses = new Dictionary<int, string>
        {
            [1] = """{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""",
            [2] = """{"items":[{"id":3,"name":"C"},{"id":4,"name":"D"}]}""",
            [3] = """{"items":[{"id":5,"name":"E"}]}""",
        };

        HttpClient client = StubHttpMessageHandler.CreateClient(
            request => JsonResponse(responses[ParseQueryInt(request.RequestUri!, "page")]), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Page)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 2);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Offset_strategy_advances_by_the_page_size()
    {
        var responses = new Dictionary<int, string>
        {
            [0] = """{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""",
            [2] = """{"items":[{"id":3,"name":"C"}]}""",
        };

        HttpClient client = StubHttpMessageHandler.CreateClient(
            request => JsonResponse(responses[ParseQueryInt(request.RequestUri!, "offset")]), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Offset)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 2);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Cursor_strategy_follows_the_response_token_until_absent()
    {
        string[] pages =
        {
            """{"items":[{"id":1,"name":"A"}],"nextCursor":"tok2"}""",
            """{"items":[{"id":2,"name":"B"}],"nextCursor":"tok3"}""",
            """{"items":[{"id":3,"name":"C"}]}""",
        };
        var callIndex = 0;

        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse(pages[callIndex++]), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Cursor)
            .RecordsAt("items")
            .CursorField("nextCursor", "cursor")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests[1].RequestUri!.Query.ShouldContain("cursor=tok2");
        handler.Requests[2].RequestUri!.Query.ShouldContain("cursor=tok3");
    }

    [Fact]
    public async Task Cursor_strategy_accepts_a_numeric_continuation_token()
    {
        string[] pages =
        {
            """{"items":[{"id":1,"name":"A"}],"nextCursor":42}""",
            """{"items":[{"id":2,"name":"B"}]}""",
        };
        var callIndex = 0;

        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse(pages[callIndex++]), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Cursor)
            .RecordsAt("items")
            .CursorField("nextCursor", "cursor")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2 });
        handler.Requests[1].RequestUri!.Query.ShouldContain("cursor=42");
    }

    [Fact]
    public async Task LinkHeader_strategy_follows_rel_next_until_absent()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            HttpResponseMessage response = call switch
            {
                1 => JsonResponse("""{"items":[{"id":1,"name":"A"}]}"""),
                2 => JsonResponse("""{"items":[{"id":2,"name":"B"}]}"""),
                _ => JsonResponse("""{"items":[{"id":3,"name":"C"}]}"""),
            };

            if (call < 3)
                response.Headers.Add("Link", $"<http://api.test/items?page={call + 1}>; rel=\"next\"");

            return response;
        }, out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.LinkHeader)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests[1].RequestUri!.ToString().ShouldBe("http://api.test/items?page=2");
    }

    [Fact]
    public async Task LinkHeader_strategy_refuses_to_send_credentials_to_a_different_host()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            HttpResponseMessage response = JsonResponse("""{"items":[{"id":1,"name":"A"}]}""");
            // A different host than the configured base URL — simulates a compromised/malicious
            // endpoint (or response tampering) trying to redirect the next request, with its
            // configured API key, to an attacker-controlled server.
            response.Headers.Add("Link", "<http://attacker.test/steal>; rel=\"next\"");
            return response;
        }, out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.LinkHeader)
            .RecordsAt("items")
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
    public async Task Non_success_response_throws_with_status_and_retry_after()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        }, out _);

        var source = Source.Http("http://api.test/items", client).Paginate(HttpPaginationStrategy.Page).RecordsAt("items").As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Applies_configured_api_key_bearer_and_static_headers()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("[]"), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
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
}
