using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class JobCompletedTests : NeoReportsTestContext
{
    private static ApiJobView CompletedJob(string id = "job-1", string reportName = "clientsVip") => new(
        id, reportName, "Completed", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-2),
        DateTimeOffset.UtcNow, null, new ApiJobStats(500, 500, 2048, 1, 5));

    [Fact]
    public void No_id_given_shows_the_no_job_id_text()
    {
        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, (string?)null));

        cut.Markup.ShouldContain("No job id given.");
    }

    [Fact]
    public void Unknown_job_shows_not_found_naming_the_id()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(null);

        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, "ghost-job"));

        cut.Find(".es-title").TextContent.ShouldBe("Job not found");
        cut.Markup.ShouldContain("ghost-job");
    }

    [Fact]
    public void Known_job_shows_report_name_and_written_record_count()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(CompletedJob());
        Api.JobArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(Array.Empty<ApiArtifact>());

        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, "job-1"));

        cut.Find("h1").TextContent.ShouldBe("clientsVip");
        cut.Markup.ShouldContain("500 rows processed");
    }

    [Fact]
    public void No_artifacts_shows_the_no_files_empty_state()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(CompletedJob());
        Api.JobArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(Array.Empty<ApiArtifact>());

        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, "job-1"));

        cut.FindAll(".es-title").ShouldContain(e => e.TextContent == "No files recorded for this job");
    }

    [Fact]
    public void Artifacts_list_a_download_button_per_file()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(CompletedJob());
        Api.JobArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>([new ApiArtifact("out.csv", "text/csv", 4096)]);

        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("out.csv");
    }

    [Fact]
    public void Download_navigates_to_the_built_download_url_with_a_forced_load()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(CompletedJob());
        Api.JobArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>([new ApiArtifact("out.csv", "text/csv", 4096)]);
        Api.DownloadUrl = id => $"/api/jobs/{id}/download";

        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Download")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/api/jobs/job-1/download");
    }

    [Fact]
    public void No_events_shows_the_honest_no_events_message()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(CompletedJob());
        Api.JobArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(Array.Empty<ApiArtifact>());
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());

        var cut = Render<JobCompleted>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("No events recorded — the event log may not be enabled on this host.");
    }
}
