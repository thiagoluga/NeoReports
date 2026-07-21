using System.Net;
using System.Text;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Salesforce.UnitTests;

/// <summary>
/// Tests the SOQL <c>SELECT</c>-to-<c>COUNT()</c> rewrite (ADR D67) indirectly through
/// <see cref="SalesforceRowCounter"/>'s public surface — the rewrite itself
/// (<c>SalesforceCountQuery</c>) is <c>internal</c> (no <c>InternalsVisibleTo</c> convention in this
/// repo), so its behavior is verified through the resulting <c>q=</c> query-string value sent on the
/// wire, the same "test through the public entry point" approach every other Epic P source's tests use.
/// </summary>
public sealed class SalesforceCountQueryTests
{
    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string? SentQuery(StubHttpMessageHandler handler) =>
        handler.Requests.Count == 0 ? null : Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query).Split("q=", 2)[1].Split('&')[0];

    [Fact]
    public async Task Rewrites_a_simple_query()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":5,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id, Name FROM Account", new SalesforceSourceOptions().Bearer("token123"));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(5);
        SentQuery(handler).ShouldBe("SELECT COUNT() FROM Account");
    }

    [Fact]
    public async Task Preserves_where_and_order_by_clauses_unchanged()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":3,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id, Name FROM Account WHERE Industry = 'Tech' ORDER BY Name", new SalesforceSourceOptions().Bearer("token123"));

        await counter.CountAsync(Exec(), CancellationToken.None);

        SentQuery(handler).ShouldBe("SELECT COUNT() FROM Account WHERE Industry = 'Tech' ORDER BY Name");
    }

    [Fact]
    public async Task Does_not_match_a_subquerys_nested_from_as_the_split_point()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":1,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id, (SELECT Name FROM Contacts) FROM Account", new SalesforceSourceOptions().Bearer("token123"));

        await counter.CountAsync(Exec(), CancellationToken.None);

        SentQuery(handler).ShouldBe("SELECT COUNT() FROM Account");
    }

    [Fact]
    public async Task Returns_null_when_no_from_keyword_is_found()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("{}"), out StubHttpMessageHandler handler);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id, Name", new SalesforceSourceOptions().Bearer("token123"));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Returns_null_on_a_non_success_response_rather_than_throwing()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Forbidden), out _);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id FROM Account", new SalesforceSourceOptions().Bearer("token123"));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_totalSize_is_missing()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"done":true,"records":[]}"""), out _);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id FROM Account", new SalesforceSourceOptions().Bearer("token123"));

        long? count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBeNull();
    }

    [Fact]
    public async Task An_underscore_delimited_field_name_containing_the_word_from_is_not_mistaken_for_the_keyword()
    {
        // Regression: a bare char.IsLetterOrDigit word-boundary check treats '_' as a non-word
        // character, so "FROM" embedded in a Salesforce field API name like "Migrated_From_System__c"
        // (bounded by '_' on both sides) was misdetected as the keyword, corrupting the rewrite for a
        // perfectly valid, extremely common Salesforce naming pattern.
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"totalSize":2,"done":true,"records":[]}"""), out StubHttpMessageHandler handler);

        var counter = new SalesforceRowCounter(client, "https://myorg.my.salesforce.com", "SELECT Id, Migrated_From_System__c FROM Account", new SalesforceSourceOptions().Bearer("token123"));

        await counter.CountAsync(Exec(), CancellationToken.None);

        SentQuery(handler).ShouldBe("SELECT COUNT() FROM Account");
    }
}
