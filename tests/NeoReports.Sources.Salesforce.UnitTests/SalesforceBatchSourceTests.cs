using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Salesforce.UnitTests;

public sealed record Account(string Id, string Name);

/// <summary>
/// Tests the typed Salesforce source's <c>nextRecordsUrl</c> pagination (ADR D67) end to end against
/// a stubbed <see cref="HttpMessageHandler"/> — <c>SalesforceBatchSource{T}</c> is <c>internal</c>
/// (no <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.Salesforce(...).As&lt;T&gt;()</c>, the same approach every prior Epic P source's tests use.
/// </summary>
public sealed class SalesforceBatchSourceTests
{
    private const string Soql = "SELECT Id, Name FROM Account";

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

    // For single-page assertions only — does NOT loop until HasMore is false, unlike CollectAsync. A
    // stub that always returns the same non-"done" page would make CollectAsync loop forever against
    // it (a real bug found and fixed in the Google Sheets PR, confirmed via a runaway 22GB
    // testhost.exe); reading exactly one page sidesteps that entirely.
    private static Task<BatchResult<T>> ReadOnePageAsync<T>(IBatchSource<T> source, int pageSize) =>
        source.ReadBatchAsync(new BatchContext(Exec(), pageSize, null, 1), CancellationToken.None);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Paginates_via_nextRecordsUrl_until_done()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse("""{"totalSize":2,"done":false,"nextRecordsUrl":"/services/data/v59.0/query/01g-500","records":[{"attributes":{"type":"Account"},"Id":"001","Name":"Acme"}]}"""),
                2 => JsonResponse("""{"totalSize":2,"done":true,"records":[{"attributes":{"type":"Account"},"Id":"002","Name":"Globex"}]}"""),
                _ => throw new InvalidOperationException("Unexpected extra request."),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        List<Account> all = await CollectAsync(source, pageSize: 1);

        all.Select(a => a.Name).ShouldBe(new[] { "Acme", "Globex" });
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.ToString().ShouldContain("/services/data/v59.0/query?q=");
        handler.Requests[1].RequestUri!.ToString().ShouldBe("https://myorg.my.salesforce.com/services/data/v59.0/query/01g-500");
    }

    [Fact]
    public async Task Records_attributes_envelope_is_ignored_not_required()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"totalSize":1,"done":true,"records":[{"attributes":{"type":"Account","url":"/x"},"Id":"001","Name":"Acme"}]}"""), out _);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        BatchResult<Account> result = await ReadOnePageAsync(source, pageSize: 10);

        result.Records.Single().Id.ShouldBe("001");
        result.Records.Single().Name.ShouldBe("Acme");
    }

    [Fact]
    public async Task Done_true_stops_pagination_even_if_nextRecordsUrl_were_present()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"totalSize":1,"done":true,"nextRecordsUrl":"/should/be/ignored","records":[{"Id":"001","Name":"Acme"}]}"""), out _);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        BatchResult<Account> result = await ReadOnePageAsync(source, pageSize: 10);

        result.HasMore.ShouldBeFalse();
        result.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Applies_configured_bearer_token()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":0,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        await ReadOnePageAsync(source, pageSize: 10);

        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer token123");
    }

    [Fact]
    public async Task Custom_api_version_is_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":0,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client)
            .ApiVersion("v61.0")
            .As<Account>();

        await ReadOnePageAsync(source, pageSize: 10);

        handler.Requests[0].RequestUri!.ToString().ShouldContain("/services/data/v61.0/query");
    }

    [Fact]
    public async Task Missing_records_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":0,"done":true}"""), out _);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        await Should.ThrowAsync<HttpSourceException>(() => ReadOnePageAsync(source, pageSize: 10));
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

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => ReadOnePageAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_cross_origin_nextRecordsUrl_is_rejected()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"totalSize":2,"done":false,"nextRecordsUrl":"https://evil.example/steal","records":[{"Id":"001","Name":"Acme"}]}"""), out _);

        var source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        BatchResult<Account> firstPage = await ReadOnePageAsync(source, pageSize: 10);
        firstPage.HasMore.ShouldBeTrue();

        var context = new BatchContext(Exec(), 10, firstPage.NextCursor, 2);
        await Should.ThrowAsync<HttpSourceException>(() => source.ReadBatchAsync(context, CancellationToken.None));
    }

    [Fact]
    public void Construction_requires_instance_url_and_soql()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("{}"), out _);

        Should.Throw<ArgumentException>(() => Source.Salesforce("", Soql, "token123", client));
        Should.Throw<ArgumentException>(() => Source.Salesforce("https://myorg.my.salesforce.com", "", "token123", client));
    }

    [Fact]
    public async Task The_source_is_reachable_as_ISourceRowCounter_the_same_way_ReportBuilder_detects_it()
    {
        // Regression: SalesforceBatchSource<T> originally implemented only IBatchSource<T>, so the
        // fully-built SalesforceRowCounter was unreachable dead code in production — ReportBuilder
        // detects counting support via exactly this "source as ISourceRowCounter" pattern-match on
        // the instance a source factory returns, the same composition ODataBatchSource/
        // ElasticsearchBatchSource use.
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":42,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        IBatchSource<Account> source = Source.Salesforce("https://myorg.my.salesforce.com", Soql, "token123", client).As<Account>();

        (source as ISourceRowCounter).ShouldNotBeNull();
        long? count = await ((ISourceRowCounter)source).CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(42);
        Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query).ShouldContain("SELECT COUNT() FROM Account");
    }
}
