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

public class EndpointsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task List_reports_returns_registered_report()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var reports = await client.GetFromJsonAsync<List<JsonElement>>("/api/reports", Json);

        reports.ShouldNotBeNull();
        reports.ShouldContain(r => r.GetProperty("name").GetString() == "sales");
    }

    [Fact]
    public async Task The_202s_Location_points_at_the_prefix_the_endpoints_were_mapped_under()
    {
        using var host = await TestApp.StartAsync(prefix: "/v2");
        var client = host.GetTestClient();

        var run = await client.PostAsJsonAsync("/v2/reports/sales/run", new { parameters = (object?)null }, Json);
        run.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The point of a Location is that a client can follow it. Asserting only the string would
        // still pass on a header that is well-formed and wrong, which is what "/api" hardcoded under
        // a "/v2" mapping was — a 404 for every caller that did follow it.
        Uri location = run.Headers.Location.ShouldNotBeNull();
        location.OriginalString.ShouldBe($"/v2/jobs/{(await run.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("jobId").GetString()}");

        HttpResponseMessage followed = await client.GetAsync(location.OriginalString);
        followed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Async_run_goes_queued_to_completed_and_downloads()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        // CA-9: async trigger returns 202 + jobId.
        var run = await client.PostAsJsonAsync("/api/reports/sales/run", new { parameters = (object?)null }, Json);
        run.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var accepted = await run.Content.ReadFromJsonAsync<JsonElement>(Json);
        var jobId = accepted.GetProperty("jobId").GetString();
        jobId.ShouldNotBeNullOrWhiteSpace();

        // Poll status until completed.
        string? status = null;
        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
            status = job.GetProperty("status").GetString();
            if (status is "Completed" or "Failed" or "Cancelled")
                break;
            await Task.Delay(50);
        }

        status.ShouldBe("Completed");

        // Download the result.
        var download = await client.GetAsync($"/api/jobs/{jobId}/download");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldEndWith(".csv");

        var text = await download.Content.ReadAsStringAsync();
        text.ShouldContain("ID;Customer");   // header
        text.ShouldContain("1;C1");          // first row
        var dataLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        dataLines.Length.ShouldBe(31);       // header + 30 rows
    }

    [Fact]
    public async Task Sync_run_streams_single_output_with_content_disposition()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        // CA-10: sync streams the file directly.
        var response = await client.PostAsJsonAsync("/api/reports/sales/run?mode=sync", new { }, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldEndWith(".csv");

        var text = await response.Content.ReadAsStringAsync();
        text.ShouldContain("ID;Customer");
        text.ShouldContain("30;C30");
    }

    [Fact]
    public async Task Sync_run_rejects_multi_output_with_400()
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

        var response = await client.PostAsJsonAsync("/api/reports/multi/run?mode=sync", new { }, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Run_unknown_report_returns_404()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reports/nope/run", new { }, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_unknown_job_returns_404()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/jobs/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_before_completion_returns_conflict_or_notfound()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        // Unknown job → 404.
        var unknown = await client.GetAsync("/api/jobs/missing/download");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_unknown_job_returns_404()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.PostAsync("/api/jobs/missing/cancel", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_running_job_is_accepted()
    {
        // A slow source keeps the job running long enough to cancel it deterministically.
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

        // Wait until it is actually running before cancelling.
        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
            if (job.GetProperty("status").GetString() == "Running")
                break;
            await Task.Delay(50);
        }

        var cancel = await client.PostAsync($"/api/jobs/{jobId}/cancel", content: null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Download_multi_output_returns_zip()
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

        var run = await client.PostAsJsonAsync("/api/reports/multi/run", new { }, Json);
        var jobId = (await run.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("jobId").GetString();

        string? status = null;
        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
            status = job.GetProperty("status").GetString();
            if (status is "Completed" or "Failed")
                break;
            await Task.Delay(50);
        }

        status.ShouldBe("Completed");

        var download = await client.GetAsync($"/api/jobs/{jobId}/download");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");

        // The zip carries both CSV outputs.
        await using var stream = await download.Content.ReadAsStreamAsync();
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(2);
    }
}
