using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeoReports.Abstractions;
using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Airtable.UnitTests;

public sealed record Project(string Name, bool Done);

/// <summary>
/// Tests the typed Airtable source's cursor pagination (ADR D65) end to end against a stubbed
/// <see cref="HttpMessageHandler"/> — <c>AirtableBatchSource{T}</c> is <c>internal</c> (no
/// <c>InternalsVisibleTo</c> convention in this repo), so correctness is verified through
/// <c>Source.Airtable(...).As&lt;T&gt;()</c>, the same approach OData's/HubSpot's tests use.
/// </summary>
public sealed class AirtableBatchSourceTests
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
    public async Task Paginates_via_offset_until_absent()
    {
        var call = 0;
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse("""{"records":[{"id":"rec1","fields":{"name":"Alpha","done":false}}],"offset":"o1"}"""),
                _ => JsonResponse("""{"records":[{"id":"rec2","fields":{"name":"Beta","done":true}}]}"""),
            };
        }, out StubHttpMessageHandler handler);

        var source = Source.Airtable("appXXX", "Projects", "token123", client).As<Project>();

        List<Project> all = await CollectAsync(source, pageSize: 10);

        all.Select(p => p.Name).ShouldBe(new[] { "Alpha", "Beta" });
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://api.airtable.com/v0/appXXX/Projects?pageSize=10");
        handler.Requests[1].RequestUri!.Query.ShouldContain("offset=o1");
    }

    [Fact]
    public async Task Table_name_with_spaces_is_percent_encoded()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"records":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.Airtable("appXXX", "My Table", "token123", client).As<Project>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests[0].RequestUri!.AbsoluteUri.ShouldContain("My%20Table");
    }

    [Fact]
    public async Task Applies_configured_bearer_token()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"records":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.Airtable("appXXX", "Projects", "token123", client).As<Project>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer token123");
    }

    [Fact]
    public async Task A_record_missing_fields_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"records":[{"id":"rec1"}]}"""), out _);

        var source = Source.Airtable("appXXX", "Projects", "token123", client).As<Project>();

        await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));
    }

    [Fact]
    public async Task Missing_records_throws()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"items":[]}"""), out _);

        var source = Source.Airtable("appXXX", "Projects", "token123", client).As<Project>();

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

        var source = Source.Airtable("appXXX", "Projects", "token123", client).As<Project>();

        HttpSourceException ex = await Should.ThrowAsync<HttpSourceException>(() => CollectAsync(source, pageSize: 10));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task BaseUrl_override_is_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"records":[]}"""), out StubHttpMessageHandler handler);

        var source = Source.Airtable("appXXX", "Projects", "token123", client)
            .BaseUrl("https://proxy.internal/v0")
            .As<Project>();

        await CollectAsync(source, pageSize: 10);

        handler.Requests[0].RequestUri!.Host.ShouldBe("proxy.internal");
    }
}
