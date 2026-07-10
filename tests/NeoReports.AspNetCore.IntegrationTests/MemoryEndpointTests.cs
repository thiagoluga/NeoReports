using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NeoReports.Abstractions;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>ADR D39: <c>GET /system/memory</c> — process-level reading, never per-job.</summary>
public class MemoryEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Returns_a_sane_shape()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/system/memory", Json);

        // GC.GetGCMemoryInfo() reports as of the last GC — in a freshly started test host that may
        // not have happened yet, so the two GC-derived fields can legitimately still be 0; only
        // WorkingSetBytes (OS-level, Environment.WorkingSet) is guaranteed positive.
        body.GetProperty("workingSetBytes").GetInt64().ShouldBeGreaterThan(0);
        body.GetProperty("gcHeapSizeBytes").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        body.GetProperty("gcCommittedBytes").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        body.GetProperty("measuredAt").GetDateTimeOffset().ShouldBeInRange(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Running_jobs_reflects_a_started_job()
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

        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
            if (job.GetProperty("status").GetString() == "Running")
                break;
            await Task.Delay(20);
        }

        var body = await client.GetFromJsonAsync<JsonElement>("/api/system/memory", Json);
        body.GetProperty("runningJobs").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        await client.PostAsync($"/api/jobs/{jobId}/cancel", content: null);
    }

    [Fact]
    public async Task Host_without_a_job_store_still_returns_200_with_zero_running_jobs()
    {
        // AddNeoReportsInMemoryJobs() bundles IJobStore with IReportJobScheduler (needed by other
        // handlers in the same MapNeoReports group — D2's minimal-API metadata-inference lesson:
        // an unresolvable service parameter anywhere in the group breaks route building for all of
        // it), so this surgically removes only IJobStore afterwards rather than building a bare host.
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddReport<Sale>("sales", b => b
                .From(new InMemorySource(rows: 30, pageSize: 10))
                .Column(v => v.Id, "ID")
                .To(Csv(o => o.Delimiter(';'))));
            services.AddNeoReportsInMemoryJobs();
            services.AddNeoReportsArtifacts();
            services.RemoveAll<IJobStore>();
        });
        var client = host.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/system/memory", Json);
        body.GetProperty("runningJobs").GetInt32().ShouldBe(0);
    }
}
