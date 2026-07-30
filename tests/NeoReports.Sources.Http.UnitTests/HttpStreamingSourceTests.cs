using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>
/// Tests the <see cref="HttpPaginationStrategy.None"/> path (ADR D61) — a single response, the whole
/// array stream-parsed one element at a time (<see cref="JsonRecords.StreamArrayAsync"/>). Reached
/// through <c>Source.Http(...).As&lt;T&gt;()</c> since <c>HttpStreamingSource{T}</c> is <c>internal</c>.
/// </summary>
public sealed class HttpStreamingSourceTests
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
    public async Task Reads_the_whole_root_array()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""[{"id":1,"name":"A"},{"id":2,"name":"B"}]"""), out _);

        var source = Source.Http("http://api.test/items", client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.ShouldBe(new[] { new Item(1, "A"), new Item(2, "B") });
    }

    [Fact]
    public async Task Reads_a_nested_records_path()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""{"data":{"items":[{"id":1,"name":"A"}]}}"""), out _);

        var source = Source.Http("http://api.test/items", client).RecordsAt("data.items").As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.ShouldBe(new[] { new Item(1, "A") });
    }

    [Fact]
    public async Task Reads_an_empty_array_as_no_rows()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("[]"), out _);

        var source = Source.Http("http://api.test/items", client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 10);

        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task Streams_correctly_across_many_small_buffer_refills()
    {
        IEnumerable<string> items = Enumerable.Range(1, 200).Select(i => $$"""{"id":{{i}},"name":"N{{i}}"}""");
        var json = "[" + string.Join(",", items) + "]";

        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrottledReadStream(new MemoryStream(Encoding.UTF8.GetBytes(json)), maxBytesPerRead: 7)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }, out _);

        var source = Source.Http("http://api.test/items", client).As<Item>();

        List<Item> all = await CollectAsync(source, pageSize: 1000);

        all.Count.ShouldBe(200);
        all[0].ShouldBe(new Item(1, "N1"));
        all[199].ShouldBe(new Item(200, "N200"));
    }

    [Fact]
    public async Task Non_success_response_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") }, out _);

        var source = Source.Http("http://api.test/items", client).As<Item>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Records_path_resolving_to_a_non_array_throws_instead_of_latching_onto_an_unrelated_array()
    {
        // "data.items" resolves to an object here, not an array; a real array ("nested") sits one
        // level deeper under an unrelated name — a naive scanner that keeps looking for the next
        // StartArray token would wrongly stream that one instead of failing loudly.
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""{"data":{"items":{"nested":[1,2]}}}"""), out _);

        var source = Source.Http("http://api.test/items", client).RecordsAt("data.items").As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task Records_path_never_found_before_the_response_ends_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""{"data":{"other":[1,2]}}"""), out _);

        var source = Source.Http("http://api.test/items", client).RecordsAt("data.items").As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task A_response_body_truncated_mid_array_throws_instead_of_reporting_a_short_but_successful_read()
    {
        // No closing ']' and the second element is cut off mid-object — simulates a dropped
        // connection/truncated body, not a well-formed empty tail.
        const string truncated = """[{"id":1,"name":"A"},{"id":2,"na""";

        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(truncated, Encoding.UTF8, "application/json"),
        }, out _);

        var source = Source.Http("http://api.test/items", client).As<Item>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }
}
