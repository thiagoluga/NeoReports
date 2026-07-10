using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>ADR D38: <c>GET /jobs/{id}/events</c>.</summary>
public class JobEventsEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Passing a configureReports callback to TestApp.StartAsync replaces its default "sales"
    // registration entirely, so every test that needs both a real report and AddInMemoryJobEvents
    // must register both itself — this mirrors TestApp's own default "sales" definition.
    private static void AddSalesReportWithEvents(IServiceCollection services)
    {
        services.AddInMemoryJobEvents();
        services.AddReport<Sale>("sales", b => b
            .From(new InMemorySource(rows: 30, pageSize: 10))
            .Column(v => v.Id, "ID")
            .Column(v => v.Customer, "Customer")
            .To(Csv(o => o.Delimiter(';'))));
    }

    private static async Task<string> RunToCompletionAsync(HttpClient client, string reportName)
    {
        var run = await client.PostAsJsonAsync($"/api/reports/{reportName}/run", new { }, Json);
        var jobId = (await run.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("jobId").GetString()!;

        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
            if (job.GetProperty("status").GetString() is "Completed" or "Failed")
                break;
            await Task.Delay(20);
        }

        return jobId;
    }

    [Fact]
    public async Task Unknown_job_returns_404()
    {
        using var host = await TestApp.StartAsync(AddSalesReportWithEvents);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/jobs/does-not-exist/events");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task No_event_store_registered_returns_empty_array_for_a_real_job()
    {
        // The default TestApp host never calls AddJobEvents()/AddInMemoryJobEvents().
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var events = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/events", Json);
        events.ShouldNotBeNull();
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Completed_job_returns_the_full_lifecycle_ascending_by_sequence()
    {
        using var host = await TestApp.StartAsync(AddSalesReportWithEvents);
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var events = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/events", Json);

        events.ShouldNotBeNull();
        events!.Count.ShouldBeGreaterThan(0);
        events.Select(e => e.GetProperty("type").GetString()).First().ShouldBe("run-started");
        events.Select(e => e.GetProperty("type").GetString()).Last().ShouldBe("run-completed");
        events.Select(e => e.GetProperty("sequence").GetInt32()).ShouldBe(Enumerable.Range(1, events.Count));
    }

    [Fact]
    public async Task Type_filter_returns_only_matching_events()
    {
        using var host = await TestApp.StartAsync(AddSalesReportWithEvents);
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var events = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/events?type=retry", Json);

        events.ShouldNotBeNull();
        events!.ShouldAllBe(e => e.GetProperty("type").GetString() == "retry");
    }

    [Fact]
    public async Task Unknown_type_value_returns_empty_array_not_an_error()
    {
        using var host = await TestApp.StartAsync(AddSalesReportWithEvents);
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var response = await client.GetAsync($"/api/jobs/{jobId}/events?type=not-a-real-type");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var events = await response.Content.ReadFromJsonAsync<List<JsonElement>>(Json);
        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Limit_and_offset_are_honored()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddInMemoryJobEvents();
            services.AddReport<Sale>("many-pages", b => b
                .From(new InMemorySource(rows: 50, pageSize: 5))
                .Column(v => v.Id, "ID")
                .To(Csv()));
        });
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "many-pages");

        var all = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/events?limit=1000", Json);
        var page = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/events?limit=2&offset=1", Json);

        page!.Count.ShouldBe(2);
        page[0].GetProperty("sequence").GetInt32().ShouldBe(all![1].GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task Response_has_no_properties_key()
    {
        using var host = await TestApp.StartAsync(AddSalesReportWithEvents);
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var raw = await client.GetStringAsync($"/api/jobs/{jobId}/events");
        raw.ToLowerInvariant().ShouldNotContain("\"properties\"");
    }
}
