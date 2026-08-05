using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
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
    public async Task Page_strategy_pages_until_an_empty_response()
    {
        // Ends on an EMPTY page, not on a short one — page 3 is short but is no longer treated as the
        // last (ADR D72), because a short page is exactly what a service that clamps the page size
        // returns first, and stopping there truncated the report while reporting success.
        var responses = new Dictionary<int, string>
        {
            [1] = """{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""",
            [2] = """{"items":[{"id":3,"name":"C"},{"id":4,"name":"D"}]}""",
            [3] = """{"items":[{"id":5,"name":"E"}]}""",
            [4] = """{"items":[]}""",
        };

        HttpClient client = StubHttpMessageHandler.CreateClient(
            request => JsonResponse(responses[HttpTestHelpers.ParseQueryInt(request.RequestUri!, "page")]), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Page)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 2);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
        handler.Requests.Count.ShouldBe(4); // the extra request is the empty page that ends the run
    }

    [Fact]
    public async Task A_maliciously_configured_page_param_name_cannot_inject_an_extra_query_parameter()
    {
        // pageParam/pageSizeParam/etc. come from a dynamic report's author-configured properties —
        // untrusted enough that a crafted value like "page&evil=1" must not be able to break the
        // query string's structure and inject an unrelated parameter (security review finding).
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"items":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Page)
            .PageParams(pageParam: "page&evil=1")
            .RecordsAt("items")
            .As<Item>();

        await CollectAsync(source, pageSize: 2);

        string rawQuery = handler.Requests[0].RequestUri!.Query;
        rawQuery.ShouldNotContain("evil=1");
        rawQuery.ShouldContain(Uri.EscapeDataString("page&evil=1"));
    }

    [Fact]
    public async Task Offset_strategy_advances_by_the_rows_received_until_an_empty_response()
    {
        var responses = new Dictionary<int, string>
        {
            [0] = """{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""",
            [2] = """{"items":[{"id":3,"name":"C"}]}""",
            [3] = """{"items":[]}""",
        };

        HttpClient client = StubHttpMessageHandler.CreateClient(
            request => JsonResponse(responses[HttpTestHelpers.ParseQueryInt(request.RequestUri!, "offset")]), out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Offset)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 2);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests.Count.ShouldBe(3); // the extra request is the empty page that ends the run
    }

    [Fact]
    public async Task Page_strategy_keeps_going_when_the_service_caps_the_page_below_what_was_asked()
    {
        // Dynamics, SAP Gateway and Business Central all clamp the page size, and many REST APIs
        // silently reduce an over-max limit. Asking for 10 and getting 2 back used to read as "that
        // was the last page": the run stopped after 2 of 5 rows and still reported Completed.
        var responses = new Dictionary<int, string>
        {
            [1] = """{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""",
            [2] = """{"items":[{"id":3,"name":"C"},{"id":4,"name":"D"}]}""",
            [3] = """{"items":[{"id":5,"name":"E"}]}""",
            [4] = """{"items":[]}""",
        };

        HttpClient client = StubHttpMessageHandler.CreateClient(
            request => JsonResponse(responses[HttpTestHelpers.ParseQueryInt(request.RequestUri!, "page")]),
            out StubHttpMessageHandler _);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Page)
            .RecordsAt("items")
            .As<Item>();

        // Page size 10 requested; the stub never returns more than 2.
        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
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
    public async Task LinkHeader_strategy_survives_a_comma_inside_the_target_url()
    {
        // RFC 8288 allows a comma inside the target URI, and a base URL carrying a list parameter
        // (?fields=id,name) is echoed straight into the next-page link. Splitting the header on every
        // comma left two fragments that parse as neither a URI nor a rel parameter, so paging stopped
        // silently after page 1 and the report completed with a third of its rows.
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
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    $"<http://api.test/items?fields=id,name&page={call + 1}>; rel=\"next\", " +
                    "<http://api.test/items?fields=id,name&page=1>; rel=\"first\"");
            }

            return response;
        }, out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.LinkHeader)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2, 3 });
        handler.Requests[1].RequestUri!.ToString().ShouldBe("http://api.test/items?fields=id,name&page=2");
    }

    [Fact]
    public async Task LinkHeader_strategy_resolves_a_relative_next_url()
    {
        // RFC 8288 defines the target as a URI *reference*, so a relative one is conformant; feeding
        // it to `new Uri(string)` (absolute-only) failed the run with an opaque UriFormatException.
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            HttpResponseMessage response = call == 1
                ? JsonResponse("""{"items":[{"id":1,"name":"A"}]}""")
                : JsonResponse("""{"items":[{"id":2,"name":"B"}]}""");

            if (call == 1)
                response.Headers.Add("Link", "</v2/items?page=2>; rel=\"next\"");

            return response;
        }, out StubHttpMessageHandler handler);

        var source = Source.Http("http://api.test/v2/items", client)
            .Paginate(HttpPaginationStrategy.LinkHeader)
            .RecordsAt("items")
            .As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.Select(i => i.Id).ShouldBe(new long[] { 1, 2 });
        handler.Requests[1].RequestUri!.ToString().ShouldBe("http://api.test/v2/items?page=2");
    }

    [Fact]
    public async Task Cursor_strategy_fails_loudly_when_the_api_echoes_the_requested_cursor()
    {
        // Facebook Graph's paging.cursors.after does this on the last page. The runner's page loop is
        // driven purely by HasMore and has no cap, so an unchanged token means the identical request
        // repeats forever — a hang, not a failure. GraphQL (D63) and Elasticsearch already refuse it.
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""{"items":[{"id":1,"name":"A"}],"nextCursor":"tok1"}"""),
            out StubHttpMessageHandler _);

        var source = Source.Http("http://api.test/items", client)
            .Paginate(HttpPaginationStrategy.Cursor)
            .RecordsAt("items")
            .CursorField("nextCursor", "cursor")
            .As<Item>();

        // First page is fine — nothing has been requested yet, so "tok1" is genuinely new.
        BatchResult<Item> first = await source.ReadBatchAsync(
            new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        first.HasMore.ShouldBeTrue();

        // The second page comes back with the very token just sent, which is the loop.
        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() =>
            source.ReadBatchAsync(new BatchContext(Exec(), 10, first.NextCursor, 2), CancellationToken.None));
        ex.Message.ShouldContain("unchanged");
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
