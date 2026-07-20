using System.Net;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Elasticsearch.UnitTests;

/// <summary>
/// ADR D64: <see cref="ElasticsearchRowCounter"/> — genuinely analogous to <c>ODataRowCounter</c>'s
/// <c>$count</c> (a real, spec-guaranteed mechanism). Best-effort by contract: never throws, always
/// <c>null</c> on failure.
/// </summary>
public sealed class ElasticsearchRowCounterTests
{
    private const string SortDsl = """[{"_id":"asc"}]""";

    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Successful_count_reads_the_count_field()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((request, _) =>
        {
            request.RequestUri!.ToString().ShouldBe("http://es.test/orders/_count");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"count":42}""") };
        }, out _);

        var counter = new ElasticsearchRowCounter(client, "http://es.test", "orders", new ElasticsearchSourceOptions().Sort(SortDsl));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(42);
    }

    [Fact]
    public async Task Non_success_response_returns_null()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var counter = new ElasticsearchRowCounter(client, "http://es.test", "orders", new ElasticsearchSourceOptions().Sort(SortDsl));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_count_field_returns_null()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }, out _);

        var counter = new ElasticsearchRowCounter(client, "http://es.test", "orders", new ElasticsearchSourceOptions().Sort(SortDsl));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
    }

    [Fact]
    public async Task Sends_the_configured_static_query()
    {
        string? seenBody = null;
        HttpClient client = StubHttpMessageHandler.CreateClient((_, body) =>
        {
            seenBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"count":5}""") };
        }, out _);

        var options = new ElasticsearchSourceOptions().Sort(SortDsl).Query("""{"term":{"status":"open"}}""");
        var counter = new ElasticsearchRowCounter(client, "http://es.test", "orders", options);

        await counter.CountAsync(Exec(), CancellationToken.None);

        seenBody.ShouldNotBeNull();
        seenBody.ShouldContain("\"status\":\"open\"");
    }
}
