using System.Net;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.GoogleSheets.UnitTests;

public sealed record Person(string Name, long Amount, DateTime Created);

/// <summary>
/// Tests the typed Google Sheets source's fixed-window pagination (ADR D66) end to end against a
/// stubbed <see cref="HttpMessageHandler"/> — <c>GoogleSheetsBatchSource{T}</c> is <c>internal</c>
/// (no <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.GoogleSheets(...).As&lt;T&gt;()</c>, the same approach every prior Epic P source's
/// tests use. Response fixtures match Google's documented <c>values:batchGet</c>/
/// <c>UNFORMATTED_VALUE</c> shape (ADR D66 — researched, not verified against a live API).
/// </summary>
public sealed class GoogleSheetsBatchSourceTests
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

    // For single-page assertions only — does NOT loop until HasMore is false, unlike CollectAsync.
    // A stub that always returns the same non-empty page (used by several single-assertion tests
    // below) would make CollectAsync loop forever against it; reading exactly one page sidesteps
    // that entirely rather than requiring every such stub to fake an eventual empty response.
    private static Task<BatchResult<T>> ReadOnePageAsync<T>(IBatchSource<T> source, int pageSize) =>
        source.ReadBatchAsync(new BatchContext(Exec(), pageSize, null, 1), CancellationToken.None);

    private const string Header = """{"values":[["Name","Amount","Created"]]}""";

    [Fact]
    public async Task Paginates_via_fixed_window_until_an_empty_response()
    {
        // The header is fetched (and cached) only on page 1 — see D66's header-caching fix — so page
        // 2's stub returns just the data range, not header+data.
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse($$"""{"valueRanges":[{{Header}},{"values":[["Ada",100,1],["Alan",200,2]]}]}"""),
                2 => JsonResponse("""{"valueRanges":[{"values":[]}]}"""),
                _ => throw new InvalidOperationException("Unexpected extra request."),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        List<Person> all = await CollectAsync(source, pageSize: 2);

        all.Select(p => p.Name).ShouldBe(new[] { "Ada", "Alan" });
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_header_range_is_fetched_once_and_cached_not_re_fetched_on_later_pages()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse($$"""{"valueRanges":[{{Header}},{"values":[["Ada",100,1]]}]}"""),
                _ => JsonResponse("""{"valueRanges":[{"values":[]}]}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests.Count.ShouldBe(2);
        Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query).ShouldContain("A1:C1");
        Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query).ShouldNotContain("A1:C1");
    }

    [Fact]
    public async Task A_window_shorter_than_page_size_still_continues_pagination()
    {
        // ADR D66: a window shorter than requested does NOT mean exhausted — trailing blank rows are
        // omitted by the API, so only a fully empty window signals the end. Header is fetched (and
        // cached) only on page 1, so pages 2/3's stubs return just the data range.
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse($$"""{"valueRanges":[{{Header}},{"values":[["Ada",100,1]]}]}"""),
                2 => JsonResponse("""{"valueRanges":[{"values":[["Alan",200,2]]}]}"""),
                3 => JsonResponse("""{"valueRanges":[{"values":[]}]}"""),
                _ => throw new InvalidOperationException("Unexpected extra request."),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        List<Person> all = await CollectAsync(source, pageSize: 10);

        all.Select(p => p.Name).ShouldBe(new[] { "Ada", "Alan" });
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Materializes_by_header_name_case_insensitively()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse($$"""{"valueRanges":[{"values":[["name","AMOUNT","created"]]},{"values":[["Ada",100,1]]}]}"""), out _);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        BatchResult<Person> result = await ReadOnePageAsync(source, pageSize: 10);

        result.Records.Single().Name.ShouldBe("Ada");
        result.Records.Single().Amount.ShouldBe(100L);
    }

    [Fact]
    public async Task Date_serial_number_is_converted_via_the_1899_12_30_epoch()
    {
        // ADR D66: UNFORMATTED_VALUE renders a date/time cell as a serial-number double (days since
        // 1899-12-30), not an ISO-8601 string — researched from documented spreadsheet convention,
        // not verified against a live Google Sheets API call (no internet access in this environment).
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse($$"""{"valueRanges":[{{Header}},{"values":[["Ada",100,44197]]}]}"""), out _);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        BatchResult<Person> result = await ReadOnePageAsync(source, pageSize: 10);

        result.Records.Single().Created.ShouldBe(new DateTime(1899, 12, 30).AddDays(44197));
    }

    [Fact]
    public async Task Request_includes_both_header_and_data_ranges_and_the_api_key()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse($$"""{"valueRanges":[{{Header}},{"values":[]}]}"""), out StubHttpMessageHandler handler);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        await CollectAsync(source, pageSize: 10);

        Uri requestUri = handler.Requests[0].RequestUri!;
        requestUri.ToString().ShouldContain("sheet123");
        requestUri.Query.ShouldContain("key=key123");
        requestUri.Query.ShouldContain("valueRenderOption=UNFORMATTED_VALUE");
        Uri.UnescapeDataString(requestUri.Query).ShouldContain("'Sheet1'!A1:C1");
        Uri.UnescapeDataString(requestUri.Query).ShouldContain("'Sheet1'!A2:C11");
    }

    [Fact]
    public void Construction_without_configured_columns_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("{}"), out _);

        Should.Throw<ArgumentException>(() => Source.GoogleSheets("sheet123", "Sheet1", "key123", client).As<Person>());
    }

    [Fact]
    public async Task Missing_valueRanges_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"foo":[]}"""), out _);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task A_single_valueRange_instead_of_two_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse($$"""{"valueRanges":[{{Header}}]}"""), out _);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task Non_success_response_throws_with_status_and_retry_after()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        }, out _);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_blank_middle_cell_falls_back_to_the_targets_default()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse($$"""{"valueRanges":[{{Header}},{"values":[["Ada","",1]]}]}"""), out _);

        var source = Source.GoogleSheets("sheet123", "Sheet1", "key123", client).Columns("A", "C").As<Person>();

        BatchResult<Person> result = await ReadOnePageAsync(source, pageSize: 10);

        result.Records.Single().Amount.ShouldBe(0L);
    }
}
