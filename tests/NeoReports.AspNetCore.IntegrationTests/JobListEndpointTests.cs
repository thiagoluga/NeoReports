using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// Epic D / D3: <c>GET /jobs</c> — the job list endpoint over the existing <c>IJobStore.ListAsync</c>.
/// </summary>
public class JobListEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
    public async Task Empty_store_returns_empty_array()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var jobs = await client.GetFromJsonAsync<List<JsonElement>>("/api/jobs", Json);

        jobs.ShouldNotBeNull();
        jobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_jobs_ordered_by_CreatedAt_descending()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var firstId = await RunToCompletionAsync(client, "sales");
        var secondId = await RunToCompletionAsync(client, "sales");

        var jobs = await client.GetFromJsonAsync<List<JsonElement>>("/api/jobs", Json);

        jobs!.Select(j => j.GetProperty("id").GetString()).ShouldBe(new[] { secondId, firstId });
    }

    [Fact]
    public async Task Filters_by_report_name()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddReport<Sale>("alpha", b => b
                .From(new InMemorySource(rows: 1, pageSize: 10)).Column(v => v.Id, "ID").To(Csv()));
            services.AddReport<Sale>("beta", b => b
                .From(new InMemorySource(rows: 1, pageSize: 10)).Column(v => v.Id, "ID").To(Csv()));
            services.AddNeoReportsInMemoryJobs();
            services.AddNeoReportsArtifacts(Path.Join(Path.GetTempPath(), "nr-d3-" + Guid.NewGuid().ToString("N")));
        });
        var client = host.GetTestClient();

        await RunToCompletionAsync(client, "alpha");
        await RunToCompletionAsync(client, "beta");

        var jobs = await client.GetFromJsonAsync<List<JsonElement>>("/api/jobs?report=alpha", Json);

        jobs!.ShouldHaveSingleItem();
        jobs[0].GetProperty("reportName").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task Filters_by_status()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        await RunToCompletionAsync(client, "sales");

        var completed = await client.GetFromJsonAsync<List<JsonElement>>("/api/jobs?status=Completed", Json);
        var failed = await client.GetFromJsonAsync<List<JsonElement>>("/api/jobs?status=Failed", Json);

        completed!.ShouldNotBeEmpty();
        completed.ShouldAllBe(j => j.GetProperty("status").GetString() == "Completed");
        failed!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bad_status_value_returns_400()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/jobs?status=NotARealStatus");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filters_by_since()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        await RunToCompletionAsync(client, "sales");
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(10);
        var secondId = await RunToCompletionAsync(client, "sales");

        var jobs = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/jobs?since={Uri.EscapeDataString(cutoff.ToString("O"))}", Json);

        jobs!.Select(j => j.GetProperty("id").GetString()).ShouldBe(new[] { secondId });
    }

    [Fact]
    public async Task Limit_zero_is_clamped_to_a_minimum_of_one()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        await RunToCompletionAsync(client, "sales");
        await RunToCompletionAsync(client, "sales");

        var jobs = await client.GetFromJsonAsync<List<JsonElement>>("/api/jobs?limit=0", Json);

        jobs!.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Large_limit_and_negative_offset_do_not_error()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        await RunToCompletionAsync(client, "sales");

        var response = await client.GetAsync("/api/jobs?limit=99999&offset=-5");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
