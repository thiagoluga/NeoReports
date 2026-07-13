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

/// <summary>
/// ADR D47: the row count travels through <c>GET /jobs/{id}</c> (<c>stats.totalRecords</c>, the
/// terminal/aggregate channel) and <c>GET /jobs/{id}/events</c> (the live channel — <c>JobStats</c>
/// only persists at completion, per <see cref="JobEventsEndpointTests"/>'s no-store-registered case).
/// </summary>
public class ProgressTrackingEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddCountingReport(IServiceCollection services, bool? trackProgress = null)
    {
        services.AddInMemoryJobEvents();
        services.AddReport<Sale>("sales", b =>
        {
            b.From(new CountingInMemorySource(rows: 30, pageSize: 10))
                .Column(v => v.Id, "ID")
                .Column(v => v.Customer, "Customer")
                .To(Csv(o => o.Delimiter(';')));
            if (trackProgress is { } explicitValue)
                b.TrackProgress(explicitValue);
        });
    }

    private static void AddUncountableReport(IServiceCollection services)
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
    public async Task Default_enabled_counting_source_reports_totalRecords_on_the_job()
    {
        using var host = await TestApp.StartAsync(services => AddCountingReport(services));
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
        job.GetProperty("stats").GetProperty("totalRecords").GetInt64().ShouldBe(30);
    }

    [Fact]
    public async Task Default_enabled_counting_source_puts_totalRecords_on_page_completed_events()
    {
        using var host = await TestApp.StartAsync(services => AddCountingReport(services));
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var events = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/events", Json);
        events.ShouldNotBeNull();

        // run-started is emitted before the count (so a crash mid-count still leaves a persisted
        // event for restart detection) — it never carries totalRecords; page-completed does.
        var runStarted = events!.Single(e => e.GetProperty("type").GetString() == "run-started");
        var hasData = runStarted.TryGetProperty("data", out var runStartedData) && runStartedData.ValueKind != JsonValueKind.Null;
        hasData.ShouldBeFalse();

        var pageCompleted = events.Where(e => e.GetProperty("type").GetString() == "page-completed");
        pageCompleted.ShouldNotBeEmpty();
        pageCompleted.ShouldAllBe(e => e.GetProperty("data").GetProperty("totalRecords").GetString() == "30");
    }

    [Fact]
    public async Task TrackProgress_false_leaves_totalRecords_null_even_for_a_counting_source()
    {
        using var host = await TestApp.StartAsync(services => AddCountingReport(services, trackProgress: false));
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
        job.GetProperty("stats").GetProperty("totalRecords").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_source_that_cannot_count_leaves_totalRecords_null_by_default()
    {
        using var host = await TestApp.StartAsync(AddUncountableReport);
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
        job.GetProperty("status").GetString().ShouldBe("Completed"); // unaffected by having nothing to count
        job.GetProperty("stats").GetProperty("totalRecords").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
