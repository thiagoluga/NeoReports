using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using NeoReports.Core.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>Epic D / D5: <c>GET /jobs/{id}/artifacts</c> — finished output files, name/mime/size only.</summary>
public class JobArtifactsEndpointTests
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
    public async Task Completed_job_with_single_output_returns_its_artifact()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var artifacts = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/artifacts", Json);

        artifacts!.ShouldHaveSingleItem();
        artifacts[0].GetProperty("fileName").GetString().ShouldEndWith(".csv");
        artifacts[0].GetProperty("mimeType").GetString().ShouldBe("text/csv");
        artifacts[0].GetProperty("sizeBytes").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Completed_job_with_multiple_outputs_returns_all_artifacts_matching_download()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddReport<Sale>("multi", b => b
                .From(new InMemorySource(rows: 5, pageSize: 10))
                .Column(v => v.Id, "ID")
                .To(Csv(o => o.Delimiter(';')))
                .To(Csv(o => o.Delimiter(','))));
        });
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "multi");

        var artifacts = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/artifacts", Json);

        artifacts!.Count.ShouldBe(2);
        artifacts.ShouldAllBe(a => a.GetProperty("sizeBytes").GetInt64() > 0);

        var download = await client.GetAsync($"/api/jobs/{jobId}/download");
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
    }

    [Fact]
    public async Task Response_never_contains_a_path_key()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var jobId = await RunToCompletionAsync(client, "sales");

        var raw = await client.GetStringAsync($"/api/jobs/{jobId}/artifacts");

        raw.ToLowerInvariant().ShouldNotContain("\"path\"");
    }

    [Fact]
    public async Task Running_job_returns_empty_array()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddReport<Sale>("slow", b => b
                .From(new InMemorySource(rows: 100_000, pageSize: 10, delay: TimeSpan.FromMilliseconds(20)))
                .Column(v => v.Id, "ID")
                .To(Csv(o => o.Delimiter(';'))));
        });
        var client = host.GetTestClient();

        var run = await client.PostAsJsonAsync("/api/reports/slow/run", new { }, Json);
        var jobId = (await run.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("jobId").GetString();

        var artifacts = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/artifacts", Json);

        artifacts.ShouldNotBeNull();
        artifacts.ShouldBeEmpty();

        await client.PostAsync($"/api/jobs/{jobId}/cancel", content: null);
    }

    [Fact]
    public async Task Unknown_job_returns_404()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/jobs/does-not-exist/artifacts");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
