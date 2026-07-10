using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>ADR D40: <c>GET /jobs/{id}/partial-artifacts</c> and its <c>/download</c>.</summary>
public class PartialArtifactsEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes one row on page 1, then fails reading page 2 — a read failure can never be
    /// skipped (D11), so this always ends the job Failed after one fully-written page.</summary>
    private sealed class FailOnSecondPageSource : IBatchSource<Sale>
    {
        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            if (context.PageNumber >= 2)
                throw new InvalidOperationException("boom");

            var rows = new[] { new Sale(1, "C1") };
            return Task.FromResult(new BatchResult<Sale>(rows, "1", true));
        }
    }

    private static async Task<string> RunToFailureAsync(HttpClient client, string reportName)
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

    private static void AddFailingReportWithPartials(IServiceCollection services)
    {
        services.AddPartialArtifacts();
        services.AddReport<Sale>("failing", b => b
            .From(new FailOnSecondPageSource())
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(Csv()));
    }

    [Fact]
    public async Task Unknown_job_returns_404()
    {
        using var host = await TestApp.StartAsync(AddFailingReportWithPartials);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/jobs/does-not-exist/partial-artifacts");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Completed_job_returns_empty_array()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddPartialArtifacts();
            services.AddReport<Sale>("sales", b => b
                .From(new InMemorySource(rows: 10, pageSize: 10))
                .Column(v => v.Id, "ID")
                .To(Csv(o => o.Delimiter(';'))));
        });
        var client = host.GetTestClient();

        var jobId = await RunToFailureAsync(client, "sales"); // also waits for Completed

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
        job.GetProperty("status").GetString().ShouldBe("Completed");

        var partials = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/partial-artifacts", Json);
        partials.ShouldNotBeNull();
        partials.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_job_returns_the_captured_partial()
    {
        using var host = await TestApp.StartAsync(AddFailingReportWithPartials);
        var client = host.GetTestClient();

        var jobId = await RunToFailureAsync(client, "failing");

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}", Json);
        job.GetProperty("status").GetString().ShouldBe("Failed");

        var partials = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/partial-artifacts", Json);
        partials.ShouldNotBeNull();
        partials!.ShouldHaveSingleItem();
        partials[0].GetProperty("fileName").GetString().ShouldBe("failing.partial.csv");
        partials[0].GetProperty("sizeBytes").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Failed_job_download_streams_the_partial_file()
    {
        using var host = await TestApp.StartAsync(AddFailingReportWithPartials);
        var client = host.GetTestClient();

        var jobId = await RunToFailureAsync(client, "failing");

        var response = await client.GetAsync($"/api/jobs/{jobId}/partial-artifacts/download");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
    }

    [Fact]
    public async Task No_partial_store_registered_returns_empty_array_for_a_failed_job()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            // No AddPartialArtifacts() — IPartialArtifactStore absent.
            services.AddReport<Sale>("failing", b => b
                .From(new FailOnSecondPageSource())
                .WithPageSize(10)
                .Column(v => v.Id, "Id")
                .To(Csv()));
        });
        var client = host.GetTestClient();

        var jobId = await RunToFailureAsync(client, "failing");

        var partials = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/partial-artifacts", Json);
        partials.ShouldNotBeNull();
        partials.ShouldBeEmpty();

        var download = await client.GetAsync($"/api/jobs/{jobId}/partial-artifacts/download");
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Completed_artifacts_endpoint_never_includes_partials()
    {
        using var host = await TestApp.StartAsync(AddFailingReportWithPartials);
        var client = host.GetTestClient();

        var jobId = await RunToFailureAsync(client, "failing");

        var artifacts = await client.GetFromJsonAsync<List<JsonElement>>($"/api/jobs/{jobId}/artifacts", Json);
        artifacts.ShouldNotBeNull();
        artifacts.ShouldBeEmpty();
    }
}
